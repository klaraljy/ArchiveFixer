using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;



namespace ArchiveFixer.ViewModels;

internal sealed partial class ExtractionCoordinator
{

    /// <summary>
    /// 「其余物」目录名（契约 §3.2，2026-09-22 由「过程物」改名）：为得到内容物而产生、
    /// 用户不需要的东西 —— 内层归档、分卷、抠出来的中间 ZIP、纯壳文件夹；**以及源包本身**
    /// （决策 D-9：一键处理默认把源包也移进来，用户在那个目录里一次删掉就干净了）。
    ///
    /// 名字的**唯一来源**是 <see cref="ProcessArtifactLayout.ArtifactDirectoryName"/>，
    /// 这里只是给本类内部一个短名字用（别在别处再写一遍字面量）。
    /// </summary>
    internal const string ProcessArtifactDirectoryName = ProcessArtifactLayout.ArtifactDirectoryName;

    internal readonly MainViewModel _vm;
    internal readonly IArchiveEngine _archiveEngine;
    internal readonly PasswordService _passwordService;
    private readonly PasswordFailureRegistry _passwordFailureRegistry;

    /// <summary>测试/既有调用点仍按字段名读本批手动密码（唯一实现已搬进 PasswordFailureRegistry）。</summary>
    internal List<string> _manualBatchPasswords => _passwordFailureRegistry.ManualBatchPasswords;
    internal readonly PathService _pathService;
    internal readonly DialogService _dialogService;

    /// <summary>
    /// 源包搬运要碰的文件系统（决策 D-12）。
    ///
    /// 默认走真实文件系统；单测注入假实现来验证**跨盘那条路**（真机上很难临时造第二个卷）：
    /// "复制成功后再删原件、复制失败源包一个字节都不动"是红线，不能只靠读代码。
    /// </summary>
    private readonly ISourceMoveFileSystem _sourceMoveFileSystem;

    /// <summary>特定解压独立管线用的引擎（只读转发，不另造实例）。</summary>
    internal IArchiveEngine SpecificExtractionEngine => _archiveEngine;

    /// <summary>边界 resolver 试开用的引擎（只读转发；⛔ 不另造实例）。</summary>
    internal IArchiveEngine ArchiveEngine => _archiveEngine;

    /// <summary>特定解压批次开工时清空“本次导入内已成功”的两本账。</summary>
    internal void BeginSpecificPasswordBatch() => _passwordService.BeginBatch();

    /// <summary>特定解压某层成功后记账；空密码不进本批复用表（与主解压同一口径）。</summary>
    internal void RecordSpecificPasswordSuccess(string sourceArchive, string? password) =>
        _passwordService.RecordPasswordSuccess(sourceArchive, password);

    /// <summary>特定解压候选日志的来源描述（只影响日志，不参与密码选择逻辑）。</summary>
    internal string DescribeSpecificPasswordCandidate(string? password, int index)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "尝试空密码";
        }

        if (_passwordService.BatchVerifiedPasswords.Contains(password, StringComparer.Ordinal))
        {
            return "复用本批已成功的密码：******";
        }

        return $"尝试密码列表第 {index} 项：******";
    }

    /// <summary>
    /// 特定解压每层候选：**复用普通路径的同一个候选构造器**（本批成功优先、空密码至多一条、
    /// 密码本映射/统一密码/密码列表顺序一致），并按每层上限截断。
    /// </summary>
    internal IReadOnlyList<string?> SpecificExtractionPasswords =>
        BuildSpecificPasswordCandidatesCore(string.Empty);

    /// <summary>
    /// 特定解压某一层的候选。传当前层归档路径，密码本映射项才能按这一层的名字匹配。
    /// </summary>
    internal IReadOnlyList<string?> BuildSpecificPasswordCandidates(string archivePath) =>
        BuildSpecificPasswordCandidatesCore(archivePath);

    private IReadOnlyList<string?> BuildSpecificPasswordCandidatesCore(string? archivePath)
    {
        return _passwordCandidatesStep
            .BuildRecursionPasswordCandidates(archivePath ?? string.Empty)
            .Take(_passwordCandidatesStep.MaxPasswordAttemptsPerLayer)
            .Select(value => (string?)value)
            .ToList();
    }


    /// <summary>
    /// 解压前预检最多试几个密码候选去列表：试太多次会让"一键"变成等待。
    /// <c>internal</c>（M5）：步骤 Handler 直接读这一份常量，⛔ 不各留访问器、不复制数值。
    /// </summary>
    internal const int MaxPreflightPasswordAttempts = 3;

    // ================================================================
    // 不变量 11：源文件变化（AGENTS.md §6 第 11 条）
    // ================================================================


    internal CancellationTokenSource? _operationCts;

    /// <summary>
    /// **第二大步"当前工作"的取消源**：解析 / 试开 / 命名 / 建入口根这一整段。
    ///
    /// <para>它不在任务表里，所以旧的「停止当前任务」只看任务 CTS 时会报"没有正在执行的任务"，
    /// 而实际上一组 21 GB 正在被整组试开（真机 2026-11-10 00:00:45）。现在这段工作也有句柄：
    /// 「停止当前任务 / 暂停所有操作」先取消它，再取消任务；停止后续（只停后续）不碰它。</para>
    /// </summary>
    private readonly SecondStepWorkTracker _secondStepWorkTracker = new();
    /// <summary>
    /// 正在跑的任务各自的取消源。
    ///
    /// <para>⚠ <b>必须加锁访问</b>（2026-09-22：默认并发由 1 提到 4 之后暴露的真缺陷）：
    /// 每个任务在**自己的线程**上加/删这个列表，而 <see cref="CancelCurrentTask"/> 在 UI 线程上读它。
    /// 旧的裸 <c>List</c> 写法在并发下会直接把内部数组搞坏 ——
    /// 实测到的现象是收尾时抛 <c>ArgumentOutOfRangeException</c>（<c>List.RemoveAt</c> 越界），
    /// 一个任务因此变成「未知错误」，而不是它本来的结论。</para>
    /// </summary>
    private readonly List<CancellationTokenSource> _runningTaskCts = new();

    private readonly Dictionary<CancellationTokenSource, ArchiveTask> _runningTaskOwners = new();

    private readonly object _runningTaskCtsLock = new();

    /// <summary>
    /// 这一批用户导入的源文件路径（含分卷各卷）。供别名清理判“这是用户的东西，绝不许删”。
    /// 批首建立；任务表在清理时可能已被移动 / 改写，不能只靠当时的 CurrentPath 再猜。
    /// <c>internal</c>（M5）：协作类 <see cref="PieceAdoptionHandler"/> 直接读写这一份账，
    /// ⛔ 不再单留一个 <c>…ForHandler</c> 访问器。
    /// </summary>
    internal readonly HashSet<string> _userSourcePaths = new(StringComparer.OrdinalIgnoreCase);

    internal void TrackRunningTask(CancellationTokenSource cts, ArchiveTask task)
    {
        lock (_runningTaskCtsLock)
        {
            _runningTaskCts.Add(cts);
            _runningTaskOwners[cts] = task;
        }
    }

    internal void UntrackRunningTask(CancellationTokenSource cts)
    {
        lock (_runningTaskCtsLock)
        {
            _runningTaskCts.Remove(cts);
            _runningTaskOwners.Remove(cts);
        }
    }

    /// <summary>取一份快照（取消时**在锁外**逐个 Cancel：Cancel 会触发回调，不能拿着锁做）。</summary>
    private List<CancellationTokenSource> SnapshotRunningTasks()
    {
        lock (_runningTaskCtsLock)
        {
            return _runningTaskCts.ToList();
        }
    }

    /// <summary>特定解压的 unit 也注册进同一张运行任务表，取消当前才能找到它。</summary>
    internal CancellationTokenSource BeginSpecificTask(ArchiveTask task)
    {
        var cts = new CancellationTokenSource();
        TrackRunningTask(cts, task);
        return cts;
    }

    internal void EndSpecificTask(CancellationTokenSource cts)
    {
        UntrackRunningTask(cts);
        cts.Dispose();
    }

    internal bool _isExtracting;

    /*
     * 本批因密码没通过而失败的任务（密码错误 / 达到密码尝试上限）。
     *
     * 旧逻辑在每个任务的循环体里各弹一次模态框，而且用的是**同步** Dispatcher.Invoke：
     * 50 个错包 = 50 次阻塞点击，批量跑不动。现在只在这里登记，
     * 批次结束后由 ShowPasswordFailuresSummaryAsync 合并成一次提示。
     * 并发解压时多个任务会同时写它，所以必须加锁（而不是靠"反正都在 UI 线程上"）。
     */

    /*
     * ===== 同名冲突（ConflictAction = Ask）的批内状态 =====
     *
     * 决策 D-4：用户的真实场景是 50–200+ 个包，逐个弹窗等于不可用。所以"询问"这一档必须做到
     * **本批只问一次**：用户答了「覆盖全部 / 跳过全部 / 全部自动重命名」之后，后面的冲突一律照办，
     * 一个字都不许再问。这三条状态就是这件事的全部记账（批内重置、并发下加锁）。
     *
     * · _batchConflictDecision —— "全部 X"：本批后续所有冲突的答案；
     * · _taskConflictDecision  —— "这一个"：同一个任务内后面的冲突沿用（一次聚合询问管整任务）；
     * · _conflictPromptUnavailable —— 无界面宿主（单元测试 / 控制台宿主）或问不出答案：
     *   记下来，后面的冲突不再尝试弹窗，直接走保守档并只写一次日志。
     */

    /*
     * ===== 本批的"完成后打开输出目录"记账（设置项 OpenOutputFolderWhenDone，默认关）=====
     *
     * 为什么必须记账：设置开着的时候，如果每个任务成功都开一次资源管理器，
     * 一批 50–200 个包就会在用户桌面上炸出几十个窗口 —— 那不是"看一眼结果"，是骚扰。
     * 所以整批**只开一次**（第一个成功收尾的最外层任务），批开始时清零。
     *
     * 并发下多个任务会同时走到判断处，所以必须加锁（与 _passwordFailures 同一个理由）。
     */

    /*
     * ===== 手动输入的密码（WinRAR 参考 §3 附注 / §2 H 组采纳项）=====
     *
     * 不变量 5 的红线：**只对本次运行有效、绝不落盘**。所以它只是一个字段 ——
     * 不进 AppSettings、不进密码列表、不进日志、不进报告，进程一退就没了。
     *
     * 为什么需要它：密码本没命中、统一密码也不对时，用户以前只能"改设置再重跑整批"。
     * 现在整批**一次性**问一次，输入的值当成本批所有任务的候选（排在空密码之后、
     * 密码本之前 —— 它比密码本更"新"，是用户刚给出的信息）。
     *
     * ⚠ <c>internal</c>（2026-10-05）：单测要钉住"它必须同时进**递归**那条路的候选表"——
     * 只靠公开入口造不出"本批手动输过密码"这个状态（无界面宿主不会弹那个框）。
     */

    /*
     * ===== 「一键处理 · 本次选项」的运行期覆盖（规格 docs/输出与整理模型.md §9）=====
     *
     * 面板选的落点 / 终端落法 / 源包处理**只对这一次一键处理有效**：
     * · 它是一个**每批一份、批结束就置空**的字段（不是静态、不进设置、不落盘）——
     *   「不勾存为默认就不许改设置文件」这条硬要求靠的就是"这里根本没有写设置的路径"；
     * · 同时只有一批能在跑（_isExtracting 守着），所以字段不会被两批同时用到；
     * · 手动「只解压」（地基路径）**不带**它：那条路径照旧完全按设置走。
     */
    internal OneClickRunOptions? _runOptions;

    /// <summary>本批的"本次选项"（没有就是 null —— 地基路径、或没弹面板时的降级）。</summary>
    internal OneClickRunOptions? RunOptions => _runOptions;

    /*
     * ===== 手动档「解压到当前文件夹」的运行期开关（用户 2026-09-27）=====
     *
     * 语义 = WinRAR 右键的「解压到当前文件夹」：`111\222.rar` → `111\内容物`（**不建包名那一层**）。
     * 三条边界写死在这里，别的路径一律不许打开它：
     * ① 只有①页那个显式按钮会传 true（一键处理 / 批量 / 续解一律 false）—— 用户红线：批量绝不摊平；
     * ② 与「本次选项」同寿：批首置入、`finally` 里连同 _runOptions 一起清掉，不进设置、不落盘；
     * ③ 同名冲突照走既定冲突档（用户拍板接受这个代价：摊平本来就更容易撞名）。
     */
    internal bool _extractIntoSourceFolderThisRun;

    /*
     * 2026-10-25 用户拍板：手动「只解压」只解当前这一层（WinRAR 语义）。
     * 这条运行期事实只给定稿分类用：这一层引擎刚写出来的所有东西都是内容物，
     * ⛔ 不许再按"像不像归档"把它们扫进其余物。一键处理始终 false。
     */
    internal bool _manualSingleLayerThisRun;

    /*
     * ⛔ 原来这里有一个 `SourceDeleteFileSystemOverride`（"清理源包"的删除执行体，只给测试注入）。
     * 2026-09-25 第 32 条之后**管线里再没有任何一处调用 `SourceCleanupService`**
     * （删除统一走 `Storage/RestItemPurger`，它自己的执行体接缝在 `RecycleBinService` 那一层），
     * 所以这个接缝成了"看起来能影响删除、其实什么都不影响"的假接口 —— 一并删掉，
     * 免得以后有人拿它当"删除没发生"的证据。
     */

    /*
     * ===== 空间规划 + 危险模式（用户 2026-09-22 需求）=====
     *
     * 三件互相咬合的东西，都在这一批里活着：
     * · _ledger         —— 空间预留账本：并发时"已经许出去多少空间"的唯一记账处。
     *                      没有它，5G 与 6G 两个包会各自看到"还剩 10G"然后一起开跑。
     * · _runtime        —— 逐任务的运行期记录（预留了多少、开工前的可用空间、危险模式删了多少）。
     *                      自测证据与"跳过报告"都从这里取数；key 是任务路径。
     * · _refinedEstimates —— 解压前那一遍 list 算出来的**精确**空间需求（按任务路径存）。
     *                      自测的空间曲线要用它的"内容物"那一项，而任务对象上没有这个字段
     *                      （Models\ArchiveTask.cs 不在本批授权范围内，也就不去动它）。
     */

    /*
     * ⚠ 两张表都用**任务对象本身**做键，不用路径 —— 这一点有实际后果：
     * 源包搬进其余物之后，协调器会把 task.CurrentPath 改写到新位置（决策 D-12 要求的回写）。
     * 用路径当键的话，同一个任务在"搬之前"与"搬之后"会落进两个不同的格子：
     * 自测读证据时拿到的是全新的一份（RestPurged=false、可用空间 -1），于是"明明删掉了却报没删"。
     * ArchiveTask 没有重写 Equals，字典默认就是引用相等 —— 正是这里要的语义。
     */

    /// <summary>
    /// 本批是不是**一键处理**（批首钉一次；<c>oneClickRun</c> 只在批首那个参数里，
    /// 而"中途撞上空间不足该不该弹提示"这件事发生在很深的地方，需要它当判据）。
    ///
    /// <para>为什么需要：一键档的批中间是**零弹窗**红线，而那条"空间不足"的纯提示恰恰是
    /// 唯一被允许的例外之一（纯提示 / 一个按钮 / 非模态 / 同一批一次）——
    /// 手动档保持原样（只写日志），所以要能把两档分开。</para>
    /// </summary>
    internal bool _oneClickThisBatch;

    /// <summary>批首钉死的空间策略；所有空间分支只读这一份事实。</summary>
    internal SpaceTightPolicy _spaceTightPolicy = SpaceTightPolicy.Normal;

    public ExtractionCoordinator(
        MainViewModel vm,
        IArchiveEngine archiveEngine,
        PasswordService passwordService,
        PathService pathService,
        DialogService dialogService,
        ISourceMoveFileSystem? sourceMoveFileSystem = null)
    {
        _workspaceStep = new WorkspaceStep(this);

        _vm = vm;
        _archiveEngine = archiveEngine;
        _passwordService = passwordService;
        _pathService = pathService;
        _dialogService = dialogService;

        _uiThread = new UiThreadStep(_dialogService);
        _progressHeartbeat = new ProgressHeartbeatStep(this);
        _conflictStep = new ConflictStep(this);
        _passwordCandidatesStep = new PasswordCandidatesStep(this);
        _passwordFailureRegistry = new PasswordFailureRegistry();
        _placementStep = new PlacementStep(this);
        _partialPublishStep = new PartialPublishStep(this);
        _restHandlingStep = new RestHandlingStep(this);
        _sourceMoveFileSystem = sourceMoveFileSystem ?? FileSystemSourceMoveFileSystem.Instance;

        /*
         * 源包处理子系统**构造时建一次**（⛔ 不懒建）：它持有本批清理账本与链内输入名单
         * （CleanupLedger / ChainInnerLayerInputs），并发收尾的任务线程会同时读它 ——
         * 无锁懒建一旦造出第二个实例，账本就会分成两半（与 LogCaptureHandler 同一个理由）。
         */
        _source = new SourceHandler(this);

        /*
         * 第二大步开工前检查 + 组收片账**构造时建一次**（⛔ 不懒建）：它持有两本无锁字典
         * （组收片账 / 每组结论），批内任务线程会同时读写（登记在第二大步点名、收片在任务收尾）——
         * 无锁懒建一旦造出第二个实例，账就会分成两半（与 SourceHandler 同一个理由）。
         */
        _volumeStepPreflight = new VolumeStepPreflightHandler(this);
    }

    /// <summary>源包搬运用的文件系统（真实实现或测试注入的假实现）。</summary>
    private ISourceMoveFileSystem SourceMoveFileSystem => _sourceMoveFileSystem;

    private AppSettings? _runSettings;

    internal AppSettings Settings => _runSettings ?? _vm.Settings;


    private string GlobalPassword => _vm.GlobalPassword;

    private string SelectedOutputDirectory => _vm.SelectedOutputDirectory;

    internal ObservableCollection<ArchiveTask> Tasks => _vm.Tasks;

    internal bool IsStopping { get => _vm.IsStopping; set => _vm.IsStopping = value; }

    internal bool IsBusy { get => _vm.IsBusy; set => _vm.IsBusy = value; }

    // ================================================================
    // 任务级日志捕获 / 摘要行 / 批清账（M5：原 LogCaptureStep 分片已删）
    //
    // 实现（捕获缓冲、摘要行、批记账、诊断收尾）整块在 <see cref="LogCaptureHandler"/>；
    // 本类只保留三类成员：
    //   ① 唯一实例 <see cref="LogCapture"/>（宿主必须有它，Handler 不能每处 new —— 它有实例状态）；
    //   ② 宿主级入口（<c>AppendLog</c> ×2 / <c>AppendLogPair</c> / <c>AppendLogWithCode</c> /
    //      <c>CriterionLog</c> / <c>UpdateSummary</c>）：全仓 300+ 个调用点直接走它们；
    //   ③ 测试与别的 Handler 直接读写的两个状态（<see cref="KeepTaskDetailInLog"/> / <see cref="PostProcessStartedForTests"/>）。
    // 批清账的四个字段（<c>_batchPurged*</c> / <c>_stopNoticeLogged</c>）已随实现移入 Handler，
    // 批首清零的唯一出口仍是 <see cref="ResetBatchPurgeCounters"/>（ExtractEntryStep 分片）。
    // ================================================================

    private LogCaptureHandler? _logCaptureHandler;

    private readonly object _logCaptureHandlerGate = new();

    /// <summary>
    /// 任务级日志的**唯一实例**（懒建一次；宿主就是本类，全类共用同一个实例）。
    ///
    /// <para>⚠ 用锁而不是 <c>??=</c>：Handler 里有<b>实例状态</b>（任务级 <c>AsyncLocal</c> 捕获缓冲 + 批记账的锁），
    /// 任务线程本来就会并发调 <see cref="LogCaptureHandler.BeginTaskLogCapture"/> / <see cref="LogCaptureHandler.AppendLog(string, string, string, string, string, int, string)"/>；
    /// 无锁懒建一旦造出第二个实例，"开始捕获"与"写日志"就落到两个不同的缓冲上
    /// （后果：普通档下成功任务的 INFO 细节会漏到屏幕上，而那是既有红线）。
    /// 锁只护第一次创建，之后就是一次空判。</para>
    /// </summary>
    internal LogCaptureHandler LogCapture
    {
        get
        {
            LogCaptureHandler? handler = _logCaptureHandler;

            if (handler != null)
            {
                return handler;
            }

            lock (_logCaptureHandlerGate)
            {
                return _logCaptureHandler ??= new LogCaptureHandler(this);
            }
        }
    }

    /// <summary>
    /// **测试用**的显式口子：true = 成功时也把全部细节写进日志
    /// （默认 <c>false</c> = 产品行为：成功的任务在日志里只留一行摘要）。
    ///
    /// <para>为什么留这个口子：一大批既有用例把"日志里那一行"当作**行为证据**（跨盘搬运走了复制、
    /// 工作区被清理、空间依据写了"同卷只算一份"……），而第 44 条的瘦身改的是**默认呈现**、
    /// 不是行为本身。让那些用例显式声明"我这个用例要看细节"，比把它们一条条改成断言摘要更诚实
    /// —— 也让"默认现在是简洁的"这件事只由 <c>LogVolumePolicyTests</c> 一处钉住。</para>
    ///
    /// <para>⚠ 用户侧的入口是 ⑥设置 →「详细日志（排查用）」（<see cref="AppSettings.VerboseLog"/>，
    /// 默认关），二者在 <see cref="LogCaptureHandler"/> 的详细档判据里合成同一个行为；
    /// ⛔ 这里这个口子只给测试用，界面不绑它。</para>
    /// </summary>
    internal bool KeepTaskDetailInLog { get; set; }

    /// <summary>
    /// **测试缝**：收尾（定稿/归集/搬源）**已经开始、第一道取消闸门之后**回调一次。
    ///
    /// <para>为什么需要它（用户「残余缺口：收尾内部按取消无用例钉住」）：收尾内部有三处
    /// <c>ThrowIfCancellationRequested()</c>，而**没有任何用例能看见它们** —— 现有那两个钩子
    /// （<c>Engine.OnList/OnExtract</c>）都发生在收尾之前，更早的闸门就先看见了取消。
    /// ⇒ 只有在这里回调，测试才能把"取消"按在**收尾已经开始之后**。</para>
    ///
    /// <para>⛔ 默认 <c>null</c> = 一个字都不做（生产路径行为与改动前逐字相同）。</para>
    /// </summary>
    internal Action? PostProcessStartedForTests { get; set; }

    /// <summary>
    /// 写一处**判据**（人读一行 + 机器读一条，字段固定；唯一收口 <see cref="MainViewModel.CriterionLog"/>）。
    ///
    /// <para>⚠ 刻意**不走任务级捕获缓冲**（那条缓冲会把成功任务的 INFO 丢掉）：判据是大测试"只看日志排查"的
    /// 唯一线索 ⇒ 它每一处都必须落进日志文件。⛔ 只是记录：判据与行为一个字都不改。</para>
    /// </summary>
    internal void CriterionLog(
        string criterion,
        string inputs,
        string verdict,
        string action = "",
        string file = "",
        string identity = "",
        string level = "INFO",
        string errorCode = "",
        string component = "",
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLine = 0,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        => _vm.CriterionLog(
            criterion, inputs, verdict, action, file, identity, level, errorCode, component,
            sourceFile, sourceLine, caller);

    internal void UpdateSummary() => _vm.UpdateSummary();

    /// <summary>
    /// 适配既有 <see cref="Action{T1,T2}"/> 注入点的两参数入口（引擎原话/失败清单写入器）。
    /// 它只是转到带 caller 的唯一入口；⛔ 不另建一套日志逻辑（实现见 <see cref="LogCaptureHandler.AppendLogPair"/>）。
    /// </summary>
    internal void AppendLogPair(string level, string message) => LogCapture.AppendLogPair(level, message);

    /// <summary>适配 <see cref="Action{T1,T2,T3,T4}"/> 注入点（失败清单写入器）。</summary>
    internal void AppendLogWithCode(string level, string message, string errorCode, string component) =>
        LogCapture.AppendLogWithCode(level, message, errorCode, component);

    /*
     * ⚠ 下面两个 AppendLog 重载**必须**保留 CallerFilePath / CallerLineNumber / CallerMemberName：
     * 取值点在调用点，转发时把取到的值原样传下去 ⇒ 别的 partial / Handler 报进诊断日志的
     * 文件、行号、成员名与搬家前完全一致（实现见 LogCaptureHandler 的同签名重载）。
     */
    internal void AppendLog(
        string level,
        string message,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLine = 0,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        => LogCapture.AppendLog(level, message, sourceFile, sourceLine, caller);

    internal void AppendLog(
        string level,
        string message,
        string errorCode,
        string component,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLine = 0,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        => LogCapture.AppendLog(level, message, errorCode, component, sourceFile, sourceLine, caller);

    public void StopAfterCurrent()
    {
        try
        {
            IsStopping = true;

            /*
             * 说明：
             * 这里取消的是批量操作 token，用来阻止启动后续任务。
             *
             * 正在运行的任务各自持有独立的取消源，
             * 所以这里只停止后续，不强制结束当前任务。
             *
             * 如果用户想立即取消正在运行的任务，需要再点“取消当前任务”。
             */
            _operationCts?.Cancel();

            LogCapture.LogStopRequestOnce();
        }
        catch (Exception ex)
        {
            AppendLog("ERROR", "停止后续任务失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 进入第二大步（解析 / 试开 / 命名）时挂上"当前工作"句柄；结束时 Dispose。
    /// 一键处理的轮循环是串行的，所以同一时刻只允许一个句柄。
    /// </summary>
    internal SecondStepWorkScope BeginSecondStepWork()
    {
        return _secondStepWorkTracker.Begin();
    }

    private bool CancelSecondStepWork()
    {
        return _secondStepWorkTracker.Cancel();
    }

    public void CancelCurrentTask(string operation = "停止当前任务")
    {
        try
        {
            // 先取消"第二大步当前工作"（解析/试开/命名），再按任务取消；两者都是"当前正在执行"。
            bool secondStepWorkCancelled = CancelSecondStepWork();

            // 先取快照再逐个 Cancel：Cancel 会同步触发回调，不能拿着列表的锁做这件事。
            List<CancellationTokenSource> running = SnapshotRunningTasks();

            if (running.Count == 0)
            {
                if (secondStepWorkCancelled)
                {
                    AppendLog("INFO", $"{operation}：已取消正在进行的第二大步工作（解析 / 试开 / 命名）。");
                }
                else
                {
                    AppendLog("WARN", $"当前没有正在执行的任务可{operation}。");
                }

                return;
            }

            foreach (CancellationTokenSource cts in running)
            {
                ArchiveTask? owner;

                lock (_runningTaskCtsLock)
                {
                    _runningTaskOwners.TryGetValue(cts, out owner);
                }

                /*
                 * 取消是用户的选择，必须在收尾清理之前就落成机器事实；
                 * 否则工作区清理会把还没跑完的“成功”状态当成真成功。
                 */
                if (owner != null)
                {
                    owner.Outcome = TaskOutcome.Cancelled;
                    owner.Reason = TaskReason.Cancelled;
                    owner.ErrorMessage = $"用户{operation}";
                }

                try
                {
                    cts.Cancel();
                }
                catch
                {
                }
            }

            AppendLog("WARN", $"已请求{operation}，正在结束当前任务进程。");
        }
        catch (Exception ex)
        {
            AppendLog("ERROR", $"{operation}失败：" + ex.Message);
        }
    }


    private ExtractionTaskRunner? _taskRunner;

    /// <summary>一个任务从开工到收工的执行器（生命周期与协调器同寿命）。</summary>
    internal ExtractionTaskRunner TaskRunner => _taskRunner ??= new ExtractionTaskRunner(this, _vm);

    // ================================================================
    // 分卷交接登记与第一大步账本（M5：原 <c>ExtractionCoordinator.VolumeHandoffStep.cs</c> 已整份删除）
    // ================================================================

    private VolumeHandoffHandler? _volumeHandoffHandler;

    /// <summary>
    /// 分卷交接登记与"这一份是不是本批某组的一片"的唯一落点（懒建一次；实现见
    /// <see cref="VolumeHandoffHandler"/>，⛔ 协调器不再留同名薄转发）。
    ///
    /// <para>⚠ 交接账、第一大步文件账本、内容组图、边界解析管线、收拢源片账与「这一组同一套片
    /// 试过一遍」签名都归它持有（详见 <see cref="VolumeHandoffHandler"/> 的说明）；它是**有状态**的
    /// 协作类 ⇒ 全批只能有这一个实例，⛔ 不许在别处 <c>new VolumeHandoffHandler(this)</c>。</para>
    ///
    /// <para>交接账本记录器 <see cref="VolumeHandoffHandler.Ledger"/> 与产出物内容卡闸门
    /// <see cref="VolumeHandoffHandler.ProducedVolumeGate"/> 也挂在它下面
    /// （它们读写同一份账/共用一份缓存，⛔ 各自 new 会分成两半）。</para>
    /// </summary>
    internal VolumeHandoffHandler VolumeHandoff => _volumeHandoffHandler ??= new VolumeHandoffHandler(this);

    // ================================================================
    // 本批分卷账本（M5：原 <c>ExtractionCoordinator.VolumeLedgerStep.cs</c> 已整份删除）
    // ================================================================

    /// <summary>
    /// 本批账本的**唯一实例**：事实与说明整块在 <see cref="VolumeLedgerState"/>
    /// （<c>ViewModels/VolumeLedgerState.cs</c>），本类只持有这一个实例 —— ⛔ 不再给每一本账留
    /// 同名薄转发，读写点一律直接走 <c>_volumeLedgerState.Xxx</c>
    /// （协作类走 <c>_host._volumeLedgerState.Xxx</c>，字段名/类型/初始化与搬家前逐字相同）。
    ///
    /// <para>⚠ 立刻建、不懒建：状态对象没有构造依赖，而任务线程本来就会并发读写其中几本账
    /// （见 <see cref="VolumeLedgerState.GroupPieceProducersGate"/> 的说明）；懒建一旦造出第二个实例，
    /// 账就会分成两半。</para>
    /// </summary>
    internal readonly VolumeLedgerState _volumeLedgerState = new();

    private VolumeLedgerHandler? _volumeLedgerHandler;

    /// <summary>
    /// 分卷账本记账入口（懒建一次；实现见 <see cref="VolumeLedgerHandler"/>，⛔ 协调器不再留同名转发）。
    /// 过路层其余物/组片消费/缺卷延期/测试钩子全部走它。
    /// </summary>
    internal VolumeLedgerHandler VolumeLedger => _volumeLedgerHandler ??= new VolumeLedgerHandler(this);

    private SourceProtectionHandler? _sourceProtectionHandler;

    /// <summary>
    /// 用户源包就地保护/真名还原的唯一落点（懒建一次；实现见 <see cref="SourceProtectionHandler"/>，
    /// 全批共用同一个实例）。⚠ 这里只持有访问器，读写的那两份账仍在
    /// <see cref="VolumeLedgerState.KeepSourceSnapshots"/> / <see cref="VolumeLedgerState.KeepSourceSnapshotDirectory"/>。
    /// </summary>
    internal SourceProtectionHandler SourceProtection =>
        _sourceProtectionHandler ??= new SourceProtectionHandler(this);

    private readonly SourceHandler _source;

    /// <summary>
    /// 源包处理子系统（收尾重活 / 源包清理与逐层回收 / 半套分卷闸门 / 链尾补搬 / 源文件快照）
    /// 的唯一持有物（构造时建一次、全实例共用；账本与链内输入名单都在它里面，
    /// ⛔ 不许懒建成两份）。M5：原 <c>ExtractionCoordinator.Source.cs</c> 的成员已整份并入
    /// <see cref="SourceHandler"/>，协调器只留这一个访问器（⛔ 不复制判据、⛔ 不另开路径）。
    /// </summary>
    internal SourceHandler Source => _source;

    // ================================================================
    // 整组候选收集与收拢 / 分卷候选路径收集（M5：原 <c>ExtractionCoordinator.VolumeGatherCollectStep.cs</c> 已整份删除）
    // ================================================================

    private VolumeGatherCollectHandler? _volumeGatherCollectHandler;

    /// <summary>
    /// 整组候选收集与收拢（组目录/参考名/成员判定/槽位键/规范名准备）的唯一落点
    /// （懒建一次；实现见 <see cref="VolumeGatherCollectHandler"/>，⛔ 协调器不再留同名薄转发）。
    /// </summary>
    internal VolumeGatherCollectHandler VolumeGatherCollect =>
        _volumeGatherCollectHandler ??= new VolumeGatherCollectHandler(this);

    private ExtractionVolumeCandidateCollector? _volumeCandidateCollector;

    /// <summary>
    /// 分卷候选路径收集（懒建一次；实现见 <see cref="ExtractionVolumeCandidateCollector"/>，⛔ 协调器不再留同名薄转发）。
    /// </summary>
    internal ExtractionVolumeCandidateCollector VolumeCandidateCollector =>
        _volumeCandidateCollector ??= new ExtractionVolumeCandidateCollector(this);

    // ================================================================
    // 第二大步开工前检查 + 组收片账（M5：原 <c>ExtractionCoordinator.VolumeStepPreflightStep.cs</c> 已整份删除）
    //
    // 实现（缺卷不开工 / 组收片账 / 入口层清理 / 缺卷补救子系统）整块在
    // <see cref="VolumeStepPreflightHandler"/>；本类只保留三样东西，⛔ 不留任何同名薄转发：
    //   ① 唯一实例 <see cref="VolumeStepPreflight"/>（构造时建一次，两本账在它里面）；
    //   ② 搬出去的实现要用的最小 internal 访问器（私有成员在别的分片里）；
    //   ③ 缺卷补救那一侧要的访问器（底层是 <see cref="VolumeLedgerState"/> 与 BatchReminder 分片）。
    // ================================================================

    private readonly VolumeStepPreflightHandler _volumeStepPreflight;

    /// <summary>
    /// 第二大步开工前检查 + 组收片账的唯一落点（构造时建一次；实现见
    /// <see cref="VolumeStepPreflightHandler"/>，⛔ 协调器不再留同名薄转发）。
    ///
    /// <para>⚠ 它持有组收片账与缺卷补救子系统（都有状态）⇒ 全批只能有这一个实例，
    /// ⛔ 不许在别处 <c>new VolumeStepPreflightHandler(this)</c>。</para>
    /// </summary>
    internal VolumeStepPreflightHandler VolumeStepPreflight => _volumeStepPreflight;

    // ================================================================
    // ===== 搬出去的实现要用的 private 成员：定义在别的 partial 里（不改那些文件），
    // 这里只加最小 internal 访问器，⛔ 不复制判据、⛔ 不用反射（唯一实现仍在原处）=====

    /// <summary>
    /// 访问器：「这一批已经问过这一单的缺卷补救」的登记（字段 = 协调器的
    /// <c>_volumeLedgerState.VolumeRepairAsked</c>）：锁与登记都留在宿主这一侧，
    /// <see cref="VolumeRepairHandler"/> 只取"这次是不是第一次问"。
    /// </summary>
    internal bool TryMarkVolumeRepairAskedForHandler(ArchiveTask task)
    {
        lock (_volumeLedgerState.VolumeRepairAsked)
        {
            return _volumeLedgerState.VolumeRepairAsked.Add(task);
        }
    }

    /// <summary>访问器：确认框的唯一出口（实现 = BatchReminder 分片的 private 转发）；<see cref="VolumeRepairHandler"/> 用。</summary>
    internal Task<bool> DispatcherSafeConfirmForHandlerAsync(string message) =>
        DispatcherSafeConfirmAsync(message);

    /// <summary>访问器：UI 线程目录选择框的唯一出口（实现 = BatchReminder 分片的 private 转发）；<see cref="VolumeRepairHandler"/> 用。</summary>
    internal Task<string> ShowFolderBrowserOnUiThreadForHandlerAsync() =>
        ShowFolderBrowserOnUiThreadAsync();

    /// <summary>访问器：解前分卷判定（实现 = Recursion 分片的 private 转发）。</summary>
    internal Task<VolumeGroupResolution> ResolveVolumeGroupForVolumeStepPreflightAsync(
        ArchiveTask task,
        CancellationToken cancellationToken) =>
        ResolveVolumeGroupAsync(task, cancellationToken);

    /// <summary>访问器：缺卷诊断文案（实现 = Recursion 分片的 private static）。</summary>
    internal static string BuildVolumeVerdictMessageForVolumeStepPreflight(
        ArchiveTask task,
        VolumeGroupResolution resolution) =>
        BuildVolumeVerdictMessage(task, resolution);

    /// <summary>访问器：分卷证据行（实现 = Recursion 分片的 private 转发）。</summary>
    internal void LogVolumeGroupEvidenceForVolumeStepPreflight(
        ArchiveTask task,
        VolumeGroupResolution resolution) =>
        LogVolumeGroupEvidence(task, resolution);
}


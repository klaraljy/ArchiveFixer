using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 批量解压流程协调器。
    /// 负责并发调度、单任务解压管线、停止后续/取消当前。
    /// </summary>
    internal sealed class ExtractionCoordinator
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

        private readonly MainViewModel _vm;
        private readonly IArchiveEngine _archiveEngine;
        private readonly PasswordService _passwordService;
        private readonly PathService _pathService;
        private readonly DialogService _dialogService;

        /// <summary>
        /// 源包搬运要碰的文件系统（决策 D-12）。
        ///
        /// 默认走真实文件系统；单测注入假实现来验证**跨盘那条路**（真机上很难临时造第二个卷）：
        /// "复制成功后再删原件、复制失败源包一个字节都不动"是红线，不能只靠读代码。
        /// </summary>
        private readonly ISourceMoveFileSystem _sourceMoveFileSystem;

        /// <summary>
        /// 递归解压（M4）。引擎、探测器、密码来源全部注入，递归层自己不碰密码本。
        ///
        /// 注意：**不在这里留一个构造时就固定上限的实例** ——
        /// <see cref="RecursiveExtractor"/> 的上限是构造参数，构造一次就再也改不了，
        /// 而这里是 ViewModel 层的单例：用户改完设置（层数 / 每层密码上限）不重启程序就不生效。
        /// 所以每次任务现建一个（见 <see cref="CreateRecursiveExtractor"/>），上限当场从设置里取。
        ///
        /// 日志出口也接在这里：递归核心要把它清工作区（删目录，**不可逆**）的"删什么、为什么"
        /// 写出来，而它自己不认识 GUI、也不引用 LogService（与引擎、探测器同一套注入方式，
        /// AGENTS.md §4 分层铁律）。<see cref="MainViewModel.AppendLog(string, string)"/> 走的是
        /// BeginInvoke，从后台线程调用是安全的。
        ///
        /// ⚠ 一个任务一个实例：递归核心把"本次任务的工作区"记在实例上（<c>CurrentWorkspace</c>），
        /// 清理只认那一个目录。共用实例会让并发任务互相覆盖这个字段。
        /// </summary>
        private RecursiveExtractor CreateRecursiveExtractor() =>
            new(
                _archiveEngine,
                new MagicArchiveProber(),
                BuildRecursionPasswordCandidates,
                BuildRecursionLimits(),
                AppendLog,
                // 不变量 11 在**每一层**上的落点：递归核心每解一层之前都会问一次
                // "这一层要解的那个源文件还是原来那一份吗"。只有第 0 层（用户给的源包）
                // 会真的比对 —— 第 1 层起解的是工作区里我们自己产出的中间件，
                // 它们本来就不在快照里（详见 CheckRootSourceUnchangedAsync）。
                CheckRootSourceUnchangedAsync);

        /// <summary>
        /// 递归的硬上限（不变量 8）：层数与每层密码尝试次数都取用户的设置项。
        ///
        /// 旧实现的坑：调用方 new 的时候没传 limits，递归核心一直用 <c>RecursionLimits.Default</c> ——
        /// 于是"最大嵌套层数"改成 5 也照样只解 3 层，而每层密码上限被悄悄压到 8
        /// （设置项缺省是 10，用户调大更是不生效）。设置项必须真的管用，不能只是界面上的数字。
        /// </summary>
        private RecursionLimits BuildRecursionLimits() => new()
        {
            MaxDepth = Math.Clamp(Settings.MaxRecursionDepth, 1, 10),
            MaxPasswordAttemptsPerLayer = MaxPasswordAttemptsPerLayer
        };

        /// <summary>解压前预检最多试几个密码候选去列表：试太多次会让"一键"变成等待。</summary>
        private const int MaxPreflightPasswordAttempts = 3;

        // ================================================================
        // 不变量 11：源文件变化（AGENTS.md §6 第 11 条）
        // ================================================================

        /// <summary>
        /// **开工前**保证手上有一份基准：没有就现在补拍一次。
        ///
        /// <para>
        /// 为什么是补拍而不是拦下（"判据缺失 ≠ 判据不通过"）：老任务（这个功能落地之前建的任务）、
        /// 测试直接 <c>new</c> 出来的任务、以及用户手改过列表的情形，都可能没有快照。
        /// 把"我们没记录"说成"你的文件变了"，会把一个本来能正常跑的任务拦死，
        /// 而用户完全无从理解（他什么都没做过）。正确做法是把基准补上，从这一刻起开始保护。
        /// </para>
        /// </summary>
        public void EnsureSourceSnapshot(ArchiveTask task)
        {
            if (task == null || task.HasSourceSnapshot)
            {
                return;
            }

            task.CaptureSourceSnapshot();

            AppendLog(
                "INFO",
                $"{task.FileName}：开工前补拍源文件快照（这份任务过去没有基准），"
                + "从这一刻起按源文件大小 / 修改时间判断它有没有被改动。");
        }

        /// <summary>
        /// **真正调引擎之前**比一次：变了就当场停下（不变量 11）。
        ///
        /// <para><b>为什么落在这里</b>：<see cref="ExtractSingleTaskAsync"/> 是**所有**解压入口的
        /// 唯一收口 —— 一键处理、手动「只解压」、单包重试、危险模式自测走的都是它；
        /// 而"引擎"这条路只有它下游会走。放在这里，等于任何一个入口都不可能绕过去。</para>
        ///
        /// <para><b>停下来意味着什么（不变量 1 的红线照旧）</b>：引擎**一次都不会被调用**、
        /// 源包一个字节都不动、<c>其余物</c> 不生成、不发布任何产物、不留任何中间品。
        /// 状态落「源文件已变化」，<c>ErrorMessage</c> 里点名"哪一个文件、哪一项变了"并给出出路。</para>
        /// </summary>
        /// <returns>true = 已经拦下（调用方必须立刻 return，什么都不许做）。</returns>
        private bool StopIfSourceChanged(ArchiveTask task) => StopIfSourceChanged(task, out _);

        /// <summary>
        /// 与上面同一个判据，只是把"为什么"也交回给调用方（递归核心要拿它当停止理由）。
        /// <paramref name="reason"/> 为空串 = 没有变化，任务照常继续。
        /// </summary>
        private bool StopIfSourceChanged(ArchiveTask task, out string reason)
        {
            reason = string.Empty;

            if (task == null || !task.HasSourceSnapshot)
            {
                // 没有基准：不拦（调用方会先 EnsureSourceSnapshot 补上）。
                return false;
            }

            SourceChangeResult? comparison = task.CompareWithSourceSnapshot();

            if (comparison == null || !comparison.Changed)
            {
                return false;
            }

            reason = SourceFileSnapshot.DescribeChange(comparison);

            /*
             * 状态 / 进度 / 原因三件事一起落（不变量 6 的反面同样成立：
             * 一件没做成的事不许看起来像做成了）。这里刻意**不碰** task.OutputPath：
             * 它可能还停在上一次的值，但对一个"压根没开工"的任务来说没有意义，
             * 而失败清单里的"位置"一行读的是 CurrentPath（源包在哪），那才是用户要去找的东西。
             */
            task.Status = StatusText.SourceChanged;
            task.Operation = StatusText.OpWaiting;
            task.ProgressText = StatusText.ProgressFailed;
            task.ErrorMessage = reason;
            task.IsOutputVerified = false;
            task.EndTime = DateTime.Now;
            task.LastUpdatedTime = DateTime.Now;
            task.ClearProgress();
            task.UpdateElapsedText();

            AppendLog("ERROR", $"源文件已变化，未开始解压：{task.FileName} —— {reason}");

            return true;
        }

        /// <summary>
        /// 递归核心那一侧的检查口（不变量 11 覆盖到每一层）。
        ///
        /// <para><b>只认第 0 层</b>，也就是用户给的源包：第 1 层起解的是**我们自己**从内层抠出来的
        /// 中间件（工作区里的文件），它们既不在源文件快照里、也不该被源文件的变化牵连 ——
        /// 拿源包的快照去比内层包，只会得出一句必然错误的"源文件已变化"。</para>
        ///
        /// <para>返回非 null = 已经拦下，递归核心会停止展开并把这句话当成本次的结论。</para>
        /// </summary>
        private Task<string?> CheckRootSourceUnchangedAsync(ArchiveTask task)
        {
            return Task.FromResult(StopIfSourceChanged(task, out string reason) ? reason : null);
        }

        /// <summary>
        /// 每个归档（**每一层**）最多真的试几个密码候选（AGENTS.md §9.2：每层、每任务、每批次都要有尝试上限）。
        ///
        /// 为什么必须有：解压模式下一个候选 = **一次完整解压**。加密分卷的第二层，
        /// 几百条密码本的包会被逐个候选整包重解一遍（几百 MB 起、磁盘一直在写），
        /// 表现就是"几十分钟不动"，最后还可能把"没试完"报成"密码错误"。
        ///
        /// 到上限时的状态是 <see cref="StatusText.PasswordAttemptLimitReached"/>，**不是**"密码错误"。
        ///
        /// 值取自设置项 <see cref="AppSettings.MaxPasswordAttemptsPerLayer"/>（界面可改、能落盘）。
        /// 这里再夹一次只是兜底：设置对象不一定走过 <see cref="AppSettings.Normalize"/>（例如测试里直接
        /// <c>new AppSettings()</c>），而 0 或负数会让"一个候选都不试"直接变成密码错误 —— 那是最误导人的结论。
        /// （旧写法：这里是个常量 10，用户在设置界面改了不生效。）
        /// </summary>
        private int MaxPasswordAttemptsPerLayer =>
            Math.Clamp(Settings.MaxPasswordAttemptsPerLayer, 1, 1000);

        /// <summary>合并提示里最多列几个文件名（再多就让用户去看失败清单），别弹一个占满屏幕的框。</summary>
        private const int MaxPasswordFailureNamesInDialog = 10;

        private CancellationTokenSource? _operationCts;
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

        private readonly object _runningTaskCtsLock = new();

        private void TrackRunningTask(CancellationTokenSource cts)
        {
            lock (_runningTaskCtsLock)
            {
                _runningTaskCts.Add(cts);
            }
        }

        private void UntrackRunningTask(CancellationTokenSource cts)
        {
            lock (_runningTaskCtsLock)
            {
                _runningTaskCts.Remove(cts);
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

        private bool _isExtracting;

        /*
         * 本批因密码没通过而失败的任务（密码错误 / 达到密码尝试上限）。
         *
         * 旧逻辑在每个任务的循环体里各弹一次模态框，而且用的是**同步** Dispatcher.Invoke：
         * 50 个错包 = 50 次阻塞点击，批量跑不动。现在只在这里登记，
         * 批次结束后由 ShowPasswordFailuresSummaryAsync 合并成一次提示。
         * 并发解压时多个任务会同时写它，所以必须加锁（而不是靠"反正都在 UI 线程上"）。
         */
        private readonly List<(string FileName, string Status)> _passwordFailures = new();
        private readonly object _passwordFailuresLock = new();

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
        private readonly object _conflictDecisionLock = new();
        private ConflictDecision? _batchConflictDecision;
        private ConflictDecision? _taskConflictDecision;
        private bool _conflictPromptUnavailable;

        /// <summary>本批已经真的弹过几次询问（日志与排障用；正常情况下 ≤ 1）。</summary>
        private int _conflictPromptCount;

        /*
         * ===== 本批的"完成后打开输出目录"记账（设置项 OpenOutputFolderWhenDone，默认关）=====
         *
         * 为什么必须记账：设置开着的时候，如果每个任务成功都开一次资源管理器，
         * 一批 50–200 个包就会在用户桌面上炸出几十个窗口 —— 那不是"看一眼结果"，是骚扰。
         * 所以整批**只开一次**（第一个成功收尾的最外层任务），批开始时清零。
         *
         * 并发下多个任务会同时走到判断处，所以必须加锁（与 _passwordFailures 同一个理由）。
         */
        private readonly object _outputFolderLock = new();
        private bool _outputFolderOpenedThisBatch;

        /*
         * ===== 手动输入的密码（WinRAR 参考 §3 附注 / §2 H 组采纳项）=====
         *
         * 不变量 5 的红线：**只对本次运行有效、绝不落盘**。所以它只是一个字段 ——
         * 不进 AppSettings、不进密码列表、不进日志、不进报告，进程一退就没了。
         *
         * 为什么需要它：密码本没命中、统一密码也不对时，用户以前只能"改设置再重跑整批"。
         * 现在整批**一次性**问一次，输入的值当成本批所有任务的候选（排在空密码之后、
         * 密码本之前 —— 它比密码本更"新"，是用户刚给出的信息）。
         */
        private string? _manualBatchPassword;

        /// <summary>本批是否已经问过手动密码（一次性：问过就不再问，无论用户填没填）。</summary>
        private bool _manualPasswordPrompted;

        /*
         * ===== 「一键处理 · 本次选项」的运行期覆盖（规格 docs/输出与整理模型.md §9）=====
         *
         * 面板选的落点 / 终端落法 / 源包处理**只对这一次一键处理有效**：
         * · 它是一个**每批一份、批结束就置空**的字段（不是静态、不进设置、不落盘）——
         *   「不勾存为默认就不许改设置文件」这条硬要求靠的就是"这里根本没有写设置的路径"；
         * · 同时只有一批能在跑（_isExtracting 守着），所以字段不会被两批同时用到；
         * · 手动「只解压」（地基路径）**不带**它：那条路径照旧完全按设置走。
         */
        private OneClickRunOptions? _runOptions;

        /// <summary>本批的"本次选项"（没有就是 null —— 地基路径、或没弹面板时的降级）。</summary>
        private OneClickRunOptions? RunOptions => _runOptions;

        /// <summary>
        /// 本批是否出现过密码类失败（密码错误 / 达到尝试上限）。
        /// 批次结束后的合并提示据此换一句话：出过密码问题时指引"再点一次、把密码输进去"，
        /// 没出过就不提（免得每次跑完都念一遍）。
        /// </summary>
        private bool _batchHadPasswordFailures;

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

        /// <summary>本批的空间预留账本（串行时也建，只是永远只有一个任务在账上）。</summary>
        private SpaceReservationLedger? _spaceLedger;

        private readonly object _spaceRuntimeLock = new();

        /*
         * ⚠ 两张表都用**任务对象本身**做键，不用路径 —— 这一点有实际后果：
         * 源包搬进其余物之后，协调器会把 task.CurrentPath 改写到新位置（决策 D-12 要求的回写）。
         * 用路径当键的话，同一个任务在"搬之前"与"搬之后"会落进两个不同的格子：
         * 自测读证据时拿到的是全新的一份（RestPurged=false、可用空间 -1），于是"明明删掉了却报没删"。
         * ArchiveTask 没有重写 Equals，字典默认就是引用相等 —— 正是这里要的语义。
         */
        private readonly Dictionary<ArchiveTask, ScheduledTaskRuntime> _spaceRuntime = new();

        private readonly Dictionary<ArchiveTask, TaskSpaceEstimate> _refinedEstimates = new();

        /// <summary>
        /// 危险模式：本批因为"空间不足"被跳过（没启动）的任务。
        /// 批末要**如实报告**它们各自需要多少 —— 静默跳过是明令禁止的。
        /// </summary>
        private readonly List<(string Name, long RequiredBytes, long AvailableBytes, long ShortfallBytes)> _spaceBlockedTasks = new();

        /*
         * 自测期间把危险模式**临时**打开（用户 2026-09-22 要求的协议：先拿 2×并发数 个文件真跑一遍）。
         *
         * 为什么是一个运行期开关而不是"先把设置改成 true 再跑"：
         * ① 自测没通过时设置必须**保持原样**（一个字节都不许被写）；
         * ② 设置是落盘的，而自测是一个可能中途失败的动作 —— 写到盘上的 true 会留在那里。
         */
        private bool _dangerModeArmedForSelfTest;

        /// <summary>正在跑的自测证据收集器（不在自测时为 null）。</summary>
        private DangerModeSelfTestRecorder? _selfTestRecorder;

        /// <summary>
        /// 本批实际生效的危险模式。
        ///
        /// <para>⚠ 它是**批首定一次**的（<see cref="PrepareDangerModeForBatch"/>），不是每次读设置现算：
        /// 自测期间要强制开（<c>_dangerModeArmedForSelfTest</c>），平时还要求"自测凭证盖得住本批的并发档"。
        /// 默认 false —— 没经过批首的路径（例如单独解压一个包）永远按普通档走，不会顺手删东西。</para>
        /// </summary>
        private bool IsDangerModeActive => _dangerModeActiveThisBatch;

        /// <summary>本批危险模式是否真的生效（批首定一次）。</summary>
        private bool _dangerModeActiveThisBatch;

        /// <summary>本批"开着但自测凭证盖不住"的说明（空 = 没有这种情况）。</summary>
        private string _dangerModeNotCoveredReason = string.Empty;

        /// <summary>逐任务的运行期记账（并发下多个任务同时写，所以全部走锁）。</summary>
        private sealed class ScheduledTaskRuntime
        {
            /// <summary>当前账上给这个任务预留的字节数（开工时是粗估，拿到清单后可能被调整）。</summary>
            public long ReservedBytes;

            /// <summary>它开工前的可用空间（-1 = 取不到）——自测的空间曲线要它。</summary>
            public long AvailableBeforeStart = -1;

            /// <summary>它收尾（含危险模式删除）之后的可用空间（-1 = 取不到）。</summary>
            public long AvailableAfterFinish = -1;

            /// <summary>其余物是不是被彻底删掉了。</summary>
            public bool RestPurged;

            /// <summary>彻底删除释放的字节数。</summary>
            public long PurgedBytes;

            /// <summary>危险模式动作没做成时的原因（空 = 没出问题）。</summary>
            public string PurgeNote = string.Empty;
        }

        /// <summary>
        /// 自测的进度与样本台账。
        ///
        /// <para>它只记"这一批自测在处理哪几个文件、并发几" —— 逐文件的结果在
        /// <see cref="_spaceRuntime"/> 里（那是所有任务共用的运行期记账，不专为自测而生）。
        /// 两处分开的好处：自测中断（用户点取消）时，已经跑完的那几个文件的结果照样是完整的。</para>
        /// </summary>
        private sealed class DangerModeSelfTestRecorder
        {
            public int ParallelCount { get; init; }

            public int RequiredSampleSize { get; init; }

            public List<ArchiveTask> Samples { get; } = new();
        }

        public ExtractionCoordinator(
            MainViewModel vm,
            IArchiveEngine archiveEngine,
            PasswordService passwordService,
            PathService pathService,
            DialogService dialogService,
            ISourceMoveFileSystem? sourceMoveFileSystem = null)
        {
            _vm = vm;
            _archiveEngine = archiveEngine;
            _passwordService = passwordService;
            _pathService = pathService;
            _dialogService = dialogService;
            _sourceMoveFileSystem = sourceMoveFileSystem ?? FileSystemSourceMoveFileSystem.Instance;
        }

        /// <summary>源包搬运用的文件系统（真实实现或测试注入的假实现）。</summary>
        private ISourceMoveFileSystem SourceMoveFileSystem => _sourceMoveFileSystem;

        private AppSettings Settings => _vm.Settings;

        /// <summary>
        /// 收尾重活的产物：后台线程只负责碰文件系统，结论与日志都通过它回传给 UI 线程，
        /// 免得后台线程去动界面集合 / 任务状态。
        /// </summary>
        private sealed class PostProcessWorkResult
        {
            public OutputVerificationResult Verification { get; init; } = new();

            public List<(string Level, string Message)> LogEntries { get; init; } = new();

            public bool BudgetExceeded { get; init; }

            public string BudgetMessage { get; init; } = string.Empty;

            /// <summary>
            /// 产物越出目标根目录的说明（非 null = **不承认**这次解压：不归集、不清理源包）。
            /// 它必须走"结论"这条路回传，而不是像旧实现那样只往日志里塞一行 ERROR。
            /// </summary>
            public string? LandingViolation { get; init; }

            public CollectResult? Collected { get; init; }

            /// <summary>定稿（暂存区 → 最终目录）的结果；没走到定稿这一步时为 null。</summary>
            public StageCommitResult? Commit { get; init; }

            /// <summary>
            /// 定稿**整体失败**（一个内容物都没能搬进最终目录）：与越界同一口径 —— 结论不成立。
            /// 与 <see cref="LandingViolation"/> 分开写，是为了让状态与原因各自说自己的事，
            /// 别让"越界"这个词出现在一句讲搬运失败的提示里。
            /// </summary>
            public bool CommitFailed { get; init; }

            public string CommitFailureMessage { get; init; } = string.Empty;

            /// <summary>
            /// 源包没能移入其余物（非 null = **任务要标成"部分完成"**）。
            ///
            /// 与越界/预算同一口径地走"结论"这条路回传：内容物已经好了，这件事只影响任务的完成度，
            /// 所以它**不**顶掉"解压成功"，但也绝不允许被静默吞掉（D-12：移动失败要写明原因）。
            /// </summary>
            public string? SourceMoveFailure { get; init; }
        }

        /// <summary>定稿搬运的一句话结论（给日志与任务字段用）。</summary>
        internal sealed class StageCommitResult
        {
            public bool Attempted { get; init; }

            /// <summary>搬运过程中被取消（已把搬过去的搬回来，最终目录不留半成品）。</summary>
            public bool Cancelled { get; init; }

            /// <summary>计划里的内容物搬运条数（一条可能是一整个目录）。</summary>
            public int PlannedContentCount { get; init; }

            /// <summary>真的搬过去的内容物条数。</summary>
            public int MovedContentCount { get; init; }

            /// <summary>真的搬过去的其余物条数。</summary>
            public int MovedProcessCount { get; init; }

            /// <summary>
            /// 因为同名冲突被**跳过**的条数（用户选了「跳过」/「跳过全部」）。
            ///
            /// 与"没能搬运"分开记：跳过是用户的选择，不是失败；但"一条内容物都没落位"时
            /// 必须靠它把任务落成「已跳过」而不是「解压成功」（不变量 6 的反面同样成立）。
            /// </summary>
            public int SkippedCount { get; init; }

            /// <summary>其中被跳过的是内容物的条数。</summary>
            public int SkippedContentCount { get; init; }

            /// <summary>因为用户选了「覆盖」而被顶掉的同名条目数。</summary>
            public int OverwrittenCount { get; init; }

            /// <summary>被顶掉的落点（写日志用：**哪些文件被覆盖**必须留痕）。</summary>
            public IReadOnlyList<string> OverwrittenPaths { get; init; } = Array.Empty<string>();

            /// <summary>内容物文件数（来自定稿计划，不是"搬了几条"）。</summary>
            public int ContentFileCount { get; init; }

            /// <summary>其余物目录（定稿计划算出来的那个）。源包搬运要用它当目标根（决策 D-10/D-12）。</summary>
            public string ProcessArtifactDirectory { get; init; } = string.Empty;

            public long ProcessArtifactBytes { get; init; }

            public int RenamedCount { get; init; }

            public int FailedCount { get; init; }

            public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();

            public string Message { get; init; } = string.Empty;

            /// <summary>要带回 UI 线程写日志的行（后台线程不许直接写界面集合）。</summary>
            public List<(string Level, string Message)> LogEntries { get; init; } = new();
        }

        /// <summary>
        /// 定稿布局计划：直接包住 <see cref="ResultFinalizer"/> 的结论，只加三样执行阶段要用的东西 ——
        /// 其余物源路径的集合（执行时判断一条移动属于哪一类）与"这是不是一次失败的规划"。
        ///
        /// 为什么不再自己算一套相对路径：判定表（契约 §3.1：终端单文件直接放 / 多文件套一层 /
        /// 多重空目录提上来 / 单链塌缩）**只有一份实现**，就在 <see cref="ResultFinalizer"/> 里。
        /// 本类只负责"把暂存树描述给它、把它的计划执行掉"。
        /// </summary>
        internal sealed class FinalLayoutPlan
        {
            public bool Failed { get; init; }

            public string FailureReason { get; init; } = string.Empty;

            public FinalizeLayoutKind Layout { get; init; } = FinalizeLayoutKind.Empty;

            /// <summary>全部移动（**内容物在前、其余物在后**，按这个顺序执行）。</summary>
            public IReadOnlyList<PlannedMove> Moves { get; init; } = Array.Empty<PlannedMove>();

            /// <summary>计划里的内容物条数。</summary>
            public int PlannedContentCount { get; init; }

            /// <summary>哪几条是其余物（按 From 查，执行时用）。</summary>
            public HashSet<string> ProcessArtifactSources { get; init; } = new(StringComparer.OrdinalIgnoreCase);

            public string ProcessArtifactDirectory { get; init; } = string.Empty;

            public long ProcessArtifactTotalSize { get; init; }

            public int ContentFileCount { get; init; }

            public string Summary { get; init; } = string.Empty;

            public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        }

        /// <summary>
        /// 解压成功之后的收尾：**定稿搬运** → 校验 → 归集 → 视开关清理源包（M3/契约 §2）。
        ///
        /// 顺序不能换：
        /// 0. 先定稿 —— 暂存区里的产物只有搬到最终目录才算"结果"，校验也必须以搬完之后的落点为对象
        ///    （契约 §2.2：定稿是搬运，不是"原地解压"）；
        /// 1. 再校验 —— 没有校验就没有"删除源包"的资格（AGENTS.md §9.5）；
        /// 2. 再归集 —— 归集是移动；先归集再校验会算不准（文件已经不在原输出目录）；
        /// 3. 最后清理源包 —— 它依赖前两步的结论。
        ///
        /// 任何一步失败都**不改变**"解压成功"这个结论，只是把结论写进日志与任务字段；
        /// 不要因为归集或清理失败就把任务标成失败 —— 用户的文件确实解出来了。
        ///
        /// 三个例外（它们不是"收尾失败"，而是**解压结论本身不成立**，必须顶掉"解压成功"）：
        /// ① 产物越出目标根目录（不变量 4）—— 不归集、不清理，状态落成失败；
        /// ② 产物超出资源预算 —— 同样是失败，并停止后续任务；
        /// ③ 定稿时被取消 —— 已经搬过去的会搬回来，最终目录不留半成品，状态落成"已取消"。
        ///
        /// 返回值就是这个语义：<c>true</c> = "解压成功"仍然成立；<c>false</c> = 已被上面三条否掉，
        /// 调用方**不许**再补一句"解压成功：xxx"（那会与任务状态自相矛盾，等于骗人）。
        ///
        /// 线程规则（这是 P0 修复的关键）：
        /// 收尾里的"全目录枚举 / 二次遍历 / 定稿搬运 / 归集移动 / 删除源包"**全是同步磁盘活**，
        /// 之前整段跑在 UI 线程上（整条管线没有 ConfigureAwait(false)，await 的续体全回 Dispatcher），
        /// 780MB 的包解出几千个文件时窗口彻底无响应 —— 与"抠出内嵌归档"是同一个坑，
        /// 那边已经改成 Task.Run 并写了注释，收尾这段是漏改。
        /// 现在重活统一进 <see cref="RunPostProcessWork"/> 交给 Task.Run，
        /// await 回来（没有 ConfigureAwait(false)，续体仍在 UI 上下文）才写任务状态与日志。
        ///
        /// 取消规则（P1）：收尾每一步之前都查令牌。用户按了「取消当前」就不许再移动产物、更不许删源包
        /// （AGENTS.md §9.5：取消、部分完成、校验失败一律不删）。取消由上层 catch 落成"已取消"，不得显示成功。
        /// </summary>
        /// <param name="stageDirectory">
        /// 本任务的暂存目录（入仓阶段的产物在这里，最终目录此时还是干净的）。
        /// </param>
        /// <param name="oneClickRun">
        /// 这一批是不是「一键处理」发起的整理路径（决策 D-9）。
        ///
        /// <para>
        /// ⚠ <b>它已经不再决定"要不要动源包"</b>（用户 2026-09-22 的新规则：两条路径一致，
        /// 成功 + 校验通过就把源包移入 <c>其余物</c>；见下方源包处理那一段）。
        /// 现在它只剩一个用处：**一键处理有续解链、手动「只解压」没有** ——
        /// 真实文件常常"第一层只出中间件"，那时一键处理要把源包搬运留到链结束后补做，
        /// 而单层的「只解压」当场就能按"定稿 + 校验通过"处理掉。
        /// </para>
        /// </param>
        private async Task<bool> PostProcessSuccessAsync(
            ArchiveTask task,
            string engineArchivePath,
            string password,
            string stageDirectory,
            string outputRedirectNote,
            OutputPlacementMode placementMode,
            bool oneClickRun,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1) 校验：拿到引擎声明的条目数与总大小，和落盘结果对一遍。
            // 清单同样取自**真正解开的那份归档**：内嵌归档要拿抠出来的文件去列，源文件 7z 根本打不开。
            // 密码照旧传进去：加密头（-mhe）的包不给密码根本列不出清单，校验会直接退化成"没法比"。
            ArchiveListResult expected = await _archiveEngine.ListAsync(
                ArchiveRequest.For(engineArchivePath, password),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // 开关在进后台之前读一次：设置是用户可改的，别让后台线程读到半路改掉的值。
            bool collectResults = Settings.CollectResultsToDirectory;
            string collectTargetDirectory = Settings.CollectTargetDirectory;

            /*
             * 源包处理（决策 D-9，一次读清、三档互斥）。
             *
             * **两条路径一致**（用户 2026-09-22 拍板，推翻上一版"地基路径永远不动源包"）：
             * 不论是一键处理还是手动「只解压」，源包档位都取自 SourceHandling，
             * 三条前提（内容物已定稿 + 输出校验通过 + 未取消 + 属于本任务分卷组）由
             * MoveSourcePackageIntoRest 统一把关。用户原话：
             * 「如果成功了你就直接将源包放在其余物里面」「地基路径也照这条走」。
             *
             * 档位语义：
             * · MoveToRest（默认）—— 成功 + 校验通过就把整组源包移入其余物；
             * · KeepInPlace —— 一个字节都不搬（"传统解压器"语义的出口）；
             * · DeleteAfterVerify —— 沿用 §9.5：校验通过后删源包（回收站/彻底删除按既有设置）。
             *
             * 为什么 deleteSource 要按档位算，而不是照抄一个布尔：
             * "移"和"删"绝不能同时生效 —— 刚搬进其余物的源包会被下一步清理掉，
             * 用户看到的就成了"源包没了"（这句踩坑记录从上一版保留）。
             * 另外地基路径上那个更老的 §9.5 开关 DeleteSourceAfterExtract（默认关）仍然算数，
             * 但**只在 KeepInPlace 档**：那是"传统解压器 + 解压后清理源包"的既有组合，
             * 上一版就是这么用的，不该被这次改动悄悄改掉；MoveToRest 档下它不参与，
             * 否则用户开过这个老开关就会在"移入其余物"之后立刻被删掉。
             *
             * ⚠ 「本次选项」里选过源包处理时**以它为准**（规格 §9.2 硬要求①：覆盖只对本次有效）：
             * RunOptions 只在"一键处理这一批"里非空，地基路径永远是 null → 照旧读设置。
             */
            SourceHandlingMode sourceHandling = RunOptions?.SourceHandling
                ?? AppSettings.ParseSourceHandling(Settings.SourceHandling);

            bool deleteSource = sourceHandling == SourceHandlingMode.DeleteAfterVerify ||
                (!oneClickRun && sourceHandling == SourceHandlingMode.KeepInPlace && Settings.DeleteSourceAfterExtract);

            /*
             * 终端落法（规格 §3.1 / 设置项 TerminalLayoutMode）同样在这里读一次并解析：
             * 解析口径唯一（OutputPlacement.ParseTerminalLayoutMode），非法值回落 KeepLastFolder，
             * 所以一个读不懂的配置只会退到"最不意外"的那一档，不会把落点算成别的东西。
             * 「本次选项」里选过就以它为准（同上，只对本次一键处理有效）。
             */
            TerminalLayoutMode terminalLayout = RunOptions?.TerminalLayout
                ?? OutputPlacement.ParseTerminalLayoutMode(Settings.TerminalLayoutMode);

            /*
             * 同名冲突的决定：Ask 档**必须在动最终目录之前**拿到答案（"暂停该任务"就发生在这里）。
             *
             * 位置刻意放在收尾重活之前、并且只在本任务第一次撞上冲突时问：
             * · 预检是磁盘活（要算一遍定稿计划 + 判每个落点是否存在）→ 走 Task.Run，UI 线程不碰盘；
             * · 询问本身是 UI 活 → 走 DialogService 的异步重载（可取消、无界面宿主不弹窗、不用同步 Invoke）；
             * · 非 Ask 档一个字都不问，直接由档位推出结论（默认 AutoRename，与既有行为一致）。
             *
             * 已经答过"全部 X"或是本任务已经答过"这一个"时（GetEffectiveConflictDecision 非空），
             * 连预检都不做 —— 决策 D-4：50–200+ 个包逐个问等于不可用。
             */
            string conflictAction = ConflictActionValue;
            ConflictDecision? conflictDecision = null;

            if (ConflictActions.IsAsk(conflictAction))
            {
                ConflictDecision? decided = GetEffectiveConflictDecision();

                if (decided == null && !IsConflictPromptUnavailable)
                {
                    ConflictPrecheck precheck = await Task.Run(
                        () => PrecheckFinalLayoutConflicts(
                            task,
                            stageDirectory,
                            task.OutputPath,
                            placementMode,
                            terminalLayout),
                        cancellationToken);

                    if (precheck.TotalCount > 0)
                    {
                        string message =
                            $"定稿时发现 {precheck.TotalCount} 个同名冲突（目标已经存在），" +
                            (precheck.ContentCount > 0
                                ? $"其中 {precheck.ContentCount} 个是内容物。"
                                : "都在其余物里。") +
                            "请选择同名时怎么处理。";

                        decided = await AskConflictAsync(
                            task,
                            message,
                            BuildConflictDetail(precheck),
                            cancellationToken);
                    }
                }

                // 问不到答案（无界面宿主 / 超时）→ 保守档：自动重命名落位，绝不覆盖（不变量 3）。
                conflictDecision = decided ?? ConflictDecision.Conservative;
            }

            PostProcessWorkResult work = await Task.Run(
                () => RunPostProcessWork(
                    task,
                    stageDirectory,
                    expected,
                    collectResults,
                    collectTargetDirectory,
                    deleteSource,
                    sourceHandling,
                    placementMode,
                    terminalLayout,
                    oneClickRun,
                    conflictAction,
                    conflictDecision,
                    cancellationToken),
                cancellationToken);

            // 回到 UI 线程：只做状态与日志，不再碰大盘。
            foreach ((string level, string message) in work.LogEntries)
            {
                AppendLog(level, message);
            }

            /*
             * 定稿被取消：产物已经搬回暂存区，最终目录里没有半成品。
             * 这里必须抛出去让上层落成"已取消"（不变量 6：取消不得显示成功），
             * 而不是 return false —— 那会被上层当成"结论不成立"，任务变成"解压失败"，
             * 用户看到的原因就成了"失败"，但他的操作其实是取消。
             */
            if (work.Commit?.Cancelled == true)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (work.BudgetExceeded)
            {
                task.Status = StatusText.ExtractFailed;
                task.ErrorMessage = work.BudgetMessage;

                // 不发布、不清理：产物留在原地让用户自己判断，源文件更不能删。
                StopAfterCurrent();
                return false;
            }

            if (!string.IsNullOrWhiteSpace(work.LandingViolation))
            {
                /*
                 * 产物越界（不变量 4）：这不是"收尾出的小问题"，而是**这次解压的结果不能承认**。
                 *
                 * 旧实现只写了一行 ERROR 日志：任务照样是"解压成功"，产物照样被归集走、源包照样被删 ——
                 * 用户的文件已经写到目标根之外了，程序却报告一切正常。现在与递归路径同一口径：
                 * 状态落成失败、原因写进 ErrorMessage，归集与清理在 RunPostProcessWork 里就已经不做了
                 * （它在越界处直接 return，这里也**不允许**补做任何一步）。
                 */
                task.Status = StatusText.ExtractFailed;
                task.ProgressText = StatusText.ProgressFailed;
                task.ErrorMessage = work.LandingViolation;

                // 结论不成立时不许在详情里显示"输出校验通过"。
                task.IsOutputVerified = false;
                task.LastUpdatedTime = DateTime.Now;

                return false;
            }

            if (work.CommitFailed)
            {
                // 定稿一个内容物都没搬过去：产物还在暂存区，最终目录是空的 —— 不能报成功（不变量 6）。
                task.Status = StatusText.ExtractFailed;
                task.ProgressText = StatusText.ProgressFailed;
                task.ErrorMessage = work.CommitFailureMessage;
                task.IsOutputVerified = false;
                task.LastUpdatedTime = DateTime.Now;

                return false;
            }

            /*
             * 同名冲突全部选了「跳过」：一个内容物都没落位。
             *
             * 这是"用户的选择"，不是失败，但**绝不能报成功**（不变量 6 的反面同样成立：跑完了没产物，
             * 说成"解压成功"就是骗人 —— 用户会去那个目录里找一份根本不存在的产物）。
             * 同时也不能清理工作区：产物还在暂存目录里，那是用户唯一的一份。
             */
            if (work.Commit is { Attempted: true, MovedContentCount: 0, FailedCount: 0, SkippedContentCount: > 0 } skipped
                && skipped.PlannedContentCount > 0)
            {
                task.Status = StatusText.Skipped;
                task.Operation = StatusText.OpSkip;
                task.ProgressText = StatusText.ProgressSkipped;
                task.ErrorMessage = "同名冲突按你的选择跳过：产物仍留在暂存目录，未写入输出目录。";
                task.IsOutputVerified = false;
                task.LastUpdatedTime = DateTime.Now;

                return false;
            }

            task.IsOutputVerified = work.Verification.Verified;

            // 把"实际输出到别处"这件事写进任务对象：输出目录被自动改名时，用户必须在任务上看得见，
            // 不能只在日志里留一句就过去（AGENTS.md 不变量 6 的同一精神：结论不能静默）。
            string verifyMessage = string.IsNullOrWhiteSpace(outputRedirectNote)
                ? work.Verification.Message
                : $"{work.Verification.Message}；{outputRedirectNote}";

            // 定稿里搬不动的文件也必须让用户看得见：产物还在暂存区，不在他打开的那个目录里。
            if (work.Commit is { FailedCount: > 0 })
            {
                verifyMessage += $"；定稿时有 {work.Commit.FailedCount} 个文件没能搬到最终目录" +
                                 $"（{string.Join("、", work.Commit.Failures.Take(5))}），它们仍在暂存区。";
            }

            /*
             * 覆盖了哪些落点也要写进任务结论（不只写日志）。
             *
             * 覆盖是不可逆的（旧文件按两阶段落位删掉了），用户回头找"我原来那份去哪了"时，
             * 日志与任务详情必须都能给出答案 —— 这就是"真正覆盖前要留痕"的落点。
             */
            if (work.Commit is { OverwrittenCount: > 0 } overwriteCommit)
            {
                verifyMessage += $"；同名冲突按你的选择覆盖了 {overwriteCommit.OverwrittenCount} 项" +
                                 $"（{string.Join("、", overwriteCommit.OverwrittenPaths.Take(5))}）";
            }

            task.VerifyMessage = verifyMessage;

            /*
             * 源包没能移入其余物（决策 D-12）：内容物已经好了，所以**不**改"解压成功"这个结论，
             * 但任务整体没做完 —— 按用户明确要求标成「部分完成」并写明原因。
             * 不变量 6 的反面同样成立：跑了一半的事不许只报成功。
             */
            if (!string.IsNullOrWhiteSpace(work.SourceMoveFailure))
            {
                task.Status = StatusText.PartiallyCompleted;
                task.ErrorMessage = work.SourceMoveFailure;
                task.VerifyMessage = $"{verifyMessage}；{work.SourceMoveFailure}";
            }

            if (work.Collected != null && work.Collected.Success)
            {
                task.CollectedPath = work.Collected.DestinationPath;
            }

            /*
             * 记下**这一轮定稿实际使用的其余物目录**。
             *
             * 为什么必须记：真实文件（222.mp4 = 假 MP4 头 + 尾部 ZIP + 内层加密分卷）第一层只出中间件，
             * 源包搬运要留到整条续解链跑完之后补做 —— 那一刻已经不在这一轮的收尾里了，
             * 而"其余物到底在哪"这个事实只有定稿计划知道（归集之后还会整体平移一次）。
             * 现场重算路径会在三处出错：内容物层正好也叫「其余物」时的 `其余物(1)`、
             * 共用输出根模式下按包名分的那一层、以及归集把整个目录搬走之后的落点。
             */
            if (work.Commit != null)
            {
                string restDirectory = ResolveRestDirectoryAfterCollect(work.Commit, work.Collected);

                if (!string.IsNullOrWhiteSpace(restDirectory))
                {
                    task.RestDirectoryPath = restDirectory;
                }
            }

            /*
             * 中间工作区清理（P1，端到端验收实测一次成功就留下近 1 GB 垃圾）。
             *
             * 位置与条件都在这里定死：**解压成功 + 输出校验通过 + 没被取消**。
             * 越界 / 超预算在上面已经 return（结论不成立）；取消会让上面的 await 抛 OperationCanceledException；
             * 校验没过时产物可能不全，工作区里的中间件是用户唯一的线索 —— 一律不删。
             *
             * 入仓（stage）之后这里更要紧了：暂存目录就在本任务的工作区里，产物已经定稿搬走，
             * 剩下的全是可再生的中间件。
             */
            if (work.Verification.Verified && !cancellationToken.IsCancellationRequested)
            {
                CleanupTaskWorkspaceDirectory(task, stageDirectory);
            }

            /*
             * 「定稿完成后打开输出目录」（设置项 OpenOutputFolderWhenDone，**默认关**）。
             *
             * 触发条件刻意收得最紧（四条全中才开）：
             * ① 这一批还没开过（TryOpenOutputFolderOnce 里的批记账 —— 50–200 个包每个都开一次是骚扰）；
             * ② 不是续解出来的内层包（它们与父任务共用同一个目录，反复打开就是刷屏）；
             * ③ 内容物已定稿（走到这一行说明定稿分支都没 return，见上面的越界/超预算/定稿失败早退）；
             * ④ 输出校验通过 + 没被取消。
             *
             * 失败 / 取消 / 部分完成一律不开：打开一个空目录只会误导用户（他以为东西在里面）。
             * 打开动作本身**只开文件夹，不置前、不最大化、不抢焦点**（AGENTS.md §13）。
             *
             * 整段包着 try/catch：这是收尾的最后一步，一个"打开资源管理器失败"绝不允许
             * 把已经成功的解压拖成异常（结论已经写在任务上了）。
             */
            try
            {
                if (work.Verification.Verified &&
                    !cancellationToken.IsCancellationRequested &&
                    task.Status == StatusText.ExtractSuccess)
                {
                    TryOpenOutputFolderOnce(task);
                }
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"{task.FileName}：打开输出目录失败（不影响解压结论）：{ex.Message}");
            }

            // 走到这里说明"解压成功"这个结论没有被越界 / 超预算顶掉。
            return true;
        }

        /// <summary>
        /// 收尾重活本体：落点校验 → 结果校验 → 预算事后判定 → **定稿搬运** → 归集 → 处理源包。
        ///
        /// 校验与预算量的都是**暂存区**：那才是引擎真正写盘的地方（契约 §2.1）。
        /// 最终目录此时还是干净的，只有定稿那一步会往里面写东西。
        ///
        /// **只允许在后台线程上跑**（见 <see cref="PostProcessSuccessAsync"/> 的线程规则）：
        /// 这里只碰文件系统，不写任务状态、不写界面日志集合，要说的都放进 LogEntries 回传。
        /// </summary>
        /// <param name="oneClickRun">
        /// 是不是「一键处理」发起的整理路径。**只影响一件事**：一键处理有续解链，
        /// 所以"本轮没有内容物"时源包搬运要记成"留到链结束后补搬"；
        /// 手动「只解压」是单层路径，没有链可等，当场按"定稿 + 校验通过"处理。
        /// </param>
        private PostProcessWorkResult RunPostProcessWork(
            ArchiveTask task,
            string stageDirectory,
            ArchiveListResult expected,
            bool collectResults,
            string collectTargetDirectory,
            bool deleteSource,
            SourceHandlingMode sourceHandling,
            OutputPlacementMode placementMode,
            TerminalLayoutMode terminalLayout,
            bool oneClickRun,
            string conflictAction,
            ConflictDecision? conflictDecision,
            CancellationToken cancellationToken)
        {
            var logEntries = new List<(string Level, string Message)>();

            cancellationToken.ThrowIfCancellationRequested();

            OutputVerificationResult verification = OutputVerifier.Verify(
                stageDirectory,
                expected.Success ? expected : null);

            /*
             * 解压后落点校验（第二道防线，不变量 4）：产物必须都落在目标根目录之内。
             *
             * 量的是**暂存区**：引擎写盘的地方就是它（契约 §2.1）。落点校验必须在定稿**之前**做，
             * 否则越界的产物已经被搬进最终目录，再报"拒绝承认"就晚了 —— 用户目录里已经多了东西。
             *
             * 旧写法（本轮修掉）：越界时只写一行 ERROR 日志，**结论照样是"解压成功"**，
             * 而且照样归集产物、照样清理源包 —— 等于把不变量 4 降级成一条没人看的提示：
             * 越界的产物被搬进结果目录、源包被删掉，程序却报一切正常。
             *
             * 现在与递归路径（RecursiveExtractor 的第二道防线）同一口径：
             * 越界 = **失败结论**，不定稿、不归集、不清理，原因写回任务状态与错误信息。
             * 这里直接 return，让"不定稿不归集不清理"由控制流保证 —— 不依赖后面每一步都记得查这个标志。
             *
             * 复用 RecursiveExtractor.FindLandingViolation 而不是就地再枚举一遍：
             * 两条路径的口径必须一样，复制一份迟早分叉；而且它还能发现目录联接点 / 符号链接
             * （名字在产物目录里、内容却写到目录外）—— 旧写法只枚举文件，看不出这一类。
             */
            string? landingViolation = RecursiveExtractor.FindLandingViolation(stageDirectory);

            if (landingViolation != null)
            {
                /*
                 * 这一句就是"产物越界即整包失败"在界面上的口径（README「设计边界」已声明，
                 * 以前界面上只有一行日志，用户看不到）。所以它必须一次说清四件事：
                 * ① 越到哪里去了；② 这是**整包失败**，不是"少解了几个文件"；
                 * ③ 因此**不归集、不清理源包、其余物不生成**（不变量 4）；
                 * ④ 已经写出去的那一次拦不住（诚实边界，别让用户以为我们拦住了）。
                 * 落进 task.ErrorMessage（任务行 + 详情 + 失败清单都看得到），不是只进日志。
                 */
                string landingMessage =
                    "产物越出目标根目录 —— 按既定口径**整包判定失败**（不归集产物、不处理源包、其余物不生成）：" +
                    landingViolation +
                    "。⚠ 解压是外部 7-Zip 进程写的盘，越界的那一次写入拦不住；" +
                    "这次的结果不能承认，产物留在暂存目录里供你自行判断，请先确认包里有没有异常条目。";

                logEntries.Add(("ERROR", $"{task.FileName}：{landingMessage}"));

                return new PostProcessWorkResult
                {
                    Verification = verification,
                    LogEntries = logEntries,
                    LandingViolation = landingMessage
                };
            }

            /*
             * 运行时预算的事后判定。
             *
             * 7z 写盘的时候我们拦不住（外部进程，我们看不到它的写入），所以"超限就停"只能在事后做：
             * 量一遍产物，超了就**停止后续任务**并把这一单标出来 —— 至少不会让整盘被一个包吃掉。
             * 这条只在引擎给不出清单时才真正有意义，但事后量一遍很便宜，就一直做。
             */
            (int producedFiles, long producedSize) = OutputVerifier.Measure(stageDirectory);
            ResourceBudgetOptions budgetLimits = ResourceBudgetOptions.Default;

            if (producedSize > budgetLimits.MaxTotalSize || producedFiles > budgetLimits.MaxFileCount)
            {
                string budgetMessage =
                    $"解压产物超出资源预算：{producedFiles} 个文件 / {producedSize} 字节" +
                    $"（上限 {budgetLimits.MaxFileCount} 个 / {budgetLimits.MaxTotalSize} 字节）。已停止后续任务。";

                logEntries.Add(("ERROR", $"{task.FileName}：{budgetMessage}"));

                return new PostProcessWorkResult
                {
                    Verification = verification,
                    LogEntries = logEntries,
                    BudgetExceeded = true,
                    BudgetMessage = budgetMessage
                };
            }

            logEntries.Add((verification.Verified ? "INFO" : "WARN", $"{task.FileName}：结果校验 —— {verification.Message}"));

            /*
             * 2) 定稿（契约 §2.2）：把暂存产物**一次性**搬到最终目录，中间件归入 <c>其余物</c>。
             *
             * 只在这一刻、只有这一条路径会往最终目录写东西。之前的所有动作（抠内嵌归档、解第一层、
             * 解内层分卷）都在私有暂存目录里完成 —— 这就是用户那句"分卷文件你居然又解压到外面来了"
             * 的根治点：源目录与最终目录在入仓阶段一个字节都不会被写。
             *
             * 搬运本身是**同步磁盘活**（几千个文件的 File.Move、跨盘还是拷贝），
             * 所以它跟着本方法一起跑在后台线程上（见 PostProcessSuccessAsync 的线程规则）。
             */
            StageCommitResult? commit = ExecuteFinalLayout(
                task,
                stageDirectory,
                task.OutputPath,
                placementMode,
                terminalLayout,
                conflictAction,
                conflictDecision,
                cancellationToken);

            if (commit.Attempted)
            {
                foreach ((string level, string message) in commit.LogEntries)
                {
                    logEntries.Add((level, message));
                }
            }

            if (commit.Cancelled)
            {
                // 取消：搬过去的已经搬回暂存区，最终目录没有半成品。结论由上层落成"已取消"。
                logEntries.Add(("WARN", $"{task.FileName}：定稿被取消，产物已退回暂存目录，未写入最终目录。"));

                return new PostProcessWorkResult
                {
                    Verification = verification,
                    LogEntries = logEntries,
                    Commit = commit
                };
            }

            if (commit.Attempted && commit.PlannedContentCount > 0 && commit.MovedContentCount == 0 && commit.FailedCount > 0)
            {
                /*
                 * 计划里的内容物一条都没搬过去、而且全是失败：这次"定稿"等于没发生。
                 * 不能再报成功（不变量 6 的反面：跑完了不许说成成功，跑失败了也不许）——
                 * 结论落成失败，产物留在暂存区供用户自己取。
                 */
                string failureMessage = "定稿搬运失败，产物没能写进输出目录：" + commit.Message;

                logEntries.Add(("ERROR", $"{task.FileName}：{failureMessage}"));

                return new PostProcessWorkResult
                {
                    Verification = verification,
                    LogEntries = logEntries,
                    Commit = commit,
                    CommitFailed = true,
                    CommitFailureMessage = failureMessage
                };
            }

            // 归集是**移动**产物，动手之前再查一次令牌（取消就不再移动）。
            cancellationToken.ThrowIfCancellationRequested();

            // 3) 归集（可选）
            CollectResult? collected = null;

            /*
             * 续解出来的内层包**不参与归集**：它的落点就是父任务那一个最终目录，
             * 而归集会把那个目录整个搬走 —— 第二轮再搬一次，内容物就会被搬进 "名字(1)" 里，
             * 用户看到的还是"多了一个文件夹"。归集只由最外层源包做一次。
             */
            if (collectResults && task.IsContinuationTask)
            {
                logEntries.Add(("INFO", $"{task.FileName}：内层包，产物与父任务同一个目录，跳过结果归集。"));
            }
            else if (collectResults)
            {
                if (!verification.Verified)
                {
                    logEntries.Add(("WARN", $"{task.FileName}：校验未通过，已跳过结果归集。"));
                }
                else
                {
                    collected = new ResultCollector().Collect(task, collectTargetDirectory);

                    logEntries.Add((collected.Success ? "INFO" : "WARN", $"{task.FileName}：结果归集 —— {collected.Message}"));
                }
            }

            /*
             * 删除是**不可逆**的，AGENTS.md §9.5 明确要求"取消、部分完成、校验失败时一律不删"，
             * 所以处理源包之前单独再查一次令牌 —— 用户刚按下「取消当前」时最不该发生的就是删源包。
             * 搬运（MoveToRest）也走这一个检查点：D-11 要求"未取消"才动源包。
             */
            cancellationToken.ThrowIfCancellationRequested();

            /*
             * 4) 源包处理（决策 D-9/D-11/D-12，三档互斥）。
             *
             * 走到这里已经满足 D-11 的前三个条件：内容物已定稿（上面的 commit）、输出校验通过
             * （verification.Verified，不在下面每个分支里重查会漏，所以这里统一把门）、未取消（刚查过）。
             * 第四个条件是"属于本任务分卷组"——清单只来自任务自身（见 SourcePackageMover.ResolveSourceGroup）。
             *
             * 两条路径都走这里（用户 2026-09-22 的新规则：手动「只解压」不再例外），
             * 唯一的差别是"本轮没有内容物"时怎么办，见 MoveSourcePackageIntoRest。
             *
             * 只对**最外层源包**做：续解出来的内层包，它的"源文件"是我们自己产出的中间件
             * （现在就在 <c>其余物</c> 里），既不是用户给的包，也不该被搬走/删掉 ——
             * 用户按下"解压后删除源包"时想删的是他拖进来的那个包，不是其余物里的中间件。
             */
            SourcePackageMoveResult? sourceMove = null;
            string? sourceMoveFailure = null;

            if (task.IsContinuationTask)
            {
                if (sourceHandling != SourceHandlingMode.KeepInPlace)
                {
                    logEntries.Add(("INFO", $"{task.FileName}：内层包，源文件属于其余物里的中间件，已跳过源包处理。"));
                }
            }
            else if (sourceHandling == SourceHandlingMode.MoveToRest)
            {
                sourceMove = MoveSourcePackageIntoRest(
                    task, verification, commit, collected, oneClickRun, logEntries);

                if (sourceMove.FailedCount > 0)
                {
                    // 内容物已经好了，只有源包没搬成 —— 这不是"解压失败"，而是"这件事没做完"。
                    sourceMoveFailure =
                        $"内容物已好，源包未能移入其余物：{sourceMove.Message}（源包仍在原处，内容物不受影响）";
                }
            }
            else if (sourceHandling == SourceHandlingMode.DeleteAfterVerify)
            {
                SourceCleanupResult cleanup = new SourceCleanupService().Cleanup(
                    task,
                    verification,
                    deleteSource);

                if (cleanup.Attempted)
                {
                    logEntries.Add((cleanup.FailedFiles.Count == 0 ? "INFO" : "WARN", $"{task.FileName}：清理源包 —— {cleanup.Message}"));
                }
            }

            return new PostProcessWorkResult
            {
                Verification = verification,
                LogEntries = logEntries,
                Collected = collected,
                Commit = commit,
                SourceMoveFailure = sourceMoveFailure
            };
        }

        /// <summary>
        /// 把本任务的**整组源包**搬进其余物（决策 D-9/D-11/D-12；用户 2026-09-22 起两条路径一致）。
        ///
        /// <para>
        /// 四个前置条件缺一不可（D-11）：① 内容物已定稿；② 输出校验通过；③ 未取消（调用方刚查过令牌）；
        /// ④ 属于本任务分卷组。任何一个不成立就**一个字节都不动**，并且不生成 <c>其余物</c> 目录。
        /// </para>
        /// <para>
        /// <b>2026-09-22 修掉的那个真实缺陷</b>：条件 ① 原来死认"本轮搬过内容物"
        /// （<c>commit.MovedContentCount &gt; 0</c>），而用户的真实文件
        /// （<c>222.mp4</c> = 假 MP4 头 + 尾部完整 ZIP + ZIP 里是头加密的 7z 分卷）**第一层只出中间件**，
        /// 内容物是续解出来的子任务产出的 —— 子任务按设计跳过源包处理（它的"源文件"是我们自己的中间件），
        /// 于是整条一键处理流水线跑完，源包还躺在原地、其余物里只有 4 个内层分卷。
        /// 现在把它分成三种情况：
        /// </para>
        /// <list type="number">
        /// <item><description><b>本轮有内容物、且没有任何搬运失败</b> → 当场搬（原有路径）。</description></item>
        /// <item><description><b>本轮没有内容物、但也没有任何搬运失败</b>（典型：第一层只出中间件）：
        /// 一键处理记成"留到链结束后补搬"（<see cref="SourcePackageMoveState.DeferredToChainEnd"/>，
        /// 由 <see cref="OneClickCoordinator"/> 在整条续解链跑完之后调
        /// <see cref="CompleteRootSourcePackagesAfterChainAsync"/>）；手动「只解压」是单层路径、
        /// 没有链可等，**当场就按"定稿 + 校验通过"处理**（用户新规则：成功就把源包放进其余物）。</description></item>
        /// <item><description><b>定稿根本没发生、或部分搬运失败</b> → 维持原判：一个字节都不动，
        /// 也不延期（这种情况连"这一轮的内容物已定稿"都不成立，延后也等不到）。</description></item>
        /// </list>
        /// <para>
        /// 目标目录的算法（这里有个坑）：其余物目录是**定稿计划**里算出来的那个，
        /// 但归集（Collect）会把整个产物目录搬走 —— 搬走之后计划里的路径已经不存在了。
        /// 归集成功时其余物跟着落到 <c>归集目录\&lt;相对路径&gt;</c>（归集保留目录结构），
        /// 所以按"计划路径相对 destDir 的那一段"平移过去即可；平移后的位置不存在就退回计划路径
        /// （归集部分失败时其余物可能只搬走了一半，留在原地的那些才是真的）。
        /// </para>
        /// <para>
        /// **只允许在后台线程上跑**（跨盘时是整包拷贝，几百 MB 到几十 GB）。
        /// </para>
        /// </summary>
        /// <param name="oneClickRun">
        /// 是不是「一键处理」发起的整理路径 —— 决定"本轮没有内容物"时是延期补搬还是当场搬
        /// （见上面的情况 ②）。
        /// </param>
        private SourcePackageMoveResult MoveSourcePackageIntoRest(
            ArchiveTask task,
            OutputVerificationResult verification,
            StageCommitResult commit,
            CollectResult? collected,
            bool oneClickRun,
            List<(string Level, string Message)> logEntries)
        {
            if (!verification.Verified)
            {
                // 校验没过就没有"这次整理完成了"这回事，源包一律不动（D-11 第 2 条）。
                logEntries.Add(("WARN", $"{task.FileName}：输出校验未通过，源包留在原地（未移入其余物）。"));
                return new SourcePackageMoveResult { Attempted = false, Message = "输出校验未通过，源包留在原地" };
            }

            if (task.SourcePackageMove == SourcePackageMoveState.Done)
            {
                /*
                 * 幂等（硬要求）：这个源包**已经搬进其余物**了（上次运行搬的），绝不许再搬第二次。
                 *
                 * 不查这一步会怎样：用户对同一个包再点一次「一键处理」时，源文件已经在
                 * 上一轮的 <c>其余物</c> 里了，而新一轮的输出目录是自动改名后的 <c>pack(1)</c> ——
                 * 于是"计划里的其余物"变成 <c>pack(1)\其余物</c>，源包会被从它已经安顿好的位置
                 * **再搬一次**，用户看到的是"源包又跑回来了 / 凭空多一份"。
                 * 判据只认这个显式标记，不认"文件还在不在"：文件不在时同样可能什么都没搬成过。
                 */
                logEntries.Add(("INFO", $"{task.FileName}：源包已经搬进其余物，本次不再搬（幂等）。"));
                return new SourcePackageMoveResult { Attempted = false, Message = "源包已经搬进其余物，不再搬第二次" };
            }

            if (!commit.Attempted || commit.FailedCount > 0)
            {
                // 定稿没发生 / 有搬运失败：内容物没定稿成功，源包不动，其余物也不生成（D-11 第 1 条）。
                logEntries.Add(("WARN", $"{task.FileName}：内容物未全部定稿，源包留在原地（未移入其余物）。"));
                return new SourcePackageMoveResult { Attempted = false, Message = "内容物未全部定稿，源包留在原地" };
            }

            if (oneClickRun && commit.MovedContentCount == 0)
            {
                /*
                 * 一键处理 + 本轮没有内容物：真实文件的常见形状（第一层只出待续解的中间件）。
                 * 此刻"内容物已定稿"这个事实还不存在，但它**马上会由续解子任务产生** ——
                 * 所以不能像原来那样直接放弃，而要记成"留到链结束后补搬"。
                 *
                 * 为什么只延期、不当场搬：这一轮产物全是中间件，内容物还没出现；
                 * 一键处理的链可能还要跑两层，链没跑完就动源包＝在"内容物可能不全"时动用户唯一无法再生的东西。
                 */
                task.SourcePackageMove = SourcePackageMoveState.DeferredToChainEnd;

                logEntries.Add((
                    "INFO",
                    $"{task.FileName}：本轮产出的都是待续解的中间件（没有内容物定稿），" +
                    $"源包先留在原地，等整条续解链跑完后再按设置搬进其余物。"));

                return new SourcePackageMoveResult { Attempted = false, Message = "本轮没有内容物，源包搬运留到链结束后补搬" };
            }

            string artifactRoot = ResolveRestDirectoryAfterCollect(commit, collected);

            if (string.IsNullOrWhiteSpace(artifactRoot))
            {
                logEntries.Add(("WARN", $"{task.FileName}：其余物目录算不出来，源包留在原地。"));
                return new SourcePackageMoveResult { Attempted = false, Message = "其余物目录算不出来" };
            }

            return ExecuteSourcePackageMove(task, artifactRoot, SourceMoveTrigger.DirectInRound, logEntries);
        }

        /// <summary>
        /// 源包搬运的**触发点**：两条路径共用同一个执行体，但日志必须能分清是哪一次搬的
        /// （用户要求：链结束后的补搬与本轮直接搬要在日志里区分开）。
        /// </summary>
        private enum SourceMoveTrigger
        {
            /// <summary>本轮定稿搬完内容物之后直接搬（原有路径）。</summary>
            DirectInRound,

            /// <summary>整条续解链跑完之后对"本轮没有内容物"的源包补搬（2026-09-22 修复的落点）。</summary>
            AfterChain
        }

        /// <summary>
        /// 源包搬运的执行体（规划 → 执行 → 记账 → 日志 → 回写任务路径）。两条触发路径共用一份，
        /// 免得"本轮直接搬"和"链结束后的补搬"在幂等、日志、跨盘规则上各写一套、迟早分叉。
        ///
        /// <para>
        /// **幂等记账在这里**（硬要求）：搬成（或压根没有可搬的）之后就把
        /// <see cref="ArchiveTask.SourcePackageMove"/> 落成 <see cref="SourcePackageMoveState.Done"/> ——
        /// 同一个源包绝不允许被搬第二次，而且这个判断只认标记，不认"文件还在不在"
        /// （搬运失败时文件当然还在原地，靠它猜会导致每次运行都再试一遍、多出一份 <c>222(1).mp4</c>）。
        /// 失败时保持原状态，让下一次运行还有机会补上（见方法里那段说明）。
        /// </para>
        ///
        /// **只允许在后台线程上跑**（跨盘时是整包拷贝）。
        /// </summary>
        private SourcePackageMoveResult ExecuteSourcePackageMove(
            ArchiveTask task,
            string artifactRoot,
            SourceMoveTrigger trigger,
            List<(string Level, string Message)> logEntries)
        {
            /*
             * 搬运本体（SourcePackageMover，纯 Extraction 层、可脱离 WPF 单测）：
             * 整组一起移、绝不覆盖、跨盘先复制成功再删原件、单个失败不影响其余。
             */
            var mover = new SourcePackageMover(SourceMoveFileSystem);

            SourcePackageMovePlan plan = mover.Plan(task, artifactRoot);
            SourcePackageMoveResult result = mover.Execute(plan);

            /*
             * 幂等记账（硬要求）：**只有确实搬成了、或压根没有可搬的**（源包已经在其余物里、
             * 清单里的文件都已不存在）才落成 <see cref="SourcePackageMoveState.Done"/>，
             * 之后任何路径都不许再动这个源包。
             *
             * 搬失败（只读 / 被占用 / 跨盘复制失败）时**保持原状态**：那说明源包还原封不动地
             * 留在原地（D-12：整组一起移、绝不先删后移），任务已经标成「部分完成」并写明原因；
             * 下一次运行还有机会把它补上 —— 把"尝试过"也记成"搬过了"，用户就再也修不好这一次失败。
             */
            if (result.FailedCount == 0)
            {
                task.SourcePackageMove = SourcePackageMoveState.Done;
            }

            foreach (string line in result.LogLines)
            {
                logEntries.Add(("INFO", $"{task.FileName}：{line}"));
            }

            // 日志口径要能区分"本轮直接搬"与"链结束后的补搬"（用户明确要求）。
            string label = trigger == SourceMoveTrigger.AfterChain ? "链结束后的补搬" : "源包处理";

            if (result.Attempted)
            {
                logEntries.Add((result.FailedCount == 0 ? "INFO" : "WARN", $"{task.FileName}：{label} —— {result.Message}"));

                if (result.FailedCount > 0)
                {
                    // 失败必须写 WARN 并说清原因（不许只留一句汇总）。
                    logEntries.Add((
                        "WARN",
                        $"{task.FileName}：{label}没能完成 —— {string.Join("；", result.Failures)}" +
                        $"（源包仍在原处，内容物不受影响）"));
                }
            }
            else
            {
                logEntries.Add(("INFO", $"{task.FileName}：{label} —— {plan.Message}"));

                foreach (ArtifactSkip skip in plan.Skipped)
                {
                    logEntries.Add(("WARN", $"{task.FileName}：{skip.Message}：{skip.SourcePath}"));
                }
            }

            if (result.MovedCount > 0)
            {
                /*
                 * 源文件换了地方，任务对象必须跟着改（ArchiveTask.CurrentPath 的语义就是"当前真实路径"）。
                 * 不改的后果是具体的：一键处理的续解扫描按"解压后新出现的归档起点"找内层包，
                 * 而它排除"源文件"用的正是任务的 CurrentPath / VolumePaths ——
                 * 于是刚搬进其余物的源包会被当成一个**新的内层包**再解一遍，
                 * 内容物被重复写进同一个输出目录（实测过的那种"凭空多一份"）。
                 *
                 * 仍然把 OriginalPath 留着（它表示"最初导入路径"，详情窗口与报告都读它）。
                 */
                ApplySourceMoveResult(task, result);
            }

            return result;
        }

        /// <summary>链结束后补搬的一次后台作业结论（日志与"要不要把任务标成部分完成"都从这里回传）。</summary>
        private sealed class DeferredSourceMoveWork
        {
            public DeferredSourceMoveWork(List<(string Level, string Message)> logEntries, string? failure = null)
            {
                LogEntries = logEntries;
                Failure = failure;
            }

            public List<(string Level, string Message)> LogEntries { get; }

            /// <summary>非 null = 源包没能搬成（任务要标「部分完成」，内容物结论不受影响）。</summary>
            public string? Failure { get; }
        }

        /// <summary>
        /// **链结束后的补搬**（2026-09-22 修掉的真实缺陷的落点）。
        ///
        /// <para>
        /// 场景：<c>222.mp4</c> = 假 MP4 头 + 尾部完整 ZIP + ZIP 里是头加密的 7z 分卷。
        /// 第一层只解出 4 个内层分卷（都是"其余物"），内容物要到第 2 层续解才出现；
        /// 而续解子任务按设计**跳过源包处理**（它的"源文件"是我们自己产出的中间件）。
        /// 于是最外层那一轮既没有内容物、又是唯一有资格处理源包的任务 —— 整条链跑完，源包还躺在原地。
        /// 修法：最外层那一轮把源包记账成"留到链结束后补搬"，由
        /// <see cref="OneClickCoordinator"/> 在**整条续解链跑完之后**（它才知道链什么时候结束）
        /// 调这里补做一次。
        /// </para>
        ///
        /// <para>
        /// 判据全部是**明确的事实**，不拿"本轮搬了几条"当唯一依据（用户明确要求）：
        /// </para>
        /// <list type="number">
        /// <item><description>链已结束 —— 调用方只在没被「停止后续」打断、也没撞轮数上限时才调（它在链尾）；</description></item>
        /// <item><description>根任务以「解压成功」收尾、且输出校验通过，且**分卷组完整**（不完整就不动源包）；</description></item>
        /// <item><description>链里**每一个**把那个最终目录当落点的任务都通过了输出校验，而且至少有一个确实落在那里
        /// （内容物是它们共同定稿的；只看根任务会漏掉"第二层校验没过却照样搬"）；</description></item>
        /// <item><description>那个目录里**确实有内容物文件** —— 排除 <c>其余物</c> 里的东西、排除归档/分卷这类中间件；
        /// 这就是"内容物已定稿"的事实依据；</description></item>
        /// <item><description>未取消（调用方与本方法各查一次令牌）；</description></item>
        /// <item><description><see cref="ArchiveTask.SourcePackageMove"/> 记账：只有被标记成"待补搬"且没搬过的才做，
        /// 搬过（<see cref="SourcePackageMoveState.Done"/>）的一律跳过 —— 同一个源包绝不搬第二次。</description></item>
        /// </list>
        ///
        /// <para>
        /// 任何一条不成立：**源包原地不动**，并写一行说清原因（WARN/INFO），绝不让它静默失败。
        /// </para>
        /// </summary>
        /// <param name="rootTasks">本批的**最外层**任务（一键处理第一轮的输入）。</param>
        /// <param name="chainTasks">整条链上的全部任务（根 + 续解子任务）：用来找"谁把内容物定稿到了那个目录"。</param>
        /// <param name="cancellationToken">取消令牌（既有调用点按位置传它，所以它必须留在原位置）。</param>
        /// <param name="runOptions">
        /// 本批的「本次选项」快照（规格 §9）。
        ///
        /// <para>
        /// ⚠ 必须**显式传进来**，不能读 <see cref="RunOptions"/> 字段：本方法是在整条续解链跑完之后
        /// 由 <c>OneClickCoordinator</c> 调用的，那时批量解压本体早就退出、字段已经置空
        /// （见 <c>StartExtractCoreAsync</c> 的 finally）—— 读字段会静默退回设置值，
        /// 于是"本次选了留在原地"的源包在链结束后被照设置搬走（正是最不该发生的静默行为）。
        /// </para>
        /// <para>
        /// 参数排在 <paramref name="cancellationToken"/> **之后**：既有调用点（含测试）按位置传令牌，
        /// 插在它前面会让那些调用点编译不过 —— 加可选参数不该改动别人的调用形状。
        /// </para>
        /// </param>
        internal async Task CompleteRootSourcePackagesAfterChainAsync(
            IReadOnlyList<ArchiveTask>? rootTasks,
            IReadOnlyList<ArchiveTask>? chainTasks,
            CancellationToken cancellationToken = default,
            OneClickRunOptions? runOptions = null)
        {
            if (rootTasks == null || rootTasks.Count == 0)
            {
                return;
            }

            // 设置在进后台之前读一次：设置是用户随时可改的，别让后台线程读到半路改掉的值。
            // 本次选项优先（同上：只对这一次生效）。
            SourceHandlingMode sourceHandling = runOptions?.SourceHandling
                ?? AppSettings.ParseSourceHandling(Settings.SourceHandling);

            foreach (ArchiveTask rootTask in rootTasks)
            {
                if (rootTask == null || rootTask.SourcePackageMove != SourcePackageMoveState.DeferredToChainEnd)
                {
                    continue;
                }

                // 后台重活（目录遍历 + 可能的整包跨盘拷贝），日志先收集、回到 UI 线程再写。
                DeferredSourceMoveWork work = await Task.Run(
                    () => RunDeferredSourceMoveWork(rootTask, chainTasks, sourceHandling, cancellationToken),
                    CancellationToken.None);

                foreach ((string level, string message) in work.LogEntries)
                {
                    AppendLog(level, message);
                }

                if (!string.IsNullOrWhiteSpace(work.Failure))
                {
                    /*
                     * 与"本轮直接搬"同一口径（决策 D-12）：内容物已经好了，所以**不**顶掉"解压成功"，
                     * 但任务整体没做完 —— 标成「部分完成」并写明原因（不变量 6 的反面同样成立）。
                     */
                    rootTask.Status = StatusText.PartiallyCompleted;
                    rootTask.ErrorMessage = work.Failure;

                    rootTask.VerifyMessage = string.IsNullOrWhiteSpace(rootTask.VerifyMessage)
                        ? work.Failure
                        : $"{rootTask.VerifyMessage}；{work.Failure}";
                }
            }
        }

        /// <summary>
        /// 链结束后补搬的后台本体：判据 → 执行 → 结论。**只允许在后台线程上跑**。
        /// </summary>
        private DeferredSourceMoveWork RunDeferredSourceMoveWork(
            ArchiveTask rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks,
            SourceHandlingMode sourceHandling,
            CancellationToken cancellationToken)
        {
            var logEntries = new List<(string Level, string Message)>();

            // 幂等（硬要求）：已经搬成的一律跳过 —— 不靠"文件还在不在"猜。
            if (rootTask.SourcePackageMove == SourcePackageMoveState.Done)
            {
                return new DeferredSourceMoveWork(logEntries);
            }

            if (sourceHandling != SourceHandlingMode.MoveToRest)
            {
                logEntries.Add((
                    "INFO",
                    $"{rootTask.FileName}：源包处理档是「{sourceHandling}」，链结束后的补搬按该档不搬源包。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            if (rootTask.IsContinuationTask)
            {
                // 只处理最外层源包：内层包的"源文件"是其余物里的中间件（与本轮直接搬同一口径）。
                return new DeferredSourceMoveWork(logEntries);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                logEntries.Add(("WARN", $"{rootTask.FileName}：链结束后的补搬被取消，源包留在原地（其余物不生成）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            if (!OneClickCoordinator.IsSuccessStatus(rootTask))
            {
                logEntries.Add((
                    "WARN",
                    $"{rootTask.FileName}：根任务没有以「解压成功」收尾（当前状态：{rootTask.Status}），" +
                    $"链结束后不补搬源包（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            if (!rootTask.IsOutputVerified)
            {
                logEntries.Add((
                    "WARN",
                    $"{rootTask.FileName}：根任务的输出校验没有通过，链结束后不补搬源包（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            if (rootTask.IsVolumeGroup && !rootTask.IsVolumeComplete)
            {
                logEntries.Add((
                    "WARN",
                    $"{rootTask.FileName}：分卷组不完整" +
                    (string.IsNullOrWhiteSpace(rootTask.VolumeInfoText) ? string.Empty : $"（{rootTask.VolumeInfoText}）") +
                    "，链结束后不补搬源包（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            string destinationDirectory = ResolveContinuationOutputDirectory(rootTask);

            if (string.IsNullOrWhiteSpace(destinationDirectory))
            {
                logEntries.Add(("WARN", $"{rootTask.FileName}：算不出根任务的最终输出目录，链结束后不补搬源包。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            string? verificationGap = DescribeChainVerificationGap(destinationDirectory, rootTask, chainTasks);

            if (verificationGap != null)
            {
                logEntries.Add((
                    "WARN",
                    $"{rootTask.FileName}：链里的输出校验没有全部通过（{verificationGap}），" +
                    $"链结束后不补搬源包（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            int contentFiles = CountContentFiles(destinationDirectory);

            if (contentFiles == 0)
            {
                // 内容物压根没出现（例如第二层解压失败 / 密码不对）：源包留在原地，其余物不为它生成。
                logEntries.Add((
                    "WARN",
                    $"{rootTask.FileName}：{destinationDirectory} 里没有内容物（只有中间件），" +
                    $"链结束后不补搬源包（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            string artifactRoot = rootTask.RestDirectoryPath;

            if (string.IsNullOrWhiteSpace(artifactRoot))
            {
                // 定稿计划没留下其余物目录：宁可不搬，也不要把用户的源包扔到一个猜出来的位置。
                logEntries.Add((
                    "WARN",
                    $"{rootTask.FileName}：这一轮的其余物目录没有被记下来，为避免把源包搬到别处，" +
                    $"链结束后不补搬（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                logEntries.Add(("WARN", $"{rootTask.FileName}：链结束后的补搬被取消，源包留在原地（其余物不生成）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            logEntries.Add((
                "INFO",
                $"{rootTask.FileName}：链结束后的补搬 —— 整条续解链已跑完，{destinationDirectory} 里有 " +
                $"{contentFiles} 个内容物且输出校验通过；本轮没有内容物可定稿、留到现在的源包按设置移入其余物。"));

            SourcePackageMoveResult result = ExecuteSourcePackageMove(
                rootTask, artifactRoot, SourceMoveTrigger.AfterChain, logEntries);

            /*
             * 搬成功后记账已经是 Done（见 ExecuteSourcePackageMove）；搬失败时**保持"待补搬"**，
             * 让下一次运行还有机会补上（这一次的失败已经写进任务状态，不会被静默吞掉）。
             */
            string? failure = result.FailedCount > 0
                ? $"内容物已好，源包未能移入其余物：{result.Message}（源包仍在原处，内容物不受影响）"
                : null;

            return new DeferredSourceMoveWork(logEntries, failure);
        }

        /// <summary>
        /// 本任务的**最终输出目录**：归集开启时产物整棵被搬到归集目录，权威落点就是它。
        ///
        /// 转发到 <see cref="OneClickCoordinator.ResolveContinuationOutputDirectory"/>，
        /// 不在这里重写一遍 —— 续解、归集、源包补搬必须用同一个"内容物最终在哪"的口径。
        /// </summary>
        private static string ResolveContinuationOutputDirectory(ArchiveTask task) =>
            OneClickCoordinator.ResolveContinuationOutputDirectory(task);

        /// <summary>
        /// 链里的输出校验有没有缺口：**每一个**把这个目录当落点的任务都必须通过输出校验。
        ///
        /// <para>
        /// 为什么要看**整条链**而不是只看根任务：用户那个形状里内容物是**续解子任务**产出的，
        /// 根任务那一轮的校验只覆盖了"中间件都搬进去了"。只看根任务，就会出现
        /// "第二层校验没过（比如声明的条目数与落盘不符）却照样把源包搬走"。
        /// </para>
        /// <para>
        /// 为什么是"每一个都必须过"而不是"有一个过就行"：同一个最终目录是链上所有任务共同产出的，
        /// 其中任何一个没通过校验，就没有"内容物已定稿并校验通过"这个事实 ——
        /// 这与"本轮直接搬"那条路径的口径一致（它也要求 <c>verification.Verified</c>）。
        /// </para>
        /// </summary>
        /// <returns>
        /// null = 这条链在这个目录上校验齐了（且至少有一个任务确实落在这里）；
        /// 非 null = 说给用户听的原因（哪个任务没通过 / 压根没有任务落在这里）。
        /// </returns>
        private static string? DescribeChainVerificationGap(
            string destinationDirectory,
            ArchiveTask rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks)
        {
            bool anyLanding = false;

            foreach (ArchiveTask? candidate in EnumerateChainTasks(rootTask, chainTasks))
            {
                if (!SafePathHelper.PathEquals(
                        ResolveContinuationOutputDirectory(candidate), destinationDirectory))
                {
                    continue;
                }

                anyLanding = true;

                if (!candidate.IsOutputVerified)
                {
                    return $"{candidate.FileName} 的输出校验没通过";
                }
            }

            return anyLanding
                ? null
                : $"链里没有一个任务把 {destinationDirectory} 当落点（没有产物被定稿到那里）";
        }

        /// <summary>
        /// 链上的任务集合（调用方传进来的续解子任务 + 根任务自己），去重后逐个产出。
        /// 根任务单独兜一层：调用方可能只传了子任务，而"根任务也是链上的一员"这件事不能漏。
        /// </summary>
        private static IEnumerable<ArchiveTask> EnumerateChainTasks(
            ArchiveTask rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks)
        {
            var seen = new HashSet<ArchiveTask>();

            if (chainTasks != null)
            {
                foreach (ArchiveTask? candidate in chainTasks)
                {
                    if (candidate != null && seen.Add(candidate))
                    {
                        yield return candidate;
                    }
                }
            }

            if (seen.Add(rootTask))
            {
                yield return rootTask;
            }
        }

        /// <summary>
        /// 数一个最终目录里**真正的内容物文件**：排除 <c>其余物</c>（含旧名 <c>过程物</c>）里的东西，
        /// 排除归档与分卷这类"待续解的中间件"。
        ///
        /// 这是"内容物确实已定稿"的事实依据 —— 不用"本轮搬了几条"这种过程数字
        /// （用户明确要求：别拿 move 计数当唯一判据）。
        /// 读不了目录时返回 0：宁可判定"没有内容物"而不动源包。
        /// </summary>
        private static int CountContentFiles(string destinationDirectory)
        {
            if (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory))
            {
                return 0;
            }

            try
            {
                int count = 0;

                foreach (string file in Directory.EnumerateFiles(destinationDirectory, "*", SearchOption.AllDirectories))
                {
                    if (ProcessArtifactLayout.IsInsideArtifactDirectory(file, destinationDirectory))
                    {
                        continue;
                    }

                    if (IsProcessArtifactFile(file))
                    {
                        continue;
                    }

                    count++;
                }

                return count;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 归集之后其余物目录到底在哪。
        ///
        /// 归集把 <c>destDir</c> 整棵搬到 <c>归集目标\包名\</c>（**保留目录结构**），
        /// 所以"计划路径相对 destDir 的那一段"就是它在新位置下的相对路径。三种情况：
        /// ① 归集之后的位置已经存在（其余物真的被搬过去了）→ 用它；
        /// ② 它还不存在、而计划路径还在（归集只搬走了一部分）→ 用计划路径；
        /// ③ 两边都不存在（本次根本没有中间件）→ **跟着内容物走**，在归集目录里新建。
        ///
        /// ③ 是最容易写错的一档：没有中间件时 <c>destDir\其余物</c> 压根不存在，
        /// 只看"存在与否"就会退回那个**已经不存在**的旧位置，源包被扔进一个空目录（用户找不到）。
        /// </summary>
        private static string ResolveRestDirectoryAfterCollect(
            StageCommitResult commit,
            CollectResult? collected)
        {
            string planned = commit.ProcessArtifactDirectory;

            if (string.IsNullOrWhiteSpace(planned))
            {
                return string.Empty;
            }

            if (collected is not { Success: true } || string.IsNullOrWhiteSpace(collected.DestinationPath))
            {
                return planned;
            }

            try
            {
                // destDir 从计划里的路径反推：其余物目录永远在它下面（计划就是这么算的）。
                string destDir = Path.GetDirectoryName(planned) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(destDir))
                {
                    return planned;
                }

                string moved = Path.Combine(
                    collected.DestinationPath,
                    Path.GetRelativePath(destDir, planned));

                if (SafePathHelper.DirectoryExists(moved))
                {
                    return moved;
                }

                return SafePathHelper.DirectoryExists(planned) ? planned : moved;
            }
            catch
            {
                return planned;
            }
        }

        /// <summary>
        /// 搬运成功后把任务对象上的源路径改到新位置（CurrentPath 与分卷清单）。
        ///
        /// <c>task.FileName</c> 不变（只是换目录），所以界面上的名字照旧，路径列会显示新位置 ——
        /// 这是诚实的：源包确实已经在其余物里了。
        /// </summary>
        private static void ApplySourceMoveResult(ArchiveTask task, SourcePackageMoveResult result)
        {
            var byOldPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (SourcePackageMove move in result.Moved)
            {
                byOldPath[move.SourcePath] = move.TargetPath;
            }

            if (task.VolumePaths.Count > 0)
            {
                for (int i = 0; i < task.VolumePaths.Count; i++)
                {
                    if (byOldPath.TryGetValue(task.VolumePaths[i], out string? movedVolume))
                    {
                        task.VolumePaths[i] = movedVolume;
                    }
                }
            }

            if (byOldPath.TryGetValue(task.CurrentPath, out string? movedCurrent))
            {
                task.CurrentPath = movedCurrent;
            }
        }

        /// <summary>
        /// 抠出来的内嵌归档（双面文件尾部那一段）在定稿后是否搬进 <c>其余物</c>。
        ///
        /// 契约 §3.2 把"抠出来的中间 ZIP"列为其余物，本来是**该搬**的 —— 但实测下来不能搬，
        /// 因为它不是"解出来的产物"，而是**解压的输入**（用户那个源文件的一段副本）：
        ///
        /// · 双面文件 <c>user.mp4</c> 的 ZIP 里就是内层分卷 <c>inner.7z.001/.002</c>；
        ///   把它搬进 <c>其余物\user.zip</c> 之后，一键处理的续解扫描（它只看"本轮新出现的归档起点"）
        ///   会把这个 ZIP 也当成一个**新的内层包**，于是又解一遍、又产出 <c>inner.7z.001</c>、
        ///   再多续解一轮 —— 实测就是这个结果（回归测试"双面文件真实现场"从 2 轮变成 3 轮）。
        /// · 而且它往往很大（双面文件能到 760MB 量级），搬进用户目录等于凭空多一份大副本。
        ///
        /// 所以默认 **false**：它留在工作区（<c>&lt;work&gt;\&lt;taskId&gt;\</c>），
        /// 成功且校验通过后随工作区一起清掉，失败/取消时留着供排查。
        /// 如果将来判定表/ResultFinalizer 能把它标注成"输入而非产物"（续解扫描据此跳过），
        /// 把这里改成 true 即可，其余代码不用动。
        /// </summary>
        private const bool MoveCarvedArtifactIntoProcessDirectory = false;

        /// <summary>
        /// 定稿布局规划：把暂存产物树描述成 <see cref="StagedEntry"/>，交给
        /// <see cref="ResultFinalizer.Plan"/>（契约 §3.1 判定表的**唯一实现处**），
        /// 再把它的话翻译成本类执行阶段用的形状。
        ///
        /// 分工要说清楚：
        /// · 本方法负责"**哪些是其余物**" —— 这一步只有跑过暂存阶段的人知道。
        ///   判据用后缀（分卷段 + 归档本体）：解压出来的归档就是**待续解的内层包**，
        ///   按流水线语义它属于其余物（一键处理下一轮会去解它，成功的话它的内容物自会落进内容物一层）；
        ///   判定失败/到轮数上限时它留在 <c>其余物</c> 里可回收，比混在内容物里强。
        ///   （ResultFinalizer 的注释建议"由调用方显式标"，这里就是那个调用方。）
        /// · <see cref="ResultFinalizer"/> 负责"摆成什么样" —— 终端单文件直接放、多文件套一层、
        ///   多重空目录提上来、单链塌缩，以及 D-2 的 <c>&lt;源目录&gt;\其余物\&lt;包基名&gt;\</c>。
        ///
        /// 纯函数：只读目录、只返回计划，一个字节都不动，所以能脱离管线被测。
        /// </summary>
        /// <param name="stageDirectory">暂存目录（入仓阶段的产物树）。</param>
        /// <param name="destinationDirectory">最终目录（落点由 <see cref="PathService.BuildOutputPath"/> 算）。</param>
        /// <param name="placementMode">落点模式；只影响其余物集中到哪（D-2 依赖它）。</param>
        /// <param name="archiveBaseName">
        /// 终端归档基名（"没有最外层文件夹名"时给那一层取名用）。传空则退回 destDir 自己的末段名。
        /// </param>
        /// <param name="terminalLayout">
        /// 终端落法（规格 §3.1 的可选项，来自设置项 <see cref="AppSettings.TerminalLayoutMode"/>）。
        ///
        /// ⚠ 这里**必须由调用方传**：本方法以前把 <see cref="TerminalLayoutMode.KeepLastFolder"/> 写死，
        /// 于是用户在设置里选"用压缩包名当最后一层"完全不生效 —— 界面上的选项与真实行为不一致，
        /// 是本项目明令禁止的那一类（"改了没用"的开关）。
        /// </param>
        internal static FinalLayoutPlan PlanFinalLayout(
            string stageDirectory,
            string destinationDirectory,
            OutputPlacementMode placementMode,
            string? archiveBaseName,
            TerminalLayoutMode terminalLayout = TerminalLayoutMode.KeepLastFolder)
        {
            if (string.IsNullOrWhiteSpace(stageDirectory) ||
                string.IsNullOrWhiteSpace(destinationDirectory) ||
                !Directory.Exists(stageDirectory))
            {
                return new FinalLayoutPlan();
            }

            string stageRoot = SafePathHelper.GetFullPathSafe(stageDirectory);
            var staged = new List<StagedEntry>();

            foreach (string file in Directory.EnumerateFiles(stageRoot, "*", SearchOption.AllDirectories))
            {
                long size = 0;

                try
                {
                    size = new FileInfo(file).Length;
                }
                catch
                {
                    // 量不出大小只影响"其余物总共多大"这个数字，不影响布局。
                }

                staged.Add(new StagedEntry
                {
                    RelativePath = Path.GetRelativePath(stageRoot, file),
                    Size = size,
                    IsProcessArtifact = IsProcessArtifactFile(file)
                });
            }

            // 目录条目也要给：判定表要认"自带一层文件夹""多重空目录嵌套""单链"这些形态，
            // 光有文件路径推不出"这一层是不是空的"。
            foreach (string directory in Directory.EnumerateDirectories(stageRoot, "*", SearchOption.AllDirectories))
            {
                staged.Add(new StagedEntry
                {
                    RelativePath = Path.GetRelativePath(stageRoot, directory),
                    IsDirectory = true
                });
            }

            if (staged.Count == 0)
            {
                return new FinalLayoutPlan();
            }

            FinalizePlan finalize = ResultFinalizer.Plan(
                staged,
                destinationDirectory,
                terminalLayout,
                archiveBaseName,
                contentRoot: null,
                stagingRoot: stageRoot,
                placementMode: placementMode);

            var processSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (PlannedMove move in finalize.ProcessArtifactMoves)
            {
                processSources.Add(move.From);
            }

            return new FinalLayoutPlan
            {
                Failed = finalize.Layout == FinalizeLayoutKind.Failed,
                FailureReason = finalize.FailureReason,
                Layout = finalize.Layout,
                Moves = finalize.Moves,
                PlannedContentCount = finalize.ContentMoves.Count,
                ProcessArtifactSources = processSources,
                ProcessArtifactDirectory = finalize.ProcessArtifactDirectory,
                ProcessArtifactTotalSize = finalize.ProcessArtifactTotalSize,
                ContentFileCount = finalize.ContentFileCount,
                Summary = finalize.Summary,
                Warnings = finalize.Warnings
            };
        }

        /// <summary>
        /// 这条路是不是"其余物"（契约 §3.2）：分卷段（<c>.001</c>/<c>.z01</c>/<c>.r00</c>/<c>.part1</c>）
        /// 与归档本体（<c>.7z</c>/<c>.zip</c>/<c>.rar</c>/…）。抠出来的中间 ZIP 也落在这一条里。
        /// </summary>
        internal static bool IsProcessArtifactFile(string filePath)
        {
            string extension = Path.GetExtension(filePath);

            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            return ExtensionHelper.IsVolumePartExtension(extension) ||
                   ExtensionHelper.IsKnownArchiveExtension(extension);
        }

        /// <summary>
        /// 执行定稿：按 <see cref="PlanFinalLayout"/> 的计划把暂存产物搬进最终目录。
        ///
        /// **只允许在后台线程上跑**（几千个 File.Move、跨盘还是拷贝）。
        /// 四条硬要求：
        /// ① 取消时把已经搬过去的**搬回来**（最终目录不许留半成品，契约 §6 第 2 条）；
        /// ② 同名绝不**默认**覆盖（AutoRename 改名，记进 <see cref="StageCommitResult.RenamedCount"/>）；
        /// ③ 单个文件搬不动只记一笔，不拖垮其余文件（与 <see cref="ExtractionWorkspace"/> 的搬运口径一致）；
        /// ④ 用户明确选了「覆盖」时才覆盖，而且必须走 <see cref="PathService.TryOverwriteLanding"/>
        ///    的两阶段（先挪到临时名 → 落位 → 再删），并逐个落点写日志留痕。
        /// </summary>
        /// <param name="conflictAction">同名冲突档（<see cref="ConflictActions"/> 的值）。</param>
        /// <param name="conflictDecision">
        /// 用户对同名冲突的答案（Ask 档；非 Ask 档为 null）。为 null 且档位是 Ask 时，
        /// <see cref="PathService.ResolveConflict"/> 会退回保守档（跳过）—— 绝不替用户决定成覆盖。
        /// </param>
        private StageCommitResult ExecuteFinalLayout(
            ArchiveTask task,
            string stageDirectory,
            string destinationDirectory,
            OutputPlacementMode placementMode,
            TerminalLayoutMode terminalLayout,
            string conflictAction,
            ConflictDecision? conflictDecision,
            CancellationToken cancellationToken)
        {
            var logEntries = new List<(string Level, string Message)>();

            if (string.IsNullOrWhiteSpace(stageDirectory) ||
                string.IsNullOrWhiteSpace(destinationDirectory) ||
                !Directory.Exists(stageDirectory))
            {
                return new StageCommitResult { Attempted = false };
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new StageCommitResult
                {
                    Attempted = true,
                    Cancelled = true,
                    Message = "定稿开始前已取消"
                };
            }

            FinalLayoutPlan plan;

            try
            {
                plan = PlanFinalLayoutForTask(
                    task,
                    stageDirectory,
                    destinationDirectory,
                    placementMode,
                    terminalLayout);
            }
            catch (Exception ex)
            {
                string readFailure = $"读取暂存目录失败：{ex.Message}";

                logEntries.Add(("ERROR", $"{task.FileName}：{readFailure}"));

                return new StageCommitResult
                {
                    Attempted = true,
                    FailedCount = 1,
                    Failures = new[] { readFailure },
                    Message = readFailure,
                    LogEntries = logEntries
                };
            }

            if (plan.Failed)
            {
                string planFailure = "定稿布局规划失败：" + plan.FailureReason;

                logEntries.Add(("ERROR", $"{task.FileName}：{planFailure}"));

                return new StageCommitResult
                {
                    Attempted = true,
                    FailedCount = 1,
                    Failures = new[] { planFailure },
                    Message = planFailure,
                    LogEntries = logEntries
                };
            }

            foreach (string warning in plan.Warnings)
            {
                logEntries.Add(("WARN", $"{task.FileName}：定稿提醒 —— {warning}"));
            }

            if (plan.Moves.Count == 0)
            {
                // 引擎什么都没写出来（空包）：没有可定稿的东西，也不该凭空造一个空目录给用户。
                logEntries.Add(("WARN", $"{task.FileName}：暂存目录里没有产物，没有需要定稿的内容。"));

                return new StageCommitResult
                {
                    Attempted = true,
                    Message = "暂存目录里没有产物",
                    LogEntries = logEntries
                };
            }

            bool destinationExisted = Directory.Exists(destinationDirectory);

            if (!SafePathHelper.EnsureDirectoryExists(destinationDirectory))
            {
                string createFailure = $"最终目录不存在且创建失败：{destinationDirectory}";

                logEntries.Add(("ERROR", $"{task.FileName}：{createFailure}"));

                return new StageCommitResult
                {
                    Attempted = true,
                    FailedCount = plan.Moves.Count,
                    Failures = new[] { createFailure },
                    Message = createFailure,
                    LogEntries = logEntries
                };
            }

            var moved = new List<(string From, string To)>();
            var failures = new List<string>();
            var overwritten = new List<string>();

            int movedContent = 0;
            int movedProcess = 0;
            int renamed = 0;
            int skippedContent = 0;
            int skippedProcess = 0;

            // 计划里 **内容物在前、其余物在后**：先让用户要的东西落位，再收拾中间件。
            foreach (PlannedMove move in plan.Moves)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    RollBackFinalLayout(moved, destinationDirectory, destinationExisted);

                    logEntries.Add(("WARN", $"{task.FileName}：定稿被取消，已把 {moved.Count} 项退回暂存目录。"));

                    return new StageCommitResult
                    {
                        Attempted = true,
                        Cancelled = true,
                        Message = "定稿被取消，产物已退回暂存目录",
                        LogEntries = logEntries
                    };
                }

                bool isProcessArtifact = plan.ProcessArtifactSources.Contains(move.From);

                try
                {
                    // 计划里的一条可能是**文件**，也可能是**整个目录**（判定表 2/3/4 会整棵搬）。
                    bool isDirectory = Directory.Exists(move.From) && !File.Exists(move.From);

                    if (!SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(move.To)))
                    {
                        failures.Add($"{Path.GetFileName(move.To)}（无法创建目标目录）");
                        continue;
                    }

                    string target = move.To;

                    // 覆盖档由 TryOverwriteLanding 自己把新条目搬过去（它要先腾位），这里不再搬第二次。
                    bool landed = false;

                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        /*
                         * 目标是**最终目录自己**时不许覆盖：那等于把刚落位的产物连同目录一起顶掉，
                         * 而且是用户完全没预期的一次删除。规划器现在不会给出这种条目
                         * （计划里永远是 destDir\子项），这里是最后一道保险。
                         */
                        bool isDestinationRoot = SafePathHelper.PathEquals(target, destinationDirectory);

                        ConflictResolution resolution = isDestinationRoot
                            ? new ConflictResolution(ConflictChoice.AutoRename, target, true, false)
                            : _pathService.ResolveConflict(
                                target,
                                isDirectory ? ConflictTargetKind.Directory : ConflictTargetKind.File,
                                conflictAction,
                                conflictDecision);

                        if (resolution.Choice == ConflictChoice.Skip)
                        {
                            // 「跳过」：这一项不动，已有文件一个字节都不改（新产物留在暂存目录）。
                            if (isProcessArtifact)
                            {
                                skippedProcess++;
                            }
                            else
                            {
                                skippedContent++;
                            }

                            logEntries.Add(("WARN", $"{task.FileName}：同名冲突，按你的选择跳过：{target}"));
                            continue;
                        }

                        if (resolution.Choice == ConflictChoice.Overwrite && !isDestinationRoot)
                        {
                            /*
                             * 「覆盖」= 两阶段落位（AGENTS.md §6 第 3 条）：
                             * 先把占位者挪到临时名 → 新条目落位 → 落位成功后才删掉那个临时名。
                             * **禁止"先 File.Delete 再 Move"**：中间任何失败都会让用户想保留的那一份
                             * 永久消失，而新条目还没到位。
                             */
                            if (!_pathService.TryOverwriteLanding(
                                    move.From,
                                    target,
                                    isDirectory,
                                    out string overwriteError,
                                    out string overwriteNote))
                            {
                                failures.Add($"{Path.GetFileName(move.From)}（{overwriteError}）");
                                continue;
                            }

                            // 真正覆盖前要留痕：哪个落点被顶掉了必须写进日志（AGENTS.md §6 第 3 条 / 用户要求）。
                            overwritten.Add(target);
                            landed = true;

                            logEntries.Add((
                                "WARN",
                                $"{task.FileName}：同名冲突，按你的选择覆盖：{target}（{overwriteNote}）"));
                        }
                        else
                        {
                            // 自动重命名：换成 名字(1)，**绝不覆盖**（AGENTS.md §6 第 3 条）。
                            string renamedTarget = resolution.TargetPath;

                            if (!string.Equals(renamedTarget, target, StringComparison.OrdinalIgnoreCase))
                            {
                                renamed++;
                            }

                            logEntries.Add(("INFO", $"{task.FileName}：同名冲突，改名落位（绝不覆盖）：{renamedTarget}"));

                            target = renamedTarget;
                        }
                    }

                    if (!landed)
                    {
                        if (isDirectory)
                        {
                            Directory.Move(move.From, target);
                        }
                        else
                        {
                            File.Move(move.From, target);
                        }
                    }

                    moved.Add((move.From, target));

                    if (isProcessArtifact)
                    {
                        movedProcess++;
                    }
                    else
                    {
                        movedContent++;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(move.From)}（{ex.Message}）");
                }
            }

            /*
             * 抠出来的内嵌归档：它是从源文件按偏移再生出来的派生数据，契约 §3.2 把它列为其余物。
             * 它**不在暂存区里**（在任务工作区根下，是解压的输入），所以不在上面的计划里，单独搬一次。
             * 为什么默认不搬：见 MoveCarvedArtifactIntoProcessDirectory 的说明。
             */
            if (MoveCarvedArtifactIntoProcessDirectory && task.EmbeddedArchiveOffset > 0)
            {
                string carvedPath = BuildEmbeddedArchivePath(task);

                if (File.Exists(carvedPath))
                {
                    try
                    {
                        string processRoot = string.IsNullOrWhiteSpace(plan.ProcessArtifactDirectory)
                            ? Path.Combine(destinationDirectory, ProcessArtifactDirectoryName)
                            : plan.ProcessArtifactDirectory;

                        string carvedTarget = Path.Combine(processRoot, Path.GetFileName(carvedPath));

                        SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(carvedTarget));

                        if (File.Exists(carvedTarget))
                        {
                            carvedTarget = SafePathHelper.AutoRenameFilePath(carvedTarget);
                            renamed++;
                        }

                        File.Move(carvedPath, carvedTarget);

                        movedProcess++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{Path.GetFileName(carvedPath)}（抠出的中间归档：{ex.Message}）");
                    }
                }
            }

            string processDirectory = string.IsNullOrWhiteSpace(plan.ProcessArtifactDirectory)
                ? Path.Combine(destinationDirectory, ProcessArtifactDirectoryName)
                : plan.ProcessArtifactDirectory;

            string summary =
                $"内容物 {plan.ContentFileCount} 个文件 → {destinationDirectory}" +
                (movedProcess > 0
                    ? $"；其余物 {movedProcess} 项（{plan.ProcessArtifactTotalSize} 字节）→ {processDirectory}"
                    : string.Empty) +
                (renamed > 0 ? $"；{renamed} 项同名，已改名未覆盖" : string.Empty) +
                (overwritten.Count > 0 ? $"；{overwritten.Count} 项同名，按你的选择覆盖（已写日志）" : string.Empty) +
                (skippedContent + skippedProcess > 0 ? $"；{skippedContent + skippedProcess} 项同名，按你的选择跳过" : string.Empty) +
                (failures.Count > 0 ? $"；{failures.Count} 项没能搬运" : string.Empty);

            logEntries.Add((failures.Count == 0 ? "INFO" : "WARN", $"{task.FileName}：定稿完成 —— {summary}"));

            // 覆盖留痕：被顶掉的落点单独再写一条，用户事后追查"我原来那份去哪了"时有据可依。
            foreach (string overwrittenPath in overwritten)
            {
                logEntries.Add(("WARN", $"{task.FileName}：已覆盖（原文件按两阶段落位删除）：{overwrittenPath}"));
            }

            return new StageCommitResult
            {
                Attempted = true,
                PlannedContentCount = plan.PlannedContentCount,
                MovedContentCount = movedContent,
                MovedProcessCount = movedProcess,
                ContentFileCount = plan.ContentFileCount,
                ProcessArtifactDirectory = processDirectory,
                ProcessArtifactBytes = plan.ProcessArtifactTotalSize,
                RenamedCount = renamed,
                SkippedCount = skippedContent + skippedProcess,
                SkippedContentCount = skippedContent,
                OverwrittenCount = overwritten.Count,
                OverwrittenPaths = overwritten,
                FailedCount = failures.Count,
                Failures = failures,
                Message = summary,
                LogEntries = logEntries
            };
        }

        /// <summary>
        /// 定稿的回滚：把已经搬过去的条目按**相反顺序**搬回暂存区原路径。
        ///
        /// 为什么必须回滚（契约 §6 第 2 条"定稿前 destDir 不得出现半成品"）：
        /// 用户按下「取消当前」时，搬运可能已经做了一半 —— 只把最终目录里那一半留下，
        /// 用户看到的就是一个"解压到一半"的目录，正是这次改造要根治的观感问题。
        /// 回滚失败（文件被别的程序锁住等）只吞掉：那是更小的问题，而且日志里已经有取消记录。
        ///
        /// 条目可能是**整个目录**（判定表 2/3/4 会整棵搬），所以这里也要按类型分开搬回去。
        /// </summary>
        private static void RollBackFinalLayout(
            List<(string From, string To)> moved,
            string destinationDirectory,
            bool destinationExisted)
        {
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                try
                {
                    SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(moved[i].From));

                    if (Directory.Exists(moved[i].To) && !File.Exists(moved[i].To))
                    {
                        Directory.Move(moved[i].To, moved[i].From);
                    }
                    else
                    {
                        File.Move(moved[i].To, moved[i].From);
                    }
                }
                catch
                {
                    // 回滚不掉的单个条目不影响"已经尽量退回"这个结论。
                }
            }

            if (destinationExisted)
            {
                // 目录本来就在（续解内层包时它装着上一轮的内容物），绝不能删。
                return;
            }

            TryDeleteEmptyDirectoryTree(destinationDirectory);
        }

        /// <summary>由深到浅删掉空目录（只删空的；非空一律不碰）。定稿回滚后收拾自己造出来的目录壳。</summary>
        private static void TryDeleteEmptyDirectoryTree(string root)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    return;
                }

                string[] directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);

                foreach (string directory in directories.OrderByDescending(d => d.Length))
                {
                    try
                    {
                        if (Directory.GetFileSystemEntries(directory).Length == 0)
                        {
                            Directory.Delete(directory, recursive: false);
                        }
                    }
                    catch
                    {
                        // 删不掉就留着，不影响结论。
                    }
                }

                if (Directory.GetFileSystemEntries(root).Length == 0)
                {
                    Directory.Delete(root, recursive: false);
                }
            }
            catch
            {
                // 见方法注释：收拾目录壳是尽力而为。
            }
        }

        /// <summary>
        /// 给递归层提供密码候选（只给值，不给来源说明）。
        /// 顺序与单层解压完全一致 —— 递归的内层包同样是"用户的包"，不该用另一套规则。
        ///
        /// 这里就截到每层上限（同 <see cref="MaxPasswordAttemptsPerLayer"/> 这个设置项）：
        /// 递归里每个候选同样是一次完整解压尝试，让几百个候选排着队进去
        /// 等于把"一键处理"变成没人看得懂的长时间等待（AGENTS.md §9.2）。
        /// </summary>
        private IReadOnlyList<string> BuildRecursionPasswordCandidates(string archivePath)
        {
            var probe = new ArchiveTask(archivePath);

            return _passwordService
                .GetPasswordCandidates(
                    probe,
                    Settings.UseGlobalPasswordForAllTasks ? GlobalPassword : string.Empty,
                    _passwordService.Passwords,
                    Settings.TryEmptyPasswordFirst,
                    Settings.EnableSidecarPassword)
                .Take(MaxPasswordAttemptsPerLayer)
                .Select(p => p.Value ?? string.Empty)
                .ToList();
        }

        /// <summary>
        /// 递归解压一个任务，并把结果落到任务状态上。
        /// 多分支时**必须问用户**（不变量 8），问完只处理用户确认的那批候选。
        /// </summary>
        /// <param name="engineArchivePath">
        /// 真正交给引擎的归档路径。内嵌归档时它是工作区里抠出来的那个文件（不是 <c>task.CurrentPath</c>）。
        /// </param>
        /// <param name="stageDirectory">
        /// 暂存目录（入仓阶段）。递归核心把叶子层产物发布到**这里**，不直接写最终目录 ——
        /// 定稿那一步才把它们搬进 <see cref="ArchiveTask.OutputPath"/>。
        /// </param>
        private async Task RunRecursiveAsync(
            ArchiveTask task,
            string engineArchivePath,
            string stageDirectory,
            CancellationToken cancellationToken)
        {
            RecursionMode mode = string.Equals(Settings.RecursionMode, "AllBranches", StringComparison.OrdinalIgnoreCase)
                ? RecursionMode.AllBranches
                : RecursionMode.SingleChain;

            // 上限（层数 / 每层密码尝试次数）当场从设置里取，并交给**本次**的递归核心：
            // 构造时固定一份的话，用户改完设置不重启就不生效（见字段上的说明）。
            RecursionLimits limits = BuildRecursionLimits();
            RecursiveExtractor recursiveExtractor = CreateRecursiveExtractor();

            task.Status = StatusText.Extracting;
            task.ProgressText = StatusText.ProgressProcessing;
            task.LastUpdatedTime = DateTime.Now;

            /*
             * 递归核心（RecursiveExtractor）从 task.CurrentPath 取"第 0 层要解哪个归档"。
             * 内嵌归档真正的归档是抠出来的那个文件，所以这里给它一个只带正确路径的**探针任务**，
             * 而不是把 task.CurrentPath 临时改掉 —— 源文件路径是改名 / 清理 / 统计的依据，
             * 任何"改了再改回来"的写法都会在这些地方留下一个窗口期（异常时更是永远改不回来）。
             * 递归结论仍然应用在真正的 task 上（ApplyRecursionResult(task, result)）。
             */
            ArchiveTask recursionTask = string.Equals(
                    engineArchivePath,
                    task.CurrentPath,
                    StringComparison.OrdinalIgnoreCase)
                ? task
                : new ArchiveTask(engineArchivePath);

            AppendLog("INFO", $"{task.FileName}：开始递归解压（模式 {mode}，最大 {limits.MaxDepth} 层）。");

            /*
             * 递归内层的进度（2026-09-22 补）：复用**同一个** TaskProgressSink，
             * 于是界面上的百分比 / 当前条目、详情窗口的耗时、以及"跨 10% 写一行"的日志
             * 与单层路径完全是同一套行为 —— 递归核心只负责把 sink 转发给引擎。
             *
             * 卡住提示同理：一层套一层时最容易出现"几十秒没有任何输出"，
             * 那一段过去在界面上就是死等（整条递归连一条 WARN 都不会有）。
             */
            TaskProgressSink recursionProgressSink = new(this, task);

            RecursionResult result = await recursiveExtractor.ExtractAsync(
                recursionTask,
                stageDirectory,
                mode,
                previousDecision: null,
                cancellationToken,
                recursionProgressSink,
                notice => OnEngineStalled(task, notice));

            if (result.StopReason == RecursionStopReason.NeedsDecision && result.Decision != null)
            {
                bool expandAll = await ShowConfirmOnUiThreadAsync(
                    result.Decision.Prompt + Environment.NewLine + Environment.NewLine +
                    "选“确定”：把这些内层归档也解开。选“取消”：只保留当前这一层的结果。");

                if (expandAll)
                {
                    // 同一个实例接着跑（上限不变）：续跑用的是用户刚确认的那批候选，不是重新扫一遍。
                    result = await recursiveExtractor.ExtractAsync(
                        recursionTask,
                        stageDirectory,
                        mode,
                        result.Decision,
                        cancellationToken,
                        recursionProgressSink,
                        notice => OnEngineStalled(task, notice));
                }
                else
                {
                    /*
                     * 用户只想保留当前这一层。
                     *
                     * 这里**不能**就这样返回 NeedsDecision：那会让用户以为"东西已经解到输出目录了"，
                     * 而按不变量 12，需要决定时产物还留在工作区里 —— 文案与实现不符等于骗人。
                     *
                     * 做法：把已经解好的第 0 层产物从递归工作区搬进**暂存目录**（它照样是入仓阶段的一部分），
                     * 并把它当成 Completed 继续走"校验 → 定稿 → 归集 → 清理"。
                     * 不重新解压一遍：大包重解代价太大，而产物本来就在工作区里。
                     */
                    string? layerZeroOutput = result.Layers
                        .FirstOrDefault(l => l.Depth == 0 && l.Success)?.OutputPath;

                    if (!string.IsNullOrWhiteSpace(layerZeroOutput) && Directory.Exists(layerZeroOutput))
                    {
                        /*
                         * 搬运是同步磁盘活（递归枚举 + File.Move），同样必须离开 UI 线程 ——
                         * 理由与收尾那一段完全相同（见 PostProcessSuccessAsync 的线程规则）。
                         */
                        int moved = await Task.Run(
                            () => MoveDirectoryContent(layerZeroOutput, stageDirectory),
                            cancellationToken);

                        result = new RecursionResult
                        {
                            StopReason = RecursionStopReason.Completed,
                            Completed = true,
                            PartiallyCompleted = false,
                            Layers = result.Layers,
                            FinalOutputPath = stageDirectory,
                            Summary = $"按你的选择只解开了当前这一层，已取回 {moved} 个文件（定稿后进输出目录）。"
                        };

                        /*
                         * 产物已经取回暂存目录 → 那份递归工作区（含 report.json）从此是纯垃圾，
                         * 留着还会在下次启动时被算进"未完成的工作区"报告。
                         *
                         * 为什么得在这里补一刀：递归核心是以 NeedsDecision 收的尾，按规则**没有**清工作区
                         * （"等你决定"属于部分完成）；结论是在**这里**被改写成 Completed 的 ——
                         * 谁改写结论，谁负责把"这次算成功"的收尾补上（递归成功那一支的清理在
                         * RecursiveExtractor.FinalizeRun 里，走的是同一个 Cleanup()）。
                         *
                         * ⚠ 只有"一个文件都没漏下"才清：MoveDirectoryContent 是尽力而为
                         * （单个文件搬不动就跳过、只记进返回的文件数），漏下的那些还在工作区里，
                         * 删掉它们等于丢用户的产物（不变量 1 的精神）。
                         */
                        if (HasNoFiles(layerZeroOutput))
                        {
                            recursiveExtractor.TryCleanupCurrentWorkspace(
                                task.FileName,
                                "用户选择只保留当前这一层，第 0 层产物已取回暂存目录");
                        }
                    }
                    else
                    {
                        // 第 0 层本身就没成功：保持原结论（部分完成），让它如实报告。
                        AppendLog("WARN", $"{task.FileName}：当前这一层没有可用产物，保留原结论。");
                    }
                }
            }

            ApplyRecursionResult(task, result);
        }

        /// <summary>
        /// 把递归结论翻译成任务状态。
        /// 规则：完成 = 成功；部分完成 = **绝不显示成功**；需要用户决定 = 等用户；
        /// 其余（上限 / 密码 / 损坏 / 取消）都要说清停在哪一层、为什么。
        /// </summary>
        private void ApplyRecursionResult(ArchiveTask task, RecursionResult result)
        {
            foreach (RecursionLayerReport layer in result.Layers)
            {
                AppendLog(
                    layer.Success ? "INFO" : "WARN",
                    $"  ├ 第 {layer.Depth} 层：{Path.GetFileName(layer.ArchivePath)} → {layer.Status}" +
                    (string.IsNullOrWhiteSpace(layer.Message) ? string.Empty : $"，{layer.Message}"));
            }

            task.ErrorMessage = result.Summary;

            switch (result.StopReason)
            {
                case RecursionStopReason.Completed:
                    task.Status = StatusText.ExtractSuccess;
                    task.ProgressText = StatusText.ProgressCompleted;
                    break;

                case RecursionStopReason.NeedsDecision:
                    task.Status = StatusText.PartiallyCompleted;
                    task.ProgressText = StatusText.ProgressCompleted;
                    break;

                case RecursionStopReason.UserCancelled:
                    task.Status = StatusText.Cancelled;
                    task.ProgressText = StatusText.Cancelled;
                    break;

                /*
                 * 源文件已变化（不变量 11）：**状态与原因都已经落好了**（拦下那一刻由
                 * StopIfSourceChanged 一次写全），这里只补一句"产物在哪"就收尾。
                 *
                 * 单独一支的理由：它落到下面 default 那一支会被改写成「部分完成」——
                 * 那是**另一个错误结论**：这个任务一个字节都没解（引擎压根没被调用），
                 * 说成"部分完成"会让用户去暂存目录里找根本不存在的产物；
                 * 而 ErrorMessage 若被 result.Summary 覆盖，用户就看不到**哪一个文件、哪一项变了**
                 * 这条唯一能行动的信息。两者都必须原样保住。
                 */
                case RecursionStopReason.SourceChanged:
                    task.Operation = StatusText.OpWaiting;
                    task.ProgressText = StatusText.ProgressFailed;
                    task.EndTime = DateTime.Now;
                    task.LastUpdatedTime = DateTime.Now;
                    task.ClearProgress();
                    task.UpdateElapsedText();

                    AppendLog("ERROR", $"{task.FileName}：{result.Summary}");
                    AppendLog("ERROR", $"{task.FileName}：{task.ErrorMessage}");
                    return;

                default:
                    task.Status = StatusText.PartiallyCompleted;
                    task.ProgressText = StatusText.ProgressFailed;
                    break;
            }

            task.LastUpdatedTime = DateTime.Now;

            AppendLog(result.Completed ? "INFO" : "WARN", $"{task.FileName}：{result.Summary}");

            /*
             * task.OutputPath **不跟着递归结论走**。
             *
             * 它必须始终指向**最终目录**（一键处理的续解、界面"输出目录"列、清理源包都读它），
             * 而现在递归的产物先落在**暂存目录**里（调用方传的就是 stage），要等定稿那一步才搬出去。
             * 旧写法 `task.OutputPath = result.FinalOutputPath` 在这里会把任务指向暂存区：
             * 用户点"打开输出目录"会看到工作区里的中间件，续解也会去暂存区里找内层包。
             */
            if (!result.Completed)
            {
                // 没走完（上限 / 密码 / 需要决定）：产物留在暂存区，如实说清它在哪。
                AppendLog(
                    "WARN",
                    $"{task.FileName}：这次没有走完，产物仍在暂存目录，未搬进输出目录：{result.FinalOutputPath}");
            }
        }

        /// <summary>
        /// 把目录内容（保持子目录结构）移动到目标目录，同名自动改名**绝不覆盖**，返回移动的文件数。
        /// 只在"用户选择只保留当前一层"这条路径上用。
        /// </summary>
        private static int MoveDirectoryContent(string sourceDirectory, string targetDirectory)
        {
            int moved = 0;

            SafePathHelper.EnsureDirectoryExists(targetDirectory);

            foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    string relative = Path.GetRelativePath(sourceDirectory, file);
                    string destination = Path.Combine(targetDirectory, relative);

                    SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(destination) ?? targetDirectory);

                    if (File.Exists(destination))
                    {
                        destination = SafePathHelper.AutoRenameFilePath(destination);
                    }

                    File.Move(file, destination);
                    moved++;
                }
                catch
                {
                    // 单个文件搬不动不该让整件事失败：日志里已经能看出解压本身是成功的。
                }
            }

            return moved;
        }

        /// <summary>
        /// 目录里（含各级子目录）**一个文件都没有**时返回 true；目录不存在或读不了返回 false。
        ///
        /// 只给"用户选择只保留当前这一层"那条路用：它是"现在能不能安全清掉这份工作区"的判据 ——
        /// 读不了就按"还有东西"处理，宁可不清理，也不能把没搬走的产物删掉。
        /// </summary>
        private static bool HasNoFiles(string? directory)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(directory) &&
                       Directory.Exists(directory) &&
                       Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 在 **UI 线程** 上弹确认框。
        ///
        /// 为什么必须显式调度（这是实测出来的卡死原因）：
        /// 引擎调用与抠出分别用了 ConfigureAwait(false) 和 Task.Run，管线拿到递归结论时
        /// **已经不在 UI 线程上**。此时直接 MessageBox.Show 会创建出一个没人泵消息的窗口 ——
        /// 用户看到的现象是"点了按钮之后什么都动不了、没有弹窗、连 X 都点不掉"，
        /// 而进程既不占 CPU、也没有子进程、磁盘也没在忙，看着像死锁。
        /// 命令行直接调 RecursiveExtractor 会秒回 NeedsDecision，卡的就是这一步弹窗。
        /// </summary>
        private Task<bool> ShowConfirmOnUiThreadAsync(string message)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null || dispatcher.CheckAccess())
            {
                return Task.FromResult(_dialogService.ShowConfirm(message));
            }

            return dispatcher.InvokeAsync(() => _dialogService.ShowConfirm(message)).Task;
        }

        /// <summary>
        /// 在 UI 线程上弹警告（**异步**，不阻塞调用线程）。
        ///
        /// 与 <see cref="ShowConfirmOnUiThreadAsync"/> 同一套做法，取代原先任务循环里的
        /// 同步 <c>Application.Current.Dispatcher.Invoke</c>：
        /// ① 同步 Invoke 会让调用线程一直等模态框关掉（将来从后台线程调用时就是死等）；
        /// ② <c>Application.Current</c> 为 null 时（单元测试 / 无界面宿主）它会直接 NRE。
        /// </summary>
        private Task ShowWarningOnUiThreadAsync(string message)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null)
            {
                // 没有 WPF 应用：只留日志（调用方已经写过），绝不在这里弹模态框 —— 那会阻塞调用方且没人点得掉。
                return Task.CompletedTask;
            }

            if (dispatcher.CheckAccess())
            {
                _dialogService.ShowWarning(message);
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(() => _dialogService.ShowWarning(message)).Task;
        }

        // ================================================================ 进度可见（D5）/ 长时间无响应

        /// <summary>
        /// 把一段动作投递到 UI 线程执行（**异步、不阻塞、无界面宿主也不抛**）。
        ///
        /// <para>
        /// 三条纪律（每一条都对应一次踩过的坑）：
        /// </para>
        /// <list type="number">
        /// <item><description>用 <c>BeginInvoke</c> 而不是同步 <c>Dispatcher.Invoke</c>：
        /// 进度回调是从**读进程输出那条循环**里发出来的，同步 Invoke 会让"读输出"等"UI 处理完"，
        /// 一旦 UI 线程正忙（正是本项目卡死过的那个场景）就会互相拖住。</description></item>
        /// <item><description><c>Application.Current</c> 为 null（单元测试 / 控制台宿主）时就地执行：
        /// 既不抛异常也不死等，而且测试仍然能观察到结果。</description></item>
        /// <item><description>动作本身抛异常一律吞掉：进度显示出问题绝不允许影响解压结论。</description></item>
        /// </list>
        /// </summary>
        private static void PostToUiThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null || dispatcher.CheckAccess())
            {
                TryRunSafely(action);
                return;
            }

            try
            {
                dispatcher.BeginInvoke(action);
            }
            catch
            {
                // 调度器正在关闭（关窗口时）：丢弃这一次进度，绝不影响引擎调用。
            }
        }

        private static void TryRunSafely(Action action)
        {
            try
            {
                action();
            }
            catch
            {
                // 界面侧的进度显示失败与解压结论无关。
            }
        }

        /// <summary>
        /// 跨 10% 档位的进度日志行数上限（10 个档位 = 最多 10 行）。
        ///
        /// 为什么必须限：引擎的进度是**每 250ms 一条**，一次 10 分钟的解压有 2400 条 ——
        /// 每条都写日志会把日志彻底淹掉，用户反而看不到真正重要的那几行（密码、失败原因）。
        /// </summary>
        private const int ProgressLogDecileStep = 10;

        /// <summary>进度日志里当前条目名最多留几个字符（再长就截断，免得一行日志比屏幕还宽）。</summary>
        private const int ProgressLogEntryMaxLength = 60;

        /// <summary>
        /// 一个任务的进度接收端。
        ///
        /// <para>
        /// 它接的是引擎层**节流之后**的进度（最多 250ms 一次，见 <c>ArchiveProgressReporter</c>），
        /// 所以这里不需要再去重；它只负责把结论落到任务上、按档位写日志。
        /// </para>
        ///
        /// <para>
        /// 线程：<see cref="Report"/> 可能来自读管道的线程，实际落地一律经
        /// <see cref="PostToUiThread"/> 回到 UI 线程（无界面宿主时就地执行）。
        /// </para>
        /// </summary>
        private sealed class TaskProgressSink : IProgress<ArchiveProgress>
        {
            private readonly ExtractionCoordinator _owner;
            private int _lastLoggedDecile = -1;

            public TaskProgressSink(ExtractionCoordinator owner, ArchiveTask task)
            {
                _owner = owner;
                Task = task;
            }

            public ArchiveTask Task { get; }

            public void Report(ArchiveProgress? value)
            {
                if (value == null)
                {
                    return;
                }

                PostToUiThread(() => Apply(value));
            }

            private void Apply(ArchiveProgress progress)
            {
                /*
                 * 任务已经收尾时，迟到的进度一律不落到界面上。
                 *
                 * 引擎层已经把"进程退出后不再回调"做到了（reporter.Complete()），这里是第二道：
                 * 批量解压里上一个任务的最后一帧进度完全可能在它标成"解压成功"之后才排到 UI 队列，
                 * 那一帧如果照写，用户就会看到"解压成功 87%"（不变量 6：收尾后不许再显示进度）。
                 */
                if (Task.EndTime != null)
                {
                    return;
                }

                Task.ApplyProgress(progress.Percent, progress.CurrentEntry);

                // 详情窗口的「耗时」也跟着走：不然任务在跑的时候那一格一直停在 00:00:00。
                Task.UpdateElapsedText();

                if (progress.Percent < 0)
                {
                    // "只知道在动"（扫描阶段）：不写档位日志，免得开头连写好几行没信息量的东西。
                    return;
                }

                int decile = progress.Percent / ProgressLogDecileStep;

                /*
                 * 进度**回退**（或从"未知"回到具体值）＝ 新的一轮：换一个密码候选重试、或者递归解到了下一层。
                 * 档位计数必须跟着重来，否则后面每一轮都因为"decile 没超过上一轮"而一行日志都写不出来
                 * （实测形态：递归第 2 层开始日志里再也没有进度行，看起来像卡住了）。
                 */
                if (decile < _lastLoggedDecile)
                {
                    _lastLoggedDecile = -1;
                }

                if (decile <= _lastLoggedDecile)
                {
                    return;
                }

                _lastLoggedDecile = decile;

                string entry = progress.CurrentEntry ?? string.Empty;

                if (entry.Length > ProgressLogEntryMaxLength)
                {
                    entry = entry[..ProgressLogEntryMaxLength] + "…";
                }

                _owner.AppendLog(
                    "INFO",
                    string.IsNullOrWhiteSpace(entry)
                        ? $"{Task.FileName}：进度 {progress.Percent}%"
                        : $"{Task.FileName}：进度 {progress.Percent}%（当前：{entry}）");
            }
        }

        /// <summary>
        /// 造一个带进度 / 卡住提示的引擎请求（本次改动的接线点）。
        ///
        /// 阈值用引擎层的默认值（90 秒）：它**不是**设置项 —— 这一条只提示、不阻断，
        /// 不需要用户去调；用户真正要做的动作是「取消当前」，界面上已经有了。
        /// </summary>
        private ArchiveRequest BuildTrackedRequest(
            string archivePath,
            string password,
            string? outputPath,
            TaskProgressSink progressSink)
        {
            return new ArchiveRequest
            {
                ArchivePath = archivePath,
                Password = password,
                OutputPath = outputPath,
                Progress = progressSink,
                Stalled = notice => OnEngineStalled(progressSink.Task, notice),
                StallThreshold = EngineOutputActivityMonitor.DefaultStallThreshold
            };
        }

        /// <summary>
        /// 引擎超过阈值没有任何输出时的处理：**写一条 WARN + 在任务上挂提示，绝不杀进程**。
        ///
        /// <para>
        /// 为什么只提示不处理：用户点「取消当前」才是唯一的中止语义（不变量 9）。
        /// 自动杀进程会把"慢"误判成"死"，而真实场景里几十秒不输出（固实块、慢盘、杀毒软件扫描）
        /// 完全正常 —— 那正是这个功能存在的理由，不能反过来用它去打断正常任务。
        /// </para>
        /// </summary>
        private void OnEngineStalled(ArchiveTask task, ArchiveStallNotice notice)
        {
            if (notice == null)
            {
                return;
            }

            PostToUiThread(() =>
            {
                if (task.EndTime != null)
                {
                    // 任务已经收尾，这条提示已经过时（迟到的看门狗巡查）。
                    return;
                }

                int idleSeconds = (int)Math.Round(notice.Idle.TotalSeconds);
                int thresholdSeconds = (int)Math.Round(notice.Threshold.TotalSeconds);

                task.ResponsivenessHint =
                    $"{StatusText.LongTimeNoResponse}：已 {idleSeconds} 秒没有任何引擎输出，" +
                    "进程仍在运行；需要中止请点「取消当前」。";

                // WARN 只写一次（引擎层对同一段沉默只报一次），所以不会刷屏。
                AppendLog(
                    "WARN",
                    $"{task.FileName}：{StatusText.LongTimeNoResponse} —— 已 {idleSeconds} 秒没有任何引擎输出" +
                    $"（阈值 {thresholdSeconds} 秒），进程仍在运行。程序不会自动结束它；需要中止请点「取消当前」。");
            });
        }

        // ================================================================ 同名冲突（ConflictAction = Ask）

        /// <summary>同名冲突处理档（唯一来源；空 / 非法回落 AutoRename，见 <see cref="ConflictActions"/>）。</summary>
        private string ConflictActionValue => ConflictActions.Normalize(Settings.ConflictAction);

        /// <summary>预检里最多列几条冲突给用户看（再多就让他去看日志，别弹一个占满屏幕的框）。</summary>
        private const int MaxConflictSamplesInDialog = 5;

        /// <summary>定稿前的冲突预检结论（Ask 档要在动最终目录之前先问完）。</summary>
        private sealed class ConflictPrecheck
        {
            /// <summary>计划里"落点已经被占着"的条目总数。</summary>
            public int TotalCount { get; set; }

            /// <summary>其中属于**内容物**的条数（其余物撞名不算用户的内容被顶掉）。</summary>
            public int ContentCount { get; set; }

            /// <summary>给用户看的样例（最多 <see cref="MaxConflictSamplesInDialog"/> 条）。</summary>
            public List<(string Path, ConflictTargetKind Kind)> Samples { get; } = new();
        }

        /// <summary>本批开始时把冲突记账清零（上一批的"全部 X"绝不许影响这一批）。</summary>
        private void ResetBatchConflictState()
        {
            lock (_conflictDecisionLock)
            {
                _batchConflictDecision = null;
                _taskConflictDecision = null;
                _conflictPromptUnavailable = false;
                _conflictPromptCount = 0;
            }
        }

        /// <summary>每个任务开始时清掉"这一个"的记账（批量档位不受影响）。</summary>
        private void ResetTaskConflictState()
        {
            lock (_conflictDecisionLock)
            {
                _taskConflictDecision = null;
            }
        }

        /// <summary>本批 / 本任务已经定下的答案（"全部 X"优先于"这一个"）；没有就是 null。</summary>
        private ConflictDecision? GetEffectiveConflictDecision()
        {
            lock (_conflictDecisionLock)
            {
                return _batchConflictDecision ?? _taskConflictDecision;
            }
        }

        private bool IsConflictPromptUnavailable
        {
            get
            {
                lock (_conflictDecisionLock)
                {
                    return _conflictPromptUnavailable;
                }
            }
        }

        /// <summary>
        /// 问一次同名冲突并记账（**本批最多真正弹一次**）。
        ///
        /// <para>
        /// 返回值 <c>null</c> = 没问到答案（无界面宿主 / 等待超时）：调用方按保守档处理，**绝不覆盖**。
        /// 选择「取消本批」时本方法直接抛出取消，由上层把任务落成「已取消」（不变量 6）。
        /// </para>
        /// <para>
        /// 只允许在 UI 线程的上下文里调用（管线的那条 <c>await</c> 续体就在 UI 上下文）：
        /// 弹框本身走 <see cref="DialogService.ShowConflictDecisionAsync"/>，它自己负责
        /// "无界面宿主不弹窗、不阻塞"以及"后台线程用 InvokeAsync 而不是同步 Invoke"。
        /// </para>
        /// </summary>
        private async Task<ConflictDecision?> AskConflictAsync(
            ArchiveTask task,
            string message,
            string detail,
            CancellationToken cancellationToken)
        {
            lock (_conflictDecisionLock)
            {
                _conflictPromptCount++;
            }

            ConflictDecision? decision = await _dialogService.ShowConflictDecisionAsync(
                new DialogService.ConflictPrompt
                {
                    Message = message,
                    Subtitle = "同名冲突在本批只会问你这一次；勾上「对后面所有同名冲突都照此办理」就不会再打断你。",
                    Detail = detail,
                    Destructive = true
                },
                cancellationToken);

            /*
             * 询问期间被取消（用户点了「取消当前」）：不覆盖、不落位，把取消照原样抛上去，
             * 由 ProcessExtractTaskAsync 落成「已取消」—— 询问期间点了取消却报成功是最不能接受的。
             */
            cancellationToken.ThrowIfCancellationRequested();

            if (decision == null)
            {
                /*
                 * 问不到答案：**绝不代用户拍板成「覆盖」**（不变量 3：默认不得覆盖）。
                 * 记下"问不了"，后面的冲突不再尝试弹窗（否则一次批量会刷满降级日志）。
                 */
                lock (_conflictDecisionLock)
                {
                    _conflictPromptUnavailable = true;
                }

                AppendLog(
                    "WARN",
                    $"{task.FileName}：同名冲突问不到答案（当前宿主没有界面或等待超时），已按保守档继续 —— " +
                    "同名条目自动重命名落位，已有文件一个字节都不动。");

                return null;
            }

            ConflictDecision answered = decision.Value;

            if (answered.IsCancel)
            {
                // 「取消本批」= 既有的两套取消语义一起用：停止后续（不再启动新任务）+ 取消当前（正在跑的这个）。
                AppendLog("WARN", $"{task.FileName}：你在同名冲突询问里选择了「取消本批」，停止后续并取消当前任务。");

                StopAfterCurrent();
                CancelCurrentTask();

                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException();
            }

            lock (_conflictDecisionLock)
            {
                if (answered.ApplyToAll)
                {
                    _batchConflictDecision = answered;
                }
                else
                {
                    _taskConflictDecision = answered;
                }
            }

            AppendLog("INFO", $"{task.FileName}：同名冲突 —— 你的选择：{answered.Describe()}");

            if (answered.ApplyToAll)
            {
                AppendLog("INFO", "本批后续的同名冲突一律照此办理，不再询问。");
            }

            return answered;
        }

        /// <summary>
        /// 解析一次同名冲突：按档位（或用户的答案）给出"怎么办 + 落到哪"。
        ///
        /// Ask 档在这里才真的去问 —— 第一次遇到"目标已存在"时聚合问一次；
        /// 用户选了"全部 X"之后本批不再问，选了"这一个"则本任务内不再问（一次询问管一个任务）。
        /// 非 Ask 档一个字都不问，直接由档位推出结论（默认 AutoRename：绝不覆盖）。
        /// 落点的算法只有一处实现（<see cref="PathService.ResolveConflict"/>）。
        /// </summary>
        private async Task<ConflictResolution> ResolveConflictAsync(
            ArchiveTask task,
            ConflictTargetKind kind,
            string targetPath,
            string message,
            string detail,
            CancellationToken cancellationToken)
        {
            string action = ConflictActionValue;
            ConflictDecision? decision = GetEffectiveConflictDecision();

            if (ConflictActions.IsAsk(action) && decision == null && !IsConflictPromptUnavailable)
            {
                decision = await AskConflictAsync(task, message, detail, cancellationToken);
            }

            if (ConflictActions.IsAsk(action))
            {
                // decision 仍可能为 null —— 那就是"问不到"，走保守档（自动重命名，绝不覆盖）。
                return _pathService.ResolveConflict(
                    targetPath,
                    kind,
                    action,
                    decision ?? ConflictDecision.Conservative);
            }

            return _pathService.ResolveConflict(targetPath, kind, action);
        }

        /// <summary>把预检结论拼成给用户看的冲突清单（等宽小字那一栏）。</summary>
        private static string BuildConflictDetail(ConflictPrecheck precheck)
        {
            if (precheck.Samples.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();

            foreach ((string path, ConflictTargetKind kind) in precheck.Samples)
            {
                builder.Append(kind == ConflictTargetKind.Directory ? "［目录］" : "［文件］");
                builder.AppendLine(path);
            }

            if (precheck.TotalCount > precheck.Samples.Count)
            {
                builder.Append($"… 另有 {precheck.TotalCount - precheck.Samples.Count} 项同名（完整清单见日志）");
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// 定稿冲突预检：**只读**地算出"计划里哪些落点已经被占着"。
        ///
        /// 为什么要先算一遍：Ask 档必须**在动最终目录之前**拿到答案（也就是"暂停该任务"），
        /// 而落位那一刻已经在后台搬运的中途，边搬边问会让最终目录进退两难
        /// （一部分已经落位、剩下的在等一个还没出现的答案）。
        /// 本方法只读目录、只判存在，一个字节都不写；**只允许在后台线程上跑**。
        /// </summary>
        private ConflictPrecheck PrecheckFinalLayoutConflicts(
            ArchiveTask task,
            string stageDirectory,
            string destinationDirectory,
            OutputPlacementMode placementMode,
            TerminalLayoutMode terminalLayout)
        {
            var precheck = new ConflictPrecheck();

            if (string.IsNullOrWhiteSpace(stageDirectory) ||
                string.IsNullOrWhiteSpace(destinationDirectory) ||
                !Directory.Exists(stageDirectory))
            {
                return precheck;
            }

            FinalLayoutPlan plan;

            try
            {
                plan = PlanFinalLayoutForTask(task, stageDirectory, destinationDirectory, placementMode, terminalLayout);
            }
            catch
            {
                // 预检失败不改变任何结论：真正的失败会在定稿那一步如实报出来（这里只当"没有冲突"）。
                return precheck;
            }

            if (plan.Failed || plan.Moves.Count == 0)
            {
                return precheck;
            }

            foreach (PlannedMove move in plan.Moves)
            {
                if (!File.Exists(move.To) && !Directory.Exists(move.To))
                {
                    continue;
                }

                ConflictTargetKind kind = Directory.Exists(move.To) && !File.Exists(move.To)
                    ? ConflictTargetKind.Directory
                    : ConflictTargetKind.File;

                precheck.TotalCount++;

                if (!plan.ProcessArtifactSources.Contains(move.From))
                {
                    precheck.ContentCount++;
                }

                if (precheck.Samples.Count < MaxConflictSamplesInDialog)
                {
                    precheck.Samples.Add((move.To, kind));
                }
            }

            return precheck;
        }

        /// <summary>
        /// 定稿布局规划的统一入口：算"终端归档基名"这件事只在这里做一次，
        /// 预检与真正的定稿走**同一份输入**（否则预检说有冲突、定稿却按另一套计划落位）。
        /// </summary>
        private static FinalLayoutPlan PlanFinalLayoutForTask(
            ArchiveTask task,
            string stageDirectory,
            string destinationDirectory,
            OutputPlacementMode placementMode,
            TerminalLayoutMode terminalLayout)
        {
            string archiveBaseName = OutputPlacement.ResolveArchiveBaseName(task.CurrentPath);

            return PlanFinalLayout(
                stageDirectory,
                destinationDirectory,
                placementMode,
                archiveBaseName,
                terminalLayout);
        }

        /// <summary>把"密码没通过"的任务登记到本批（只登记，不弹窗）。</summary>
        private void RecordPasswordFailure(ArchiveTask task)
        {
            if (task == null)
            {
                return;
            }

            if (task.Status != StatusText.WrongPassword &&
                task.Status != StatusText.PasswordAttemptLimitReached)
            {
                return;
            }

            // 本批出现过"密码类失败"这个事实：批次结束后据此在合并提示里带上"手动输入密码"的出口。
            _batchHadPasswordFailures = true;

            string fileName = string.IsNullOrWhiteSpace(task.FileName)
                ? Path.GetFileName(task.CurrentPath)
                : task.FileName;

            lock (_passwordFailuresLock)
            {
                _passwordFailures.Add((fileName, task.Status));
            }
        }

        private void ClearPasswordFailures()
        {
            lock (_passwordFailuresLock)
            {
                _passwordFailures.Clear();
            }
        }

        // ================================================================ 缺卷补救（手动指定缺失卷所在目录）

        /// <summary>本批已经为哪几个任务问过"缺失卷在哪"（同一个包重试时不再重复打扰）。</summary>
        private readonly HashSet<ArchiveTask> _volumeRepairAsked = new();

        /// <summary>
        /// 「我手动指定缺失卷所在目录」——WinRAR 参考 §2 G 组 / §3 第 2 条的采纳项。
        ///
        /// <para>
        /// 用户痛点很实在：真实分卷包常常"主体在这一层、后几卷在隔壁文件夹或另一个盘"，
        /// 而现在的行为是直接拒绝启动并报"缺 A、B"，用户只能自己去找、去挪，毫无下手的地方。
        /// </para>
        /// <para>
        /// ⛔ <b>不变量 7 一个字都不放松</b>：这里只做"帮你把卷找齐"，**绝不允许缺卷启动**。
        /// 重新归组之后仍然缺 → 返回 false，调用方照旧落成"分卷缺失"并拒绝开始。
        /// </para>
        /// <para>
        /// 归组**复用既有实现**（<see cref="VolumeGroupDetector.Group"/> +
        /// <see cref="VolumeGroupingService.ApplyGroupInfo"/>），不另写一套分卷命名规则 ——
        /// 那种规则一旦分叉，就会出现"界面上说齐了、7z 却说缺卷"。
        /// </para>
        /// </summary>
        /// <returns>true = 这一组现在齐了（可以开始）；false = 仍然不能开始。</returns>
        private async Task<bool> TryRepairMissingVolumesAsync(ArchiveTask task, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_volumeRepairAsked)
            {
                if (!_volumeRepairAsked.Add(task))
                {
                    // 同一个任务这一批已经问过一次了：再问就是骚扰（用户已经说了"没有"或"找过还是不够"）。
                    return false;
                }
            }

            string missing = task.MissingVolumeNames.Count > 0
                ? string.Join("、", task.MissingVolumeNames)
                : "（引擎没给出具体缺哪几个）";

            string question =
                $"「{task.FileName}」的分卷不完整，现在缺：{missing}。{Environment.NewLine}{Environment.NewLine}" +
                $"分卷包里每一卷都是必需的数据片，缺一卷就一定解不开 —— 所以程序**不会**在缺卷时开始。" +
                $"{Environment.NewLine}{Environment.NewLine}" +
                "如果你的卷散在别的文件夹（或者另一个盘），可以现在指定那个目录：" +
                "程序会在那里找齐这一组，找齐了就继续；找不齐仍然不会开始。";

            // 确认框走既有实现：无 UI 宿主时它返回 false（保守档），绝不弹窗、绝不死等。
            bool wantPick = await DispatcherSafeConfirmAsync(question);

            if (!wantPick)
            {
                AppendLog("INFO", $"{task.FileName}：没有手动指定缺失卷所在目录，按缺卷处理（不启动）。");
                return false;
            }

            string directory = ShowFolderBrowserOnUiThread();

            if (string.IsNullOrWhiteSpace(directory))
            {
                AppendLog("INFO", $"{task.FileName}：没有选择目录，按缺卷处理（不启动）。");
                return false;
            }

            /*
             * 目录枚举（可能几万个文件）放后台：它跑在解压管线里，而这条路径的 await 续体在 UI 上下文。
             * 只在**指定的那一个目录**里找，不递归、不扫全盘（AGENTS.md §8 隐私红线：不读工作区以外的个人目录）。
             */
            List<string> volumeFiles = await Task.Run(
                () => CollectVolumeFilesInDirectory(directory),
                cancellationToken);

            AppendLog(
                "INFO",
                $"{task.FileName}：在 {directory} 里找到 {volumeFiles.Count} 个分卷文件，正在重新归组判定。");

            VolumeGroup? group = RebuildVolumeGroup(task, volumeFiles);

            if (group == null || !group.IsComplete)
            {
                string stillMissing = group != null && group.MissingVolumeNames.Count > 0
                    ? string.Join("、", group.MissingVolumeNames)
                    : missing;

                // 如实说清"找过哪儿、还是缺什么"——用户下一步就是拿着这句话去别处找。
                string summary = group == null
                    ? "在那个目录里没找到属于这一组的分卷（或目录读不了）"
                    : group.MissingVolumeNames.Count > 0
                        ? $"重新归组后仍缺 {stillMissing}"
                        : "重新归组后仍然不完整";

                task.Status = StatusText.VolumeMissing;
                task.ErrorMessage =
                    $"分卷仍然不完整：{summary}。已找过：{directory}（{volumeFiles.Count} 个候选分卷）。";

                AppendLog("ERROR", $"{task.FileName}：{task.ErrorMessage} 仍然不启动（不变量 7）。");
                return false;
            }

            /*
             * 找齐了：把任务切到**这一组所在的那个目录**。
             *
             * 为什么必须切：分卷组必须整体在一处才能解（7-Zip 从第一卷顺着往后读），
             * 组里各卷分散在两个目录时，光"知道它们在哪儿"是解不开的。
             * 这里挑中的那个组，它的每一卷都真实存在于同一个目录里 —— 要么全在用户指定的目录，
             * 要么全在原目录（那种情况本来就不缺卷，走不到这里）。
             *
             * 切路径是安全的：CurrentPath 是"现在拿哪个文件去解压"，而 OriginalPath 仍然记着用户最初给的那个；
             * 改名、清理、报告都以 CurrentPath 为准，所以后续一切照常。
             */
            string firstVolume = group.FirstVolumePath;
            string previousPath = task.CurrentPath;

            if (!File.Exists(firstVolume))
            {
                AppendLog("ERROR", $"{task.FileName}：归组给出的第一卷不存在（{firstVolume}），按缺卷处理（不启动）。");
                return false;
            }

            new VolumeGroupingService().ApplyGroupInfo(task, group);

            task.CurrentPath = firstVolume;
            task.FileName = Path.GetFileName(firstVolume);
            task.CurrentExtension = Path.GetExtension(firstVolume);

            AppendLog(
                "INFO",
                $"{task.FileName}：分卷已找齐（{task.VolumeCount} 卷，起点 {firstVolume}）" +
                (string.Equals(previousPath, firstVolume, StringComparison.OrdinalIgnoreCase)
                    ? "，开始解压。"
                    : $"，起点由 {previousPath} 改为它（OriginalPath 仍指向你最初给的那个文件），开始解压。"));

            return true;
        }

        /// <summary>
        /// 在**指定的一个目录**里列出候选分卷文件（顶层，不递归）。
        ///
        /// 只按命名筛（能不能归组由 <see cref="VolumeGroupDetector"/> 说了算），
        /// 不读内容、不碰别的目录。
        /// </summary>
        private static List<string> CollectVolumeFilesInDirectory(string directory)
        {
            var found = new List<string>();

            try
            {
                if (!Directory.Exists(directory))
                {
                    return found;
                }

                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    string fileName = Path.GetFileName(file);

                    if (VolumeGroupDetector.TryGetVolumeIndex(fileName).HasValue ||
                        VolumeGroupDetector.TryGetFirstVolumeName(fileName) != null)
                    {
                        found.Add(file);
                    }
                }
            }
            catch
            {
                // 目录读不了：返回空集合，调用方按"没找到"如实报告。
            }

            return found;
        }

        /// <summary>
        /// 把"任务现有各卷 + 用户指定目录里找到的卷"合成一份候选去重新归组，返回**可能已经补齐**的那一组。
        ///
        /// 判据：优先按原来的 <see cref="ArchiveTask.VolumeGroupKey"/> 找；找不到就退回
        /// "哪一组包含本任务的第一卷"。两条都对不上就返回 null（如实说"没找到属于这一组的"）。
        /// </summary>
        private static VolumeGroup? RebuildVolumeGroup(ArchiveTask task, IEnumerable<string> extraFiles)
        {
            var candidates = new List<VolumeCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string path)
            {
                if (string.IsNullOrWhiteSpace(path) || !seen.Add(path))
                {
                    return;
                }

                long size = -1;

                try
                {
                    size = new FileInfo(path).Length;
                }
                catch
                {
                    // 量不出大小不影响归组（大小只用于"同名不同组"的复核）。
                }

                candidates.Add(new VolumeCandidate { Path = path, Size = size });
            }

            foreach (string path in task.VolumePaths)
            {
                AddCandidate(path);
            }

            AddCandidate(task.CurrentPath);

            foreach (string path in extraFiles ?? Enumerable.Empty<string>())
            {
                AddCandidate(path);
            }

            IReadOnlyList<VolumeGroup> groups = VolumeGroupDetector.Group(candidates);

            if (groups.Count == 0)
            {
                return null;
            }

            string currentPath = SafePathHelper.GetFullPathSafe(task.CurrentPath);

            VolumeGroup? byKey = groups.FirstOrDefault(
                g => !string.IsNullOrWhiteSpace(task.VolumeGroupKey) &&
                     string.Equals(g.GroupKey, task.VolumeGroupKey, StringComparison.OrdinalIgnoreCase));

            if (byKey != null)
            {
                return byKey;
            }

            return groups.FirstOrDefault(g => g.Volumes.Any(
                v => string.Equals(SafePathHelper.GetFullPathSafe(v.Path), currentPath, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// 在 UI 线程上弹一个"是 / 否"确认框（**无 UI 宿主返回 false**，不弹窗口也不死等）。
        ///
        /// 与 <see cref="ShowConfirmOnUiThreadAsync"/> 的区别只有降级行为：那个走
        /// <see cref="DialogService.ShowConfirm(string)"/>，无界面宿主时返回 false 已是保守档，
        /// 但它仍然要求调用方在 UI 上下文里；这里显式调度，后台线程调用也安全。
        /// </summary>
        private Task<bool> DispatcherSafeConfirmAsync(string message)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null)
            {
                return Task.FromResult(false);
            }

            if (dispatcher.CheckAccess())
            {
                return Task.FromResult(_dialogService.ShowConfirm(message));
            }

            return dispatcher.InvokeAsync(() => _dialogService.ShowConfirm(message)).Task;
        }

        /// <summary>
        /// 在 UI 线程上打开"选择文件夹"对话框（**无 UI 宿主返回空串**）。
        ///
        /// 只挑目录、不扫盘：真正的枚举在调用方丢给后台线程做（UI 线程纪律）。
        /// </summary>
        private string ShowFolderBrowserOnUiThread()
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null)
            {
                return string.Empty;
            }

            if (dispatcher.CheckAccess())
            {
                return _dialogService.ShowFolderBrowserDialog();
            }

            return dispatcher.InvokeAsync(() => _dialogService.ShowFolderBrowserDialog()).Result;
        }


        /// <summary>
        /// 本批允许同时跑几个任务（并发上限）。
        ///
        /// <para>
        /// 「全速」开着时返回本批的任务数（= 不节流）—— 这是"自动等待/节流必须配一个立即继续的出口"
        /// 那条采纳项的落地（<c>docs/WinRAR功能参考.md</c> §2 D 组）。它有意的取舍：
        /// **不管磁盘、不管别的程序**，跑成什么样由用户自己负责；界面上的勾选框文案把这一点写明了。
        /// </para>
        /// <para>
        /// 关着时读设置项 <see cref="AppSettings.MaxParallelExtractCount"/>（1–8，默认 4 ——
        /// 2026-09-22 的并行实测：并发 1→4 是 3.08×，4→8 只再快 1.9% 却把单包耗时拉长一倍）。
        /// 夹一次是兜底：设置对象不一定走过 <c>AppSettings.Normalize</c>（测试里直接 new），
        /// 0 或负数会让"等并发位"的循环条件永远成立 —— 那个任务会永远等下去。
        /// </para>
        /// </summary>
        private int ResolveMaxParallel(IReadOnlyList<ArchiveTask> tasks, out bool fullSpeed)
        {
            fullSpeed = _vm.RunAtFullSpeed;

            if (fullSpeed)
            {
                return Math.Max(1, tasks?.Count ?? 1);
            }

            return Math.Clamp(Settings.MaxParallelExtractCount, 1, MaxParallelExtractCountCeiling);
        }

        /// <summary>并发上限的硬顶（与设置界面的 1–8 一致；这里只做兜底夹取）。</summary>
        private const int MaxParallelExtractCountCeiling = 8;


        /// <summary>
        /// 本批的"完成后打开输出目录"记账清零（设置项 <see cref="AppSettings.OpenOutputFolderWhenDone"/>）。
        /// </summary>
        private void ResetBatchOutputFolderState()
        {
            lock (_outputFolderLock)
            {
                _outputFolderOpenedThisBatch = false;
            }
        }

        /// <summary>
        /// 试着打开"这一批的"输出目录，**整批只成功打开一次**。
        ///
        /// 只允许在"最外层任务 + 内容物已定稿 + 输出校验通过 + 未取消"的收尾处调用
        /// （判定在 <see cref="PostProcessSuccessAsync"/> 里，那里才知道结论是否成立）。
        /// 内层包（续解出来的）一律不触发：它们与父任务共用同一个目录，反复打开就是刷屏。
        /// </summary>
        /// <returns>真的打开了返回 true；关着开关 / 已经开过 / 目录不存在返回 false。</returns>
        private bool TryOpenOutputFolderOnce(ArchiveTask task)
        {
            if (!Settings.OpenOutputFolderWhenDone)
            {
                return false;
            }

            if (task.IsContinuationTask)
            {
                return false;
            }

            lock (_outputFolderLock)
            {
                if (_outputFolderOpenedThisBatch)
                {
                    return false;
                }

                _outputFolderOpenedThisBatch = true;
            }

            /*
             * ⚠ 顺序有意：先"占位"再打开。
             *
             * 并发下两个任务可能同时走到这里，先占位保证只有一个真的去开资源管理器；
             * 万一打开失败（目录被删了之类），这一批也不会再补开一次 —— 用户看到一句 WARN 就够，
             * 反复尝试开一个不存在的目录只会刷日志。
             *
             * 调用**只打开文件夹**的实现（不置前、不最大化、不抢焦点，见 MainViewModel 的说明）。
             */
            return _vm.OpenCompletedOutputDirectory(task.OutputPath);
        }

        /// <summary>
        /// 批开始时把手动密码清干净：**只对本次运行有效**（不变量 5）。
        ///
        /// 不是"记住上次输的密码"—— 那是持久化，红线。上一批输过什么，这一批重新问。
        /// </summary>
        private void ResetManualBatchPassword()
        {
            _manualBatchPassword = null;
            _manualPasswordPrompted = false;
            _batchHadPasswordFailures = false;
        }

        // ================================================================ 解压前的提醒（无用物 / 无可用密码）

        /*
         * 用户 2026-09-22 需求第 8 条原话：
         * 「无用物提醒 —— 源文件夹里可能有打包者的工具/诱饵文件，我们识别不出来；每次解压前弹一个提醒，
         *   列出可能的无用物；同时提醒那些没有可用密码的压缩包（即使密码本已经预加载）。」
         *
         * 两段合并成**一个**弹窗（项目先例：多任务清理合并成 1 次确认）。四条纪律（都有测试钉住）：
         * ① **无界面宿主按「继续处理」放行** —— 与 ShowConfirm 的 fallback=false（=取消）方向相反，
         *    理由见 DialogService.ShowReminderConfirm：这是一条纯提示，把整批解压拦死才是事故；
         * ② **「本次运行不再提示」只记在内存里**（下面那个字段），绝不写设置文件；
         * ③ **两段都为空时不弹**（不许弹空对话框）；
         * ④ 判据**窄**：无用物只看文件名 + 魔数体检，宁可漏报不可误报。
         */

        /// <summary>
        /// 本次运行内不再弹"解压前的提醒"（用户在弹窗里勾了那个勾选项）。
        ///
        /// <para>它是**实例字段**，不是静态、更不是设置项：用户勾的是"这次运行别再烦我"，
        /// 关掉程序重开就该再提醒一次（用户点名："只在本次运行内记忆，绝不写设置文件"）。
        /// 也因此**没有**任何把它写盘 / 读盘的代码 —— 测试直接断言设置文件一个字节都没变。</para>
        /// </summary>
        private bool _remindersSuppressedThisRun;

        /// <summary>
        /// "无用物"魔数体检用的识别器：**复用现有那一套**
        /// （<see cref="MagicArchiveProber"/> → <c>ArchiveDetectService</c>：文件头魔数 + 认不出来时再看尾部内嵌归档），
        /// 绝不另写一份魔数表 —— 两份必然会漂移，而漂移的后果是把用户的真包（伪装后缀 / 双面文件）
        /// 叫成"无用物"，正好砸在本程序最拿手的场景上。
        /// </summary>
        private readonly IArchiveProber _reminderProber = new MagicArchiveProber();

        /// <summary>
        /// 解压前提醒的"用户答案"（可注入；单测用它模拟"点了继续 / 点了先不处理 / 勾了不再提示"）。
        ///
        /// <para>为什么需要它：无界面宿主里那个框根本不会显示，而"勾了不再提示之后到底还会不会再弹"
        /// 只有让调用方答一次才测得出来。正式路径永远是 null（走真弹窗）。</para>
        /// </summary>
        internal Func<string, ReminderAnswer>? ReminderAnswerOverride { get; set; }

        /// <summary>一次"解压前的提醒"的用户答案。</summary>
        internal sealed class ReminderAnswer
        {
            /// <summary>true = 继续处理；false = 先不处理（这一批不开始，一个字节都不动）。</summary>
            public bool Confirmed { get; init; }

            /// <summary>是否勾了「本次运行不再提示这类提醒」。</summary>
            public bool OptionChecked { get; init; }
        }

        /// <summary>
        /// 解压前的合并提醒。
        /// </summary>
        /// <returns><c>false</c> = 用户在提醒里选了「先不处理」，调用方**直接返回**（这一批不开始）。</returns>
        private async Task<bool> ConfirmBatchRemindersAsync(IReadOnlyList<ArchiveTask> selectedTasks)
        {
            if (_remindersSuppressedThisRun)
            {
                return true;
            }

            SourceJunkScanResult junk = await SourceJunkScanner
                .ScanAsync(selectedTasks, _reminderProber, CancellationToken.None)
                .ConfigureAwait(false);

            List<ArchiveTask> noPassword = FindTasksWithoutUsablePassword(selectedTasks);

            if (!junk.HasAnything && noPassword.Count == 0)
            {
                // 两段都为空：不弹、也不写日志（没有结论可写）。空对话框是纯噪声。
                return true;
            }

            LogReminderFindings(junk, noPassword);

            ReminderAnswer answer = await AskReminderAsync(BuildReminderMessage(junk, noPassword))
                .ConfigureAwait(false);

            if (answer.OptionChecked)
            {
                _remindersSuppressedThisRun = true;
                AppendLog("INFO", StatusText.JunkReminderOptionLog);
            }

            if (!answer.Confirmed)
            {
                AppendLog("INFO", StatusText.JunkReminderDeclinedLog);
            }

            return answer.Confirmed;
        }

        /// <summary>
        /// B 段：需要密码、但当前**一个可用候选都没有**的包（"即使密码本已经预加载"的那种）。
        ///
        /// <para>判据与真正解压时**完全同一套参数**（同一个 <c>GetPasswordCandidates</c>、同一个统一密码开关、
        /// 同一个旁路说明文件开关）：判据不一样就会出现"提醒说没有密码、结果它解开了"这种自相矛盾。</para>
        ///
        /// <para>⚠ 只判"有没有候选"，不判"到底加没加密" —— 后者是 <see cref="ArchiveTask.IsEncrypted"/>
        /// （识别阶段的结论）。没有密码的普通包不在这一段范围内。</para>
        /// </summary>
        private List<ArchiveTask> FindTasksWithoutUsablePassword(IReadOnlyList<ArchiveTask> tasks)
        {
            var result = new List<ArchiveTask>();

            foreach (ArchiveTask? task in tasks)
            {
                if (task == null || !task.IsEncrypted)
                {
                    continue;
                }

                List<PasswordItem> candidates = _passwordService.GetPasswordCandidates(
                    task,
                    Settings.UseGlobalPasswordForAllTasks ? GlobalPassword : string.Empty,
                    _passwordService.Passwords,
                    Settings.TryEmptyPasswordFirst,
                    Settings.EnableSidecarPassword);

                if (!SourceJunkScanner.HasUsablePasswordCandidate(candidates))
                {
                    result.Add(task);
                }
            }

            return result;
        }

        /// <summary>批首写一条 WARN，把两段的结论都落进日志（数量 + 前几条名字）。</summary>
        private void LogReminderFindings(SourceJunkScanResult junk, IReadOnlyList<ArchiveTask> noPassword)
        {
            string junkPart = junk.HasAnything
                ? string.Format(
                    StatusText.JunkReminderLogFoundFormat,
                    junk.TotalCount,
                    string.Join("、", junk.SampleNames))
                : StatusText.JunkReminderLogNoneText;

            string passwordPart = noPassword.Count > 0
                ? string.Format(
                    StatusText.JunkReminderLogFoundFormat,
                    noPassword.Count,
                    string.Join(
                        "、",
                        noPassword.Take(MaxPasswordFailureNamesInDialog).Select(DisplayNameOf)))
                : StatusText.JunkReminderLogNoneText;

            AppendLog("WARN", string.Format(StatusText.JunkReminderLogFormat, junkPart, passwordPart));
        }

        /// <summary>
        /// 拼提醒正文。三段话的顺序见 <see cref="StatusText"/> 里那一组的说明（**不要重排**）：
        /// ① 这些文件是什么 → ② 本程序不会动它们 → ③ 解压完你自己判断要不要删。
        /// </summary>
        private static string BuildReminderMessage(SourceJunkScanResult junk, IReadOnlyList<ArchiveTask> noPassword)
        {
            var builder = new StringBuilder();

            builder.AppendLine(StatusText.JunkReminderIntro);

            if (junk.HasAnything)
            {
                builder.AppendLine();
                builder.AppendLine(StatusText.JunkReminderJunkHeader);
                builder.AppendLine(string.Format(StatusText.JunkReminderJunkCountFormat, junk.TotalCount));

                foreach (IGrouping<string, SourceJunkItem> group in
                         junk.Items.GroupBy(item => item.DirectoryPath, StringComparer.OrdinalIgnoreCase))
                {
                    builder.AppendLine("  " + group.Key);

                    foreach (SourceJunkItem item in group)
                    {
                        builder.AppendLine("    " + item.FileName);
                    }
                }

                if (junk.ExtraCount > 0)
                {
                    builder.AppendLine(string.Format(StatusText.JunkReminderJunkMoreFormat, junk.ExtraCount));
                }

                if (junk.Truncated)
                {
                    builder.AppendLine(StatusText.JunkReminderTruncatedNote);
                }

                builder.AppendLine(StatusText.JunkReminderJunkFooter);
            }

            if (noPassword.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine(string.Format(StatusText.JunkReminderPasswordHeaderFormat, noPassword.Count));

                foreach (ArchiveTask task in noPassword.Take(MaxPasswordFailureNamesInDialog))
                {
                    builder.AppendLine("    " + DisplayNameOf(task));
                }

                if (noPassword.Count > MaxPasswordFailureNamesInDialog)
                {
                    builder.AppendLine(string.Format(
                        StatusText.JunkReminderPasswordMoreFormat,
                        noPassword.Count - MaxPasswordFailureNamesInDialog));
                }

                builder.AppendLine(StatusText.JunkReminderPasswordOutcome);
                builder.AppendLine(StatusText.JunkReminderPasswordExit);
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>任务在提示里的显示名（文件名优先，退回完整路径）。</summary>
        private static string DisplayNameOf(ArchiveTask task) =>
            string.IsNullOrWhiteSpace(task.FileName) ? task.CurrentPath : task.FileName;

        /// <summary>
        /// 拿用户的答案。
        ///
        /// <para><b>无界面宿主（单测 / 控制台宿主）在这里按「继续处理」返回</b>，而且**连弹窗都不尝试**：
        /// 既不产生 DialogService 的降级记录（那会让"这个宿主没有界面"的既有断言被这条纯提示污染），
        /// 也不会有任何等待。整批解压因此照常跑完 —— 这一点由管线测试证明。</para>
        /// </summary>
        private async Task<ReminderAnswer> AskReminderAsync(string message)
        {
            if (ReminderAnswerOverride != null)
            {
                return ReminderAnswerOverride(message);
            }

            if (System.Windows.Application.Current == null)
            {
                AppendLog("WARN", StatusText.JunkReminderNoHostLog);

                return new ReminderAnswer { Confirmed = true, OptionChecked = false };
            }

            return await ShowReminderConfirmOnUiThreadAsync(message).ConfigureAwait(false);
        }

        /// <summary>
        /// 在 UI 线程上弹提醒（与 <see cref="ShowConfirmOnUiThreadAsync"/> 同一套调度纪律：
        /// 管线可能已经不在 UI 线程上，直接弹会造出一个没人泵消息的窗口）。
        ///
        /// <para>降级方向由 <see cref="DialogService.ShowReminderConfirm"/> 定义成"继续"，
        /// 所以这里即使调度器已经关掉也只是放行，不会把整批拦下。</para>
        /// </summary>
        private async Task<ReminderAnswer> ShowReminderConfirmOnUiThreadAsync(string message)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            bool confirmed;
            bool optionChecked = false;

            if (dispatcher == null || dispatcher.CheckAccess())
            {
                confirmed = _dialogService.ShowReminderConfirm(
                    StatusText.JunkReminderTitle,
                    message,
                    StatusText.JunkReminderYesText,
                    StatusText.JunkReminderNoText,
                    StatusText.JunkReminderOptionText,
                    optionCheckedByDefault: false,
                    out optionChecked);
            }
            else
            {
                confirmed = await dispatcher.InvokeAsync(() =>
                {
                    bool checkedNow;

                    bool result = _dialogService.ShowReminderConfirm(
                        StatusText.JunkReminderTitle,
                        message,
                        StatusText.JunkReminderYesText,
                        StatusText.JunkReminderNoText,
                        StatusText.JunkReminderOptionText,
                        optionCheckedByDefault: false,
                        out checkedNow);

                    optionChecked = checkedNow;

                    return result;
                }).Task.ConfigureAwait(false);
            }

            return new ReminderAnswer { Confirmed = confirmed, OptionChecked = optionChecked };
        }

        /// <summary>
        /// 整批**一次性**地问"要不要手动给一个密码"（WinRAR 参考 §2 H 组 / §3 附注采纳项）。
        ///
        /// <para>
        /// 什么时候问：本批里有任何任务"可能带密码"时才问 —— 判据是既有的两个任务字段
        /// （<see cref="ArchiveTask.IsEncrypted"/> 或 <see cref="ArchiveTask.PasswordStatus"/> 为"需要密码"）。
        /// 都没有的批次一个字都不问，免得每次解一堆无密码的包都被挡一下。
        /// </para>
        /// <para>
        /// <b>无 UI 宿主（单测 / 控制台宿主）：不弹窗、不死等</b>，直接返回并写一条降级日志 ——
        /// 与 <c>DialogService.ShowConflictDecisionAsync</c> 同一口径。
        /// </para>
        /// <para>
        /// 用户答"不用"也一样记 <see cref="_manualPasswordPrompted"/>：一次性就是一次性，
        /// 不能因为"没填"就每次遇到觉得需要密码的包再问一遍。
        /// </para>
        /// </summary>
        private async Task PromptForManualBatchPasswordAsync(IReadOnlyList<ArchiveTask> tasks)
        {
            if (_manualPasswordPrompted)
            {
                return;
            }

            bool mayNeedPassword = tasks.Any(task =>
                task != null &&
                (task.IsEncrypted ||
                 string.Equals(task.PasswordStatus, StatusText.PasswordNeed, StringComparison.Ordinal)));

            if (!mayNeedPassword)
            {
                return;
            }

            _manualPasswordPrompted = true;

            List<ArchiveTask> suspects = tasks
                .Where(task => task != null && (task.IsEncrypted ||
                    string.Equals(task.PasswordStatus, StatusText.PasswordNeed, StringComparison.Ordinal)))
                .Take(MaxPasswordFailureNamesInDialog)
                .ToList();

            string message =
                $"本批有 {tasks.Count(t => t != null && (t.IsEncrypted || string.Equals(t.PasswordStatus, StatusText.PasswordNeed, StringComparison.Ordinal)))} 个包可能带密码。" +
                Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, suspects.Select(t => t.FileName)) +
                Environment.NewLine + Environment.NewLine +
                "如果密码本里没有它们的密码，可以现在手动给一个：本批所有任务都会把它当候选试一遍。" +
                Environment.NewLine +
                "⚠ 只对本次运行有效，**不会写进任何文件、也不会进密码列表**（关掉程序就没了）。";

            string? entered = await ShowPasswordPromptOnUiThreadAsync(message);

            if (string.IsNullOrEmpty(entered))
            {
                AppendLog("INFO", "没有手动输入密码（或当前宿主没有界面），本批按密码本与统一密码继续。");
                return;
            }

            _manualBatchPassword = entered;

            /*
             * 日志只说"收到一个手动密码"，**绝不记录内容**（不变量 5）。
             * 措辞上不给"密码"配冒号：PasswordMasker 的兜底规则是"密码 + 冒号 → 本行剩余全部打码"，
             * 那样这一行的后半句会被吃掉（旧版实测踩过这个坑）。
             */
            AppendLog(
                "INFO",
                $"已收到一个手动密码（{entered.Length} 个字符），只对本次运行有效，不落盘；" +
                "本批所有任务都会试它。");

            if (entered.Length > MaxSupportedPasswordLength)
            {
                /*
                 * 抄 WinRAR 6.10 才补上的那个坑：超长密码会被**静默截断**，
                 * 用户输对了却看到"密码错误"是最难查的一类现象，所以当场说清。
                 */
                AppendLog(
                    "WARN",
                    $"⚠ 你输入的手动密码有 {entered.Length} 个字符，超过 7-Zip 支持的 {MaxSupportedPasswordLength} 个，" +
                    "更长的部分会被截断 —— 如果解不开，先确认密码本身有没有这么长。");
            }
        }

        /// <summary>
        /// 手动密码的候选位置：插在**空密码之后**（<paramref name="insertAfterEmpty"/> 为 true 时），
        /// 否则追加到末尾。
        ///
        /// <para>
        /// 为什么不是最前：空密码是"无密码包"的必经一步，而且它是零成本的一次尝试（不进密码循环也不亏）。
        /// 为什么排在密码本之前：手动输入是用户**刚刚**给出的信息，比密码本里的历史条目更"新"；
        /// 而且它通常就是那个包真正需要的密码，早点试能省掉一大圈无谓试错。
        /// </para>
        /// <para>
        /// 传 null / 空串时什么都不做（空密码已经由调用方自己保证了）。
        /// </para>
        /// </summary>
        private static void InsertManualPasswordCandidate(List<PasswordItem> candidates, string? manualPassword)
        {
            if (string.IsNullOrEmpty(manualPassword) || candidates == null)
            {
                return;
            }

            if (candidates.Any(item => string.Equals(item?.Value, manualPassword, StringComparison.Ordinal)))
            {
                return;
            }

            var candidate = new PasswordItem
            {
                Value = manualPassword,
                Source = ManualPasswordSource,
                IsEnabled = true,
                Remark = "本次运行手动输入（不落盘）"
            };

            int index = candidates.FindIndex(
                item => string.Equals(item?.Source, "Empty", StringComparison.Ordinal));

            if (index >= 0 && index < candidates.Count - 1)
            {
                candidates.Insert(index + 1, candidate);
                return;
            }

            candidates.Add(candidate);
        }

        /// <summary>手动密码候选的来源标记（只在这里出现一次，别在别处再写字面量）。</summary>
        internal const string ManualPasswordSource = "ManualBatch";

        /// <summary>
        /// 7-Zip 支持的密码长度上限（超过会被截断）。抄 WinRAR 参考里"6.10 才补上截断警告"那条教训：
        /// 我们**当场提示**，不静默截断。
        /// </summary>
        private const int MaxSupportedPasswordLength = 127;

        /// <summary>
        /// 在 UI 线程上要一个密码。**无 UI 宿主返回 null（不弹窗、不死等）**。
        ///
        /// <para>
        /// 为什么自己建一个小窗口而不是复用 <c>DialogService</c>：它的自绘对话框
        /// （<c>Views/AppDialogWindow</c>）没有输入框，而"改一个通用对话框控件"不在本次授权范围内
        /// （任务书明确要求"需要什么就写进报告"）。这里用 <see cref="PasswordBox"/> 而不是 TextBox：
        /// 手动密码同样不许明文显示在屏幕上（不变量 5 的同一精神），而且 <c>PasswordBox.Password</c>
        /// 不会进 WPF 的绑定/日志路径。
        /// </para>
        /// <para>
        /// 返回 null 的两种情况（都不算失败）：当前宿主没有 WPF 界面；用户点了"跳过"或直接关窗口。
        /// </para>
        /// </summary>
        private Task<string?> ShowPasswordPromptOnUiThreadAsync(string message)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null)
            {
                // 无界面宿主（单测 / 控制台）：只写日志，绝不弹模态框 —— 那会阻塞调用方且没人点得掉。
                AppendLog("INFO", "当前宿主没有 WPF 界面，跳过「手动输入密码」这一步（不影响其它流程）。");
                return Task.FromResult<string?>(null);
            }

            if (dispatcher.CheckAccess())
            {
                return Task.FromResult(ShowPasswordPromptModal(message));
            }

            return dispatcher.InvokeAsync(() => ShowPasswordPromptModal(message)).Task;
        }

        /// <summary>
        /// 密码输入小窗口本体（**只允许在 UI 线程上调用**）。
        ///
        /// 刻意**不设** <c>WindowStartupLocation</c> 以外的任何窗口行为：不置前、不最大化、不改尺寸。
        /// 默认按钮是「跳过」而不是「确定」——回车不该把可能错的密码当真（同 Ask 档"默认不能是危险动作"）。
        /// </summary>
        private static string? ShowPasswordPromptModal(string message)
        {
            var window = new System.Windows.Window
            {
                Title = "手动输入密码（只对本次运行有效）",
                Width = 520,
                Height = 300,
                MinWidth = 460,
                MinHeight = 280,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                Owner = System.Windows.Application.Current?.MainWindow,
                Background = System.Windows.Media.Brushes.White
            };

            var root = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(16) };

            for (int i = 0; i < 3; i++)
            {
                root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
                {
                    Height = i == 1 ? new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
                                    : System.Windows.GridLength.Auto
                });
            }

            var messageBlock = new System.Windows.Controls.TextBlock
            {
                Text = message,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 12)
            };

            System.Windows.Controls.Grid.SetRow(messageBlock, 0);
            root.Children.Add(messageBlock);

            var passwordBox = new System.Windows.Controls.PasswordBox
            {
                Height = 30,
                VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new System.Windows.Thickness(0, 0, 0, 12)
            };

            System.Windows.Controls.Grid.SetRow(passwordBox, 1);
            root.Children.Add(passwordBox);

            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };

            var okButton = new System.Windows.Controls.Button
            {
                Content = "用这个密码",
                MinWidth = 110,
                Height = 30,
                Margin = new System.Windows.Thickness(0, 0, 8, 0)
            };

            var skipButton = new System.Windows.Controls.Button
            {
                Content = "跳过",
                MinWidth = 86,
                Height = 30,
                IsDefault = true,
                IsCancel = true
            };

            string? result = null;

            okButton.Click += (_, _) =>
            {
                result = passwordBox.Password;
                window.DialogResult = true;
                window.Close();
            };

            skipButton.Click += (_, _) =>
            {
                result = null;
                window.DialogResult = false;
                window.Close();
            };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(skipButton);

            System.Windows.Controls.Grid.SetRow(buttonPanel, 2);
            root.Children.Add(buttonPanel);

            window.Content = root;

            /*
             * 提醒（用户 2026-09-22 反馈的原话：「密码本那个窗口弹出来之后躲到主窗口后面了，
             * 只有声音、没有闪烁，声音还很轻」）。
             *
             * ⚠ 这一条**修正**了本方法早先那句"不置前、不 Topmost"的注释：那个口径来自 AGENTS.md §13，
             * 而 §13 管的是**代理脚本不许抢用户前台**（免得用户没法干别的事）。这里是**程序自己**
             * 在"整批卡在等密码"时把输入框拎出来 —— 用户明确要求这个行为，两者不冲突。
             * 强度是 Strong：一直闪任务栏（闪到被点）+ 连响三声。
             */
            ArchiveFixer.Helpers.WindowAttention.Attach(window, ArchiveFixer.Helpers.AttentionStrength.Strong);

            // 焦点给输入框（窗口被激活之后才拿得到键盘焦点）。
            window.Loaded += (_, _) => passwordBox.Focus();

            window.ShowDialog();

            return result;
        }

        // ================================================================ 危险条目提示（只提示不阻断）

        /// <summary>
        /// 可执行 / 脚本类后缀 —— 判据只有这一份（别在别处再列一遍）。
        ///
        /// 取自 WinRAR 的"解压时排除的文件类型"示例（<c>*.scr *.pif *.exe</c>），
        /// 再加上脚本类与快捷方式。**只用于统计提示**，不做任何过滤（见 <see cref="AnalyzeDangerousEntries"/>）。
        /// </summary>
        private static readonly string[] DangerousEntryExtensions =
        {
            ".exe", ".scr", ".pif", ".com", ".msi", ".msp", ".cpl",
            ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
            ".hta", ".lnk", ".reg", ".jar", ".inf"
        };

        /// <summary>提示里最多列几个条目名（列太多会把详情刷屏，用户反而不看）。</summary>
        private const int MaxDangerousEntriesInHint = 5;

        /// <summary>
        /// 「危险条目提示」的展示前缀。它**不是**任务状态（不进 StatusText / 配色 / 统计），
        /// 只是一句提示文本的前缀，所以按 §7 的要求用常量收在这里，不散落字面量。
        /// </summary>
        internal const string DangerousEntriesHintPrefix = "可疑条目提示：";

        /// <summary>
        /// 在**已有的那一遍 list 结果**里统计"可执行 / 脚本类条目"，产出给用户看的一句话。
        ///
        /// <para>
        /// 三条纪律（<c>docs/WinRAR功能参考.md</c> §3 第 4/5 条）：
        /// ① **绝不为此再 list 一遍** —— 只吃调用方已经拿到手的那份清单（加密包每多列一次目录就多一次失败机会）；
        /// ② **只提示，绝不阻断** —— 真实资源包里安装器 / 补丁经常就是内容物，不做全局硬排除掩码；
        /// ③ 关掉设置项时返回空串，调用方据此不写任务字段、也不打日志。
        /// </para>
        ///
        /// 返回空串 = "没统计" 或 "统计了但没有可疑条目"，两种情况调用方都不必区分：
        /// 提示的意义只在"有东西要说"的时候。
        /// </summary>
        internal static string AnalyzeDangerousEntries(IEnumerable<ArchiveEntry>? entries, bool enabled)
        {
            if (!enabled || entries == null)
            {
                return string.Empty;
            }

            var found = new List<string>();
            int dangerousCount = 0;

            foreach (ArchiveEntry? entry in entries)
            {
                if (entry == null || entry.IsDirectory)
                {
                    continue;
                }

                string path = entry.Path ?? string.Empty;

                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                string extension = Path.GetExtension(path);

                if (string.IsNullOrWhiteSpace(extension))
                {
                    continue;
                }

                if (!DangerousEntryExtensions.Any(
                        known => string.Equals(known, extension, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                dangerousCount++;

                if (found.Count < MaxDangerousEntriesInHint)
                {
                    found.Add(Path.GetFileName(path));
                }
            }

            if (dangerousCount == 0 || found.Count == 0)
            {
                return string.Empty;
            }

            string samples = string.Join("、", found);

            if (dangerousCount > found.Count)
            {
                samples += $"…等 {dangerousCount} 个";
            }

            return $"{DangerousEntriesHintPrefix}本包含 {dangerousCount} 个可执行 / 脚本类条目（{samples}）。" +
                   "这不是错误，只是提醒你先看清楚再打开；程序不会因此阻断解压。";
        }

        /// <summary>
        /// 这条提示去哪儿显示：任务字段（失败清单里逐归档的第二级会原样带出）＋ 界面日志。
        ///
        /// 与"文件名已加密"/"路径过长"同一处产出（同一次 list），保证三个结论口径一致。
        /// </summary>
        private void PublishDangerousEntriesHint(ArchiveTask task, string hint, string level = "WARN")
        {
            if (string.IsNullOrWhiteSpace(hint))
            {
                return;
            }

            task.DangerousEntriesWarning = hint;
            AppendLog(level, $"{task.FileName}：{hint}");
        }

        /// <summary>
        /// 把本批"密码错误 / 达到密码尝试上限"的任务**合并成一次**提示。
        ///
        /// 旧做法：在每个任务内部各弹一次模态框，50 个错包 = 50 次阻塞点击 ——
        /// 批量处理根本跑不动，用户看到的就是"点了没反应"。
        /// 新做法：循环里只登记（<see cref="RecordPasswordFailure"/>），批次结束后在这里一次说清：
        /// 有几个、都是谁、其中几个是"到上限"（不是密码错误）。
        /// 日志与弹窗是同一句话，所以没有界面时（无头宿主）也留得下证据。
        /// </summary>
        private async Task ShowPasswordFailuresSummaryAsync()
        {
            List<(string FileName, string Status)> failures;

            lock (_passwordFailuresLock)
            {
                if (_passwordFailures.Count == 0)
                {
                    return;
                }

                failures = _passwordFailures.ToList();
                _passwordFailures.Clear();
            }

            int wrongPasswordCount = failures.Count(f => f.Status == StatusText.WrongPassword);
            int attemptLimitCount = failures.Count - wrongPasswordCount;

            /*
             * 措辞有个坑：日志会被 PasswordMasker 兜底脱敏，规则是"密码 + 冒号 → 本行剩余全部打码"。
             * 所以这里**不要在"密码"后面直接跟冒号**，否则用户看到的是"本批 3 个包…密码：******"，
             * 文件名与原因全被吃掉（实测踩过）。原因写成"原因：密码错误…"这种形态是安全的。
             */
            string reason = attemptLimitCount == 0
                ? "密码错误或缺少正确密码"
                : wrongPasswordCount == 0
                    ? $"{StatusText.PasswordAttemptLimitReached}（候选还没试完就按上限停了，不等于密码错误）"
                    : $"密码错误 {wrongPasswordCount} 个 / {StatusText.PasswordAttemptLimitReached} {attemptLimitCount} 个" +
                      "（后者只是候选没试完，不等于密码错误）";

            var builder = new StringBuilder();

            builder.Append(failures.Count == 1
                ? "有 1 个包没能解开"
                : $"本批 {failures.Count} 个包没能解开");

            builder.Append("，原因：");
            builder.Append(reason);
            builder.AppendLine();

            foreach (string name in failures.Select(f => f.FileName).Take(MaxPasswordFailureNamesInDialog))
            {
                builder.AppendLine(name);
            }

            if (failures.Count > MaxPasswordFailureNamesInDialog)
            {
                builder.AppendLine($"…等共 {failures.Count} 个（完整清单见「复制失败列表 / 导出失败清单」）。");
            }

            /*
             * 手动输入密码的**出口指引**（WinRAR 参考 §2 H 组采纳项）。
             *
             * 关键的一句是"重跑这一批会自动再问一次"：用户在这里看到"密码错误"时，
             * 下一步该做什么不是"去设置里改密码本"，而是"再点一次、把密码输进去"。
             * 只写"可以用手动密码"而不说怎么再触发，等于没说。
             */
            builder.AppendLine();
            builder.AppendLine(
                _manualBatchPassword != null
                    ? "本次运行你已经手动输入过一个密码；如果还是解不开，说明它不是这些包的密码 —— 换一个再试，或者去核对密码本。"
                    : _batchHadPasswordFailures
                        ? "下一步：再点一次「一键处理 / 只解压」，程序会在开始前问你要不要手动输一个密码" +
                          "（只对那次运行有效，不会存盘）。"
                        : "如果这些包需要密码：再点一次「一键处理 / 只解压」，程序会在开始前问你要不要手动输一个密码。");

            string message = builder.ToString().TrimEnd();

            // 日志与弹窗同源：日志一定写（无界面宿主也留得下证据），弹窗只在真的有界面时弹。
            // 这里把换行换成"；"是为了让日志一行看完 —— 换行后的内容不会被脱敏规则吃掉。
            AppendLog("WARN", message.Replace(Environment.NewLine, "；"));

            await ShowWarningOnUiThreadAsync(message);
        }

        private string GlobalPassword => _vm.GlobalPassword;
        private string SelectedOutputDirectory => _vm.SelectedOutputDirectory;
        private ObservableCollection<ArchiveTask> Tasks => _vm.Tasks;
        private bool IsStopping { get => _vm.IsStopping; set => _vm.IsStopping = value; }
        private bool IsBusy { get => _vm.IsBusy; set => _vm.IsBusy = value; }
        private void AppendLog(string message) => _vm.AppendLog(message);
        private void AppendLog(string level, string message) => _vm.AppendLog(level, message);
        private void UpdateSummary() => _vm.UpdateSummary();

        /// <summary>
        /// 批量解压入口 —— **地基路径**（界面「只解压（不改后缀）」走的就是它）。
        ///
        /// <para>
        /// 源包处理（决策 D-9；用户 2026-09-22 的最新规则）：
        /// </para>
        /// <list type="bullet">
        /// <item><description>「一键处理」= 整理路径 → 走 <see cref="StartExtractForOneClickAsync"/>；</description></item>
        /// <item><description>本方法 = 地基路径（单层「只解压」）。</description></item>
        /// </list>
        /// <para>
        /// ⚠ <b>两条路径的源包规则现在是同一个</b>：都读 <see cref="AppSettings.SourceHandling"/>，
        /// 都在"内容物已定稿 + 输出校验通过 + 未取消 + 分卷组完整"之后把整组源包移入 <c>其余物</c>。
        /// 用户原话：「如果成功了你就直接将源包放在其余物里面」，并明确"地基路径也照这条走"
        /// （这推翻了更早那版"地基路径永远不动源包"的约定）。<c>KeepInPlace</c> 档仍是"永不搬"的出口。
        /// </para>
        /// <para>
        /// 两条路径真正的差别只剩一处（回写 <c>oneClickRun</c> 一路传到底）：**一键处理有续解链**，
        /// 所以"第一层只出中间件"时它把源包搬运留到链结束后补做（<see cref="CompleteRootSourcePackagesAfterChainAsync"/>）；
        /// 单层的「只解压」没有链可等，当场按"定稿 + 校验通过"处理。
        /// 定稿、校验、归集、工作区清理的行为两条路径完全一致。
        /// </para>
        /// </summary>
        public Task StartExtractAsync() => StartExtractCoreAsync(oneClickRun: false, runOptions: null);

        /// <summary>
        /// 「一键处理」的显式入口：与 <see cref="StartExtractAsync"/> 同一个本体，
        /// 差别只在"有续解链"这件事上（见 <see cref="StartExtractAsync"/> 的说明）。
        /// </summary>
        /// <param name="runOptions">
        /// 本次选项面板选出来的**运行期快照**（规格 §9；null = 没弹面板 / 无界面宿主，按设置走）。
        ///
        /// <para>
        /// 它只活在这一批里：进方法时记下、<c>finally</c> 里置空。落点经它翻译成
        /// <see cref="ExtractOptions"/> 上那三个既有字段（唯一实现仍是 <c>OutputPlacement</c>），
        /// 终端落法与源包处理则在收尾处优先取它的值 ——
        /// **不勾"存为默认"时设置文件一个字节都不会被写**，因为这条路径上没有任何写设置的代码。
        /// </para>
        /// </param>
        public Task StartExtractForOneClickAsync(OneClickRunOptions? runOptions = null) =>
            StartExtractCoreAsync(oneClickRun: true, runOptions);

        private async Task StartExtractCoreAsync(bool oneClickRun, OneClickRunOptions? runOptions = null)
        {
            if (_isExtracting)
            {
                AppendLog("WARN", "当前已经在批量解压中，忽略重复启动。");
                return;
            }

            var selectedTasks = Tasks.Where(x => x.IsSelected).ToList();

            if (selectedTasks.Count == 0)
            {
                /*
                 * 措辞与界面蓝字（StatusText.SelectionScopeHint）同一套词："勾选"。
                 *
                 * 旧文案"请先选择需要解压的任务"是含糊的 —— 用户高亮了一行、心里已经算"选择了"，
                 * 而这条命令只认最左侧那一列的勾选（同一个误读 2026-09-22 在清理类命令上被抓过一次，
                 * 见 MainViewModel.ResolveCleanupTargets 的注释）。
                 */
                _dialogService.ShowWarning(
                    $"请先勾选要解压的任务（最左侧一列）。{Environment.NewLine}{Environment.NewLine}"
                    + $"列表里有 {Tasks.Count} 个任务，当前一个都没勾。在列表上按 Ctrl+A 可以全选。");
                return;
            }

            if (!_archiveEngine.IsAvailable)
            {
                /*
                 * 提示由 ToolLocator 现算（体检报告 §4 第 1 条）：
                 * 这条判定的口径是"**任一**引擎可用"，而默认优先级是 WinRAR(UnRAR) → 7-Zip ——
                 * 写死 7z 路径会把"其实只缺 UnRAR"的用户引去修一个没问题的目录。
                 * 两条期望路径与当前优先级顺序都由 ToolLocator 给出（外部工具路径只有它一个来源）。
                 */
                _dialogService.ShowError(ToolLocator.Default.DescribeNoEngineAvailable());
                AppendLog("ERROR", StatusText.SevenZipMissing);
                AppendLog("ERROR", ToolLocator.Default.DescribeAvailability());
                return;
            }

            _runOptions = runOptions;

            if (runOptions != null)
            {
                /*
                 * 本次选项进日志（规格 §9.2 硬要求⑥）：用户事后要能回答"这次为什么解到这里"。
                 * 逐任务那一行在 ExtractSingleTaskAsync 里写（带实际落点），这一行是本批的总纲。
                 */
                AppendLog("INFO", $"本次一键处理按「本次选项」执行（不写回设置）：{runOptions.Describe()}");

                if (!runOptions.IsPlacementValid)
                {
                    AppendLog(
                        "WARN",
                        "本次选项里选了「指定位置」但没填路径 —— 为了避免它被解释成「解压到压缩包所在目录」，" +
                        "本次落点**回落设置里的值**（其余两项照常生效）。");
                }
            }

            /*
             * ===== 解压前的提醒（用户 2026-09-22 需求第 8 条）=====
             *
             * 位置：**引擎可用性检查之后**（一个引擎都没有时该报的是「没有可用的解压引擎」，
             * 而不是"源目录里有些说明文件"）、**IsBusy = true 之前**（这是"还没动手"的最后一步：
             * 用户在这里选「先不处理」，这一批就是一个字节都没动过的干净状态）。
             *
             * 整批只弹一次，两段合并成同一个框（项目先例：多任务清理合并成 1 次确认）；
             * 两段都为空时**不弹** —— 空对话框只会训练用户闭眼点确定。
             */
            if (!await ConfirmBatchRemindersAsync(selectedTasks))
            {
                return;
            }

            IsBusy = true;
            IsStopping = false;
            _isExtracting = true;

            // 本批的密码失败登记从零开始：上一批的残留不能让这一批多弹一次提示。
            ClearPasswordFailures();

            // 同名冲突的记账同样从零开始：上一批答过的「全部覆盖」绝不许延续到这一批。
            ResetBatchConflictState();

            // 「完成后打开输出目录」整批只开一次；上一批开过不算这一批的（见 _outputFolderOpenedThisBatch）。
            ResetBatchOutputFolderState();

            // 手动密码只对**本次运行**有效（不变量 5：绝不落盘）：批开始时重新问一次、重新记一次。
            ResetManualBatchPassword();

            // 空间规划与本批记账清零（见 finally 里的说明：清在**批首**，自测才读得到这一批的证据）。
            ResetSpacePlanningState();

            try
            {
                try
                {
                    _operationCts?.Dispose();
                }
                catch
                {
                }

                _operationCts = new CancellationTokenSource();

                AppendLog("INFO", "开始批量解压");

                /*
                 * 手动密码出口（WinRAR 参考 §2 H 组 / §3 附注采纳项）。
                 *
                 * 位置：**动手之前**问一次。理由很实在 —— 密码本没命中、统一密码也不对时，
                 * 用户以前只能"改设置 → 重跑整批"，而重跑意味着已经解过的包再解一遍。
                 * 现在这一步先问，答了就当成本批所有任务的候选（排在空密码之后、密码本之前：
                 * 它是用户刚刚给出的信息，比密码本里的历史条目更"新"）。
                 *
                 * 三条纪律：
                 * ① **整批一次性**（_manualPasswordPrompted）—— 不做成每个包问一次，那正是 D-4 要避免的；
                 * ② **只对本次运行有效、绝不落盘**（不变量 5）—— 只是一个字段，不进设置、不进密码列表、不进日志；
                 * ③ **无 UI 宿主不弹窗、不死等** —— 判定见 PromptForManualBatchPasswordAsync。
                 */
                await PromptForManualBatchPasswordAsync(selectedTasks);

                int maxParallel = ResolveMaxParallel(selectedTasks, out bool fullSpeed);

                /*
                 * ===== 空间规划 + 空间门（用户 2026-09-22 需求第 1 / 2 条）=====
                 *
                 * 顺序：
                 * ① 按空间需求**从小到大**排序（用户点名的反例：一开始就去解 5G+6G，两个都跑不动）；
                 * ② 算出"按当前可用空间，最多能并行几个"，与用户选的档位一起写进日志；
                 * ③ 每个任务**启动之前**再判一次空间（不是只在计划时判一次）——
                 *    空间是随跑随变的，计划时的数字只能当建议（见 SpaceReservationLedger 的说明）。
                 *
                 * ⚠ 排序只影响**执行顺序**，不动任务列表本身：用户在列表里看到的顺序、
                 * 任务的 Index 都不变（改列表顺序会让"我刚才是第 3 个"这种对照失效）。
                 */
                ExtractionSchedulePlan plan = BuildSchedulePlan(selectedTasks, maxParallel);

                PrepareDangerModeForBatch(maxParallel);

                foreach (string line in plan.DescribeLines())
                {
                    AppendLog("INFO", line);
                }

                if (IsDangerModeActive)
                {
                    LogDangerModeArmed();
                }
                else if (!string.IsNullOrWhiteSpace(_dangerModeNotCoveredReason))
                {
                    LogDangerModeNotCovered();
                }

                _spaceLedger = new SpaceReservationLedger(
                    ProbeAvailableSpace(ResolveSpaceProbePath(selectedTasks)),
                    ReserveSpaceBytes);

                AppendLog("INFO", "空间账面：" + _spaceLedger.Describe());

                var runningTasks = new List<Task>();

                foreach (ScheduledExtractionItem item in plan.Ordered)
                {
                    ArchiveTask task = item.Task;

                    if (IsStopping || _operationCts.IsCancellationRequested)
                    {
                        AppendLog("WARN", "已停止后续任务，不再启动新的解压任务。");
                        break;
                    }

                    // 每个任务各自的"已经在队列里等过"标记（只写一次等待日志，不刷屏）。
                    bool queuedLogged = false;

                    // 等待有空闲并发位；期间若用户点了“停止后续”，不再为后面的任务等位。
                    while (runningTasks.Count >= maxParallel)
                    {
                        /*
                         * 「等待状态必须可见」（WinRAR 参考 §2 D 组采纳项）。
                         *
                         * 没有这一行时，被节流挡住的任务在界面上**完全看不出在等** ——
                         * 用户看到的是"点了开始，一半任务纹丝不动"，然后怀疑程序卡死
                         * （这正是本机历史上被投诉过的那类现象）。
                         * 一次等待只写一行，不刷屏；这一行同时是"该开全速了"的提示。
                         */
                        if (!queuedLogged)
                        {
                            queuedLogged = true;

                            AppendLog(
                                "INFO",
                                $"并发已满（{runningTasks.Count}/{maxParallel} 个任务正在跑），" +
                                $"「{task.FileName}」在队列里等一个空位。" +
                                (fullSpeed
                                    ? "（已开「全速」，本批不再节流。）"
                                    : "想让它立刻开跑：勾上主界面的「全速」；或点「停止后续」不再启动后面的任务。"));
                        }

                        Task finished = await Task.WhenAny(runningTasks);
                        runningTasks.Remove(finished);
                        await finished;

                        if (IsStopping || _operationCts.IsCancellationRequested)
                        {
                            break;
                        }
                    }

                    /*
                     * 关键修复（实测复现）：上面的 break 只跳出了"等并发位"的 while，
                     * 落到这里仍会把当前任务 Add 进去 —— 表现为"点了停止后续，另一个包照样跑成功了"，
                     * 违反不变量 9（停止后续只阻止**启动后续**，绝不能再启动新任务）。
                     * 所以在真正启动之前必须再查一次。
                     */
                    if (IsStopping || _operationCts.IsCancellationRequested)
                    {
                        AppendLog("WARN", "已停止后续任务，不再启动新的解压任务。");
                        break;
                    }

                    if (queuedLogged)
                    {
                        // 队列里等过了：说一句"轮到它了"，否则用户只看到"等待"没有下文。
                        AppendLog("INFO", $"「{task.FileName}」等到空位，开始解压。");
                    }

                    /*
                     * 空间门（**启动之前**判，用户 2026-09-22 需求第 1 条）。
                     *
                     * 判据里含"已经在跑的任务预留了多少"（账本），所以 5G+6G 这种组合里
                     * 第二个大包会在启动前就被拦下，而不是等它写到一半才报磁盘满。
                     * 拦下时**跳过它、继续看后面的**（后面的包可能更小、正好塞得下）——
                     * 全部排完再一次性报告哪些被跳过、各需要多少。
                     */
                    ScheduledTaskRuntime runtime = GetOrCreateRuntime(task);

                    SpaceGateDecision gate = _spaceLedger.TryReserve(
                        item.RequiredBytes,
                        item.Estimate.ReclaimableBytes);

                    if (!gate.Allowed)
                    {
                        MarkSpaceBlocked(task, item, gate);
                        continue;
                    }

                    runtime.ReservedBytes = gate.ProbeFailed ? 0L : item.RequiredBytes;
                    runtime.AvailableBeforeStart = _spaceLedger.AvailableBytes;

                    runningTasks.Add(RunScheduledTaskAsync(task, oneClickRun, runtime));
                }

                // 等待所有已启动的任务结束（包括“停止后续”后仍在运行的任务）。
                while (runningTasks.Count > 0)
                {
                    Task finished = await Task.WhenAny(runningTasks);
                    runningTasks.Remove(finished);
                    await finished;
                }

                AppendLog("INFO", "批量解压完成");

                /*
                 * **如实报告**被空间门跳过的任务（用户 2026-09-22 需求第 2 条：
                 * "全部排不下时如实报告哪些包因为空间不足被跳过、各自需要多少，不要静默跳过"）。
                 *
                 * 每一条都带具体数字与建议动作 —— 这一行往往是用户唯一能拿到的行动线索。
                 */
                ReportSpaceBlockedTasks();

                /*
                 * 询问次数写进日志：决策 D-4 要求"本批只问一次"，这一行就是它的证据 ——
                 * 用户与排障都能看到"这一批到底打断了几次"（选了「全部 X」之后不该再涨）。
                 */
                int conflictPrompts;

                lock (_conflictDecisionLock)
                {
                    conflictPrompts = _conflictPromptCount;
                }

                if (conflictPrompts > 0)
                {
                    AppendLog(
                        "INFO",
                        $"本批同名冲突询问共 {conflictPrompts} 次（选了「全部 X」之后不再询问）。");
                }

                /*
                 * 密码错误**合并成一次提示**（P0）。
                 * 逐个任务弹模态框时，50 个错包就是 50 次阻塞点击 —— 批量处理根本跑不动。
                 * 放在批次结束后：此时才谈得上"本批 N 个包"，也不会挡住正在跑的任务。
                 */
                await ShowPasswordFailuresSummaryAsync();
            }
            finally
            {
                try
                {
                    _operationCts?.Dispose();
                }
                catch
                {
                }

                _operationCts = null;

                _isExtracting = false;
                IsBusy = false;
                IsStopping = false;

                // 本次选项**只活这一批**：批结束就丢掉，下一批（哪怕是同一次运行里的下一轮续解）
                // 由调用方重新给一份 —— 这样"覆盖"永远不会悄悄延续到别的批次上。
                _runOptions = null;

                /*
                 * 空间规划与本批记账同样"只活这一批"：
                 * 上一批的账本必须丢掉（下一批的可用空间与任务集都变了）；
                 * 自测的临时开关也必须落回原状 —— 它只在一次自测里有效。
                 *
                 * ⚠ **_spaceRuntime / _refinedEstimates 刻意不在这里清**：自测要在这条批跑完之后
                 * 才去读逐任务的记账（删了多少、空间曲线怎么走），在这里清掉等于让自测永远拿不到证据。
                 * 它们由批**开始时**的 ResetSpacePlanningState 清 —— 与其它批级记账同一套时机。
                 */
                _spaceLedger = null;
                _dangerModeArmedForSelfTest = false;

                // 批级开关也要落回原状：它只对这一批有效（下一批会重新按当时的档位与凭证判定）。
                _dangerModeActiveThisBatch = false;
                _dangerModeNotCoveredReason = string.Empty;

                UpdateSummary();
            }
        }

        // ================================================================ 空间规划 + 危险模式

        /// <summary>
        /// 给界面算一次"按当前可用空间，最多能并行几个"（用户 2026-09-22 需求第 2 条）。
        ///
        /// <para>它与真正开跑时用的是**同一段实现**（<see cref="BuildSchedulePlan"/>），
        /// 所以界面上显示的建议与日志里那一刻的调度口径不会分叉 ——
        /// "界面写一个数、跑起来按另一个数"正是本项目最不能接受的那类形态。</para>
        ///
        /// <para>⚠ 它会 stat 每个源包（以及分卷的每一卷），所以调用方要放后台线程。</para>
        /// </summary>
        internal ExtractionSchedulePlan BuildSpaceAdvice(IReadOnlyList<ArchiveTask> tasks)
        {
            int requested = Math.Clamp(Settings.MaxParallelExtractCount, 1, ExtractionScheduler.ParallelCeiling);

            return BuildSchedulePlan(tasks ?? Array.Empty<ArchiveTask>(), requested);
        }

        /// <summary>
        /// 供界面显示的"当前空间档位"一句话（危险模式是否生效也在这句里说清）。
        /// </summary>
        internal string DescribeSpaceMode()
        {
            string danger = Settings.DangerousSpaceModeEnabled
                ? "危险模式：**已开启**（每个任务成功后立刻彻底删它的其余物，源包不可还原）"
                : "危险模式：关闭（其余物按默认档处理）";

            string stamp = DangerModeSelfTestStamp.Describe(Settings.DangerModeSelfTestStamp);

            /*
             * 开着但凭证盖不住当前档位时，这句话必须说出来 —— 它是界面上"当前空间档位"的权威一句，
             * 只写"已开启"会让人以为边解边删正在起作用。
             */
            string coverage = Settings.DangerousSpaceModeEnabled &&
                              !DangerModeSelfTestStamp.Covers(Settings.DangerModeSelfTestStamp, ResolveParallelCountForDisplay())
                ? "；⚠ 但自测凭证盖不住当前并发档，本批不生效（" + StatusText.DangerModeNotCoveredBySelfTest + "）"
                : string.Empty;

            return danger + (string.IsNullOrWhiteSpace(stamp) ? "；还没有自测凭证" : "；" + stamp) + coverage;
        }

        /// <summary>界面上那句"当前档位"用的并发数（与真正开跑时同一个口径：设置项 1–8）。</summary>
        private int ResolveParallelCountForDisplay()
        {
            return Math.Clamp(Settings.MaxParallelExtractCount, 1, MaxParallelExtractCountCeiling);
        }
        /// <summary>本批探测可用空间用的路径（一次定住：同一批里所有判断都对着同一块盘，数字才可比）。</summary>
        private string _spaceProbePath = string.Empty;

        /// <summary>
        /// 可用空间探测（可注入）。
        ///
        /// <para>为什么必须留这个口子：真机上没法把盘写成只剩 3 MiB，而"空间不足就不启动"这条
        /// 是本批最核心的行为之一 —— 只靠读代码是证明不了的。单测用它造一块假盘。</para>
        /// </summary>
        internal Func<string, long?>? SpaceProbeOverride { get; set; }

        /// <summary>
        /// 要保留的余量（可注入）。默认取 <see cref="SpaceGate.DefaultReserveBytes"/>（512 MiB）。
        /// 单测里不调小它，任何几十字节的样本都会被那 512 MiB 拦下 —— 那样测的就不是被测逻辑了。
        /// </summary>
        internal long? SpaceReserveOverride { get; set; }

        private long? ProbeAvailableSpace(string path)
        {
            return SpaceProbeOverride != null
                ? SpaceProbeOverride(path)
                : SpaceChecker.GetAvailableFreeSpace(path);
        }

        private long ReserveSpaceBytes => SpaceReserveOverride ?? SpaceGate.DefaultReserveBytes;

        /*
         * ===== 并发下的最终目录占位（见 ExtractSingleTaskAsync 里的调用点）=====
         *
         * 只记"还没落盘、但已经被某个正在跑的任务要用"的目录。任务收尾即释放 ——
         * 那一刻目录要么已经带着产物存在（后来的任务走既有的"已存在"冲突档），
         * 要么根本没建起来（后来的任务就该用它）。
         */
        private readonly HashSet<string> _claimedOutputDirectories = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _outputClaimLock = new();

        /// <summary>占位失败时的兜底尝试上限（与 <c>SafePathHelper.AutoRenameDirectoryPath</c> 同一个量级）。</summary>
        private const int MaxOutputClaimAttempts = 10000;

        /// <summary>
        /// 把一个"还不存在的最终目录"在进程内占下来；被别的正在跑的任务占了就换 <c>名字(1)</c>。
        ///
        /// <para><c>(i)</c> 的命名规则与 <see cref="SafePathHelper.AutoRenameDirectoryPath"/> 一致
        /// （同一个用户可见约定），区别只有一个：那里判"磁盘上有没有"，这里还要判"有没有被并发任务占下"——
        /// 被占下的目录此刻在磁盘上还不存在，所以那个方法看不见它。</para>
        /// </summary>
        private string ClaimOutputDirectory(string requestedPath, ArchiveTask task, ref string outputRedirectNote)
        {
            lock (_outputClaimLock)
            {
                string candidate = requestedPath;

                for (int attempt = 1; attempt <= MaxOutputClaimAttempts; attempt++)
                {
                    string full = SafePathHelper.GetFullPathSafe(candidate);

                    if (string.IsNullOrWhiteSpace(full))
                    {
                        return candidate;
                    }

                    bool claimedByAnotherTask = _claimedOutputDirectories.Contains(full);

                    // 磁盘上真出现了也算"占不到"（TOCTOU 的温和版本：两次系统调用之间别人建了）。
                    bool existsOnDisk = Directory.Exists(candidate);

                    if (!claimedByAnotherTask && !existsOnDisk)
                    {
                        _claimedOutputDirectories.Add(full);

                        if (!string.Equals(candidate, requestedPath, StringComparison.OrdinalIgnoreCase))
                        {
                            /*
                             * 与"目录已存在"那条路同一口径：实际落点与打算的落点不一致**不许静默**。
                             * 日志 + outputRedirectNote（会进任务详情的校验结论）+ task.OutputPath 回写，三处都要有。
                             */
                            string note =
                                $"原定输出目录 {requestedPath} 正被另一个正在解压的任务使用，本次实际输出到 {candidate}";

                            outputRedirectNote = string.IsNullOrWhiteSpace(outputRedirectNote)
                                ? note
                                : outputRedirectNote + "；" + note;

                            AppendLog(
                                "WARN",
                                $"{task.FileName}：同一个名字的包正在并行解压，为避免两个任务写进同一个目录，" +
                                $"本次落成 {candidate}（绝不合并、绝不覆盖）。");
                        }

                        return candidate;
                    }

                    try
                    {
                        string? parent = Path.GetDirectoryName(requestedPath);
                        string name = Path.GetFileName(requestedPath);

                        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name))
                        {
                            return candidate;
                        }

                        candidate = Path.Combine(parent, $"{name}({attempt})");
                    }
                    catch
                    {
                        return candidate;
                    }
                }

                return candidate;
            }
        }

        /// <summary>释放最终目录占位（任务收尾时调；没占过就是一次空操作）。</summary>
        private void ReleaseOutputDirectoryClaim(string? outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return;
            }

            string full = SafePathHelper.GetFullPathSafe(outputPath);

            if (string.IsNullOrWhiteSpace(full))
            {
                return;
            }

            lock (_outputClaimLock)
            {
                _claimedOutputDirectories.Remove(full);
            }
        }

        /// <summary>
        /// 批首清零空间规划与本批记账（账本、逐任务运行期记录、精确估算、被跳过的清单）。
        /// </summary>
        private void ResetSpacePlanningState()
        {
            _spaceLedger = null;
            _spaceProbePath = string.Empty;

            lock (_spaceRuntimeLock)
            {
                _spaceRuntime.Clear();
                _refinedEstimates.Clear();
            }

            _spaceBlockedTasks.Clear();
        }

        private ScheduledTaskRuntime GetOrCreateRuntime(ArchiveTask task)
        {
            lock (_spaceRuntimeLock)
            {
                if (!_spaceRuntime.TryGetValue(task, out ScheduledTaskRuntime? runtime))
                {
                    runtime = new ScheduledTaskRuntime();
                    _spaceRuntime[task] = runtime;
                }

                return runtime;
            }
        }

        /// <summary>
        /// 排一次执行计划：按空间需求从小到大排序 + 算建议并行数。
        ///
        /// <para>估算用**粗估**（只 stat 文件）而不是逐个列目录：50–200 个包的场景下，
        /// 为了排序去把每个包的目录都列一遍，等于在"还没开始解"之前先跑一遍整批。
        /// 真正的精确值在解压前的预检里算（那时本来就要列目录），并会在开工后调整预留。</para>
        /// </summary>
        private ExtractionSchedulePlan BuildSchedulePlan(IReadOnlyList<ArchiveTask> tasks, int requestedParallel)
        {
            _spaceProbePath = ResolveSpaceProbePath(tasks);

            return ExtractionScheduler.Build(
                tasks,
                SpaceEstimator.FromSourceFiles,
                ProbeAvailableSpace(_spaceProbePath),
                ReserveSpaceBytes,
                requestedParallel);
        }

        /// <summary>
        /// 本批探测哪块盘：优先归集目标目录（开了归集时产物最终都落那儿），
        /// 其次是第一个任务的输出目录，最后退回源包所在目录。
        /// </summary>
        private string ResolveSpaceProbePath(IReadOnlyList<ArchiveTask> tasks)
        {
            if (Settings.CollectResultsToDirectory && !string.IsNullOrWhiteSpace(Settings.CollectTargetDirectory))
            {
                return Settings.CollectTargetDirectory;
            }

            foreach (ArchiveTask task in tasks ?? Array.Empty<ArchiveTask>())
            {
                string path = ResolveSpaceProbePathForTask(task);

                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }

            return string.Empty;
        }

        private static string ResolveSpaceProbePathForTask(ArchiveTask task)
        {
            if (task == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(task.OutputPath))
            {
                return task.OutputPath;
            }

            return task.DirectoryPath;
        }

        private string ResolveSpaceProbePathOrBatch(ArchiveTask task)
        {
            string path = ResolveSpaceProbePathForTask(task);

            return string.IsNullOrWhiteSpace(path) ? _spaceProbePath : path;
        }

        /// <summary>
        /// 空间门拦下：任务**不启动**，状态落「磁盘空间不足」，原因里带具体数字与建议动作。
        ///
        /// <para>为什么用这个状态而不是「解压失败」：归档本身没有任何问题，连开始都没开始 ——
        /// 用户要做的是清空间 / 换盘 / 用危险模式，而不是去怀疑包坏了。</para>
        /// </summary>
        private void MarkSpaceBlocked(ArchiveTask task, ScheduledExtractionItem item, SpaceGateDecision gate)
        {
            MarkSpaceBlockedCore(
                task,
                gate.ToLogLine() + $"（{item.Estimate.Describe()}；依据：{item.Estimate.Basis}）",
                item.RequiredBytes,
                gate.AvailableBytes,
                gate.ShortfallBytes);
        }

        /// <summary>
        /// 空间门拦下的共用落点（启动前与解压前预检两条路都走它）：
        /// 状态、错误信息、日志、批末报告清单**一次写全**。
        /// </summary>
        private void MarkSpaceBlockedCore(
            ArchiveTask task,
            string message,
            long requiredBytes,
            long availableBytes,
            long shortfallBytes)
        {
            task.Status = StatusText.DiskSpaceInsufficient;
            task.ProgressText = StatusText.ProgressFailed;
            task.Operation = StatusText.OpExtract;
            task.ErrorMessage = message;
            task.EndTime = DateTime.Now;
            task.LastUpdatedTime = DateTime.Now;

            // 措辞用"未解压"而不是"未启动"：这一条同时覆盖两条路 —— 批调度里根本没开跑的那些，
            // 以及跑起来之后在解压前预检里被拦下的那些（那时引擎一个字节都还没写）。
            AppendLog("ERROR", $"空间不足，未解压：{task.FileName} —— {message}");

            _spaceBlockedTasks.Add((
                string.IsNullOrWhiteSpace(task.FileName) ? task.CurrentPath : task.FileName,
                requiredBytes,
                availableBytes,
                shortfallBytes));
        }

        /// <summary>
        /// 批末如实报告被空间门跳过的任务（不变量：不许静默跳过）。
        /// 一条汇总 + 逐个点名"需要多少、差多少、当时可用多少"。
        /// </summary>
        private void ReportSpaceBlockedTasks()
        {
            if (_spaceBlockedTasks.Count == 0)
            {
                return;
            }

            AppendLog(
                "WARN",
                $"空间不足，本批跳过 {_spaceBlockedTasks.Count} 个任务（一个字节都没动过）：");

            foreach ((string name, long required, long available, long shortfall) in _spaceBlockedTasks)
            {
                AppendLog(
                    "WARN",
                    $"  跳过：{name} —— 需要 {TaskSpaceEstimate.FormatSize(required)}，"
                    + $"当时可用 {(available < 0 ? "未知" : TaskSpaceEstimate.FormatSize(available))}，"
                    + $"差 {TaskSpaceEstimate.FormatSize(shortfall)}");
            }

            AppendLog(
                "WARN",
                "处理办法：清理「其余物」腾空间 / 换一个空间更大的输出盘 / "
                + "把「最大并发解压数」调小，或在通过自测后改用危险模式（边解边彻底删其余物）。");
        }

        /// <summary>
        /// 跑一个任务：解压 → （危险模式）立刻彻底删其余物 → 释放预留 + 刷新可用空间。
        ///
        /// <para>⚠ 顺序是刻意的：删除发生在**任务收尾之后、释放预留之前**。
        /// 这样"危险模式回收回来的空间"会出现在下一个任务的空间门判断里（这正是它能解决空间不够的原因），
        /// 而账本上的预留仍然按"这些字节还在盘上"来算，绝不会提前把它许给别的任务。</para>
        /// </summary>
        private async Task RunScheduledTaskAsync(ArchiveTask task, bool oneClickRun, ScheduledTaskRuntime runtime)
        {
            try
            {
                await ProcessExtractTaskAsync(task, oneClickRun);

                await RunDangerModePurgeAsync(task, runtime);
            }
            finally
            {
                runtime.AvailableAfterFinish = ProbeAvailableSpace(ResolveSpaceProbePathOrBatch(task))
                                               ?? SpaceReservationLedger.UnknownAvailable;

                SpaceReservationLedger? ledger = _spaceLedger;

                if (ledger != null)
                {
                    ledger.Release(runtime.ReservedBytes);
                    ledger.RefreshAvailable(runtime.AvailableAfterFinish >= 0 ? runtime.AvailableAfterFinish : null);
                    runtime.ReservedBytes = 0;
                }

                LogSelfTestProgress(task);

                UpdateSummary();
            }
        }

        /// <summary>
        /// 自测的**逐文件进度**（用户明确要求"自测要有进度与结果：哪个文件、通过/失败、为什么"）。
        /// 每跑完一个样本就写一行，而不是憋到最后一次性吐出十条。
        /// </summary>
        private void LogSelfTestProgress(ArchiveTask task)
        {
            DangerModeSelfTestRecorder? recorder = _selfTestRecorder;

            if (recorder == null)
            {
                return;
            }

            int total = recorder.Samples.Count;
            int done = recorder.Samples.Count(sample => !string.IsNullOrWhiteSpace(sample.Status) &&
                                                        sample.EndTime != null);

            ScheduledTaskRuntime runtime = GetOrCreateRuntime(task);

            AppendLog(
                "INFO",
                $"自测进度 {Math.Min(done, total)}/{total}：{task.FileName} —— "
                + $"终态「{task.Status}」，输出校验{(task.IsOutputVerified ? "通过" : "未通过")}，"
                + $"其余物{(runtime.RestPurged ? $"已彻底删除（释放 {TaskSpaceEstimate.FormatSize(runtime.PurgedBytes)}）" : "未删除")}"
                + (string.IsNullOrWhiteSpace(runtime.PurgeNote) ? string.Empty : $"，说明：{runtime.PurgeNote}"));
        }

        /// <summary>
        /// 危险模式的**唯一触发点**：任务成功了就立刻把它自己的其余物彻底删掉。
        ///
        /// <para>门槛全在 <see cref="RestItemPurger"/> 里（终态必须是「解压成功」+ 校验通过 + 未取消 +
        /// 路径是本次记下来的那一条 + 落在自己的输出根之内）——这里只负责"什么时候试"和"怎么记账"。
        /// **失败 / 部分完成 / 取消的任务一个字节都不会被删**（不变量 1 的红线）。</para>
        /// </summary>
        private async Task RunDangerModePurgeAsync(ArchiveTask task, ScheduledTaskRuntime runtime)
        {
            if (!IsDangerModeActive)
            {
                return;
            }

            bool cancelled = IsStopping || _operationCts?.IsCancellationRequested == true;

            RestPurgeOutcome outcome;

            try
            {
                // 磁盘活（量大小 + 删目录）→ 放后台，别占住 UI 线程。
                outcome = await Task.Run(() => new RestItemPurger().Purge(task, cancelled));
            }
            catch (Exception ex)
            {
                // 这一层不该抛（RestItemPurger 内部全部收敛成结论），但"删东西"这件事
                // 绝不允许把已经成功的解压拖成异常：结论落成"没删成"，内容物不受影响。
                outcome = new RestPurgeOutcome
                {
                    Message = $"{task.FileName}：危险模式删除其余物时出现意外错误：{ex.Message}"
                };
            }

            foreach (string line in outcome.LogLines)
            {
                AppendLog("INFO", line);
            }

            if (outcome.Succeeded)
            {
                runtime.RestPurged = true;
                runtime.PurgedBytes = outcome.FreedBytes;

                AppendLog("WARN", outcome.Message + "（这一步不可逆：源包与本任务的中间件已经不在回收站里）");
                return;
            }

            runtime.PurgeNote = outcome.Message;

            AppendLog(outcome.Attempted ? "ERROR" : "INFO", outcome.Message);
        }

        /// <summary>
        /// 批首定一次"本批危险模式到底生不生效"。
        ///
        /// <para>两条独立条件：开关开着（或正在自测），**并且**自测凭证盖得住本批的并发档。
        /// 后者是用户那条协议的直接推论 —— 自测是"拿并发数 × 2 个文件真跑一遍"，
        /// 结论只对它跑过的那一档成立；档位调高之后拿旧凭证开这个模式等于**没测过就用了**，
        /// 而这条路上源包会被永久删除，代价不可逆。</para>
        ///
        /// <para>盖不住时**不是**偷偷关掉开关，而是本批不生效 + 在日志与界面横幅上说明白：
        /// 静默降级会让人以为"边解边删"在起作用，于是放心把盘塞满 —— 那才是真正的危险。</para>
        /// </summary>
        private void PrepareDangerModeForBatch(int maxParallel)
        {
            _dangerModeActiveThisBatch = false;
            _dangerModeNotCoveredReason = string.Empty;

            // 自测本身就是那次"证明"：它按自己的档位跑，不再要凭证（凭证正是它要产出的东西）。
            if (_dangerModeArmedForSelfTest)
            {
                _dangerModeActiveThisBatch = true;
                return;
            }

            if (!Settings.DangerousSpaceModeEnabled)
            {
                return;
            }

            string? stamp = Settings.DangerModeSelfTestStamp;

            if (!DangerModeSelfTestStamp.Covers(stamp, maxParallel))
            {
                _dangerModeNotCoveredReason = DangerModeSelfTestStamp.DescribeCoverage(stamp, maxParallel);
                return;
            }

            _dangerModeActiveThisBatch = true;
        }

        /// <summary>危险模式开着、但凭证盖不住本批并发档时的那条日志（本批一个字节都不删）。</summary>
        private void LogDangerModeNotCovered()
        {
            AppendLog(
                "WARN",
                "⚠ " + StatusText.DangerModeNotCoveredBySelfTest
                + " 本批所有任务按**普通档**执行：其余物照常生成（进回收站，可还原）。");

            AppendLog("WARN", "原因：" + _dangerModeNotCoveredReason);
        }

        /// <summary>危险模式开启时在批首写一条"当前处于什么档位"的日志（可追溯）。</summary>
        private void LogDangerModeArmed()
        {
            bool selfTest = _dangerModeArmedForSelfTest;

            AppendLog(
                "WARN",
                $"⚠ {StatusText.DangerModeName} 已生效"
                + (selfTest ? "（**自测**：这一批是为了验证协议，跑完就恢复原状）" : string.Empty)
                + "：每个任务在「内容物定稿 + 输出校验通过 + 未取消」之后，会立刻把它自己的其余物"
                + "（源包 + 中间件）**彻底删除**，不进回收站、无法还原。"
                + "失败 / 部分完成 / 取消的任务一个字节都不删。");

            foreach (string risk in StatusText.DangerModeRiskLines)
            {
                AppendLog("WARN", "危险模式风险：" + risk);
            }

            string stamp = DangerModeSelfTestStamp.Describe(Settings.DangerModeSelfTestStamp);

            AppendLog(
                "INFO",
                string.IsNullOrWhiteSpace(stamp)
                    ? "危险模式当前**没有**自测凭证（设置里手改开的会被自动关回去）。"
                    : "危险模式自测凭证：" + stamp);
        }

        /// <summary>把解压前那一遍 list 算出来的**精确**空间需求记下来（自测的空间曲线要用）。</summary>
        private void RecordRefinedEstimate(ArchiveTask task, TaskSpaceEstimate estimate)
        {
            if (task == null || estimate == null)
            {
                return;
            }

            lock (_spaceRuntimeLock)
            {
                _refinedEstimates[task] = estimate;
            }
        }

        private TaskSpaceEstimate? TryGetRefinedEstimate(ArchiveTask task)
        {
            lock (_spaceRuntimeLock)
            {
                return _refinedEstimates.TryGetValue(task, out TaskSpaceEstimate? estimate)
                    ? estimate
                    : null;
            }
        }

        /// <summary>
        /// 拿开工时申请的预留与精确峰值对一次账：不够就加、多了就还。
        /// 加不上（盘已经不够了）时按「磁盘空间不足」处理 —— **这一步必须在真正写盘之前**。
        /// </summary>
        private SpaceGateDecision ReconcileReservation(ArchiveTask task, TaskSpaceEstimate refined)
        {
            SpaceReservationLedger? ledger = _spaceLedger;

            if (ledger == null)
            {
                // 没有账本（例如从别处直接调解压管线）：退化成一次直接判断，不假装知道并发情况。
                return SpaceGate.Check(
                    refined.PeakBytes,
                    ProbeAvailableSpace(ResolveSpaceProbePathOrBatch(task)),
                    ReserveSpaceBytes,
                    0,
                    refined.ReclaimableBytes);
            }

            ScheduledTaskRuntime runtime = GetOrCreateRuntime(task);
            SpaceGateDecision decision = ledger.Adjust(
                runtime.ReservedBytes,
                refined.PeakBytes,
                refined.ReclaimableBytes);

            if (decision.Allowed && !decision.ProbeFailed)
            {
                runtime.ReservedBytes = refined.PeakBytes;
            }

            return decision;
        }

        /// <summary>
        /// **危险模式自测**（用户 2026-09-22 明确要求的协议）。
        ///
        /// <para>「拿 2×并发数 个文件跑一次，自测通过才允许开启」——这里就是那个"跑一次"：
        /// 真的走一遍解压管线（含空间门、定稿、校验、危险模式删除），
        /// 然后把证据交给 <see cref="DangerModeSelfTestProtocol.Evaluate"/> 判定。</para>
        ///
        /// <para><b>为什么用它自己那条批而不是另写一条精简流程</b>：自测要证明的正是"真跑起来会怎样"。
        /// 另写一条"简化版"只能证明简化版没问题 —— 那是最典型的自欺。</para>
        ///
        /// <para>三件事保证不越界：① 只对传进来的候选（= 用户勾选的任务）动手；
        /// ② 跑完把勾选状态**原样还原**；③ 危险模式只是**临时**打开（<c>_dangerModeArmedForSelfTest</c>），
        /// 设置文件一个字节都不写。</para>
        /// </summary>
        /// <param name="candidates">候选任务（调用方传"当前勾选的任务"）。</param>
        internal async Task<DangerModeSelfTestVerdict> RunDangerModeSelfTestAsync(IReadOnlyList<ArchiveTask> candidates)
        {
            if (_isExtracting)
            {
                return DangerModeSelfTestProtocol.Evaluate(new DangerModeSelfTestEvidence
                {
                    BlockedReason = "当前正在批量解压，先等它跑完再自测"
                });
            }

            int parallel = Math.Clamp(Settings.MaxParallelExtractCount, 1, ExtractionScheduler.ParallelCeiling);
            int required = DangerModeSelfTestProtocol.RequiredSampleSize(parallel);

            if (!DangerModeSelfTestProtocol.CheckPreconditions(Settings, out string precondition))
            {
                AppendLog("ERROR", "危险模式自测未开始：" + precondition);

                return DangerModeSelfTestProtocol.Evaluate(new DangerModeSelfTestEvidence
                {
                    ParallelCount = parallel,
                    RequiredSampleSize = required,
                    BlockedReason = precondition
                });
            }

            IReadOnlyList<ArchiveTask> samples =
                DangerModeSelfTestProtocol.SelectSamples(candidates, required);

            if (samples.Count < required)
            {
                string why =
                    $"自测需要 {required} 个可解压的任务（当前并发档 {parallel} × {DangerModeSelfTestProtocol.SampleMultiplier}），"
                    + $"当前勾选的任务里只凑得出 {samples.Count} 个 —— 先多勾几个，"
                    + "或者把「最大并发解压数」调小（并发越小，要求的样本越少）。";

                AppendLog("ERROR", "危险模式自测未开始：" + why);

                return DangerModeSelfTestProtocol.Evaluate(new DangerModeSelfTestEvidence
                {
                    ParallelCount = parallel,
                    RequiredSampleSize = required,
                    BlockedReason = why
                });
            }

            AppendLog(
                "INFO",
                $"危险模式自测开始：并发 {parallel}，按协议要跑 {required} 个文件，实际挑了 {samples.Count} 个"
                + "（从需求最小的开始）。" + StatusText.DangerModeSelfTestWarning);

            var recorder = new DangerModeSelfTestRecorder
            {
                ParallelCount = parallel,
                RequiredSampleSize = required
            };

            recorder.Samples.AddRange(samples);

            // 勾选状态快照：自测只处理样本，跑完**原样还原**（绝不顺手改用户的勾选）。
            var selectionSnapshot = Tasks.ToDictionary(task => task, task => task.IsSelected);

            _selfTestRecorder = recorder;
            _dangerModeArmedForSelfTest = true;

            try
            {
                foreach (ArchiveTask task in Tasks)
                {
                    task.IsSelected = samples.Contains(task);
                }

                // 自测走**地基路径**（oneClickRun = false）：源包处理在定稿那一刻就做，
                // 不牵扯"续解链结束再补搬"那套延期语义 —— 自测要验的是危险模式本身。
                await StartExtractCoreAsync(oneClickRun: false, runOptions: null);
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", $"危险模式自测执行时出现意外错误：{ex.Message}");
            }
            finally
            {
                foreach (KeyValuePair<ArchiveTask, bool> pair in selectionSnapshot)
                {
                    pair.Key.IsSelected = pair.Value;
                }

                _dangerModeArmedForSelfTest = false;
                _selfTestRecorder = null;
            }

            DangerModeSelfTestVerdict verdict = DangerModeSelfTestProtocol.Evaluate(BuildSelfTestEvidence(recorder));

            foreach (string line in verdict.StepLines)
            {
                AppendLog("INFO", "自测结果 —— " + line);
            }

            foreach (string reason in verdict.FailureReasons)
            {
                AppendLog("ERROR", "自测不通过：" + reason);
            }

            AppendLog(verdict.Passed ? "WARN" : "ERROR", verdict.Summary);

            return verdict;
        }

        /// <summary>把运行期记账翻成自测证据（逐文件：成功 / 校验 / 其余物已删 / 空间曲线）。</summary>
        private DangerModeSelfTestEvidence BuildSelfTestEvidence(DangerModeSelfTestRecorder recorder)
        {
            var steps = new List<DangerModeSelfTestStep>();

            foreach (ArchiveTask task in recorder.Samples)
            {
                ScheduledTaskRuntime runtime = GetOrCreateRuntime(task);
                TaskSpaceEstimate? refined = TryGetRefinedEstimate(task);

                steps.Add(new DangerModeSelfTestStep
                {
                    DisplayName = string.IsNullOrWhiteSpace(task.FileName) ? task.CurrentPath : task.FileName,
                    TaskPath = task.CurrentPath,
                    ExtractSucceeded = string.Equals(task.Status, StatusText.ExtractSuccess, StringComparison.Ordinal),
                    Verified = task.IsOutputVerified,
                    RestPurged = runtime.RestPurged,
                    PurgedBytes = runtime.PurgedBytes,
                    AvailableBeforeStart = runtime.AvailableBeforeStart,
                    AvailableAfterFinish = runtime.AvailableAfterFinish,
                    RequiredBytes = refined?.PeakBytes ?? 0L,
                    ContentBytes = refined?.ContentBytes ?? 0L,
                    FailureReason = string.IsNullOrWhiteSpace(runtime.PurgeNote) ? string.Empty : runtime.PurgeNote
                });
            }

            return new DangerModeSelfTestEvidence
            {
                ParallelCount = recorder.ParallelCount,
                RequiredSampleSize = recorder.RequiredSampleSize,
                Steps = steps
            };
        }

        private async Task ProcessExtractTaskAsync(ArchiveTask task, bool oneClickRun)
        {
            var taskCts = new CancellationTokenSource();
            TrackRunningTask(taskCts);

            try
            {
                await ExtractSingleTaskAsync(task, taskCts.Token, oneClickRun);
            }
            catch (OperationCanceledException)
            {
                task.Status = StatusText.Cancelled;
                task.Operation = StatusText.OpWaiting;
                task.ProgressText = StatusText.Cancelled;
                task.ErrorMessage = "任务已取消";
                task.EndTime = DateTime.Now;
                task.ElapsedText = task.StartTime.HasValue
                    ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                    : "-";
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("WARN", $"任务已取消：{task.FileName}");
            }
            catch (Exception ex)
            {
                task.Status = StatusText.UnknownError;
                task.ErrorMessage = ex.Message;
                task.Operation = StatusText.OpWaiting;
                task.ProgressText = StatusText.ProgressFailed;
                task.EndTime = DateTime.Now;
                task.ElapsedText = task.StartTime.HasValue
                    ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                    : "-";
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("ERROR", $"任务失败：{task.FileName}，{ex.Message}");
            }
            finally
            {
                UntrackRunningTask(taskCts);

                try
                {
                    taskCts.Dispose();
                }
                catch
                {
                }

                // 释放"最终目录占位"：这一刻目录要么已经带着产物存在（后来的任务走既有的冲突档），
                // 要么根本没建起来（后来的任务就该用它）。
                ReleaseOutputDirectoryClaim(task.OutputPath);

                UpdateSummary();
            }
        }

        private async Task ExtractSingleTaskAsync(ArchiveTask task, CancellationToken cancellationToken, bool oneClickRun)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (task == null)
            {
                return;
            }

            // 每个任务开始：清掉"这一个"的冲突记账（"全部 X"是本批级的，不清）。
            ResetTaskConflictState();

            /*
             * 上一轮的"提示类"结论先清掉。
             *
             * 为什么必须清：任务对象是**复用**的（同一个包重试、或用户再点一次），
             * 上一轮算出来的"本包含 N 个可执行文件""路径过长"如果不擦掉，
             * 这一轮换了密码/换了输出目录之后会继续挂着 —— 用户看到的是一句**已经不成立**的结论。
             * 它们是"提示"不是状态，所以只重置这两个字段，不碰 Status / ErrorMessage（那是下面按流程写的）。
             */
            task.DangerousEntriesWarning = string.Empty;
            task.PathLengthWarning = string.Empty;

            /*
             * ===== 不变量 11 的**第一道**（也是唯一收口的那一道）：源文件变化 = 立刻停下 =====
             *
             * 位置刻意放在本方法**最前面**，理由有三条，缺一条都会漏：
             * ① 这里是所有解压入口的唯一收口（一键处理 / 手动「只解压」/ 单包重试 / 危险模式自测）；
             * ② 必须在**任何引擎调用之前** —— 下面的"分卷缺失"里会请引擎帮忙列目录、
             *    "格式未知"那条路也会让引擎试列一次，一旦引擎碰过这个包，
             *    后面再拦就已经"用过旧识别结果"了（不变量 11 要防的正是这个）；
             * ③ 必须在**任何写盘之前**：此刻还没有暂存目录、没有输出目录、没有其余物，
             *    拦下来就是"什么都没发生"（不变量 1 的红线在这种情形下照旧成立）。
             *
             * 没有基准的老任务在这里补拍（见 EnsureSourceSnapshot），不会被误拦。
             */
            EnsureSourceSnapshot(task);

            if (StopIfSourceChanged(task))
            {
                return;
            }

            /*
             * 分卷缺失时**不许开始**（AGENTS.md §6 第 7 条）。
             * 理由：分卷包里每一卷都是必需的数据片，缺一卷 7z 必然失败，
             * 让它跑一遍只会浪费用户时间、还可能留下半截输出目录；
             * 直接说清"缺哪几个卷"才是用户能行动的信息。
             *
             * 「我手动指定缺失卷所在目录」（WinRAR 参考 §2 G 组 / §3 第 2 条采纳项）：
             * 先给用户**一次**补救机会 —— 卷常常就散在隔壁文件夹或另一个盘，
             * 选中那个目录后重新归组再判定。
             * ⛔ 不变量 7 **一个字都不放松**：重新归组之后仍然缺，就仍然**不启动**。
             * 出口只是"帮你把卷找齐"，绝不是"缺卷也允许开始"。
             */
            if (task.IsVolumeGroup && !task.IsVolumeComplete)
            {
                bool repaired = await TryRepairMissingVolumesAsync(task, cancellationToken);

                if (!repaired)
                {
                    task.Status = StatusText.VolumeMissing;
                    task.ErrorMessage = string.IsNullOrWhiteSpace(task.VolumeInfoText)
                        ? "分卷不完整，缺少分卷"
                        : task.VolumeInfoText;

                    AppendLog("ERROR", $"分卷缺失，未开始解压：{task.FileName}，{task.ErrorMessage}");
                    return;
                }

                /*
                 * 用户刚把缺的卷找回来了：基准必须**跟着重拍**（不变量 11 与这条补救出口的和解）。
                 *
                 * 不重拍的话，新找回来的那一卷不在快照里，比对时它会被算成"新出现的文件" →
                 * 用户按提示把卷补齐了，反而被"源文件已变化"拦下，而且再补多少次都一样 ——
                 * 那句提示就成了死循环。补齐分卷是**改变源文件组**的正当操作，
                 * 所以基准从这里重新开始（与"重新扫描"同一条道理）。
                 */
                task.CaptureSourceSnapshot();

                AppendLog("INFO", $"{task.FileName}：分卷已补齐，源文件快照重新记录（{task.VolumeCount} 卷）。");
            }

            bool tryExtractUnknown = Settings.UnknownFormatAction == "TryExtract";

            if (!task.IsArchive || task.DetectedFormat == "Unknown")
            {
                if (tryExtractUnknown)
                {
                    AppendLog("WARN", $"格式未知：{task.FileName}，已开启“尝试解压未知格式”，继续尝试。");
                }
                else
                {
                    /*
                     * 魔数说不认识，**不等于"它不是压缩包"** —— 只说明文件头不在我的签名表里。
                     * 基线里早就写下了正确原则：最终以 7-Zip 的结论为准。
                     *
                     * 所以这里让引擎亲自试一次列目录（只对用户勾选的任务做，代价是一次进程调用）：
                     *   · 列得出来 → 就是能解的容器（自解压安装器、签名表里没有的格式都算），继续处理；
                     *   · 列不出来 → 才跳过，并把"7-Zip 也打不开"写进原因，而不是含糊的"格式未知"。
                     */
                    ArchiveListResult probe = await _archiveEngine.ListAsync(
                        ArchiveRequest.For(task.CurrentPath, string.Empty),
                        cancellationToken);

                    if (probe.Success && probe.FileCount > 0)
                    {
                        task.IsArchive = true;
                        task.EngineVerdict = $"{_archiveEngine.DisplayName} {_archiveEngine.Version} 能打开：{probe.FileCount} 个文件";

                        AppendLog("INFO", $"{task.FileName}：文件头不在签名表里，但 7-Zip 能打开（{probe.FileCount} 个文件），继续处理。");
                    }
                    else
                    {
                        task.Status = StatusText.Skipped;
                        task.Operation = StatusText.OpSkip;
                        task.ProgressText = StatusText.ProgressCompleted;
                        task.ErrorMessage = string.IsNullOrWhiteSpace(probe.Message)
                            ? "不是压缩包：7-Zip 也打不开"
                            : $"不是压缩包：7-Zip 打不开（{probe.Message}）";

                        AppendLog("WARN", $"跳过：{task.FileName} —— {task.ErrorMessage}");
                        return;
                    }
                }
            }

            if (!File.Exists(task.CurrentPath))
            {
                task.Status = StatusText.ExtractFailed;
                task.Operation = StatusText.OpWaiting;
                task.ProgressText = StatusText.ProgressCompleted;
                task.ErrorMessage = "文件不存在";
                AppendLog("ERROR", $"文件不存在：{task.CurrentPath}");
                return;
            }

            task.StartTime = DateTime.Now;
            task.EndTime = null;
            task.ElapsedText = "-";
            task.Operation = StatusText.OpPrepare;
            task.Status = StatusText.WaitingExtract;
            task.ProgressText = StatusText.ProgressProcessing;
            task.ErrorMessage = string.Empty;
            task.LastUpdatedTime = DateTime.Now;

            var extractOptions = new ExtractOptions
            {
                ExtractToOriginalDirectory = Settings.ExtractToOriginalDirectory,
                CustomOutputDirectory = SelectedOutputDirectory,
                KeepArchiveNameFolder = Settings.KeepArchiveNameFolder,
                TestBeforeExtract = Settings.TestBeforeExtract,
                OverwriteMode = Settings.OverwriteMode,
                UseGlobalPassword = Settings.UseGlobalPasswordForAllTasks,
                GlobalPassword = GlobalPassword,
                TryPasswordList = true,
                TryEmptyPasswordFirst = Settings.TryEmptyPasswordFirst,
                CancelOnFirstSuccess = true,
                TryExtractUnknownFormat = tryExtractUnknown,
                MaxParallelExtractCount = Settings.MaxParallelExtractCount
            };

            extractOptions.Normalize();

            /*
             * 「本次选项」的运行期覆盖（规格 §9.2 硬要求①/⑥）。
             *
             * 位置刻意放在**这里**：ExtractOptions 是落点推导的入口，覆盖写在它上面，
             * 下面那条唯一的推导链（BuildOutputPath → OutputPlacement.FromLegacyFlags →
             * ResolveDestinationDirectory）一个字都不用改 —— 面板不新增第二条拼路径的实现。
             * 内层包（IsContinuationTask）的落点由父任务给定，覆盖对它没有影响。
             */
            if (RunOptions != null)
            {
                RunOptions.ApplyTo(extractOptions);

                // 任务上留一份"为什么落这儿"（§9.2 硬要求⑥）：失败清单第二级与「复制任务信息」读它。
                task.RunOptionsNote = RunOptions.Describe();
            }

            /*
             * 落点模式（契约 §1.1 的四种，由旧的三个设置项翻译过来）。
             * 定稿时要靠它决定其余物集中到哪：模式 B（解压到当前目录）下目标目录就是源目录本身，
             * 一个目录里几十上百个包会共用它，所以其余物再套一层包基名（决策 D-2，由 ResultFinalizer 实现）。
             */
            OutputPlacementMode placementMode = OutputPlacement.FromLegacyFlags(
                extractOptions.ExtractToOriginalDirectory,
                extractOptions.KeepArchiveNameFolder,
                extractOptions.CustomOutputDirectory);

            /*
             * 场景 B 塌缩（规格 §3.3）：111\222\名字\名字.rar → 产物落 111\222\名字\内容物。
             *
             * 「包基名 == 所在目录名」是纯字符串判断（不碰磁盘）；成立之后才需要**扫一次目录**，
             * 确认里面没有别的包。扫描是磁盘活：用户场景里一个目录可能有几百个条目、网络盘更慢，
             * 所以必须在后台线程上跑（AGENTS.md / 任务硬约束：UI 线程只更新状态）。
             *
             * 只对**最外层源包**做：续解出来的内层包落点由父任务给定（见 BuildOutputPath 的早退分支），
             * 这层规则对它没有意义。
             */
            bool collapseRepeatedFolderLayer = Settings.CollapseRepeatedFolderLayer;
            bool sourceDirectoryContainsOnlyThisArchive = false;

            if (collapseRepeatedFolderLayer
                && !task.IsContinuationTask
                && SourceFolderScanService.IsRepeatedFolderNameCandidate(task.CurrentPath))
            {
                SourceFolderScanResult scan = await Task.Run(
                    () => SourceFolderScanService.Inspect(task.CurrentPath),
                    cancellationToken);

                sourceDirectoryContainsOnlyThisArchive = scan.ContainsOnlyThisArchive;

                // 塌缩会少一层目录，这件事必须让用户看得见（日志里说清为什么）。
                AppendLog(scan.ContainsOnlyThisArchive ? "INFO" : "WARN", $"{task.FileName}：{scan.Message}");
            }

            // 解压前算出来的"打算输出到哪"。它只是一个提议：下面可能因为目录已存在被改名。
            // 续解出来的内层包由 PathService.BuildOutputPath 直接给出**父任务那一个**最终目录（不再套一层）。
            string requestedOutputPath = _pathService.BuildOutputPath(
                task,
                extractOptions,
                collapseRepeatedFolderLayer,
                sourceDirectoryContainsOnlyThisArchive);
            string outputPath = requestedOutputPath;
            string outputRedirectNote = string.Empty;

            /*
             * 落点算不出来时必须**当场停下**（必修项，2026-09-21）。
             *
             * 旧实现会把空的"自定义输出目录"回落成程序安装目录，于是用户的文件被解进 exe 所在的那个目录 ——
             * 既找不到、又可能污染程序自己的 data。现在 BuildOutputPath 返回空串，
             * 这里给出明确状态与一句话原因，让用户去设置里选一个输出目录。
             */
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                task.Status = StatusText.ExtractFailed;
                task.ProgressText = StatusText.ProgressFailed;
                task.ErrorMessage = "输出目录无效（未设置或不可用），请在设置里选择输出目录后重试";
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("ERROR", $"{task.FileName}：{task.ErrorMessage}");
                return;
            }

            /*
             * 落点是不是"源包自己的目录"（模式 B「解压到压缩包所在目录」/ 场景 B 塌缩后的
             * 111\222\名字）。这条事实决定"目录已存在且非空就改名 xxx(1)"要不要让开，
             * 理由见下面那个分支上的说明；判定本身只有一处实现（OutputPlacement）。
             */
            bool landsInSourceDirectory = OutputPlacement.LandsInSourceDirectory(task.CurrentPath, outputPath);

            try
            {
                /*
                 * 「输出目录已存在且非空 → 自动改名成 xxx(1)」只对**最外层源包**做。
                 *
                 * 为什么续解出来的内层包必须跳过这一步：它的落点是父任务**正在用**的那个目录
                 * （内容物 + 其余物都在里面），对它改名等于把第二层解到旁边的 xxx(1) 去 ——
                 * 那正是用户抱怨的"多弄了四个文件夹、文件一多根本分不清"。
                 * 同名冲突也不会因此丢东西：内层产物进目录时由定稿搬运的 AutoRename 兜住（绝不覆盖）。
                 */
                if (task.IsContinuationTask)
                {
                    AppendLog(
                        "INFO",
                        $"{task.FileName}：内层包，产物归入父任务输出目录 {outputPath}（不再另建目录）。");
                }
                else if (landsInSourceDirectory)
                {
                    /*
                     * 落点就是**源包所在目录**（模式 B「解压到压缩包所在目录」，或场景 B 塌缩后的
                     * 111\222\名字）→ 上面那条"已存在且非空就改名 xxx(1)"必须让开。
                     *
                     * 为什么：那个目录**必然非空** —— 源包自己就躺在里面。照旧规则一改名，
                     * 用户选的就地整理 / 塌缩当场被抵消，产物落到旁边的 名字(1)\，
                     * 正是这一轮要根治的"凭空多一层目录"。
                     *
                     * 让开之后会不会和既有文件混在一起：不会丢东西。定稿搬运对同名条目一律
                     * AutoRename（绝不覆盖），而且这条规则的本意是"别把产物倒进一个已有内容的目录"，
                     * 可这里那个目录**本来就是这次要整理的目标**。
                     */
                    AppendLog(
                        "INFO",
                        $"{task.FileName}：落点就是源包所在目录 {outputPath}（就地整理 / 已塌缩重复层），不套用“目录已存在就改名”的规则。");
                }
                else if (!string.IsNullOrWhiteSpace(outputPath) &&
                    Directory.Exists(outputPath) &&
                    Directory.EnumerateFileSystemEntries(outputPath).Any())
                {
                    /*
                     * 目标目录已存在且非空 = 第一次撞上"目标已存在"的冲突。
                     *
                     * ⚠ 这里以前**无条件**自动改名成 xxx(1) —— 于是设置里那一档「询问」在落点上
                     * 从来没有兑现过：用户选了询问，程序静默改名。现在一律走 ResolveConflictAsync：
                     * · 非 Ask 档：按档位直接算（默认 AutoRename = 老行为，绝不覆盖）；
                     * · Ask 档：第一次冲突时暂停该任务、聚合问一次（覆盖 / 跳过 / 自动重命名，可对整批生效）。
                     */
                    ConflictResolution resolution = await ResolveConflictAsync(
                        task,
                        ConflictTargetKind.Directory,
                        outputPath,
                        $"输出目录已存在且非空，{task.FileName} 的产物会落进一个已经有内容的目录里。" +
                        "请选择同名时怎么处理。",
                        "落点：" + outputPath,
                        cancellationToken);

                    if (resolution.Choice == ConflictChoice.Skip)
                    {
                        // 「跳过」= 这个包这次不处理：一个字节都不写，也不动目录里已有的东西。
                        task.Status = StatusText.Skipped;
                        task.Operation = StatusText.OpSkip;
                        task.ProgressText = StatusText.ProgressSkipped;
                        task.ErrorMessage = $"输出目录已存在且非空，按你的选择跳过：{outputPath}";
                        task.EndTime = DateTime.Now;
                        task.ElapsedText = task.StartTime.HasValue
                            ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                            : "-";
                        task.LastUpdatedTime = DateTime.Now;

                        AppendLog("WARN", $"{task.FileName}：同名冲突按你的选择跳过，本任务不解压（输出目录：{outputPath}）。");
                        return;
                    }

                    if (resolution.Choice == ConflictChoice.Overwrite)
                    {
                        /*
                         * 「覆盖」= 沿用这个已有目录（把产物合并进去）。
                         *
                         * 目录本身不会被删：同名条目在**定稿搬运**那一步才真正相撞，
                         * 到那一刻仍然按同一个决定处理（覆盖走"先挪到临时名 → 落位 → 再删"两阶段）。
                         */
                        AppendLog(
                            "WARN",
                            $"{task.FileName}：输出目录已存在且非空，按你的选择「覆盖」沿用该目录：{outputPath}" +
                            "（目录里已有的同名条目会在定稿时按同一决定处理）。");

                        outputRedirectNote = $"原定输出目录 {outputPath} 已存在且非空，按你的选择覆盖进该目录";
                    }
                    else
                    {
                        string newOutputPath = resolution.TargetPath;

                        AppendLog("WARN", $"输出目录已存在且非空，为避免混入旧文件，自动改用新目录：{newOutputPath}");

                        /*
                         * 实际落点与"打算的落点"不一致这件事**不许静默**：
                         * ① 下面会把实际落点写回 task.OutputPath（界面"输出目录"列看得见）；
                         * ② 校验结论里也带一句（task.VerifyMessage）；
                         * ③ 日志里额外提醒续解的影响 —— 一键处理的续解是按**解压前**的目录快照找内层包的，
                         *    内层包落进新目录时它找不到，用户看到的现象就是"第二层没解"。
                         *    这里的提示是让人一眼知道该去哪找，而不是让程序假装没发生。
                         */
                        outputRedirectNote = $"原定输出目录 {requestedOutputPath} 已存在且非空，本次实际输出到 {newOutputPath}";

                        AppendLog("WARN", $"{task.FileName}：{outputRedirectNote}。自动续解按解压前的目录查找内层包，若内层包落在新目录里可能不会被继续解开。");

                        outputPath = newOutputPath;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                /*
                 * 取消必须原样抛上去（落成「已取消」，不变量 6）。
                 *
                 * 同名冲突询问里的「取消本批」也走这条路：下面的兜底 catch 会把任何异常变成
                 * 一句"检查输出目录失败，继续使用原输出目录"—— 那会让取消被吞掉、任务接着跑。
                 */
                throw;
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"检查输出目录失败，将继续使用原输出目录：{ex.Message}");
            }

            /*
             * ===== 并发下的"最终目录"占位（2026-09-22：默认并发提到 4 之后暴露的竞态）=====
             *
             * 场景：同一个目录里 `pipe.rar` 与 `pipe.zip` 的**包基名都是 `pipe`**，
             * 默认落点都是 `<out>\pipe`。串行时第二个会看到目录已存在、按冲突档落成 `pipe(1)`；
             * 并发时两个任务会在同一瞬间认定"这个目录还不存在"，于是双双往同一个新目录里定稿 ——
             * 后到的那个 `File.Move` 撞上已存在的目标，整条内容物都没搬成，任务报「解压失败」。
             * 用户什么错都没犯，却因为并发丢了一次解压。
             *
             * 修法：把"这个还不存在的最终目录"在**进程内先占下来**（只占不建，用户目录里不留空壳），
             * 占不到就按同一套 `名字(1)` 约定换一个。等这个任务收尾时释放（见 ProcessExtractTaskAsync）。
             *
             * 只对"目录还不存在"的情况占位：
             * · 目录已存在时，上面的冲突档已经基于一个**看得见的事实**做完了决定（沿用 / 改名 / 跳过），
             *   再插一手会把用户显式选的「覆盖」变成"改名"，那是另一回事；
             * · 落点就是源目录（模式 B）时**绝不占位**：那个目录本来就是这次要整理的目标，
             *   给它改名等于把用户的就地整理甩到旁边的 `名字(1)` 去。
             */
            if (!task.IsContinuationTask &&
                !landsInSourceDirectory &&
                !string.IsNullOrWhiteSpace(outputPath) &&
                !Directory.Exists(outputPath))
            {
                outputPath = ClaimOutputDirectory(outputPath, task, ref outputRedirectNote);
            }

            /*
             * 回写**实际最终输出目录**（P1）。
             *
             * 这是"产物到底在哪"的唯一权威来源：上面可能刚把目录从 xxx 改成了 xxx(1)，
             * 解压后校验、结果归集、清理源包、界面"输出目录"列、以及一键处理的续解全都以它为准
             * （续解那边要读的就是这个值，见报告里的移交项）。
             * 绝不允许只把改名写进日志、却让 task.OutputPath 停在"打算输出到哪"。
             *
             * ⚠ 它**始终指向最终目录**，不指向暂存目录：暂存区只是过程，
             * 续解逻辑（OneClickCoordinator）读的必须是"内容物最终在哪"。
             */
            task.OutputPath = outputPath;

            // 每个任务都明确说一次实际落点：目录被改名时用户必须能立刻看出产物去了哪。
            // 有「本次选项」时带上依据（§9.2 硬要求⑥）：光有落点回答不了"这次为什么解到这里"。
            AppendLog(
                "INFO",
                RunOptions == null
                    ? $"{task.FileName}：本次实际输出目录 {outputPath}"
                    : $"{task.FileName}：本次实际输出目录 {outputPath}（依据 → {RunOptions.Describe()}）");

            /*
             * ===== 阶段一：入仓（stage，契约 §2.1）=====
             *
             * 从这一刻起，本任务**所有**中间动作（抠内嵌归档之后的解压、第一层、内层分卷、递归）
             * 都在私有暂存目录 <c>&lt;work&gt;\&lt;taskId&gt;\stage</c> 里完成，
             * 源目录与最终目录一个字节都不会被写。
             *
             * 这条就是用户那句"分卷文件你居然又解压到外面来了"的根治点：
             * 旧实现把引擎的输出目录直接设成 destDir，内层分卷于是和内容物一起躺在用户目录里；
             * 一旦失败，用户目录里还留着半成品。
             */
            string stageDirectory = _pathService.BuildTaskStageDirectory(task);

            if (string.IsNullOrWhiteSpace(stageDirectory))
            {
                task.Status = StatusText.ExtractFailed;
                task.ProgressText = StatusText.ProgressFailed;
                task.ErrorMessage = "无法确定暂存目录（工作区不可用）";
                AppendLog("ERROR", $"{task.FileName}：{task.ErrorMessage}");
                return;
            }

            string? stageFailure = await PrepareStageDirectoryAsync(task, stageDirectory, cancellationToken);

            if (!string.IsNullOrWhiteSpace(stageFailure))
            {
                task.Status = StatusText.ExtractFailed;
                task.ProgressText = StatusText.ProgressFailed;
                task.ErrorMessage = stageFailure;
                AppendLog("ERROR", $"{task.FileName}：{stageFailure}");
                return;
            }

            // 引擎写盘的地方 = 暂存目录。**不是** outputPath —— 那是定稿的目标。
            string engineOutputPath = stageDirectory;

            AppendLog("INFO", $"{task.FileName}：入仓目录 {stageDirectory}（中间产物先落这里，定稿时才搬进 {outputPath}）");

            /*
             * 内嵌归档（双面文件）：先按偏移把尾部那段真正的 ZIP 抠出来，
             * 再拿它去列目录 / 预检 / 解压 / 解压后校验。
             *
             * 为什么不能直接把源文件交给 7z：这种文件的 ZIP 内部偏移是**相对 ZIP 自己**的。
             * 7-Zip 会尝试用"文件末尾 EOCD 反推的基准偏移"容忍这种错位，但只容忍 8 MiB 以内
             * （本机 26.01 实测：前置 8,388,608 字节可以，8,388,609 字节就报
             * "Cannot open the file as archive"）；用户那个文件垫了 17,031,321 字节，必然被拒。
             * 改后缀也没用（资源管理器能打开，只是因为它的 ZIP 读取器容忍这种整体错位）。
             * 把 [偏移, EOF) 原样复制出来是唯一可靠、且完全不改源文件的做法。
             *
             * 换成局部变量 engineArchivePath，而**不去改 task.CurrentPath**：
             * 后者是源文件路径，改名、清理源包、结果统计、报告全都依赖它 ——
             * 一旦被换成工作区里的临时文件，"清理源包"会去删我们自己的中间产物，
             * 而真正的源文件永远得不到处理，报告里指向的也不再是用户给的那个文件。
             */
            string engineArchivePath = task.CurrentPath;

            if (task.EmbeddedArchiveOffset > 0)
            {
                string carveTarget = BuildEmbeddedArchivePath(task);

                long carveBytes = 0;

                try
                {
                    carveBytes = new FileInfo(task.CurrentPath).Length - task.EmbeddedArchiveOffset;
                }
                catch
                {
                    // 量不出大小不影响能不能抠，只是日志与空间预检里少个数字。
                }

                /*
                 * 临时空间预检：抠出来的中间文件落在工作区（<c>&lt;程序目录&gt;\data\work</c>，即程序所在的那个盘）。
                 * 一个 780MB 的双面文件要在那里占掉 760MB —— 空间不够会写到一半失败，
                 * 还会把那个盘挤满。取不到空间就不拦（宁可试也不误拒），取到了才判。
                 */
                long? carveFree = SpaceChecker.GetAvailableFreeSpace(Path.GetDirectoryName(carveTarget));

                if (carveFree.HasValue && carveBytes > 0 && carveFree.Value < carveBytes + (256L * 1024 * 1024))
                {
                    task.Status = StatusText.ExtractFailed;
                    task.ErrorMessage =
                        $"取出内嵌归档需要约 {carveBytes / 1024 / 1024} MB 临时空间，" +
                        $"但系统盘只剩 {carveFree.Value / 1024 / 1024} MB。请先清理空间再试。";
                    task.LastUpdatedTime = DateTime.Now;

                    AppendLog("ERROR", $"{task.FileName}：{task.ErrorMessage}");
                    return;
                }

                AppendLog(
                    "INFO",
                    $"{task.FileName}：正在取出内嵌归档（约 {carveBytes / 1024 / 1024} MB），这一步在后台做，界面不会卡住。");

                /*
                 * 关键修复：抠出是**同步磁盘拷贝**（几百 MB 量级），必须扔到后台线程。
                 * 之前直接在这里同步调用，拷贝期间整个界面线程被占住 ——
                 * 表现就是"点了一键处理之后程序卡死、窗口未响应"，只能强杀进程
                 * （实测：780MB 的双面文件拷 763MB，界面全程无响应）。
                 */
                int lastPercent = -1;

                // 每前进 20% 报一次：够密到能看出在动，又不至于把日志刷爆。
                var carveProgress = new Progress<int>(percent =>
                {
                    if (percent >= lastPercent + 20 || percent >= 100)
                    {
                        lastPercent = percent;
                        AppendLog("INFO", $"{task.FileName}：取出内嵌归档 {percent}%");
                    }
                });

                CarveResult carve = await Task.Run(
                    () => EmbeddedArchiveCarver.Carve(
                        task.CurrentPath,
                        task.EmbeddedArchiveOffset,
                        carveTarget),
                    cancellationToken);

                if (!carve.Success || !File.Exists(carve.OutputPath))
                {
                    task.Status = StatusText.ExtractFailed;
                    task.Operation = StatusText.OpWaiting;
                    task.ProgressText = StatusText.ProgressFailed;
                    task.ErrorMessage = string.IsNullOrWhiteSpace(carve.Message)
                        ? "取出内嵌归档失败"
                        : "取出内嵌归档失败：" + carve.Message;
                    task.LastUpdatedTime = DateTime.Now;

                    AppendLog("ERROR", $"{task.FileName}：{task.ErrorMessage}");
                    return;
                }

                engineArchivePath = carve.OutputPath;

                AppendLog(
                    "INFO",
                    $"检测到内嵌归档：已从偏移 {task.EmbeddedArchiveOffset} 处取出 {carve.BytesWritten} 字节，" +
                    $"实际使用 {engineArchivePath} 解压。");
            }

            List<PasswordItem> candidates = _passwordService.GetPasswordCandidates(
                task,
                Settings.UseGlobalPasswordForAllTasks ? GlobalPassword : string.Empty,
                _passwordService.Passwords,
                Settings.TryEmptyPasswordFirst,
                Settings.EnableSidecarPassword);

            if (candidates.Count == 0)
            {
                candidates.Add(new PasswordItem
                {
                    Value = string.Empty,
                    Source = "Empty",
                    IsEnabled = true,
                    Remark = "空密码"
                });
            }

            /*
             * 本批手动输入的密码（如果用户答过）插进候选里。
             *
             * 位置：空密码之后、密码本之前 —— 见 InsertManualPasswordCandidate 的说明。
             * 它天然参与下面的"每层尝试上限"，所以不会让加密分卷的候选循环变成无限循环（不变量 8）。
             */
            InsertManualPasswordCandidate(candidates, _manualBatchPassword);

            /*
             * 密码候选的**硬上限**（AGENTS.md §9.2：每层、每任务、每批次都要有尝试上限）。
             *
             * 解压模式下一个候选 = 一次完整解压：几百条密码本的包会被逐个候选整包重解一遍，
             * 用户看到的是"几十分钟不动、磁盘一直在写"。所以本层只试前 N 个，
             * 到上限时状态说"达到密码尝试上限"（**不是**"密码错误"）——
             * 包可能完全没问题，只是密码不在这批候选里。
             */
            // 上限读一次就定住：设置是用户随时可改的，同一单任务里不许"前半段按一个上限、后半段按另一个"。
            int attemptLimit = MaxPasswordAttemptsPerLayer;
            int maxPasswordAttempts = Math.Min(candidates.Count, attemptLimit);
            bool candidatesTruncated = candidates.Count > maxPasswordAttempts;

            if (candidatesTruncated)
            {
                AppendLog(
                    "WARN",
                    $"{task.FileName}：密码候选共 {candidates.Count} 个，超过单层上限 {attemptLimit} 个，" +
                    $"本层只试前 {maxPasswordAttempts} 个（到上限会明确报“{StatusText.PasswordAttemptLimitReached}”，不会报成密码错误）。");
            }

            /*
             * M5 解压前预检：路径安全 + 资源预算。
             *
             * 为什么必须先"列目录"再解压：真正写盘的是外部 7z.exe，进程外拦不住 Zip Slip。
             * 能做的只有两件事 —— ①解压前把危险条目挑出来，拒绝这一单；
             * ②解压后再校验落点（见 PostProcessSuccessAsync）。这里做的是 ①。
             *
             * 加密头（-mhe）的包不给密码是列不出目录的，所以先拿密码候选去试列表：
             * 试成功的那一个同时也是最可能的解压密码，后面解压循环还会再用一遍。
             */
            ArchiveListResult? preflightList = null;

            // 最后一次 list 失败的结构化原因（成功时用不到）。下面两个"只 list 失败的 WARN 分支"要靠它出结论：
            // 加密文件名与"文件损坏"的文本**分不开**，能分开的是错误类型（见 SevenZipOutputParser 的实测记录）。
            string lastListErrorType = string.Empty;
            string lastListMessage = string.Empty;

            // 预检给出的"文件名已加密"结论（含给用户的那句话）。null = 预检没下这个结论。
            // 它必须活到这一层结束，见下面 preflightSaidEncryptedHeaders 的恢复。
            string? encryptedHeadersMessage = null;

            foreach (PasswordItem candidate in candidates.Take(MaxPreflightPasswordAttempts))
            {
                ArchiveListResult attempt = await _archiveEngine.ListAsync(
                    ArchiveRequest.For(engineArchivePath, candidate.Value),
                    cancellationToken);

                if (attempt.Success)
                {
                    preflightList = attempt;
                    break;
                }

                lastListErrorType = attempt.ErrorType ?? string.Empty;
                lastListMessage = attempt.Message ?? string.Empty;
            }

            if (preflightList == null)
            {
                /*
                 * 列不出内容：要么加密头（RAR -hp / 7z -mhe），要么归档真的坏了。
                 *
                 * ⚠ 这里**只认引擎给的错误类型**，绝不在这条路径上"猜"：
                 * 解压 / 测试路径上的 WrongPassword 是**密码候选循环的驱动信号**
                 * （循环见到非 WrongPassword 就 break），在那里把它改成"文件名已加密"
                 * 会让第一个候选（常是空密码）就打断循环 —— 我们自己 -mhe 的内层分卷会全部解不开。
                 * 判定本身只在 SevenZipOutputParser.LooksLikeEncryptedHeaders 里，而且只在列目录操作上成立
                 * （有反向回归测试钉着），这里只是把那个结论落到任务状态上。
                 */
                if (string.Equals(
                        lastListErrorType,
                        SevenZipOutputParser.EncryptedHeadersErrorType,
                        StringComparison.OrdinalIgnoreCase))
                {
                    task.Status = StatusText.EncryptedHeaders;
                    task.PasswordStatus = StatusText.PasswordNeed;

                    /*
                     * 这份文案要**留到这一层结束**（见下面 preflightSaidEncryptedHeaders 的恢复）：
                     * 密码候选循环会把 task.ErrorMessage 覆盖成泛泛的"密码错误或缺少正确密码"，
                     * 而用户真正需要知道的是"这个包连文件列表都读不出来，得先给对密码"。
                     */
                    encryptedHeadersMessage =
                        $"这个包可能加密了文件名（RAR -hp / 7z -mhe），所以连内容清单都读不出来，" +
                        $"需要正确密码才能列出内容。{lastListMessage}";

                    task.ErrorMessage = encryptedHeadersMessage;

                    AppendLog(
                        "WARN",
                        $"{task.FileName}：{StatusText.EncryptedHeaders} —— 连内容清单都读不出来（需要正确密码），" +
                        $"本次跳过路径预检与资源预算，解压后仍会校验落点。原因：{lastListMessage}");
                }
                else
                {
                    AppendLog("WARN", $"{task.FileName}：没能列出归档内容（可能是加密头或文件损坏），本次跳过路径预检与资源预算，解压后仍会校验落点。");
                }
            }
            else
            {
                PathSafetyReport pathReport = ArchivePathGuard.CheckEntries(preflightList.Entries);

                if (!pathReport.IsSafe)
                {
                    task.Status = StatusText.ExtractFailed;
                    task.ErrorMessage = "归档里有不安全的条目，已拒绝解压：" + pathReport.Summary;

                    AppendLog("ERROR", $"{task.FileName}：路径预检未通过 —— {pathReport.Summary}");
                    return;
                }

                long archiveSize = 0;

                try
                {
                    // 量的是**真正交给引擎的那个文件**：内嵌归档抠出来之后它比源文件小得多，
                    // 拿源文件大小当分母会把展开比算小，压缩炸弹就更容易蒙混过关。
                    archiveSize = new FileInfo(engineArchivePath).Length;
                }
                catch
                {
                    // 取不到大小就不做展开比判断，其余预算照常。
                }

                // 预算里的"落点"用暂存目录：引擎真正写盘的地方是它，最终目录此时还不存在。
                BudgetCheckResult budget = new ResourceBudget().CheckBeforeExtract(preflightList, archiveSize, engineOutputPath);

                /*
                 * 精确空间需求（用户 2026-09-22 需求第 1 条：核算必须含内容物 + 过程物 + 去重后的峰值）。
                 *
                 * 用的是**手上这一份 list**（绝不为此再跑一次 7z：加密包每多列一次目录就多一次失败机会），
                 * 而流程预算只算了"内容物"那一项 —— 源包在盘上还没走、抠出来的内嵌中间件、
                 * 内层包再展开的增量、以及并发下别的任务已经占下的份额，都要在这一步一起算进来。
                 */
                TaskSpaceEstimate refined = SpaceEstimator.RefineWithListing(
                    SpaceEstimator.FromSourceFiles(task),
                    preflightList,
                    SpaceEstimator.EstimateCarvedBytes(task, archiveSize));

                RecordRefinedEstimate(task, refined);

                if (!budget.Allowed)
                {
                    if (budget.IsSpaceShortage)
                    {
                        /*
                         * 空间不够（精确值算出来的）：**不启动**，报具体数字与建议动作。
                         * 顺手用 ReconcileReservation 把账本调准 —— 它给出的信息比预算那句话更全
                         * （含"差多少"与三条建议）；万一它反而放行（两边估算口径不同），
                         * 就退回预算那句话，绝不把"放行"当结论（宁可保守）。
                         */
                        SpaceGateDecision spaceGate = ReconcileReservation(task, refined);
                        string spaceReason = spaceGate.Allowed ? budget.Reason : spaceGate.ToLogLine();

                        MarkSpaceBlockedCore(
                            task,
                            spaceReason + $"（{refined.Describe()}；依据：{refined.Basis}）",
                            refined.PeakBytes,
                            budget.FreeSpaceBytes ?? SpaceReservationLedger.UnknownAvailable,
                            spaceGate.Allowed ? 0L : spaceGate.ShortfallBytes);

                        return;
                    }

                    task.Status = StatusText.ExtractFailed;
                    task.ErrorMessage = "资源预算未通过：" + budget.Reason;

                    AppendLog("ERROR", $"{task.FileName}：资源预算未通过 —— {budget.Reason}");
                    return;
                }

                /*
                 * 空间门（精确值）：够了就放行、不够就**在这里停下**（引擎一个字节都还没写）。
                 * 它同时把本任务的预留从"粗估"调整成精确峰值 —— 并发下这一步很关键：
                 * 估小了会让几个任务一起把盘写满，估大了会白白拦下本来能跑的任务。
                 */
                SpaceGateDecision preciseGate = ReconcileReservation(task, refined);

                if (!preciseGate.Allowed)
                {
                    MarkSpaceBlockedCore(
                        task,
                        preciseGate.ToLogLine() + $"（{refined.Describe()}；依据：{refined.Basis}）",
                        refined.PeakBytes,
                        preciseGate.AvailableBytes,
                        preciseGate.ShortfallBytes);

                    return;
                }

                AppendLog("INFO", $"{task.FileName}：空间门通过（精确） —— {preciseGate.Reason}；{refined.Basis}");

                if (IsDangerModeActive)
                {
                    AppendLog(
                        "INFO",
                        $"{task.FileName}：危险模式会在定稿 + 校验通过之后彻底删除它的其余物"
                        + $"（预计可回收 {TaskSpaceEstimate.FormatSize(refined.ReclaimableBytes)}）");
                }

                if (!string.IsNullOrWhiteSpace(budget.Reason))
                {
                    AppendLog("WARN", $"{task.FileName}：资源预算提示 —— {budget.Reason}");
                }

                /*
                 * 长路径提示落地（WinRAR 参考 §2 I 组采纳项）。
                 *
                 * 以前它只在上面那行日志里（ResourceBudget 已经把它拼进 Reason），任务详情里看不到 ——
                 * 而"路径过长导致少了几个文件"恰恰是用户事后才来查的问题。落到任务字段之后，
                 * 失败清单逐归档的第二级（TaskSummaryService）也会带上这一行。
                 * 数字与文案都由 Security/PathLengthPreflight 备好，这里只搬运，不重算。
                 */
                if (!string.IsNullOrWhiteSpace(budget.PathLengthWarning))
                {
                    task.PathLengthWarning = budget.PathLengthWarning;
                }

                /*
                 * 危险条目统计（WinRAR 参考 §2 F 组 / §3 第 5 条采纳项，设置项 ReportDangerousEntries 默认开）。
                 *
                 * ⚠ 复用**手上这一份** list（preflightList），绝不为它再跑一次 7z：
                 * 加密包每多列一次目录就多一次失败机会，重复 list 是把失败概率成倍放大。
                 * **只提示不阻断**：安装器 / 补丁经常就是内容物，不做全局硬排除掩码。
                 */
                PublishDangerousEntriesHint(
                    task,
                    AnalyzeDangerousEntries(preflightList.Entries, Settings.ReportDangerousEntries),
                    "INFO");
            }

            /*
             * 预检给出的"文件名已加密"结论必须**留住**。
             *
             * 为什么不能只写在预检那一支里：后面的密码候选循环无论如何都会覆盖 task.Status
             * （解压失败 → WrongPassword）。于是预检算出来的那个**更具体**的结论会被一句泛泛的
             * "密码错误"吃掉 —— 用户拿到的还是错的那句话，这一处接线就等于白做。
             *
             * 只在"这一层最后仍然只是密码没通过"时把它恢复：
             * · 解压成功（有的包加密头也能解）→ 不动；
             * · 达到尝试上限 → 那是更具体的结论，不动；
             * · 文件损坏 / 权限不足这类别的失败 → 更具体的结论，不动。
             */
            bool preflightSaidEncryptedHeaders = task.Status == StatusText.EncryptedHeaders;

            /*
             * ===== 不变量 11 的**第二道**：就在引擎真正开工之前再比一次 =====
             *
             * 第一道在本方法开头（挡住"扫描之后就被改过"的包）。这一道挡的是**中间那段窗口**：
             * 从第一道到这里，程序已经做过冲突询问、目录占位、坍缩扫描、暂存目录准备
             * —— 弹窗可以停在屏幕上等用户点，一秒到几分钟都正常，
             * 而这期间用户完全可能自己把那个包换掉（"我先看看里面是什么"→ 解压 → 覆盖回去）。
             *
             * 两道之间只做 stat（不读内容、不写任何东西），所以代价可以忽略；
             * 而漏掉这一道的代价是**拿旧识别结果解新文件**，那正是不变量 11 要防的事。
             */
            if (StopIfSourceChanged(task))
            {
                return;
            }

            /*
             * 递归模式（M4）：不是"只解当前层"时，整条解压交给 RecursiveExtractor。
             * 它自己会解第 0 层、探测内层、按模式决定继续还是问用户，并受硬上限约束。
             * 放在预检之后：预检已经把暂存目录算好，而且非归档 / 格式未知在前面已经分流走了。
             *
             * 落点传的是**暂存目录**：递归核心本来就在自己的工作区里逐层解、最后才 Publish，
             * 让它直接 Publish 到最终目录等于跳过了"入仓 → 定稿"这道门（失败时还会在用户目录里留下半成品）。
             * 现在它发布进暂存区，再由下面的定稿一次性搬进最终目录 —— 与单层路径同一口径。
             *
             * ⚠ 这条分支里的第 0 层同样受不变量 11 保护：递归核心**每一层**开工之前都会回头问一次
             * "源文件还是原来那一份吗"（见 CheckRootSourceUnchangedAsync），不必在这里再写一遍。
             */
            if (!string.Equals(Settings.RecursionMode, "SingleLayer", StringComparison.OrdinalIgnoreCase))
            {
                await RunRecursiveAsync(task, engineArchivePath, engineOutputPath, cancellationToken);

                if (task.Status == StatusText.ExtractSuccess)
                {
                    // 递归产物同样要走"校验 → 定稿 → 归集 → 源包处理"，与单层路径一个字都不差。
                    bool recursionConclusionStands = await PostProcessSuccessAsync(
                        task, engineArchivePath, string.Empty, engineOutputPath, outputRedirectNote, placementMode, oneClickRun, cancellationToken);

                    if (recursionConclusionStands && task.Status == StatusText.ExtractSuccess)
                    {
                        AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
                    }
                }

                return;
            }

            ArchiveOperationResult? lastResult = null;
            string selectedPassword = string.Empty;
            bool passwordConfirmed = false;

            /*
             * 进度可见（D5）的接线点：**每个任务一个接收端**。
             *
             * 为什么一个任务只建一个：它记着"上一个写进日志的 10% 档位"，
             * 每换一次密码候选就新建一个的话，"达到 10% 就写一行"这条限制会失效
             * （每个候选都从头数一遍档位）。
             *
             * 清一次残留：同一个任务可能被重跑（换密码候选、用户再点一次），
             * 上一轮的百分比绝不能带到这一轮来。
             */
            TaskProgressSink progressSink = new(this, task);
            task.ClearProgress();

            /*
             * 关键修复：
             *
             * 旧逻辑：
             * candidates.Count > 1 就强制先执行 7z t。
             *
             * 问题：
             * 7z t 只是测试，不会产生解压文件。
             * 所以用户会看到 7-Zip Console 一直运行，但输出目录没有产物。
             *
             * 新逻辑：
             * 只有用户明确开启 TestBeforeExtract，或者任务明确标记 IsEncrypted，
             * 才先测试密码。
             */
            bool shouldTestPasswordBeforeExtract =
                Settings.TestBeforeExtract;

            if (shouldTestPasswordBeforeExtract)
            {
                task.Operation = StatusText.OpTest;
                task.Status = StatusText.Testing;
                task.ProgressText = StatusText.ProgressProcessing;
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("INFO", $"{task.FileName}：开始解压前测试。");

                for (int i = 0; i < maxPasswordAttempts; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    PasswordItem candidate = candidates[i];
                    string password = candidate.Value ?? string.Empty;

                    AppendLog("INFO", $"{task.FileName}：测试密码候选 {i + 1}/{maxPasswordAttempts}，{_passwordService.BuildTryPasswordLogText(candidate, i + 1)}");

                    ArchiveOperationResult testResult = await _archiveEngine.TestAsync(
                        BuildTrackedRequest(engineArchivePath, password, null, progressSink),
                        cancellationToken);

                    lastResult = testResult;

                    if (testResult.DetectedErrorType == "Cancelled" ||
                        testResult.Status == StatusText.Cancelled ||
                        cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    if (testResult.Success)
                    {
                        selectedPassword = password;
                        passwordConfirmed = true;

                        task.Status = StatusText.TestPassed;
                        task.PasswordStatus = string.IsNullOrEmpty(password) ? StatusText.PasswordNotNeeded : StatusText.PasswordCorrect;
                        task.ErrorMessage = string.Empty;
                        task.LastUpdatedTime = DateTime.Now;

                        AppendLog("INFO", $"{task.FileName}：测试通过。");
                        break;
                    }

                    if (testResult.DetectedErrorType == "WrongPassword")
                    {
                        task.PasswordStatus = StatusText.WrongPassword;
                        AppendLog("WARN", $"{task.FileName}：密码错误，继续尝试下一个候选密码。");
                        continue;
                    }

                    task.Status = testResult.Status;
                    task.ErrorMessage = testResult.Message;
                    task.Operation = StatusText.OpWaiting;
                    task.ProgressText = StatusText.ProgressCompleted;
                    task.LastUpdatedTime = DateTime.Now;

                    AppendLog("ERROR", $"{task.FileName}：测试失败，原因：{testResult.Message}");
                    break;
                }

                if (!passwordConfirmed)
                {
                    /*
                     * "试到上限停了"和"候选全都试过、都说密码错"是两件事（AGENTS.md §9.2）：
                     * 前者的状态是"达到密码尝试上限"，不能让用户以为密码本里就一定没有正确密码。
                     * lastResult 不是 WrongPassword 时说明停因另有其人（损坏/权限…），照旧原样上报。
                     */
                    bool stoppedByAttemptLimit = candidatesTruncated &&
                        (lastResult == null || lastResult.DetectedErrorType == "WrongPassword");

                    if (stoppedByAttemptLimit)
                    {
                        task.Status = StatusText.PasswordAttemptLimitReached;
                        task.PasswordStatus = StatusText.PasswordNeed;
                        task.ErrorMessage =
                            $"已达到密码尝试上限：本层试了 {maxPasswordAttempts} 个候选（共 {candidates.Count} 个），未能确认密码。";
                    }
                    else if (lastResult != null && lastResult.DetectedErrorType == "WrongPassword")
                    {
                        task.Status = StatusText.WrongPassword;
                        task.PasswordStatus = StatusText.WrongPassword;
                        task.ErrorMessage = "密码错误或缺少正确密码";
                    }
                    else if (lastResult != null)
                    {
                        task.Status = lastResult.Status;
                        task.ErrorMessage = lastResult.Message;
                    }
                    else
                    {
                        task.Status = StatusText.TestFailed;
                        task.ErrorMessage = "没有可用密码或测试失败";
                    }

                    task.EndTime = DateTime.Now;
                    task.ElapsedText = task.StartTime.HasValue
                        ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                        : "-";

                    task.Operation = StatusText.OpWaiting;
                    task.ProgressText = StatusText.ProgressCompleted;
                    task.LastUpdatedTime = DateTime.Now;

                    AppendLog("ERROR", $"解压前测试失败：{task.FileName}，原因：{task.ErrorMessage}");

                    /*
                     * 密码类失败**登记到批次**，由 ShowPasswordFailuresSummaryAsync 合并成一次提示。
                     * 原来这里（以及下面的直接解压分支）各弹一次模态框、而且用的是同步 Dispatcher.Invoke：
                     * 一批几十个错包就要点几十次，还会阻塞调用线程。
                     */
                    RecordPasswordFailure(task);

                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                task.Operation = StatusText.OpExtract;
                task.Status = StatusText.Extracting;
                task.ProgressText = StatusText.ProgressProcessing;
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("INFO", $"{task.FileName}：测试通过，开始正式解压。");

                ArchiveOperationResult extractResult = await _archiveEngine.ExtractAsync(
     BuildTrackedRequest(engineArchivePath, selectedPassword, engineOutputPath, progressSink),
     extractOptions,
     cancellationToken);

                lastResult = extractResult;

                if (extractResult.DetectedErrorType == "Cancelled" ||
                    extractResult.Status == StatusText.Cancelled ||
                    cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (extractResult.Success)
                {
                    task.Status = StatusText.ExtractSuccess;
                    task.PasswordStatus = string.IsNullOrEmpty(selectedPassword) ? StatusText.PasswordNotNeeded : StatusText.PasswordCorrect;
                    task.ErrorMessage = string.Empty;

                    // 密码成功记录仍然挂在**源文件**上：下次用户再导入这个文件时要能直接命中，
                    // 而工作区里抠出来的临时文件活不过这次任务。
                    _passwordService.RecordPasswordSuccess(task.CurrentPath, selectedPassword);

                    // 收尾可能把"解压成功"顶掉（产物越界 / 超预算 / 定稿失败）：只有结论仍然成立时才敢这么写日志。
                    bool conclusionStands = await PostProcessSuccessAsync(
                        task, engineArchivePath, selectedPassword, engineOutputPath, outputRedirectNote, placementMode, oneClickRun, cancellationToken);

                    if (conclusionStands)
                    {
                        AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
                    }
                }
                else
                {
                    task.Status = extractResult.Status;
                    task.ErrorMessage = extractResult.Message;

                    if (extractResult.DetectedErrorType == "WrongPassword")
                    {
                        task.Status = StatusText.WrongPassword;
                        task.PasswordStatus = StatusText.WrongPassword;
                        task.ErrorMessage = "密码错误或缺少正确密码";
                    }

                    AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}");
                }
            }
            else
            {
                /*
                 * 普通模式：
                 * 不先测试，直接解压。
                 *
                 * 好处：
                 * 1. 普通无密码压缩包会立刻产生解压产物。
                 * 2. 不会让用户误以为卡住。
                 * 3. 如果遇到密码错误，再尝试下一个密码。
                 */
                bool extractSuccess = false;
                bool hasWrongPassword = false;

                for (int i = 0; i < maxPasswordAttempts; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    PasswordItem candidate = candidates[i];
                    string password = candidate.Value ?? string.Empty;

                    selectedPassword = password;

                    task.Operation = StatusText.OpExtract;
                    task.Status = StatusText.Extracting;
                    task.ProgressText = StatusText.ProgressProcessing;
                    task.LastUpdatedTime = DateTime.Now;

                    AppendLog("INFO", $"{task.FileName}：开始解压，密码候选 {i + 1}/{maxPasswordAttempts}，{_passwordService.BuildTryPasswordLogText(candidate, i + 1)}");

                    ArchiveOperationResult extractResult = await _archiveEngine.ExtractAsync(
     BuildTrackedRequest(engineArchivePath, selectedPassword, engineOutputPath, progressSink),
     extractOptions,
     cancellationToken);

                    lastResult = extractResult;

                    if (extractResult.DetectedErrorType == "Cancelled" ||
                        extractResult.Status == StatusText.Cancelled ||
                        cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    if (extractResult.Success)
                    {
                        extractSuccess = true;

                        task.Status = StatusText.ExtractSuccess;
                        task.PasswordStatus = string.IsNullOrEmpty(selectedPassword) ? StatusText.PasswordNotNeeded : StatusText.PasswordCorrect;
                        task.ErrorMessage = string.Empty;

                        // 同"先测试再解压"那条分支：密码成功记录挂源文件，不挂工作区里的临时文件。
                        _passwordService.RecordPasswordSuccess(task.CurrentPath, selectedPassword);

                        // 同"先测试再解压"那条分支：收尾否掉结论（越界 / 超预算 / 定稿失败 / 源包没有搬成）时
                        // 不许写"解压成功" —— 源包没搬成的任务状态是"部分完成"，写"解压成功"就是自相矛盾。
                        bool conclusionStands = await PostProcessSuccessAsync(
                            task, engineArchivePath, selectedPassword, engineOutputPath, outputRedirectNote, placementMode, oneClickRun, cancellationToken);

                        if (conclusionStands && task.Status == StatusText.ExtractSuccess)
                        {
                            AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
                        }

                        break;
                    }

                    if (extractResult.DetectedErrorType == "WrongPassword")
                    {
                        hasWrongPassword = true;
                        task.PasswordStatus = StatusText.WrongPassword;

                        AppendLog("WARN", $"{task.FileName}：密码错误，继续尝试下一个候选密码。");
                        continue;
                    }

                    task.Status = extractResult.Status;
                    task.ErrorMessage = extractResult.Message;

                    AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}");

                    break;
                }

                if (!extractSuccess)
                {
                    // 与"先测试再解压"分支同一条规则：试到上限 ≠ 候选全都试过、更 ≠ 密码错误（AGENTS.md §9.2）。
                    bool stoppedByAttemptLimit = candidatesTruncated && hasWrongPassword;

                    if (stoppedByAttemptLimit)
                    {
                        task.Status = StatusText.PasswordAttemptLimitReached;
                        task.PasswordStatus = StatusText.PasswordNeed;
                        task.ErrorMessage =
                            $"已达到密码尝试上限：本层试了 {maxPasswordAttempts} 个候选（共 {candidates.Count} 个），未能确认密码。";
                    }
                    else if (hasWrongPassword)
                    {
                        task.Status = StatusText.WrongPassword;
                        task.PasswordStatus = StatusText.WrongPassword;
                        task.ErrorMessage = "密码错误或缺少正确密码";
                    }
                    else if (lastResult != null)
                    {
                        task.Status = lastResult.Status;
                        task.ErrorMessage = lastResult.Message;
                    }
                    else
                    {
                        task.Status = StatusText.ExtractFailed;
                        task.ErrorMessage = "未知解压失败";
                    }

                    if (task.Status == StatusText.WrongPassword || task.Status == StatusText.PasswordAttemptLimitReached)
                    {
                        AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}");
                    }
                }
            }

            /*
             * 把预检的"文件名已加密"结论恢复回来（见上面 preflightSaidEncryptedHeaders 的说明）。
             * 条件收得最紧：预检说过、而且这一层最后得到的**只是**"密码没通过"。
             */
            if (preflightSaidEncryptedHeaders &&
                (task.Status == StatusText.WrongPassword || task.Status == StatusText.ExtractFailed))
            {
                task.Status = StatusText.EncryptedHeaders;
                task.PasswordStatus = StatusText.PasswordNeed;

                if (!string.IsNullOrWhiteSpace(encryptedHeadersMessage))
                {
                    // 连原因也一起恢复：泛泛的"密码错误"会让用户去翻密码本，
                    // 而这里的结论是"先给它一个密码，它才肯把内容清单给你看"。
                    task.ErrorMessage = encryptedHeadersMessage;
                }

                AppendLog(
                    "WARN",
                    $"{task.FileName}：这次拿到的是「{StatusText.EncryptedHeaders}」的结论" +
                    "（不是泛泛的密码错误）—— 连内容清单都读不出来，需要正确密码才能列出内容。");
            }

            // 密码类失败登记到本批，批次结束后合并成一次提示（不再在任务循环里逐个弹模态框）。
            RecordPasswordFailure(task);

            task.EndTime = DateTime.Now;
            task.ElapsedText = task.StartTime.HasValue
                ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                : "-";

            /*
             * 实时进度到此为止：把百分比与"长时间无响应"提示一起清掉。
             *
             * 光靠 EndTime 已经能让 HasLiveProgress 判否（界面上不会再显示百分比），
             * 但字段本身留着会变成一颗定时炸弹：将来任何一处把 EndTime 清空（重跑、重置），
             * 上一轮的 87% 就会莫名其妙地重新出现在界面上。收尾时清干净是唯一稳妥的做法。
             */
            task.ClearProgress();

            task.Operation = StatusText.OpWaiting;

            /*
             * 进度文案只在"这一单没有失败 / 取消结论"时才写"完成"。
             * 越界、超预算、递归部分完成这些分支自己写了 ProgressFailed，取消写的是"已取消"，
             * 无条件覆盖会让任务详情窗口出现"状态：解压失败 / 进度：完成"这种自相矛盾的显示。
             */
            if (task.ProgressText != StatusText.ProgressFailed &&
                task.ProgressText != StatusText.ProgressCancelled)
            {
                task.ProgressText = StatusText.ProgressCompleted;
            }

            task.LastUpdatedTime = DateTime.Now;
        }

        /// <summary>
        /// 准备本任务的暂存目录（入仓的第一步）：建出来，并把**上一次留下的残留**清掉。
        ///
        /// 为什么要清残留：暂存目录的 taskId 带源路径哈希，所以同一个包重试时落回同一个目录。
        /// 上一次失败/取消留下的产物如果不清，这一次的定稿会把它们一起当成"本次产物"搬进最终目录 ——
        /// 用户会看到上一轮的垃圾文件混进结果里（比"多几个文件夹"更难查）。
        ///
        /// 为什么必须在后台线程：<see cref="Directory.Delete(string, bool)"/> 删的是几十万个文件的树时
        /// 是秒级到分钟级的同步磁盘活，放在 UI 线程上就是"点了没反应"。
        ///
        /// 返回 null = 准备好了；返回非空字符串 = 失败原因（调用方落成失败状态并把原因写给用户）。
        /// </summary>
        private async Task<string?> PrepareStageDirectoryAsync(
            ArchiveTask task,
            string stageDirectory,
            CancellationToken cancellationToken)
        {
            try
            {
                return await Task.Run(
                    () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (Directory.Exists(stageDirectory))
                        {
                            bool hasLeftover = false;

                            try
                            {
                                hasLeftover = Directory.EnumerateFileSystemEntries(stageDirectory).Any();
                            }
                            catch
                            {
                                hasLeftover = true;
                            }

                            if (hasLeftover)
                            {
                                // 上一轮的残留：先清掉再解。清不掉就如实失败，绝不"接着往里解"。
                                Directory.Delete(stageDirectory, recursive: true);
                            }
                        }

                        return SafePathHelper.EnsureDirectoryExists(stageDirectory)
                            ? null
                            : $"暂存目录不存在且创建失败：{stageDirectory}";
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"准备暂存目录失败（{ex.Message}）：{stageDirectory}";
            }
        }

        /// <summary>
        /// 内嵌归档抠出来之后落在哪：<c>&lt;work&gt;\&lt;taskId&gt;\&lt;包基名&gt;.zip</c>。
        ///
        /// 三条刻意的选择：
        /// ① 放工作区（<see cref="PathService.WorkDirectory"/>）而不是源目录旁边 ——
        ///    中间产物不得写进源目录（AGENTS.md §6 第 12 条）。抠出来的这一段只是为了能解压，
        ///    不是用户要的东西；定稿时它会被归入 <c>其余物</c>（契约 §3.2），取消/失败时留在工作区；
        /// ② 文件名沿用**源文件的包基名**，不改成随机临时名 ——
        ///    密码本"名称:密码"的映射匹配用的就是包基名，随机名会让本来能命中的密码全部落空，
        ///    用户看到的现象会是"同一个包以前能解开，现在说密码错误"；
        /// ③ 它是**暂存目录的兄弟**（不在 stage 里面）：stage 里的东西会被整体定稿搬走，
        ///    而它是解压的输入，不该被自己搬走。
        /// </summary>
        private string BuildEmbeddedArchivePath(ArchiveTask task)
        {
            string baseName = FileNameHelper.SanitizeFileName(FileNameHelper.GetArchiveBaseName(task.CurrentPath));

            return Path.Combine(_pathService.BuildTaskWorkDirectory(task), baseName + ".zip");
        }

        /// <summary>
        /// 清理**本任务自己**的中间工作区目录：<c>&lt;work&gt;\&lt;taskId&gt;\</c>。
        ///
        /// 为什么必须有（端到端验收实测）：双面文件要先按偏移把它尾部那段真正的 ZIP 抠进工作区再解压，
        /// 一次**成功**的一键处理就在那个目录里留下近 1 GB 中间件（实测 733 MB + 167 MB 两个包）。
        /// 这些是从源文件按偏移可再生的派生数据，成功后留着纯属垃圾：跑几批就把盘吃掉，
        /// 而且 MainViewModel 启动时会把工作区根下的每个子目录都报成"未完成的工作区"，留着还会造成假警报。
        ///
        /// 入仓（stage）之后这一条覆盖的范围变大了：暂存目录也在这个工作区里，
        /// 所以**每个任务**（不只是抠过内嵌归档的）成功后都要清 —— 条件反而更简单：
        /// 定稿把内容物搬走之后，这里剩下的全是可再生的中间件。
        ///
        /// 只在这三件事同时成立时才删（与 AGENTS.md §9.5 的清理语义对齐）：
        /// ① 解压成功；② 输出校验通过；③ 没有被取消、没有越界结论、没有超预算、没有定稿失败。
        /// 取消 / 部分完成 / 校验失败 / 越界一律不删 —— 产物可能没落全，工作区里的中间件是用户唯一的线索。
        ///
        /// 安全边界（这是"删目录"，每一条都要有）：
        /// · 目录按**暂存目录的父目录**算出（不再按 CurrentPath 重算，见下），
        ///   再规范化确认它确实在工作区根**之下**（容器内校验，越界就什么都不删）；
        /// · 目录里只允许出现 <c>stage</c> 这一个子目录（我们自己造的）与本任务的中间件文件；
        ///   出现别的子目录说明这不是我们造的那个目录（最典型：任务名撞上了递归工作区的 <c>recursive</c>），
        ///   为安全起见一个字节都不碰；
        /// · 删失败（被占用 / 权限不足）只写日志，绝不让已经成功的任务变成失败。
        ///
        /// ⚠ 目录**必须**由 <paramref name="stageDirectory"/> 反推，不能再调
        /// <see cref="PathService.BuildTaskWorkDirectory"/> 重算：它的 taskId 含源路径哈希，
        /// 而一键处理成功后会按设置把源包搬进其余物并回写 <c>task.CurrentPath</c> ——
        /// 重算出来的就是另一个目录，清理会静默地什么都不做（实测口径：近 1 GB 中间件留在工作区）。
        /// </summary>
        private void CleanupTaskWorkspaceDirectory(ArchiveTask task, string stageDirectory)
        {
            if (task == null)
            {
                return;
            }

            string workRoot = _pathService.WorkDirectory;
            string taskDirectory = string.IsNullOrWhiteSpace(stageDirectory)
                ? _pathService.BuildTaskWorkDirectory(task)
                : Path.GetDirectoryName(stageDirectory.TrimEnd('\\', '/')) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(taskDirectory))
            {
                return;
            }

            if (!ArchivePathGuard.IsInsideRoot(workRoot, taskDirectory, out string reason))
            {
                AppendLog("WARN", $"{task.FileName}：中间工作区目录不在工作区根目录之下，已跳过清理 —— {reason}");
                return;
            }

            if (!Directory.Exists(taskDirectory))
            {
                return;
            }

            try
            {
                string[] subdirectories = Directory.GetDirectories(taskDirectory);

                string? foreign = subdirectories.FirstOrDefault(
                    directory => !string.Equals(
                        Path.GetFileName(directory),
                        PathService.StageDirectoryName,
                        StringComparison.OrdinalIgnoreCase));

                if (foreign != null)
                {
                    AppendLog("WARN", $"{task.FileName}：中间工作区目录里有非本任务造的子目录（{foreign}），为安全起见不清理：{taskDirectory}");
                    return;
                }
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"{task.FileName}：读不了中间工作区目录（{ex.Message}），已跳过清理：{taskDirectory}");
                return;
            }

            (int fileCount, long totalSize) = OutputVerifier.Measure(taskDirectory);

            try
            {
                // 删除是**不可逆**的：动手之前先把"删什么、为什么"写进日志（AGENTS.md §9.5 的同一要求）。
                AppendLog(
                    "INFO",
                    $"{task.FileName}：解压成功且输出校验通过，清理本任务的中间工作区（{fileCount} 个文件 / {totalSize} 字节）：{taskDirectory}");

                Directory.Delete(taskDirectory, recursive: true);

                AppendLog("INFO", $"{task.FileName}：中间工作区已清理：{taskDirectory}");
            }
            catch (Exception ex)
            {
                // 删不掉只是"垃圾多留一会儿"，不影响任务结论（同 ExtractionWorkspace.Cleanup 的口径）。
                AppendLog("WARN", $"{task.FileName}：清理中间工作区失败（{ex.Message}），目录保留：{taskDirectory}");
            }
        }

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

                AppendLog("WARN", "已请求停止后续任务，不再启动新的解压任务。");
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "停止后续任务失败：" + ex.Message);
            }
        }


        public void CancelCurrentTask()
        {
            try
            {
                // 先取快照再逐个 Cancel：Cancel 会同步触发回调，不能拿着列表的锁做这件事。
                List<CancellationTokenSource> running = SnapshotRunningTasks();

                if (running.Count == 0)
                {
                    AppendLog("WARN", "当前没有正在执行的任务可取消。");
                    return;
                }

                foreach (CancellationTokenSource cts in running)
                {
                    try
                    {
                        cts.Cancel();
                    }
                    catch
                    {
                    }
                }

                AppendLog("WARN", "已请求取消当前正在执行的任务，正在结束 7-Zip 进程。");
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "取消当前任务失败：" + ex.Message);
            }
        }
    }
}

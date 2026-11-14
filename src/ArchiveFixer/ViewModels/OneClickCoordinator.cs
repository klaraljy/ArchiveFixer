using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;


/// <summary>
/// 「一个包一次搞定」：把 识别 → 修正伪装后缀 → 按密码本试密码解压 → 一行汇总 串成一次操作
/// （AGENTS.md §10 的 M2 验收）。
///
/// 关于"一键"的准确含义（2026-09-25 用户改口径）：
/// **一键 = 点一次，剩下全自动** —— 需要改后缀的包**自动改名、不弹预览**（用户原话："这个一键处理
/// 自己会自动改名自动解压，为什么遇到这种改名的压缩包还要我两次确认……那种情况只有手动档才会有"）。
/// 手动档（②页那几条改名命令）照旧**先出预览**；一键档的每个改名照旧逐条写进日志与任务列表。
///
/// 关于自动续解（第 2 层起）：
/// 真实的"双面文件"（<c>xxx.mp4</c> = 视频头 + 尾部一个完整 ZIP）解出来往往**不是最终数据**，
/// 而是 <c>&lt;id&gt;.7z.001</c> + <c>.002</c> 这样的加密分卷，还要再解一层。
/// 以前一键处理只做第一层就停手（<see cref="AppSettings.RecursionMode"/> 默认 SingleLayer），
/// 用户看到的现象就是"只有第一层能解出来"。
///
/// 这里**刻意不碰** <c>Extraction/RecursiveExtractor</c>：那条递归路径在用户的真实文件上会卡死
/// （历史事故见 docs/需求变更.md），所以续解用的是**已经跑通的单层解压**重复跑 ——
/// 一轮就是一次正常的"识别 → 改名 → 解压"，只是把上一轮产出的内层包当成本轮输入，
/// 轮数卡死在 <see cref="RoundLimit"/> 轮（默认 7，天花板 <see cref="MaxRoundsCeiling"/> = 不变量 8）。RecursionMode 的默认值不动。
///
/// 刻意不做的事：
/// - 不自动删源包（那是 M3 的独立开关，默认关闭，且要校验通过才删）；
/// - 不改用户没勾选的任务（沿用既有"只处理选中项"的约定）——
///   续解只处理**本轮产物里新出现**的归档，产物目录里本来就有的包一个都不碰。
///
/// <para><b>本类只留公开 API 与编排 facade</b>（M5 按职责拆分）：选项在
/// <see cref="OneClickOptionsPromptSource"/>、确认在 <see cref="OneClickConfirmFlow"/>、
/// 批处理编排在 <see cref="OneClickBatchDriver"/> 与 <see cref="OneClickRoundLoop"/>、
/// 结果在 <see cref="OneClickResultPresenter"/>、继续解在
/// <see cref="OneClickContinuationWorkbench"/> 与 <see cref="OneClickVolumeGroupFacts"/>。
/// ⛔ 判据不在这里再写一份：本类里的转发器只有一行，既有调用点与守门用例按原名字指认。</para>
/// </summary>
internal sealed partial class OneClickCoordinator
{
    /// <summary>
    /// 轮数**硬上限**（不变量 8）：含第一层在内一共最多 **10 轮**。
    ///
    /// <para>⚠ 2026-09-24 第 16 条追加由用户拍板从 3 提到 10：他要的是**一键解到尽头**
    /// （原话："你为什么只弄了两层，我要的一键解压时多重解压……都到最后一步了还没成功"）。
    /// 但硬上限这件事本身不许取消（不变量 8）：真遇到"套了几十层"的包，继续无脑往下解
    /// 只会把时间和磁盘烧在一个可能失控的展开上。</para>
    ///
    /// <para>⚠ <b>2026-09-26：实际用几轮改由设置决定</b>（②页「最大嵌套层数」，见
    /// <see cref="RoundLimit"/>）—— 默认从 10 调到 **5**（他原话："现在将默认的最大的解压层数
    /// 从 10 改到 5 吧，用户有需要自己会改的"）。这个常量从此只当**天花板**用：
    /// 设置允许 1~10，绝不允许超过它。⛔ 界面写 5、程序跑到 10 这种不一致不许出现。</para>
    ///
    /// <para>到顶**不是静默停下**：剩下的内层包会被加进任务列表并勾好，汇总与弹窗里写明
    /// "还剩 N 个内层包没解"，界面给一个「继续解」按钮接着跑下一批
    /// （见 <see cref="OneClickOutcome.PendingContinuationCount"/>）。</para>
    /// </summary>
    internal const int MaxRoundsCeiling = 10;

    /// <summary>
    /// 这一批实际允许跑几轮 = ②页「最大嵌套层数」（默认 7），夹在 1~<see cref="MaxRoundsCeiling"/>。
    ///
    /// <para>为什么跟设置走：这一格的名字就叫"最大嵌套层数"，而一键处理是主流程 ——
    /// 两处各有一套上限只会让人以为"有一处没生效"（2026-09-24 把 3 与 10 统一成 10 就是这个原因，
    /// 2026-09-26 它跟着 10→5 一起下来，2026-10-14 用户拍板**默认再调到 7**，始终是同一个数字）。</para>
    /// </summary>
    internal int RoundLimit => Math.Clamp(
        Settings?.MaxRecursionDepth ?? 7,
        1,
        MaxRoundsCeiling);

    private readonly MainViewModel _vm;
    private readonly RenameCoordinator _renameCoordinator;
    private readonly ExtractionCoordinator _extractionCoordinator;
    private readonly DialogService _dialogService;
    private readonly InnerLayerCollector _innerLayerCollector;
    private readonly OneClickBatchFinalizer _batchFinalizer;
    private readonly OneClickRoundRunner _roundRunner;
    private readonly OneClickSecondStepRunner _secondStepRunner;

    /*
     * 按职责拆出去的协作者（M5）：选项 / 确认 / 批处理编排 / 结果 / 继续解。
     * ⛔ 它们各自只回答一件事，判据全在那边；本类只做一行转发与公开入口，不写第二份。
     */
    private readonly OneClickOptionsPromptSource _optionsPromptSource;
    private readonly OneClickConfirmFlow _confirmFlow;
    private readonly OneClickBatchDriver _batchDriver;
    private readonly OneClickRoundLoop _roundLoop;
    private readonly OneClickResultPresenter _resultPresenter;
    private readonly OneClickContinuationWorkbench _continuation;

    /// <summary>本次运行的不可变快照（阶段 4 影子：先冻结，尚未替换所有读取点）。</summary>
    internal RunSnapshot? CurrentRunSnapshot { get; private set; }

    /// <summary>
    /// 「本次选项 / 确认框」的询问入口（**可注入**；默认 = 真窗口 <c>Views/OneClickOptionsWindow</c>）。
    /// 转发到 <see cref="OneClickOptionsPromptSource"/>，位置与用法与拆分前完全一致。
    /// </summary>
    internal Func<OneClickRunOptions, OneClickOptionsPrompt>? OptionsPromptOverride
    {
        get => _optionsPromptSource.OptionsPromptOverride;
        set => _optionsPromptSource.OptionsPromptOverride = value;
    }

    public OneClickCoordinator(
        MainViewModel vm,
        ScanCoordinator scanCoordinator,
        RenameCoordinator renameCoordinator,
        ExtractionCoordinator extractionCoordinator,
        DialogService dialogService)
    {
        _vm = vm;
        _renameCoordinator = renameCoordinator;
        _extractionCoordinator = extractionCoordinator;
        _dialogService = dialogService;
        _innerLayerCollector = new InnerLayerCollector(vm, extractionCoordinator);
        _batchFinalizer = new OneClickBatchFinalizer(vm, extractionCoordinator);
        _roundRunner = new OneClickRoundRunner(vm, extractionCoordinator, _batchFinalizer, _innerLayerCollector);
        _secondStepRunner = new OneClickSecondStepRunner(vm, extractionCoordinator, _batchFinalizer);

        _optionsPromptSource = new OneClickOptionsPromptSource(vm, extractionCoordinator);
        _confirmFlow = new OneClickConfirmFlow(vm, extractionCoordinator, _optionsPromptSource);
        _resultPresenter = new OneClickResultPresenter(vm, dialogService);
        _batchDriver = new OneClickBatchDriver(
            vm,
            extractionCoordinator,
            dialogService,
            _confirmFlow,
            _resultPresenter,
            RunPipelineAsync);
        _roundLoop = new OneClickRoundLoop(vm, extractionCoordinator, _roundRunner, _secondStepRunner);
        _continuation = new OneClickContinuationWorkbench(vm, scanCoordinator);
    }

    private ObservableCollection<ArchiveTask> Tasks => _vm.Tasks;
    private AppSettings Settings => _vm.Settings;
    private bool IsBusy => _vm.IsBusy;

    private void AppendLog(string level, string message) => _vm.AppendLog(level, message);
    internal void UpdateSummary() => _vm.UpdateSummary();

    /*
     * ===== 续解落点规划（实现见 OneClickContinuationPlanner）=====
     *
     * 下面这些 internal 静态入口是既有用例与协调器的调用点（⛔ 判据只有一处，不许在这里再写一份）：
     * 本类只保留一行转发，行为与改动前逐字相同。
     */

    internal static string ResolveContinuationOutputDirectory(
        ArchiveTask parentTask,
        string? innerPackagePath = null,
        string? ownLayerName = null) =>
        OneClickContinuationPlanner.ResolveContinuationOutputDirectory(parentTask, innerPackagePath, ownLayerName);

    internal static bool IsOwnLayerOccupiedByPackageFile(
        string directory,
        string? ownLayerName,
        string? innerPackagePath) =>
        OneClickContinuationPlanner.IsOwnLayerOccupiedByPackageFile(directory, ownLayerName, innerPackagePath);

    internal static bool ShouldAddContinuationLevelLayer(
        bool parentProducedContent,
        int siblingCount) =>
        OneClickContinuationPlanner.ShouldAddContinuationLevelLayer(parentProducedContent, siblingCount);

    internal static string AppendOwnLayer(string directory, string? ownLayerName) =>
        OneClickContinuationPlanner.AppendOwnLayer(directory, ownLayerName);

    internal static string ResolveContinuationLayerName(string? innerPackagePath) =>
        OneClickContinuationPlanner.ResolveContinuationLayerName(innerPackagePath);

    internal static string ResolveContinuationBaseDirectory(
        ArchiveTask parentTask,
        string? innerPackagePath = null) =>
        OneClickContinuationPlanner.ResolveContinuationBaseDirectory(parentTask, innerPackagePath);

    /// <summary>
    /// 一键处理的公开入口：只处理**勾选**的任务，其余（确认 / 忙标志 / 跑管线 / 批末汇总）
    /// 全部交给 <see cref="OneClickBatchDriver"/>。
    /// </summary>
    public async Task RunAsync()
    {
        if (IsBusy)
        {
            return;
        }

        /*
         * 只处理**勾选**的任务。
         * 以前是"选了就处理选中的、没选就处理全部"，看起来贴心，实际是灾难：
         * 用户以为只动自己挑的那几个，结果整列表都被解压；日志里的任务数还会前后对不上。
         * 明确一点更好：没勾就提示去勾。
         */
        List<ArchiveTask> targets = Tasks.Where(t => t.IsSelected).ToList();

        if (targets.Count == 0)
        {
            /*
             * 一律只认勾选（用户 2026-09-24 第 12 条）：一个都没勾 → **只提示、什么都不做**。
             * ⛔ 这里永远不许再出现"没勾就退回当前点中的那一行"之类的兜底
             *（用户原话："你只需要操作我选中的文件，其他的不用管"）。
             *
             * 提示语来自 StatusText 的唯一来源，与「只解压」、清理类命令逐字相同 ——
             * 以前三处各写一套，用户看到三种说法，还以为是三个不同的问题。
             */
            _dialogService.ShowInfo(Tasks.Count == 0
                ? StatusText.TaskListEmptyPrompt
                : string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.NoCheckedTaskPromptFormat,
                    "一键处理",
                    Tasks.Count));

            AppendLog("WARN", StatusText.NoCheckedTaskLogFormat);

            return;
        }

        // 确认框在 EnterBusy 之前、整条流水线只问一次 —— 顺序与判据见 OneClickBatchDriver。
        await _batchDriver.RunAsync(targets).ConfigureAwait(true);
    }

    /// <summary>
    /// 真正干活的部分：**不带任何对话框**，返回结果对象。
    ///
    /// 为什么要单独一个入口：GUI 的 MessageBox 会真的弹出来（自动测试里没人点，就挂在那里了），
    /// 把"跑流程"和"弹窗"分开，流程本身才测得动。
    /// </summary>
    /// <param name="runOptions">
    /// 本次选项快照（规格 §9；null = 按设置走）。
    ///
    /// <para>
    /// ⚠ 它必须**一路传到最后一轮**：盘问只在 <see cref="RunAsync"/> 里做一次，
    /// 续解的第 2/3 轮、以及链结束后的源包补搬都用同一个快照 —— 中途重新问一次
    /// 或者中途退回设置值，都会让"这一次选的落点"在链的后半段静默变掉。
    /// </para>
    /// </param>
    internal async Task<OneClickOutcome> RunPipelineAsync(
        IReadOnlyList<ArchiveTask> firstRoundTargets,
        OneClickRunOptions? runOptions = null)
    {
        var state = new OneClickRunState();

        RunSnapshot snapshot = _roundRunner.Prepare(state, firstRoundTargets, runOptions);
        CurrentRunSnapshot = snapshot;

        /*
         * 特定解压 = 独立整理模式，不再由旧规则清单决定是否生效。
         * 旧规则注册表里的“只留一层内容”已经不影响定稿；这里只看总开关。
         */
        if (snapshot.UseSpecificExtraction)
        {
            return await RunSpecificBatchAsync(state.RootTargets, state.SnapshotOptions!, snapshot).ConfigureAwait(true);
        }

        return await _roundLoop.RunAsync(this, state, runOptions).ConfigureAwait(true);
    }

    /*
     * ===== 继续解作业区（实现见 OneClickContinuationWorkbench）=====
     *
     * 下面这些 internal 入口是轮内阶段（OneClickRoundRunner / OneClickSecondStepRunner）与既有用例的
     * 调用点 —— 它们按 `host.XXX` 这个名字指认，⛔ 判据只有一处，本类只保留一行转发，行为与改动前相同。
     */

    internal static bool ShouldScanProducedFile(
        string file,
        IReadOnlyCollection<string> sourcePaths,
        IReadOnlyCollection<string> knownTaskPaths) =>
        OneClickContinuationWorkbench.ShouldScanProducedFile(file, sourcePaths, knownTaskPaths);

    internal async Task ScanRoundAsync(int round, IReadOnlyList<ArchiveTask> roundTargets) =>
        await _continuation.ScanRoundAsync(round, roundTargets).ConfigureAwait(true);

    internal void DeselectProcessedTasks(List<ArchiveTask> processed) =>
        _continuation.DeselectProcessedTasks(processed);

    internal async Task<List<ArchiveTask>> AddInnerTasksAsync(
        IReadOnlyList<InnerArchiveCandidate> candidates) =>
        await _continuation.AddInnerTasksAsync(candidates).ConfigureAwait(true);

    internal async Task<(HashSet<string> ExistingFiles, HashSet<string> UnreadableDirectories)> SnapshotCandidateDirectoriesAsync(
        IReadOnlyList<ArchiveTask> roundTargets) =>
        await _continuation.SnapshotCandidateDirectoriesAsync(roundTargets).ConfigureAwait(true);

    internal static HashSet<string> BuildSourcePathSet(IEnumerable<ArchiveTask> tasks) =>
        OneClickContinuationWorkbench.BuildSourcePathSet(tasks);

    /// <summary>「后续卷」判断的唯一入口（测试与既有调用点按这个名字指认；实现见 <see cref="InnerLayerCandidateClassifier"/>）。</summary>
    internal static bool IsVolumeContinuationPart(string filePath) =>
        InnerLayerCandidateClassifier.IsVolumeContinuationPart(filePath);

    /*
     * ===== 分卷组事实（实现见 OneClickVolumeGroupFacts；既有用例按这些名字指认）=====
     */

    internal static ArchiveFixer.Detection.VolumeGroup? ResolveVolumeGroupFromDirectory(ArchiveTask? task) =>
        OneClickVolumeGroupFacts.ResolveVolumeGroupFromDirectory(task);

    internal static ArchiveFixer.Detection.VolumeGroup? PickFullerGroup(
        ArchiveFixer.Detection.VolumeGroup? byDirectory,
        ArchiveFixer.Detection.VolumeGroup? byName) =>
        OneClickVolumeGroupFacts.PickFullerGroup(byDirectory, byName);

    internal static bool NeedsVolumeGroupRefreshByName(ArchiveTask? task, string? currentPath) =>
        OneClickVolumeGroupFacts.NeedsVolumeGroupRefreshByName(task, currentPath);

    internal static ArchiveFixer.Detection.VolumeGroup? ResolveVolumeGroupFromDirectoryByName(
        ArchiveTask? task,
        string currentPath) =>
        OneClickVolumeGroupFacts.ResolveVolumeGroupFromDirectoryByName(task, currentPath);

    /*
     * ===== 结果（实现见 OneClickBatchFinalizer；本类只留既有入口）=====
     */

    /// <summary>批末副作用（实现见 <see cref="OneClickBatchFinalizer"/>；本类只留既有入口）。</summary>
    internal void FinalizeBatch() => _batchFinalizer.FinalizeBatch();

    /// <summary>一行汇总（实现见 <see cref="OneClickBatchFinalizer"/>）。</summary>
    internal string BuildSummaryLine(
        IReadOnlyList<ArchiveTask> targets,
        bool stopped = false,
        bool stopRequestedByUser = false,
        int continuationLayers = 0,
        bool hitRoundLimit = false,
        int pendingContinuation = 0,
        string stoppedPhase = "") =>
        _batchFinalizer.BuildSummaryLine(
            targets, stopped, stopRequestedByUser, continuationLayers, hitRoundLimit, pendingContinuation, stoppedPhase);

    internal static bool IsSuccessStatus(ArchiveTask task) => OneClickBatchFinalizer.IsSuccessStatus(task);

    internal static bool IsHandled(ArchiveTask task) => OneClickBatchFinalizer.IsHandled(task);

    internal static bool IsFailureStatus(ArchiveTask task) => OneClickBatchFinalizer.IsFailureStatus(task);

    internal static int CleanupSuccessfulProcessArtifacts(
        IEnumerable<ArchiveTask>? tasks,
        Action<string>? log = null) =>
        OneClickBatchFinalizer.CleanupSuccessfulProcessArtifacts(tasks, log);

    internal static bool IsContentCanonicalName(string path) =>
        OneClickBatchFinalizer.IsContentCanonicalName(path);
}

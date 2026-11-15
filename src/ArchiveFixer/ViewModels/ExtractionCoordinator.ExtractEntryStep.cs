using ArchiveFixer.BindingModels;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;


namespace ArchiveFixer.ViewModels;

/// <summary>解压入口（手动/一键）与核心批流程（第 11 块，从 Password 大杂烩搬出；同一 partial 类）。
/// 实现已搬到协作类 <see cref="ExtractEntryHandler"/>：本 partial 只留同签名的公开门面与最少 internal 访问器。</summary>
internal sealed partial class ExtractionCoordinator
{
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
    /// 所以"第一层只出过程物"时它把源包搬运留到链结束后补做（<see cref="CompleteRootSourcePackagesAfterChainAsync"/>）；
    /// 单层的「只解压」没有链可等，当场按"定稿 + 校验通过"处理。
    /// 定稿、校验、归集、工作区清理的行为两条路径完全一致。
    /// </para>
    /// </summary>
    /// <param name="extractIntoSourceFolder">
    /// 这一次按**「解压到当前文件夹」**办（用户 2026-09-27 新增的手动档；默认 false）。
    ///
    /// <para>
    /// 语义 = WinRAR 右键的同一句话：内容物**不建包名那一层**，直接落在源包所在的那个目录里
    /// （<c>111\222.rar</c> → <c>111\内容物</c>）。它只活在这一批里，⛔ 绝不写进设置；
    /// **一键处理 / 继续解永远不会传它**（用户红线：批量处理一律套包名那一层，不摊平）。
    /// 同名文件/文件夹按当前冲突档处理 —— 摊平会更容易撞名，这个代价用户 2026-09-27 明确接受。
    /// </para>
    /// </param>
    public Task StartExtractAsync(
        bool extractIntoSourceFolder = false,
        IReadOnlyList<ArchiveTask>? targetTasks = null) =>
        ExtractEntry.StartExtractAsync(extractIntoSourceFolder, targetTasks);

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
    /// <param name="continuationRound">
    /// 这一次是**同一条续解链的后续轮次**（一键处理第 2 轮起）。
    ///
    /// <para>它只决定一件事：批首要不要清掉"缺卷留到最后再判"的那份名单（用户 2026-10-05 口径 2）。
    /// 第 1 轮没解出来的第 1 卷很可能在第 2 轮才出现 ⇒ 那份名单必须**跨轮活到链尾**；
    /// 而用户重新点一次就是新的一批 ⇒ 那时清空（旧结论不许留到新批次上）。
    /// ⛔ 不改任何解压行为、不改轮数（轮数只有一个出口 <c>OneClickCoordinator.RoundLimit</c>）。</para>
    /// </param>
    public Task StartExtractForOneClickAsync(OneClickRunOptions? runOptions = null, bool continuationRound = false) =>
        ExtractEntry.StartExtractForOneClickAsync(runOptions, continuationRound);

    private ExtractEntryHandler? _extractEntry;
    private ExtractEntryHandler ExtractEntry => _extractEntry ??= new ExtractEntryHandler(this);

    private ExtractionBatchEntry? _batchEntry;
    internal ExtractionBatchEntry BatchEntry => _batchEntry ??= new ExtractionBatchEntry(this);

    /*
     * ===== 交给 ExtractEntryHandler 的最小访问面 =====
     *
     * `StartExtractCoreAsync` 整段搬到 `ViewModels/Extraction/Steps/ExtractEntryHandler.cs` 之后，
     * 协作类要调用下面这些私有成员 / 私有状态。⛔ 一律在本 partial 里加 internal 转发：
     * 不改其它 partial 的可见性、⛔ 不用反射；⛔ 这里只转发、不写判据 —— 判据的唯一来源
     * 仍是被转发的那个成员（与 `RegisterVolumeHandoff` / `RememberVolumeStepTaskForRound`
     * 那两处已有的转发同一个口径）。状态名沿用 `SpaceLedger` / `Settings` 那种
     * 「字段名去掉下划线」的写法；动作名后缀 `ForEntry` = 入口步骤这一层用的那个口子。
     */

    /// <summary>本批运行期设置（`_runSettings`；批首写入、批尾置空）。</summary>
    internal AppSettings? RunSettings { get => _runSettings; set => _runSettings = value; }

    /// <summary>空间不足模式下的并发上限（`_spaceLimit`；批首与动态重算两处都写它）。</summary>
    internal int SpaceLimit { get => _spaceLimit; set => _spaceLimit = value; }

    /// <summary>「等空位等得太久才补一句」的秒数阈值（`ThrottleNoticeThresholdSeconds`）。</summary>
    internal int ThrottleNoticeThresholdSecondsForEntry => ThrottleNoticeThresholdSeconds;

    /// <summary>本批同名冲突询问次数（`_conflictStep.PromptCount`，批末那一行的证据）。</summary>
    internal int ConflictPromptCount => _conflictStep.PromptCount;

    /// <summary>多层 + 空间小的批前提醒（唯一出口仍是 reporter 自己）。</summary>
    internal ExtractionMultiLayerSpaceRiskReporter MultiLayerSpaceRiskReporterForEntry =>
        MultiLayerSpaceRiskReporter;

    /// <summary>批首那四句记账清零（原 `StartExtractCoreAsync` 开头，顺序与原来一致）。</summary>
    internal void ResetBatchPurgeCounters()
    {
        _batchPurgedTaskCount = 0;
        _batchPurgedBytes = 0;
        _batchPurgedPermanently = false;
        _stopNoticeLogged = false;
    }

    internal ExtractionSchedulePlan BuildSchedulePlanForEntry(
        IReadOnlyList<ArchiveTask> tasks,
        int requestedParallel,
        bool spaceTightOrdering = false) =>
        BuildSchedulePlan(tasks, requestedParallel, spaceTightOrdering);

    internal int SpaceTightParallelForEntry(ExtractionSchedulePlan plan) =>
        ResolveSpaceTightParallelForBatch(plan);

    internal void PrepareRestHandlingForEntry() => PrepareRestHandlingForBatch();

    internal void LogRestHandlingForEntry() => LogRestHandlingForBatch();

    internal void LogExtractionLimitsForEntry() => LogExtractionLimitsForBatch();

    internal long ReserveSpaceBytesForEntry => ReserveSpaceBytes;

    internal void StartSpaceTrendMonitorForEntry(string probePath) => StartSpaceTrendMonitor(probePath);

    internal void StopSpaceTrendMonitorForEntry() => StopSpaceTrendMonitor();

    internal int ResolveDynamicSpaceParallelForEntry(
        IReadOnlyList<ScheduledExtractionItem> pending,
        int runningCount) =>
        ResolveDynamicSpaceParallel(pending, runningCount);

    internal void LogStopRequestOnceForEntry() => LogStopRequestOnce();

    internal void MarkSpaceBlockedForEntry(
        ArchiveTask task,
        ScheduledExtractionItem item,
        SpaceGateDecision gate) =>
        MarkSpaceBlocked(task, item, gate);

    internal void NotifySpaceBlockedOnceForEntry(
        ArchiveTask task,
        long requiredBytes,
        long availableBytes,
        long shortfallBytes) =>
        NotifySpaceBlockedOnce(task, requiredBytes, availableBytes, shortfallBytes);

    internal Task ShowPasswordFailuresSummaryForEntryAsync(bool showDialog) =>
        ShowPasswordFailuresSummaryAsync(showDialog);

    internal void AppendTaskRowSnapshotForEntry() => AppendTaskRowSnapshot();

    internal void AppendBatchSummaryForEntry(IReadOnlyList<ArchiveTask>? tasks) => AppendBatchSummary(tasks);

    internal void RetryPendingPassThroughRestPurgesForEntry() => RetryPendingPassThroughRestPurges();

    internal (int Tasks, long Bytes, bool Permanent) ReadBatchPurgeForEntry() => ReadBatchPurge();

    internal void ReportSpaceBlockedTasksForEntry() => ReportSpaceBlockedTasks();

    internal void RemoveEmptyWorkspaceShellsForEntry() => RemoveEmptyWorkspaceShells();

    internal void RemoveEmptyTargetShellIfWeCreatedItForEntry() => RemoveEmptyTargetShellIfWeCreatedIt();

    internal void LogOneClickPasswordHintForEntry(IReadOnlyList<ArchiveTask> selectedTasks) =>
        LogOneClickPasswordHint(selectedTasks);

    internal Task PromptForManualBatchPasswordForEntryAsync(IReadOnlyList<ArchiveTask> tasks) =>
        PromptForManualBatchPasswordAsync(tasks);

}

using ArchiveFixer.BindingModels;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;


namespace ArchiveFixer.ViewModels;

/// <summary>
/// 解压入口（手动「只解压」/「一键处理」）的宿主门面（M5：原
/// <c>ExtractionCoordinator.ExtractEntryStep.cs</c> 已整份删除，批流程本体在 <see cref="ExtractEntryHandler"/>）。
///
/// <para>本 partial 只留三类成员：① 两个公开入口（界面 / 一键 / 用例都按
/// <c>ExtractionCoordinator.StartExtract*Async</c> 调用，签名与说明逐字不变）；② 宿主侧唯一的三个
/// 协作类实例 —— <see cref="ExtractEntry"/> / <see cref="BatchEntry"/> / <see cref="Schedule"/>
/// （三者都有实例状态 ⇒ 全批共用同一个实例，⛔ 别处不许 new）；③ 入口步骤还在用、但目标仍是
/// **别的分片私有成员**的那几个访问器（目标一旦被各自的 M5 迁移改成 internal，就按 <c>_host.X</c>
/// 直调并删掉这里的一行）。⛔ 本文件不放任何判据：判据的唯一来源仍是被转发的那个成员。</para>
/// </summary>
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

    /// <summary>入口编排协作类（懒建一次；实现见 <see cref="ExtractEntryHandler"/>）。</summary>
    private ExtractEntryHandler ExtractEntry => _extractEntry ??= new ExtractEntryHandler(this);

    private ExtractionBatchEntry? _batchEntry;

    /// <summary>批流程账目与"等待 / 空位补跑"循环（懒建一次；同一批内共用同一个实例）。</summary>
    internal ExtractionBatchEntry BatchEntry => _batchEntry ??= new ExtractionBatchEntry(this);

    /// <summary>
    /// 空间体检/趋势/调度计划/空间闸门/多层风险/批级上限的执行体（第 12 块）。
    /// 原 <c>ExtractionCoordinator.ExtractionScheduleStep.cs</c> 已删除：实现与状态都归
    /// <see cref="ExtractionScheduleHandler"/>，本类只保留这一个懒建入口（同 <see cref="ExtractEntry"/> /
    /// <see cref="BatchEntry"/> 的寿命口径：全批共用同一个实例）。
    /// </summary>
    private ExtractionScheduleHandler? _scheduleHandler;

    internal ExtractionScheduleHandler Schedule => _scheduleHandler ??= new ExtractionScheduleHandler(this);

    /// <summary>本批运行期设置（`_runSettings`；批首写入、批尾置空）。</summary>
    internal AppSettings? RunSettings { get => _runSettings; set => _runSettings = value; }

    /// <summary>批首那四句记账清零（原 `StartExtractCoreAsync` 开头，顺序与原来一致；实现在 <see cref="LogCaptureHandler"/>）。</summary>
    internal void ResetBatchPurgeCounters() => LogCapture.ResetBatchPurgeCounters();

    /*
     * ===== 仍留在宿主这一侧的入口步骤访问器 =====
     *
     * 目标成员仍是别的分片的 private（`PrepareRestHandlingForBatch` / `RemoveEmptyWorkspaceShells` /
     * `RemoveEmptyTargetShellIfWeCreatedIt` / `ShowPasswordFailuresSummaryAsync` /
     * `LogOneClickPasswordHint` / `PromptForManualBatchPasswordAsync` / `_conflictStep`）：
     * 同一个 partial 类可以在本文件里直接读写它们，所以这几个访问器先留在这里，
     * ⛔ 不去改那些正在被别的 M5 迁移批次改写的文件。
     *
     * 其余"目标是 Schedule / LogCapture / VolumeLedger"的访问器已删除：
     * <see cref="ExtractEntryHandler"/> 一律直调 `_host.Schedule.X` / `_host.LogCapture.X` /
     * `_host.VolumeLedger.X`（唯一实例仍由宿主持有）。
     */

    /// <summary>「等空位等得太久才补一句」的秒数阈值（常量在 <c>ExtractionCoordinator.cs</c>）。</summary>
    internal int ThrottleNoticeThresholdSecondsForEntry => ThrottleNoticeThresholdSeconds;

    /// <summary>本批同名冲突询问次数（`_conflictStep.PromptCount`，批末那一行的证据）。</summary>
    internal int ConflictPromptCount => _conflictStep.PromptCount;

    internal void PrepareRestHandlingForEntry() => PrepareRestHandlingForBatch();

    internal void LogRestHandlingForEntry() => LogRestHandlingForBatch();

    internal Task ShowPasswordFailuresSummaryForEntryAsync(bool showDialog) =>
        ShowPasswordFailuresSummaryAsync(showDialog);

    internal void RemoveEmptyWorkspaceShellsForEntry() => RemoveEmptyWorkspaceShells();

    internal void RemoveEmptyTargetShellIfWeCreatedItForEntry() => RemoveEmptyTargetShellIfWeCreatedIt();

    internal void LogOneClickPasswordHintForEntry(IReadOnlyList<ArchiveTask> selectedTasks) =>
        LogOneClickPasswordHint(selectedTasks);

    internal Task PromptForManualBatchPasswordForEntryAsync(IReadOnlyList<ArchiveTask> tasks) =>
        PromptForManualBatchPasswordAsync(tasks);

    // ================================================================
    // 批前提醒 / 确认框 / 并行与输出目录（M5：原 <c>ExtractionCoordinator.BatchReminderStep.cs</c> 已整份删除）
    // 实现整块在 <see cref="BatchReminderHandler"/>；本类只留：① 唯一实例 <see cref="BatchReminders"/>；
    // ② 跨 partial 的两个 UI 线程入口；③ 「等空位太久才补一句」的秒数阈值（读者在入口步骤 partial）。
    // 其余调用点一律直接走 BatchReminders（测试同口径）。
    // ================================================================

    private BatchReminderHandler? _batchReminderHandler;

    /// <summary>
    /// 批前提醒 / 确认框 / 并行与输出目录的唯一实例（懒建一次；实现见 <see cref="BatchReminderHandler"/>）。
    /// ⚠ 它有实例状态（本次运行不再提醒 / 批记账 / 预扫结论 / 输出目录只开一次）⇒
    /// 全批只能有这一个实例，⛔ 不许在别处 <c>new BatchReminderHandler(this)</c>。
    /// </summary>
    internal BatchReminderHandler BatchReminders => _batchReminderHandler ??= new BatchReminderHandler(this);

    /// <summary>UI 线程确认框的唯一出口（实现在 <see cref="BatchReminderHandler.DispatcherSafeConfirmAsync"/>；分卷预检那一片要用）。</summary>
    private Task<bool> DispatcherSafeConfirmAsync(string message) =>
        BatchReminders.DispatcherSafeConfirmAsync(message);

    /// <summary>UI 线程目录选择框的唯一出口（实现在 <see cref="BatchReminderHandler.ShowFolderBrowserOnUiThreadAsync"/>；同上）。</summary>
    private Task<string> ShowFolderBrowserOnUiThreadAsync() =>
        BatchReminders.ShowFolderBrowserOnUiThreadAsync();

    /// <summary>排队等空位**超过这个秒数**才单独写一行（否则只进批末汇总；用户 2026-09-26 第 45 条）。读者是入口步骤 partial 的 <c>ThrottleNoticeThresholdSecondsForEntry</c>。</summary>
    private const int ThrottleNoticeThresholdSeconds = 30;

    // ================================================================
    // 批末一条汇总（M5：原 <c>ExtractionCoordinator.BatchSummaryStep.cs</c> 已整份删除）
    // ================================================================

    private BatchSummaryHandler? _batchSummaryHandler;

    /// <summary>
    /// 批末「本批汇总」的唯一落点（懒建一次；实现见 <see cref="BatchSummaryHandler"/>，
    /// ⛔ 协调器不再留同名薄转发、也不再为它单开 partial）。
    ///
    /// <para>它只持有宿主、没有第二份状态（数法仍只在 <c>BatchOutcomeTally</c> 一处）⇒
    /// <c>??=</c> 懒建足够，不存在"造出第二个实例就把账分成两半"的问题。</para>
    /// </summary>
    internal BatchSummaryHandler BatchSummary => _batchSummaryHandler ??= new BatchSummaryHandler(this);


    // 收片采纳与路径红线（M5：原 <c>ExtractionCoordinator.PieceAdoptionStep.cs</c> 已整份删除）
    //
    // 实现（用户源路径集合/别名清理/产出方两本账/源路径红线/任务表路径同步/接片判据）
    // 整块在 <see cref="PieceAdoptionHandler"/>；本类只留唯一实例，⛔ 不留任何同名薄转发。
    // 接片判据实例（<see cref="ExtractionPieceHandoverRules"/>）也归它持有，生命周期与原来一致。
    // ================================================================

    private PieceAdoptionHandler? _pieceAdoptionHandler;

    /// <summary>
    /// 收片采纳与路径红线的唯一落点（懒建一次；实现见 <see cref="PieceAdoptionHandler"/>，
    /// ⛔ 协调器不再留同名薄转发）。别名清理 / 产出方两本账 / 接片判据都走它。
    /// </summary>
    internal PieceAdoptionHandler PieceAdoption => _pieceAdoptionHandler ??= new PieceAdoptionHandler(this);


}

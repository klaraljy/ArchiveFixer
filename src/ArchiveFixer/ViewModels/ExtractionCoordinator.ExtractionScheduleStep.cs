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

/// <summary>
/// 空间体检/趋势/调度计划/空间闸门/多层风险/批级上限（第 12 块）——实现已按职责搬到
/// <see cref="ExtractionScheduleHandler"/>（<c>ViewModels/Extraction/Steps/ExtractionScheduleHandler.cs</c>），
/// 这里只留同签名薄转发与仍被其他分片直接读写的本批状态。
///
/// <para>⛔ 本分片不再放任何调度判据：搬走的是**实现**，调用点（<c>ExtractionBatchEntry</c> /
/// <c>ExtractionCoordinator.ExtractEntryStep</c> / <c>ExtractionTaskRunner</c> / <c>ExtractionPreflight</c> /
/// <c>OneClickRoundRunner</c> / <c>SpaceAdviceCoordinator</c> / <c>SpaceTextCoordinator</c>）一个字都没改。</para>
///
/// <para>可变记账（探测路径 / 空间侦察器 / 逐任务运行期预留 / 被空间门拦下的清单）随之归
/// <see cref="ExtractionScheduleHandler"/> 持有：本类只有**一个**宿主实例，且用 <c>??=</c> 懒建一次
/// ⇒ 生命周期与原来完全一致。留在本类的只有其他分片直接读写的成员（空间账本、被拦清单开关、
/// 并发上限、预算上限、多层风险报表器、两个可注入覆盖点）与 <c>DescribeMultiLayerSpaceRisk</c> 那个静态门面。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    /// <summary>搬出去的那一份空间规划/空间门实现（懒建一次；宿主就是本类，全类共用同一个实例）。</summary>
    private ExtractionScheduleHandler? _scheduleHandler;

    private ExtractionScheduleHandler ScheduleHandler => _scheduleHandler ??= new ExtractionScheduleHandler(this);

    internal ExtractionSchedulePlan BuildSpaceAdvice(IReadOnlyList<ArchiveTask> tasks) =>
        ScheduleHandler.BuildSpaceAdvice(tasks);

    internal string DescribeSpaceMode() =>
        ScheduleHandler.DescribeSpaceMode();

    internal long? ProbeAvailableSpace(string path) =>
        ScheduleHandler.ProbeAvailableSpace(path);

    private void StartSpaceTrendMonitor(string probePath) =>
        ScheduleHandler.StartSpaceTrendMonitor(probePath);

    private void StopSpaceTrendMonitor() =>
        ScheduleHandler.StopSpaceTrendMonitor();

    internal bool RecordSpaceTrendPostscript(string note) =>
        ScheduleHandler.RecordSpaceTrendPostscript(note);

    internal void RecordSpaceTrend(string note) =>
        ScheduleHandler.RecordSpaceTrend(note);

    internal void ResetSpacePlanningState() =>
        ScheduleHandler.ResetSpacePlanningState();

    internal ScheduledTaskRuntime GetOrCreateRuntime(ArchiveTask task) =>
        ScheduleHandler.GetOrCreateRuntime(task);

    private ExtractionSchedulePlan BuildSchedulePlan(
        IReadOnlyList<ArchiveTask> tasks,
        int requestedParallel,
        bool spaceTightOrdering = false) =>
        ScheduleHandler.BuildSchedulePlan(tasks, requestedParallel, spaceTightOrdering);

    private int ResolveDynamicSpaceParallel(
        IReadOnlyList<ScheduledExtractionItem> pending,
        int runningCount) =>
        ScheduleHandler.ResolveDynamicSpaceParallel(pending, runningCount);

    internal bool? ResolveWorkspaceVolumeShare(string? targetPath) =>
        ScheduleHandler.ResolveWorkspaceVolumeShare(targetPath);

    internal bool DirectReadAppliesTo(ArchiveTask? task) =>
        ScheduleHandler.DirectReadAppliesTo(task);

    internal string ResolveSpaceProbePath(IReadOnlyList<ArchiveTask> tasks) =>
        ScheduleHandler.ResolveSpaceProbePath(tasks);

    internal string ResolveSpaceProbePathOrBatch(ArchiveTask task) =>
        ScheduleHandler.ResolveSpaceProbePathOrBatch(task);

    private void MarkSpaceBlocked(ArchiveTask task, ScheduledExtractionItem item, SpaceGateDecision gate) =>
        ScheduleHandler.MarkSpaceBlocked(task, item, gate);

    internal void MarkSpaceBlockedCore(
        ArchiveTask task,
        string message,
        long requiredBytes,
        long availableBytes,
        long shortfallBytes) =>
        ScheduleHandler.MarkSpaceBlockedCore(task, message, requiredBytes, availableBytes, shortfallBytes);

    private void NotifySpaceBlockedOnce(ArchiveTask task, long requiredBytes, long availableBytes, long shortfallBytes) =>
        ScheduleHandler.NotifySpaceBlockedOnce(task, requiredBytes, availableBytes, shortfallBytes);

    private void ReportSpaceBlockedTasks() =>
        ScheduleHandler.ReportSpaceBlockedTasks();

    private void LogExtractionLimitsForBatch() =>
        ScheduleHandler.LogExtractionLimitsForBatch();

    internal MultiLayerSpaceRisk EvaluateMultiLayerSpaceRisk(ExtractionSchedulePlan plan) =>
        ScheduleHandler.EvaluateMultiLayerSpaceRisk(plan);

    internal SpaceGateDecision ReconcileReservation(ArchiveTask task, TaskSpaceEstimate refined) =>
        ScheduleHandler.ReconcileReservation(task, refined);

    /*
     * 下面是留在宿主这一侧的本批状态（其他分片仍直接读写它）：成员本体一个字节不动，
     * 判据/写点仍只有原来那一处。调度处理器要读的只有"保留余量"那一个私有属性，
     * 用下面那个最小 internal 访问器把读取口径提出来 —— ⛔ 不用反射、不新增第二套判断。
     */

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

    private long ReserveSpaceBytes => SpaceReserveOverride ?? SpaceGate.DefaultReserveBytes;

    /// <summary>
    /// 调度处理器要读的"保留余量"（唯一实现在上面；这里只把读取口径提到 internal，可见性不改）。
    /// </summary>
    internal long ReserveSpaceBytesForSchedule => ReserveSpaceBytes;

    /// <summary>
    /// 解压前的资源预算上限（不变量 8），四条全部来自设置（用户 2026-09-25 第 36 条）。
    ///
    /// <para>为什么收口成一个属性：这套上限有**四个**用它的地方 —— ①解压前预检
    /// （<see cref="ResourceBudget.CheckBeforeExtract"/>）②产物事后核算 ③内嵌 ZIP 直读
    /// ④递归核心的累计上限。以前①③④各写各的默认值（预检与直读是 <c>ResourceBudgetOptions.Default</c>、
    /// 递归是 <c>RecursionLimits.Default</c> 的 50 GiB），用户在设置里改一处并不会同时生效 ——
    /// 现在只有这一个出口，口径不可能再分叉。</para>
    ///
    /// <para>旧写法：解压前预检直接用 <c>new ResourceBudget()</c>（= 硬编码 4 GiB 单文件 / 20 GiB 总量），
    /// 界面上一个字都没有 —— 他 12 GiB 的分卷包被拦下时看到的就是那句判决。</para>
    /// </summary>
    internal ResourceBudgetOptions BudgetLimits => ResourceBudgetOptions.FromSettings(Settings);

    /// <summary>本批的空间预留账本（串行时也建，只是永远只有一个任务在账上）。</summary>
    internal SpaceReservationLedger? _spaceLedger;

    /// <summary>
    /// 本批那条"中途空间不足"的纯提示**弹过没有**（用户 2026-09-29 第 2 条：
    /// "同一批只弹一次（合并计数）"）。
    ///
    /// <para>后面的同类情况只写日志 —— 一批几十个包时逐个弹等于把批卡死在他面前。</para>
    /// </summary>
    internal bool _spaceBlockedNoticeShown;

    /// <summary>
    /// 本批是不是按**「空间不足」模式**跑（批首定一次；用户 2026-09-27 拍板的模式）。
    ///
    /// <para>它是一个**运行期**开关（<see cref="MainViewModel.SpaceTightMode"/>，⛔ 不写设置、不记忆），
    /// 但一经开跑就**钉死在这一批上**：同一批里不许"前半段边解边删源包、后半段又不删了" ——
    /// 那会让空间账面与用户看到的行为前后矛盾。批首定一次、批内只读它。</para>
    ///
    /// <para>它一共改四件事（每一件都写在下面对应位置的注释里）：
    /// ① 并发档（忽略设置里的档与「全速」，按 <c>ExtractionScheduler.ResolveSpaceTightParallelCount</c>）；
    /// ② 排序（按净占用从小到大，先解"解完占地最少"的）；
    /// ③ 源包处理（✅ 定稿 + 校验通过 → **当场永久删除源包**，不进其余物）；
    /// ④ 删除操作（其余物 = 过程物，任务成功后彻底删除）。
    /// ⛔ 它**不动**任何设置：用户设置里的三档在这次运行里一个字节都不会被改写。</para>
    /// </summary>

    private int _spaceLimit = int.MaxValue;

    private ExtractionMultiLayerSpaceRiskReporter? _multiLayerSpaceRiskReporter;
    private ExtractionMultiLayerSpaceRiskReporter MultiLayerSpaceRiskReporter =>
        _multiLayerSpaceRiskReporter ??= new ExtractionMultiLayerSpaceRiskReporter(this);

    internal static string DescribeMultiLayerSpaceRisk(MultiLayerSpaceRisk risk) =>
        ExtractionMultiLayerSpaceRiskReporter.DescribeMultiLayerSpaceRisk(risk);

}

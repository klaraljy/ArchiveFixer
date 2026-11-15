using ArchiveFixer.Security;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Storage;

/// <summary>
/// 一次「一键处理 / 只解压」的**执行计划**：按空间需求排好的顺序 + 当前可用空间下建议的并行档
/// （用户 2026-09-22 需求第 2 条）。
/// </summary>
public sealed class ExtractionSchedulePlan
{
    /// <summary>执行顺序（按空间需求**从小到大**）。</summary>
    public IReadOnlyList<ScheduledExtractionItem> Ordered { get; init; } = Array.Empty<ScheduledExtractionItem>();

    /// <summary>
    /// **单独跑也放不下**的任务（需求超过整个预算）—— "因为空间不足被跳过"的就是它们。
    ///
    /// <para>刻意**不**把"这一批并行的位置里挤不下"的那些算进来：那些任务只是排队，
    /// 前面的跑完就轮到它。把它们报成"跳过"会让用户以为整批都没跑。</para>
    /// </summary>
    public IReadOnlyList<ScheduledExtractionItem> BlockedAtPlanTime { get; init; } = Array.Empty<ScheduledExtractionItem>();

    /// <summary>当前可用空间下建议的并行档（0 = 连最小的包都放不下）。</summary>
    public int RecommendedParallelCount { get; init; }

    /// <summary>用户选的（或设置里的）并行档。</summary>
    public int RequestedParallelCount { get; init; }

    /// <summary>用户档与空间建议合成后的唯一并发结论。</summary>
    public ConcurrencyLimit? Limit { get; init; }

    /// <summary>排计划时的可用字节数（-1 = 取不到）。</summary>
    public long AvailableBytes { get; init; }

    /// <summary>要保留的余量字节数。</summary>
    public long ReserveBytes { get; init; }

    /// <summary>
    /// 可用于并行的预算 = 可用 − 余量（取不到可用空间时为 -1）。
    ///
    /// <para>它同时是"空间不足模式该开几个"那一档的输入（见
    /// <see cref="ExtractionScheduler.ResolveSpaceTightParallelCount"/>）——
    /// 两处各算一遍预算必然分叉，所以预算只在这里定义一次。</para>
    /// </summary>
    public long BudgetBytes => AvailableBytes < 0
        ? -1L
        : Math.Max(0L, AvailableBytes - ReserveBytes);

    /// <summary>排序与建议并行数的依据（进日志，用户要能复核）。</summary>
    public string Basis { get; init; } = string.Empty;

    /// <summary>
    /// 这一批是**按什么排的顺序**（进日志的那一句话）。
    ///
    /// <para>它必须跟着真实的排序键走：普通档按「本次要从可用空间里新写多少」（内容物 + 过程物，
    /// 也就是 <see cref="ScheduledExtractionItem.RequiredBytes"/>），空间不足模式按「净占用」
    /// （解完之后真正留在盘上的字节数）—— 两句话要是说反了，
    /// 用户按日志核对顺序时就会得出"程序排序排错了"的结论。</para>
    /// </summary>
    public string OrderBasis { get; init; } = DefaultOrderBasis;

    internal const string DefaultOrderBasis = "按空间需求（本次要从可用空间里新写多少）从小到大";

    /// <summary>空间不足模式的排序口径（唯一措辞来源，日志与界面都引它）。</summary>
    internal const string NetOccupancyOrderBasis = "按净占用从小到大（空间不足模式：先解「解完占地最少」的包）";

    /// <summary>空间不足模式（2026-10-25 新口径）：按源包总体积从大到小。</summary>
    internal const string SourceSizeDescendingOrderBasis = "按源包总体积从大到小（空间不足模式：大包先解）";
    internal const string DurationDescendingOrderBasis = "按预计耗时从大到小（长任务先占槽位，小任务补空）";

    /// <summary>给用户看的一句话："当前可用 10 GB，建议并行 2 个"。</summary>
    public string ParallelAdviceText()
    {
        if (AvailableBytes < 0)
        {
            return "未能取到目标盘可用空间，本次不做空间门判断，也无法按空间给出并行建议";
        }

        if (RecommendedParallelCount <= 0)
        {
            return $"当前可用 {TaskSpaceEstimate.FormatSize(AvailableBytes)}，"
                   + "连需求最小的那个包都放不下 —— 建议先清理「其余物」或换一个输出盘";
        }

        return $"当前可用 {TaskSpaceEstimate.FormatSize(AvailableBytes)}，建议并行 {RecommendedParallelCount} 个";
    }

    /// <summary>日志里"顺序明细"最多列几条（其余的只报个数 —— 用户 2026-09-25 第 44 条）。</summary>
    private const int SpacePlanDetailLimit = 5;

    /// <summary>进日志的多行说法：顺序 + 建议 + 计划时就被挡下的。</summary>
    public IReadOnlyList<string> DescribeLines()
    {
        var lines = new List<string>
        {
            /*
             * ⚠ 括号里刻意写"设置里的档"而不是"当前档"：①页「空间不足」模式开着时，
             * 本批**不用**那一档（并发由空间自己定，另有一条 WARN 说明）——
             * 写"当前档"会让人把设置里的数字当成这一批真的在用的数字。
             */
            $"空间调度：{OrderBasis}排了 {Ordered.Count} 个任务；{ParallelAdviceText()}（本批上限：{RequestedParallelCount}）",
            "空间调度依据：" + Basis
        };

        /*
         * 「第 N 位」= **按空间需求排出来的真实位次**（明细本来就是升序印的，它必须跟着递增）；
         * 「本批第 M 个」= 它在本批任务清单里的序号（`ScheduledExtractionItem.OriginalIndex`，
         * 那个字段的固有用途是"需求相同时的兜底 tiebreaker"）。
         *
         * ⛔ 别再把 OriginalIndex 单独写成「顺序 N」：2026-10-02 真机日志里因此出现
         * 「顺序 6」排在「顺序 5」前面 —— 排序其实是对的，读的人却当场得出"程序排错了"的结论。
         * ⛔ 也不写"①页第 M 行"：一键处理可能只跑勾选的那几个，那时 M 只是本批内的序号。
         */
        int position = 0;

        foreach (ScheduledExtractionItem item in Ordered.Take(SpacePlanDetailLimit))
        {
            position++;
            lines.Add($"  第 {position} 位（本批第 {item.OriginalIndex + 1} 个）：{item.Estimate.Describe()}");
        }

        /*
         * 明细只列前几条（用户 2026-09-25 第 44 条：一次导出 713 KB，"每次不需要汇报得那么详细"）。
         * 68 个包就是 68 行、每行一条长路径 —— "谁排第几、为什么这么排"在前几条里已经看得很清楚，
         * 剩下的只是把同一句话重复几十遍。⛔ 不静默截断：还剩多少必须写出来。
         */
        if (Ordered.Count > SpacePlanDetailLimit)
        {
            lines.Add($"  …还有 {Ordered.Count - SpacePlanDetailLimit} 个任务没列出来（明细见各自的任务详情）");
        }

        foreach (ScheduledExtractionItem item in BlockedAtPlanTime)
        {
            lines.Add(
                $"  单独跑也放不下（会被跳过）：{item.Estimate.DisplayName} —— "
                + $"需要 {TaskSpaceEstimate.FormatSize(item.RequiredBytes)}，"
                + $"可用 {TaskSpaceEstimate.FormatSize(Math.Max(0, AvailableBytes))}，"
                + $"差 {TaskSpaceEstimate.FormatSize(item.ShortfallBytes)}");
        }

        return lines;
    }
}

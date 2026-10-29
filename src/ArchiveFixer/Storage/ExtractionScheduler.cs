using System;
using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;

namespace ArchiveFixer.Storage
{
    /// <summary>排进本次执行顺序的一个任务（带它的空间账面）。</summary>
    public sealed class ScheduledExtractionItem
    {
        public ScheduledExtractionItem(ArchiveTask task, TaskSpaceEstimate estimate, int originalIndex)
        {
            Task = task;
            Estimate = estimate;
            OriginalIndex = originalIndex;
        }

        public ArchiveTask Task { get; }

        public TaskSpaceEstimate Estimate { get; }

        /// <summary>任务在用户列表里的原始序号（同需求时按它保持稳定顺序，不把用户看到的顺序打乱）。</summary>
        public int OriginalIndex { get; }

        /// <summary>
        /// 排计划那一刻它需要的字节数（= <see cref="TaskSpaceEstimate.FreeSpaceDemandBytes"/>：
        /// 内容物 + 过程物，也就是"这次要从可用空间里新写多少"）。
        ///
        /// <para>⚠ 它**不是** <see cref="TaskSpaceEstimate.PeakBytes"/>（那个含源包）。
        /// 源包已经在盘上、不在可用空间里，拿峰值当需求就是把源包算两遍 ——
        /// 2026-09-29 真机上正是这一处把一组 17.7 GiB 的三卷包判成"整盘都放不下"。</para>
        /// </summary>
        public long RequiredBytes => Estimate.FreeSpaceDemandBytes;

        /// <summary>
        /// 排计划那一刻**在并发集合里**是否轮得到它（它前面那些放得下的任务 + 它自己不超预算）。
        ///
        /// <para><b>它不是放行凭据</b> —— 真正的门在启动那一刻（<see cref="SpaceReservationLedger"/>）。
        /// 这个字段只用来算"建议并行几个"。</para>
        /// </summary>
        public bool FitsAtPlanTime { get; init; }

        /// <summary>
        /// **单独跑也放不下**（需求就超过了整个预算）。
        ///
        /// <para>它才是"因为空间不足被跳过"的判据：<see cref="FitsAtPlanTime"/> 为 false 只说明
        /// "现在这一批并行的位置里挤不下它"，等前面的跑完它照样能上 —— 那不是跳过，是排队。</para>
        /// </summary>
        public bool FitsAlone { get; init; }

        /// <summary>差多少字节（能排上时为 0）。单独跑也放不下时 = 需求 − 预算。</summary>
        public long ShortfallBytes { get; init; }
    }

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
                $"空间调度：{OrderBasis}排了 {Ordered.Count} 个任务；{ParallelAdviceText()}（设置里的档：{RequestedParallelCount}）",
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

    /// <summary>
    /// **空间感知的调度**（用户 2026-09-22 需求第 2 条）。纯计算，不碰磁盘、不碰 WPF。
    ///
    /// <para><b>两条规则，缺一不可</b>：</para>
    /// <list type="number">
    /// <item><description><b>先排序</b>：按空间需求（<see cref="ScheduledExtractionItem.RequiredBytes"/>，
    /// 也就是"本次要从可用空间里新写多少"）**从小到大**排。用户点名的反例是
    /// "一开始就去解 5G+6G 的文件，这样并行两个都弄不了" —— 从小到大排之后，
    /// 排在最前面的一定是能塞进去的那些小包。</description></item>
    /// <item><description><b>只有装得下才启动</b>：本类给出的 <see cref="ScheduledExtractionItem.FitsAtPlanTime"/>
    /// 只是**计划时**的快照，真正的放行判断在执行循环里每个任务启动前做
    /// （<see cref="SpaceReservationLedger.TryReserve"/>）—— 空间是随跑随变的，
    /// 拿计划时的数字当放行凭据，等于把"当时够"当成"一直够"。</description></item>
    /// </list>
    ///
    /// <para><b>建议并行数怎么算</b>：把排好序的需求从最小的开始累加，累加和（含余量）
    /// 不超过可用空间的最多几个 —— 这就是"这些任务同时达到各自的峰值"也不会撑爆盘的最大并行数。
    /// 它是**建议**：用户在界面上选的档位不会因此被静默改掉，但每个任务启动前都会真的判一次空间。</para>
    /// </summary>
    public static class ExtractionScheduler
    {
        /// <summary>并发档的硬顶（与设置项 <c>MaxParallelExtractCount</c> 的 1–8 一致）。</summary>
        public const int ParallelCeiling = 8;

        /// <summary>大任务（单个需求 ≥ 预算 1/4）的并发保护上限；小任务不受这个 3 的限制。</summary>
        public const int SpaceTightLargeTaskParallelCeiling = 3;

        private const int SpaceTightLargeTaskBudgetDivisor = 4;

        /// <summary>
        /// 界面上允许用户选的并发档（用户 2026-09-22 原话："可以让用户选择解压时并行解压的文件数量"）。
        /// 8 是硬顶：再往上不是"帮用户省事"，而是把磁盘和内存一起打满。
        /// </summary>
        public static IReadOnlyList<int> AllowedParallelCounts { get; } = new[] { 1, 2, 3, 4, 8 };

        /// <summary>把一个并发数夹到合法档位（不在这里的档位取最接近的更大档，兜底 1）。</summary>
        public static int NormalizeParallelCount(int value)
        {
            if (value <= 1)
            {
                return 1;
            }

            foreach (int allowed in AllowedParallelCounts)
            {
                if (value <= allowed)
                {
                    return allowed;
                }
            }

            return ParallelCeiling;
        }

        /// <summary>
        /// 排计划。
        /// </summary>
        /// <param name="tasks">本次要处理的任务（按用户列表顺序）。</param>
        /// <param name="estimate">任务 → 空间估算（粗估或精估都行，由调用方决定精度）。</param>
        /// <param name="availableBytes">目标盘可用字节数（null = 取不到）。</param>
        /// <param name="reserveBytes">要保留的余量（null = 用 <see cref="SpaceGate.DefaultReserveBytes"/>）。</param>
        /// <param name="requestedParallelCount">用户选 / 设置里的并行档（会被夹到 1..8）。</param>
        /// <param name="sortKey">
        /// **排序键**（不传 = 按 <see cref="ScheduledExtractionItem.RequiredBytes"/>，也就是
        /// "本次要从可用空间里新写多少"= 内容物 + 过程物）。
        ///
        /// <para>空间不足模式传「净占用」（<see cref="TaskSpaceEstimate.ContentBytes"/>）：
        /// 那个模式下每个包一校验通过就把它自己的源包删掉，所以"解完真正留在盘上"的字节数
        /// 才是决定后面还能解几个的量 —— 先解净占用小的，盘上越跑越宽。
        /// ⛔ 它**只改顺序**：放行判断（<see cref="ScheduledExtractionItem.FitsAlone"/> 与真正的空间门）
        /// 仍然按 <see cref="TaskSpaceEstimate.FreeSpaceDemandBytes"/> 算，
        /// 绝不允许因为"净占用小"就放行一个需求装不下的包。</para>
        /// </param>
        /// <param name="orderBasis">这次按什么排的（进日志的那一句话；不传 = 默认口径）。</param>
        public static ExtractionSchedulePlan Build(
            IEnumerable<ArchiveTask>? tasks,
            Func<ArchiveTask, TaskSpaceEstimate> estimate,
            long? availableBytes,
            long? reserveBytes = null,
            int requestedParallelCount = 1,
            Func<ScheduledExtractionItem, long>? sortKey = null,
            string? orderBasis = null)
        {
            var ordered = new List<ScheduledExtractionItem>();
            int index = 0;

            foreach (ArchiveTask task in tasks ?? Enumerable.Empty<ArchiveTask>())
            {
                if (task == null)
                {
                    index++;
                    continue;
                }

                TaskSpaceEstimate item = estimate(task) ?? new TaskSpaceEstimate();

                ordered.Add(new ScheduledExtractionItem(task, item, index));
                index++;
            }

            long reserve = reserveBytes ?? SpaceGate.DefaultReserveBytes;

            /*
             * 排序键有两级：先按排序键（升序；默认 = 峰值需求），再按原始序号（升序）。
             * 第二级是**稳定性的保证**：需求相同的包不会因为排序而互相超越，
             * 用户对照日志与列表时不会看到"顺序莫名其妙变了"。
             */
            List<ScheduledExtractionItem> sorted = ordered
                .OrderBy(item => sortKey?.Invoke(item) ?? item.RequiredBytes)
                .ThenBy(item => item.OriginalIndex)
                .ToList();

            long available = availableBytes ?? SpaceReservationLedger.UnknownAvailable;
            var blocked = new List<ScheduledExtractionItem>();

            if (available >= 0)
            {
                long budget = available > reserve ? available - reserve : 0L;
                long accumulated = 0;

                for (int i = 0; i < sorted.Count; i++)
                {
                    long required = sorted[i].RequiredBytes;
                    long withThis = TaskSpaceEstimate.SaturatingSum(accumulated, required);

                    bool fitsAlone = required <= budget;
                    bool fitsInSet = withThis <= budget;

                    sorted[i] = new ScheduledExtractionItem(sorted[i].Task, sorted[i].Estimate, sorted[i].OriginalIndex)
                    {
                        FitsAtPlanTime = fitsInSet,
                        FitsAlone = fitsAlone,
                        ShortfallBytes = fitsAlone ? 0 : required - budget
                    };

                    if (fitsInSet)
                    {
                        accumulated = withThis;
                    }
                    else if (!fitsAlone)
                    {
                        // 单独跑也放不下 → 它就是"这一批因为空间不足被跳过"的那一个，要点名报出来。
                        blocked.Add(sorted[i]);
                    }
                }
            }
            else
            {
                // 取不到可用空间：不谎报"放得下"，也不谎报"放不下" —— 两个字段都留中性值。
                for (int i = 0; i < sorted.Count; i++)
                {
                    sorted[i] = new ScheduledExtractionItem(sorted[i].Task, sorted[i].Estimate, sorted[i].OriginalIndex)
                    {
                        FitsAtPlanTime = true,
                        FitsAlone = true,
                        ShortfallBytes = 0
                    };
                }
            }

            return new ExtractionSchedulePlan
            {
                Ordered = sorted,
                BlockedAtPlanTime = blocked,
                RecommendedParallelCount = RecommendParallelCount(sorted, available),
                RequestedParallelCount = Math.Clamp(requestedParallelCount, 1, ParallelCeiling),
                AvailableBytes = available,
                ReserveBytes = reserve,
                Basis = BuildBasis(sorted, reserve, available),
                OrderBasis = string.IsNullOrWhiteSpace(orderBasis) ? ExtractionSchedulePlan.DefaultOrderBasis : orderBasis!
            };
        }

        /// <summary>
        /// 建议并行数：从最小的开始数，**计划时放得下的有几个**就是几个；上限 8。
        ///
        /// <para>为什么用"放得下"来数而不是再累加一遍：<see cref="Build"/> 里那次累加已经
        /// 把"这些任务同时达到峰值会不会撑爆盘"算完了 —— 放得下的前 N 个正是"同时跑也不会爆"的最大 N。
        /// 数两遍等于两处实现，迟早分叉。</para>
        ///
        /// <para>取不到可用空间时返回 1（**最保守的那一档**）：不知道够不够的时候，
        /// 唯一不会把盘写满的建议就是不并行。</para>
        /// </summary>
        public static int RecommendParallelCount(IReadOnlyList<ScheduledExtractionItem> ordered, long availableBytes)
        {
            if (ordered == null || ordered.Count == 0)
            {
                return 0;
            }

            if (availableBytes < 0)
            {
                return 1;
            }

            int count = 0;

            foreach (ScheduledExtractionItem item in ordered)
            {
                /*
                 * 遇到第一个放不下的就停：后面的需求只会更大（已经按升序排过），
                 * 继续数下去只会得出一个比实际更乐观的数字。
                 */
                if (!item.FitsAtPlanTime)
                {
                    break;
                }

                count++;

                if (count >= ParallelCeiling)
                {
                    break;
                }
            }

            return count;
        }

        /// <summary>
        /// 空间不足模式的并发结论：不再按“大包 3 / 小包 5”固定档，而是按**当前空间能装下几个**
        /// 动态算；待跑任务已按需求从大到小排好。
        ///
        /// <para>算法：从队首开始，逐个累加 <see cref="ScheduledExtractionItem.RequiredBytes"/>，
        /// 只要不超过预算就继续，硬顶 <see cref="ParallelCeiling"/>（8）。因此大包自然只开 1–3 个，
        /// 小包会一直加到 8 个；不在算法里再写死“大 / 小”档。</para>
        ///
        /// <para>取不到可用空间时仍按 1 个（不知道够不够时不并行）；0 会死等，所以最低取 1，
        /// 真的一个都放不下时由空间门逐个拦下并点名报告。</para>
        /// </summary>
        public static int ResolveSpaceTightParallelCount(
            IReadOnlyList<ScheduledExtractionItem>? ordered,
            long budgetBytes,
            int recommendedBySpace,
            out string basis)
        {
            IReadOnlyList<ScheduledExtractionItem> items = ordered ?? Array.Empty<ScheduledExtractionItem>();

            if (budgetBytes < 0)
            {
                basis = "没能取到目标盘可用空间：按 1 个跑";
                return 1;
            }

            if (items.Count == 0)
            {
                basis = "没有待跑任务：按 1 个跑";
                return 1;
            }

            IReadOnlyList<long> demands = items.Select(item => item.RequiredBytes).ToList();
            _ = recommendedBySpace; // 物理空间已在这里重新按需求累加，不再用计划里的旧建议压数。
            return ResolveSpaceTightParallelCountForDemand(
                demands,
                budgetBytes,
                reservedBytes: 0,
                out basis);
        }

        /// <summary>
        /// 与 <see cref="ResolveSpaceTightParallelCount"/> 同一套空间判定，入参是纯需求字节
        /// （特定解压的 unit 用源包体积作下界）。</summary>
        public static int ResolveSpaceTightParallelCountForDemand(
            IReadOnlyList<long>? pendingDemandBytes,
            long availableBytes,
            long reservedBytes,
            out string basis)
        {
            IReadOnlyList<long> demands = pendingDemandBytes ?? Array.Empty<long>();

            if (availableBytes < 0)
            {
                basis = "没能取到目标盘可用空间：按 1 个跑";
                return 1;
            }

            if (demands.Count == 0)
            {
                basis = "没有待跑任务：按 1 个跑";
                return 1;
            }

            int launchable = SpaceConcurrencyPlanner.CountLaunchable(
                availableBytes,
                reservedBytes,
                demands,
                ParallelCeiling);

            /*
             * 大任务保护：单个任务就吃掉 1/8 以上预算时，最多并发 3 个，避免几个大包同时冲峰值；
             * 全是几百 MB 的小包时不再限 3，能装下多少就开多少（硬顶仍是 8）。
             */
            long largestDemand = demands.Max();
            int sizeCap = largestDemand >= availableBytes / SpaceTightLargeTaskBudgetDivisor
                ? SpaceTightLargeTaskParallelCeiling
                : ParallelCeiling;

            int count = Math.Max(1, Math.Min(sizeCap, launchable));

            basis = launchable <= 0
                ? "空间连最小的任务都放不下：先按 1 个交给空间门逐个拦"
                : sizeCap < ParallelCeiling
                    ? $"按可用空间能放 {launchable} 个；最大任务占用超过预算的 1/4，按大任务保护上限 {sizeCap} 个跑"
                    : $"按当前可用空间能同时放下 {count} 个（按队列需求累加，硬顶 {ParallelCeiling} 个）";

            return count;
        }

        private static string BuildBasis(
            IReadOnlyList<ScheduledExtractionItem> sorted,
            long reserve,
            long available)
        {
            if (available < 0)
            {
                return "没能取到目标盘可用空间，本次不做空间门判断（顺序仍按需求从小到大，便于人工核对）";
            }

            long smallest = sorted.Count > 0 ? sorted[0].RequiredBytes : 0L;
            long largest = sorted.Count > 0 ? sorted[sorted.Count - 1].RequiredBytes : 0L;

            return $"可用 {TaskSpaceEstimate.FormatSize(available)} − 保留 {TaskSpaceEstimate.FormatSize(reserve)}"
                   + $" = 可用于并行的预算；需求最小 {TaskSpaceEstimate.FormatSize(smallest)}、"
                   + $"最大 {TaskSpaceEstimate.FormatSize(largest)}，按从小到大累加得出建议并行数";
        }
    }
}

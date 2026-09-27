using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>排计划那一刻它需要的字节数（= 峰值需求）。</summary>
        public long RequiredBytes => Estimate.PeakBytes;

        /// <summary>
        /// 排计划那一刻**在并发集合里**是否轮得到它（它前面那些放得下的任务 + 它自己不超预算）。
        ///
        /// <para><b>它不是放行凭据</b> —— 真正的门在启动那一刻（<see cref="SpaceReservationLedger"/>）。
        /// 这个字段只用来算"建议并行几个"。</para>
        /// </summary>
        public bool FitsAtPlanTime { get; init; }

        /// <summary>
        /// **单独跑也放不下**（峰值就超过了整个预算）。
        ///
        /// <para>它才是"因为空间不足被跳过"的判据：<see cref="FitsAtPlanTime"/> 为 false 只说明
        /// "现在这一批并行的位置里挤不下它"，等前面的跑完它照样能上 —— 那不是跳过，是排队。</para>
        /// </summary>
        public bool FitsAlone { get; init; }

        /// <summary>差多少字节（能排上时为 0）。单独跑也放不下时 = 峰值 − 预算。</summary>
        public long ShortfallBytes { get; init; }
    }

    /// <summary>
    /// 一次「一键处理 / 只解压」的**执行计划**：按空间需求排好的顺序 + 当前可用空间下建议的并行档
    /// （用户 2026-09-22 需求第 2 条）。
    /// </summary>
    public sealed class ExtractionSchedulePlan
    {
        /// <summary>执行顺序（按峰值需求**从小到大**）。</summary>
        public IReadOnlyList<ScheduledExtractionItem> Ordered { get; init; } = Array.Empty<ScheduledExtractionItem>();

        /// <summary>
        /// **单独跑也放不下**的任务（峰值超过整个预算）—— "因为空间不足被跳过"的就是它们。
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
        /// <para>它必须跟着真实的排序键走：普通档按「峰值需求」（源包 + 内容物 + 过程物），
        /// 空间不足模式按「净占用」（解完之后真正留在盘上的字节数）—— 两句话要是说反了，
        /// 用户按日志核对顺序时就会得出"程序排序排错了"的结论。</para>
        /// </summary>
        public string OrderBasis { get; init; } = DefaultOrderBasis;

        internal const string DefaultOrderBasis = "按空间需求从小到大";

        /// <summary>空间不足模式的排序口径（唯一措辞来源，日志与界面都引它）。</summary>
        internal const string NetOccupancyOrderBasis = "按净占用从小到大（空间不足模式：先解「解完占地最少」的包）";

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
                $"空间调度：{OrderBasis}排了 {Ordered.Count} 个任务；{ParallelAdviceText()}（当前档：{RequestedParallelCount}）",
                "空间调度依据：" + Basis
            };

            foreach (ScheduledExtractionItem item in Ordered.Take(SpacePlanDetailLimit))
            {
                lines.Add($"  顺序 {item.OriginalIndex + 1}：{item.Estimate.Describe()}");
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
    /// <item><description><b>先排序</b>：按峰值需求**从小到大**排。用户点名的反例是
    /// "一开始就去解 5G+6G 的文件，这样并行两个都弄不了" —— 从小到大排之后，
    /// 排在最前面的一定是能塞进去的那些小包。</description></item>
    /// <item><description><b>只有装得下才启动</b>：本类给出的 <see cref="ScheduledExtractionItem.FitsAtPlanTime"/>
    /// 只是**计划时**的快照，真正的放行判断在执行循环里每个任务启动前做
    /// （<see cref="SpaceReservationLedger.TryReserve"/>）—— 空间是随跑随变的，
    /// 拿计划时的数字当放行凭据，等于把"当时够"当成"一直够"。</description></item>
    /// </list>
    ///
    /// <para><b>建议并行数怎么算</b>：把排好序的需求从最小的开始累加，累加和（含余量）
    /// 不超过可用空间的最多几个 —— 这就是"这些任务同时达到峰值"也不会撑爆盘的最大并行数。
    /// 它是**建议**：用户在界面上选的档位不会因此被静默改掉，但每个任务启动前都会真的判一次空间。</para>
    /// </summary>
    public static class ExtractionScheduler
    {
        /// <summary>并发档的硬顶（与设置项 <c>MaxParallelExtractCount</c> 的 1–8 一致）。</summary>
        public const int ParallelCeiling = 8;

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
        /// **排序键**（不传 = 按 <see cref="ScheduledExtractionItem.RequiredBytes"/>，也就是峰值需求）。
        ///
        /// <para>空间不足模式传「净占用」（<see cref="TaskSpaceEstimate.ContentBytes"/>）：
        /// 那个模式下每个包一校验通过就把它自己的源包删掉，所以"解完真正留在盘上"的字节数
        /// 才是决定后面还能解几个的量 —— 先解净占用小的，盘上越跑越宽。
        /// ⛔ 它**只改顺序**：放行判断（<see cref="ScheduledExtractionItem.FitsAlone"/> 与真正的空间门）
        /// 仍然按峰值需求算，绝不允许因为"净占用小"就放行一个峰值装不下的包。</para>
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
        /// 「空间不足」模式的并发档上限（用户 2026-09-27 拍板：**多个体积不同 / 偏大的包最多 3 个，
        /// 很多个体积相近的小包最多 5 个**）。
        ///
        /// <para>为什么空间紧张时反而要按"体积是不是相近"分两档：空间不足时最怕的是
        /// "几个大包同时冲到峰值"（盘立刻满，全批一起失败）；而一堆体积相近的小包
        /// 峰值加起来也远小于预算，多开两个只是把整批时间摊短，风险不变。
        /// 这个判据只影响**并发数**，不影响放行 —— 真正的门仍然是每个任务启动前的空间门。</para>
        /// </summary>
        public const int SpaceTightParallelForMixedOrLarge = 3;

        /// <summary>「很多个体积相近的小包」那一档（见 <see cref="SpaceTightParallelForMixedOrLarge"/>）。</summary>
        public const int SpaceTightParallelForManySmall = 5;

        /// <summary>算"体积相近的小包"时，"小"的判据：峰值 ≤ 预算 ÷ 这个数。</summary>
        private const int SpaceTightSmallTaskBudgetDivisor = 4;

        /// <summary>"体积相近"的判据：最大峰值 ÷ 最小峰值 ≤ 它。</summary>
        private const double SpaceTightSimilarSizeRatio = 1.5d;

        /// <summary>至少这么多个"相近的小包"才算"很多个小包"。</summary>
        private const int SpaceTightManySmallTaskCount = 5;

        /// <summary>
        /// **空间不足模式的并发档**（用户设置里的并发档与「全速」在这一档下都不生效，见调用方）。
        ///
        /// <para>规则（两条，判据全在这一个方法里，⛔ 不许在别处再写一套）：</para>
        /// <list type="number">
        /// <item><description><b>很多个体积相近的小包</b>（≥ 5 个，且每个峰值 ≤ 预算 ÷ 4，
        /// 且最大 / 最小峰值 ≤ 1.5 倍）→ <see cref="SpaceTightParallelForManySmall"/>（5）；</description></item>
        /// <item><description>其余（多个偏大 / 体积差得远的包）→ <see cref="SpaceTightParallelForMixedOrLarge"/>（3）。</description></item>
        /// </list>
        ///
        /// <para>出来的数还要与"计划时按空间算得出的建议并行数"取小 —— 空间不足模式下
        /// 唯一不能放宽的就是空间。取不到可用空间时用最保守的 3（不猜"够"）。</para>
        /// </summary>
        /// <param name="ordered">已排好序的计划（按净占用或峰值，两者都可）。</param>
        /// <param name="budgetBytes">可用于并行的预算（可用 − 余量；&lt; 0 = 取不到）。</param>
        /// <param name="recommendedBySpace">计划给出的建议并行数（0 = 连最小的都放不下）。</param>
        /// <param name="basis">这一档是怎么来的（进日志，用户要能复核）。</param>
        public static int ResolveSpaceTightParallelCount(
            IReadOnlyList<ScheduledExtractionItem>? ordered,
            long budgetBytes,
            int recommendedBySpace,
            out string basis)
        {
            int cap;
            string reason;

            IReadOnlyList<ScheduledExtractionItem> items = ordered ?? Array.Empty<ScheduledExtractionItem>();

            if (budgetBytes < 0)
            {
                cap = SpaceTightParallelForMixedOrLarge;
                reason = "没能取到目标盘可用空间";
            }
            else if (items.Count >= SpaceTightManySmallTaskCount
                     && CountSimilarSmallTasks(items, budgetBytes) >= SpaceTightManySmallTaskCount)
            {
                cap = SpaceTightParallelForManySmall;
                reason = $"本批有 {items.Count} 个体积相近的小包（每个峰值不超过预算的 1/{SpaceTightSmallTaskBudgetDivisor}）";
            }
            else
            {
                cap = SpaceTightParallelForMixedOrLarge;
                reason = "本批是多个偏大 / 体积差得远的包";
            }

            /*
             * 与空间建议取小 —— 但**绝不取 0**：0 会让"等并发位"那个循环永远等不到空位
             * （启动条件写成 `runningTasks.Count >= maxParallel`，0 时恒为真）。
             * 真的一个都放不下时由空间门逐个拦下并点名报告，而不是在这里变成死等。
             *
             * ⚠ 取不到可用空间时空间建议就是 1（"不知道够不够的时候，唯一不会把盘写满的建议就是不并行"，
             * 见 RecommendParallelCount）—— 空间不足模式照它办，绝不因为"模式开了"就放开并行。
             */
            int spaceLimit = recommendedBySpace > 0 ? recommendedBySpace : 1;
            int count = Math.Max(1, Math.Min(cap, spaceLimit));

            /*
             * 说法必须与**最终那个数**一致（§9.5 值 + 通知的老坑）：上限是 5、实际跑 2 的时候说
             * "按 5 个跑"比不说更糟 —— 用户会拿日志去对并发数，然后得出"日志是假的"。
             */
            basis = count >= cap
                ? reason + $"：按上限 {cap} 个跑"
                : reason + $"：上限 {cap} 个，但按空间建议压到 {count} 个";

            return count;
        }

        /// <summary>数一遍"体积相近的小包"有几个（只数，不改顺序）。</summary>
        private static int CountSimilarSmallTasks(IReadOnlyList<ScheduledExtractionItem> items, long budgetBytes)
        {
            long smallLimit = budgetBytes / SpaceTightSmallTaskBudgetDivisor;

            long smallest = long.MaxValue;
            long largest = 0;

            foreach (ScheduledExtractionItem item in items)
            {
                long required = item.RequiredBytes;

                if (required > smallLimit)
                {
                    continue;
                }

                smallest = Math.Min(smallest, required);
                largest = Math.Max(largest, required);
            }

            if (smallest == long.MaxValue || smallest <= 0)
            {
                // 一个"小包"都没有（或大小量不出来）→ 不算"很多个小包"。
                return 0;
            }

            double ratio = (double)largest / smallest;

            if (ratio > SpaceTightSimilarSizeRatio)
            {
                // 体积差得远（不是"很多个一样的包"）→ 不放开到 5。
                return 0;
            }

            int count = 0;

            foreach (ScheduledExtractionItem item in items)
            {
                if (item.RequiredBytes <= smallLimit)
                {
                    count++;
                }
            }

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

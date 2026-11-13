using System;
using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;

namespace ArchiveFixer.Storage
{
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
        /// <summary>内部硬顶（空间不足模式仍可按空间用到 8；普通手动档只开放 1–4）。</summary>
        public const int ParallelCeiling = 8;

        /// <summary>大任务（单个需求 ≥ 预算 1/4）的并发保护上限；小任务不受这个 3 的限制。</summary>
        public const int SpaceTightLargeTaskParallelCeiling = 3;

        private const int SpaceTightLargeTaskBudgetDivisor = 4;

        /// <summary>
        /// 界面上允许用户选的并发档（用户 2026-09-22 原话："可以让用户选择解压时并行解压的文件数量"）。
        /// 1–4：单盘 HDD/移动盘超过 1 通常更慢，4 已覆盖 SSD 的常见收益区间。
        /// </summary>
        public static IReadOnlyList<int> AllowedParallelCounts { get; } = new[] { 1, 2, 3, 4 };

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

            return AllowedParallelCounts[AllowedParallelCounts.Count - 1];
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
                Limit = ConcurrencyLimit.Create(
                    requested: Math.Clamp(requestedParallelCount, 1, ParallelCeiling),
                    recommended: RecommendParallelCount(sorted, available)),
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

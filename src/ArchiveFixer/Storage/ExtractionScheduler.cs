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

        /// <summary>排序与建议并行数的依据（进日志，用户要能复核）。</summary>
        public string Basis { get; init; } = string.Empty;

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
                $"空间调度：按空间需求从小到大排了 {Ordered.Count} 个任务；{ParallelAdviceText()}（当前档：{RequestedParallelCount}）",
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
        public static ExtractionSchedulePlan Build(
            IEnumerable<ArchiveTask>? tasks,
            Func<ArchiveTask, TaskSpaceEstimate> estimate,
            long? availableBytes,
            long? reserveBytes = null,
            int requestedParallelCount = 1)
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
             * 排序键有两级：先按峰值需求（升序），再按原始序号（升序）。
             * 第二级是**稳定性的保证**：需求相同的包不会因为排序而互相超越，
             * 用户对照日志与列表时不会看到"顺序莫名其妙变了"。
             */
            List<ScheduledExtractionItem> sorted = ordered
                .OrderBy(item => item.RequiredBytes)
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
                Basis = BuildBasis(sorted, reserve, available)
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

        private static string BuildBasis(
            IReadOnlyList<ScheduledExtractionItem> sorted,
            long reserve,
            long available)
        {
            if (available < 0)
            {
                return "没能取到目标盘可用空间，本次不做空间门判断，也不按空间排序（顺序仍按需求从小到大，便于人工核对）";
            }

            long smallest = sorted.Count > 0 ? sorted[0].RequiredBytes : 0L;
            long largest = sorted.Count > 0 ? sorted[sorted.Count - 1].RequiredBytes : 0L;

            return $"可用 {TaskSpaceEstimate.FormatSize(available)} − 保留 {TaskSpaceEstimate.FormatSize(reserve)}"
                   + $" = 可用于并行的预算；需求最小 {TaskSpaceEstimate.FormatSize(smallest)}、"
                   + $"最大 {TaskSpaceEstimate.FormatSize(largest)}，按从小到大累加得出建议并行数";
        }
    }
}

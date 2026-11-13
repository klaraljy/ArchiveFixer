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
}

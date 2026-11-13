// STATUS: 未完成 / 暂停（2026-11-05）。自动并发实验代码，不接入产品路径。

namespace ArchiveFixer.Experimental.AutoConcurrency
{
    /// <summary>一个并发采样窗口（P0 埋点的唯一出口）。</summary>
    public readonly record struct ConcurrencyWindowSample(
        double NormalizedLatencySecondsPerGb,
        double AggregateMegabytesPerSecond,
        int ActiveTasks,
        int UiStalls,
        bool DiskSpaceLow,
        int Failures)
    {
        /// <summary>完成任务的源包字节数；0 = 旧调用方/测试，按小任务处理。</summary>
        public long TaskBytes { get; init; }

        /// <summary>系统 CPU 忙的百分比（0–100；&lt;=0 表示没有采到，按有余量处理）。</summary>
        public double SystemCpuBusyPercent { get; init; }

        /// <summary>可用物理内存字节；0 = 没有采到，按未知处理（不据此降档）。</summary>
        public long AvailablePhysicalBytes { get; init; }
    }
}

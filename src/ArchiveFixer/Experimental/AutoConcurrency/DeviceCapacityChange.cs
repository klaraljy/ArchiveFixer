// STATUS: 未完成 / 暂停（2026-11-05）。自动并发实验代码，不接入产品路径。

namespace ArchiveFixer.Experimental.AutoConcurrency;

public readonly record struct DeviceCapacityChange(
    string Root,
    int Previous,
    int Current,
    string Reason,
    double AggregateMegabytesPerSecond,
    double AverageLatencySecondsPerGb);

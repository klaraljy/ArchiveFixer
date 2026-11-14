// STATUS: 未完成 / 暂停（2026-11-05）。自动并发实验代码，不接入产品路径。
using System;

namespace ArchiveFixer.Experimental.AutoConcurrency;

/// <summary>本机“上次验证过好的并发/基线”，按卷+设备类别存储；不记录文件名。</summary>
public sealed class ConcurrencyProfile
{
    public int LastGoodLimit { get; set; } = 1;

    public double MinLatencySecondsPerGb { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

namespace ArchiveFixer.Extraction;

/// <summary>其余物归置的规划结果：移动清单 + 集中目录 + 总字节数（失败时只带原因）。</summary>
internal sealed class FinalizeArtifactMovePlan
{
    /// <summary>其余物的移动（都归到 <see cref="Directory"/> 下）。</summary>
    public List<PlannedMove> Moves { get; } = new();

    /// <summary>其余物集中目录（<c>destDir\其余物</c>）；失败时为空。</summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>其余物总字节数。</summary>
    public long TotalSize { get; set; }

    /// <summary>规划失败的原因（空 = 没失败）。整份计划作废，不搬、不删。</summary>
    public string FailureReason { get; set; } = string.Empty;

    public bool Failed => FailureReason.Length > 0;
}

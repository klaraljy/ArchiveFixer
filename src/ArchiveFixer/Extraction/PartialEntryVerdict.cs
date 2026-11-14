namespace ArchiveFixer.Extraction;

/// <summary>一条条目的比对结果。</summary>
public sealed class PartialEntryVerdict
{
    public string Path { get; init; } = string.Empty;

    public long ExpectedSize { get; init; }

    /// <summary>盘上实际大小；<c>-1</c> = 不在盘上。</summary>
    public long ActualSize { get; init; } = -1;

    public PartialEntryState State { get; init; } = PartialEntryState.Publishable;
}

namespace ArchiveFixer.Security;

/// <summary>一条不安全条目。<see cref="Reason"/> 是中文说明，可以直接展示给用户。</summary>
public sealed class UnsafeArchiveEntry
{
    /// <summary>归档内的原始条目路径（未做任何改写，方便用户回原始包里去核对）。</summary>
    public string EntryPath { get; init; } = string.Empty;

    public PathRiskKind Kind { get; init; }

    /// <summary>为什么判成危险。中文，直接给用户看。</summary>
    public string Reason { get; init; } = string.Empty;
}

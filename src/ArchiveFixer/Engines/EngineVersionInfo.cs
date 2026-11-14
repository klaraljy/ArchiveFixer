using System.Collections.Generic;

namespace ArchiveFixer.Engines;

/// <summary>引擎的版本记录：能力是"哪个版本上验过的"，出问题时能对上号。</summary>
public sealed class EngineVersionInfo
{
    public string EngineId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Version { get; init; } = "unknown";

    /// <summary>这套能力是在哪一天、用什么样本验过的（人写的备注，不是自动推断）。</summary>
    public string VerifiedOn { get; init; } = string.Empty;

    public IReadOnlyList<string> KnownIssues { get; init; } = new List<string>();
}

using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction;
/// <summary>源包搬运计划。<b>只是计划</b>。</summary>
public sealed class SourcePackageMovePlan
{
    /// <summary>其余物目录（源包搬进去的目标根）；算不出来时为空。</summary>
    public string ArtifactDirectory { get; init; } = string.Empty;

    public IReadOnlyList<SourcePackageMove> Moves { get; init; } = Array.Empty<SourcePackageMove>();

    public IReadOnlyList<ArtifactSkip> Skipped { get; init; } = Array.Empty<ArtifactSkip>();

    /// <summary>整组源包的总字节数。</summary>
    public long TotalSize { get; init; }

    public string Message { get; init; } = string.Empty;
}

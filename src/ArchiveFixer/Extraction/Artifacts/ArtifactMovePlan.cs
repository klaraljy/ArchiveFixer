using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction
{    /// <summary>归置计划。<b>只是计划</b>：本类型不代表磁盘上已经发生了任何移动。</summary>
    public sealed class ArtifactMovePlan
    {
        /// <summary>其余物目录的完整路径（<c>parent(D)\其余物</c>）；目标目录不合法时为空。</summary>
        public string ArtifactDirectory { get; init; } = string.Empty;

        public IReadOnlyList<ArtifactMove> Moves { get; init; } = Array.Empty<ArtifactMove>();

        public IReadOnlyList<ArtifactSkip> Skipped { get; init; } = Array.Empty<ArtifactSkip>();

        public string Message { get; init; } = string.Empty;
    }

}

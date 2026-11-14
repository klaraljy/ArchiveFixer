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
{    /// <summary>一条被跳过的待归置项。</summary>
    public sealed class ArtifactSkip
    {
        public string SourcePath { get; init; } = string.Empty;

        public ArtifactSkipReason Reason { get; init; }

        /// <summary>中文说明，可直接给用户看。</summary>
        public string Message { get; init; } = string.Empty;
    }

}

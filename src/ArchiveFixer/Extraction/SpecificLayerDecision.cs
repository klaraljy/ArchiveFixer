using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction
{
    public sealed class SpecificLayerDecision
    {
        public SpecificLayerKind Kind { get; init; }

        public string ArchivePath { get; init; } = string.Empty;

        public IReadOnlyList<string> VolumePaths { get; init; } = Array.Empty<string>();
    }
}

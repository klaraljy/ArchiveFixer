using ArchiveFixer.Models;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>一层开始前已经确定的事实：实际归档、选项、密码候选、日志标签。</summary>
internal sealed class LayerPlan
{
    internal LayerPlan(
        string archivePath,
        ExtractOptions options,
        IReadOnlyList<string> candidates,
        string layerLabel)
    {
        ArchivePath = archivePath;
        Options = options;
        Candidates = candidates;
        LayerLabel = layerLabel;
    }

    internal string ArchivePath { get; }

    internal ExtractOptions Options { get; }

    internal IReadOnlyList<string> Candidates { get; }

    internal string LayerLabel { get; }
}

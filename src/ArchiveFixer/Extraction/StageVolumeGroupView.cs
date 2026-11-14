using ArchiveFixer.Detection;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>一个暂存目录里的分卷组 + 它的判定结论（定稿闸门按文件查它）。</summary>
internal sealed class StageVolumeGroupView
{
    /// <summary>这一组的目录（绝对路径）。</summary>
    public string DirectoryPath { get; init; } = string.Empty;

    /// <summary>判定器给出的唯一结论。</summary>
    public VolumeGroupResolution Resolution { get; init; } = new();

    /// <summary>组的全部文件（绝对路径，含推定进来的候选）。</summary>
    public IReadOnlyList<string> FilePaths { get; init; } = Array.Empty<string>();
}

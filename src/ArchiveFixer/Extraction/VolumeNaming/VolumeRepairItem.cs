using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction;

/// <summary>一组里的一卷：现在叫什么、该叫什么。</summary>
public sealed class VolumeRepairItem
{
    public string CurrentPath { get; init; } = string.Empty;

    public string CurrentFileName { get; init; } = string.Empty;

    public string SuggestedFileName { get; init; } = string.Empty;

    public string TargetPath { get; init; } = string.Empty;
}

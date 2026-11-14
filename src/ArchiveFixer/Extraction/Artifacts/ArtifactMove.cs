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
/// <summary>一条移动计划：从哪搬到哪。</summary>
public sealed class ArtifactMove
{
    public string SourcePath { get; init; } = string.Empty;

    public string TargetPath { get; init; } = string.Empty;

    /// <summary>落点相对其余物目录的相对路径。</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>为什么算其余物。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>是否因为重名被加了序号（加过序号的要写进报告，让用户知道名字变了）。</summary>
    public bool Renamed { get; init; }
}

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
/// <summary>一条待归置的路径。</summary>
public sealed class ArtifactPlacementItem
{
    /// <summary>待归置的路径（文件或目录）。</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>
    /// 相对某个基准的相对路径（例：<c>inner\pack.7z.001</c>）。
    /// 留空时按 <see cref="ArtifactPlacementRequest.BaseDirectory"/> 推算，再不行用文件名。
    /// </summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>为什么算其余物（写报告 / 日志用，例："内层归档"）。</summary>
    public string Reason { get; init; } = string.Empty;

    public ArtifactPlacementItem()
    {
    }

    public ArtifactPlacementItem(string sourcePath, string relativePath = "", string reason = "")
    {
        SourcePath = sourcePath ?? string.Empty;
        RelativePath = relativePath ?? string.Empty;
        Reason = reason ?? string.Empty;
    }
}

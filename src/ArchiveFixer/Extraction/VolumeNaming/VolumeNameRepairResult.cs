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

/// <summary>改名结果。</summary>
public sealed class VolumeNameRepairResult
{
    /// <summary>是否真的改成功了。</summary>
    public bool Success { get; init; }

    /// <summary>改完之后的完整路径（失败时为空）。</summary>
    public string NewPath { get; init; } = string.Empty;

    /// <summary>给人看的一句话（成功 / 失败都说清）。</summary>
    public string Message { get; init; } = string.Empty;
}

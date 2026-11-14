using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 结果归集的结果。
/// </summary>
public sealed class CollectResult
{
    public bool Success { get; init; }

    public string DestinationPath { get; init; } = string.Empty;

    public int MovedFileCount { get; init; }

    /// <summary>因重名被自动改名 / 未覆盖的文件（源侧文件名）。</summary>
    public IReadOnlyList<string> Conflicts { get; init; } = Array.Empty<string>();

    public string Message { get; init; } = string.Empty;
}

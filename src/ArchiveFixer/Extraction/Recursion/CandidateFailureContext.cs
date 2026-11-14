using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 一个候选**解压失败**之后，判定"这一层该停下还是接着试下一个"需要的本层事实。
///
/// <para>只读快照 + 两个待回写的账（<see cref="InformativeFailure"/> / <see cref="AmbiguousPasswordFailure"/>）：
/// 其余各档只出结论，不改循环状态。</para>
/// </summary>
internal sealed class CandidateFailureContext
{
    internal WorkItem Item { get; init; } = null!;

    internal string ArchivePath { get; init; } = string.Empty;

    internal string LayerLabel { get; init; } = string.Empty;

    /// <summary>非 null = 这一层已经在解抠出来的副本（⛔ 不许再抠一次 / 再判双面文件）。</summary>
    internal string? CarvedSource { get; init; }

    internal ArchiveOperationResult Result { get; init; } = null!;

    internal IReadOnlyList<(string Path, long Size)> LastListedEntries { get; init; } =
        Array.Empty<(string Path, long Size)>();

    /// <summary>"走到最远的那一次失败"（第一次真在产物目录里留下字节的那一次）。</summary>
    internal ArchiveOperationResult? InformativeFailure { get; init; }

    internal ArchiveOperationResult? AmbiguousPasswordFailure { get; init; }

    internal IProgress<ArchiveProgress>? Progress { get; init; }

    internal Action<ArchiveStallNotice>? Stalled { get; init; }
}

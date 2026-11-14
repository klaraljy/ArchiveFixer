using ArchiveFixer.Engines;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 候选循环跑完（一个候选都没成功）之后，失败收尾需要的那一份本层事实。
///
/// <para>只读快照：收尾段（<see cref="RecursiveResultBuilder"/>）不写回任何一项，
/// 免得把"循环里的事实"和"收尾改过的结论"混成一份说不清的状态。</para>
/// </summary>
internal sealed class LayerFailureContext
{
    internal WorkItem Item { get; init; } = null!;

    internal string ArchivePath { get; init; } = string.Empty;

    internal string LayerLabel { get; init; } = string.Empty;

    internal IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();

    /// <summary>这一层按规矩**跳过**了几个候选（空密码两档）；结论判"是否被上限截断"要减掉它们。</summary>
    internal int SkippedCandidates { get; init; }

    /// <summary>这一层**真正试过**几个候选（= <c>PasswordAttemptLedger.Tried</c>）。</summary>
    internal int TriedCandidateCount { get; init; }

    internal bool TriedAny { get; init; }

    /// <summary>候选循环走到过的最后一个候选下标（-1 = 一个都还没碰）。</summary>
    internal int LastConsumedCandidateIndex { get; init; } = -1;

    internal IReadOnlyList<string> TriedCandidates { get; init; } = Array.Empty<string>();

    internal bool SawEncryptedArchive { get; init; }

    internal ArchiveOperationResult? AmbiguousPasswordFailure { get; init; }

    internal ArchiveOperationResult? InformativeFailure { get; init; }

    internal ArchiveOperationResult? LastFailure { get; init; }

    internal LayerManifest CandidateManifest { get; init; } = LayerManifest.Unavailable(string.Empty);

    internal bool ListedAnyCandidate { get; init; }

    internal bool SawEncryptedHeadersListFailure { get; init; }

    internal bool SawOtherListFailure { get; init; }

    internal string EncryptedHeadersListMessage { get; init; } = string.Empty;

    internal IReadOnlyList<(string Path, long Size)> LastListedEntries { get; init; } =
        Array.Empty<(string Path, long Size)>();
}

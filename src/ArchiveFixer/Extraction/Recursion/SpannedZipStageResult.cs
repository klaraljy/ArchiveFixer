using System.Collections.Generic;

namespace ArchiveFixer.Extraction
{
    /// <summary>跨盘 ZIP 收卷预检的结论；不直接写工作区账本，由调用方决定记账。</summary>
    internal sealed class SpannedZipStageResult
    {
        internal bool Blocked { get; init; }

        internal string BlockedMessage { get; init; } = string.Empty;

        internal string LinkedMessage { get; init; } = string.Empty;

        internal IReadOnlyList<string> LinkedPaths { get; init; } = System.Array.Empty<string>();

        internal IReadOnlyList<string> LinkedSources { get; init; } = System.Array.Empty<string>();

        internal int LinkedCount => LinkedPaths.Count;
    }
}

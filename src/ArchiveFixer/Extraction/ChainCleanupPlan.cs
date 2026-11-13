using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction
{
    /// <summary>一条链的清理计划。AllowedRoot 由计划给出，执行器不再自己解析。</summary>
    public sealed class ChainCleanupPlan
    {
        public IReadOnlyList<ChainCleanupStep> Steps { get; init; } = Array.Empty<ChainCleanupStep>();
        public string AllowedRoot { get; init; } = string.Empty;
    }
}


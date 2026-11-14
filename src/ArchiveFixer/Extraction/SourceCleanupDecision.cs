using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 一次删源裁决的完整输入。调用方只填事实，是否可删由 <see cref="SourceCleanupService"/> 判。
/// </summary>
public sealed class SourceCleanupDecision
{
    public bool Enabled { get; init; } = true;

    public SourceCleanupEvidence Evidence { get; init; }

    public string Note { get; init; } = string.Empty;

    public bool IncludeRestAliases { get; init; }

    /// <summary>目标清单的唯一来源；给了它就不再由服务自己 BuildTargetList。</summary>
    public DeletionPlan? Plan { get; init; }
}


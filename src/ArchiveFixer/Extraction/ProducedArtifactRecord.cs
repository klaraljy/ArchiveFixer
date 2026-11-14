using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 第一大步/递归层产出的一个文件。候选分卷片在这里持有租约，交界前不得回收。
/// </summary>
public sealed class ProducedArtifactRecord
{
    public string Path { get; init; } = string.Empty;

    public string OriginRootId { get; init; } = string.Empty;

    public string ProducedByTaskId { get; init; } = string.Empty;

    public ArtifactRole Role { get; init; } = ArtifactRole.Unknown;

    public string CandidateLeaseId { get; init; } = string.Empty;

    public string PlacementKey { get; init; } = string.Empty;

    public bool HasCandidateLease => !string.IsNullOrWhiteSpace(CandidateLeaseId);
}

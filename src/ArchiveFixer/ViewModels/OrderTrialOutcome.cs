using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using System.Collections.Generic;

namespace ArchiveFixer.ViewModels;

/// <summary>试开那一段的结论（把 9 个被改写的局部量一次交回调用方）。</summary>
internal sealed record OrderTrialOutcome(
    VolumeGatherPlan GatherPlan,
    CardVolumeGroupReading CardReading,
    string GroupResolutionLayer,
    ArchiveFixer.Detection.CardVolumeGroupReading? GatherCardEvidence,
    bool ContentOrderUndetermined,
    bool StagedOrderGroup,
    IReadOnlyList<string>? ConfirmedOrderForGroup,
    VolumeNamingFamily? ConfirmedFamilyForGroup,
    bool ConfirmedGroupForTask);


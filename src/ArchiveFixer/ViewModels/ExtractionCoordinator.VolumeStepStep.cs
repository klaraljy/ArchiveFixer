using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// **第二大步的接线口**（2026-10-24 重建 P0）：交界处只把「候选 + owner 任务 + 入口层」交给
/// <see cref="VolumeGroupService"/>，由它建一次 <see cref="VolumeGroupUnit"/>；⛔ 这里不搬片、不改名、
/// 不判完整性——行为与接线前保持一致，方便先验“Unit 化不产生回归”。
///
/// <para>实现已整块搬进协作类 <see cref="VolumeStepHandler"/>
/// （<c>ViewModels/Extraction/Steps/VolumeStepHandler.cs</c>）；本格只留**原方法签名的薄转发**，
/// 外加搬出实现要用的最小 internal 访问器 —— 判据、日志顺序、删除闸门一个字不改。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    internal readonly VolumeGroupService _volumeGroupService = new();

    /// <summary>本批/本链已登记的组（只读，给日志/批末用）。</summary>
    internal IReadOnlyCollection<VolumeGroupUnit> VolumeGroupUnits => _volumeGroupService.Units;

    /// <summary>批首清账（与交接账同一处调用）。</summary>
    internal void ResetVolumeGroupService() => _volumeGroupService.Reset();

    // ===== 搬出后的入口薄转发（实现见 Extraction/Steps/VolumeStepHandler.cs）=====
    private VolumeStepHandler? _volumeStepHandler;

    private VolumeStepHandler VolumeStep => _volumeStepHandler ??= new VolumeStepHandler(this);

    /// <summary>薄转发；实现见 <see cref="VolumeStepHandler.TryCommitVolumeGroupAsync"/>。</summary>
    internal Task<VolumeGroupUnit?> TryCommitVolumeGroupAsync(
        VolumeCandidateDiscovery.VolumeCandidate? group,
        ArchiveTask? ownerTask,
        string? entryLayer,
        bool standardNameFastPath = false,
        CancellationToken cancellationToken = default,
        VolumeNamingFamily? standardFamily = null) =>
        VolumeStep.TryCommitVolumeGroupAsync(
            group,
            ownerTask,
            entryLayer,
            standardNameFastPath,
            cancellationToken,
            standardFamily);

    /// <summary>薄转发；实现见 <see cref="VolumeStepHandler.TryCommitVolumeGroup"/>。</summary>
    internal VolumeGroupUnit? TryCommitVolumeGroup(
        VolumeCandidateDiscovery.VolumeCandidate? group,
        ArchiveTask? ownerTask,
        string? entryLayer,
        IReadOnlyList<string>? confirmedOrder = null,
        bool standardNameFastPath = false,
        VolumeNamingFamily? standardFamily = null) =>
        VolumeStep.TryCommitVolumeGroup(
            group,
            ownerTask,
            entryLayer,
            confirmedOrder,
            standardNameFastPath,
            standardFamily);

    /// <summary>链接完成后回写 Unit（唯一写者：Service）。</summary>
    internal void MarkVolumeGroupLinked(VolumeGroupUnit? unit, IReadOnlyList<string>? linkedNames) =>
        _volumeGroupService.MarkLinked(unit, linkedNames);

    /// <summary>薄转发；实现见 <see cref="VolumeStepHandler.SettleExtractedVolumeGroup"/>。</summary>
    internal void SettleExtractedVolumeGroup(VolumeGroupUnit? unit) =>
        VolumeStep.SettleExtractedVolumeGroup(unit);

    /// <summary>薄转发；实现见 <see cref="VolumeStepHandler.TrySettleVolumeGroup"/>。</summary>
    internal bool TrySettleVolumeGroup(ArchiveTask? task) => VolumeStep.TrySettleVolumeGroup(task);

    /// <summary>薄转发；实现见 <see cref="VolumeStepHandler.SettleSucceededGroupSourcesAtChainEnd"/>。</summary>
    internal void SettleSucceededGroupSourcesAtChainEnd(OneClickRunOptions? runOptions) =>
        VolumeStep.SettleSucceededGroupSourcesAtChainEnd(runOptions);

    // ===== 搬出实现要用的私有成员：最小 internal 访问器（唯一实现仍在原处）=====
    internal void MarkFollowerTasksForGroup(VolumeGroupUnit unit, ArchiveTask? owner) =>
        VolumeGroupSettlement.MarkFollowerTasksForGroup(unit, owner);

    internal void MarkFollowerTasksForPreparedGroup(PreparedVolumeGroup prepared, ArchiveTask owner) =>
        VolumeGroupSettlement.MarkFollowerTasksForPreparedGroup(prepared, owner);

    internal void MarkFollowerTasksGroupFailed(VolumeGroupUnit unit, ArchiveTask owner) =>
        VolumeGroupSettlement.MarkFollowerTasksGroupFailed(unit, owner);

    internal void MarkVolumeGroupPiecesConsumed(IEnumerable<string>? piecePaths) =>
        VolumeGroupSettlement.MarkVolumeGroupPiecesConsumed(piecePaths);

    internal void ReclaimSucceededGroupSources(VolumeGroupUnit unit, ArchiveTask? ownerTask) =>
        VolumeGroupSettlement.ReclaimSucceededGroupSources(unit, ownerTask);

    internal ArchiveTask? FindOwnerTask(VolumeGroupUnit unit) =>
        VolumeGroupSettlement.FindOwnerTask(unit);

    /// <summary>访问器：给 <see cref="VolumeStepHandler"/> 数入口落点里的内容物（唯一实现 = RestHandlingStep）。</summary>
    internal int CountContentFilesForVolumeStep(string destinationDirectory) =>
        _restHandlingStep.CountContentFiles(destinationDirectory);

    private ExtractionVolumeGroupSettlement? _volumeGroupSettlement;
    private ExtractionVolumeGroupSettlement VolumeGroupSettlement =>
        _volumeGroupSettlement ??= new ExtractionVolumeGroupSettlement(this);
}

using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// **第二大步的接线口**（2026-10-24 重建 P0）：交界处只把「候选 + owner 任务 + 入口层」交给
    /// <see cref="VolumeGroupService"/>，由它建一次 <see cref="VolumeGroupUnit"/>；⛔ 这里不搬片、不改名、
    /// 不判完整性——行为与接线前保持一致，方便先验“Unit 化不产生回归”。
    /// </summary>
    internal sealed partial class ExtractionCoordinator
    {
        private readonly VolumeGroupService _volumeGroupService = new();

        /// <summary>本批/本链已登记的组（只读，给日志/批末用）。</summary>
        internal IReadOnlyCollection<VolumeGroupUnit> VolumeGroupUnits => _volumeGroupService.Units;

        /// <summary>批首清账（与交接账同一处调用）。</summary>
        internal void ResetVolumeGroupService() => _volumeGroupService.Reset();

        /// <summary>
        /// **交界处 → 第二大步的唯一入口**：把一个“可证完整”的候选提交成 Unit。
        /// 族判不出 / 源层非法 / 入口为空 ⇒ 返回 null，一个字节都不动。
        /// </summary>
        internal VolumeGroupUnit? TryCommitVolumeGroup(
            GroupAnalysis.GroupCandidate? group,
            ArchiveTask? ownerTask,
            string? entryLayer)
        {
            if (group == null
                || ownerTask == null
                || group.Verdict != GroupAnalysis.GroupAnalysisVerdict.Complete
                || string.IsNullOrWhiteSpace(entryLayer))
            {
                return null;
            }

            VolumeNamingFamily? family;

            try
            {
                family = ArchiveFixer.Detection.VolumePiecesFromCards.ResolveNamingFamily(group.Cards);
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"[第二大步·组] {group.BaseName}：定族失败（{ex.GetType().Name}）⇒ 不建组。");
                return null;
            }

            if (family == null)
            {
                AppendLog("INFO", $"[第二大步·组] {group.BaseName}：族判不出 ⇒ 不建组（判不出就不动）。");
                return null;
            }

            VolumeGroupUnit? unit = _volumeGroupService.TryCommit(
                group.BaseName,
                family,
                entryLayer,
                group.EntryPath,
                FileNameHelper.GetFileName(ownerTask.CurrentPath ?? string.Empty),
                group.PieceFacts,
                group.MissingNumbers,
                group.Evidence,
                group.DeclaredPieceCount > 0 ? group.DeclaredPieceCount : null,
                group.DeclaredTotalBytes > 0 ? group.DeclaredTotalBytes : null,
                Path.GetDirectoryName(ownerTask.CurrentPath ?? string.Empty) ?? string.Empty);

            if (unit == null)
            {
                AppendLog("WARN", $"[第二大步·组] {group.BaseName}：建组被拒（源层/入口/片清单不完整）⇒ 不动作。");
                return null;
            }

            AppendLog(
                "INFO",
                $"[第二大步·组] 建组 {unit.BaseName}｜族={unit.Family}｜入口={FileNameHelper.GetFileName(unit.EntryPath)}"
                + $"｜片={unit.Pieces.Count}｜owner={unit.OwnerTaskFileName}｜状态={unit.State}｜{unit.Verdict}");

            return unit;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        /// <summary>链接完成后回写 Unit（唯一写者：Service）。</summary>
        internal void MarkVolumeGroupLinked(VolumeGroupUnit? unit, IReadOnlyList<string>? linkedNames) =>
            _volumeGroupService.MarkLinked(unit, linkedNames);

        /// <summary>
        /// **收尾（P1）**：owner 任务跑完后按 Unit 收片/留片、回写结论。
        /// 返回 false = 这一单不是某组的 owner（交回旧收片账处理）。
        /// </summary>
        internal bool TrySettleVolumeGroup(ArchiveTask? task)
        {
            if (task == null)
            {
                return false;
            }

            string ownerName = FileNameHelper.GetFileName(task.CurrentPath ?? string.Empty);
            VolumeGroupUnit? unit = _volumeGroupService.FindByOwner(ownerName);

            /*
             * 只有**族算法接线并出了规范名计划**的组才走 Unit 收尾；
             * 其它族（计划为空）仍走旧收片账 —— 否则链接是按原文件名建的、收片却按 LinkedNames 删，
             * 两边对不上（V01–V04 回归当场变红）。
             */
            if (unit == null || unit.NamePlan == null)
            {
                return false;
            }

            bool succeeded = task.Outcome == TaskOutcome.Succeeded;

            if (!succeeded)
            {
                _volumeGroupService.MarkState(
                    unit,
                    VolumeGroupState.Failed,
                    "没成功（密码错 / 缺卷 / 失败），片全部留在入口层");
                MarkVolumeHandoffVerdict(unit.Pieces.Select(piece => piece.Path), unit.Verdict);
                AppendLog("INFO", $"[第二大步·结论] {unit.BaseName}：{unit.Verdict}");
                return true;
            }

            int deleted = VolumeGroupEntryLayer.PurgeLinkedNames(
                unit.LinkedNames,
                path => IsUserImportedSourcePath(path),
                (level, message) => AppendLog(level, $"[第二大步·收片] {message}"));

            _volumeGroupService.MarkState(
                unit,
                VolumeGroupState.Succeeded,
                $"解压成功，收片 {deleted} 片（用户原件不删）");
            MarkVolumeHandoffVerdict(unit.Pieces.Select(piece => piece.Path), unit.Verdict);
            AppendLog("INFO", $"[第二大步·结论] {unit.BaseName}：{unit.Verdict}");
            return true;
        }
    }
}

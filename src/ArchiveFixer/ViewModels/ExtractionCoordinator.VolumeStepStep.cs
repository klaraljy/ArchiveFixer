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

            /*
             * 已接线的族：**由族算法判完整性**，候选日志只是建议（交界处不再当判据）。
             * 未接线的族：仍按“候选必须 Complete”走 P0 行为，避免把未验证的组放进来。
             */
            bool familyWired = family is VolumeNamingFamily.RarPart
                or VolumeNamingFamily.ZipSpanned
                or VolumeNamingFamily.ZipNumbered
                or VolumeNamingFamily.SevenZipNumbered;

            if (!familyWired && group.Verdict != GroupAnalysis.GroupAnalysisVerdict.Complete)
            {
                return null;
            }

            string? ownerName = ownerTask == null
                ? null
                : FileNameHelper.GetFileName(ownerTask.CurrentPath ?? string.Empty);

            string outputLayer = ownerTask != null
                ? Path.GetDirectoryName(ownerTask.CurrentPath ?? string.Empty) ?? string.Empty
                : Path.GetDirectoryName(group.EntryPath) ?? string.Empty;

            VolumeGroupUnit? unit = _volumeGroupService.TryCommit(
                group.BaseName,
                family,
                entryLayer,
                group.EntryPath,
                ownerName,
                group.PieceFacts,
                group.MissingNumbers,
                group.Evidence,
                group.DeclaredPieceCount > 0 ? group.DeclaredPieceCount : null,
                group.DeclaredTotalBytes > 0 ? group.DeclaredTotalBytes : null,
                outputLayer);

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
                AppendLog(
                    "WARN",
                    $"[第二大步·收片] {unit.BaseName}：没成功 ⇒ 入口层里的片全部留着（一个字节没动）");
                AppendLog("INFO", $"[第二大步·结论] {unit.BaseName}：{unit.Verdict}");
                return true;
            }

            /*
             * 成功收片 = 本组建出来的链接名 + 本组消费掉的**链内产出片**（含规范名计划里的原产片名）。
             * ⛔ 用户原件（Source=UserImported 或 IsUserImportedSourcePath）不删；
             * 链内产出片即使还是某个任务的源（例如被闸门跳过的分卷片）也收掉 —— 组已解开，它没用了。
             */
            var consumed = new List<string>();

            consumed.AddRange(unit.Pieces
                .Where(piece => piece.Source != VolumeHandoffSource.UserImported)
                .Select(piece => piece.Path)
                .Where(path => !IsUserImportedSourcePath(path)));

            if (unit.NamePlan != null)
            {
                foreach (CanonicalNamePlanItem item in unit.NamePlan.Items)
                {
                    if (item.Conflict.Length > 0 || IsUserImportedSourcePath(item.PiecePath))
                    {
                        continue;
                    }

                    string targetPath = Path.Combine(unit.SourceLayer, item.TargetName);

                    bool linked = unit.LinkedNames.Any(name =>
                        string.Equals(
                            Path.GetFullPath(name),
                            Path.GetFullPath(targetPath),
                            StringComparison.OrdinalIgnoreCase));

                    bool sameOriginalName = string.Equals(
                        item.TargetName,
                        Path.GetFileName(item.PiecePath),
                        StringComparison.OrdinalIgnoreCase);

                    if (linked || sameOriginalName)
                    {
                        consumed.Add(item.PiecePath);
                    }
                }
            }

            /*
             * 同一份物理文件可能还有别的名字（`.中文003` 与 `.003` 是同一份）——
             * 收片必须按**文件身份**把同层里这些多余名字一起收掉，否则链接名删了、原产片名还在。
             * ⛔ 用户原件（任务源）一律不动。
             */
            var purgeSeeds = unit.LinkedNames.Concat(consumed)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string directory in purgeSeeds
                         .Select(path => Path.GetDirectoryName(path) ?? string.Empty)
                         .Where(path => path.Length > 0)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                IEnumerable<string> siblings;

                try
                {
                    siblings = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                foreach (string sibling in siblings)
                {
                    if (IsUserImportedSourcePath(sibling)
                        || purgeSeeds.Contains(sibling, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (purgeSeeds.Any(seed => FileIdentity.IsSamePhysicalFile(seed, sibling)))
                    {
                        consumed.Add(sibling);
                    }
                }
            }

            int deleted = VolumeGroupEntryLayer.PurgeLinkedNames(
                unit.LinkedNames.Concat(consumed).Distinct(StringComparer.OrdinalIgnoreCase),
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

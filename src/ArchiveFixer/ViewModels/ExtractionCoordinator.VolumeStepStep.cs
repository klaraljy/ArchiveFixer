using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        /// 第二大步建组前的**试开顺序**：只对内容能定末片的族（通用分片 / 跨盘 ZIP）跑
        /// <see cref="ProbeGatherGroupAsync"/>，赢的顺序作为 <see cref="VolumeGroupUnit.ConfirmedOrder"/>
        /// 交给族算法；试不开/不适用 ⇒ 照旧不带顺序建组，由族算法判 Unknown。
        /// </summary>
        internal async Task<VolumeGroupUnit?> TryCommitVolumeGroupAsync(
            GroupAnalysis.GroupCandidate? group,
            ArchiveTask? ownerTask,
            string? entryLayer,
            bool standardNameFastPath = false,
            CancellationToken cancellationToken = default)
        {
            if (group == null
                || string.IsNullOrWhiteSpace(entryLayer)
                || group.Pieces.Count < 2)
            {
                return TryCommitVolumeGroup(
                    group,
                    ownerTask,
                    entryLayer,
                    standardNameFastPath: standardNameFastPath);
            }

            if (standardNameFastPath)
            {
                return TryCommitVolumeGroup(
                    group,
                    ownerTask,
                    entryLayer,
                    standardNameFastPath: true);
            }

            GroupOrderTrialResult probe = await ProbeGatherGroupAsync(
                    group.Pieces,
                    group.Pieces,
                    entryLayer,
                    group.EntryPath,
                    group.BaseName,
                    allowFallback: false,
                    cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<string>? confirmedOrder = probe.Confirmed && probe.OrderedPieces.Count > 0
                ? probe.OrderedPieces
                : null;

            if (probe.Confirmed)
            {
                AppendLog(
                    "INFO",
                    $"[第二大步·组] {group.BaseName}：试开确认顺序 {confirmedOrder!.Count} 片"
                    + $"（{probe.Reason}）⇒ 交给族算法");
            }

            return TryCommitVolumeGroup(group, ownerTask, entryLayer, confirmedOrder);
        }

        /// <summary>
        /// **交界处 → 第二大步的唯一入口**：把一个“可证完整”的候选提交成 Unit。
        /// 族判不出 / 源层非法 / 入口为空 ⇒ 返回 null，一个字节都不动。
        /// </summary>
        internal VolumeGroupUnit? TryCommitVolumeGroup(
            GroupAnalysis.GroupCandidate? group,
            ArchiveTask? ownerTask,
            string? entryLayer,
            IReadOnlyList<string>? confirmedOrder = null,
            bool standardNameFastPath = false)
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
             * 已接线的族：**由族算法判完整性**；GroupAnalysis 的候选建议不参与最终结论。
             */

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
                outputLayer,
                confirmedOrder,
                standardNameFastPath);

            if (unit == null)
            {
                AppendLog("WARN", $"[第二大步·组] {group.BaseName}：建组被拒（源层/入口/片清单不完整）⇒ 不动作。");
                return null;
            }

            AppendLog(
                "INFO",
                $"[第二大步·组] 建组 {unit.BaseName}｜族={unit.Family}｜入口={FileNameHelper.GetFileName(unit.EntryPath)}"
                + $"｜片={unit.Pieces.Count}｜owner={unit.OwnerTaskFileName}｜状态={unit.State}｜{unit.Verdict}");

            if (ownerTask != null)
            {
                MarkFollowerTasksForGroup(unit, ownerTask);
            }

            return unit;
        }

        /// <summary>
        /// 入口已定 ⇒ 同组其余片的任务先落“跟班”事实。
        /// 这样它们开跑前就不会再撞“源文件已变化”，也不会各自解一遍整组。
        /// </summary>
        private void MarkFollowerTasksForGroup(VolumeGroupUnit unit, ArchiveTask owner)
        {
            string ownerName = string.IsNullOrWhiteSpace(owner.DisplayFileName)
                ? owner.FileName
                : owner.DisplayFileName;

            foreach (GroupPieceFact piece in unit.Pieces)
            {
                ArchiveTask? pieceTask = SnapshotTaskTable(Tasks).FirstOrDefault(candidate =>
                    candidate != null
                    && !ReferenceEquals(candidate, owner)
                    && !string.IsNullOrWhiteSpace(candidate.CurrentPath)
                    && (SafePathHelper.PathEquals(candidate.CurrentPath, piece.Path)
                        || FileIdentity.IsSamePhysicalFile(candidate.CurrentPath, piece.Path)));

                if (pieceTask == null)
                {
                    continue;
                }

                pieceTask.IsVolumeGroupFollower = true;
                pieceTask.SettledWithGroupUnitName = ownerName;

                if (pieceTask.Outcome == TaskOutcome.Pending)
                {
                    pieceTask.Outcome = TaskOutcome.Skipped;
                    pieceTask.EndTime = DateTime.Now;
                    pieceTask.MarkSkipped(string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.VolumeGroupFollowerSkippedFormat,
                        ownerName));
                }
            }
        }

        /// <summary>链接完成后回写 Unit（唯一写者：Service）。</summary>
        internal void MarkVolumeGroupLinked(VolumeGroupUnit? unit, IReadOnlyList<string>? linkedNames) =>
            _volumeGroupService.MarkLinked(unit, linkedNames);

        /// <summary>
        /// **收尾（P1）**：owner 任务跑完后按 Unit 收片/留片、回写结论。
        /// 返回 false = 这一单不是某组的 owner（交回旧收片账处理）。
        /// </summary>
        /// <summary>
        /// 入口任务**已经解成功过**的组：不再点名，但把它其余的片按“已被消费”收掉
        /// （⛔ 用户原件不删；真机 8888 的 `.zip.00!~2` 就属于这一档）。
        /// </summary>
        internal void SettleExtractedVolumeGroup(VolumeGroupUnit? unit)
        {
            if (unit == null)
            {
                return;
            }

            var consumed = unit.Pieces
                .Where(piece => piece.Source != VolumeHandoffSource.UserImported)
                .Select(piece => piece.Path)
                .Where(path => !IsUserImportedSourcePath(path))
                .ToList();

            int deleted = VolumeGroupEntryLayer.PurgeLinkedNames(
                unit.LinkedNames.Concat(consumed).Distinct(StringComparer.OrdinalIgnoreCase),
                path => IsUserImportedSourcePath(path),
                (level, message) => AppendLog(level, $"[第二大步·收片] {message}"));

            _volumeGroupService.MarkState(
                unit,
                VolumeGroupState.Succeeded,
                $"入口已解成功，收片 {deleted} 片（用户原件不删）");
            AppendLog("INFO", $"[第二大步·结论] {unit.BaseName}：{unit.Verdict}");

            ReclaimSucceededGroupSources(unit, FindOwnerTask(unit));
        }

        /// <summary>
        /// 组成功后按「源包操作」收本组用户源片：只在 `DeleteInPlace` 档、且组齐全、
        /// owner 内容可证完整时执行；把**组级事实**（而非单片“旁边还有一片”）喂给删除闸门。
        /// </summary>
        private void ReclaimSucceededGroupSources(VolumeGroupUnit unit, ArchiveTask? ownerTask)
        {
            if (unit == null
                || unit.State != VolumeGroupState.Succeeded
                || unit.MissingNumbers.Count > 0)
            {
                return;
            }

            SourceHandlingMode handling = RunOptions?.SourceHandling
                ?? AppSettings.ParseSourceHandling(Settings?.SourceHandling);

            if (handling != SourceHandlingMode.DeleteInPlace)
            {
                return;
            }

            ArchiveTask? owner = ownerTask ?? FindOwnerTask(unit);
            ResultCompletenessVerdict? groupVerdict = owner != null
                ? ResultCompletenessClassifier.Classify(owner)
                : null;

            if (groupVerdict == null || !groupVerdict.AllowsSourceRemoval)
            {
                return;
            }

            var logEntries = new List<(string Level, string Message)>();
            var reclaimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Reclaim(ArchiveTask? task)
            {
                if (task == null || string.IsNullOrWhiteSpace(task.CurrentPath)
                    || !reclaimed.Add(task.CurrentPath))
                {
                    return;
                }

                PurgeLayerSourcePackage(
                    task,
                    verification: null,
                    commit: null,
                    logEntries,
                    LayerPurgeTrigger.SourceHandlingDelete,
                    reclaimVerdict: groupVerdict,
                    groupComplete: true);
            }

            Reclaim(owner);

            foreach (GroupPieceFact piece in unit.Pieces)
            {
                if (piece.Source != VolumeHandoffSource.UserImported
                    && !IsUserImportedSourcePath(piece.Path))
                {
                    continue;
                }

                ArchiveTask? pieceTask = SnapshotTaskTable(Tasks).FirstOrDefault(candidate =>
                    candidate != null
                    && !string.IsNullOrWhiteSpace(candidate.CurrentPath)
                    && (SafePathHelper.PathEquals(candidate.CurrentPath, piece.Path)
                        || FileIdentity.IsSamePhysicalFile(candidate.CurrentPath, piece.Path)));

                Reclaim(pieceTask);
            }

            foreach ((string level, string message) in logEntries)
            {
                AppendLog(level, message);
            }
        }

        private ArchiveTask? FindOwnerTask(VolumeGroupUnit unit)
        {
            if (unit == null || string.IsNullOrWhiteSpace(unit.OwnerTaskFileName))
            {
                return null;
            }

            return SnapshotTaskTable(Tasks).FirstOrDefault(candidate =>
                candidate != null
                && !string.IsNullOrWhiteSpace(candidate.CurrentPath)
                && string.Equals(
                    FileNameHelper.GetFileName(candidate.CurrentPath),
                    unit.OwnerTaskFileName,
                    StringComparison.OrdinalIgnoreCase));
        }


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

            /*
             * 组成功 ⇒ 这一组每一片对应的任务按**跟班卷**收场（唯一出口
             * `SettleOneConsumedPieceTask`，与既有的“谁吐过片”那本账同一套闸门）：
             * 源片在「彻底删除」档下这才有人处理；Keep 档一个字节都不动。
             * ⛔ 不新造删除判据、⛔ 不直接 File.Delete。
             */
            var pieceSettleLogs = new List<(string Level, string Message)>();
            IReadOnlyList<ArchiveTask> taskSnapshot = SnapshotTaskTable(Tasks);

            foreach (GroupPieceFact piece in unit.Pieces)
            {
                ArchiveTask? pieceTask = taskSnapshot.FirstOrDefault(candidate =>
                    candidate != null
                    && !string.IsNullOrWhiteSpace(candidate.CurrentPath)
                    && (SafePathHelper.PathEquals(candidate.CurrentPath, piece.Path)
                        || FileIdentity.IsSamePhysicalFile(candidate.CurrentPath, piece.Path)));

                if (pieceTask == null || ReferenceEquals(pieceTask, task))
                {
                    continue;
                }

                SettleOneConsumedPieceTask(
                    pieceTask,
                    task,
                    unit.BaseName,
                    Path.GetFileName(piece.Path),
                    moveSources: true,
                    pieceSettleLogs,
                    producedPiecePath: piece.Path);
            }

            foreach ((string level, string message) in pieceSettleLogs)
            {
                AppendLog(level, message);
            }

            MarkVolumeHandoffVerdict(unit.Pieces.Select(piece => piece.Path), unit.Verdict);
            AppendLog("INFO", $"[第二大步·结论] {unit.BaseName}：{unit.Verdict}");

            ReclaimSucceededGroupSources(unit, task);
            return true;
        }
    }
}

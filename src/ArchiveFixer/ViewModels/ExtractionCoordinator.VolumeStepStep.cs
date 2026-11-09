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
            VolumeCandidateDiscovery.VolumeCandidate? group,
            ArchiveTask? ownerTask,
            string? entryLayer,
            bool standardNameFastPath = false,
            CancellationToken cancellationToken = default,
            VolumeNamingFamily? standardFamily = null)
        {
            if (group == null
                || string.IsNullOrWhiteSpace(entryLayer)
                || group.Pieces.Count < 2)
            {
                return TryCommitVolumeGroup(
                    group,
                    ownerTask,
                    entryLayer,
                    standardNameFastPath: standardNameFastPath,
                    standardFamily: standardFamily);
            }

            if (group.PreparedGroup != null || standardNameFastPath)
            {
                return TryCommitVolumeGroup(
                    group,
                    ownerTask,
                    entryLayer,
                    standardNameFastPath: standardNameFastPath,
                    standardFamily: standardFamily);
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
            VolumeCandidateDiscovery.VolumeCandidate? group,
            ArchiveTask? ownerTask,
            string? entryLayer,
            IReadOnlyList<string>? confirmedOrder = null,
            bool standardNameFastPath = false,
            VolumeNamingFamily? standardFamily = null)
        {
            if (group == null
                || string.IsNullOrWhiteSpace(entryLayer))
            {
                return null;
            }

            string? ownerName = ownerTask == null
                ? null
                : FileNameHelper.GetFileName(ownerTask.CurrentPath ?? string.Empty);

            string outputLayer = ownerTask != null
                ? Path.GetDirectoryName(ownerTask.CurrentPath ?? string.Empty) ?? string.Empty
                : Path.GetDirectoryName(group.EntryPath) ?? string.Empty;

            /*
             * 共用骨架：prepared 组已经由边界 resolver 定序/校验/出标准名，
             * ⛔ 不再走族算法二次定序，也不再把临时标准名当族证据重读。
             */
            if (group.PreparedGroup is { } prepared)
            {
                VolumeGroupUnit? preparedUnit =
                    _volumeGroupService.TryCommitPrepared(prepared, ownerName, outputLayer);

                if (preparedUnit == null)
                {
                    AppendLog(
                        "WARN",
                        $"[第二大步·组] {group.BaseName}：prepared 组建组被拒（源层/入口/片清单不完整）⇒ 不动作。");
                    return null;
                }

                AppendLog(
                    "INFO",
                    $"[第二大步·组] prepared 建组 {preparedUnit.BaseName}｜族={preparedUnit.Family}"
                    + $"｜入口={FileNameHelper.GetFileName(preparedUnit.EntryPath)}"
                    + $"｜owner={preparedUnit.OwnerTaskFileName}｜状态={preparedUnit.State}｜{preparedUnit.Verdict}");

                if (ownerTask != null)
                {
                    MarkFollowerTasksForPreparedGroup(prepared, ownerTask);
                }

                return preparedUnit;
            }

            VolumeNamingFamily? family;

            if (standardNameFastPath && standardFamily != null)
            {
                /*
                 * 用户 2026-10-27：同目录标准名组不再要求内容卡定族；
                 * 名字族已经由 StandardNameFastPath 唯一出口给出，直接用它去试解。
                 */
                family = standardFamily;
            }
            else
            {
                try
                {
                    family = ArchiveFixer.Detection.VolumePiecesFromCards.ResolveNamingFamily(group.Cards);
                }
                catch (Exception ex)
                {
                    AppendLog("WARN", $"[第二大步·组] {group.BaseName}：定族失败（{ex.GetType().Name}）⇒ 不建组。");
                    return null;
                }
            }

            if (family == null)
            {
                AppendLog("INFO", $"[第二大步·组] {group.BaseName}：族判不出 ⇒ 不建组（判不出就不动）。");
                return null;
            }

            /*
             * 已接线的族：**由族算法判完整性**；VolumeCandidateDiscovery 的候选建议不参与最终结论。
             */
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
        private void MarkFollowerTasksForGroup(VolumeGroupUnit unit, ArchiveTask? owner)
        {
            string ownerName = owner == null
                ? unit.OwnerTaskFileName ?? unit.BaseName
                : string.IsNullOrWhiteSpace(owner.DisplayFileName)
                    ? owner.FileName
                    : owner.DisplayFileName;

            foreach (VolumeFacts piece in unit.Pieces)
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
            }
        }

        /// <summary>
        /// prepared 组的跟班标记：按**原始路径**找任务（⛔ 不拿临时标准名去找）。
        /// </summary>
        private void MarkFollowerTasksForPreparedGroup(
            PreparedVolumeGroup prepared,
            ArchiveTask owner)
        {
            string ownerName = string.IsNullOrWhiteSpace(owner.DisplayFileName)
                ? owner.FileName
                : owner.DisplayFileName;

            foreach (PreparedVolumePiece piece in prepared.Pieces)
            {
                if (string.IsNullOrWhiteSpace(piece.OriginalPath))
                {
                    continue;
                }

                ArchiveTask? pieceTask = SnapshotTaskTable(Tasks).FirstOrDefault(candidate =>
                    candidate != null
                    && !ReferenceEquals(candidate, owner)
                    && !string.IsNullOrWhiteSpace(candidate.CurrentPath)
                    && (SafePathHelper.PathEquals(candidate.CurrentPath, piece.OriginalPath)
                        || FileIdentity.IsSamePhysicalFile(candidate.CurrentPath, piece.OriginalPath)));

                if (pieceTask == null)
                {
                    continue;
                }

                pieceTask.IsVolumeGroupFollower = true;
                pieceTask.SettledWithGroupUnitName = ownerName;
            }
        }

        /// <summary>入口没成功 ⇒ 先标过跟班的片不能继续显示“随整组解开”。</summary>
        private void MarkFollowerTasksGroupFailed(VolumeGroupUnit unit, ArchiveTask owner)
        {
            string ownerName = string.IsNullOrWhiteSpace(owner.DisplayFileName)
                ? owner.FileName
                : owner.DisplayFileName;

            foreach (VolumeFacts piece in unit.Pieces)
            {
                ArchiveTask? pieceTask = SnapshotTaskTable(Tasks).FirstOrDefault(candidate =>
                    candidate != null
                    && !ReferenceEquals(candidate, owner)
                    && candidate.IsVolumeGroupFollower
                    && !string.IsNullOrWhiteSpace(candidate.CurrentPath)
                    && (SafePathHelper.PathEquals(candidate.CurrentPath, piece.Path)
                        || FileIdentity.IsSamePhysicalFile(candidate.CurrentPath, piece.Path)));

                if (pieceTask == null || pieceTask.Outcome == TaskOutcome.Succeeded)
                {
                    continue;
                }

                pieceTask.Outcome = TaskOutcome.Skipped;
                pieceTask.EndTime = DateTime.Now;
                pieceTask.MarkSkipped(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    "同组入口「{0}」没有成功，这一片没有单独解（源包原样保留）。",
                    ownerName));
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

            /*
             * ⛔ 2026-11-01 真机：组已解成功、但组内跟班片任务可能是在**建组之前**
             * 就以「跳过」收场的（`2.part3.rar`）⇒ 它没被标跟班，链尾的
             * `DescribeChainVerificationGap` 会把它当成"链里有一层没过" ⇒ 源包永远不删。
             * 组成功这一层把组内其余片的任务补标成跟班（⛔ 不改它们自己的解压结论）。
             */
            MarkFollowerTasksForGroup(unit, FindOwnerTask(unit));

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
                    reclaimVerdict: groupVerdict);
            }

            Reclaim(owner);

            foreach (VolumeFacts piece in unit.Pieces)
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

            /*
             * ⛔ 2026-11-01 真机：同名文件在不同目录（2916/3100 都叫 `风景01.mp4`）时，
             * 只按文件名找 owner 会把两组的 owner 认成同一单 ⇒ 链接账/源包结算串组。
             * 先按**组的入口全路径/片路径**精确匹配，匹配不到才退回文件名（兼容入口被改名）。
             */
            ArchiveTask[] snapshot = SnapshotTaskTable(Tasks);

            /*
             * ⛔ 先认**入口**和**owner 文件名**，最后才允许按任意片路径兜底。
             *
             * 2026-11-08 真机：`aaa-L4` 四片里第一片是用户导入的 `.004`；旧写法把
             * “any piece path matches” 放在文件名匹配之前，于是 `FindOwnerTask` 返回了
             * `aaa-L4.7.....mkwz.004`（Outcome=Skipped），链尾整组被跳过，包装源包永远不结算。
             * 片路径只能证明“这一单手里有片”，不能证明“它是 owner”。
             */
            ArchiveTask? byEntry = snapshot.FirstOrDefault(candidate =>
                candidate != null
                && !string.IsNullOrWhiteSpace(candidate.CurrentPath)
                && !string.IsNullOrWhiteSpace(unit.EntryPath)
                && ArchiveFixer.Helpers.SafePathHelper.PathEquals(candidate.CurrentPath, unit.EntryPath));

            if (byEntry != null)
            {
                return byEntry;
            }

            ArchiveTask? byName = snapshot.FirstOrDefault(candidate =>
                candidate != null
                && !string.IsNullOrWhiteSpace(candidate.CurrentPath)
                && string.Equals(
                    FileNameHelper.GetFileName(candidate.CurrentPath),
                    unit.OwnerTaskFileName,
                    StringComparison.OrdinalIgnoreCase));

            if (byName != null)
            {
                return byName;
            }

            return snapshot.FirstOrDefault(candidate =>
                candidate != null
                && !string.IsNullOrWhiteSpace(candidate.CurrentPath)
                && unit.Pieces.Any(piece =>
                    !string.IsNullOrWhiteSpace(piece.Path)
                    && ArchiveFixer.Helpers.SafePathHelper.PathEquals(candidate.CurrentPath, piece.Path)));
        }


        internal bool TrySettleVolumeGroup(ArchiveTask? task)
        {
            if (task == null)
            {
                return false;
            }

            /*
             * ⛔ 2026-11-01 真机：同名文件在不同目录（2916/3100 都叫 `风景01.mp4`）时，
             * 按文件名找组会把两单认成同一组 ⇒ 第二组的链接账永远不结算。
             * 先按入口/片的**全路径**找组，找不到才退回文件名（兼容入口被改名的旧形状）。
             */
            string ownerName = FileNameHelper.GetFileName(task.CurrentPath ?? string.Empty);
            VolumeGroupUnit? unit = _volumeGroupService.FindForTask(task.CurrentPath, ownerName);

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
                bool groupWasComplete = unit.State is VolumeGroupState.Complete
                    or VolumeGroupState.OwnerAssigned
                    or VolumeGroupState.Linked
                    or VolumeGroupState.Extracting;

                if (task.Status == StatusText.Corrupted
                    || (groupWasComplete && task.Status == StatusText.VolumeMissing))
                {
                    RecordFinalGroupFailure(
                        unit,
                        FinalGroupFailureKind.Corrupted,
                        groupWasComplete
                            ? "组事实完整，但引擎解不出最终内容：按数据损坏处理"
                            : $"最终内容分卷组数据损坏：{task.Status}",
                        Tasks);
                }

                _volumeGroupService.MarkState(
                    unit,
                    VolumeGroupState.Failed,
                    "没成功（密码错 / 缺卷 / 失败），片全部留在入口层");
                MarkFollowerTasksGroupFailed(unit, task);
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

            // 组成功这一层把组内其余片补标成跟班（含建组之前就以「跳过」收场的那几片）。
            MarkFollowerTasksForGroup(unit, task);

            /*
             * 组成功 ⇒ 这一组每一片对应的任务按**跟班卷**收场（唯一出口
             * `SettleOneConsumedPieceTask`，与既有的“谁吐过片”那本账同一套闸门）：
             * 源片在「彻底删除」档下这才有人处理；Keep 档一个字节都不动。
             * ⛔ 不新造删除判据、⛔ 不直接 File.Delete。
             */
            var pieceSettleLogs = new List<(string Level, string Message)>();
            IReadOnlyList<ArchiveTask> taskSnapshot = SnapshotTaskTable(Tasks);

            foreach (VolumeFacts piece in unit.Pieces)
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

        /// <summary>
        /// **链尾：成功组的原包按「源包档」收**（用户 2026-11-01 口径）。
        ///
        /// <para>过程物片在收片那一刻已经删掉；**原包不许先动** —— 用户原包片、以及"只出过程物"的
        /// 包壳都留到这里，由源包档决定删还是留。判据只读运行期事实：</para>
        /// <list type="number">
        /// <item><description>组不缺片，owner 任务成功，且完整性判据允许删源；</description></item>
        /// <item><description>**入口包落点里有内容物**（用户 2026-11-01 订正：分卷的内容物是经入口包
        /// 解出来的，所以要检查**入口包的位置**，不是包壳自己的落点目录）；</description></item>
        /// <item><description>源包档 = 彻底删除才动作；留下档一个字节都不动。</description></item>
        /// </list>
        ///
        /// <para>包装壳（产出片被本组接手的那几单）也在这里按同一档收：它们的"内容"就是入口包树里
        /// 那些内容物 —— ⛔ 不按包名猜，按「谁产出的这一片」这条事实认（<see cref="ArchiveTask.ProducedFiles"/>，
        /// 认不出再按落点树），⛔ 不放宽半套分卷闸门（走既有 <see cref="PurgeLayerSourcePackage"/> 唯一出口）。</para>
        /// </summary>
        internal void SettleSucceededGroupSourcesAtChainEnd(OneClickRunOptions? runOptions)
        {
            if (_volumeGroupService.Units.Count == 0)
            {
                return;
            }

            SourceHandlingMode handling = _spaceTightPolicy.Enabled
                ? SourceHandlingMode.DeleteInPlace
                : runOptions?.SourceHandling ?? AppSettings.ParseSourceHandling(Settings?.SourceHandling);

            if (handling != SourceHandlingMode.DeleteInPlace)
            {
                return;
            }

            var logEntries = new List<(string Level, string Message)>();
            ArchiveTask[] tasks = SnapshotTaskTable(Tasks);

            foreach (VolumeGroupUnit unit in _volumeGroupService.Units.ToList())
            {
                if (unit == null || unit.MissingNumbers.Count > 0)
                {
                    continue;
                }

                ArchiveTask? owner = FindOwnerTask(unit);

                if (owner == null || owner.Outcome != TaskOutcome.Succeeded)
                {
                    continue;
                }

                ResultCompletenessVerdict verdict = ResultCompletenessClassifier.Classify(owner);

                if (!verdict.AllowsSourceRemoval)
                {
                    continue;
                }

                string entryOutput = owner.OutputPath;

                if (string.IsNullOrWhiteSpace(entryOutput))
                {
                    entryOutput = owner.ContentDirectoryPath;
                }

                if (string.IsNullOrWhiteSpace(entryOutput)
                    || !Directory.Exists(entryOutput)
                    || _restHandlingStep.CountContentFiles(entryOutput) == 0)
                {
                    continue;
                }

                _volumeGroupService.MarkState(
                    unit,
                    VolumeGroupState.Succeeded,
                    $"解压成功，入口包落点已有内容物：{entryOutput}");

                // 组入口自己的源包 + 直接导入的分卷原包片（过程物片早在收片时删掉了）
                ReclaimSucceededGroupSources(unit, owner);

                // 包装壳：它的产出片被这一组接手、组又可证完整 ⇒ 原包按同一档处理（⛔ 不是过程物）。
                foreach (VolumeFacts piece in unit.Pieces)
                {
                    if (string.IsNullOrWhiteSpace(piece.Path))
                    {
                        continue;
                    }

                    /*
                     * 每一片都要找**产出它的那一单**，不能按 piece.Source 分叉：
                     * 用户导入的外层包装（111-1.rar / 111-2.zip）解出的片，Source 可能仍是
                     * UserImported，但产出的片路径在包装单的输出树里。老写法在这类片上
                     * `continue`，于是包装单永远不走源包结算。
                     *
                     * 判据仍是链尾这一份组结论：组齐全 + owner 可证完整 + 内容校验通过；
                     * 删除仍只走 PurgeLayerSourcePackage 唯一出口。
                     */
                    ArchiveTask? producer = FindProducingTaskForPiece(tasks, piece.Path, owner)
                        ?? tasks.FirstOrDefault(candidate =>
                            candidate != null
                            && !ReferenceEquals(candidate, owner)
                            && !string.IsNullOrWhiteSpace(candidate.CurrentPath)
                            && SafePathHelper.PathEquals(candidate.CurrentPath, piece.Path));

                    if (producer == null
                        || ReferenceEquals(producer, owner)
                        || producer.Outcome != TaskOutcome.Succeeded)
                    {
                        continue;
                    }

                    PurgeLayerSourcePackage(
                        producer,
                        verification: null,
                        commit: null,
                        logEntries,
                        LayerPurgeTrigger.SourceHandlingDelete,
                        reclaimVerdict: verdict);
                }
            }

            foreach ((string level, string message) in logEntries)
            {
                AppendLog(level, message);
            }
        }

        /// <summary>
        /// 这一片是哪一单产出的：先认任务自己记下的 <see cref="ArchiveTask.ProducedFiles"/>，
        /// 认不出再按"它的落点树里确实有这一片"这条盘上事实认。⛔ 不按名字猜。
        /// </summary>
        private static ArchiveTask? FindProducingTaskForPiece(
            IReadOnlyList<ArchiveTask> tasks,
            string piecePath,
            ArchiveTask? exclude)
        {
            if (string.IsNullOrWhiteSpace(piecePath))
            {
                return null;
            }

            foreach (ArchiveTask candidate in tasks)
            {
                if (candidate == null || ReferenceEquals(candidate, exclude))
                {
                    continue;
                }

                if (candidate.ProducedFiles.Any(produced =>
                        SafePathHelper.PathEquals(produced, piecePath)
                        || FileIdentity.IsSamePhysicalFile(produced, piecePath)))
                {
                    return candidate;
                }
            }

            foreach (ArchiveTask candidate in tasks)
            {
                if (candidate == null
                    || ReferenceEquals(candidate, exclude)
                    || string.IsNullOrWhiteSpace(candidate.OutputPath))
                {
                    continue;
                }

                if (ArchiveFixer.Security.ArchivePathGuard.IsInsideRoot(candidate.OutputPath, piecePath, out _))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}


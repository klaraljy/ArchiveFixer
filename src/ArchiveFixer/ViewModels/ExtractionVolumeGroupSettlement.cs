using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 分卷组收场（从 `ExtractionCoordinator.VolumeStepStep.cs` 搬出）：入口已定后给同组其余片落跟班事实、
    /// 组解开后按物理身份把原始路径记成「已消费源包」、成功组按档回收源包、按组找 owner 任务；
    /// ⛔ 删除闸门与文案沿用原实现。
    /// </summary>
    internal sealed class ExtractionVolumeGroupSettlement
    {
        private readonly ExtractionCoordinator _host;

        internal ExtractionVolumeGroupSettlement(ExtractionCoordinator host)
        {
            _host = host;
        }


        /// <summary>
        /// 入口已定 ⇒ 同组其余片的任务先落“跟班”事实。
        /// 这样它们开跑前就不会再撞“源文件已变化”，也不会各自解一遍整组。
        /// </summary>
        internal void MarkFollowerTasksForGroup(VolumeGroupUnit unit, ArchiveTask? owner)
        {
            string ownerName = owner == null
                ? unit.OwnerTaskFileName ?? unit.BaseName
                : string.IsNullOrWhiteSpace(owner.DisplayFileName)
                    ? owner.FileName
                    : owner.DisplayFileName;

            foreach (VolumeFacts piece in unit.Pieces)
            {
                ArchiveTask? pieceTask = ExtractionCoordinator.SnapshotTaskTable(_host.Tasks).FirstOrDefault(candidate =>
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
        internal void MarkFollowerTasksForPreparedGroup(
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

                ArchiveTask? pieceTask = ExtractionCoordinator.SnapshotTaskTable(_host.Tasks).FirstOrDefault(candidate =>
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
        internal void MarkFollowerTasksGroupFailed(VolumeGroupUnit unit, ArchiveTask owner)
        {
            string ownerName = string.IsNullOrWhiteSpace(owner.DisplayFileName)
                ? owner.FileName
                : owner.DisplayFileName;

            foreach (VolumeFacts piece in unit.Pieces)
            {
                ArchiveTask? pieceTask = ExtractionCoordinator.SnapshotTaskTable(_host.Tasks).FirstOrDefault(candidate =>
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
                pieceTask.EndTime = ArchiveFixer.Helpers.Clock.Now;
                pieceTask.MarkSkipped(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    "同组入口「{0}」没有成功，这一片没有单独解（源包原样保留）。",
                    ownerName));
            }
        }

        /// <summary>
        /// **组已经解开**的统一出口：把组里每一片的原始路径在唯一账本里记成「已消费源包」，
        /// 后续轮次的组分析不许再从留在盘上的用户原件重建同一组。
        ///
        /// <para>真机 125 批 926000008（运行期事实）：KeepInPlace 留下用户原件；组解开收场之后，
        /// 下一轮用剩下的 4 片又重建了一个 `SizeAnomaly` 组（`缺=1`、`通用 ZIP 槽位 4 有两份不同文件`），
        /// 把已经收场的跟班任务刷成「最终内容分卷组结构异常且缺片」⇒ 整单算失败。</para>
        ///
        /// <para>按**物理身份**标（改名前后是同一条账本记录）；判据只认路径/身份事实，
        /// ⛔ 不看后缀、基名、内容格式。</para>
        /// </summary>
        internal void MarkVolumeGroupPiecesConsumed(IEnumerable<string>? piecePaths)
        {
            foreach (string path in piecePaths ?? Enumerable.Empty<string>())
            {
                _host._firstStepFileLedger.MarkConsumedByIdentity(path);
            }
        }
        internal void ReclaimSucceededGroupSources(VolumeGroupUnit unit, ArchiveTask? ownerTask)
        {
            if (unit == null
                || unit.State != VolumeGroupState.Succeeded
                || unit.MissingNumbers.Count > 0)
            {
                return;
            }

            SourceHandlingMode handling = _host.RunOptions?.SourceHandling
                ?? AppSettings.ParseSourceHandling(_host.Settings?.SourceHandling);

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

                _host.PurgeLayerSourcePackage(
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
                    && !_host.IsUserImportedSourcePath(piece.Path))
                {
                    continue;
                }

                ArchiveTask? pieceTask = ExtractionCoordinator.SnapshotTaskTable(_host.Tasks).FirstOrDefault(candidate =>
                    candidate != null
                    && !string.IsNullOrWhiteSpace(candidate.CurrentPath)
                    && (SafePathHelper.PathEquals(candidate.CurrentPath, piece.Path)
                        || FileIdentity.IsSamePhysicalFile(candidate.CurrentPath, piece.Path)));

                Reclaim(pieceTask);
            }

            foreach ((string level, string message) in logEntries)
            {
                _host.AppendLog(level, message);
            }
        }

        internal ArchiveTask? FindOwnerTask(VolumeGroupUnit unit)
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
            ArchiveTask[] snapshot = ExtractionCoordinator.SnapshotTaskTable(_host.Tasks);

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
    }
}


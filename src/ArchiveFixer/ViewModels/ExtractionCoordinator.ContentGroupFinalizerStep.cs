using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;

namespace ArchiveFixer.ViewModels
{
    /// <summary>内容组最终内容判决：任务级成功不代替组级最终内容。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// 从当前任务表回填每组最终内容。只读任务上的既有校验/定稿事实，
        /// ⛔ 不重算解压结果、⛔ 不把 Pending/Skipped 的 owner 判成失败。
        /// </summary>
        /// <summary>
        /// 组事实层登记最终组失败：不链接、不解压、不删源包；只把 Missing/Corrupted
        /// 写进 ContentGroup，并把最终层输入片的直接产出者记成事实。
        /// </summary>
        internal bool RecordFinalGroupFailure(
            VolumeGroupUnit? unit,
            FinalGroupFailureKind kind,
            string evidence,
            IReadOnlyList<ArchiveTask>? tasks)
        {
            if (unit == null
                || kind == FinalGroupFailureKind.None
                || !_extractionContentGraph.TryGetContentGroup(
                    unit.BaseName,
                    unit.Family,
                    unit.SourceLayer,
                    out ContentGroup group))
            {
                return false;
            }

            bool changed = group.MarkFinalGroupFailure(kind, evidence);

            foreach (VolumeFacts piece in unit.Pieces)
            {
                if (string.IsNullOrWhiteSpace(piece.Path))
                {
                    continue;
                }

                ArchiveTask? producer = tasks?.FirstOrDefault(candidate =>
                    candidate != null
                    && candidate.ProducedFiles.Any(path =>
                        SafePathHelper.PathEquals(path, piece.Path)));

                if (producer == null)
                {
                    continue;
                }

                bool isSource = string.IsNullOrWhiteSpace(producer.CurrentPath)
                    || IsUserImportedSourcePath(producer.CurrentPath)
                    || SafePathHelper.PathEquals(producer.CurrentPath, producer.OriginalPath);

                group.RecordFinalLayerProducer(producer.FileName, producer.CurrentPath, isSource);
            }

            TaskReason reason = kind == FinalGroupFailureKind.Corrupted
                ? TaskReason.FinalGroupCorrupted
                : TaskReason.FinalGroupIncomplete;

            string status = kind == FinalGroupFailureKind.Corrupted
                ? StatusText.FinalGroupCorrupted
                : StatusText.FinalGroupIncomplete;

            ArchiveTask? representative = tasks?.FirstOrDefault(candidate =>
                candidate != null
                && !string.IsNullOrWhiteSpace(candidate.CurrentPath)
                && (SafePathHelper.PathEquals(candidate.CurrentPath, unit.EntryPath)
                    || unit.Pieces.Any(piece => SafePathHelper.PathEquals(candidate.CurrentPath, piece.Path))));

            if (representative != null)
            {
                representative.Outcome = TaskOutcome.Failed;
                representative.Reason = reason;
                representative.Status = status;
                representative.ErrorMessage = evidence;
                representative.EndTime ??= DateTime.Now;
            }

            return changed;
        }

        internal int FinalizeContentGroups(IReadOnlyList<ArchiveTask>? tasks)
        {
            if (tasks == null || tasks.Count == 0)
            {
                return 0;
            }

            int changedCount = 0;

            foreach (ContentGroup group in _extractionContentGraph.ContentGroups.ToList())
            {
                ArchiveTask? owner = FindContentGroupOwner(group, tasks);

                if (owner == null || owner.Outcome is TaskOutcome.Pending or TaskOutcome.Skipped)
                {
                    continue;
                }

                /*
                 * owner 自己往往只是“这一组的入口包”，它的内容物可能是内层包；
                 * 最终内容要取 owner 输出树下**最深的有内容物任务**，不能拿入口层的
                 * “内容物=1 个内层包”冒充最终内容。
                 */
                ArchiveTask finalTask = FindFinalContentTask(owner, tasks) ?? owner;

                string contentPath = !string.IsNullOrWhiteSpace(finalTask.ContentDirectoryPath)
                    ? finalTask.ContentDirectoryPath
                    : finalTask.OutputPath;

                (int measuredFiles, long measuredBytes) = OutputVerifier.Measure(contentPath);
                int fileCount = finalTask.ContentFileCount > 0
                    ? finalTask.ContentFileCount
                    : measuredFiles;

                (FinalContentState state, string evidence) = ClassifyFinalContent(finalTask, fileCount);

                /*
                 * 组事实已经是 Complete，却解不出最终内容：这不是缺片，而是完整组的数据坏。
                 * 只在组事实层做这个判断；⛔ 不按文件存在性/任务表猜。
                 */
                if (group.State == PreparedGroupState.Complete
                    && state != FinalContentState.Verified
                    && (owner.Status == StatusText.Corrupted
                        || owner.Status == StatusText.VolumeMissing))
                {
                    group.MarkFinalGroupFailure(
                        FinalGroupFailureKind.Corrupted,
                        $"组事实 Complete，但引擎解不出最终内容：{owner.Status}");
                    state = FinalContentState.Missing;
                    evidence = group.FailureEvidence;
                }

                if (group.FailureKind != FinalGroupFailureKind.None)
                {
                    state = FinalContentState.Missing;
                    evidence = group.FailureEvidence;

                    if (owner.Reason != TaskReason.PasswordExhausted)
                    {
                        owner.Reason = group.FailureKind == FinalGroupFailureKind.Corrupted
                            ? TaskReason.FinalGroupCorrupted
                            : TaskReason.FinalGroupIncomplete;
                        owner.Outcome = TaskOutcome.Failed;
                        owner.Status = group.FailureKind == FinalGroupFailureKind.Corrupted
                            ? StatusText.FinalGroupCorrupted
                            : StatusText.FinalGroupIncomplete;
                    }
                }

                bool changed = group.SetFinalOutput(
                    owner.OutputPath,
                    contentPath,
                    state,
                    fileCount,
                    measuredBytes,
                    evidence);

                MarkContentGroupTasks(group, owner, finalTask, tasks, state);

                if (state == FinalContentState.Verified)
                {
                    CleanupEmptyProcessOutputDirectories(group, tasks);
                }

                if (!changed)
                {
                    continue;
                }

                changedCount++;

                AppendLog(
                    state == FinalContentState.Verified ? "INFO" : "WARN",
                    $"[内容组·最终] {group.BaseName}：{state} ｜ 文件={fileCount}"
                    + $" ｜ 输出={(string.IsNullOrWhiteSpace(contentPath) ? "判不出" : contentPath)}"
                    + $" ｜ 证据={evidence}");
            }

            return changedCount;
        }

        private static (FinalContentState State, string Evidence) ClassifyFinalContent(
            ArchiveTask owner,
            int fileCount)
        {
            if (owner.Outcome is TaskOutcome.Failed or TaskOutcome.Cancelled or TaskOutcome.PartiallyCompleted)
            {
                return fileCount > 0
                    ? (FinalContentState.Partial, $"owner={owner.Outcome}；有 {fileCount} 个产物但未通过最终校验")
                    : (FinalContentState.Missing, $"owner={owner.Outcome}；没有最终内容");
            }

            if (owner.Outcome != TaskOutcome.Succeeded || fileCount <= 0)
            {
                return (FinalContentState.Missing, "owner 不是成功终态，或最终内容为空");
            }

            if (!owner.IsOutputVerified)
            {
                return (FinalContentState.Partial, "产物未通过输出校验");
            }

            if (owner.OutputOnlyZeroByteEntriesMissing)
            {
                return (FinalContentState.Partial, "内容字节完整，但逐条清单只缺 0 字节条目；不升 Complete、不删源包");
            }

            if (!owner.OutputManifestCrossChecked)
            {
                return (FinalContentState.Partial, "产物只做了非空底线校验，未拿清单逐条核对");
            }

            return (FinalContentState.Verified, "输出校验通过且清单逐条核对");
        }

        private static void MarkContentGroupTasks(
            ContentGroup group,
            ArchiveTask owner,
            ArchiveTask finalTask,
            IReadOnlyList<ArchiveTask> tasks,
            FinalContentState state)
        {
            string ownerOutput = SafeFullPath(owner.OutputPath);

            foreach (ArchiveTask task in tasks)
            {
                if (task == null)
                {
                    continue;
                }

                bool inGroup = group.SourceRootIds.Any(rootId =>
                        string.Equals(
                            NormalizePath(task.OriginalPath),
                            NormalizePath(rootId),
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            NormalizePath(task.CurrentPath),
                            NormalizePath(rootId),
                            StringComparison.OrdinalIgnoreCase))
                    || (ownerOutput.Length > 0
                        && (IsUnder(SafeFullPath(task.OutputPath), ownerOutput)
                            || IsUnder(SafeFullPath(task.CurrentPath), ownerOutput)));

                if (!inGroup)
                {
                    continue;
                }

                bool isFinalTask = ReferenceEquals(task, finalTask);
                task.ApplyContentGroupFacts(
                    group.GroupId,
                    group.BaseName,
                    state,
                    isProcessTask: !isFinalTask,
                    finalContentPath: isFinalTask ? group.FinalContentPath : string.Empty,
                    finalOutputPath: isFinalTask ? group.FinalOutputRoot : string.Empty);
            }
        }

        /// <summary>
        /// 指定位置档：组内过程任务的输出壳在成功后已经空了，连同空父目录一起收掉。
        /// ⛔ 只删空目录、只删 PlacementRoot 之下；有任何一个文件/非空子目录就停手。
        /// </summary>
        private static void CleanupEmptyProcessOutputDirectories(
            ContentGroup group,
            IReadOnlyList<ArchiveTask> tasks)
        {
            string placementRoot = SafeFullPath(group.PlacementRoot);

            if (placementRoot.Length == 0)
            {
                return;
            }

            foreach (ArchiveTask task in tasks)
            {
                if (task == null
                    || !task.IsContentGroupProcessTask
                    || !string.Equals(task.ContentGroupId, group.GroupId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string directory = SafeFullPath(task.OutputPath);

                if (directory.Length == 0
                    || string.Equals(directory, placementRoot, StringComparison.OrdinalIgnoreCase)
                    || !IsUnder(directory, placementRoot))
                {
                    continue;
                }

                if (!Directory.Exists(directory))
                {
                    directory = SafeFullPath(Path.GetDirectoryName(directory));
                }

                while (directory.Length > 0
                       && !string.Equals(directory, placementRoot, StringComparison.OrdinalIgnoreCase)
                       && IsUnder(directory, placementRoot)
                       && Directory.Exists(directory)
                       && !ContainsAnyFile(directory))
                {
                    try
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                    catch
                    {
                        break;
                    }

                    directory = SafeFullPath(Path.GetDirectoryName(directory));
                }
            }
        }

        private static bool ContainsAnyFile(string directory)
        {
            try
            {
                return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any();
            }
            catch
            {
                return true;
            }
        }

        private static string NormalizePath(string? path) => SafeFullPath(path);

        private static ArchiveTask? FindFinalContentTask(
            ArchiveTask owner,
            IReadOnlyList<ArchiveTask> tasks)
        {
            string ownerOutput = SafeFullPath(owner.OutputPath);

            if (ownerOutput.Length == 0)
            {
                return owner.ContentFileCount > 0 ? owner : null;
            }

            ArchiveTask? best = null;
            int bestDepth = -1;

            foreach (ArchiveTask task in tasks)
            {
                if (task == null
                    || task == owner
                    || task.Outcome != TaskOutcome.Succeeded
                    || task.ContentFileCount <= 0)
                {
                    continue;
                }

                string contentPath = SafeFullPath(
                    !string.IsNullOrWhiteSpace(task.ContentDirectoryPath)
                        ? task.ContentDirectoryPath
                        : task.OutputPath);

                if (contentPath.Length == 0 || !IsUnder(contentPath, ownerOutput))
                {
                    continue;
                }

                int depth = contentPath.Count(ch => ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar);

                if (depth > bestDepth)
                {
                    best = task;
                    bestDepth = depth;
                }
            }

            return best;
        }

        private static string SafeFullPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsUnder(string path, string root)
        {
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static ArchiveTask? FindContentGroupOwner(
            ContentGroup group,
            IReadOnlyList<ArchiveTask> tasks)
        {
            foreach (ArchiveTask task in tasks)
            {
                if (task == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(group.OwnerTaskPath)
                    && (PathsEqual(task.CurrentPath, group.OwnerTaskPath)
                        || PathsEqual(task.OriginalPath, group.OwnerTaskPath)))
                {
                    return task;
                }

                if (!string.IsNullOrWhiteSpace(group.OwnerTaskFileName)
                    && string.Equals(task.FileName, group.OwnerTaskFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return task;
                }
            }

            return null;
        }

        private static bool PathsEqual(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.GetFullPath(left),
                    Path.GetFullPath(right),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

    }
}

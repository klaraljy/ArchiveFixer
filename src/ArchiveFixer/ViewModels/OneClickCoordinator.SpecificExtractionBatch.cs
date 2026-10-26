using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;

namespace ArchiveFixer.ViewModels
{
    /// <summary>一键处理 · 特定解压独立入口（功能代码独立文件，⛔ 不再塞回主文件）。</summary>
    internal sealed partial class OneClickCoordinator
    {
        /// <summary>
        /// 特定解压的一键入口：每个源包独立跑 3 层以内的简单管线，不进入轮次/第二大步。
        /// 输出根按用户选中的合集根算；源包与其余物按当前档处理，空间不足模式覆盖为
        /// 「用户已接受风险：成功/失败都删源包」。
        /// </summary>
        private async Task<OneClickOutcome> RunSpecificBatchAsync(
            IReadOnlyList<ArchiveTask> targets,
            OneClickRunOptions? runOptions)
        {
            runOptions ??= OneClickRunOptions.FromSettings(Settings, _vm.SelectedOutputDirectory);

            var runner = new SpecificExtractionRunner(
                _extractionCoordinator.SpecificExtractionEngine,
                message => AppendLog("INFO", message));
            _extractionCoordinator.BeginSpecificPasswordBatch();
            // 空间不足模式：按源包体积从大到小；源包按用户接受风险处理（失败/预检不足也删）。
            bool spaceTight = _vm.SpaceTightMode;
            IReadOnlyList<SpecificBatchUnit> orderedUnits = SpecificBatchUnitBuilder.Build(targets);

            if (spaceTight)
            {
                orderedUnits = orderedUnits
                    .OrderByDescending(unit => unit.SourceBytes)
                    .ThenBy(unit => unit.Entry.CurrentPath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            int succeeded = 0;
            int failed = 0;
            int skippedFollowers = 0;

            var pending = new List<SpecificBatchUnit>(orderedUnits);

            while (pending.Count > 0)
            {
                if (_vm.IsStopping)
                {
                    break;
                }

                /*
                 * 空间不足：按整组源包体积从大到小，按**当前可用空间**一浪一浪放；
                 * 每浪跑完重新探盘，后面的小包自然能提高并发。
                 */
                int waveSize = spaceTight
                    ? ResolveSpecificParallel(pending, runOptions)
                    : 1;

                waveSize = Math.Max(1, Math.Min(waveSize, pending.Count));

                List<SpecificBatchUnit> wave = pending.Take(waveSize).ToList();
                pending.RemoveRange(0, waveSize);

                if (spaceTight)
                {
                    AppendLog(
                        "INFO",
                        $"特定解压空间不足动态并发：本浪 {waveSize} 个（待处理 {pending.Count} 个，上限 {Math.Clamp(Settings.MaxParallelExtractCount, 1, 8)}）");
                }

                bool[] results = await Task
                    .WhenAll(wave.Select(unit => ProcessSpecificUnitAsync(unit, runOptions, spaceTight, runner)))
                    .ConfigureAwait(true);

                foreach (SpecificBatchUnit unit in wave)
                {
                    skippedFollowers += unit.Followers.Count;
                }

                foreach (bool success in results)
                {
                    if (success)
                    {
                        succeeded++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }

            string summary = $"特定解压完成：成功 {succeeded} / 失败 {failed}"
                             + (skippedFollowers > 0 ? $" / 跟班卷 {skippedFollowers}" : string.Empty)
                             + $"（本次 {targets.Count} 个任务）。";
            AppendLog("INFO", summary);

            return new OneClickOutcome
            {
                Rounds = 1,
                Summary = summary,
                Severity = failed == 0 ? BatchSummarySeverity.Success : BatchSummarySeverity.Failed,
                Diagnostics = string.Empty,
                DiagnosticLines = Array.Empty<string>()
            };
        }

        private async Task<bool> ProcessSpecificUnitAsync(
            SpecificBatchUnit unit,
            OneClickRunOptions runOptions,
            bool spaceTight,
            SpecificExtractionRunner runner)
        {
            bool success = await ProcessSpecificTaskAsync(
                    unit.Entry,
                    runOptions,
                    spaceTight,
                    runner,
                    requiredSourceBytes: unit.SourceBytes)
                .ConfigureAwait(true);

            foreach (ArchiveTask follower in unit.Followers)
            {
                MarkSpecificFollower(follower, unit.Entry, success, runOptions, spaceTight);
            }

            return success;
        }

        private void MarkSpecificFollower(
            ArchiveTask follower,
            ArchiveTask entry,
            bool entrySuccess,
            OneClickRunOptions runOptions,
            bool spaceTight)
        {
            follower.IsVolumeGroupFollower = true;
            follower.Status = StatusText.Skipped;
            follower.ProgressText = StatusText.ProgressSkipped;
            follower.Outcome = TaskOutcome.Skipped;
            follower.EndTime = DateTime.Now;
            follower.ErrorMessage = entrySuccess
                ? $"跟班卷：同组入口「{entry.LogName}」已处理"
                : $"跟班卷：同组入口「{entry.LogName}」没有成功";

            if (entrySuccess)
            {
                bool deleteSource = runOptions.SourceHandling == SourceHandlingMode.DeleteInPlace
                    || spaceTight;

                if (deleteSource)
                {
                    SourceCleanupResult cleanup = new SourceCleanupService().CleanupVerified(
                        follower,
                        verified: true,
                        verificationNote: $"同组入口「{entry.LogName}」已成功",
                        enabled: true);

                    AppendLog(
                        cleanup.FailedFiles.Count == 0 ? "INFO" : "WARN",
                        $"{follower.LogName}：跟班卷随同组入口处理源包 —— {cleanup.Message}");
                }

                return;
            }

            DeleteSourceOnSpaceRisk(follower, spaceTight);
        }

        private async Task<bool> ProcessSpecificTaskAsync(
            ArchiveTask task,
            OneClickRunOptions runOptions,
            bool spaceTight,
            SpecificExtractionRunner runner,
            long requiredSourceBytes = 0)
        {
            string source = task.CurrentPath;

            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                MarkSpecificTaskFailed(task, "源文件不存在");
                return false;
            }

            string outputRoot = ResolveSpecificOutputRoot(task, runOptions);

            if (string.IsNullOrWhiteSpace(outputRoot))
            {
                MarkSpecificTaskFailed(task, "输出目录无效");
                return false;
            }

            if (spaceTight)
            {
                long freeBytes = GetFreeBytes(outputRoot);
                long sourceBytes = requiredSourceBytes > 0 ? requiredSourceBytes : GetSourceSize(task);

                if (freeBytes < sourceBytes)
                {
                    DeleteSourceOnSpaceRisk(task, spaceTight);
                    MarkSpecificTaskFailed(
                        task,
                        $"空间不足：需要 {sourceBytes:N0} 字节，当前只剩 {freeBytes:N0} 字节");
                    return false;
                }
            }

            string workRoot = Path.Combine(
                outputRoot,
                ".ArchiveFixer.work",
                "specific-" + Guid.NewGuid().ToString("N"));

            SpecificExtractionRunResult result;

            try
            {
                result = await runner
                    .RunAsync(
                        source,
                        outputRoot,
                        workRoot,
                        _extractionCoordinator.SpecificExtractionPasswords,
                        passwordProvider: () => _extractionCoordinator.SpecificExtractionPasswords,
                        onPasswordSuccess: password =>
                            _extractionCoordinator.RecordSpecificPasswordSuccess(source, password))
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                result = new SpecificExtractionRunResult { Message = ex.Message };
            }
            finally
            {
                try
                {
                    if (Directory.Exists(workRoot))
                    {
                        Directory.Delete(workRoot, recursive: true);
                    }

                    string workParent = Path.Combine(outputRoot, ".ArchiveFixer.work");

                    if (Directory.Exists(workParent)
                        && !Directory.EnumerateFileSystemEntries(workParent).Any())
                    {
                        Directory.Delete(workParent);
                    }
                }
                catch
                {
                }
            }

            if (result.Success)
            {
                task.OutputPath = result.OutputPath;
                task.ContentDirectoryPath = result.OutputPath;
                task.RestDirectoryPath = result.RestDirectory;
                task.Status = StatusText.ExtractSuccess;
                task.ProgressText = StatusText.ExtractSuccess;
                task.IsOutputVerified = true;
                task.Outcome = TaskOutcome.Succeeded;
                task.EndTime = DateTime.Now;
                task.ApplyProgress(100, null);

                bool deleteSource = runOptions.SourceHandling == SourceHandlingMode.DeleteInPlace
                    || spaceTight;

                SourceCleanupResult cleanup = new SourceCleanupService().CleanupVerified(
                    task,
                    verified: true,
                    verificationNote: "特定解压内容物已落地",
                    enabled: deleteSource,
                    includeRestAliases: true);

                if (deleteSource)
                {
                    AppendLog(
                        cleanup.FailedFiles.Count == 0 ? "INFO" : "WARN",
                        $"{task.LogName}：特定解压后源包处理 —— {cleanup.Message}");
                }

                if ((spaceTight
                        || string.Equals(runOptions.RestHandling, RestHandlingModes.Delete, StringComparison.OrdinalIgnoreCase))
                    && !string.IsNullOrWhiteSpace(result.RestDirectory))
                {
                    try
                    {
                        Directory.Delete(result.RestDirectory, recursive: true);
                        task.RestDirectoryPath = string.Empty;
                        AppendLog(
                            "INFO",
                            spaceTight
                                ? $"{task.LogName}：空间不足模式，最后一层过程物在成功后彻底删除"
                                : $"{task.LogName}：特定解压其余物按删除档清理完毕");
                    }
                    catch (Exception ex)
                    {
                        AppendLog("WARN", $"{task.LogName}：特定解压其余物删除失败：{ex.Message}");
                    }
                }

                AppendLog(
                    "INFO",
                    $"{task.LogName}：特定解压完成 → {Path.GetFileName(result.OutputPath)}（{result.Depth + 1} 层）"
                    + (spaceTight ? $"；空间不足剩余 {TryGetFreeBytes(outputRoot)}" : string.Empty));

                _vm.Logger.Event(
                    "specific.success",
                    result.Message,
                    detail: $"层={result.Depth + 1}; 输出={result.OutputPath}",
                    level: "INFO",
                    file: source,
                    source: source,
                    target: result.OutputPath,
                    result: "Succeeded",
                    group: "特定解压");

                return true;
            }

            task.RestDirectoryPath = result.RestDirectory;
            DeleteSourceOnSpaceRisk(task, spaceTight);
            MarkSpecificTaskFailed(task, result.Message);
            AppendLog("WARN", $"{task.LogName}：特定解压失败 —— {result.Message}");

            _vm.Logger.Event(
                "specific.failure",
                result.Message,
                detail: result.DiagnosticDetail,
                level: "ERROR",
                file: source,
                source: source,
                result: "Failed",
                group: "特定解压");

            return false;
        }

        private int ResolveSpecificParallel(
            IReadOnlyList<SpecificBatchUnit> pending,
            OneClickRunOptions runOptions)
        {
            int cap = Math.Clamp(Settings.MaxParallelExtractCount, 1, 8);

            if (pending == null || pending.Count == 0)
            {
                return 1;
            }

            long freeBytes = pending
                .Select(unit => GetFreeBytes(ResolveSpecificOutputRoot(unit.Entry, runOptions)))
                .Where(bytes => bytes > 0)
                .DefaultIfEmpty(0)
                .Min();

            List<long> sizes = pending.Select(unit => unit.SourceBytes).ToList();
            int launchable = SpaceConcurrencyPlanner.CountLaunchable(freeBytes, 0, sizes, cap);

            return Math.Max(1, Math.Min(cap, launchable));
        }

        private static string ResolveSpecificOutputRoot(ArchiveTask task, OneClickRunOptions runOptions)
        {
            string source = task.CurrentPath;

            return runOptions.PlacementMode == OutputPlacementMode.CustomRootPerArchive
                ? runOptions.CustomRoot
                : SpecificOutputNaming.ResolveOutputRoot(
                    source,
                    task.SourceSelectionKind,
                    task.SourceSelectionRoot);
        }

        private void DeleteSourceOnSpaceRisk(ArchiveTask task, bool spaceTight)
        {
            if (!spaceTight)
            {
                return;
            }

            SourceCleanupResult cleanup = new SourceCleanupService()
                .CleanupAcceptedRisk(task, enabled: true);

            AppendLog(
                cleanup.FailedFiles.Count == 0 ? "INFO" : "WARN",
                $"{task.LogName}：空间不足模式删源包（用户已接受风险）—— {cleanup.Message}");
        }

        private static void MarkSpecificTaskFailed(ArchiveTask task, string message)
        {
            task.Status = StatusText.ExtractFailed;
            task.ProgressText = StatusText.ProgressFailed;
            task.ErrorMessage = string.IsNullOrWhiteSpace(message) ? "特定解压失败" : message;
            task.IsOutputVerified = false;
            task.Outcome = TaskOutcome.Failed;
            task.EndTime = DateTime.Now;
        }

        private static long GetSourceSize(ArchiveTask task)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(task.CurrentPath) && File.Exists(task.CurrentPath)
                    ? new FileInfo(task.CurrentPath).Length
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static long GetFreeBytes(string path)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;

                return string.IsNullOrWhiteSpace(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
            }
            catch
            {
                return 0;
            }
        }

        private static string TryGetFreeBytes(string path)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;

                return string.IsNullOrWhiteSpace(root)
                    ? "未知"
                    : new DriveInfo(root).AvailableFreeSpace.ToString("N0") + " 字节";
            }
            catch
            {
                return "未知";
            }
        }

    }
}

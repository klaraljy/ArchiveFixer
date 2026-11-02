using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;

namespace ArchiveFixer.ViewModels
{
    /// <summary>一键处理 · 特定解压独立入口（功能代码独立文件，⛔ 不再塞回主文件）。</summary>
    internal sealed partial class OneClickCoordinator
    {
        /// <summary>
        /// 特定解压的一键入口：每个源包独立跑 3 层以内的简单管线，不进入轮次/第二大步。
        /// 输出根跟着每个压缩包自己的父目录走；源包与其余物按当前档处理，空间不足模式覆盖为
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
            var running = new List<(SpecificBatchUnit Unit, Task<bool> Work)>();

            int lastTarget = -1;
            string lastBasis = string.Empty;

            /*
             * 常驻调度池：完成一个就立刻补一个，不等整浪结束。
             * 每次补位都重新探空间、按当前 pending 重新算目标并发；pending 从大包变成小包时，
             * 目标会从 3 自动升到 5 / 6 / 8。
             */
            while (pending.Count > 0 || running.Count > 0)
            {
                if (_vm.IsStopping)
                {
                    // 不再启动新任务；已经在跑的收完再结束，不能留下没统计的尾巴。
                    foreach ((SpecificBatchUnit unit, Task<bool> work) in running)
                    {
                        bool stoppedSuccess = await work.ConfigureAwait(true);
                        skippedFollowers += unit.Followers.Count;

                        if (stoppedSuccess)
                        {
                            succeeded++;
                        }
                        else
                        {
                            failed++;
                        }
                    }

                    running.Clear();
                    break;
                }

                string spaceBasis = string.Empty;
                int target = spaceTight
                    ? ResolveSpecificParallel(pending, running, runOptions, out spaceBasis)
                    : 1;

                target = Math.Max(1, Math.Min(target, pending.Count + running.Count));

                if (spaceTight && (target != lastTarget || !string.Equals(spaceBasis, lastBasis, StringComparison.Ordinal)))
                {
                    lastTarget = target;
                    lastBasis = spaceBasis;
                    AppendLog(
                        "INFO",
                        $"特定解压由空间不足接管并发：当前目标 {target} 个（正在跑 {running.Count} 个，待处理 {pending.Count} 个）。{spaceBasis}。");
                }

                while (running.Count < target && pending.Count > 0)
                {
                    SpecificBatchUnit unit = pending[0];
                    pending.RemoveAt(0);
                    Task<bool> work = ProcessSpecificUnitAsync(unit, runOptions, spaceTight, runner);
                    running.Add((unit, work));
                }

                if (running.Count == 0)
                {
                    break;
                }

                Task<bool> finished = await Task.WhenAny(running.Select(item => item.Work)).ConfigureAwait(true);
                int index = running.FindIndex(item => ReferenceEquals(item.Work, finished));

                if (index < 0)
                {
                    continue;
                }

                (SpecificBatchUnit completedUnit, Task<bool> completedWork) = running[index];
                running.RemoveAt(index);

                bool success = await completedWork.ConfigureAwait(true);
                skippedFollowers += completedUnit.Followers.Count;

                if (success)
                {
                    succeeded++;
                }
                else
                {
                    failed++;
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
                // 跟班卷自己没有产物校验，不能拿“同组入口成功”替代本单证据；
                // 源包保留，由唯一删源决策在整组可证完整后再处理。
                return;
            }

            // 空间不足也不能用“用户接受风险”替代本单成功/校验证据：源包保留。
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

                // 特定解压这一支目前没有产物清单校验；源包保留，等接入统一
                // SourceCleanupDecision 后再按“可证完整”处置。

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
            IReadOnlyList<(SpecificBatchUnit Unit, Task<bool> Work)> running,
            OneClickRunOptions runOptions,
            out string basis)
        {
            if (pending == null || pending.Count == 0)
            {
                basis = "没有待跑任务";
                return Math.Max(1, running?.Count ?? 0);
            }

            long freeBytes = pending
                .Select(unit => GetFreeBytes(ResolveSpecificOutputRoot(unit.Entry, runOptions)))
                .Where(bytes => bytes > 0)
                .DefaultIfEmpty(0)
                .Min();

            // 正在跑的任务已经用掉的空间不能再许给下一批；探到的 free 是当前真实值，再扣一次预留。
            long runningBytes = running?.Sum(item => item.Unit.SourceBytes) ?? 0;
            long budget = Math.Max(0, freeBytes - runningBytes);
            List<long> sizes = pending.Select(unit => unit.SourceBytes).ToList();

            int launchable = ExtractionScheduler.ResolveSpaceTightParallelCountForDemand(
                sizes,
                budget,
                reservedBytes: 0,
                out string spaceBasis);

            int target = (running?.Count ?? 0) + launchable;

            // 当前队列仍是“大包”时，总并发也不许超过 3；队列变小后这个限制自然消失。
            long largest = sizes.Max();
            if (largest >= budget / 4)
            {
                target = Math.Min(target, ExtractionScheduler.SpaceTightLargeTaskParallelCeiling);
            }

            target = Math.Max(running?.Count ?? 0, Math.Min(ExtractionScheduler.ParallelCeiling, target));

            basis = spaceBasis + $"；正在跑 {running?.Count ?? 0} 个，预算 {TaskSpaceEstimate.FormatSize(budget)}";
            return Math.Max(1, target);
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

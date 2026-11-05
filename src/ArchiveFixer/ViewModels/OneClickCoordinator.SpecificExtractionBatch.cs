using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Extraction;
using ArchiveFixer.Engines;
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
            OneClickRunOptions? runOptions,
            RunSnapshot snapshot)
        {
            runOptions ??= snapshot.Options;

            var runner = new SpecificExtractionRunner(
                _extractionCoordinator.SpecificExtractionEngine,
                message => AppendLog("INFO", message));
            _extractionCoordinator.BeginSpecificPasswordBatch();
            // 空间不足模式：按源包体积从大到小；源包按用户接受风险处理（失败/预检不足也删）。
            bool spaceTight = snapshot.SpaceTightMode;
            // 预计耗时从大到小：长任务先占槽位，小任务随后补空；不再按包大小分档。
            IReadOnlyList<SpecificBatchUnit> orderedUnits = SpecificBatchUnitBuilder.Build(targets)
                .OrderByDescending(unit => TaskDurationEstimator.EstimateSeconds(unit.SourceBytes, unit.Entry, unit.Followers.Count + 1, unit.Followers.Count > 0))
                .ThenBy(unit => unit.Entry.CurrentPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int preparedTarget = _extractionCoordinator.PrepareAutoConcurrencyForSpecificBatch(targets);
            AppendLog(
                "INFO",
                $"特定解压：预计耗时排序完成，自动容量 {preparedTarget} 个"
                + (spaceTight ? "；空间不足另按空间上限收紧。" : "。"));

            int succeeded = 0;
            int failed = 0;
            int skippedFollowers = 0;

            string SpecificDeviceRoot(SpecificBatchUnit unit)
            {
                return Path.GetDirectoryName(unit.Entry.CurrentPath) ?? string.Empty;
            }

            Dictionary<SpecificBatchUnit, ScheduledWorkUnit> workUnits = orderedUnits
                .ToDictionary(
                    unit => unit,
                    unit => ScheduledWorkUnit.FromSpecificUnit(unit, SpecificDeviceRoot(unit)));
            var scheduler = new DevicePoolScheduler<ScheduledWorkUnit>();
            Func<string, int> maximumByDevice = root =>
                StorageDeviceProfile.Detect(root) == StorageDeviceKind.Ssd ? 8 : 4;
            scheduler.Reset(
                workUnits.Values.Select(work => (work, work.DeviceRoot)),
                _ => Math.Max(1, preparedTarget),
                maximumByDevice,
                work => work.EstimatedSeconds);
            var running = new List<(SpecificBatchUnit Unit, Task<bool> Work)>();



            int lastTarget = -1;
            string lastBasis = string.Empty;

            // 特定解压分支不经过 RunPipelineAsync 后半段，必须在这里自己接停止令牌。
            void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(MainViewModel.IsStopping) && _vm.IsStopping)
                {
                    snapshot.StopToken.RequestStop();
                }
            }

            _vm.PropertyChanged += OnViewModelPropertyChanged;
            try
            {

                /*
                 * 常驻调度池：完成一个就立刻补一个，不等整浪结束。
                 * 每次补位都重新探空间、按当前 pending 重新算目标并发；pending 从大包变成小包时，
                 * 目标会从 3 自动升到 5 / 6 / 8。
                 */
                while (scheduler.HasWork)
                {
                    if (snapshot.StopToken.IsStopRequested)
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
                    int autoTarget = _extractionCoordinator.CurrentAutoConcurrencyLimit;
                    int target = spaceTight
                        ? Math.Min(
                            ResolveSpecificParallel(
                                scheduler.PendingUnits.Select(work => work.PayloadAs<SpecificBatchUnit>()).ToList(),
                                running, runOptions, out spaceBasis),
                            Math.Max(autoTarget, running.Count))
                        : autoTarget;

                    target = Math.Max(1, Math.Min(target, scheduler.PendingCount + scheduler.ActiveCount));

                    if (target != lastTarget || (spaceTight && !string.Equals(spaceBasis, lastBasis, StringComparison.Ordinal)))
                    {
                        lastTarget = target;
                        lastBasis = spaceBasis;
                        AppendLog(
                            "INFO",
                            (spaceTight
                                ? $"特定解压并发（空间不足 + 自动）：当前目标 {target} 个"
                                : $"特定解压并发（自动）：当前目标 {target} 个")
                            + $"（正在跑 {running.Count} 个，待处理 {scheduler.PendingCount} 个）"
                            + (spaceTight ? $"；{spaceBasis}" : "。"));
                    }

                    while (scheduler.TryTakeNext(target, out ScheduledWorkUnit? scheduledWork, out string? deviceRoot))
                    {
                        SpecificBatchUnit unit = scheduledWork!.PayloadAs<SpecificBatchUnit>();
                        Task<bool> work = ProcessSpecificUnitAsync(unit, runOptions, spaceTight, runner);
                        running.Add((unit, work));
                    }
                    if (running.Count == 0)
                    {
                        break;
                    }

                    Task delay = Task.Delay(TimeSpan.FromSeconds(1));
                    Task finished = await Task.WhenAny(
                            running.Select(item => item.Work).Append(delay))
                        .ConfigureAwait(true);

                    if (ReferenceEquals(finished, delay))
                    {
                        // 长任务不结束也能重新读一次系统压力；只影响“下一单开不开”，不杀在跑的。
                        // ⛔ 不要重置 lastTarget：目标没变就不该再写日志，否则会每秒刷一行。
                        continue;
                    }

                    Task<bool> finishedWork = (Task<bool>)finished;
                    int index = running.FindIndex(item => ReferenceEquals(item.Work, finishedWork));

                    if (index < 0)
                    {
                        continue;
                    }

                    (SpecificBatchUnit completedUnit, Task<bool> completedWork) = running[index];
                    running.RemoveAt(index);
                    bool success = await completedWork.ConfigureAwait(true);
                    scheduler.MarkCompleted(workUnits[completedUnit]);
                    if (success
                        && completedUnit.Entry.StartTime.HasValue
                        && completedUnit.Entry.EndTime.HasValue)
                    {
                        double elapsed = (completedUnit.Entry.EndTime.Value - completedUnit.Entry.StartTime.Value)
                            .TotalSeconds;
                        DurationHistory.Record(
                            completedUnit.Entry,
                            completedUnit.SourceBytes,
                            elapsed,
                            completedUnit.Followers.Count + 1,
                            completedUnit.Followers.Count > 0);

                        double gigabytes = Math.Max(1L, completedUnit.SourceBytes)
                                           / (1024.0 * 1024 * 1024);
                        if (elapsed > 0 && gigabytes > 0)
                        {
                            ConcurrencyResourceSnapshot pressure = SystemResourceProbe.Snapshot();
                            scheduler.ObserveCompletion(
                                workUnits[completedUnit],
                                completedUnit.SourceBytes,
                                elapsed / gigabytes,
                                new CapacitySample(
                                    pressure.CpuBusyPercent,
                                    pressure.AvailablePhysicalBytes,
                                    false));
                            foreach (DeviceCapacityChange change in scheduler.DrainCapacityChanges())
                            {
                                AppendLog(
                                    "INFO",
                                    $"设备 {change.Root} 容量 {change.Previous} → {change.Current}："
                                    + $"{change.Reason}；窗口聚合 {change.AggregateMegabytesPerSecond:F1} MB/s，"
                                    + $"平均延迟 {change.AverageLatencySecondsPerGb:F2} s/GB");
                            }
                        }
                    }

                    foreach (ScheduledWorkUnit pendingWork in scheduler.PendingUnits)
                    {
                        scheduler.UpdatePendingEstimate(pendingWork, pendingWork.RefreshEstimatedSeconds());
                    }

                    skippedFollowers += completedUnit.Followers.Count;
                    _extractionCoordinator.ObserveSpecificCompleted(
                        completedUnit.Entry,
                        Math.Max(1, running.Count));

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
            finally
            {
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
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

            if (success)
            {
                ApplySpecificSourceHandling(unit, runOptions, spaceTight);
            }

            return success;
        }

        /// <summary>
        /// 特定解压的收尾只做“按当前档处理源包/过程物”，不进入第一/第二大步；
        /// 这里复用唯一的删源执行体，失败只记 WARN，不把内容物任务改成失败。
        /// </summary>
        private void ApplySpecificSourceHandling(
            SpecificBatchUnit unit,
            OneClickRunOptions runOptions,
            bool spaceTight)
        {
            bool deleteSource = spaceTight || runOptions.SourceHandling == SourceHandlingMode.DeleteInPlace;
            if (!deleteSource)
            {
                return;
            }

            CleanupSpecificSource(unit.Entry, includeRestAliases: true);
            foreach (ArchiveTask follower in unit.Followers)
            {
                CleanupSpecificSource(follower, includeRestAliases: false);
            }
        }

        private void CleanupSpecificSource(ArchiveTask task, bool includeRestAliases)
        {
            SourceCleanupResult cleanup = new SourceCleanupService().Cleanup(
                task,
                new SourceCleanupDecision
                {
                    Enabled = true,
                    Evidence = SourceCleanupEvidence.LayerCompleteness,
                    Note = "特定解压内容物已落地",
                    IncludeRestAliases = includeRestAliases
                });

            AppendLog(
                cleanup.FailedFiles.Count == 0 ? "INFO" : "WARN",
                $"{task.LogName}：特定解压后源包处理 —— {cleanup.Message}");

            if (cleanup.Attempted && cleanup.FailedFiles.Count == 0)
            {
                task.SourcePackageMove = SourcePackageMoveState.Done;
            }
        }

        private void ApplySpecificRestHandling(
            ArchiveTask task,
            SpecificExtractionRunResult result,
            OneClickRunOptions runOptions,
            bool spaceTight)
        {
            if (string.IsNullOrWhiteSpace(result.RestDirectory))
            {
                return;
            }

            bool deleteRest = spaceTight
                              || string.Equals(
                                  runOptions.RestHandling,
                                  RestHandlingModes.Delete,
                                  StringComparison.OrdinalIgnoreCase);
            if (!deleteRest)
            {
                return;
            }

            try
            {
                if (Directory.Exists(result.RestDirectory))
                {
                    Directory.Delete(result.RestDirectory, recursive: true);
                }

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

            /*
             * 特定解压也要让列表看得见“正在做哪一层”：runner 每层开始时回调，
             * 属性更新投递回 UI 线程；这里不伪造百分比，只把层/入口写进状态文本。
             */
            SynchronizationContext? uiContext = SynchronizationContext.Current;

            void ApplyProgress(Action action)
            {
                if (uiContext != null)
                {
                    uiContext.Post(_ => action(), null);
                }
                else
                {
                    action();
                }
            }

            task.Status = StatusText.Extracting;
            task.ProgressText = "特定解压：准备";
            task.ApplyProgress(0, null);

            // 与普通挡位共用同一个 Sink：任务进度、10% 档位日志、30 秒心跳只维护一份实现。
            IProgress<ArchiveProgress> taskProgress = _extractionCoordinator.CreateProgressSink(task);

            SpecificExtractionRunResult result;
            CancellationTokenSource specificCts = _extractionCoordinator.BeginSpecificTask(task);
            bool cancelled = false;

            try
            {
                result = await runner
                    .RunAsync(
                        source,
                        outputRoot,
                        workRoot,
                        _extractionCoordinator.SpecificExtractionPasswords,
                        cancellationToken: specificCts.Token,
                        passwordProvider: () => _extractionCoordinator.SpecificExtractionPasswords,
                        onPasswordSuccess: password =>
                            _extractionCoordinator.RecordSpecificPasswordSuccess(source, password),
                        onLayerStarted: (depth, archive) => ApplyProgress(() =>
                        {
                            task.Status = StatusText.Extracting;
                            task.ProgressText = $"特定解压：第 {depth + 1} 层";
                            task.ApplyProgress(0, $"第 {depth + 1} 层：{archive}", depth + 1);
                        }),
                        progress: taskProgress)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                result = new SpecificExtractionRunResult { Message = "用户停止当前任务" };
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
                finally
                {
                    cancelled = cancelled || specificCts.IsCancellationRequested;
                    _extractionCoordinator.EndSpecificTask(specificCts);
                }
            }

            if (cancelled)
            {
                MarkSpecificTaskCancelled(task);
                AppendLog("WARN", $"{task.LogName}：已按「{task.ErrorMessage}」停下。");
                return false;
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

                /*
                 * 特定解压只改内容物落点/命名；源包由 unit 成功后统一处理，
                 * 最后一层过程物按当前删除档处理，不进入第一/第二大步。
                 */
                ApplySpecificRestHandling(task, result, runOptions, spaceTight);

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
            ApplySpecificRestHandling(task, result, runOptions, spaceTight);
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

        private static void MarkSpecificTaskCancelled(ArchiveTask task)
        {
            task.Status = StatusText.Cancelled;
            task.ProgressText = StatusText.Cancelled;
            if (string.IsNullOrWhiteSpace(task.ErrorMessage))
            {
                task.ErrorMessage = "用户停止当前任务";
            }
            task.IsOutputVerified = false;
            task.Outcome = TaskOutcome.Cancelled;
            task.Reason = TaskReason.Cancelled;
            task.EndTime = DateTime.Now;
            task.ClearProgress();
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

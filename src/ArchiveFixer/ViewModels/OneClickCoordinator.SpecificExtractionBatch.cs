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

            _extractionCoordinator.BeginSpecificPasswordBatch();
            // 空间不足模式：按源包体积从大到小；源包按用户接受风险处理（失败/预检不足也删）。
            bool spaceTight = snapshot.SpaceTightMode;
            // 预计耗时从大到小：长任务先占槽位，小任务随后补空；不再按包大小分档。
            IReadOnlyList<SpecificBatchUnit> orderedUnits = SpecificBatchUnitBuilder.Build(targets)
                .OrderByDescending(unit => TaskDurationEstimator.EstimateSeconds(unit.SourceBytes, unit.Entry, unit.Followers.Count + 1, unit.Followers.Count > 0))
                .ThenBy(unit => unit.Entry.CurrentPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int configuredTarget = _extractionCoordinator.ResolveManualConcurrencyLimit();
            AppendLog(
                "INFO",
                $"特定解压：按预计耗时降序排好；普通并发按用户选择 {configuredTarget} 个"
                + (spaceTight ? "；空间不足模式由空间自己定，可能高于普通档。" : "。"));

            int succeeded = 0;
            int failed = 0;
            int skippedFollowers = 0;

            Dictionary<SpecificBatchUnit, ScheduledWorkUnit> workUnits = orderedUnits
                .ToDictionary(unit => unit, unit => ScheduledWorkUnit.FromSpecificUnit(unit));
            var scheduler = new SlotScheduler<ScheduledWorkUnit>();
            scheduler.Reset(
                workUnits.Values,
                Math.Max(1, configuredTarget),
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
                 * 常驻调度池：完成一个就补一个；并发数固定为用户选择的 N。
                 * LPT 只决定启动顺序，不再按吞吐/设备升降档。
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
                    int target = configuredTarget;

                    if (spaceTight)
                    {
                        // 空间不足模式是独立通道：由空间自己决定，可以高于普通手动档。
                        target = ResolveSpecificParallel(
                            scheduler.PendingUnits.Select(work => work.PayloadAs<SpecificBatchUnit>()).ToList(),
                            running,
                            runOptions,
                            out spaceBasis);
                    }

                    target = Math.Max(1, Math.Min(target, scheduler.PendingCount + scheduler.ActiveCount));
                    scheduler.SetCapacity(target);

                    if (target != lastTarget || (spaceTight && !string.Equals(spaceBasis, lastBasis, StringComparison.Ordinal)))
                    {
                        lastTarget = target;
                        lastBasis = spaceBasis;
                        AppendLog(
                            "INFO",
                            (spaceTight
                                ? $"特定解压并发（空间不足，用户选择 {configuredTarget}）：当前目标 {target} 个"
                                : $"特定解压并发（用户选择 {configuredTarget}）：当前目标 {target} 个")
                            + $"（正在跑 {running.Count} 个，待处理 {scheduler.PendingCount} 个）"
                            + (spaceTight ? $"；{spaceBasis}" : "。"));
                    }

                    while (running.Count < target && scheduler.TryTakeNext(out ScheduledWorkUnit? scheduledWork))
                    {
                        SpecificBatchUnit unit = scheduledWork!.PayloadAs<SpecificBatchUnit>();
                        Task<bool> work = ProcessSpecificUnitAsync(unit, runOptions, spaceTight);
                        running.Add((unit, work));
                    }

                    if (running.Count == 0)
                    {
                        break;
                    }

                    Task finished = await Task.WhenAny(running.Select(item => item.Work)).ConfigureAwait(true);
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
            bool spaceTight)
        {
            _extractionCoordinator.BeginTaskLogCapture(unit.Entry);
            try
            {
                bool success = await ProcessSpecificTaskAsync(
                        unit.Entry,
                        runOptions,
                        spaceTight,
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
            finally
            {
                _extractionCoordinator.EndTaskLogCapture(unit.Entry);
            }
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

            _extractionCoordinator.AppendTaskLog(
                task,
                cleanup.FailedFiles.Count == 0 ? "INFO" : "WARN",
                $"特定解压后源包处理 —— {cleanup.Message}");

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
                _extractionCoordinator.AppendTaskLog(
                    task,
                    spaceTight
                        ? "空间不足模式，最后一层过程物在成功后彻底删除"
                        : "特定解压其余物按删除档清理完毕");
            }
            catch (Exception ex)
            {
                _extractionCoordinator.AppendTaskLog(task, "WARN", $"特定解压其余物删除失败：{ex.Message}");
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
            long requiredSourceBytes = 0)
        {
            string source = task.CurrentPath;

            var runner = new SpecificExtractionRunner(
                _extractionCoordinator.SpecificExtractionEngine,
                message => _extractionCoordinator.AppendTaskLog(task, message));

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
                        {
                            _extractionCoordinator.RecordSpecificPasswordSuccess(source, password);

                            ApplyProgress(() =>
                            {
                                if (!string.IsNullOrEmpty(password))
                                {
                                    task.PasswordStatus = StatusText.PasswordCorrect;
                                }
                                else if (!string.Equals(
                                             task.PasswordStatus,
                                             StatusText.PasswordCorrect,
                                             StringComparison.Ordinal))
                                {
                                    task.PasswordStatus = StatusText.PasswordNotNeeded;
                                }
                            });
                        },
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
                _extractionCoordinator.AppendTaskLog(task, "WARN", $"已按「{task.ErrorMessage}」停下。");
                return false;
            }

            if (!string.IsNullOrEmpty(result.PasswordStatus))
            {
                task.PasswordStatus = result.PasswordStatus;
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

                _extractionCoordinator.AppendTaskLog(
                    task,
                    $"特定解压完成 → {Path.GetFileName(result.OutputPath)}（{result.Depth + 1} 层）"
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
            _extractionCoordinator.AppendTaskLog(task, "WARN", $"特定解压失败 —— {result.Message}");

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

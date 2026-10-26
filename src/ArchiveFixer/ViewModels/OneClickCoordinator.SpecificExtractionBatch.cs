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
        /// 第一版只记录内容物落地；源包/其余物删除随后接。
        /// </summary>
        private async Task<OneClickOutcome> RunSpecificBatchAsync(
            IReadOnlyList<ArchiveTask> targets,
            OneClickRunOptions? runOptions)
        {
            runOptions ??= OneClickRunOptions.FromSettings(Settings, _vm.SelectedOutputDirectory);

            var runner = new SpecificExtractionRunner(
                _extractionCoordinator.SpecificExtractionEngine,
                message => AppendLog("INFO", message));
            IReadOnlyList<string?> passwords = _extractionCoordinator.SpecificExtractionPasswords;
            bool spaceTight = _vm.SpaceTightMode;
            IReadOnlyList<ArchiveTask> orderedTargets = spaceTight
                ? targets.OrderByDescending(task => GetSourceSize(task)).ToList()
                : targets;
            int succeeded = 0;
            int failed = 0;

            foreach (ArchiveTask task in orderedTargets)
            {
                if (_vm.IsStopping)
                {
                    break;
                }

                string source = task.CurrentPath;

                if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                {
                    MarkSpecificTaskFailed(task, "源文件不存在");
                    failed++;
                    continue;
                }

                string outputRoot = runOptions.PlacementMode == OutputPlacementMode.CustomRootPerArchive
                    ? runOptions.CustomRoot
                    : Path.GetDirectoryName(source) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(outputRoot))
                {
                    MarkSpecificTaskFailed(task, "输出目录无效");
                    failed++;
                    continue;
                }

                if (spaceTight)
                {
                    long freeBytes = GetFreeBytes(outputRoot);
                    long sourceBytes = GetSourceSize(task);

                    if (freeBytes < sourceBytes)
                    {
                        DeleteSourceOnSpaceRisk(task, spaceTight);
                        MarkSpecificTaskFailed(
                            task,
                            $"空间不足：需要 {sourceBytes:N0} 字节，当前只剩 {freeBytes:N0} 字节");
                        failed++;
                        continue;
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
                        .RunAsync(source, outputRoot, workRoot, passwords)
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
                    succeeded++;
                }
                else
                {
                    task.RestDirectoryPath = result.RestDirectory;
                    DeleteSourceOnSpaceRisk(task, spaceTight);
                    MarkSpecificTaskFailed(task, result.Message);
                    failed++;
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
                }
            }

            string summary = $"特定解压完成：成功 {succeeded} / 失败 {failed}（本次 {targets.Count} 个任务）。";
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

using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 一键诊断包：把当前操作目录的六件套 + 任务事实 + 环境/设置摘要打成 zip。
    /// 用户只需要发这一个文件；密码类属性一律不写。
    /// </summary>
    public static class DiagnosticBundleService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public static string Export(
            LogService log,
            string targetZipPath,
            IReadOnlyList<ArchiveTask> tasks,
            AppSettings? settings)
        {
            if (log == null)
            {
                throw new ArgumentNullException(nameof(log));
            }

            string operationDirectory = log.CurrentOperationDirectory;

            if (string.IsNullOrWhiteSpace(operationDirectory) || !Directory.Exists(operationDirectory))
            {
                throw new InvalidOperationException("当前操作还没有日志目录，无法导出诊断包。");
            }

            string fullTarget = Path.GetFullPath(targetZipPath);
            string? parent = Path.GetDirectoryName(fullTarget);

            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (File.Exists(fullTarget))
            {
                File.Delete(fullTarget);
            }

            string operationSummaryPath = Path.Combine(operationDirectory, "summary.json");
            File.WriteAllText(
                operationSummaryPath,
                JsonSerializer.Serialize(BuildSummary(log, tasks), JsonOptions),
                new UTF8Encoding(false));

            using var archive = ZipFile.Open(fullTarget, ZipArchiveMode.Create);
            AddText(archive, "README.txt", BuildReadme(log, tasks));
            AddOperationFiles(archive, operationDirectory);
            AddText(archive, "extra/summary.json", JsonSerializer.Serialize(BuildSummary(log, tasks), JsonOptions));
            AddText(archive, "extra/tasks.json", JsonSerializer.Serialize(BuildTasks(tasks), JsonOptions));
            AddText(archive, "extra/failed-tasks.json", JsonSerializer.Serialize(BuildFailedTasks(tasks), JsonOptions));
            AddText(archive, "extra/settings.redacted.json", JsonSerializer.Serialize(BuildSettings(settings), JsonOptions));
            AddText(archive, "extra/tool-versions.json", JsonSerializer.Serialize(BuildTools(), JsonOptions));
            List<string> fatalLogs = AddFatalLogs(archive, log.LogDirectory);
            AddText(archive, "extra/error-codes.json", JsonSerializer.Serialize(
                DiagnosticCodes.All.Select(pair => new
                {
                    code = pair.Key,
                    component = pair.Value.Component,
                    meaning = pair.Value.Meaning,
                    source = pair.Value.Source,
                    first_check = pair.Value.FirstCheck
                }),
                JsonOptions));

            var bundleFiles = new List<string> { "README.txt" };

            foreach (string name in new[]
                     {
                         "manifest.json",
                         "summary.json",
                         "operation.user.log",
                         "operation.log",
                         "operation.jsonl",
                         "operation.trace.jsonl"
                     })
            {
                if (File.Exists(Path.Combine(operationDirectory, name)))
                {
                    bundleFiles.Add("operation/" + name);
                }
            }

            bundleFiles.AddRange(new[]
            {
                "extra/summary.json",
                "extra/tasks.json",
                "extra/failed-tasks.json",
                "extra/settings.redacted.json",
                "extra/tool-versions.json",
                "extra/error-codes.json"
            });

            foreach (string fatal in fatalLogs)
            {
                bundleFiles.Add("extra/fatal/" + fatal);
            }

            bundleFiles.Add("extra/bundle.json");
            bundleFiles.Sort(StringComparer.Ordinal);

            AddText(archive, "extra/bundle.json", JsonSerializer.Serialize(new
            {
                schema = "archivefixer.diagnostic.bundle.v1",
                log_schema = "archivefixer.log.v3",
                generated_at = LogService.FormatTimestamp(Helpers.Clock.Now),
                files = bundleFiles,
                fatal_logs = fatalLogs,
                fatal_note = fatalLogs.Count == 0 ? "本次没有 fatal 日志" : "fatal 日志已放入 extra/fatal/"
            }, JsonOptions));

            return fullTarget;
        }

        private static void AddOperationFiles(ZipArchive archive, string operationDirectory)
        {
            foreach (string name in new[]
                     {
                         "manifest.json",
                         "summary.json",
                         "operation.user.log",
                         "operation.log",
                         "operation.jsonl",
                         "operation.trace.jsonl"
                     })
            {
                string path = Path.Combine(operationDirectory, name);

                if (File.Exists(path))
                {
                    archive.CreateEntryFromFile(path, "operation/" + name, CompressionLevel.Optimal);
                }
            }
        }

        private static List<string> AddFatalLogs(ZipArchive archive, string logDirectory)
        {
            var names = new List<string>();

            try
            {
                foreach (string path in Directory
                             .EnumerateFiles(logDirectory, "fatal_*.log", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(path);
                    archive.CreateEntryFromFile(path, "extra/fatal/" + name, CompressionLevel.Optimal);
                    names.Add(name);
                }
            }
            catch
            {
                // 诊断包不完整好过导出失败；bundle.json 会记录 fatal_logs。
            }

            return names;
        }

        private static void AddText(ZipArchive archive, string entryName, string text)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

            using Stream stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(text);
        }

        private static string BuildReadme(LogService log, IReadOnlyList<ArchiveTask> tasks)
        {
            var builder = new StringBuilder();
            builder.AppendLine("ArchiveFixer 诊断包");
            builder.AppendLine("===================");
            builder.AppendLine();
            builder.AppendLine("运行 ID：" + log.CurrentRunId);
            builder.AppendLine("操作 ID：" + log.CurrentOperationId);
            builder.AppendLine("操作：" + log.CurrentOperationName);
            builder.AppendLine("操作目录：" + log.CurrentOperationDirectory);
            builder.AppendLine("任务数：" + (tasks?.Count ?? 0));
            builder.AppendLine();
            builder.AppendLine("operation/  本次操作的六件套（manifest/summary/日志/两份 jsonl）。");
            builder.AppendLine("extra/      summary、全部任务、失败任务、脱敏设置、工具版本、错误码表、包清单、fatal 日志。");
            builder.AppendLine("            tasks.json/failed-tasks.json 的 error_code + source_file/source_line 是定位入口；");
            builder.AppendLine("            task.end 事件带 outcome/status/reason/verification/engine/exit_code/duration。");
            builder.AppendLine();
            builder.AppendLine("排查顺序：extra/failed-tasks.json → error_code/source_file/source_line → operation/operation.trace.jsonl 的 task.end → stack。");
            builder.AppendLine("code_source=fallback 表示该码是按终态兜底推的，不是失败行实际产生的码。");
            builder.AppendLine("密码、密钥不会写入本包。");
            return builder.ToString();
        }

        private static object BuildSummary(LogService log, IReadOnlyList<ArchiveTask> tasks)
        {
            List<ArchiveTask> list = (tasks ?? Array.Empty<ArchiveTask>()).Where(t => t != null).ToList();

            // 只汇总任务上真实落下的码；没有码就是没有，⛔ 不按 Outcome 推断。
            List<string> errorCodes = list
                .Select(t => t.Diagnostic.ErrorCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            List<object> failedTasks = list
                .Where(t => t.Outcome is TaskOutcome.Failed or TaskOutcome.PartiallyCompleted or TaskOutcome.Cancelled)
                .Select(t => new
                {
                    task_id = t.TaskId,
                    file = t.DisplayFileName,
                    status = t.Status,
                    outcome = t.Outcome.ToString(),
                    reason = t.Reason.ToString(),
                    error_code = t.Diagnostic.ErrorCode,
                    code_source = t.Diagnostic.CodeSource,
                    component = t.Diagnostic.Component,
                    stage = t.Diagnostic.Stage,
                    source_file = t.Diagnostic.SourceFile,
                    source_line = t.Diagnostic.SourceLine,
                    span_id = t.Diagnostic.SpanId
                })
                .Cast<object>()
                .ToList();

            return new
            {
                schema = "archivefixer.diagnostic.summary.v1",
                run_id = log.CurrentRunId,
                operation_id = log.CurrentOperationId,
                operation_name = log.CurrentOperationName,
                build_commit = BuildInfo.Commit,
                build_configuration = BuildInfo.Configuration,
                build_time = BuildInfo.BuildTime,
                generated_at = LogService.FormatTimestamp(Helpers.Clock.Now),
                task_count = list.Count,
                succeeded = list.Count(t => t.Outcome == TaskOutcome.Succeeded),
                failed = list.Count(t => t.Outcome == TaskOutcome.Failed),
                partially_completed = list.Count(t => t.Outcome == TaskOutcome.PartiallyCompleted),
                cancelled = list.Count(t => t.Outcome == TaskOutcome.Cancelled),
                skipped = list.Count(t => t.Outcome == TaskOutcome.Skipped),
                skipped_followers = list.Count(t => t.Outcome == TaskOutcome.Succeeded && t.Status == StatusText.Skipped),
                error_codes = errorCodes,
                failed_tasks = failedTasks,
                source_roots = list
                    .Select(t => t.OriginalPath)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(Path.GetDirectoryName)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                output_roots = list
                    .Select(t => t.OutputPath)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                log_directory = log.LogDirectory,
                operation_directory = log.CurrentOperationDirectory
            };
        }

        private static object BuildTasks(IReadOnlyList<ArchiveTask> tasks)
        {
            return (tasks ?? Array.Empty<ArchiveTask>())
                .Where(t => t != null)
                .Select(t => new
                {
                    task_id = t.TaskId,
                    file = t.DisplayFileName,
                    path = t.CurrentPath,
                    original_path = t.OriginalPath,
                    outcome = t.Outcome.ToString(),
                    status = t.Status,
                    reason = t.Reason.ToString(),
                    verification = t.OutputVerification.ToString(),
                    error = t.ErrorMessage,
                    output = t.OutputPath,
                    error_code = t.Diagnostic.ErrorCode,
                    code_source = t.Diagnostic.CodeSource,
                    component = t.Diagnostic.Component,
                    stage = t.Diagnostic.Stage,
                    source_file = t.Diagnostic.SourceFile,
                    source_line = t.Diagnostic.SourceLine,
                    caller = t.Diagnostic.Caller,
                    stack = t.Diagnostic.Stack,
                    engine = t.Diagnostic.Engine,
                    exit_code = t.Diagnostic.ExitCode,
                    attempts = t.Diagnostic.Attempts,
                    span_id = t.Diagnostic.SpanId,
                    started_at = t.Diagnostic.StartedAt,
                    ended_at = t.Diagnostic.EndedAt,
                    duration_ms = t.Diagnostic.DurationMs
                })
                .ToList();
        }

        private static object BuildFailedTasks(IReadOnlyList<ArchiveTask> tasks)
        {
            return (tasks ?? Array.Empty<ArchiveTask>())
                .Where(t => t != null && t.Outcome is TaskOutcome.Failed or TaskOutcome.PartiallyCompleted or TaskOutcome.Cancelled)
                .Select(t => new
                {
                    task_id = t.TaskId,
                    file = t.DisplayFileName,
                    path = t.CurrentPath,
                    outcome = t.Outcome.ToString(),
                    status = t.Status,
                    reason = t.Reason.ToString(),
                    verification = t.OutputVerification.ToString(),
                    error = t.ErrorMessage,
                    output = t.OutputPath,
                    error_code = t.Diagnostic.ErrorCode,
                    code_source = t.Diagnostic.CodeSource,
                    component = t.Diagnostic.Component,
                    stage = t.Diagnostic.Stage,
                    source_file = t.Diagnostic.SourceFile,
                    source_line = t.Diagnostic.SourceLine,
                    caller = t.Diagnostic.Caller,
                    stack = t.Diagnostic.Stack,
                    engine = t.Diagnostic.Engine,
                    exit_code = t.Diagnostic.ExitCode,
                    attempts = t.Diagnostic.Attempts,
                    span_id = t.Diagnostic.SpanId,
                    started_at = t.Diagnostic.StartedAt,
                    ended_at = t.Diagnostic.EndedAt,
                    duration_ms = t.Diagnostic.DurationMs
                })
                .ToList();
        }

        private static object BuildSettings(AppSettings? settings)
        {
            var values = new Dictionary<string, object?>();

            if (settings != null)
            {
                foreach (PropertyInfo property in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!property.CanRead || property.GetIndexParameters().Length > 0)
                    {
                        continue;
                    }

                    if (property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("密码", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    try
                    {
                        values[property.Name] = property.GetValue(settings);
                    }
                    catch
                    {
                    }
                }
            }

            return new
            {
                schema = "archivefixer.diagnostic.settings.v1",
                note = "密码类属性已排除；路径为导出时实际值。",
                settings = values
            };
        }

        private static object BuildTools()
        {
            var seven = ToolLocator.Default.SevenZipExePath;
            var unrar = ToolLocator.Default.UnRarExePath;

            return new
            {
                schema = "archivefixer.diagnostic.tools.v1",
                app_version = typeof(DiagnosticBundleService).Assembly.GetName().Version?.ToString() ?? string.Empty,
                build_commit = BuildInfo.Commit,
                build_configuration = BuildInfo.Configuration,
                seven_zip = seven,
                seven_zip_exists = File.Exists(seven),
                seven_zip_version = ToolLocator.Default.SevenZipVersion,
                unrar = unrar,
                unrar_exists = ToolLocator.Default.UnRarExists,
                unrar_version = ToolLocator.Default.UnRarVersion,
                winrar = ToolLocator.Default.WinRarExePath ?? string.Empty,
                winrar_exists = ToolLocator.Default.WinRarExeExists,
                winrar_version = ToolLocator.Default.WinRarVersion,
                rar_version = ToolLocator.Default.RarVersion,
                tools_directory = ToolLocator.Default.BundledDirectory
            };
        }
    }
}

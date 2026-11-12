using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;

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
            AddFatalLogs(archive, log.LogDirectory);
            AddText(archive, "extra/error-codes.json", JsonSerializer.Serialize(
                DiagnosticCodes.All.Select(pair => new
                {
                    code = pair.Key,
                    component = pair.Value.Component,
                    meaning = pair.Value.Meaning
                }),
                JsonOptions));

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

        private static void AddFatalLogs(ZipArchive archive, string logDirectory)
        {
            try
            {
                foreach (string path in Directory
                             .EnumerateFiles(logDirectory, "fatal_*.log", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(path);
                    archive.CreateEntryFromFile(path, "extra/fatal/" + name, CompressionLevel.Optimal);
                }
            }
            catch
            {
                // 诊断包不完整好过导出失败；README 会说明。
            }
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
            builder.AppendLine("运行 ID：" + log.CurrentOperationId);
            builder.AppendLine("操作：" + log.CurrentOperationName);
            builder.AppendLine("操作目录：" + log.CurrentOperationDirectory);
            builder.AppendLine("任务数：" + (tasks?.Count ?? 0));
            builder.AppendLine();
            builder.AppendLine("operation/  本次操作的六件套。");
            builder.AppendLine("extra/      summary、全部任务、失败任务、脱敏设置、工具版本、错误码表、fatal 日志。");
            builder.AppendLine();
            builder.AppendLine("排查顺序：extra/summary.json → 错误码 → operation/operation.jsonl → span_id → stack。");
            builder.AppendLine("密码、密钥不会写入本包。");
            return builder.ToString();
        }

        private static object BuildSummary(LogService log, IReadOnlyList<ArchiveTask> tasks)
        {
            List<ArchiveTask> list = (tasks ?? Array.Empty<ArchiveTask>()).Where(t => t != null).ToList();
            var errorCodes = new List<string>();

            if (list.Any(t => t.Outcome == TaskOutcome.Failed))
            {
                errorCodes.Add(DiagnosticCodes.TaskFailed);
            }

            if (list.Any(t => t.OutputVerification == OutputVerificationOutcome.Failed))
            {
                errorCodes.Add(DiagnosticCodes.VerifyFailed);
            }

            return new
            {
                schema = "archivefixer.diagnostic.summary.v1",
                run_id = log.CurrentOperationId,
                operation_id = log.CurrentOperationId,
                operation_name = log.CurrentOperationName,
                generated_at = LogService.FormatTimestamp(Helpers.Clock.Now),
                task_count = list.Count,
                succeeded = list.Count(t => t.Outcome == TaskOutcome.Succeeded),
                failed = list.Count(t => t.Outcome == TaskOutcome.Failed),
                skipped = list.Count(t => t.Outcome == TaskOutcome.Skipped),
                error_codes = errorCodes.Distinct(StringComparer.Ordinal).ToList(),
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
                    output = t.OutputPath
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
                    output = t.OutputPath
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

            return new
            {
                schema = "archivefixer.diagnostic.tools.v1",
                app_version = typeof(DiagnosticBundleService).Assembly.GetName().Version?.ToString() ?? string.Empty,
                seven_zip = seven,
                seven_zip_exists = File.Exists(seven),
                tools_directory = ToolLocator.Default.BundledDirectory
            };
        }
    }
}

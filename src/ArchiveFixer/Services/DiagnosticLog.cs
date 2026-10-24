using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ArchiveFixer.Detection;
using ArchiveFixer.Models;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// **诊断日志的写入口**（日志升级 v2）：批次清单、任务快照、设置/工具、文件操作、组事实。
    /// 它只组装 JSON 交给 <see cref="LogService.Event"/>，⛔ 不参与任何业务判据、⛔ 不写盘上的文件。
    ///
    /// <para>为什么需要：用户出问题时**只发日志、原包和工具都拿不到**。所以日志必须自己回答
    /// “哪一份 / 哪一层 / 哪条判据 / 什么结论 / 动了哪个文件 / 机器环境是什么”。</para>
    /// </summary>
    public static class DiagnosticLog
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false
        };

        /// <summary>批次开始：一次性写清本批任务、文件事实、内容卡、设置与工具。</summary>
        public static void WriteBatchStart(
            LogService log,
            string batch,
            string source,
            IReadOnlyList<ArchiveTask>? tasks,
            AppSettings? settings = null,
            string tools = "")
        {
            if (log == null)
            {
                return;
            }

            try
            {
                WriteSettings(log, settings, tools);

                var payload = new
                {
                    batch,
                    source,
                    task_count = tasks?.Count ?? 0,
                    generated_at = LogService.FormatTimestamp(DateTime.Now),
                    tasks = (tasks ?? Array.Empty<ArchiveTask>()).Select(task => DescribeTask(task, includeContentCard: false)).ToList()
                };

                log.Event(
                    "batch.start",
                    $"批次开始：{batch}，任务 {payload.task_count} 个",
                    detail: JsonSerializer.Serialize(payload, JsonOptions),
                    source: source,
                    group: batch);
            }
            catch (Exception exception)
            {
                log.Exception("写批次开始诊断失败", exception);
            }
        }

        /// <summary>批次结束：任务终态 + 产物/错误 + 机器结论（不重新扫盘，只写任务自己记录的事实）。</summary>
        public static void WriteBatchEnd(
            LogService log,
            string batch,
            IReadOnlyList<ArchiveTask>? tasks,
            string summary)
        {
            if (log == null)
            {
                return;
            }

            try
            {
                var payload = new
                {
                    batch,
                    generated_at = LogService.FormatTimestamp(DateTime.Now),
                    summary,
                    task_count = tasks?.Count ?? 0,
                    tasks = (tasks ?? Array.Empty<ArchiveTask>()).Select(task => DescribeTask(task, includeContentCard: false)).ToList()
                };

                log.Event(
                    "batch.end",
                    $"批次结束：{batch}，{summary}",
                    detail: JsonSerializer.Serialize(payload, JsonOptions),
                    result: summary,
                    group: batch);
            }
            catch (Exception exception)
            {
                log.Exception("写批次结束诊断失败", exception);
            }
        }

        /// <summary>单任务快照（建组、收片、关键判据前后可各写一次）。</summary>
        public static void WriteTaskSnapshot(LogService log, string eventName, ArchiveTask? task)
        {
            if (log == null || task == null)
            {
                return;
            }

            try
            {
                log.Event(
                    eventName,
                    $"{task.DisplayFileName}：{task.Status}",
                    detail: JsonSerializer.Serialize(DescribeTask(task, includeContentCard: true), JsonOptions),
                    file: task.CurrentPath,
                    group: task.VolumeInfoText,
                    identity: task.Outcome.ToString());
            }
            catch (Exception exception)
            {
                log.Exception("写任务诊断失败", exception, task.CurrentPath);
            }
        }

        /// <summary>本次设置与工具版本（脱敏：所有名字里带 Password 的属性一律不写）。</summary>
        public static void WriteSettings(LogService log, AppSettings? settings, string tools)
        {
            if (log == null)
            {
                return;
            }

            try
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
                            // 单个属性读不出不影响其它设置。
                        }
                    }
                }

                var payload = new
                {
                    settings = values,
                    tools
                };

                log.Event(
                    "session.settings",
                    "本次设置与工具：" + (string.IsNullOrWhiteSpace(tools) ? "未提供工具状态" : tools),
                    detail: JsonSerializer.Serialize(payload, JsonOptions));
            }
            catch (Exception exception)
            {
                log.Exception("写设置诊断失败", exception);
            }
        }

        /// <summary>文件操作（移动/删除/改名/建链接）：谁、从哪到哪、多少字节、结果。</summary>
        public static void WriteFileOperation(
            LogService log,
            string action,
            string? source,
            string? target,
            long? bytes = null,
            string? identity = null,
            string? result = null,
            string? detail = null)
        {
            if (log == null)
            {
                return;
            }

            try
            {
                log.Event(
                    "file." + action,
                    $"{action}：{Path.GetFileName(source ?? target ?? string.Empty)}"
                    + (result is { Length: > 0 } ? $" ⇒ {result}" : string.Empty),
                    detail: string.IsNullOrWhiteSpace(detail)
                        ? JsonSerializer.Serialize(new { action, source, target, bytes, identity, result }, JsonOptions)
                        : detail,
                    source: source ?? string.Empty,
                    target: target ?? string.Empty,
                    action: action,
                    result: result ?? string.Empty,
                    bytes: bytes,
                    identity: identity ?? string.Empty);
            }
            catch (Exception exception)
            {
                log.Exception("写文件操作诊断失败", exception, source ?? string.Empty);
            }
        }

        /// <summary>组事实（建组/收片/链接）：把 Unit 的组级结论写进日志，不重读盘。</summary>
        public static void WriteGroup(
            LogService log,
            string eventName,
            string batchName,
            string family,
            string state,
            string verdict,
            string entry,
            IReadOnlyList<string>? pieces,
            IReadOnlyList<int>? missing,
            string? detail = null)
        {
            if (log == null)
            {
                return;
            }

            try
            {
                var payload = new
                {
                    group = batchName,
                    family,
                    state,
                    verdict,
                    entry,
                    pieces,
                    missing,
                    detail
                };

                log.Event(
                    eventName,
                    $"组「{batchName}」：{state}；{verdict}",
                    detail: JsonSerializer.Serialize(payload, JsonOptions),
                    file: entry,
                    group: batchName,
                    result: state);
            }
            catch (Exception exception)
            {
                log.Exception("写组诊断失败", exception, entry);
            }
        }

        /// <summary>把一个任务的当前事实展开成可序列化的字典；文件读不出就如实写 null，不猜。</summary>
        private static Dictionary<string, object?> DescribeTask(ArchiveTask task, bool includeContentCard)
        {
            string path = task.CurrentPath ?? string.Empty;
            var file = DescribeFile(path);

            var result = new Dictionary<string, object?>
            {
                ["name"] = task.DisplayFileName,
                ["current_path"] = path,
                ["original_path"] = task.OriginalPath,
                ["output_path"] = task.OutputPath,
                ["parent_output_directory"] = task.ParentOutputDirectory,
                ["root_source_path"] = task.RootSourcePath,
                ["parent_task_name"] = task.ParentTaskName,
                ["is_continuation"] = task.IsContinuationTask,
                ["status"] = task.Status,
                ["outcome"] = task.Outcome.ToString(),
                ["error"] = task.ErrorMessage,
                ["detected_format"] = task.DetectedFormat,
                ["extension_status"] = task.ExtensionStatus,
                ["suggested_extension"] = task.SuggestedExtension,
                ["is_archive"] = task.IsArchive,
                ["is_selected"] = task.IsSelected,
                ["is_volume_group"] = task.IsVolumeGroup,
                ["is_volume_complete"] = task.IsVolumeComplete,
                ["volume_paths"] = task.VolumePaths?.ToList(),
                ["missing_volume_names"] = task.MissingVolumeNames?.ToList(),
                ["volume_info"] = task.VolumeInfoText,
                ["embedded_offset"] = task.EmbeddedArchiveOffset,
                ["embedded_end"] = task.EmbeddedArchiveEnd,
                ["embedded_direct_read_supported"] = task.EmbeddedDirectReadSupported,
                ["file"] = file,
                ["content_card"] = includeContentCard ? DescribeContentCard(path) : null
            };

            return result;
        }

        private static Dictionary<string, object?> DescribeFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new Dictionary<string, object?> { ["exists"] = false, ["path"] = path };
            }

            try
            {
                var info = new FileInfo(path);
                string identity = string.Empty;

                if (FileIdentity.TryGetIdentity(path, out ulong volume, out ulong index))
                {
                    identity = $"{volume}:{index}";
                }

                return new Dictionary<string, object?>
                {
                    ["exists"] = true,
                    ["path"] = path,
                    ["size"] = info.Length,
                    ["last_write_utc"] = info.LastWriteTimeUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    ["identity"] = identity
                };
            }
            catch (Exception exception)
            {
                return new Dictionary<string, object?>
                {
                    ["exists"] = true,
                    ["path"] = path,
                    ["error"] = exception.GetType().Name + "：" + exception.Message
                };
            }
        }

        private static Dictionary<string, object?>? DescribeContentCard(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                FileNatureCard card = FileNature.Inspect(path);

                return new Dictionary<string, object?>
                {
                    ["format"] = card.Format.ToString(),
                    ["body_kind"] = card.BodyKind.ToString(),
                    ["membership"] = card.Membership.ToString(),
                    ["content_fail"] = card.ContentFail.ToString(),
                    ["volume_number"] = card.VolumeNumber,
                    ["volume_index_by_name"] = card.VolumeIndexByName,
                    ["first_volume_flag"] = card.FirstVolumeFlag,
                    ["declared_volume_count"] = card.DeclaredVolumeCount,
                    ["declared_total_bytes"] = card.DeclaredTotalBytes,
                    ["has_zip_end_record"] = card.HasZipEndRecord,
                    ["zip_end_record_means_split"] = card.ZipEndRecordMeansSplit,
                    ["has_zip_local_header"] = card.HasZipLocalHeader,
                    ["archive_base_name"] = card.ArchiveBaseName,
                    ["canonical_volume_name"] = card.CanonicalVolumeName
                };
            }
            catch (Exception exception)
            {
                return new Dictionary<string, object?>
                {
                    ["error"] = exception.GetType().Name + "：" + exception.Message
                };
            }
        }
    }
}

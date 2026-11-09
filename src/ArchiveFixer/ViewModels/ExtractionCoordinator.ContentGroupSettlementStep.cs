using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;

namespace ArchiveFixer.ViewModels
{
    /// <summary>内容组级源包结算：只有组最终内容 Verified 才按 DeleteInPlace 处理所有来源根。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// 组级源包处理。和旧的“逐 rootTask 看 OutputPath”不同：
        /// 组里任何一个贡献源根都只在**组最终内容已证**时删；组未证/部分/判不出 ⇒ 一个字节不动。
        /// </summary>
        internal int SettleVerifiedContentGroupSources(OneClickRunOptions? runOptions)
        {
            SourceHandlingMode handling = runOptions?.SourceHandling
                ?? AppSettings.ParseSourceHandling(Settings.SourceHandling);

            if (handling != SourceHandlingMode.DeleteInPlace)
            {
                return 0;
            }

            int deleted = 0;

            foreach (ContentGroup group in _extractionContentGraph.ContentGroups.ToList())
            {
                if (!group.FinalContentVerified)
                {
                    continue;
                }

                var verdict = new ResultCompletenessVerdict
                {
                    State = ResultCompleteness.Complete,
                    Evidence = "ContentGroup=" + group.GroupId,
                    Message = "组最终内容已证完整",
                    Blocker = string.Empty
                };

                var logEntries = new List<(string Level, string Message)>();
                var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int groupDeleted = 0;

                foreach (string rootId in group.SourceRootIds)
                {
                    ArchiveTask? task = FindSourceRootTask(rootId);

                    if (task == null
                        || string.IsNullOrWhiteSpace(task.CurrentPath)
                        || !handled.Add(task.CurrentPath))
                    {
                        continue;
                    }

                    string? failure = PurgeLayerSourcePackage(
                        task,
                        verification: null,
                        commit: null,
                        logEntries,
                        LayerPurgeTrigger.SourceHandlingDelete,
                        reclaimVerdict: verdict,
                        reclaimLabel: group.BaseName + "（组源包）");

                    if (failure == null && task.SourcePackageMove == SourcePackageMoveState.Done)
                    {
                        deleted++;
                        groupDeleted++;
                    }
                }

                foreach ((string level, string message) in logEntries)
                {
                    AppendLog(level, message);
                }

                if (groupDeleted > 0)
                {
                    AppendLog(
                        "INFO",
                        $"[内容组·源包] {group.BaseName}：组最终内容已证，按「彻底删除」处理来源根"
                        + $"（本次成功 {groupDeleted} 个；来源根 {group.SourceRootIds.Count} 个）。");
                }
            }

            return deleted;
        }

        private ArchiveTask? FindSourceRootTask(string rootId)
        {
            string wanted = NormalizeSourceRootPath(rootId);

            if (wanted.Length == 0)
            {
                return null;
            }

            foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
            {
                if (task == null)
                {
                    continue;
                }

                if (string.Equals(NormalizeSourceRootPath(task.OriginalPath), wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(NormalizeSourceRootPath(task.CurrentPath), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return task;
                }
            }

            return null;
        }

        private static string NormalizeSourceRootPath(string? path)
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
                return path.Trim();
            }
        }
    }
}


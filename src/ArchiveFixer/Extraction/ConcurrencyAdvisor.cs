using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>并发建议：只描述设备情况，不修改用户选择。</summary>
    public readonly record struct ConcurrencySuggestion(int Suggested, string Text);

    /// <summary>
    /// 按源盘/落点盘的介质类型给出并发建议（1–4 中的保守值）。
    /// 这里不做吞吐实测、不读历史并发数据，避免把“并发争抢导致的慢”误判成设备慢。
    /// </summary>
    public static class ConcurrencyAdvisor
    {
        public static ConcurrencySuggestion Suggest(
            IReadOnlyList<ArchiveTask>? tasks,
            string? outputDirectory,
            int currentSelection,
            Func<string, StorageDeviceKind>? detectDevice = null)
        {
            Func<string, StorageDeviceKind> detect = detectDevice ?? StorageDeviceProfile.Detect;
            var roots = new List<string>();

            foreach (ArchiveTask task in tasks ?? Array.Empty<ArchiveTask>())
            {
                AddRoot(roots, task?.CurrentPath);
            }

            AddRoot(roots, outputDirectory);

            if (roots.Count == 0)
            {
                int fallback = Math.Clamp(currentSelection, 1, 4);
                return new ConcurrencySuggestion(
                    fallback,
                    $"建议 {fallback}（还没有可判断的源/落点盘；当前选择 {fallback}）");
            }

            var devices = roots
                .Select(root => new { Root = root, Kind = detect(root) })
                .ToList();

            bool hasNonSsd = devices.Any(item => item.Kind != StorageDeviceKind.Ssd);
            int suggested = hasNonSsd ? 1 : 2;
            int taskCount = tasks?.Count ?? 0;

            if (taskCount > 0)
            {
                suggested = Math.Min(suggested, taskCount);
            }

            suggested = Math.Clamp(suggested, 1, 4);

            string summary = string.Join(
                "、",
                devices.Select(item => $"{item.Root} {Describe(item.Kind)}"));

            return new ConcurrencySuggestion(
                suggested,
                $"建议 {suggested}（源/落点：{summary}；当前选择 {Math.Clamp(currentSelection, 1, 4)}）");
        }

        private static void AddRoot(List<string> roots, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrWhiteSpace(root))
                {
                    return;
                }

                if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(root);
                }
            }
            catch
            {
                // 路径判不出来就不把它算进建议；调用方仍会显示“按默认”的结论。
            }
        }

        private static string Describe(StorageDeviceKind kind) => kind switch
        {
            StorageDeviceKind.Ssd => "SSD",
            StorageDeviceKind.Hdd => "HDD",
            _ => "未知（按 HDD）"
        };
    }
}

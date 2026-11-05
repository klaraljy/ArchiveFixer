using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 只用成功任务的真实解压吞吐做粗校准：按 设备 + 格式 + 是否加密 + 单卷/组 分组，
    /// 用 EMA 慢慢吸收外盘/缓存波动。它只影响排序，不影响正确性；文件读写失败时静默退回静态初值。
    /// </summary>
    public static class DurationHistory
    {
        private sealed class Entry
        {
            public double MegabytesPerSecond { get; set; }
            public int Samples { get; set; }
        }

        private static readonly object Gate = new();
        private static Dictionary<string, Entry>? _cache;
        private static string _dataRoot = string.Empty;

        public static string DataRootDirectory
        {
            get => _dataRoot;
            set
            {
                lock (Gate)
                {
                    _dataRoot = value ?? string.Empty;
                    _cache = null;
                }
            }
        }

        public static bool TryGetMegabytesPerSecond(
            ArchiveTask? task,
            bool encrypted,
            bool isVolumeGroup,
            out double megabytesPerSecond)
        {
            megabytesPerSecond = 0;

            lock (Gate)
            {
                string key = BuildKey(task, encrypted, isVolumeGroup);
                Entry? entry = Load().GetValueOrDefault(key);
                if (entry is null || entry.Samples <= 0 || !double.IsFinite(entry.MegabytesPerSecond))
                {
                    return false;
                }

                megabytesPerSecond = Math.Clamp(entry.MegabytesPerSecond, 1, 5000);
                return true;
            }
        }

        public static void Record(
            ArchiveTask? task,
            long sourceBytes,
            double elapsedSeconds,
            int volumeCount,
            bool isVolumeGroup)
        {
            if (sourceBytes <= 0 || !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
            {
                return;
            }

            double observed = sourceBytes / (1024d * 1024d) / elapsedSeconds;
            if (!double.IsFinite(observed) || observed <= 0)
            {
                return;
            }

            lock (Gate)
            {
                string key = BuildKey(task, IsEncrypted(task), isVolumeGroup || volumeCount > 1);
                Dictionary<string, Entry> data = Load();
                if (!data.TryGetValue(key, out Entry? entry))
                {
                    entry = new Entry();
                    data[key] = entry;
                }

                entry.MegabytesPerSecond = entry.Samples <= 0
                    ? observed
                    : entry.MegabytesPerSecond * 0.8 + observed * 0.2;
                entry.Samples++;
                Save(data);
            }
        }

        internal static void ResetForTests()
        {
            lock (Gate)
            {
                _cache = null;
                _dataRoot = string.Empty;
            }
        }

        internal static bool IsEncrypted(ArchiveTask? task)
        {
            return task?.PasswordStatus is not null
                   && task.PasswordStatus.Contains("加密", StringComparison.Ordinal);
        }

        private static Dictionary<string, Entry> Load()
        {
            if (_cache is not null)
            {
                return _cache;
            }

            _cache = new Dictionary<string, Entry>(StringComparer.Ordinal);
            string path = HistoryPath;
            if (path.Length == 0 || !File.Exists(path))
            {
                return _cache;
            }

            try
            {
                Dictionary<string, Entry>? loaded =
                    JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path));
                if (loaded is not null)
                {
                    _cache = loaded;
                }
            }
            catch
            {
                // 历史坏了不影响解压；下次成功任务会重写一条。
            }

            return _cache;
        }

        private static void Save(Dictionary<string, Entry> data)
        {
            string path = HistoryPath;
            if (path.Length == 0)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(data));
            }
            catch
            {
                // 持久化失败只损失下一次校准，不影响当前任务。
            }
        }

        private static string HistoryPath =>
            string.IsNullOrWhiteSpace(_dataRoot)
                ? string.Empty
                : Path.Combine(_dataRoot, "duration-history.json");

        private static string BuildKey(ArchiveTask? task, bool encrypted, bool isVolumeGroup)
        {
            string device = "unknown";
            try
            {
                string? path = task?.CurrentPath;
                string? directory = string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    device = StorageDeviceProfile.Detect(directory).ToString();
                }
            }
            catch
            {
            }

            string format = (task?.DetectedFormat ?? "unknown").Trim().ToUpperInvariant();
            if (format.Length == 0)
            {
                format = "UNKNOWN";
            }

            return $"{device}|{format}|{(encrypted ? "enc" : "plain")}|{(isVolumeGroup ? "group" : "single")}";
        }
    }
}

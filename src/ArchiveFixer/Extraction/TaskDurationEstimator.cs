using System;
using System.IO;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 粗略预计耗时：只用于排“先做哪个”，不参与正确性判断。
    ///
    /// 关键点（用户 2026-11-04）：预计耗时不是按压缩包大小分档；
    /// 大小只是输入之一，格式/加密/设备吞吐都会修正。允许误差，运行中由真实吞吐更新。
    /// </summary>
    public static class TaskDurationEstimator
    {
        private const double DefaultMegabytesPerSecond = 25;

        public static double EstimateSeconds(ArchiveTask task)
        {
            if (task == null)
            {
                return 1;
            }

            long bytes = Math.Max(0, task.SourceSizeBytes);
            double megabytes = bytes / (1024d * 1024d);

            if (megabytes <= 0)
            {
                return 1;
            }

            double throughput = EstimateThroughputMegabytesPerSecond(task);
            double seconds = megabytes / Math.Max(1, throughput);

            // 打开包、列目录、密码候选、收尾都有固定开销；小包不能估成 0 秒。
            return Math.Max(1, seconds + 1.5);
        }

        public static string Describe(ArchiveTask task)
        {
            double seconds = EstimateSeconds(task);
            return seconds >= 60
                ? $"{seconds / 60:F1} 分钟"
                : $"{seconds:F1} 秒";
        }

        private static double EstimateThroughputMegabytesPerSecond(ArchiveTask task)
        {
            double throughput = DefaultMegabytesPerSecond;

            string path = !string.IsNullOrWhiteSpace(task.CurrentPath)
                ? task.CurrentPath
                : task.OutputPath;

            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    StorageDeviceKind device = StorageDeviceProfile.Detect(directory);
                    if (device == StorageDeviceKind.Ssd)
                    {
                        throughput = 80;
                    }
                    else if (device == StorageDeviceKind.Hdd)
                    {
                        throughput = 20;
                    }
                }
            }
            catch
            {
                // 估计失败就用默认值；调度不能因为估算抛异常。
            }

            string format = (task.DetectedFormat ?? string.Empty).ToUpperInvariant();
            if (format.Contains("RAR", StringComparison.Ordinal))
            {
                throughput *= 0.85;
            }
            else if (format.Contains("ZIP", StringComparison.Ordinal))
            {
                throughput *= 0.95;
            }

            if (task.PasswordStatus is not null
                && task.PasswordStatus.Contains("加密", StringComparison.Ordinal))
            {
                throughput *= 0.75;
            }

            return Math.Max(1, throughput);
        }
    }
}

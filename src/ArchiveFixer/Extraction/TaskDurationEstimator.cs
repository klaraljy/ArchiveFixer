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
            return task == null
                ? 1
                : EstimateSeconds(task.SourceSizeBytes, task, task.VolumeCount);
        }

        /// <summary>
        /// 按**一个调度单位**的总字节估算：单一完整包用自身大小；完整分卷组用组内各卷求和。
        /// 不能用组里第一卷的大小代替整组。
        ///
        /// <paramref name="volumeCount"/> 只加少量组内开销（每多一卷多一次打开/定位），
        /// 主体仍是总字节 / 吞吐；它不是按包大小分档。
        /// </summary>
        public static double EstimateSeconds(long sourceBytes, ArchiveTask? context, int volumeCount = 1)
        {
            double megabytes = Math.Max(0, sourceBytes) / (1024d * 1024d);

            if (megabytes <= 0)
            {
                return 1;
            }

            double throughput = EstimateThroughputMegabytesPerSecond(context);
            double seconds = megabytes / Math.Max(1, throughput);
            double volumeOverhead = 0.2 * Math.Max(0, volumeCount - 1);

            // 打开包、列目录、密码候选、收尾都有固定开销；小包不能估成 0 秒。
            return Math.Max(1, seconds + 1.5 + volumeOverhead);
        }

        public static string Describe(ArchiveTask task)
        {
            double seconds = EstimateSeconds(task);
            return seconds >= 60
                ? $"{seconds / 60:F1} 分钟"
                : $"{seconds:F1} 秒";
        }

        private static double EstimateThroughputMegabytesPerSecond(ArchiveTask? task)
        {
            double throughput = DefaultMegabytesPerSecond;

            if (task is null)
            {
                return throughput;
            }

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

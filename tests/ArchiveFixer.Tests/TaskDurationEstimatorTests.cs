using System;
using System.IO;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class TaskDurationEstimatorTests
    {
        [Fact]
        public void 成功样本会校准预计吞吐()
        {
            string root = Path.Combine(Path.GetTempPath(), "af-duration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var task = new ArchiveTask(@"H:\samples\history.7z", 9) { SourceSizeBytes = 100L * 1024 * 1024 };

            DurationHistory.DataRootDirectory = root;
            try
            {
                double before = TaskDurationEstimator.EstimateSeconds(task);
                DurationHistory.Record(task, task.SourceSizeBytes, elapsedSeconds: 2, volumeCount: 1, isVolumeGroup: false);
                double after = TaskDurationEstimator.EstimateSeconds(task);

                Assert.True(after < before, $"历史吞吐应让 100 MiB 的估计变快：{before:F1}s → {after:F1}s");
            }
            finally
            {
                DurationHistory.ResetForTests();
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void 分卷组按整组字节估计_不是只算第一卷()
        {
            var entry = new ArchiveTask(@"H:\samples\group.7z.001", 3) { SourceSizeBytes = 1L * 1024 * 1024 * 1024 };
            double single = TaskDurationEstimator.EstimateSeconds(entry.SourceSizeBytes, entry);
            double group = TaskDurationEstimator.EstimateSeconds(3L * 1024 * 1024 * 1024, entry, volumeCount: 3, isVolumeGroup: true);

            Assert.True(group > single);
        }

        [Fact]
        public void 只作为排序用的粗略预计_大文件预计耗时更长()
        {
            var small = new ArchiveTask(@"H:\samples\small.7z", 1) { SourceSizeBytes = 100L * 1024 * 1024 };
            var large = new ArchiveTask(@"H:\samples\large.7z", 2) { SourceSizeBytes = 2L * 1024 * 1024 * 1024 };

            Assert.True(
                TaskDurationEstimator.EstimateSeconds(large) > TaskDurationEstimator.EstimateSeconds(small));
            Assert.True(TaskDurationEstimator.EstimateSeconds(small) >= 1);
        }
    }
}

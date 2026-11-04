using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class TaskDurationEstimatorTests
    {
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

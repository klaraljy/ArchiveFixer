using System;
using System.Reflection;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class ProgressEtaTests
    {
        [Fact]
        public void 进度够久且有百分比时_给出剩余时间且不伪造首帧()
        {
            var task = new ArchiveTask(@"C:\samples\a.7z") { Status = StatusText.Extracting };

            // 首帧样本太少：不显示 00:00。
            task.ApplyProgress(50, "a.bin");
            Assert.Equal(string.Empty, task.EstimatedRemainingText);

            // 模拟已经跑了 10 秒，再报 50% ⇒ 预计还剩约 10 秒。
            typeof(ArchiveTask)
                .GetField("_progressEtaStartUtc", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(task, DateTime.UtcNow.AddSeconds(-10));

            task.ApplyProgress(50, "a.bin");

            Assert.False(string.IsNullOrWhiteSpace(task.EstimatedRemainingText));
            Assert.Contains("剩余", task.ProgressDetail, StringComparison.Ordinal);
        }

        [Fact]
        public void 收尾清理进度时_剩余时间一起清掉()
        {
            var task = new ArchiveTask(@"C:\samples\a.7z") { Status = StatusText.Extracting };
            typeof(ArchiveTask)
                .GetField("_progressEtaStartUtc", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(task, DateTime.UtcNow.AddSeconds(-10));
            task.ApplyProgress(50, "a.bin");

            task.ClearProgress();

            Assert.Equal(string.Empty, task.EstimatedRemainingText);
            Assert.Equal(ArchiveTask.NoProgress, task.ProgressPercent);
            Assert.DoesNotContain("剩余", task.ProgressDetail, StringComparison.Ordinal);
        }
    }
}

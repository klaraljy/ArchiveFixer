using System;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class ConcurrencyAdvisorTests
    {
        [Fact]
        public void HDD或未知盘建议1()
        {
            var tasks = new[]
            {
                new ArchiveTask(@"H:\samples\a.7z", 1) { SourceSizeBytes = 1024 },
                new ArchiveTask(@"H:\samples\b.7z", 2) { SourceSizeBytes = 1024 }
            };

            ConcurrencySuggestion suggestion = ConcurrencyAdvisor.Suggest(
                tasks,
                @"H:\out",
                currentSelection: 4,
                detectDevice: _ => StorageDeviceKind.Hdd);

            Assert.Equal(1, suggestion.Suggested);
            Assert.Contains("建议 1", suggestion.Text, StringComparison.Ordinal);
            Assert.Contains("当前选择 4", suggestion.Text, StringComparison.Ordinal);
        }

        [Fact]
        public void 全是SSD时建议2_但不超过任务数()
        {
            var tasks = new[]
            {
                new ArchiveTask(@"C:\samples\a.7z", 1) { SourceSizeBytes = 1024 },
                new ArchiveTask(@"C:\samples\b.7z", 2) { SourceSizeBytes = 1024 }
            };

            ConcurrencySuggestion two = ConcurrencyAdvisor.Suggest(
                tasks,
                @"C:\out",
                currentSelection: 4,
                detectDevice: _ => StorageDeviceKind.Ssd);
            Assert.Equal(2, two.Suggested);

            ConcurrencySuggestion one = ConcurrencyAdvisor.Suggest(
                new[] { tasks[0] },
                @"C:\out",
                currentSelection: 4,
                detectDevice: _ => StorageDeviceKind.Ssd);
            Assert.Equal(1, one.Suggested);
        }

        [Fact]
        public void 混合盘按最保守的1()
        {
            var tasks = new[]
            {
                new ArchiveTask(@"H:\samples\a.7z", 1) { SourceSizeBytes = 1024 },
                new ArchiveTask(@"C:\samples\b.7z", 2) { SourceSizeBytes = 1024 }
            };

            ConcurrencySuggestion suggestion = ConcurrencyAdvisor.Suggest(
                tasks,
                @"C:\out",
                currentSelection: 3,
                detectDevice: root => root.StartsWith(@"H:\", StringComparison.OrdinalIgnoreCase)
                    ? StorageDeviceKind.Hdd
                    : StorageDeviceKind.Ssd);

            Assert.Equal(1, suggestion.Suggested);
            Assert.Contains("当前选择 3", suggestion.Text, StringComparison.Ordinal);
        }

        [Fact]
        public void 没有任务时只给当前选择的提示()
        {
            ConcurrencySuggestion suggestion = ConcurrencyAdvisor.Suggest(
                Array.Empty<ArchiveTask>(),
                outputDirectory: null,
                currentSelection: 2,
                detectDevice: _ => StorageDeviceKind.Hdd);

            Assert.Equal(2, suggestion.Suggested);
            Assert.Contains("还没有可判断", suggestion.Text, StringComparison.Ordinal);
        }
    }
}

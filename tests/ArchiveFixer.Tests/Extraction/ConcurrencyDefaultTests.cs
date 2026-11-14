using System;
using System.IO;
using System.Linq;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 并发默认值与手动档位（用户 2026-11-05 拍板：自动并发冻结，恢复用户选择 1–4，默认 2）。
    /// </summary>
    public class ConcurrencyDefaultTests : IDisposable
    {
        private readonly string _root;

        public ConcurrencyDefaultTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerConcurrencyDefault", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        [Fact]
        public void 并发默认值是2_夹取区间是1到4()
        {
            Assert.Equal(2, AppSettings.DefaultMaxParallelExtractCount);
            Assert.Equal(2, new AppSettings().MaxParallelExtractCount);
            Assert.Equal(2, AppSettings.CreateDefault().MaxParallelExtractCount);

            var settings = new AppSettings { MaxParallelExtractCount = 0 };
            settings.Normalize();
            Assert.Equal(1, settings.MaxParallelExtractCount);

            settings.MaxParallelExtractCount = 99;
            settings.Normalize();
            Assert.Equal(4, settings.MaxParallelExtractCount);

            settings.MaxParallelExtractCount = 3;
            settings.Normalize();
            Assert.Equal(3, settings.MaxParallelExtractCount);
        }

        [Fact]
        public void 允许的并发档位只有1到4()
        {
            Assert.Equal(new[] { 1, 2, 3, 4 }, ExtractionScheduler.AllowedParallelCounts.ToArray());
            Assert.Equal(8, ExtractionScheduler.ParallelCeiling);
            Assert.Equal(8, ConcurrencyLimit.Maximum);
            Assert.Equal(8, ConcurrencyLimit.Create(99, 0).Effective);
        }

        [Fact]
        public void 并发默认值_落盘后重启仍然是2()
        {
            var pathService = new PathService { DataRootDirectory = _root };
            new SettingsService(pathService).Save(AppSettings.CreateDefault());

            AppSettings reloaded = new SettingsService(new PathService { DataRootDirectory = _root }).Load();

            Assert.Equal(2, reloaded.MaxParallelExtractCount);
        }

        [Fact]
        public void 处理器建议_说得出核数并且不编数字()
        {
            string advice = ProcessorTopology.DescribeConcurrencyAdvice();

            Assert.Contains("建议不超过物理核心数", advice, StringComparison.Ordinal);

            if (ProcessorTopology.PhysicalCoreCount > 0)
            {
                Assert.Contains($"{ProcessorTopology.PhysicalCoreCount} 核", advice, StringComparison.Ordinal);
                Assert.True(ProcessorTopology.PhysicalCoreCount <= ProcessorTopology.LogicalCoreCount);
            }
            else
            {
                Assert.Contains("按逻辑核", advice, StringComparison.Ordinal);
                Assert.Contains($"{ProcessorTopology.LogicalCoreCount} 核", advice, StringComparison.Ordinal);
            }

            Assert.Equal(advice, ProcessorTopology.ConcurrencyAdvice);
        }

        [Fact]
        public void 解压方式页恢复并发与空间卡片_只剩1到4()
        {
            string path = Path.Combine(
                XamlBindingScan.RepositoryRoot,
                "src", "ArchiveFixer",
                "Views",
                "Tabs",
                "ExtractionTab.xaml");

            Assert.True(File.Exists(path), $"读不到解压方式页 XAML：{path}");

            string xaml = File.ReadAllText(path);

            Assert.Contains("并发与空间", xaml, StringComparison.Ordinal);
            Assert.Contains("MaxParallelExtractCount", xaml, StringComparison.Ordinal);
            Assert.Contains("按设备算建议", xaml, StringComparison.Ordinal);
            Assert.Contains("RefreshParallelAdviceCommand", xaml, StringComparison.Ordinal);

            Assert.DoesNotContain("全速", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("RunAtFullSpeed", xaml, StringComparison.Ordinal);
        }
    }
}

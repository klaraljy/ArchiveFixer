using System;
using System.IO;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 并发默认值与"处理器拓扑建议"（用户 2026-09-22 追加，依据**并行实测**）。
    ///
    /// <para>实测结论（真样本、三档、逐字节校验）：并发 1→4 吞吐 **3.08×**（保守 2.70×），
    /// **4→8 只再快 1.9%**，而单包耗时 6.63→10.91 s（+108%）、整机 CPU 均值 48.7%→72.7%；
    /// 根因是 **6 物理核超订**（第 1 波 8 个并发单包 11.9–14.2 s，降到 4 个立刻回到 5.9–6.8 s）。
    /// 所以默认值从 1 提到 **4**。</para>
    ///
    /// <para>两条硬约束一起钉住：① <c>Normalize</c> 的 1..8 夹取**不动**（上限仍是 8）；
    /// ② 界面那句"建议不超过物理核心数"必须**说得出核数**、拿不到物理核时**如实说逻辑核**。</para>
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
        public void 并发默认值是4_夹取区间仍然是1到8()
        {
            Assert.Equal(4, AppSettings.DefaultMaxParallelExtractCount);
            Assert.Equal(4, new AppSettings().MaxParallelExtractCount);
            Assert.Equal(4, AppSettings.CreateDefault().MaxParallelExtractCount);

            // 夹取口径一点没动（默认值改了，上限还是 8、下限还是 1）。
            var settings = new AppSettings { MaxParallelExtractCount = 0 };
            settings.Normalize();
            Assert.Equal(1, settings.MaxParallelExtractCount);

            settings.MaxParallelExtractCount = 99;
            settings.Normalize();
            Assert.Equal(8, settings.MaxParallelExtractCount);

            settings.MaxParallelExtractCount = 6;
            settings.Normalize();
            Assert.Equal(6, settings.MaxParallelExtractCount);
        }

        [Fact]
        public void 并发默认值_落盘后重启仍然是4()
        {
            var pathService = new PathService { DataRootDirectory = _root };
            new SettingsService(pathService).Save(AppSettings.CreateDefault());

            AppSettings reloaded = new SettingsService(new PathService { DataRootDirectory = _root }).Load();

            Assert.Equal(4, reloaded.MaxParallelExtractCount);
        }

        [Fact]
        public void 处理器建议_说得出核数并且不编数字()
        {
            string advice = ProcessorTopology.DescribeConcurrencyAdvice();

            Assert.Contains("建议不超过物理核心数", advice, StringComparison.Ordinal);

            if (ProcessorTopology.PhysicalCoreCount > 0)
            {
                Assert.Contains($"{ProcessorTopology.PhysicalCoreCount} 核", advice, StringComparison.Ordinal);

                // 物理核不可能多于逻辑处理器（开超线程时是它的一半）。
                Assert.True(ProcessorTopology.PhysicalCoreCount <= ProcessorTopology.LogicalCoreCount);
            }
            else
            {
                Assert.Contains("按逻辑核", advice, StringComparison.Ordinal);
                Assert.Contains($"{ProcessorTopology.LogicalCoreCount} 核", advice, StringComparison.Ordinal);
            }

            // 设置界面 XAML 用 x:Static 绑的就是这个属性：界面那一句与日志那一句不可能分叉。
            Assert.Equal(advice, ProcessorTopology.ConcurrencyAdvice);
        }

        [Fact]
        public void 设置界面_并发那一项旁边写着核数建议与新的默认值()
        {
            /*
             * 2026-09-24 第 11 条之后，并发档从主界面搬到了②解压方式页的「并发与空间」分组
             * （原来是主界面一个下拉 + 设置窗口一个数字框，两处并存）。
             */
            string path = Path.Combine(
                XamlBindingScan.RepositoryRoot,
                "ArchiveFixer",
                "Views",
                "Tabs",
                "ExtractionTab.xaml");

            Assert.True(File.Exists(path), $"读不到解压方式页 XAML：{path}");

            string xaml = File.ReadAllText(path);

            Assert.Contains("ProcessorTopology.ConcurrencyAdvice", xaml, StringComparison.Ordinal);
            Assert.Contains("默认 4", xaml, StringComparison.Ordinal);

            // 旧说法"默认 1（串行）更稳定"必须已经不在了 —— 默认值改了之后它就是错的。
            Assert.DoesNotContain("默认 1（串行）更稳定", xaml, StringComparison.Ordinal);
        }
    }
}

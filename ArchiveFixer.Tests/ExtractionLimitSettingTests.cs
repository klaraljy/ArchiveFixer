using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 解压前的四条安全上限（AGENTS.md §6 第 8 条；用户 2026-09-25 第 36 条）。
    ///
    /// <para><b>为什么要专门钉这一组</b>：他真机跑一个 12.22 GiB 的包时，失败清单上写着
    /// "单个文件解压后 5242880000 字节（4.88 GiB）超过单文件上限 4294967296 字节（4 GiB）：
    /// Code Complete-BZ.7z(删掉.001" —— 那个 4 GiB 是**硬编码**的默认值，界面上一个字都没有，
    /// 于是他看到的像"这个包坏了"的判决，实际上拦住他的是程序自己的安全阀。</para>
    ///
    /// <para>这一组钉四件事：①默认值必须大到真实资源包碰不到；②四条都能从设置换算过来；
    /// ③超范围的值会被夹回（不会变成"什么都不许解"）；④**同一个清单**在旧默认档下会被拒、
    /// 在新默认档下放行（撤掉修复立刻变红）。</para>
    /// </summary>
    public class ExtractionLimitSettingTests : IDisposable
    {
        private const long Gib = 1024L * 1024 * 1024;

        /// <summary>他真机那个包里最大的条目（5,242,880,000 字节 = 5000 × 1 MiB）。</summary>
        private const long HisLargestEntryBytes = 5_242_880_000L;

        private readonly string _dataRoot;

        public ExtractionLimitSettingTests()
        {
            _dataRoot = Path.Combine(Path.GetTempPath(), "ArchiveFixerLimits", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dataRoot);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dataRoot))
                {
                    Directory.Delete(_dataRoot, recursive: true);
                }
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        private PathService CreatePathService() => new() { DataRootDirectory = _dataRoot };

        // ================================================================ ① 默认值

        [Fact]
        public void 默认上限_单文件六十四吉_总大小五百一十二吉_文件数二十万_展开比一千倍()
        {
            foreach (AppSettings settings in new[] { new AppSettings(), AppSettings.CreateDefault() })
            {
                Assert.Equal(64, settings.MaxSingleExtractedFileGiB);
                Assert.Equal(512, settings.MaxExtractedTotalGiB);
                Assert.Equal(200_000, settings.MaxExtractedFileCount);
                Assert.Equal(1000d, settings.MaxExtractionRatio);
            }

            // 预算层的默认值必须与设置层的默认值**同一份**（数字只写一次，不许两处各写一份）。
            Assert.Equal(64 * Gib, ResourceBudgetOptions.Default.MaxSingleFileSize);
            Assert.Equal(512 * Gib, ResourceBudgetOptions.Default.MaxTotalSize);
            Assert.Equal(200_000, ResourceBudgetOptions.Default.MaxFileCount);
            Assert.Equal(1000d, ResourceBudgetOptions.Default.MaxExpansionRatio);
        }

        [Fact]
        public void 设置换成预算上限_四条都换过来了()
        {
            var settings = new AppSettings
            {
                MaxSingleExtractedFileGiB = 3,
                MaxExtractedTotalGiB = 9,
                MaxExtractedFileCount = 12_345,
                MaxExtractionRatio = 7.5d
            };

            ResourceBudgetOptions options = ResourceBudgetOptions.FromSettings(settings);

            Assert.Equal(3 * Gib, options.MaxSingleFileSize);
            Assert.Equal(9 * Gib, options.MaxTotalSize);
            Assert.Equal(12_345, options.MaxFileCount);
            Assert.Equal(7.5d, options.MaxExpansionRatio);

            // 保留那一项不属于用户设置（与盘空间有关，永远 512 MiB）。
            Assert.Equal(512L * 1024 * 1024, options.MinFreeSpaceReserveBytes);
        }

        [Fact]
        public void 没归一化过的设置也不会把上限变成零()
        {
            // 直接 new 出来的设置对象没走过 Normalize（测试与内部调用都会这样），
            // 0 / 负数 / NaN 必须回落到默认值 —— 否则"什么都不许解"会变成默认行为。
            var broken = new AppSettings
            {
                MaxSingleExtractedFileGiB = 0,
                MaxExtractedTotalGiB = -5,
                MaxExtractedFileCount = 0,
                MaxExtractionRatio = double.NaN
            };

            ResourceBudgetOptions options = ResourceBudgetOptions.FromSettings(broken);

            Assert.Equal(64 * Gib, options.MaxSingleFileSize);
            Assert.Equal(512 * Gib, options.MaxTotalSize);
            Assert.Equal(200_000, options.MaxFileCount);
            Assert.Equal(1000d, options.MaxExpansionRatio);
        }

        [Fact]
        public void 超范围的值会被夹回合法区间()
        {
            var settings = new AppSettings
            {
                MaxSingleExtractedFileGiB = 8192,   // 上限 4096
                MaxExtractedTotalGiB = 1,           // 比单文件上限还小 → 抬到单文件上限
                MaxExtractedFileCount = -1,         // 没配 → 默认
                MaxExtractionRatio = 0.5d            // <1 没意义 → 默认
            };

            settings.Normalize();

            Assert.Equal(AppSettings.MaxExtractionCapGiB, settings.MaxSingleExtractedFileGiB);
            Assert.Equal(AppSettings.MaxExtractionCapGiB, settings.MaxExtractedTotalGiB);
            Assert.Equal(AppSettings.DefaultMaxExtractedFileCount, settings.MaxExtractedFileCount);
            Assert.Equal(AppSettings.DefaultMaxExtractionRatio, settings.MaxExtractionRatio);
        }

        [Fact]
        public void 上限能落盘且重启后还在()
        {
            var settings = AppSettings.CreateDefault();
            settings.MaxSingleExtractedFileGiB = 128;
            settings.MaxExtractedTotalGiB = 256;
            settings.MaxExtractedFileCount = 500_000;
            settings.MaxExtractionRatio = 250d;

            var service = new SettingsService(CreatePathService());
            service.Save(settings);

            AppSettings reloaded = service.Load();

            Assert.Equal(128, reloaded.MaxSingleExtractedFileGiB);
            Assert.Equal(256, reloaded.MaxExtractedTotalGiB);
            Assert.Equal(500_000, reloaded.MaxExtractedFileCount);
            Assert.Equal(250d, reloaded.MaxExtractionRatio);
        }

        [Fact]
        public void 老配置文件里没有这四项_读进来用默认值()
        {
            // 他机器上现有的 appsettings.json 就是这种（这四条还没写进去过）。
            const string json = """
            {
              "RecursiveScan": true,
              "ScanMode": "ScanAllFiles"
            }
            """;

            PathService pathService = CreatePathService();
            File.WriteAllText(pathService.SettingsFilePath, json, new UTF8Encoding(false));

            AppSettings loaded = new SettingsService(pathService).Load();
            ResourceBudgetOptions options = ResourceBudgetOptions.FromSettings(loaded);

            Assert.Equal(64 * Gib, options.MaxSingleFileSize);
            Assert.Equal(512 * Gib, options.MaxTotalSize);
        }

        // ================================================================ ② 真的放行 / 真的拦下

        /// <summary>
        /// **这一条就是第 36 条的现场**：他那个包里最大的条目是 5,242,880,000 字节。
        /// 旧的硬编码默认（单文件 4 GiB）会把它判成"资源预算未通过"；现在默认档必须放行。
        /// 撤掉修复（把默认值改回 4 GiB）立刻变红。
        /// </summary>
        [Fact]
        public void 默认档下_他那个五吉的条目不再被拒()
        {
            ResourceBudgetOptions options = ResourceBudgetOptions.FromSettings(AppSettings.CreateDefault());

            BudgetCheckResult result = new ResourceBudget(options).CheckBeforeExtract(
                ListingWithLargestEntry(HisLargestEntryBytes, "Code Complete-BZ.7z(删掉.001"),
                archiveSizeBytes: 13_123_131_732L,
                targetDirectory: _dataRoot);

            Assert.True(
                result.Allowed,
                $"默认档必须放行 4.88 GiB 的条目（这就是他真机被拦下的那个包）；实际结论：{result.Reason}");
        }

        [Fact]
        public void 把单文件上限调小_同一个清单当场被拒且说清是程序上限()
        {
            var settings = AppSettings.CreateDefault();
            settings.MaxSingleExtractedFileGiB = 1;

            BudgetCheckResult result = new ResourceBudget(ResourceBudgetOptions.FromSettings(settings))
                .CheckBeforeExtract(
                    ListingWithLargestEntry(HisLargestEntryBytes, "Code Complete-BZ.7z(删掉.001"),
                    archiveSizeBytes: 13_123_131_732L,
                    targetDirectory: _dataRoot);

            Assert.False(result.Allowed, "调小上限之后必须真的拦住（否则这一格就是个摆设）");

            // 空间不足那一档不许混进来：两者的处置方式完全不同。
            Assert.False(result.IsSpaceShortage);

            // 文案三件事缺一不可（他看的就是这一行）：
            // ① 哪个条目（用「」括起来 —— 归档里的名字可能带不配对的括号，不括起来像被截断了）；
            // ② 这是程序的上限，不是这个包坏了；③ 去哪儿调。
            Assert.Contains("条目「Code Complete-BZ.7z(删掉.001」", result.Reason, StringComparison.Ordinal);
            Assert.Contains("安全上限", result.Reason, StringComparison.Ordinal);
            Assert.Contains("设置", result.Reason, StringComparison.Ordinal);
            Assert.Contains("1 GiB", result.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void 四条上限各自的拒绝文案都带着安全上限提示()
        {
            var budget = new ResourceBudget(new ResourceBudgetOptions
            {
                MaxSingleFileSize = 10,
                MaxTotalSize = 20,
                MaxFileCount = 1,
                MaxExpansionRatio = 2d
            });

            // 单文件
            Assert.Contains(
                StatusText.SecurityCapHint,
                budget.CheckBeforeExtract(ListingWithLargestEntry(11, "a.bin"), 100, _dataRoot).Reason,
                StringComparison.Ordinal);

            // 文件数（三个 1 字节条目 > 上限 1）
            Assert.Contains(
                StatusText.SecurityCapHint,
                budget.CheckBeforeExtract(ListingWithEntries(new[] { 1L, 1L, 1L }), 100, _dataRoot).Reason,
                StringComparison.Ordinal);

            // 总大小（30 字节 > 上限 20）
            BudgetCheckResult totalBudget = new ResourceBudget(new ResourceBudgetOptions
            {
                MaxSingleFileSize = 1000,
                MaxTotalSize = 20,
                MaxFileCount = int.MaxValue,
                MaxExpansionRatio = double.MaxValue
            }).CheckBeforeExtract(ListingWithEntries(new[] { 15L, 15L }), 100, _dataRoot);

            Assert.Contains(StatusText.SecurityCapHint, totalBudget.Reason, StringComparison.Ordinal);

            // 展开比（100 字节解成 900 字节 > 2 倍）
            BudgetCheckResult ratioBudget = new ResourceBudget(new ResourceBudgetOptions
            {
                MaxSingleFileSize = 100_000,
                MaxTotalSize = 100_000,
                MaxFileCount = int.MaxValue,
                MaxExpansionRatio = 2d
            }).CheckBeforeExtract(ListingWithEntries(new[] { 900L }), 100, _dataRoot);

            Assert.Contains(StatusText.SecurityCapHint, ratioBudget.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void 空间不足那一档_不许带安全上限提示()
        {
            /*
             * 盘不够是"清盘 / 换盘"，与"调上限"是两件事 —— 混在一起会让用户去改错的东西。
             * 造法与 SecurityGuardTests 里那条一样：四条上限全部放开，只让"空间"这一条命中
             * （估算值 = 当前可用 + 1 GiB —— 留足余量，免得别的进程刚释放几字节就把结论翻过来）。现在要 1 GiB 的余量才会翻盘（比"可用 + 1 字节"稳得多），全量并发下基本不会再假红。
             */
            string tempPath = Path.GetTempPath();
            long? freeSpace = ArchiveFixer.Storage.SpaceChecker.GetAvailableFreeSpace(tempPath);

            if (freeSpace == null)
            {
                // 取不到可用空间时本来就不做空间判断（这本身就是一条被允许的路径）。
                return;
            }

            var options = new ResourceBudgetOptions
            {
                MaxSingleFileSize = long.MaxValue,
                MaxTotalSize = long.MaxValue,
                MaxFileCount = int.MaxValue,
                MaxExpansionRatio = double.MaxValue,
                MinFreeSpaceReserveBytes = 1
            };

            BudgetCheckResult result = new ResourceBudget(options).CheckBeforeExtract(
                ListingWithLargestEntry(freeSpace.Value + (1024L * 1024 * 1024), "huge.bin"),
                archiveSizeBytes: 0,
                targetDirectory: tempPath);

            Assert.False(result.Allowed);
            Assert.True(result.IsSpaceShortage, "这一档必须标成空间不足（状态与配色都按它走）");
            Assert.Contains("空间", result.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain(StatusText.SecurityCapHint, result.Reason, StringComparison.Ordinal);
        }

        // ================================================================ ③ 保存时的钳制提示

        [Fact]
        public void 保存设置时安全上限超范围会被夹回并如实说明()
        {
            var viewModel = new SettingsViewModel(AppSettings.CreateDefault(), new SettingsService(CreatePathService()));

            viewModel.Settings.MaxSingleExtractedFileGiB = 8192;
            viewModel.SaveCommand.Execute(null);

            Assert.Equal(AppSettings.MaxExtractionCapGiB, viewModel.Settings.MaxSingleExtractedFileGiB);
            Assert.True(viewModel.DialogResult == true, "保存成功要能关窗（DialogResult = true）");
            Assert.Contains("安全上限", viewModel.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 保存设置时安全上限没超范围就不多嘴()
        {
            var viewModel = new SettingsViewModel(AppSettings.CreateDefault(), new SettingsService(CreatePathService()));

            viewModel.Settings.MaxSingleExtractedFileGiB = 128;
            viewModel.SaveCommand.Execute(null);

            Assert.Equal(128, viewModel.Settings.MaxSingleExtractedFileGiB);
            Assert.Equal("设置已保存。", viewModel.Message);
        }

        // ================================================================ 造清单

        private static ArchiveListResult ListingWithLargestEntry(long size, string path) =>
            ListingWithEntries(new[] { size }, new[] { path });

        private static ArchiveListResult ListingWithEntries(IReadOnlyList<long> sizes, IReadOnlyList<string>? paths = null)
        {
            var entries = new List<ArchiveEntry>();

            long total = 0;

            for (int i = 0; i < sizes.Count; i++)
            {
                total += sizes[i];

                entries.Add(new ArchiveEntry
                {
                    Path = paths != null && i < paths.Count ? paths[i] : $"payload-{i:D5}.bin",
                    Size = sizes[i]
                });
            }

            return new ArchiveListResult
            {
                Success = true,
                FileCount = entries.Count,
                TotalUncompressedSize = total,
                Entries = entries,
                EngineId = "fake",
                EngineVersion = "1.0"
            };
        }
    }
}

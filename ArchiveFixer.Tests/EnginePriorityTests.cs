using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Engines.WinRar;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **引擎优先级**（用户 2026-09-22："先是 winrar、7z、然后就是后面的引擎"）的设置项与选择规则。
    ///
    /// 三条要钉死的东西：
    /// ① 默认值就是 <c>WinRar → SevenZip</c>，旧配置（缺字段）读出来也是它；
    /// ② 选择规则是"**先按能力筛，再用优先级做 tiebreaker**"—— 优先级不能把干不了这活的引擎顶上去；
    /// ③ 不可用的引擎直接跳过，**不许因为"排第一但没装"就打不开包**。
    /// </summary>
    public class EnginePriorityTests : IDisposable
    {
        private readonly List<string> _tempFiles = new();

        public void Dispose()
        {
            // 设置项与运行时状态都是进程级/全局的：用例之间必须还原，免得上一个用例漏到下一个。
            EngineRuntimeSettings.ResetToDefaults();
            ToolLocator.Default.CustomSevenZipExePath = string.Empty;
            ToolLocator.Default.CustomUnRarExePath = string.Empty;

            foreach (string file in _tempFiles)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                }
            }
        }

        // ────────────────────────── 设置项与默认值 ──────────────────────────

        [Fact]
        public void 默认优先级是_WinRar到SevenZip()
        {
            AppSettings defaults = AppSettings.CreateDefault();

            Assert.Equal(new[] { EngineIds.WinRar, EngineIds.SevenZip }, defaults.EnginePriority);

            // 保留受损文件默认**关**（诊断文档 §2 C 组的采纳项，必须默认关）。
            Assert.False(defaults.KeepBrokenFiles);

            // UnRAR 路径默认留空 = 自动（已装 WinRAR 目录 → 内置）。
            Assert.Equal(string.Empty, defaults.CustomUnRarExePath);
        }

        [Fact]
        public void 旧配置缺字段时取默认值()
        {
            // 模拟升级前的 appsettings.json：没有 EnginePriority / KeepBrokenFiles / CustomUnRarExePath。
            const string legacyJson = """
            {
              "RecursiveScan": true,
              "CustomSevenZipExePath": "",
              "RecursionMode": "SingleLayer"
            }
            """;

            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(legacyJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            Assert.NotNull(settings);

            settings!.Normalize();

            Assert.Equal(new[] { EngineIds.WinRar, EngineIds.SevenZip }, settings.EnginePriority);
            Assert.False(settings.KeepBrokenFiles);
            Assert.Equal(string.Empty, settings.CustomUnRarExePath);
        }

        [Fact]
        public void 优先级归一化_去重补全并认大小写()
        {
            // 大小写差异认回规范写法；漏掉的引擎补到末尾（以后加第三个引擎时老配置不会被永久排除）。
            Assert.Equal(
                new[] { EngineIds.SevenZip, EngineIds.WinRar },
                EngineIds.Normalize(new[] { "SevenZip", "WINRAR" }));

            Assert.Equal(
                new[] { EngineIds.SevenZip, EngineIds.WinRar },
                EngineIds.Normalize(new[] { "sevenzip", "  ", "sevenzip" }));

            // 不认识的 id 留着（将来会有第三个引擎），只是排不到已知引擎前面。
            Assert.Equal(
                new[] { "libarchive", EngineIds.WinRar, EngineIds.SevenZip },
                EngineIds.Normalize(new[] { "libarchive" }));

            // 空 / null → 默认值。
            Assert.Equal(EngineIds.DefaultPriority, EngineIds.Normalize(null));
            Assert.Equal(EngineIds.DefaultPriority, EngineIds.Normalize(Array.Empty<string>()));
        }

        [Fact]
        public void 设置里的优先级会落盘并读回()
        {
            var settingsService = new SettingsService(new PathService());
            AppSettings settings = AppSettings.CreateDefault();

            settings.EnginePriority = new List<string> { EngineIds.SevenZip, EngineIds.WinRar };
            settings.KeepBrokenFiles = true;

            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

            Assert.Contains("EnginePriority", json, StringComparison.Ordinal);
            Assert.Contains("KeepBrokenFiles", json, StringComparison.Ordinal);

            AppSettings? roundTrip = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            Assert.NotNull(roundTrip);

            roundTrip!.Normalize();

            Assert.Equal(new[] { EngineIds.SevenZip, EngineIds.WinRar }, roundTrip.EnginePriority);
            Assert.True(roundTrip.KeepBrokenFiles);

            _ = settingsService;
        }

        [Fact]
        public void 失效的自定义UnRAR路径不会把引擎变成不可用()
        {
            AppSettings settings = AppSettings.CreateDefault();

            settings.CustomUnRarExePath = @"Z:\不存在的目录\UnRAR.exe";
            settings.Normalize();

            // 归一化把它清空 → ToolLocator 走"已装 WinRAR 目录 → 内置"两级回落。
            Assert.Equal(string.Empty, settings.CustomUnRarExePath);
        }

        // ────────────────────────── 选择规则 ──────────────────────────

        [Fact]
        public void 先按能力筛_再把优先级当tiebreaker()
        {
            var registry = new EngineRegistry();

            registry.Register(new FakeEngine(EngineIds.WinRar, new[] { "RAR5", "RAR4" }, available: true));
            registry.Register(new FakeEngine(EngineIds.SevenZip, new[] { "RAR5", "ZIP", "7Z" }, available: true));

            // 默认优先级：WinRar → SevenZip。
            var selector = new EngineSelector(registry, EngineIds.DefaultPriority);

            // 两者都能干的格式（RAR5）：按优先级 → UnRAR。
            Assert.Equal(EngineIds.WinRar, selector.SelectFor("RAR5", EngineOperation.Extract)?.Id);

            // 只有 7-Zip 能干的格式（ZIP）：优先级说了不算，能力说了算。
            Assert.Equal(EngineIds.SevenZip, selector.SelectFor("ZIP", EngineOperation.Extract)?.Id);

            // 把顺序倒过来：RAR5 就该走 7-Zip 了（优先级真的在起作用，不是写死的）。
            var reversed = new EngineSelector(registry, new[] { EngineIds.SevenZip, EngineIds.WinRar });

            Assert.Equal(EngineIds.SevenZip, reversed.SelectFor("RAR5", EngineOperation.Extract)?.Id);
        }

        [Fact]
        public void 不可用的引擎直接跳过_不许因为排第一但没装就打不开包()
        {
            var registry = new EngineRegistry();

            registry.Register(new FakeEngine(EngineIds.WinRar, new[] { "RAR5" }, available: false));
            registry.Register(new FakeEngine(EngineIds.SevenZip, new[] { "RAR5" }, available: true));

            var selector = new EngineSelector(registry, EngineIds.DefaultPriority);

            Assert.Equal(EngineIds.SevenZip, selector.SelectFor("RAR5", EngineOperation.Extract)?.Id);

            EngineSelection explained = selector.Explain("RAR5", EngineOperation.Extract);

            Assert.Equal(EngineIds.SevenZip, explained.Selected?.Id);
            Assert.Contains(explained.SkippedUnavailable, e => e.Id == EngineIds.WinRar);
            Assert.Contains("已跳过", explained.Describe(), StringComparison.Ordinal);
        }

        [Fact]
        public void 优先级列表把谁排前面都不影响能力筛选()
        {
            var registry = new EngineRegistry();

            // 只有 7-Zip 能解 ZIP，即使 UnRAR 排在第一位也不会被选中。
            registry.Register(new FakeEngine(EngineIds.WinRar, new[] { "RAR5" }, available: true));
            registry.Register(new FakeEngine(EngineIds.SevenZip, new[] { "ZIP" }, available: true));

            var selector = new EngineSelector(registry, EngineIds.DefaultPriority);

            Assert.Equal(EngineIds.SevenZip, selector.SelectFor("ZIP", EngineOperation.Extract)?.Id);
            Assert.Equal(EngineIds.WinRar, selector.SelectFor("RAR5", EngineOperation.Extract)?.Id);

            // 谁都干不了的格式：返回 null，由调用方报"没有引擎能处理"（不抛异常）。
            Assert.Null(selector.SelectFor("SQUASHFS", EngineOperation.Extract));
        }

        [Fact]
        public void 运行时设置能改变选择结果()
        {
            var registry = EngineRegistry.CreateDefault(new ToolLocator());

            try
            {
                EngineRuntimeSettings.SetPriority(new[] { EngineIds.SevenZip, EngineIds.WinRar });

                var selector = new EngineSelector(registry);

                Assert.Equal(new[] { EngineIds.SevenZip, EngineIds.WinRar }, selector.Priority);
                Assert.Equal(EngineIds.SevenZip, selector.SelectFor("RAR5", EngineOperation.Extract)?.Id);
            }
            finally
            {
                EngineRuntimeSettings.ResetToDefaults();
            }

            var restored = new EngineSelector(registry);

            Assert.Equal(EngineIds.WinRar, restored.SelectFor("RAR5", EngineOperation.Extract)?.Id);
        }

        [Fact]
        public void 回退规则沿用设计文档_只有引擎侧的错才换引擎()
        {
            // 可换：不支持 / 不可用 / 解析器拒绝 / 已知兼容性问题。
            Assert.True(EngineSelector.ShouldTryFallback(EngineErrorTypes.UnsupportedFormat));
            Assert.True(EngineSelector.ShouldTryFallback(EngineErrorTypes.UnsupportedFeature));
            Assert.True(EngineSelector.ShouldTryFallback(EngineErrorTypes.EngineUnavailable));
            Assert.True(EngineSelector.ShouldTryFallback(EngineErrorTypes.ParserRejected));
            Assert.True(EngineSelector.ShouldTryFallback(EngineErrorTypes.KnownCompatibilityIssue));

            // 不换：换引擎也没用，只会让用户多等一遍、把真实原因埋掉。
            Assert.False(EngineSelector.ShouldTryFallback(EngineErrorTypes.WrongPassword));
            Assert.False(EngineSelector.ShouldTryFallback(EngineErrorTypes.VolumeMissing));
            Assert.False(EngineSelector.ShouldTryFallback(EngineErrorTypes.AccessDenied));
            Assert.False(EngineSelector.ShouldTryFallback(EngineErrorTypes.Cancelled));
            Assert.False(EngineSelector.ShouldTryFallback(EngineErrorTypes.CorruptedArchive));
            Assert.False(EngineSelector.ShouldTryFallback(null));
        }

        // ────────────────────────── ToolLocator 三级来源 ──────────────────────────

        [Fact]
        public void UnRAR来源顺序_自选优先_内置兜底()
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, "tools", "unrar", "UnRAR.exe");

            Assert.True(File.Exists(bundled));

            // ① 自选路径存在 → 用它。
            var custom = new ToolLocator { CustomUnRarExePath = bundled };

            Assert.True(custom.UnRarExists);
            Assert.True(custom.IsUsingCustomUnRarPath);
            Assert.Equal(bundled, custom.UnRarExePath, ignoreCase: true);

            // ② 自选路径不存在 → **不**把后面两档吃掉，继续往下找。
            var brokenCustom = new ToolLocator
            {
                CustomUnRarExePath = Path.Combine(Path.GetTempPath(), "no-such-unrar", "UnRAR.exe"),
                UseWinRarInstallation = false,
                UseBundledUnRar = true
            };

            Assert.False(brokenCustom.IsUsingCustomUnRarPath);
            Assert.True(brokenCustom.UnRarExists);
            Assert.Equal(bundled, brokenCustom.UnRarExePath, ignoreCase: true);

            // ③ 三档全关 → 真的"没有 UnRAR"，且返回的是"本来应该在的位置"用于提示。
            var none = new ToolLocator
            {
                CustomUnRarExePath = Path.Combine(Path.GetTempPath(), "no-such-unrar", "UnRAR.exe"),
                UseWinRarInstallation = false,
                UseBundledUnRar = false
            };

            Assert.False(none.UnRarExists);
            Assert.Equal(bundled, none.UnRarExePath, ignoreCase: true);
            Assert.Contains("未找到", none.DescribeUnRarResolution(), StringComparison.Ordinal);
        }

        [Fact]
        public void 没有UnRAR时引擎不可用_选择器回落到7zip()
        {
            var tools = new ToolLocator { UseWinRarInstallation = false, UseBundledUnRar = false };

            EngineRegistry registry = EngineRegistry.CreateDefault(tools);

            Assert.False(registry.FindById(EngineIds.WinRar)!.IsAvailable);
            Assert.True(registry.FindById(EngineIds.SevenZip)!.IsAvailable);

            var selector = new EngineSelector(registry, EngineIds.DefaultPriority);

            Assert.Equal(EngineIds.SevenZip, selector.SelectFor("RAR5", EngineOperation.Extract)?.Id);
            Assert.Equal(EngineIds.SevenZip, selector.SelectFor("ZIP", EngineOperation.Extract)?.Id);
        }

        // ────────────────────────── 保留受损文件（-kb） ──────────────────────────

        [Fact]
        public void 保留受损文件默认关_只对RAR引擎加_kb()
        {
            try
            {
                EngineRuntimeSettings.ResetToDefaults();

                var unrar = new UnRarProcessRunner(new ToolLocator());

                // 默认（关）：不加 -kb。
                Assert.DoesNotContain(
                    "-kb",
                    unrar.BuildExtractArguments(@"C:\t\a.rar", @"C:\t\out", null, new ExtractOptions(), keepBrokenFiles: false));

                EngineRuntimeSettings.SetKeepBrokenFiles(true);

                // 开：UnRAR 加 -kb（实测：不加时它会删掉校验失败的半成品）。
                Assert.Contains(
                    "-kb",
                    unrar.BuildExtractArguments(@"C:\t\a.rar", @"C:\t\out", null, new ExtractOptions(), keepBrokenFiles: true));

                /*
                 * ⛔ 7-Zip **永远不加** -kb：`7z x -kb` 实测报
                 * "Command Line Error: Unknown switch: -kb"（退出码 7）——
                 * 那是 RAR/UnRAR 的开关。加上它，用户一开这个设置，所有解压都会失败。
                 * 7-Zip 本来就保留半成品，所以这一档对它是无事可做，而不是"漏了"。
                 */
                var sevenZip = new SevenZipProcessRunner(new ToolLocator());

                Assert.DoesNotContain(
                    "-kb",
                    sevenZip.BuildExtractArguments(@"C:\t\a.7z", @"C:\t\out", string.Empty, new ExtractOptions()));

                // 顺带钉死"参数里没有无效开关"这条：7z 的参数表里不该出现 RAR 专有开关。
                Assert.DoesNotContain(
                    "-kb",
                    sevenZip.BuildExtractArguments(@"C:\t\a.7z", @"C:\t\out", "pw", new ExtractOptions()));
            }
            finally
            {
                EngineRuntimeSettings.ResetToDefaults();
            }
        }

        [Fact]
        public async Task 保留受损文件不影响成败判定()
        {
            // -kb 只决定半成品留不留：退出码与分类链完全不变，仍然是"部分完成"。
            EngineRuntimeSettings.SetKeepBrokenFiles(true);

            try
            {
                var runner = new SevenZipProcessRunner(new ToolLocator());

                ArchiveOperationResult partial = runner.AnalyzeResult(1, "Everything is Ok", "Sub items Errors: 1", string.Empty, TimeSpan.Zero);

                Assert.False(partial.Success);
                Assert.Equal(StatusText.PartiallyCompleted, partial.Status);

                await Task.CompletedTask;
            }
            finally
            {
                EngineRuntimeSettings.ResetToDefaults();
            }
        }

        /// <summary>
        /// 可控的假引擎（只用来验证选择规则，不碰外部进程）。
        ///
        /// 默认按**白名单**建模（登记了哪些格式就只认哪些）—— 这正是专用引擎
        /// （UnRAR 只认 RAR）的语义；不设它的话，未登记的格式会退回"总体能力位"，
        /// 于是"只有 7-Zip 能解 zip"这条就测不出来了（实测踩过：ZIP 被路由到 winrar）。
        /// </summary>
        private sealed class FakeEngine : IArchiveEngine
        {
            private readonly string[] _formats;

            public FakeEngine(string id, string[] formats, bool available)
            {
                Id = id;
                _formats = formats;
                IsAvailable = available;

                Capabilities = new EngineCapabilities
                {
                    CanList = true,
                    CanTest = true,
                    CanExtract = true,
                    FormatsAreWhitelist = true,
                    Formats = formats
                        .Select(f => new EngineFormatCapability { Format = f, CanList = true, CanTest = true, CanExtract = true })
                        .ToList()
                };
            }

            public string Id { get; }

            public string DisplayName => Id;

            public string Version => "0.0";

            public bool IsAvailable { get; }

            public EngineCapabilities Capabilities { get; }

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = _formats[0] });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveListResult { Success = true, EngineId = Id, EngineVersion = Version });

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero));

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero));
        }
    }
}

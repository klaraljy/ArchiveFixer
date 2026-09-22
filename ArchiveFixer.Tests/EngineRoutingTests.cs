using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **引擎真的接进流程了没有**（本批最重要的一条验收）。
    ///
    /// <para>
    /// 背景：`IArchiveEngine` 这个端口从 M1 就存在，但 <c>MainViewModel</c> 里写死
    /// <c>new SevenZipEngine()</c> —— 于是"引擎优先级"设置与做好的 <c>UnRarEngine</c>
    /// 在真实流程里**一个字都没生效**。这一组用例钉住四件事：
    /// </para>
    ///
    /// <list type="number">
    /// <item><description>RAR → 走 UnRAR；zip / 7z / tar → 走 7-Zip（按**格式**分派，不看后缀）；</description></item>
    /// <item><description>UnRAR 不可用（没装 / 内置被关 / 从优先级里去掉）→ **回落 7-Zip，包照样打开**；</description></item>
    /// <item><description>改优先级，分派结果跟着变（不是把顺序写死在代码里）；</description></item>
    /// <item><description>报告写的是**这个包实际用的**引擎（不变量 14），问不到才退回通用引擎。</description></item>
    /// </list>
    ///
    /// <para>
    /// 分两组：<b>替身引擎 + 真文件头</b>（不启进程，验证分派规则本身）与
    /// <b>真引擎 + 真样本</b>（内置 7z 现造 zip / RAR 样本，验证端到端真的能解出来）。
    /// </para>
    ///
    /// <para>
    /// ⚠ 引擎优先级 / 工具路径 / 递归工作区根都是**进程级静态**，所以整类与其它同类用例一起
    /// **串行**跑（<c>InnerLayerContinuationTests</c> 顶部的 CollectionDefinition），
    /// 每个用例结束再还原一次。
    /// </para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class EngineRoutingTests : IDisposable
    {
        private readonly string _root;
        private readonly ITestOutputHelper _output;

        public EngineRoutingTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerEngineRouting", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            // 引擎优先级 / 保留受损文件是**进程级**静态：用例之间必须还原，免得上一个漏到下一个。
            EngineRuntimeSettings.ResetToDefaults();

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

        // ================================================================ ① 分派规则（替身引擎 + 真文件头）

        /// <summary>
        /// 四种真实格式各走各的引擎：RAR → UnRAR；zip / 7z / tar → 7-Zip。
        ///
        /// 判据是**哪个引擎真的被调用了**（替身把调用记下来），不是"选择器算出来是谁" ——
        /// 那正是这次要修的缺陷形态：算得对、但真正干活的还是写死的那个。
        /// </summary>
        [Fact]
        public async Task 按格式分派_RAR走UnRAR_zip与7z与tar走七Zip()
        {
            var winrar = new RecordingEngine(EngineIds.WinRar, RarOnly());
            var sevenZip = new RecordingEngine(EngineIds.SevenZip, General());

            var registry = new EngineRegistry();
            registry.Register(winrar);
            registry.Register(sevenZip);

            var router = new EngineRouter(registry);

            (string path, string format)[] cases =
            {
                (CreateMagicFile("a.rar", Rar5Magic), "RAR5"),
                (CreateMagicFile("b.zip", ZipMagic), "ZIP"),
                (CreateMagicFile("c.7z", SevenZipMagic), "7Z"),
                (CreateMagicFile("d.tar", TarMagic()), "TAR")
            };

            foreach ((string path, string format) in cases)
            {
                winrar.Calls.Clear();
                sevenZip.Calls.Clear();

                ArchiveListResult result = await router.ListAsync(ArchiveRequest.For(path), CancellationToken.None);

                _output.WriteLine($"{format} → 调用 {string.Join('、', winrar.Calls.Concat(sevenZip.Calls))}（结果引擎 {result.EngineId}）");

                Assert.True(result.Success, $"{format} 应当被列出来：{result.Message}");

                string expected = format.StartsWith("RAR", StringComparison.Ordinal)
                    ? EngineIds.WinRar
                    : EngineIds.SevenZip;

                Assert.Equal(expected, result.EngineId);

                // 另一个引擎**一次都不该被碰**（不是"先试错了再换"）。
                RecordingEngine untouched = expected == EngineIds.WinRar ? sevenZip : winrar;

                Assert.Empty(untouched.Calls);

                // 逐任务溯源：这个包实际用的就是它。
                Assert.Equal(expected, router.ResolveIdentityFor(path)?.EngineId);
                Assert.False(string.IsNullOrWhiteSpace(router.ResolveIdentityFor(path)?.Version));
            }
        }

        /// <summary>
        /// 优先级是**真的**在起作用：把它倒过来，同一个 RAR 就该走 7-Zip ——
        /// 顺序来自设置（<see cref="EngineRuntimeSettings.EnginePriority"/>），不是代码里排好的。
        /// </summary>
        [Fact]
        public async Task 优先级调换后同一个RAR改走七Zip()
        {
            var winrar = new RecordingEngine(EngineIds.WinRar, RarOnly());
            var sevenZip = new RecordingEngine(EngineIds.SevenZip, General());

            var registry = new EngineRegistry();
            registry.Register(winrar);
            registry.Register(sevenZip);

            string rar = CreateMagicFile("flip.rar", Rar5Magic);

            try
            {
                EngineRuntimeSettings.SetPriority(new[] { EngineIds.WinRar, EngineIds.SevenZip });

                var defaultOrder = new EngineRouter(registry);

                Assert.Equal(EngineIds.WinRar, (await defaultOrder.ListAsync(ArchiveRequest.For(rar))).EngineId);

                EngineRuntimeSettings.SetPriority(new[] { EngineIds.SevenZip, EngineIds.WinRar });

                var flipped = new EngineRouter(registry);

                Assert.Equal(EngineIds.SevenZip, (await flipped.ListAsync(ArchiveRequest.For(rar))).EngineId);
            }
            finally
            {
                EngineRuntimeSettings.ResetToDefaults();
            }
        }

        /// <summary>
        /// ⛔ 回落：UnRAR 排第一但**不可用**（没装 / 内置被关 / 从优先级里去掉）时，
        /// 必须安静地落到 7-Zip，**绝不许因此打不开包**（AGENTS.md §3.1）。
        /// </summary>
        [Fact]
        public async Task UnRAR不可用时_RAR仍然能打开并走七Zip()
        {
            var winrar = new RecordingEngine(EngineIds.WinRar, RarOnly(), available: false);
            var sevenZip = new RecordingEngine(EngineIds.SevenZip, General());

            var registry = new EngineRegistry();
            registry.Register(winrar);
            registry.Register(sevenZip);

            var router = new EngineRouter(registry);
            string rar = CreateMagicFile("no-unrar.rar", Rar5Magic);

            ArchiveListResult result = await router.ListAsync(ArchiveRequest.For(rar));

            Assert.True(result.Success, "没装 UnRAR 也必须能打开 RAR（回落到 7-Zip）");
            Assert.Equal(EngineIds.SevenZip, result.EngineId);
            Assert.Empty(winrar.Calls);

            // "排第一但没装"这件事要能在日志里说清（不是静默换人）。
            Assert.Contains(EngineIds.WinRar, router.DescribeDispatch(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 换引擎的判据来自选择器（设计.md §二十），**不是**"失败了就换一个试试"：
        /// 密码错、损坏这类换引擎也没用的错误必须原样报给用户，只调用一次。
        /// </summary>
        [Fact]
        public async Task 密码错不换引擎_只尝试优先级第一的那个()
        {
            var winrar = new RecordingEngine(EngineIds.WinRar, RarOnly())
            {
                ListResult = ArchiveListResult.Failure(EngineErrorTypes.WrongPassword, "密码错误", EngineIds.WinRar, "1.0")
            };

            var sevenZip = new RecordingEngine(EngineIds.SevenZip, General());

            var registry = new EngineRegistry();
            registry.Register(winrar);
            registry.Register(sevenZip);

            var router = new EngineRouter(registry);
            string rar = CreateMagicFile("locked.rar", Rar5Magic);

            ArchiveListResult result = await router.ListAsync(ArchiveRequest.For(rar, "wrong"));

            Assert.False(result.Success);
            Assert.Equal(EngineErrorTypes.WrongPassword, result.ErrorType);

            // 只问了 UnRAR 一次；7-Zip 一次都没被拉起（换引擎对密码错没有任何帮助）。
            Assert.Single(winrar.Calls);
            Assert.Empty(sevenZip.Calls);
        }

        /// <summary>
        /// 反过来：**引擎侧**的错误（不支持这个格式）值得换一个引擎再试 —— 这是设计.md §二十
        /// 允许回退的那一类，也正是第二引擎存在时最可能发生的情况（老引擎不认识新格式）。
        /// 全失败时报的是**最后尝试**的那个引擎（任务上那条错误信息就来自它）。
        /// </summary>
        [Fact]
        public async Task 引擎侧不支持才换下一个_全失败时报最后尝试的引擎()
        {
            var winrar = new RecordingEngine(EngineIds.WinRar, RarOnly())
            {
                ListResult = ArchiveListResult.Failure(EngineErrorTypes.UnsupportedFormat, "不支持", EngineIds.WinRar, "1.0")
            };

            var sevenZip = new RecordingEngine(EngineIds.SevenZip, General())
            {
                ListResult = ArchiveListResult.Failure(EngineErrorTypes.UnsupportedFormat, "也不支持", EngineIds.SevenZip, "2.0")
            };

            var registry = new EngineRegistry();
            registry.Register(winrar);
            registry.Register(sevenZip);

            var router = new EngineRouter(registry);
            string rar = CreateMagicFile("odd.rar", Rar5Magic);

            ArchiveListResult result = await router.ListAsync(ArchiveRequest.For(rar));

            Assert.False(result.Success);
            Assert.Single(winrar.Calls);
            Assert.Single(sevenZip.Calls);

            Assert.Equal(EngineIds.SevenZip, router.ResolveIdentityFor(rar)?.EngineId);
        }

        /// <summary>
        /// 格式识别不出来时交给**通用引擎**（7-Zip）去试，绝不许因为"识别失败"把包卡住，
        /// 更不许把它甩给只认 RAR 的专用引擎。
        /// </summary>
        [Fact]
        public async Task 格式识别不出来时交给通用引擎()
        {
            var winrar = new RecordingEngine(EngineIds.WinRar, RarOnly());
            var sevenZip = new RecordingEngine(EngineIds.SevenZip, General());

            var registry = new EngineRegistry();
            registry.Register(winrar);
            registry.Register(sevenZip);

            var router = new EngineRouter(registry);

            string mystery = Path.Combine(_root, "mystery.bin");
            File.WriteAllBytes(mystery, Encoding.ASCII.GetBytes("这不是任何已知归档的文件头，只是一段普通文本。"));

            ArchiveListResult result = await router.ListAsync(ArchiveRequest.For(mystery));

            Assert.True(result.Success);
            Assert.Equal(EngineIds.SevenZip, result.EngineId);
            Assert.Empty(winrar.Calls);
        }

        /// <summary>
        /// 抠出来的内嵌归档（<c>&lt;工作区&gt;\&lt;任务目录&gt;\&lt;源包基名&gt;.zip</c>）与源包
        /// **不同路径、不同扩展名**，只有基名一样 —— 报告要标的是源包那个任务，
        /// 所以溯源必须能按基名回溯（否则双面文件的报告会退回"通用引擎"）。
        /// </summary>
        [Fact]
        public async Task 内嵌归档按基名回溯到源包任务()
        {
            var sevenZip = new RecordingEngine(EngineIds.SevenZip, General());

            var registry = new EngineRegistry();
            registry.Register(sevenZip);

            var router = new EngineRouter(registry);

            string source = Path.Combine(_root, "双面视频.mp4");
            File.WriteAllBytes(source, new byte[] { 0x00, 0x00, 0x00, 0x18 });

            string carved = Path.Combine(_root, "work", "task-1", "双面视频.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(carved)!);
            File.WriteAllBytes(carved, ZipMagic.Concat(new byte[64]).ToArray());

            ArchiveListResult result = await router.ListAsync(ArchiveRequest.For(carved));

            Assert.True(result.Success);

            // 源包路径自己没被引擎碰过，但按基名应当能追到同一个引擎。
            Assert.Equal(EngineIds.SevenZip, router.ResolveIdentityFor(source)?.EngineId);
        }

        // ================================================================ ② 真引擎 + 真样本

        /// <summary>
        /// 真引擎对照：同一个 RAR 走 UnRAR、同一个 zip 走 7-Zip，而且**真的解得出文件**。
        ///
        /// 这一条同时钉住"能力位如实"：UnRAR 只声明 RAR，所以 zip 不会被它接走
        /// （实测踩过：没有白名单时 zip 会被路由到 UnRAR，本来能打开的包反而打不开）。
        /// </summary>
        [Fact]
        public async Task 真引擎_RAR走UnRAR_zip走七Zip_且都真的解得出()
        {
            RarSampleSet? samples = RarSampleSet.TryCreate(_root, _output.WriteLine);

            if (samples == null)
            {
                _output.WriteLine("跳过：既没设 ARCHIVEFIXER_RAR_SAMPLES，本机也没装 WinRAR（无法造 RAR 样本）");
                return;
            }

            var tools = new ToolLocator { CustomUnRarExePath = samples.BundledUnRarPath };
            var router = new EngineRouter(EngineRegistry.CreateDefault(tools));

            _output.WriteLine(router.DescribeDispatch());

            // ① 真 RAR → UnRAR。
            string rarOut = Path.Combine(_root, "out-rar");
            ArchiveOperationResult rarResult = await router.ExtractAsync(
                new ArchiveRequest { ArchivePath = samples.PlainArchive, OutputPath = rarOut },
                new ExtractOptions());

            _output.WriteLine($"RAR：Success={rarResult.Success} 引擎={rarResult.EngineId} {rarResult.EngineVersion}");

            Assert.True(rarResult.Success, rarResult.Message);
            Assert.Equal(EngineIds.WinRar, rarResult.EngineId);
            Assert.Equal(EngineIds.WinRar, router.ResolveIdentityFor(samples.PlainArchive)?.EngineId);

            samples.AssertContentMatches(rarOut);

            // ② 真 ZIP（用随包分发的 7z.exe 现造）→ 7-Zip。
            string zipSource = Path.Combine(_root, "zip-src");
            Directory.CreateDirectory(zipSource);
            File.WriteAllText(Path.Combine(zipSource, "hello.txt"), "hello engine routing");

            string zipPath = Path.Combine(_root, "sample.zip");

            RarSampleSet.RunSevenZip(
                new ToolLocator().SevenZipExePath,
                zipSource,
                "a", "-tzip", zipPath, Path.Combine(zipSource, "hello.txt"));

            string zipOut = Path.Combine(_root, "out-zip");
            ArchiveOperationResult zipResult = await router.ExtractAsync(
                new ArchiveRequest { ArchivePath = zipPath, OutputPath = zipOut },
                new ExtractOptions());

            _output.WriteLine($"ZIP：Success={zipResult.Success} 引擎={zipResult.EngineId} {zipResult.EngineVersion}");

            Assert.True(zipResult.Success, zipResult.Message);
            Assert.Equal(EngineIds.SevenZip, zipResult.EngineId);
            Assert.True(File.Exists(Path.Combine(zipOut, "hello.txt")));
        }

        /// <summary>
        /// ⛔ 真回落：这台机器上"没有 UnRAR"（三档来源全关）时，真 RAR 仍然由 7-Zip 正常解开。
        /// 这条是"第二引擎接进来不会把第一引擎搞坏"的判据。
        /// </summary>
        [Fact]
        public async Task 真引擎_没有UnRAR时真RAR由七Zip解开()
        {
            RarSampleSet? samples = RarSampleSet.TryCreate(_root, _output.WriteLine);

            if (samples == null)
            {
                _output.WriteLine("跳过：没有 RAR 样本");
                return;
            }

            var tools = new ToolLocator
            {
                CustomUnRarExePath = Path.Combine(_root, "no-such-unrar", "UnRAR.exe"),
                UseWinRarInstallation = false,
                UseBundledUnRar = false
            };

            EngineRegistry registry = EngineRegistry.CreateDefault(tools);

            Assert.False(registry.FindById(EngineIds.WinRar)!.IsAvailable, "三档全关时 UnRAR 必须判为不可用");
            Assert.True(registry.FindById(EngineIds.SevenZip)!.IsAvailable);

            var router = new EngineRouter(registry);
            string output = Path.Combine(_root, "out-fallback");

            ArchiveOperationResult result = await router.ExtractAsync(
                new ArchiveRequest { ArchivePath = samples.PlainArchive, OutputPath = output },
                new ExtractOptions());

            _output.WriteLine($"回落结果：Success={result.Success} 引擎={result.EngineId} {result.EngineVersion}");

            Assert.True(result.Success, result.Message);
            Assert.Equal(EngineIds.SevenZip, result.EngineId);

            samples.AssertContentMatches(output);
        }

        // ================================================================ ③ 报告与流水线接线

        /// <summary>
        /// 报告逐任务写**实际**引擎、表头列出本次用过的引擎（不变量 14）。
        ///
        /// 旧行为：整份清单只写"通用引擎"一个身份 —— 一个 RAR 走 UnRAR、一个 zip 走 7-Zip 时，
        /// 两个任务的引擎行都是 7-Zip，溯源是错的。
        /// </summary>
        [Fact]
        public void 失败清单逐任务写实际引擎_表头列出本次用过的引擎()
        {
            var rarIdentity = new EngineIdentity
            {
                EngineId = EngineIds.WinRar,
                DisplayName = "UnRAR 命令行（RARLAB 解压工具）",
                Version = "7.23.0",
                IsAvailable = true
            };

            var zipIdentity = new EngineIdentity
            {
                EngineId = EngineIds.SevenZip,
                DisplayName = "7-Zip 命令行",
                Version = "26.01",
                IsAvailable = true
            };

            var identities = new Dictionary<string, EngineIdentity>(StringComparer.OrdinalIgnoreCase)
            {
                [@"E:\x\one.rar"] = rarIdentity,
                [@"E:\x\two.zip"] = zipIdentity
            };

            var service = new TaskSummaryService
            {
                EngineIdentityProvider = task =>
                    identities.TryGetValue(task.CurrentPath, out EngineIdentity? found) ? found : null
            };

            string text = service.BuildFailedListText(new[]
            {
                Failed(@"E:\x\one.rar", StatusText.Corrupted, "压缩包可能损坏"),
                Failed(@"E:\x\two.zip", StatusText.ExtractFailed, "解压失败")
            });

            _output.WriteLine(text);

            // 表头把"本次实际用过的"列全（两个引擎都要在，不能挑一个当代表）。
            Assert.Contains("引擎：UnRAR 命令行（RARLAB 解压工具） 7.23.0 / 7-Zip 命令行 26.01（本次实际使用）", text);

            // 逐任务那一行写的是**它自己**用的那个。
            int rarLine = text.IndexOf("[归档] one.rar", StringComparison.Ordinal);
            int zipLine = text.IndexOf("[归档] two.zip", StringComparison.Ordinal);

            Assert.True(rarLine >= 0 && zipLine >= 0);

            string rarBlock = text[rarLine..zipLine];
            string zipBlock = text[zipLine..];

            Assert.Contains("引擎：UnRAR 命令行（RARLAB 解压工具） 7.23.0", rarBlock);
            Assert.Contains("引擎：7-Zip 命令行 26.01", zipBlock);

            // 引擎名 + 版本都要有（不变量 14 的底线，不许只写"引擎：7-Zip"）。
            Assert.Contains("26.01", zipBlock);
        }

        /// <summary>
        /// 问不到实际引擎时（没接口子 / 这个任务没跑过引擎）退回通用引擎身份 ——
        /// **引擎名 + 版本在任何情况下都不缺**（不变量 14 的底线）。
        /// </summary>
        [Fact]
        public void 问不到实际引擎时退回通用引擎_不变量14底线()
        {
            var service = new TaskSummaryService
            {
                EngineIdentity = new EngineIdentity
                {
                    EngineId = EngineIds.SevenZip,
                    DisplayName = "7-Zip 命令行",
                    Version = "26.01",
                    IsAvailable = true
                },
                EngineIdentityProvider = _ => null
            };

            string text = service.BuildFailedListText(new[]
            {
                Failed(@"E:\x\unknown.rar", StatusText.ExtractFailed, "解压失败")
            });

            Assert.Contains("引擎：7-Zip 命令行 26.01", text);

            // 来源抛异常同样不许把清单带崩（失败清单是失败后唯一能读的产物）。
            service.EngineIdentityProvider = _ => throw new InvalidOperationException("来源坏了");

            Assert.Contains("引擎：7-Zip 命令行 26.01", service.BuildFailedListText(new[]
            {
                Failed(@"E:\x\unknown.rar", StatusText.ExtractFailed, "解压失败")
            }));
        }

        /// <summary>
        /// MainViewModel 的**生产构造**真的把分派引擎接进了流水线：
        /// ① <c>PipelineEngine</c> 是门面（不是裸的某个引擎）；
        /// ② 真跑一遍批量解压时，RAR 走到 UnRAR 上、zip 走到 7-Zip 上（替身记录调用）；
        /// ③ 报告按任务给出**实际**引擎。
        ///
        /// ⚠ 构造 MainViewModel 会写几个进程级静态（引擎优先级 / 工具路径 / 递归工作区根），
        /// 所以这一组与其它同类用例一起**串行**跑（见 <c>InnerLayerContinuationTests</c> 顶部的
        /// CollectionDefinition），并在 finally 里还原。
        /// </summary>
        [Fact]
        public async Task MainViewModel把分派引擎接进流水线()
        {
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;
            string previousUnRarPath = ToolLocator.Default.CustomUnRarExePath;
            IReadOnlyList<string> previousPriority = EngineRuntimeSettings.EnginePriority;

            try
            {
                var winrar = new RecordingEngine(EngineIds.WinRar, RarOnly());
                var sevenZip = new RecordingEngine(EngineIds.SevenZip, General());

                var registry = new EngineRegistry();
                registry.Register(winrar);
                registry.Register(sevenZip);

                string dataRoot = Path.Combine(_root, "data");
                string outputRoot = Path.Combine(_root, "out");
                Directory.CreateDirectory(dataRoot);
                Directory.CreateDirectory(outputRoot);

                var pathService = new PathService { DataRootDirectory = dataRoot };
                var settingsService = new SettingsService(pathService);

                AppSettings settings = AppSettings.CreateDefault();
                settings.CacheRootDirectory = dataRoot;
                settings.CustomOutputDirectory = outputRoot;
                settings.ExtractToOriginalDirectory = false;
                settings.KeepArchiveNameFolder = true;
                settings.RecursionMode = "SingleLayer";
                settings.AutoScanAfterDrop = false;
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);

                settingsService.Save(settings);

                var summary = new TaskSummaryService();
                var log = new LogService(pathService);
                var passwordService = new PasswordService();

                var vm = new MainViewModel(
                    new FileScanService(),
                    new ArchiveDetectService(),
                    new RenameService(),
                    sevenZip,
                    passwordService,
                    log,
                    settingsService,
                    pathService,
                    summary,
                    new ClipboardService(),
                    new DialogService(),
                    cleanupService: null,
                    engineRegistry: registry);

                // ① 流水线拿到的必须是门面（旧代码给的是裸的 SevenZipEngine 实例）。
                Assert.IsType<EngineRouter>(vm.PipelineEngine);
                Assert.NotSame(sevenZip, vm.PipelineEngine);

                string rarPath = CreateMagicFile("pipe.rar", Rar5Magic);
                string zipPath = CreateMagicFile("pipe.zip", ZipMagic);

                ArchiveTask rarTask = AddTask(vm, rarPath, "RAR5");
                ArchiveTask zipTask = AddTask(vm, zipPath, "ZIP");

                // ② 真跑一遍批量解压（用 MainViewModel 自己那套设置与门面）。
                var coordinator = new ExtractionCoordinator(
                    vm, vm.PipelineEngine, passwordService, pathService, new DialogService());

                await coordinator.StartExtractAsync();

                _output.WriteLine($"RAR 任务：{rarTask.Status}；调用记录 {string.Join('、', winrar.Calls)}");
                _output.WriteLine($"ZIP 任务：{zipTask.Status}；调用记录 {string.Join('、', sevenZip.Calls)}");

                Assert.Equal(StatusText.ExtractSuccess, rarTask.Status);
                Assert.Equal(StatusText.ExtractSuccess, zipTask.Status);

                // RAR 只在 UnRAR 上跑过；zip 只在 7-Zip 上跑过（两个替身各自只记到了自己的包）。
                Assert.Contains(winrar.Calls, call => call.EndsWith(rarPath, StringComparison.OrdinalIgnoreCase));
                Assert.Contains(sevenZip.Calls, call => call.EndsWith(zipPath, StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(winrar.Calls, call => call.EndsWith(zipPath, StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(sevenZip.Calls, call => call.EndsWith(rarPath, StringComparison.OrdinalIgnoreCase));

                // ③ 报告按任务给出实际引擎。
                Assert.Equal(EngineIds.WinRar, summary.ResolveTaskEngine(rarTask)?.EngineId);
                Assert.Equal(EngineIds.SevenZip, summary.ResolveTaskEngine(zipTask)?.EngineId);

                // 版本号也要有（报告里"引擎名 + 版本"，不变量 14）。
                Assert.False(string.IsNullOrWhiteSpace(summary.ResolveTaskEngine(rarTask)?.Version));
            }
            finally
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
                ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;
                ToolLocator.Default.CustomUnRarExePath = previousUnRarPath;
                EngineRuntimeSettings.SetPriority(previousPriority);
                ToolLocator.Default.Invalidate();
            }
        }

        /// <summary>
        /// <c>ApplyEngineSettings()</c> 真的把设置推给了引擎层：设置里改了优先级，
        /// 运行时设置立刻跟着变（不调 <c>EngineRuntimeSettings.Apply</c> 的话，
        /// 用户在设置窗口排的顺序对真正干活的那个引擎毫无影响）。
        /// </summary>
        [Fact]
        public void 主窗口初始化把优先级推给引擎层()
        {
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            IReadOnlyList<string> previousPriority = EngineRuntimeSettings.EnginePriority;

            try
            {
                string dataRoot = Path.Combine(_root, "data-apply");
                Directory.CreateDirectory(dataRoot);

                var pathService = new PathService { DataRootDirectory = dataRoot };
                var settingsService = new SettingsService(pathService);

                AppSettings settings = AppSettings.CreateDefault();
                settings.CacheRootDirectory = dataRoot;
                settings.EnginePriority = new List<string> { EngineIds.SevenZip, EngineIds.WinRar };
                settings.KeepBrokenFiles = true;

                settingsService.Save(settings);

                _ = new MainViewModel(
                    new FileScanService(),
                    new ArchiveDetectService(),
                    new RenameService(),
                    new SevenZipEngine(),
                    new PasswordService(),
                    new LogService(pathService),
                    settingsService,
                    pathService,
                    new TaskSummaryService(),
                    new ClipboardService(),
                    new DialogService());

                Assert.Equal(new[] { EngineIds.SevenZip, EngineIds.WinRar }, EngineRuntimeSettings.EnginePriority);
                Assert.True(EngineRuntimeSettings.KeepBrokenFiles);
            }
            finally
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
                EngineRuntimeSettings.SetPriority(previousPriority);
                EngineRuntimeSettings.SetKeepBrokenFiles(false);
            }
        }

        // ================================================================ 装配

        private ArchiveTask AddTask(MainViewModel vm, string sourcePath, string format)
        {
            var task = new ArchiveTask(sourcePath, vm.Tasks.Count + 1)
            {
                IsArchive = true,
                DetectedFormat = format,
                ExtensionStatus = StatusText.ExtensionNormal,
                Status = StatusText.Recognized,
                IsSelected = true
            };

            vm.Tasks.Add(task);

            return task;
        }

        private static ArchiveTask Failed(string path, string status, string message) => new(path)
        {
            Status = status,
            ErrorMessage = message,
            IsArchive = true,
            DetectedFormat = "7Z",
            ExtensionStatus = StatusText.ExtensionNormal
        };

        /// <summary>RAR 专用引擎的能力位（白名单：只认 RAR 系列 —— 与 <c>UnRarEngine</c> 一致）。</summary>
        private static EngineCapabilities RarOnly() => new()
        {
            CanList = true,
            CanTest = true,
            CanExtract = true,
            SupportsPassword = true,
            SupportsMultiVolume = true,
            FormatsAreWhitelist = true,
            Formats = new List<EngineFormatCapability>
            {
                new() { Format = "RAR5", CanList = true, CanTest = true, CanExtract = true },
                new() { Format = "RAR4", CanList = true, CanTest = true, CanExtract = true },
                new() { Format = "RAR", CanList = true, CanTest = true, CanExtract = true }
            }
        };

        /// <summary>通用引擎的能力位（不设白名单：没登记的格式也让它去试一把 —— 与 7-Zip 一致）。</summary>
        private static EngineCapabilities General() => new()
        {
            CanList = true,
            CanTest = true,
            CanExtract = true,
            SupportsPassword = true,
            FormatsAreWhitelist = false,
            Formats = new List<EngineFormatCapability>
            {
                new() { Format = "ZIP", CanList = true, CanTest = true, CanExtract = true },
                new() { Format = "7Z", CanList = true, CanTest = true, CanExtract = true },
                new() { Format = "TAR", CanList = true, CanTest = true, CanExtract = true },
                new() { Format = "RAR5", CanList = true, CanTest = true, CanExtract = true }
            }
        };

        /// <summary>
        /// 记调用 + 能真的产出文件的替身引擎。
        /// 列目录默认给一个 1 字节的文件、解压时把它写出来 —— 这样输出校验能通过，
        /// 用例断言的就是"整条流水线真的走完了"，而不是"调用了引擎"。
        /// </summary>
        private sealed class RecordingEngine : IArchiveEngine
        {
            private const string ContentFileName = "content.txt";

            public RecordingEngine(string id, EngineCapabilities capabilities, bool available = true)
            {
                Id = id;
                Capabilities = capabilities;
                IsAvailable = available;
            }

            public List<string> Calls { get; } = new();

            public string Id { get; }

            public string DisplayName => Id + "（替身）";

            public string Version => "9.9-test";

            public bool IsAvailable { get; }

            public EngineCapabilities Capabilities { get; }

            /// <summary>想让列目录失败时替换（默认成功、1 个文件）。</summary>
            public ArchiveListResult? ListResult { get; set; }

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "Unknown" });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                Record("list", request);

                return Task.FromResult(ListResult ?? new ArchiveListResult
                {
                    Success = true,
                    FileCount = 1,
                    TotalUncompressedSize = 1,
                    Entries = new List<ArchiveEntry> { new() { Path = ContentFileName, Size = 1 } },
                    EngineId = Id,
                    EngineVersion = Version
                });
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                Record("test", request);

                return Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero)
                    .StampEngine(Id, DisplayName, Version));
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                Record("extract", request);

                if (!string.IsNullOrWhiteSpace(request.OutputPath))
                {
                    Directory.CreateDirectory(request.OutputPath);
                    File.WriteAllText(Path.Combine(request.OutputPath, ContentFileName), "x");
                }

                return Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero)
                    .StampEngine(Id, DisplayName, Version));
            }

            private void Record(string operation, ArchiveRequest request)
            {
                lock (Calls)
                {
                    Calls.Add($"{operation}:{request.ArchivePath}");
                }
            }
        }

        private static readonly byte[] Rar5Magic = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 };

        private static readonly byte[] ZipMagic = { 0x50, 0x4B, 0x03, 0x04 };

        private static readonly byte[] SevenZipMagic = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };

        /// <summary>最小 TAR 头：512 字节里偏移 257 处放 "ustar"（识别只看这一处）。</summary>
        private static byte[] TarMagic()
        {
            byte[] buffer = new byte[512];

            buffer[257] = (byte)'u';
            buffer[258] = (byte)'s';
            buffer[259] = (byte)'t';
            buffer[260] = (byte)'a';
            buffer[261] = (byte)'r';

            return buffer;
        }

        /// <summary>造一个**文件头是真的**的样本：分派靠魔数，不靠后缀（这个程序的核心前提）。</summary>
        private string CreateMagicFile(string fileName, byte[] magic)
        {
            string directory = Path.Combine(_root, "magic");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, fileName);
            byte[] content = new byte[Math.Max(magic.Length, 1024)];

            magic.CopyTo(content, 0);

            File.WriteAllBytes(path, content);

            return path;
        }
    }
}

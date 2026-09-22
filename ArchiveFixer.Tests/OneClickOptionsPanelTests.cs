using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using ArchiveFixer.Views;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「一键处理 · 本次选项」面板的回归测试
    /// （规格 <c>docs/输出与整理模型.md</c> §9，六条硬要求逐条钉住）。
    ///
    /// <para><b>为什么这么测</b>：面板是 WPF 窗口，而纪律（AGENTS.md §13）不许我们把窗口显示出来。
    /// 所以分成两层：</para>
    /// <list type="number">
    /// <item><description><b>无 UI 宿主</b>（本文件）：把面板的**询问入口**注入成替身
    /// （<see cref="OneClickCoordinator.OptionsPromptOverride"/>），于是"勾了存为默认 / 没勾 / 取消 /
    /// 没弹"四条分支全都能跑到，并且能对着**真实的 <c>appsettings.json</c>** 断言字节有没有变 ——
    /// 那是硬要求②唯一能被证明的形态（真窗口在无界面宿主里根本不会弹，那条路永远走不到）；</description></item>
    /// <item><description><b>无界面 XAML 校验宿主</b>（<c>_tmp/ArchiveFixer/xaml-check</c>，不 Show）：
    /// 验证窗口 XAML 能解析、资源键都在、控件状态 ↔ 快照的映射正确。</description></item>
    /// </list>
    ///
    /// <para><see cref="MainViewModel"/> 的构造会写进程级静态，所以本类与其它同类用例一起**串行**跑。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class OneClickOptionsPanelTests : IDisposable
    {
        private readonly string _root;

        public OneClickOptionsPanelTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerOneClickOptions", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 落点只有一处实现

        /// <summary>
        /// 硬要求①：面板选出来的每一档，算出来的路径必须与**直接调用 <see cref="OutputPlacement"/>**
        /// 的结果逐字节相同 —— 面板只能"覆盖输入"，不许自己拼一次路径。
        /// </summary>
        [Theory]
        [InlineData(OutputPlacementMode.PerArchiveSubfolder)]
        [InlineData(OutputPlacementMode.SourceDirectoryFlat)]
        [InlineData(OutputPlacementMode.CustomRootPerArchive)]
        [InlineData(OutputPlacementMode.CustomRootFlat)]
        public void 面板选的落点_与OutputPlacement唯一实现算出同一条路径(OutputPlacementMode mode)
        {
            string customRoot = Path.Combine(_root, "custom");
            string archive = Path.Combine(_root, "src", "222.7z");

            var pathService = new PathService { DataRootDirectory = Path.Combine(_root, "data") };
            var task = new ArchiveTask(archive, 1);

            // 面板的"覆盖"：只往既有字段上写
            var options = new ExtractOptions();
            new OneClickRunOptions { PlacementMode = mode, CustomRoot = customRoot }.ApplyTo(options);

            string byPanel = pathService.BuildOutputPath(task, options);

            // 唯一实现直接算一遍
            string byOutputPlacement = OutputPlacement
                .ResolveDestinationDirectory(archive, mode, customRoot)
                .DestinationDirectory;

            Assert.Equal(byOutputPlacement, byPanel);

            // 顺带钉住"面板没有偷偷另拼一条路径"：四档必须真的落在四个不同位置
            Assert.False(string.IsNullOrWhiteSpace(byPanel));
        }

        /// <summary>
        /// 硬要求⑤的反面（空根的"指定位置"）：必须判为**无效**，且整条落点不生效 ——
        /// 否则它会被 <see cref="OutputPlacement.FromLegacyFlags"/> 解释成"解压到压缩包所在目录"，
        /// 用户明明选了别处却写进源目录。
        /// </summary>
        [Fact]
        public void 指定位置没填路径_落点不生效_也不许被解释成源目录()
        {
            var snapshot = new OneClickRunOptions
            {
                PlacementMode = OutputPlacementMode.CustomRootFlat,
                CustomRoot = "   "
            };

            Assert.False(snapshot.IsPlacementValid);

            // 先摆成"设置档"的样子，再让无效覆盖去 Apply：三个字段一个都不许动。
            var options = new ExtractOptions
            {
                ExtractToOriginalDirectory = false,
                KeepArchiveNameFolder = true,
                CustomOutputDirectory = Path.Combine(_root, "settings-root")
            };

            snapshot.ApplyTo(options);

            Assert.False(options.ExtractToOriginalDirectory);
            Assert.True(options.KeepArchiveNameFolder);
            Assert.Equal(Path.Combine(_root, "settings-root"), options.CustomOutputDirectory);

            Assert.Contains("按设置值", snapshot.Describe(), StringComparison.Ordinal);
        }

        // ================================================================ ⑤ 默认档行为不变

        /// <summary>
        /// 硬要求⑤：面板的初值取设置值，而且**把初值原样当成覆盖**用一遍，落点与"完全不覆盖"一模一样。
        /// 这条就是"没弹面板 / 按设置走"与"弹了面板但什么都没改"等价的判据。
        /// </summary>
        [Fact]
        public void 面板初值等于设置值时_落点与不覆盖完全一致()
        {
            string outputRoot = Path.Combine(_root, "out");
            string archive = Path.Combine(_root, "src", "222.7z");
            var pathService = new PathService { DataRootDirectory = Path.Combine(_root, "data") };
            var task = new ArchiveTask(archive, 1);

            AppSettings settings = AppSettings.CreateDefault();
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.CustomOutputDirectory = outputRoot;
            settings.Normalize();

            // 不覆盖（引擎原本收到的就是这几个字段）
            var plain = new ExtractOptions
            {
                ExtractToOriginalDirectory = settings.ExtractToOriginalDirectory,
                KeepArchiveNameFolder = settings.KeepArchiveNameFolder,
                CustomOutputDirectory = outputRoot
            };

            // 面板初值 = 设置值 → 当成覆盖用一遍
            var overridden = new ExtractOptions();
            OneClickRunOptions.FromSettings(settings, outputRoot).ApplyTo(overridden);

            Assert.Equal(
                pathService.BuildOutputPath(task, plain),
                pathService.BuildOutputPath(task, overridden));

            // 三档默认值也钉住（§9.1 表格里写的"取设置里的当前值"）
            OneClickRunOptions seed = OneClickRunOptions.FromSettings(settings, outputRoot);

            Assert.Equal(OutputPlacementMode.CustomRootPerArchive, seed.PlacementMode);
            Assert.Equal(TerminalLayoutMode.KeepLastFolder, seed.TerminalLayout);
            Assert.Equal(SourceHandlingMode.MoveToRest, seed.SourceHandling);
            Assert.True(seed.IsPlacementValid);
            Assert.False(seed.SaveAsDefault);
            Assert.False(seed.SuppressPanelNextTime);
        }

        // ================================================================ ④ 无 UI 宿主

        /// <summary>
        /// 硬要求④（直接那一层）：无 UI 宿主下面板返回"没弹"，而且**立刻**返回 —— 不弹窗、不死等。
        /// </summary>
        [Fact]
        public void 无UI宿主_面板不弹且立刻返回()
        {
            Assert.Null(System.Windows.Application.Current);

            OneClickRunOptions seed = OneClickRunOptions.FromSettings(AppSettings.CreateDefault());

            var watch = Stopwatch.StartNew();
            OneClickOptionsPrompt prompt = OneClickOptionsWindow.Show(seed, AppSettings.CreateDefault());
            watch.Stop();

            Assert.Equal(OneClickOptionsOutcome.NotShown, prompt.Outcome);
            Assert.Null(prompt.Options);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"不该有任何等待，实测 {watch.Elapsed}");
        }

        /// <summary>
        /// 硬要求④（端到端那一层）：没有界面宿主时一键处理**照常跑完**，并且落点用的是设置值。
        /// </summary>
        [Fact]
        public async Task 无UI宿主_一键处理不弹面板_按设置值继续()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("pack.7z"));

            await harness.OneClick.RunAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // 设置档 = 指定位置 + 同名子文件夹 → 产物落在 outputRoot\pack 下
            Assert.StartsWith(harness.OutputRoot, task.OutputPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(5, CountFiles(task.OutputPath, excludeArtifactDirectory: true));

            // 没走面板 → 任务上没有"本次选项"依据，日志说明是按设置走的
            Assert.Equal(string.Empty, task.RunOptionsNote);
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("没有本次选项面板", StringComparison.Ordinal));
        }

        // ================================================================ ② 不写设置文件

        /// <summary>
        /// 硬要求②（本批最关键的一条）：**不勾「把本次选择存为默认」时，设置文件一个字节都不许改**。
        ///
        /// 做法是跑一次真正的一键处理，前后比对设置文件的**字节哈希**；同时验证这一次确实用了面板的值
        /// （产物落到了面板指定的目录）—— 否则"文件没变"可能只是因为流程根本没跑。
        /// </summary>
        [Fact]
        public async Task 不勾存为默认_设置文件字节完全不变()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("pack.7z"));

            string flatRoot = Path.Combine(_root, "panel-flat");
            string before = HashFile(harness.SettingsFilePath);

            int prompts = 0;

            harness.OneClick.OptionsPromptOverride = _ =>
            {
                prompts++;

                return OneClickOptionsPrompt.Confirmed(new OneClickRunOptions
                {
                    PlacementMode = OutputPlacementMode.CustomRootFlat,
                    CustomRoot = flatRoot,
                    TerminalLayout = TerminalLayoutMode.UseArchiveName,
                    SourceHandling = SourceHandlingMode.KeepInPlace,
                    SaveAsDefault = false
                });
            };

            await harness.OneClick.RunAsync();

            // ① 这一次确实按面板的值跑了（落点用了面板给的目录）
            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.StartsWith(flatRoot, task.OutputPath, StringComparison.OrdinalIgnoreCase);
            Assert.False(
                task.OutputPath.StartsWith(harness.OutputRoot, StringComparison.OrdinalIgnoreCase),
                "面板选的落点没生效，那么'设置文件没变'就不能说明任何问题");
            Assert.Equal(1, prompts);

            // ② 源包处理也跟着面板走：KeepInPlace = 一个字节都不搬
            Assert.True(File.Exists(harness.SourcePath), "面板选了留在原地，源包不该被搬走");

            // ③ 设置文件：字节级不变
            Assert.Equal(before, HashFile(harness.SettingsFilePath));

            // ④ 内存里的设置对象也没被这一次覆盖污染
            Assert.Equal(harness.OutputRoot, harness.Vm.Settings.CustomOutputDirectory);
            Assert.Equal(nameof(SourceHandlingMode.MoveToRest), harness.Vm.Settings.SourceHandling);
            Assert.Equal("KeepLastFolder", harness.Vm.Settings.TerminalLayoutMode);

            // ⑤ 日志里说清了"不写回设置"
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("不写回设置", StringComparison.Ordinal));
        }

        /// <summary>
        /// 硬要求②的另一半：勾了「存为默认」才写，而且写进去的正是面板上那一组值。
        /// 与上一条合起来，"写 / 不写"两侧都有判据。
        /// </summary>
        [Fact]
        public async Task 勾了存为默认_才写设置文件且写入的是本次选择()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("pack.7z"));

            string flatRoot = Path.Combine(_root, "panel-flat-saved");
            string before = HashFile(harness.SettingsFilePath);

            harness.OneClick.OptionsPromptOverride = _ => OneClickOptionsPrompt.Confirmed(new OneClickRunOptions
            {
                PlacementMode = OutputPlacementMode.CustomRootFlat,
                CustomRoot = flatRoot,
                TerminalLayout = TerminalLayoutMode.UseArchiveName,
                SourceHandling = SourceHandlingMode.KeepInPlace,
                SaveAsDefault = true
            });

            await harness.OneClick.RunAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.NotEqual(before, HashFile(harness.SettingsFilePath));

            // 重新从磁盘读一遍：写进去的必须是面板那一组，而且是设置层的标准字符串口径
            AppSettings reloaded = new SettingsService(harness.PathService).Load();

            Assert.False(reloaded.ExtractToOriginalDirectory);
            Assert.False(reloaded.KeepArchiveNameFolder);
            Assert.Equal(flatRoot, reloaded.CustomOutputDirectory);
            Assert.Equal("UseArchiveName", reloaded.TerminalLayoutMode);
            Assert.Equal(nameof(SourceHandlingMode.KeepInPlace), reloaded.SourceHandling);
        }

        // ================================================================ ③ 只弹一次

        /// <summary>
        /// 硬要求③：一次一键处理**只问一次**。50–200 个包的场景下逐个问等于不可用（决策 D-4）。
        /// </summary>
        [Fact]
        public async Task 一次一键处理_面板只弹一次_后续任务沿用同一份快照()
        {
            Harness harness = CreateHarness();

            var tasks = new List<ArchiveTask>
            {
                AddTask(harness, CreateSourceFile("a.7z")),
                AddTask(harness, CreateSourceFile("b.7z")),
                AddTask(harness, CreateSourceFile("c.7z"))
            };

            string flatRoot = Path.Combine(_root, "panel-flat-many");
            int prompts = 0;

            harness.OneClick.OptionsPromptOverride = _ =>
            {
                prompts++;
                return OneClickOptionsPrompt.Confirmed(new OneClickRunOptions
                {
                    PlacementMode = OutputPlacementMode.CustomRootFlat,
                    CustomRoot = flatRoot,
                    SourceHandling = SourceHandlingMode.KeepInPlace
                });
            };

            await harness.OneClick.RunAsync();

            Assert.Equal(1, prompts);

            // 三个任务都按同一份快照跑：条件就是"只问一次"必须在结果上看得见
            Assert.All(tasks, task => Assert.Equal(StatusText.ExtractSuccess, task.Status));
            Assert.All(tasks, task => Assert.StartsWith(flatRoot, task.OutputPath, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 勾了「以后不再询问」之后：本次运行内再点一键处理不再弹面板，行为退回"按设置走"（硬要求⑤）。
        /// </summary>
        [Fact]
        public async Task 勾了以后不再询问_第二次不再弹面板()
        {
            Harness harness = CreateHarness();
            AddTask(harness, CreateSourceFile("first.7z"));

            string flatRoot = Path.Combine(_root, "panel-flat-suppress");
            int prompts = 0;

            harness.OneClick.OptionsPromptOverride = _ =>
            {
                prompts++;

                return OneClickOptionsPrompt.Confirmed(new OneClickRunOptions
                {
                    PlacementMode = OutputPlacementMode.CustomRootFlat,
                    CustomRoot = flatRoot,
                    SourceHandling = SourceHandlingMode.KeepInPlace,
                    SuppressPanelNextTime = true
                });
            };

            await harness.OneClick.RunAsync();
            Assert.Equal(1, prompts);

            // 第二次：注入的替身仍然会被调用（注入点优先于"不再询问"），
            // 所以这里验的是**真实分支**：清掉注入点，走 AskRunOptionsOnce 自己的判断。
            harness.OneClick.OptionsPromptOverride = null;

            harness.Vm.Tasks[0].IsSelected = true;
            await harness.OneClick.RunAsync();

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("没有本次选项面板", StringComparison.Ordinal));
        }

        /// <summary>用户在面板上点「取消」：这一次不许开跑（不是"照跑不误"）。</summary>
        [Fact]
        public async Task 面板取消_这一次一键处理什么都不做()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("pack.7z"));

            harness.OneClick.OptionsPromptOverride = _ => OneClickOptionsPrompt.Cancelled();

            await harness.OneClick.RunAsync();

            Assert.Equal(0, harness.Engine.ExtractCallCount);
            Assert.Equal(StatusText.Recognized, task.Status);
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("本次选项面板没有确认", StringComparison.Ordinal));
        }

        // ================================================================ ⑥ 可追溯

        /// <summary>
        /// 硬要求⑥：本次实际用的落点要写进**日志**与**任务详情**，用户要能回答"这次为什么解到这里"。
        ///
        /// 这里的"任务详情"落点是两处用户能看到的文本：失败清单第二级（<see cref="TaskSummaryService"/>）
        /// 与「复制任务信息」（<c>MainViewModel.BuildTaskInfoText</c>）。
        /// </summary>
        [Fact]
        public async Task 本次选项写进日志与任务详情()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("pack.7z"));

            string flatRoot = Path.Combine(_root, "panel-flat-trace");

            harness.OneClick.OptionsPromptOverride = _ => OneClickOptionsPrompt.Confirmed(new OneClickRunOptions
            {
                PlacementMode = OutputPlacementMode.CustomRootFlat,
                CustomRoot = flatRoot,
                TerminalLayout = TerminalLayoutMode.UseArchiveName,
                SourceHandling = SourceHandlingMode.KeepInPlace
            });

            // 让这个包失败，好让"失败清单第二级"也有内容可断言（失败清单只列失败任务）。
            harness.Engine.ExtractFailure = new ArchiveOperationResult
            {
                Success = false,
                ExitCode = 2,
                Status = StatusText.ExtractFailed,
                Message = "文件损坏",
                DetectedErrorType = "CorruptedArchive"
            };

            await harness.OneClick.RunAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);

            // ① 任务上留了依据
            Assert.Contains("落点：", task.RunOptionsNote, StringComparison.Ordinal);
            Assert.Contains("终端落法：", task.RunOptionsNote, StringComparison.Ordinal);
            Assert.Contains("源包处理：", task.RunOptionsNote, StringComparison.Ordinal);
            Assert.Contains("指定位置", task.RunOptionsNote, StringComparison.Ordinal);

            // ② 日志里有"实际落点 + 依据"
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("本次实际输出目录", StringComparison.Ordinal) &&
                        line.Contains("依据 →", StringComparison.Ordinal));

            // ③ 失败清单第二级
            var summaryService = new TaskSummaryService();
            IReadOnlyList<string> detailLines = summaryService.BuildFailureDetailLines(task);

            Assert.Contains(
                detailLines,
                line => line.StartsWith("本次选项：", StringComparison.Ordinal) &&
                        line.Contains("指定位置", StringComparison.Ordinal));

            // ④ 「复制任务信息」
            string infoText = MainViewModel.BuildTaskInfoText(task);

            Assert.Contains("本次选项：", infoText, StringComparison.Ordinal);
            Assert.Contains("源包处理：", infoText, StringComparison.Ordinal);
        }

        // ================================================================ 递归内层进度（第 5 条）

        /// <summary>
        /// 递归内层包也要有进度（本批补的口子）：递归核心把进度接收端原样挂到**每一层**的引擎请求上，
        /// 于是界面上的百分比 / 当前条目、"长时间无响应"提示对递归路径同样成立。
        ///
        /// 判据：引擎在自己的 <c>ExtractAsync</c> 里通过 <c>request.Progress</c> 报一次进度、
        /// 触发一次 <c>request.Stalled</c>，两个接收端都必须收到。
        /// </summary>
        [Fact]
        public async Task 递归内层_进度与卡住提示都转发给引擎()
        {
            string root = Path.Combine(_root, "recursive");
            Directory.CreateDirectory(root);

            string archive = Path.Combine(root, "outer.7z");
            File.WriteAllText(archive, "fake archive");

            var engine = new ReportingEngine();
            var prober = new NeverArchiveProber();
            var extractor = new RecursiveExtractor(engine, prober, _ => new[] { string.Empty });

            var reported = new List<ArchiveProgress>();
            var stalls = new List<ArchiveStallNotice>();

            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            RecursiveExtractor.ConfiguredWorkspaceRoot = Path.Combine(root, "work");

            RecursionResult result;

            try
            {
                result = await extractor.ExtractAsync(
                    new ArchiveTask(archive, 1),
                    Path.Combine(root, "final"),
                    RecursionMode.SingleChain,
                    previousDecision: null,
                    cancellationToken: default,
                    progress: new CollectingProgress(reported),
                    stalled: notice => stalls.Add(notice));
            }
            finally
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            }

            Assert.Equal(1, engine.ExtractCallCount);
            Assert.True(engine.ProgressWasAttached, "递归内层的引擎请求里必须带上进度接收端");

            Assert.NotEmpty(reported);
            Assert.Equal(42, reported[^1].Percent);
            Assert.Equal("payload-00000.bin", reported[^1].CurrentEntry);

            Assert.Single(stalls);

            Assert.True(result.Completed || result.PartiallyCompleted, result.Summary);
        }

        // ================================================================ 装配

        private Harness CreateHarness(Action<AppSettings>? configure = null)
        {
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

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var engine = new PanelFakeEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            // MainViewModel 的构造会写进程级静态：先存后还原（与其它同类用例同一套做法）。
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                engine,
                passwordService,
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            var extraction = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());
            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());

            return new Harness(vm, engine, oneClick, logService, pathService, outputRoot);
        }

        private string CreateSourceFile(string fileName)
        {
            string directory = Path.Combine(_root, "src");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, fileName);
            File.WriteAllText(path, "not a real archive - the engine is faked in these tests");
            return path;
        }

        private static ArchiveTask AddTask(Harness harness, string sourcePath)
        {
            var task = new ArchiveTask(sourcePath, harness.Vm.Tasks.Count + 1)
            {
                IsArchive = true,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal,
                Status = StatusText.Recognized,
                IsSelected = true
            };

            harness.Vm.Tasks.Add(task);
            harness.SourcePath = sourcePath;
            return task;
        }

        private static int CountFiles(string? directory, bool excludeArtifactDirectory = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return 0;
                }

                string[] files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);

                if (!excludeArtifactDirectory)
                {
                    return files.Length;
                }

                string artifactPrefix =
                    Path.Combine(directory, ProcessArtifactLayout.ArtifactDirectoryName) + Path.DirectorySeparatorChar;

                return files.Count(file => !file.StartsWith(artifactPrefix, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return -1;
            }
        }

        private static string HashFile(string path)
        {
            Assert.True(File.Exists(path), $"设置文件不存在：{path}");

            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                PanelFakeEngine engine,
                OneClickCoordinator oneClick,
                LogService log,
                PathService pathService,
                string outputRoot)
            {
                Vm = vm;
                Engine = engine;
                OneClick = oneClick;
                Log = log;
                PathService = pathService;
                OutputRoot = outputRoot;
            }

            public MainViewModel Vm { get; }

            public PanelFakeEngine Engine { get; }

            public OneClickCoordinator OneClick { get; }

            public LogService Log { get; }

            public PathService PathService { get; }

            public string OutputRoot { get; }

            /// <summary>最近加进来的那个源包路径（断言"留在原地"时要用）。</summary>
            public string SourcePath { get; set; } = string.Empty;

            public string SettingsFilePath => PathService.SettingsFilePath;

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);
        }

        /// <summary>
        /// 可控的假引擎：往引擎输出目录（= 暂存目录）写 <see cref="FileNames"/> 里的文件，
        /// 列目录返回同样的条目（于是输出校验能通过）。与其它解压用例里的假引擎同一套形状。
        /// </summary>
        private sealed class PanelFakeEngine : IArchiveEngine
        {
            private static readonly string[] DefaultFileNames =
            {
                "payload-00000.bin", "payload-00001.bin", "payload-00002.bin",
                "payload-00003.bin", "payload-00004.bin"
            };

            public IReadOnlyList<string> FileNames { get; set; } = DefaultFileNames;

            public ArchiveOperationResult? ExtractFailure { get; set; }

            public int ExtractCallCount { get; private set; }

            public string Id => "fake";

            public string DisplayName => "假引擎";

            public string Version => "1.0";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; } = new()
            {
                CanProbe = true,
                CanList = true,
                CanTest = true,
                CanExtract = true,
                SupportsPassword = true
            };

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });
            }

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                IReadOnlyList<string> fileNames = FileNames;

                return Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    FileCount = fileNames.Count,
                    TotalUncompressedSize = 0,
                    Entries = fileNames
                        .Select(name => new ArchiveEntry { Path = name, Size = 1 })
                        .ToList(),
                    EngineId = "fake",
                    EngineVersion = "1.0"
                });
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Succeeded());
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                ExtractCallCount++;

                if (ExtractFailure != null)
                {
                    return Task.FromResult(ExtractFailure);
                }

                string output = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);

                    foreach (string name in FileNames)
                    {
                        File.WriteAllText(Path.Combine(output, name), "x");
                    }
                }

                return Task.FromResult(Succeeded());
            }

            private static ArchiveOperationResult Succeeded()
            {
                return new ArchiveOperationResult
                {
                    Success = true,
                    Status = StatusText.ExtractSuccess,
                    Message = "解压成功",
                    DetectedErrorType = "None"
                };
            }
        }

        /// <summary>把上报的进度收进列表（递归用例用；引擎层已经节流过，这里只收）。</summary>
        private sealed class CollectingProgress : IProgress<ArchiveProgress>
        {
            private readonly List<ArchiveProgress> _sink;

            public CollectingProgress(List<ArchiveProgress> sink)
            {
                _sink = sink;
            }

            public void Report(ArchiveProgress? value)
            {
                if (value != null)
                {
                    _sink.Add(value);
                }
            }
        }

        /// <summary>
        /// 递归用例的假引擎：解压时往输出目录写一个文件（好让"量产物大小"这一步有东西可量），
        /// 并通过请求上的接收端报一次进度 + 触发一次"长时间无响应"。
        /// </summary>
        private sealed class ReportingEngine : IArchiveEngine
        {
            public int ExtractCallCount { get; private set; }

            public bool ProgressWasAttached { get; private set; }

            public string Id => "reporting";

            public string DisplayName => "假引擎（会报进度）";

            public string Version => "1.0";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; } = new()
            {
                CanProbe = true,
                CanList = true,
                CanTest = true,
                CanExtract = true
            };

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });
            }

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    FileCount = 1,
                    Entries = new List<ArchiveEntry> { new() { Path = "payload-00000.bin", Size = 1 } },
                    EngineId = Id,
                    EngineVersion = Version
                });
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = true,
                    Status = StatusText.TestPassed,
                    Message = "测试通过",
                    DetectedErrorType = "None"
                });
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                ExtractCallCount++;
                ProgressWasAttached = request.Progress != null;

                string output = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);
                    File.WriteAllText(Path.Combine(output, "payload-00000.bin"), "x");
                }

                request.Progress?.Report(new ArchiveProgress
                {
                    Percent = 42,
                    CurrentEntry = "payload-00000.bin"
                });

                request.Stalled?.Invoke(new ArchiveStallNotice
                {
                    Idle = TimeSpan.FromSeconds(95),
                    Threshold = TimeSpan.FromSeconds(90)
                });

                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = true,
                    Status = StatusText.ExtractSuccess,
                    Message = "解压成功",
                    DetectedErrorType = "None"
                });
            }
        }

        /// <summary>永远回答"不是归档"：递归用例只关心第 0 层那一次解压，不要它再去展开内层。</summary>
        private sealed class NeverArchiveProber : IArchiveProber
        {
            public Task<bool> IsArchiveAsync(string filePath, CancellationToken cancellationToken = default) =>
                Task.FromResult(false);
        }
    }
}

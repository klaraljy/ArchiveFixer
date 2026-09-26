using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 用户 2026-09-24 第 17 条的回归测试：**一键处理只能有一个弹窗，而且确认之前什么都不许动**。
    ///
    /// <para>用户原话：「现在规定点击完一键处理，就只能有一个弹窗提醒，而且这个可以选中以后不弹出……
    /// 弹窗里面的东西改为『内容物会生成在什么地方，而且其余物是否自动删除』……
    /// **为什么我一键处理还没有开始确认，你的进度条就开始动了**」。</para>
    ///
    /// <para><b>那条"进度条先动"的根因</b>（这一组测试钉的就是它）：主界面上那个不确定进度条的可见性绑的是
    /// <c>MainViewModel.IsBusy</c>，而旧代码在弹框**之前**就 <c>EnterBusy()</c> 了 ——
    /// 于是框还挂在屏幕上，进度条已经在转。修法是把询问整段挪到 <c>EnterBusy()</c> 之前，
    /// 下面第一条测试在**框被问的那一刻**读 <see cref="MainViewModel.IsBusy"/> 与引擎调用次数，
    /// 谁把它挪回去都会立刻变红。</para>
    ///
    /// <para>无界面宿主里真窗口不会弹，所以"弹了几个框"用**注入的替身 + 计数**来证明
    /// （与 <c>OneClickOptionsPanelTests</c> 同一套做法；AGENTS.md §13 不许把窗口显示出来）。</para>
    ///
    /// <para><see cref="MainViewModel"/> 的构造会写进程级静态，所以本类与其它同类用例一起**串行**跑。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class OneClickConfirmTests : IDisposable
    {
        private const long Gib = 1024L * 1024 * 1024;

        private readonly string _root;

        public OneClickConfirmTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerOneClickConfirm", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 确认之前什么都不许动

        /// <summary>
        /// 第 17 条的核心：**框被问的那一刻**，程序必须还处在"没动手"的状态 ——
        /// 不进忙碌（进度条因此不动）、引擎一次都没调用、任务状态还是"已识别"。
        /// </summary>
        [Fact]
        public async Task 确认框被问时_没有进入忙碌_进度条不会动()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("pack.7z"));

            bool busyAtPrompt = true;
            int engineCallsAtPrompt = -1;
            string statusAtPrompt = string.Empty;

            harness.OneClick.OptionsPromptOverride = _ =>
            {
                busyAtPrompt = harness.Vm.IsBusy;
                engineCallsAtPrompt = harness.Engine.ExtractCallCount;
                statusAtPrompt = task.Status;

                return OneClickOptionsPrompt.Confirmed(OneClickRunOptions.FromSettings(
                    harness.Vm.Settings,
                    harness.OutputRoot));
            };

            await harness.OneClick.RunAsync();

            Assert.False(busyAtPrompt, "确认框还挂着的时候就不许进忙碌 —— 进度条绑的就是 IsBusy（用户第 17 条）");
            Assert.Equal(0, engineCallsAtPrompt);
            Assert.Equal(StatusText.Recognized, statusAtPrompt);

            // 确认之后才真的开跑，而且跑完回到"不忙"（进度条要能停下来）
            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.False(harness.Vm.IsBusy);
        }

        /// <summary>取消之后同样是"一个字节都没动"：不进忙碌、不调引擎、状态不变。</summary>
        [Fact]
        public async Task 在确认框上取消_什么都没开始()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("pack.7z"));

            harness.OneClick.OptionsPromptOverride = _ => OneClickOptionsPrompt.Cancelled();

            await harness.OneClick.RunAsync();

            Assert.Equal(0, harness.Engine.ExtractCallCount);
            Assert.Equal(StatusText.Recognized, task.Status);
            Assert.False(harness.Vm.IsBusy);
            Assert.Empty(Directory.GetDirectories(harness.OutputRoot));
        }

        // ================================================================ ② 正文说的是真话

        /// <summary>
        /// 正文第一行必须就是**真实落点**：拿确认框算出来的那一行与跑完之后任务真正的输出目录逐字比对。
        /// 这一条挡住的是"预告一套、实际落另一套"。
        /// </summary>
        [Fact]
        public async Task 正文的内容物落点_与跑完之后真实输出目录一致()
        {
            Harness harness = CreateHarness();

            string customRoot = Path.Combine(_root, "BBB");
            Directory.CreateDirectory(customRoot);

            ArchiveTask task = AddTask(harness, CreateSourceFile("222.7z"));

            OneClickConfirmFacts? facts = null;

            harness.OneClick.OptionsPromptOverride = _ =>
            {
                facts = harness.Extraction
                    .BuildConfirmFactsAsync(new[] { task }, OneClickRunOptions.FromSettings(harness.Vm.Settings, customRoot), null)
                    .GetAwaiter()
                    .GetResult();

                return OneClickOptionsPrompt.Confirmed(new OneClickRunOptions
                {
                    PlacementMode = OutputPlacementMode.CustomRootPerArchive,
                    CustomRoot = customRoot
                });
            };

            await harness.OneClick.RunAsync();

            Assert.NotNull(facts);
            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // 单包时正文就是那一条落点路径（"222.7z → BBB\222"）
            Assert.Equal(task.OutputPath, facts!.DestinationEcho);

            // 另外两行也在，而且说的是设置里的档位
            Assert.Contains("其余物", facts.RestEcho, StringComparison.Ordinal);
            Assert.Contains("源包", facts.SourceEcho, StringComparison.Ordinal);
        }

        /// <summary>
        /// 第 33 条：「其余物」那一行**跟着本次选项走**，不是照着设置念 ——
        /// 用户在确认框折叠区里把「删除操作」改成彻底删除之后，正文必须说"会自动彻底删除"；
        /// 没改（默认档）时说"不自动删除"。
        ///
        /// <para>为什么必须钉：正文那行原来只读设置，于是"弹窗里改了这一档"与"正文说的"会打架，
        /// 而用户正是靠这一行决定"要不要按开始处理"（用户 2026-09-25：
        /// "一键处理的弹窗也是要随着现在的设置进行更新的"）。</para>
        /// </summary>
        [Fact]
        public async Task 正文的其余物那一行_跟着本次选项里的删除操作档()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("222.7z"));

            // 设置里是默认档（不动其余物）：正文照它说。
            OneClickConfirmFacts bySettings = await harness.Extraction.BuildConfirmFactsAsync(
                new[] { task },
                OneClickRunOptions.FromSettings(harness.Vm.Settings),
                null);

            Assert.Contains("不自动删除", bySettings.RestEcho, StringComparison.Ordinal);

            // 本次选项改成"彻底删除"：正文必须跟着改口。
            var chosen = new OneClickRunOptions
            {
                PlacementMode = OutputPlacementMode.PerArchiveSubfolder,
                TerminalLayout = TerminalLayoutMode.KeepLastFolder,
                RestHandling = RestHandlingModes.Delete
            };

            OneClickConfirmFacts byChoice = await harness.Extraction.BuildConfirmFactsAsync(
                new[] { task },
                chosen,
                null);

            Assert.Contains("自动彻底删除", byChoice.RestEcho, StringComparison.Ordinal);
            Assert.DoesNotContain("不自动删除", byChoice.RestEcho, StringComparison.Ordinal);
        }

        /// <summary>
        /// 落点算不出来时（选了"指定位置"却给了一个**相对路径**），正文**如实说明原因**，
        /// 绝不编一个看起来正常的假路径让用户放心。
        ///
        /// <para>⚠ 刻意用相对路径而不是空路径来造这个现场：空路径会被落点实现按"没指定位置"处理
        /// （回落到源目录家族），那是一条**合法**的结果，不是"算不出来"。
        /// 真正算不出来的四类现场见 <c>OutputPlacementError</c>。</para>
        /// </summary>
        [Fact]
        public async Task 落点算不出来时_正文如实说明原因()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("pack.7z"));

            var facts = await harness.Extraction.BuildConfirmFactsAsync(
                new[] { task },
                new OneClickRunOptions
                {
                    PlacementMode = OutputPlacementMode.CustomRootPerArchive,
                    CustomRoot = @"out\sub"
                },
                null);

            Assert.Contains("暂时算不出来", facts.DestinationEcho, StringComparison.Ordinal);
            Assert.Contains("必须是绝对路径", facts.DestinationEcho, StringComparison.Ordinal);
        }

        /// <summary>
        /// 「其余物」这一行必须**跟着③页「删除操作」那一档说**（用户 2026-09-25 第 32 条定了三档）：
        /// 不动其余物 = 说不自动删除；移入回收站 / 彻底删除 = 如实说会自动处理（而且两档措辞不同）。
        /// </summary>
        [Theory]
        [InlineData(RestHandlingModes.Keep, StatusText.OneClickConfirmRestKeep)]
        [InlineData(RestHandlingModes.RecycleBin, StatusText.OneClickConfirmRestRecycle)]
        [InlineData(RestHandlingModes.Delete, StatusText.OneClickConfirmRestAutoDelete)]
        public async Task 其余物一行_按删除操作那一档说(string mode, string expected)
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.RestHandlingAfterVerify = mode;
            });

            ArchiveTask task = AddTask(harness, CreateSourceFile("pack.7z"));

            var facts = await harness.Extraction.BuildConfirmFactsAsync(
                new[] { task },
                OneClickRunOptions.FromSettings(harness.Vm.Settings),
                null);

            Assert.Contains(expected, facts.RestEcho, StringComparison.Ordinal);

            // 三档措辞互不相同：说错档 = 用户按错的预期动手。
            foreach (string other in new[]
                     {
                         StatusText.OneClickConfirmRestKeep,
                         StatusText.OneClickConfirmRestRecycle,
                         StatusText.OneClickConfirmRestAutoDelete
                     })
            {
                if (!string.Equals(other, expected, StringComparison.Ordinal))
                {
                    Assert.DoesNotContain(other, facts.RestEcho, StringComparison.Ordinal);
                }
            }
        }

        // ================================================================ ③ 只能有一个弹窗

        /// <summary>
        /// **需要改名的包也不再弹第二个框**（用户 2026-09-25 第 32 条）：
        /// "这个一键处理自己会自动改名自动解压，为什么遇到这种改名的压缩包还要我两次确认，
        /// 这个对用户来说是完全多余的……那种情况只有手动档才会有"。
        ///
        /// <para>所以一键档：改名**自动执行**（逐条写日志），整批**只弹那一个确认框**；
        /// 手动档（②页/右键那几条改名命令）照旧先出预览 —— 这一条只管一键档。</para>
        /// </summary>
        [Fact]
        public async Task 需要改后缀时_一键处理只弹一个框并自动改名()
        {
            Harness harness = CreateHarness();

            string disguised = CreateSourceFile("2222.jpg");
            ArchiveTask task = AddTask(harness, disguised);

            // 与识别阶段给的结论一致：后缀不匹配（jpg 里其实是 7z）→ 属于"需要改名"。
            task.ExtensionStatus = StatusText.ExtensionMismatch;
            task.DetectedFormat = "7Z";
            task.SuggestedExtension = ".7z";

            int prompts = 0;

            harness.OneClick.OptionsPromptOverride = _ =>
            {
                prompts++;

                return OneClickOptionsPrompt.Confirmed(
                    OneClickRunOptions.FromSettings(harness.Vm.Settings, harness.OutputRoot));
            };

            await harness.OneClick.RunAsync();

            Assert.Equal(1, prompts);

            string renamed = Path.Combine(Path.GetDirectoryName(disguised)!, "2222.7z");

            Assert.False(File.Exists(disguised), "伪装后缀必须被自动改掉");

            /*
             * 改名后的文件这会儿可能已经不在源目录里了：本批的源包处理档是「移入其余物」，
             * 解压成功 + 校验通过之后它会被搬走（那是另一条既有规则，与本次改动无关）。
             * 所以这里按"整棵树里找得到那个新名字"来断言，并把源目录为空当作正常。
             */
            string[] renamedAnywhere = Directory.GetFiles(_root, "2222.7z", SearchOption.AllDirectories);

            Assert.True(
                renamedAnywhere.Length == 1,
                $"改名后的 2222.7z 应该正好有一份（可能被搬进其余物），实际 {renamedAnywhere.Length} 份："
                + string.Join(" | ", renamedAnywhere));

            Assert.Empty(Directory.GetFiles(_root, "2222.jpg", SearchOption.AllDirectories));
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("自动修正后缀", StringComparison.Ordinal));
        }

        // ================================================================ ④ 其余物会怎么处理，当场说清

        /// <summary>
        /// 删除操作 = 不动其余物（默认档）时，批首必须**明说**会保留（用户第 32 条：
        /// 他设过"其余物彻底删除"，结果其余物还在，而日志里一声不响 —— 静默正是问题本身）。
        /// </summary>
        [Fact]
        public async Task 删除操作不动其余物_批首要说清会保留()
        {
            Harness harness = CreateHarness();
            AddTask(harness, CreateSourceFile("pack.7z"));

            harness.OneClick.OptionsPromptOverride = _ => OneClickOptionsPrompt.Confirmed(
                OneClickRunOptions.FromSettings(harness.Vm.Settings, harness.OutputRoot));

            await harness.OneClick.RunAsync();

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("其余物：本批保留", StringComparison.Ordinal)
                        && line.Contains("删除操作", StringComparison.Ordinal));
        }

        /// <summary>
        /// 删除操作 = 彻底删除时，批首同样要说清（这次说的是"会彻底删除"，并写明失败/取消不删）。
        /// </summary>
        [Fact]
        public async Task 删除操作彻底删除_批首要说清会彻底删()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
            });

            AddTask(harness, CreateSourceFile("pack.7z"));

            harness.OneClick.OptionsPromptOverride = _ => OneClickOptionsPrompt.Confirmed(
                OneClickRunOptions.FromSettings(harness.Vm.Settings, harness.OutputRoot));

            await harness.OneClick.RunAsync();

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("其余物：本批任务成功后会彻底删除", StringComparison.Ordinal));
        }

        /// <summary>
        /// 一键处理这条路**只允许一个弹窗**：无用物 / 没有可用密码的提醒并进确认框的正文，
        /// 解压前那个提醒框（<see cref="ExtractionCoordinator.ReminderAnswerOverride"/> 那条路）
        /// **一次都不许被问到**。判定与日志一条都没少（这里同时钉住日志那两行）。
        /// </summary>
        [Fact]
        public async Task 无用物提醒并进确认框_解压前不再弹第二个框()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("pack.7z"));

            // 源目录里放一个"打包者常带的那种文件"：判定为无用物的唯一入口（名字 + 魔数认不出是包）。
            string junkPath = Path.Combine(Path.GetDirectoryName(task.CurrentPath)!, "说明.txt");
            File.WriteAllText(junkPath, "这是打包者附带的说明，不是压缩包");

            int reminderCalls = 0;

            harness.Extraction.ReminderAnswerOverride = _ =>
            {
                reminderCalls++;

                return new ExtractionCoordinator.ReminderAnswer { Confirmed = true };
            };

            OneClickConfirmFacts? facts = null;

            harness.OneClick.OptionsPromptOverride = _ =>
            {
                // 真实路径就是这样：先扫两段事实（无用物 / 没有可用密码），再把它写进正文。
                ExtractionCoordinator.BatchReminderFacts reminders = harness.Extraction
                    .ScanBatchRemindersAsync(new[] { task })
                    .GetAwaiter()
                    .GetResult();

                facts = harness.Extraction
                    .BuildConfirmFactsAsync(new[] { task }, OneClickRunOptions.FromSettings(harness.Vm.Settings), reminders)
                    .GetAwaiter()
                    .GetResult();

                return OneClickOptionsPrompt.Confirmed(OneClickRunOptions.FromSettings(
                    harness.Vm.Settings,
                    harness.OutputRoot));
            };

            await harness.OneClick.RunAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // ① 第二个框一次都没弹
            Assert.Equal(0, reminderCalls);

            // ② 事实还在日志里（"少的只是弹窗"不是"少的还有判定"）
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains(StatusText.BatchReminderMergedLog, StringComparison.Ordinal));
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("解压前提醒：", StringComparison.Ordinal)
                        && line.Contains("说明.txt", StringComparison.Ordinal));

            // ③ 那一段也真的写进了正文（用户在最上面那个框里就该看到）
            Assert.NotNull(facts);
            Assert.Contains("说明.txt", facts!.NoticeEcho, StringComparison.Ordinal);

            // ④ 程序对无用物一个都不动
            Assert.True(File.Exists(junkPath), "无用物只是提醒，程序不许删/改/搬");
        }

        /// <summary>
        /// 需要密码、却一个可用候选都没有的包，也要在**同一个**确认框里说一声（旧版是第二个框）。
        /// </summary>
        [Fact]
        public async Task 没有可用密码的包_在确认框正文里有一行()
        {
            Harness harness = CreateHarness();

            ArchiveTask task = AddTask(harness, CreateSourceFile("locked.7z"));
            task.IsEncrypted = true;

            // 走真实顺序：先扫（无用物 + 无可用密码），再把结论写进正文。
            ExtractionCoordinator.BatchReminderFacts reminders = await harness.Extraction
                .ScanBatchRemindersAsync(new[] { task });

            var facts = await harness.Extraction.BuildConfirmFactsAsync(
                new[] { task },
                OneClickRunOptions.FromSettings(harness.Vm.Settings),
                reminders);

            Assert.Contains("没有可用密码", facts.NoticeEcho, StringComparison.Ordinal);
            Assert.Contains("locked.7z", facts.NoticeEcho, StringComparison.Ordinal);

            // 反例：不加密的包不该出现在这一段里（否则每批都要多一行噪声）
            task.IsEncrypted = false;

            ExtractionCoordinator.BatchReminderFacts quietReminders = await harness.Extraction
                .ScanBatchRemindersAsync(new[] { task });

            var quiet = await harness.Extraction.BuildConfirmFactsAsync(
                new[] { task },
                OneClickRunOptions.FromSettings(harness.Vm.Settings),
                quietReminders);

            Assert.DoesNotContain("没有可用密码", quiet.NoticeEcho, StringComparison.Ordinal);
        }

        // ================================================================ ④ 「继续解」（第 16 条追加）

        /// <summary>
        /// 撞到层数上限之后，界面上要出现「继续解（还有 N 个内层包）」并可以点；
        /// 新的一批一开始就把它清掉（结论按本次重设），免得上一批的提示赖在那儿。
        /// </summary>
        [Fact]
        public async Task 继续解_跟着上一批的结论出现_新的一批开始时清掉()
        {
            Harness harness = CreateHarness();
            AddTask(harness, CreateSourceFile("pack.7z"));

            // "上一批撞到上限、还剩 3 个内层包"这件事由 OneClickOutcome 报进来（这里直接喂同样的值）。
            harness.Vm.ReportPendingContinuation(3);

            Assert.True(harness.Vm.HasPendingContinuation);
            Assert.Contains("3", harness.Vm.ContinueOneClickButtonText, StringComparison.Ordinal);
            Assert.Contains(
                harness.OneClick.RoundLimit.ToString(System.Globalization.CultureInfo.CurrentCulture),
                harness.Vm.PendingContinuationText,
                StringComparison.Ordinal);
            Assert.True(harness.Vm.ContinueOneClickCommand.CanExecute(null));

            harness.OneClick.OptionsPromptOverride = _ => OneClickOptionsPrompt.Confirmed(
                OneClickRunOptions.FromSettings(harness.Vm.Settings, harness.OutputRoot));

            await harness.OneClick.RunAsync();

            // 这一批没有撞上限 → 提示清掉、按钮点不动
            Assert.False(harness.Vm.HasPendingContinuation);
            Assert.False(harness.Vm.ContinueOneClickCommand.CanExecute(null));
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

            var engine = new ConfirmFakeEngine();
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

            return new Harness(vm, engine, oneClick, extraction, logService, outputRoot);
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
            return task;
        }


        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                ConfirmFakeEngine engine,
                OneClickCoordinator oneClick,
                ExtractionCoordinator extraction,
                LogService log,
                string outputRoot)
            {
                Vm = vm;
                Engine = engine;
                OneClick = oneClick;
                Extraction = extraction;
                Log = log;
                OutputRoot = outputRoot;
            }

            public MainViewModel Vm { get; }

            public ConfirmFakeEngine Engine { get; }

            public OneClickCoordinator OneClick { get; }

            public ExtractionCoordinator Extraction { get; }

            public LogService Log { get; }

            public string OutputRoot { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);
        }

        /// <summary>
        /// 可控的假引擎：往引擎输出目录写 5 个文件、列目录返回同样的条目（于是输出校验能通过）。
        /// 与其它解压用例里的假引擎同一套形状。
        /// </summary>
        private sealed class ConfirmFakeEngine : IArchiveEngine
        {
            private static readonly string[] DefaultFileNames =
            {
                "payload-00000.bin", "payload-00001.bin", "payload-00002.bin",
                "payload-00003.bin", "payload-00004.bin"
            };

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
                IReadOnlyList<string> fileNames = DefaultFileNames;

                return Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    FileCount = fileNames.Count,
                    TotalUncompressedSize = 0,
                    Entries = fileNames
                        .Select(name => new ArchiveEntry { Path = name, Size = 1 })
                        .ToList(),
                    EngineId = Id,
                    EngineVersion = Version
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

                string output = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);

                    foreach (string name in DefaultFileNames)
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
    }
}

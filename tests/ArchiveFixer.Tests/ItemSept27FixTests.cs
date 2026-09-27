using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 2026-09-27 真机第二轮六条问题的回归钉子（用户原话见 <c>修改日志.md</c> 那一行）。
    ///
    /// <list type="number">
    /// <item><description>列表要显示选中文件的大小（排在文件名后面）+ 勾选合计；</description></item>
    /// <item><description>「移除勾选的」在跑批时点不动，必须能读出为什么；</description></item>
    /// <item><description>日志里"其余物会删什么"必须跟着**源包那一档**说，且术语统一（其余物 = 过程物 + 原包）；</description></item>
    /// <item><description>「停止后续」之后不许再试新的密码候选（在候选之间停）；</description></item>
    /// <item><description>不加密的包 / 连续候选结果相同 → 不许把结论写成「密码错误」，也不许继续烧候选；</description></item>
    /// <item><description>帮助 → 使用说明找不到文档时要列出找过的位置；docs 随程序一起发。</description></item>
    /// </list>
    ///
    /// <para>本文件里**不需要真 7z**：校验口径、术语表、列表列、命令可用性都是纯逻辑；
    /// 候选循环那三条在 <c>ExtractionPipelineFixTests</c> 里（那里已有假引擎与管线夹具）。</para>
    /// </summary>
    public sealed class ItemSept27FixTests : IDisposable
    {
        private readonly string _root;

        public ItemSept27FixTests()
        {
            _root = CreateIsolatedRoot();
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
                // 临时目录删不掉不影响断言结果。
            }
        }

        // ================================================================ 第 5 条：校验口径

        /// <summary>
        /// 归档里"仅大小写不同"的同名条目：预期数必须按**去重后**算。
        ///
        /// <para>真机铁证：`rar-android-722.132.apk` 清单 1271 条、其中 **32 组仅大小写不同**（65 条），
        /// Windows 不区分大小写 → 只能落地 1238 个；旧口径判"少 33 个文件"，于是把 10 个密码候选白试一遍
        /// 还报「密码错误」。复现实测：按"每组取**最后一条**"折算，字节数正好等于盘上的 13,543,946。</para>
        /// </summary>
        [Fact]
        public void 校验口径_仅大小写不同的同名条目按最后一条折算_不再判不完整()
        {
            string output = Path.Combine(_root, "out-collide");
            Directory.CreateDirectory(output);

            // 盘上能落地的就是去重后的三份：后写的那条赢（9n.9.png = 20 字节）
            File.WriteAllBytes(Path.Combine(output, "9n.9.png"), new byte[20]);
            File.WriteAllBytes(Path.Combine(output, "a.png"), new byte[30]);
            File.WriteAllBytes(Path.Combine(output, "readme.txt"), new byte[40]);

            OutputVerificationResult result = OutputVerifier.Verify(output, CaseCollisionList());

            Assert.True(result.Verified, "仅大小写不同的条目在 Windows 上注定落不全，不该判成「解压不完整」：" + result.Message);
            Assert.Equal(3, result.ExpectedFileCount);
            Assert.Equal(90, result.ExpectedTotalSize);
            Assert.Equal(1, result.CaseOnlyDuplicateGroups);
            Assert.Equal(1, result.CaseOnlyDroppedEntries);
            Assert.Contains("仅大小写不同", result.Message, StringComparison.Ordinal);
        }

        /// <summary>反向钉子：**真少文件**时必须照样判不通过（放宽的只是"物理上放不下"那一档，不变量 6 不变）。</summary>
        [Fact]
        public void 校验口径_真缺文件时照样判不通过()
        {
            string output = Path.Combine(_root, "out-missing");
            Directory.CreateDirectory(output);

            File.WriteAllBytes(Path.Combine(output, "9n.9.png"), new byte[20]);
            File.WriteAllBytes(Path.Combine(output, "a.png"), new byte[30]);

            // readme.txt 没解出来 → 少一个文件、少 40 字节
            OutputVerificationResult result = OutputVerifier.Verify(output, CaseCollisionList());

            Assert.False(result.Verified, "真缺文件必须判不通过（它是删源包与成功结论的前置门槛）");
            Assert.Equal(3, result.ExpectedFileCount);
            Assert.Equal(2, result.ActualFileCount);
        }

        /// <summary>没有冲突的包：折算结果与引擎给的两个总数**逐字相同**（⛔ 不许顺手改动老口径）。</summary>
        [Fact]
        public void 校验口径_没有大小写冲突时沿用引擎给的总数()
        {
            var list = new ArchiveListResult
            {
                Success = true,
                FileCount = 2,
                TotalUncompressedSize = 5,
                Entries = new List<ArchiveEntry>
                {
                    new() { Path = "a.bin", Size = 2 },
                    new() { Path = "b.bin", Size = 3 }
                }
            };

            (int count, long size, int groups, int dropped) = OutputVerifier.CollapseCaseOnlyDuplicates(list);

            Assert.Equal(2, count);
            Assert.Equal(5, size);
            Assert.Equal(0, groups);
            Assert.Equal(0, dropped);
        }

        private static ArchiveListResult CaseCollisionList() => new()
        {
            Success = true,
            FileCount = 4,
            TotalUncompressedSize = 100,   // 故意写一个"清单口径"的数：折算后必须换成 90
            Entries = new List<ArchiveEntry>
            {
                new() { Path = "res/9N.9.png", Size = 10 },
                new() { Path = "res/9n.9.png", Size = 20 },   // 与上一条仅大小写不同 → 后写的赢
                new() { Path = "res/a.png", Size = 30 },
                new() { Path = "readme.txt", Size = 40 }
            },
            EngineId = "fake",
            EngineVersion = "1.0"
        };

        // ================================================================ 第 3 条：其余物范围 + 说明

        /// <summary>
        /// 「其余物会删什么」必须**跟着源包那一档**说（真机：他选了"源包留在原地"，
        /// 日志却写"彻底删除（源包 + 过程物）"，两句自相矛盾）。
        /// </summary>
        [Fact]
        public void 其余物范围_源包留在原地时不含源包_放入其余物时才含()
        {
            string keep = ExtractionCoordinator.DescribeRestScope(SourceHandlingMode.KeepInPlace);
            string move = ExtractionCoordinator.DescribeRestScope(SourceHandlingMode.MoveToRest);

            Assert.Contains("源包留在原地", keep, StringComparison.Ordinal);
            Assert.Contains("过程物", keep, StringComparison.Ordinal);
            Assert.DoesNotContain("源包 + 过程物", keep, StringComparison.Ordinal);

            Assert.Contains("源包 + 过程物", move, StringComparison.Ordinal);
        }

        /// <summary>
        /// 术语表必须齐全、每条都要有像样的解释，而且**其余物 = 过程物 + 原包**这条口径写死在里面
        /// （用户 2026-09-27 亲自定的）。
        /// </summary>
        [Fact]
        public void 说明内容_术语表齐全_其余物等于过程物加原包()
        {
            IReadOnlyList<HelpTerm> terms = HelpContent.Glossary;

            Assert.True(terms.Count >= 15, $"术语表太薄（{terms.Count} 条）—— 用户点名要「很多专有名词的解释」");

            foreach (HelpTerm term in terms)
            {
                Assert.False(string.IsNullOrWhiteSpace(term.Term));
                Assert.True(term.Explanation.Length >= 15, $"术语「{term.Term}」的解释太短，等于没说");
            }

            foreach (string required in new[] { "其余物", "过程物", "原包（源包）", "无用物", "分卷", "内嵌归档（双面文件）" })
            {
                Assert.Contains(terms, term => term.Term == required);
            }

            HelpTerm rest = terms.First(term => term.Term == "其余物");

            Assert.Contains("原包 + 过程物", rest.Explanation, StringComparison.Ordinal);
            Assert.Contains("其余物里就只有过程物", rest.Explanation, StringComparison.Ordinal);
        }

        /// <summary>说明窗**不依赖任何外部文件**（帮助菜单那份「使用说明」要读 docs\，分发包里可能没有）。</summary>
        [Fact]
        public void 说明窗_内容全部来自HelpContent_不读文件()
        {
            string xaml = Read("Views", "HelpWindow.xaml");
            string code = Read("Views", "HelpWindow.xaml.cs");

            Assert.Contains("HelpContent.Tagline", xaml, StringComparison.Ordinal);
            Assert.Contains("HelpContent.Introduction", xaml, StringComparison.Ordinal);
            Assert.Contains("HelpContent.Glossary", xaml, StringComparison.Ordinal);

            Assert.DoesNotContain("File.ReadAllText", code, StringComparison.Ordinal);
            Assert.DoesNotContain("File.Exists", code, StringComparison.Ordinal);
        }

        /// <summary>「说明」按钮在**选项卡那一行的最右端**（用户：在设置右边），并且退到程序自带的内容。</summary>
        [Fact]
        public void 说明入口_在选项卡那一行最右端()
        {
            string window = Read("MainWindow.xaml");

            int templateStart = window.IndexOf("<ControlTemplate TargetType=\"TabControl\">", StringComparison.Ordinal);
            int firstTabItem = window.IndexOf("<TabItem", StringComparison.Ordinal);
            int helpButton = window.IndexOf("Click=\"HelpMenuItem_Click\"", StringComparison.Ordinal);

            // Dock 属性写在按钮自己的标签上（在 Click 之前那一行），所以往回找。
            int dockRight = window.LastIndexOf("DockPanel.Dock=\"Right\"", helpButton, StringComparison.Ordinal);

            Assert.True(templateStart > 0, "找不到 TabControl 的模板（「说明」按设计就挂在这一行的模板里）");
            Assert.True(helpButton > templateStart, "「说明」按钮必须在选项卡那一行的模板里");
            Assert.True(firstTabItem > helpButton, "「说明」必须写在第一个 TabItem **之前**（它属于那一行的模板）");

            /*
             * 位置靠 Dock="Right" 保证 —— ⛔ 不能拿"文本顺序在⑥设置后面"当判据：
             * 模板写在 TabItem 之前，文本顺序天然更靠前，而**渲染出来的位置**由 Dock 决定。
             */
            Assert.True(dockRight > templateStart, "「说明」必须以 DockPanel.Dock=\"Right\" 贴在那一行的最右端");
            Assert.Contains("HelpContent", Read("Models", "HelpContent.cs"), StringComparison.Ordinal);
        }

        // ================================================================ 第 1 条：大小列 + 勾选合计

        /// <summary>「大小」列必须**紧跟在「文件名」后面**，并且勾选那一排要有合计。</summary>
        [Fact]
        public void 列表大小列_紧跟文件名_并带勾选合计()
        {
            string taskTab = Read("Views", "Tabs", "TaskTab.xaml");

            int name = taskTab.IndexOf("Header=\"文件名\"", StringComparison.Ordinal);
            int size = taskTab.IndexOf("Header=\"大小\"", StringComparison.Ordinal);
            int extension = taskTab.IndexOf("Header=\"当前后缀\"", StringComparison.Ordinal);

            Assert.True(name > 0 && size > name, "「大小」列必须在「文件名」后面（用户点名要的位置）");
            Assert.True(extension > size, "「大小」必须紧跟在「文件名」后面，别被别的列插进来");

            Assert.Contains("SourceSizeText", taskTab, StringComparison.Ordinal);
            Assert.Contains("SelectedTasksSummary", taskTab, StringComparison.Ordinal);
        }

        /// <summary>源文件大小采集：读得到就是真实字节数，读不到显示「-」（⛔ 不显示 0 字节骗人）。</summary>
        [Fact]
        public void 源文件大小_读得到就显示_读不到显示短横()
        {
            string file = Path.Combine(_root, "sample.bin");
            File.WriteAllBytes(file, new byte[2048]);

            var task = new ArchiveTask(file, 1);

            Assert.Equal(2048, task.SourceSizeBytes);
            Assert.Equal("2 KiB", task.SourceSizeText);

            var missing = new ArchiveTask(Path.Combine(_root, "nope.bin"), 2);

            Assert.Equal(0, missing.SourceSizeBytes);
            Assert.Equal("-", missing.SourceSizeText);
        }

        /// <summary>勾选合计跟着勾选变（用户在列表上勾谁，就看得到"勾了几个、一共多大"）。</summary>
        [Fact]
        public void 勾选合计_跟着勾选变()
        {
            MainViewModel vm = CreateViewModel();

            string first = Path.Combine(_root, "a.bin");
            string second = Path.Combine(_root, "b.bin");
            File.WriteAllBytes(first, new byte[1024]);
            File.WriteAllBytes(second, new byte[1024 * 1024]);

            vm.Tasks.Add(new ArchiveTask(first, 1) { IsSelected = false });
            vm.Tasks.Add(new ArchiveTask(second, 2) { IsSelected = false });

            Assert.Contains("没有勾选任何任务", vm.SelectedTasksSummary, StringComparison.Ordinal);

            vm.Tasks[0].IsSelected = true;

            Assert.Contains("已勾选 1 项", vm.SelectedTasksSummary, StringComparison.Ordinal);
            Assert.Contains("1 KiB", vm.SelectedTasksSummary, StringComparison.Ordinal);

            vm.Tasks[1].IsSelected = true;

            Assert.Contains("已勾选 2 项", vm.SelectedTasksSummary, StringComparison.Ordinal);
            Assert.Contains("MiB", vm.SelectedTasksSummary, StringComparison.Ordinal);   // 1 KiB + 1 MiB → 合计按 MiB 档报
        }

        // ================================================================ 落点模型 v2：手动档「解压到当前文件夹」

        /// <summary>
        /// ①页「手动操作」里必须有那颗按钮，而且它绑的是**它自己**的命令
        /// （⛔ 不许偷偷复用「只解压」—— 两者落点不同，共用一个命令就是"点了没反应 / 落错地方"）。
        ///
        /// <para>同时钉住三条接线：文案来自 <see cref="StatusText.ExtractIntoSourceFolderText"/>、
        /// 一键处理那条路**没有**这个命令（批量永不摊平）、命令在忙时点不动（与「只解压」同一道守卫）。</para>
        /// </summary>
        [Fact]
        public void 手动操作_解压到当前文件夹_有独立按钮与命令()
        {
            string taskTab = Read("Views", "Tabs", "TaskTab.xaml");

            Assert.Contains("ExtractIntoSourceFolderCommand", taskTab, StringComparison.Ordinal);
            Assert.Contains("StatusText.ExtractIntoSourceFolderText", taskTab, StringComparison.Ordinal);

            MainViewModel vm = CreateViewModel();

            Assert.NotNull(vm.ExtractIntoSourceFolderCommand);

            // 没勾任务时点不动（与「只解压」逐字同一条守卫）。
            vm.Tasks.Add(new ArchiveTask(Path.Combine(_root, "x.rar"), 1) { IsSelected = false });

            Assert.False(vm.ExtractIntoSourceFolderCommand.CanExecute(null));
            Assert.False(vm.StartExtractCommand.CanExecute(null));

            vm.Tasks[0].IsSelected = true;

            Assert.True(vm.ExtractIntoSourceFolderCommand.CanExecute(null));
            Assert.True(vm.StartExtractCommand.CanExecute(null));

            // 两条命令是**两个对象**：共用一个就等于把摊平偷偷接给了「只解压」。
            Assert.NotSame(vm.ExtractIntoSourceFolderCommand, vm.StartExtractCommand);
        }

        // ================================================================ 第 2 条：移除勾选的

        /// <summary>
        /// 「移除勾选的」：**永远可点**（纯列表操作，与"正在处理"无关 —— 用户原话："我就是修改列表删除东西，
        /// 和正在处理有什么关系"）；优先移除勾选的，**一个都没勾时按当前高亮那一行**移除
        /// （他上一轮"点了没反应"的真因：他只点中了行、没打勾）。
        /// </summary>
        [Fact]
        public void 移除勾选的_勾选的与高亮那一行都能移除_处理中也照样能点()
        {
            MainViewModel vm = CreateViewModel(out LogService log);

            string checkedFile = Path.Combine(_root, "checked.bin");
            string highlightedFile = Path.Combine(_root, "highlighted.bin");
            string untouchedFile = Path.Combine(_root, "untouched.bin");

            File.WriteAllBytes(checkedFile, new byte[16]);
            File.WriteAllBytes(highlightedFile, new byte[16]);
            File.WriteAllBytes(untouchedFile, new byte[16]);

            var checkedTask = new ArchiveTask(checkedFile, 1) { IsSelected = true };
            var highlightedTask = new ArchiveTask(highlightedFile, 2) { IsSelected = false };
            var untouchedTask = new ArchiveTask(untouchedFile, 3) { IsSelected = false };

            vm.Tasks.Add(checkedTask);
            vm.Tasks.Add(highlightedTask);
            vm.Tasks.Add(untouchedTask);

            /* 处理中也要能点（旧实现绑了 !IsBusy → 跑批时是灰的，用户当场反问过这件事）。 */
            vm.IsBusy = true;

            try
            {
                Assert.True(vm.RemoveCheckedTasksCommand.CanExecute(null), "跑批中途也必须能改列表（纯列表操作）");
                Assert.Contains("只动列表", vm.RemoveCheckedTasksTooltip, StringComparison.Ordinal);
                Assert.Contains("当前点中", vm.RemoveCheckedTasksTooltip, StringComparison.Ordinal);
            }
            finally
            {
                vm.IsBusy = false;
            }

            // ① 有勾选 → 移除勾选的那些（高亮那一行不动）
            vm.RemoveCheckedTasksCommand.Execute(null);

            Assert.DoesNotContain(checkedTask, vm.Tasks);
            Assert.Contains(highlightedTask, vm.Tasks);

            // ② 没有勾选、但点中了一行（高亮）→ 移除那一行，并写日志说清是按哪一行办的
            vm.SelectedTask = highlightedTask;
            vm.RemoveCheckedTasksCommand.Execute(null);

            Assert.DoesNotContain(highlightedTask, vm.Tasks);
            Assert.Contains(untouchedTask, vm.Tasks);

            /*
             * 日志断言走**文件日志**（LogService.Logs）：无界面宿主里 `MainViewModel.Logs`（屏幕日志）
             * 是靠 Dispatcher 回填的，进程里没有 Application 就永远是空的 —— 拿它断言会"看起来没写日志"。
             */
            List<string> logs = log.Logs.Select(item => item.DisplayText).ToList();

            Assert.Contains(
                logs,
                text => text.Contains("没有勾选任何任务，改为移除当前点中的那一行", StringComparison.Ordinal)
                        && text.Contains("highlighted.bin", StringComparison.Ordinal));

            Assert.True(File.Exists(checkedFile) && File.Exists(highlightedFile), "移除只动列表：源文件一个字节都不许动");
        }

        // ================================================================ 第 6 条：docs 随程序发

        /// <summary>
        /// 面向用户的四份文档必须**跟着程序复制到输出目录的 docs\**下（真机：绿色目录里没有 docs\，
        /// 帮助 →「使用说明」必然报路径错误）。这里是**源码守卫**：csproj 里那四条 Link 缺一条就红。
        /// </summary>
        [Fact]
        public void 用户文档跟着程序走_csproj里四条都在()
        {
            string csproj = Read("ArchiveFixer.csproj");

            foreach (string doc in new[] { "使用说明.md", "功能一览.md", "设置项.md", "人工测试清单.md" })
            {
                Assert.Contains($"Link=\"docs\\{doc}\"", csproj, StringComparison.Ordinal);
            }

            Assert.Contains("CopyToOutputDirectory", csproj, StringComparison.Ordinal);
        }

        /// <summary>找不到《使用说明》时，提示里必须**列出找过的位置**（真机只看到一句"路径错误"）。</summary>
        [Fact]
        public void 使用说明找不到时_提示列出找过的位置()
        {
            string code = Read("MainWindow.xaml.cs");

            Assert.Contains("DescribeUsageDocSearch", code, StringComparison.Ordinal);
            Assert.Contains("我找过这些位置", code, StringComparison.Ordinal);
            Assert.Contains("最右端的「说明」", code, StringComparison.Ordinal);
        }

        // ================================================================ 装配

        private MainViewModel CreateViewModel() => CreateViewModel(out _);

        private MainViewModel CreateViewModel(out LogService logService)
        {
            string cacheRoot = Path.Combine(_root, "data");
            Directory.CreateDirectory(cacheRoot);

            var pathService = new PathService { DataRootDirectory = cacheRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CacheRootDirectory = cacheRoot;
            settings.AutoScanAfterDrop = false;
            settingsService.Save(settings);

            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            logService = new LogService(pathService);

            try
            {
                return new MainViewModel(
                    new FileScanService(),
                    new ArchiveDetectService(),
                    new RenameService(),
                    new ArchiveFixer.Engines.SevenZip.SevenZipEngine(),
                    new PasswordService(),
                    logService,
                    settingsService,
                    pathService,
                    new TaskSummaryService(),
                    new ClipboardService(),
                    new DialogService());
            }
            finally
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
                ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;
            }
        }

        /// <summary>读仓库里的文件（与 <c>InterfaceRefactorTests</c> 同一手法；找不到就让测试红掉）。</summary>
        private static string Read(params string[] parts)
        {
            string path = Path.Combine(new[] { XamlBindingScan.RepositoryRoot, "src", "ArchiveFixer" }.Concat(parts).ToArray());

            Assert.True(File.Exists(path), $"读不到 {path}");

            return File.ReadAllText(path);
        }

        /// <summary>
        /// 隔离根目录：优先放在**测试程序集所在那块盘**（避免落到 C 盘 —— 缓存根目录那一档会拒 C 盘），
        /// 都不行才退回 %TEMP%。
        /// </summary>
        private static string CreateIsolatedRoot()
        {
            var candidates = new List<string>();

            try
            {
                string? assemblyDirectory = Path.GetDirectoryName(typeof(ItemSept27FixTests).Assembly.Location);

                if (!string.IsNullOrWhiteSpace(assemblyDirectory))
                {
                    candidates.Add(Path.GetPathRoot(assemblyDirectory) ?? string.Empty);
                }
            }
            catch
            {
                // 取不到就换下面的候选。
            }

            candidates.Add("E:\\");
            candidates.Add(Path.GetTempPath());

            foreach (string baseDirectory in candidates)
            {
                if (string.IsNullOrWhiteSpace(baseDirectory))
                {
                    continue;
                }

                try
                {
                    string parent = Path.Combine(baseDirectory, "ArchiveFixer-tests", "item-sept-27");
                    Directory.CreateDirectory(parent);
                    return Path.Combine(parent, "run-" + Guid.NewGuid().ToString("N"));
                }
                catch
                {
                    // 这块盘不能写就换下一块。
                }
            }

            return Path.Combine(Path.GetTempPath(), "af-item-sept-27-" + Guid.NewGuid().ToString("N"));
        }
    }
}

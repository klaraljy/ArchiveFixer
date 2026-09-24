using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 界面重构的回归（用户 2026-09-24 第 11、18、20 条；方案见 <c>docs/界面重构方案.md</c>）。
    ///
    /// <para>这一组钉住的是**结构**，与"某个控件文案改得对不对"互补：</para>
    /// <list type="number">
    /// <item><description>六个选项卡的标题与顺序；</description></item>
    /// <item><description>① 任务页**不再**有并发档 / 空间管理 / 危险模式红横幅 / 输出位置那一大块（反向断言）；</description></item>
    /// <item><description>危险模式入口在②页，风险四条仍是 <see cref="StatusText.DangerModeRiskLines"/> 那一份；</description></item>
    /// <item><description>日志区与列表之间真的有分隔条，而且是上下拖的那种；</description></item>
    /// <item><description>菜单只剩三组、旧菜单的每个命令都找得到新家；</description></item>
    /// <item><description>窗口默认尺寸与最小尺寸；</description></item>
    /// <item><description>从设置窗口搬走的每一项都落在**指定**的那一页，并且绑的仍是同一个 <c>Settings.*</c> 属性。</description></item>
    /// </list>
    ///
    /// <para>
    /// 为什么用 XAML 文本/XML 而不是"跑起来点一遍"：这些是"文件里到底写了什么"的事实，
    /// 静态解析**零误报**且不需要 WPF 宿主；真正"模板实例化会不会炸"由
    /// <c>WindowInstantiationTests</c> 的六页逐页实例化覆盖，绑定路径能不能解析由
    /// <c>UiBindingReachabilityTests</c> + <c>XamlBindingSafetyTests</c> 覆盖。
    /// </para>
    /// </summary>
    public class InterfaceRefactorTests
    {
        private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        /// <summary>六个选项卡的标题（顺序 = 用户 2026-09-24 拍板的那一版）。</summary>
        private static readonly string[] ExpectedTabHeaders =
        {
            "任务", "解压方式", "清理与删除", "密码", "打包", "设置"
        };

        /// <summary>① 任务页里**不许**再出现的东西（用户点名"放这么大空间干什么"）。</summary>
        private static readonly string[] MustNotBeOnTaskTab =
        {
            "MaxParallelChoice",          // 并发档
            "ParallelAdviceText",         // 空间建议那句话
            "RefreshParallelAdviceCommand", // 「按空间算建议」
            "ToggleDangerModeCommand",    // 危险模式红按钮
            "DangerButtonStyle",
            "DangerModeRiskLines",        // 风险四条（红横幅）
            "DangerSoftBrush",            // 横幅底色
            "OutputPlacementSummaryConverter", // 输出位置那一大块
            "SelectedOutputDirectory",
            "RunAtFullSpeed",             // 「全速」（跟着并发档走）
            "SpaceModeText",
            "OpenSettingsCommand"         // 「改…」跳设置窗口
        };

        // ================================================================ ① 六个选项卡

        [Fact]
        public void 主窗口是六个选项卡_标题与顺序都照方案()
        {
            XDocument document = Load(Path.Combine("ArchiveFixer", "MainWindow.xaml"));

            List<string> headers = document
                .Descendants(Presentation + "TabItem")
                .Select(item => (string?)item.Attribute("Header") ?? string.Empty)
                .ToList();

            Assert.Equal(ExpectedTabHeaders, headers);

            // 每一页都必须真的挂着一个 UserControl（不是空壳页）。
            Assert.Equal(6, document.Descendants().Count(element => element.Name.NamespaceName.Contains("ArchiveFixer.Views.Tabs")));
        }

        [Fact]
        public void 六个选项卡各有一个自己的XAML文件_主窗口不再堆界面()
        {
            foreach (string name in new[] { "TaskTab", "ExtractionTab", "CleanupTab", "PasswordTab", "PackingTab", "SettingsTab" })
            {
                Assert.True(File.Exists(Path.Combine(TabsDirectory, name + ".xaml")), $"{name}.xaml 不见了");
                Assert.True(File.Exists(Path.Combine(TabsDirectory, name + ".xaml.cs")), $"{name}.xaml.cs 不见了");
            }

            // 退休的两个窗口不许留死代码（内容已经搬进选项卡）。
            Assert.False(File.Exists(Path.Combine(ViewsDirectory, "SettingsWindow.xaml")), "SettingsWindow.xaml 应该已经退休");
            Assert.False(File.Exists(Path.Combine(ViewsDirectory, "PackingWindow.xaml")), "PackingWindow.xaml 应该已经退休");
        }

        // ================================================================ ② ①页减负（反向断言）

        [Fact]
        public void 任务页不再有并发空间危险红横幅与输出位置那几块()
        {
            string taskTab = Read("Views", "Tabs", "TaskTab.xaml");

            foreach (string forbidden in MustNotBeOnTaskTab)
            {
                Assert.DoesNotContain(forbidden, taskTab, StringComparison.Ordinal);
            }

            // 主线必须还在：添加 → 一键处理 / 只解压 / 停止后续 / 取消当前 → 汇总。
            Assert.Contains("AddFilesCommand", taskTab, StringComparison.Ordinal);
            Assert.Contains("AddFolderCommand", taskTab, StringComparison.Ordinal);
            Assert.Contains("OneClickProcessCommand", taskTab, StringComparison.Ordinal);
            Assert.Contains("StartExtractCommand", taskTab, StringComparison.Ordinal);
            Assert.Contains("StopCommand", taskTab, StringComparison.Ordinal);
            Assert.Contains("CancelCurrentCommand", taskTab, StringComparison.Ordinal);
            Assert.Contains("Summary.", taskTab, StringComparison.Ordinal);
        }

        [Fact]
        public void 主窗口只剩菜单选项卡与底栏()
        {
            string mainWindow = Read("MainWindow.xaml");

            // 底部状态/进度 + 保存设置：除此之外主窗口不该再出现设置类控件。
            Assert.Contains("SettingsEditor.Message", mainWindow, StringComparison.Ordinal);
            Assert.Contains("SaveSettingsCommand", mainWindow, StringComparison.Ordinal);

            foreach (string moved in new[]
                     {
                         "MaxParallelChoice", "ParallelAdviceText", "DangerButtonStyle", "DangerModeRiskLines",
                         "TaskDataGrid", "LogList", "GlobalPasswordBox", "OpenPackingCommand"
                     })
            {
                Assert.DoesNotContain(moved, mainWindow, StringComparison.Ordinal);
            }
        }

        // ================================================================ ③ 危险模式的新家

        [Fact]
        public void 危险模式在解压方式页_风险四条仍是同一份措辞()
        {
            string extraction = Read("Views", "Tabs", "ExtractionTab.xaml");

            Assert.Contains("ToggleDangerModeCommand", extraction, StringComparison.Ordinal);
            Assert.Contains("DangerButtonStyle", extraction, StringComparison.Ordinal);

            // 四条风险**绑定** StatusText 那一份（界面里没有第二份文本，改一处不可能漏一处）。
            Assert.Contains("DangerModeRiskLines", extraction, StringComparison.Ordinal);
            Assert.Contains("StatusText.DangerModeSelfTestWarning", extraction, StringComparison.Ordinal);

            // 主 ViewModel 暴露的就是 StatusText 那一份，不是自己抄的一份。
            string viewModelSource = Read("ViewModels", "MainViewModel.cs");

            Assert.Contains(
                "IReadOnlyList<string> DangerModeRiskLines => StatusText.DangerModeRiskLines;",
                viewModelSource,
                StringComparison.Ordinal);

            // 开关也在这一页（设置里那一项 + 红色按钮两条入口都对着同一个设置项）。
            Assert.Contains("SettingsEditor.Settings.DangerousSpaceModeEnabled", extraction, StringComparison.Ordinal);

            // ①页只留一行小白字提示。
            string taskTab = Read("Views", "Tabs", "TaskTab.xaml");

            Assert.Contains("StatusText.DangerModeActiveOneLineHint", taskTab, StringComparison.Ordinal);
        }

        // ================================================================ ④ 日志区可分拖（第 18 条）

        [Fact]
        public void 日志区与列表之间有上下拖的分隔条_并且行高是星号伸缩()
        {
            XDocument document = Load(Path.Combine("ArchiveFixer", "Views", "Tabs", "TaskTab.xaml"));

            XElement splitter = Assert.Single(document.Descendants(Presentation + "GridSplitter"));

            Assert.Equal("Rows", (string?)splitter.Attribute("ResizeDirection"));
            Assert.Equal("6", (string?)splitter.Attribute("Height"));
            Assert.Equal("PreviousAndNext", (string?)splitter.Attribute("ResizeBehavior"));

            List<XElement> rows = document.Descendants(Presentation + "RowDefinition").ToList();

            // 日志行默认 28*（约占这一页高度的 28%），列表行 72* 且最小 160 ——
            // 分隔条拖到底也压不掉列表（"只有两个框变大"那种病就是从固定高度来的）。
            Assert.Contains(rows, row => (string?)row.Attribute(X + "Name") == "LogRow"
                                         && (string?)row.Attribute("Height") == "28*");

            Assert.Contains(rows, row => (string?)row.Attribute("Height") == "72*"
                                         && (string?)row.Attribute("MinHeight") == "160");

            // 固定高度的行一个都不许有（第 20 条：拉大窗口时必须跟着伸缩）。
            Assert.DoesNotContain(rows, row => ((string?)row.Attribute("Height"))?.EndsWith("px", StringComparison.Ordinal) == true);

            // 日志窗口（放大用）是独立窗口，与①页读同一个集合。
            Assert.True(File.Exists(Path.Combine(ViewsDirectory, "LogWindow.xaml")));

            string logWindow = Read("Views", "LogWindow.xaml");

            Assert.Contains("ItemsSource=\"{Binding Logs}\"", logWindow, StringComparison.Ordinal);
        }

        // ================================================================ ⑤ 菜单只剩三组 + 命令不丢

        [Fact]
        public void 顶部菜单只剩三组_工具菜单已经取消()
        {
            XDocument document = Load(Path.Combine("ArchiveFixer", "MainWindow.xaml"));

            XElement menu = Assert.Single(document.Descendants(Presentation + "Menu"));

            List<string> groups = menu
                .Elements(Presentation + "MenuItem")
                .Select(item => (string?)item.Attribute("Header") ?? string.Empty)
                .ToList();

            Assert.Equal(new[] { "文件", "视图", "帮助" }, groups);

            // 「工具」整组取消（设置 / 打包 / 密码本 / 清理入口都各自搬进了选项卡）。
            Assert.DoesNotContain("工具", groups);
            Assert.DoesNotContain("Header=\"工具\"", Read("MainWindow.xaml"), StringComparison.Ordinal);

            // 视图 / 帮助 那几项是界面动作，走 code-behind —— 处理函数必须真的在。
            string codeBehind = Read("MainWindow.xaml.cs");

            foreach (string handler in new[]
                     {
                         "LogWindowMenuItem_Click", "ToggleLogAreaMenuItem_Click", "JumpToSelectedTaskMenuItem_Click",
                         "UsageMenuItem_Click", "ShortcutsMenuItem_Click", "AboutMenuItem_Click", "ExitMenuItem_Click"
                     })
            {
                Assert.Contains(handler, codeBehind, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void 旧菜单里的每个命令都能在新界面里找到()
        {
            // 旧「文件」菜单 + 旧「工具」菜单里绑过的**全部**命令（2026-09-24 之前的 MainWindow.xaml）。
            string[] oldMenuCommands =
            {
                // 文件
                "AddFilesCommand", "AddFolderCommand", "AppendFilesCommand", "AppendFolderCommand",
                "ScanCommand", "SelectAllTasksCommand", "SelectNoneTasksCommand", "InvertTaskSelectionCommand",
                "RemoveSelectedCommand", "ClearCommand",

                // 工具
                "ImportPasswordBookCommand", "OpenOutputDirectoryCommand", "OpenWorkDirectoryCommand",
                "ExportLogCommand", "OpenLogDirectoryCommand", "ExportFailedListCommand",
                "CleanProcessArtifactsCommand", "CleanAllProcessArtifactsCommand", "CleanEmptyFoldersCommand"
            };

            string surface = ReadMainSurfaceXaml();

            foreach (string command in oldMenuCommands)
            {
                Assert.True(
                    surface.Contains(command, StringComparison.Ordinal),
                    $"旧菜单里的命令 {command} 在新界面上找不到入口了");
            }

            /*
             * 两个**语义被选项卡取代**的命令（它们原来打开的两个窗口已经退休）：
             * 设置 → ⑥「设置」页；打包 → ⑤「打包」页。
             * 这两个命令本身仍在 MainViewModel 上（改成"切到那一页"），所以不是"丢了功能"，
             * 而是"入口从菜单变成了选项卡" —— 这里钉住的是"那一页真的在"。
             */
            XDocument mainWindow = Load(Path.Combine("ArchiveFixer", "MainWindow.xaml"));

            List<string> headers = mainWindow
                .Descendants(Presentation + "TabItem")
                .Select(item => (string?)item.Attribute("Header") ?? string.Empty)
                .ToList();

            Assert.Contains("设置", headers);
            Assert.Contains("打包", headers);

            string viewModel = Read("ViewModels", "MainViewModel.cs");

            Assert.Contains("SaveSettingsCommand", viewModel, StringComparison.Ordinal);
            Assert.Contains("TabRequested", viewModel, StringComparison.Ordinal);
        }

        // ================================================================ ⑥ 窗口尺寸（第 20 条）

        [Fact]
        public void 主窗口默认尺寸与最小尺寸符合约定()
        {
            XDocument document = Load(Path.Combine("ArchiveFixer", "MainWindow.xaml"));

            Assert.Equal("1400", (string?)document.Root?.Attribute("Width"));
            Assert.Equal("900", (string?)document.Root?.Attribute("Height"));
            Assert.Equal("1000", (string?)document.Root?.Attribute("MinWidth"));
            Assert.Equal("640", (string?)document.Root?.Attribute("MinHeight"));
        }

        // ================================================================ ⑦ 搬走的设置项

        [Fact]
        public void 搬走的设置项逐项落在指定那一页_并绑定同一个Settings属性()
        {
            /*
             * "设置窗口"里那一整份清单在 2026-09-24 被拆成四页。
             * 这里钉的是**逐项的家**：写错页 = 用户按功能去找却找不到；
             * 一项出现在两页 = 两个控件改同一个值（历史上正是这种重复让人以为"改了没反应"）。
             */
            var expectedHome = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // ② 解压方式
                ["RecursionMode"] = "ExtractionTab",
                ["MaxRecursionDepth"] = "ExtractionTab",
                ["OverwriteMode"] = "ExtractionTab",
                ["MaxParallelExtractCount"] = "ExtractionTab",
                ["TestBeforeExtract"] = "ExtractionTab",
                ["OpenOutputFolderWhenDone"] = "ExtractionTab",
                ["ReportDangerousEntries"] = "ExtractionTab",
                ["CollectResultsToDirectory"] = "ExtractionTab",
                ["CollectTargetDirectory"] = "ExtractionTab",
                ["CustomSevenZipExePath"] = "ExtractionTab",
                ["DangerousSpaceModeEnabled"] = "ExtractionTab",

                // ② 的第 17 条那一格：一键处理的确认框可以关掉（也在确认框自己的勾选项里写着）
                ["SkipOneClickConfirm"] = "ExtractionTab",

                // ③ 清理与删除
                ["RestRemovalDefaultMode"] = "CleanupTab",
                ["DeleteSourceAfterExtract"] = "CleanupTab",
                ["RemindBeforeExtract"] = "CleanupTab",

                // ③ 的第 15 条那一格：导入文件夹后就提醒无用物
                ["RemindJunkAfterImport"] = "CleanupTab",

                // ④ 密码
                ["UseGlobalPasswordForAllTasks"] = "PasswordTab",
                ["TryEmptyPasswordFirst"] = "PasswordTab",
                ["EnableSidecarPassword"] = "PasswordTab",
                ["MaxPasswordAttemptsPerLayer"] = "PasswordTab",

                // ⑥ 设置
                ["RecursiveScan"] = "SettingsTab",
                ["AutoScanAfterDrop"] = "SettingsTab",
                ["ScanMode"] = "SettingsTab",
                ["IncludeHiddenFiles"] = "SettingsTab",
                ["IncludeSystemFiles"] = "SettingsTab",
                ["MaxFileSizeLimit"] = "SettingsTab",
                ["UnknownFormatAction"] = "SettingsTab",
                ["DefaultExtension"] = "SettingsTab",
                ["ConflictAction"] = "SettingsTab",
                ["EnableLog"] = "SettingsTab",
                ["LowProcessPriority"] = "SettingsTab",
                ["RememberLastOutputDirectory"] = "SettingsTab"
            };

            var tabFiles = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string name in new[] { "TaskTab", "ExtractionTab", "CleanupTab", "PasswordTab", "PackingTab", "SettingsTab" })
            {
                tabFiles[name] = Read("Views", "Tabs", name + ".xaml");
            }

            foreach ((string property, string expectedTab) in expectedHome)
            {
                // ① 属性本身必须还在（清单过时 = 测试说谎）。
                Assert.NotNull(typeof(AppSettings).GetProperty(property));

                // ② 指定的那一页必须绑它，而且用的是同一个 Settings.* 名字。
                Assert.True(
                    tabFiles[expectedTab].Contains("Settings." + property, StringComparison.Ordinal),
                    $"{expectedTab} 里没有绑定 Settings.{property}");

                // ③ 别的页不许也绑同一项 —— 两个控件改同一个值，用户会看到"改了没反应"。
                foreach ((string tabName, string xaml) in tabFiles)
                {
                    if (tabName == expectedTab)
                    {
                        continue;
                    }

                    Assert.False(
                        xaml.Contains("Settings." + property, StringComparison.Ordinal),
                        $"Settings.{property} 同时出现在 {expectedTab} 与 {tabName} 两页里");
                }
            }
        }

        [Fact]
        public void 编辑器级的那几项也各自有家()
        {
            // SettingsViewModel 自己的属性（不是 AppSettings 上的）：同样一项都不能少。
            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OutputPlacement"] = "ExtractionTab",
                ["CustomOutputDirectory"] = "ExtractionTab",
                ["OutputPlacementSummary"] = "ExtractionTab",
                ["SelectOutputDirectoryCommand"] = "ExtractionTab",
                ["TerminalLayout"] = "ExtractionTab",
                ["CollapseRepeatedFolderLayer"] = "ExtractionTab",
                ["Engines"] = "ExtractionTab",
                ["EngineSelectionSummary"] = "ExtractionTab",
                ["MoveEngineUpCommand"] = "ExtractionTab",
                ["MoveEngineDownCommand"] = "ExtractionTab",
                ["CustomUnRarExePath"] = "ExtractionTab",
                ["CustomRarExePath"] = "ExtractionTab",
                ["RarExePathHint"] = "ExtractionTab",
                ["RarToolStatusText"] = "ExtractionTab",
                ["SelectRarExeCommand"] = "ExtractionTab",
                ["KeepBrokenFiles"] = "ExtractionTab",
                ["SourceHandling"] = "CleanupTab",
                ["IsRememberingPasswordList"] = "PasswordTab",
                ["RememberedBooks"] = "PasswordTab",
                ["RemoveRememberedBookCommand"] = "PasswordTab",
                ["CacheRootDirectory"] = "SettingsTab",
                ["SelectCacheRootDirectoryCommand"] = "SettingsTab"
            };

            foreach ((string property, string expectedTab) in expected)
            {
                Assert.NotNull(typeof(SettingsViewModel).GetProperty(property));

                Assert.True(
                    Read("Views", "Tabs", expectedTab + ".xaml").Contains("SettingsEditor." + property, StringComparison.Ordinal),
                    $"{expectedTab} 里没有绑定 SettingsEditor.{property}");
            }
        }

        /// <summary>
        /// 第 15/16 追加/22 条补进来的三个入口必须真的挂在界面上。
        ///
        /// <para>命令存在但界面上找不到 = 功能只活在代码里（用户点不到），
        /// 这一组就是防那种"实现了但没接线"的半成品。</para>
        /// </summary>
        [Fact]
        public void 第15到22条补的三个入口都真的挂在界面上()
        {
            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // 第 16 条追加：撞到 10 层上限之后接着解
                ["ContinueOneClickCommand"] = "TaskTab",

                // 第 15 条：列表里删无用物
                ["RemoveCheckedTasksCommand"] = "TaskTab",

                // 第 22 条：工作区残留一键清
                ["ClearWorkspaceLeftoversCommand"] = "CleanupTab"
            };

            foreach ((string command, string tab) in expected)
            {
                Assert.NotNull(typeof(MainViewModel).GetProperty(command));

                Assert.True(
                    Read("Views", "Tabs", tab + ".xaml").Contains(command, StringComparison.Ordinal),
                    $"{tab} 里没有绑定 {command}");
            }

            // "还剩 N 个内层包"那一行提示与按钮文案也要一起出现（到顶不许静默停下）
            string taskTab = Read("Views", "Tabs", "TaskTab.xaml");

            Assert.Contains("PendingContinuationText", taskTab, StringComparison.Ordinal);
            Assert.Contains("ContinueOneClickButtonText", taskTab, StringComparison.Ordinal);
            Assert.Contains("HasPendingContinuation", taskTab, StringComparison.Ordinal);

            // 工作区残留那一行提示同理（第 22 条：看得见才谈得上清理）
            string cleanupTab = Read("Views", "Tabs", "CleanupTab.xaml");

            Assert.Contains("WorkspaceLeftoverBanner", cleanupTab, StringComparison.Ordinal);
            Assert.Contains("HasWorkspaceLeftovers", cleanupTab, StringComparison.Ordinal);
        }

        [Fact]
        public void 设置编辑器与主界面共享同一份设置对象()
        {
            /*
             * 选项卡形态下**不能**克隆设置：并发档既在②页的输入框里、又决定危险模式凭证盖不盖得住，
             * 两份值一定会打架（界面显示 A、保存写回 B）。这条钉住"共享"这个前提。
             */
            var live = new AppSettings();

            var editor = new SettingsViewModel(new AppSettings(), new SettingsService());

            // 构造函数是克隆的（设置窗口的「取消」靠它）—— 所以挂载这一步不能省。
            Assert.NotSame(live, editor.Settings);

            editor.AttachSharedSettings(live);

            Assert.Same(live, editor.Settings);

            editor.Settings.RecursionMode = "AllBranches";

            Assert.Equal("AllBranches", live.RecursionMode);
        }

        // ================================================================ 辅助

        private static string ViewsDirectory => Path.Combine(XamlBindingScan.RepositoryRoot, "ArchiveFixer", "Views");

        private static string TabsDirectory => Path.Combine(ViewsDirectory, "Tabs");

        private static string Read(params string[] parts)
        {
            string path = Path.Combine(XamlBindingScan.RepositoryRoot, "ArchiveFixer");

            foreach (string part in parts)
            {
                path = Path.Combine(path, part);
            }

            Assert.True(File.Exists(path), $"读不到文件：{path}");

            return File.ReadAllText(path);
        }

        private static XDocument Load(string relativePath)
        {
            string path = Path.Combine(XamlBindingScan.RepositoryRoot, relativePath);

            Assert.True(File.Exists(path), $"读不到文件：{path}");

            return XDocument.Load(path);
        }

        /// <summary>主窗口 + 六个选项卡的 XAML 并集（"某个命令/某个文案在不在界面上"就看它）。</summary>
        private static string ReadMainSurfaceXaml()
        {
            var builder = new System.Text.StringBuilder();

            builder.AppendLine(Read("MainWindow.xaml"));

            foreach (string file in Directory.EnumerateFiles(TabsDirectory, "*.xaml").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine(File.ReadAllText(file));
            }

            // 独立日志窗口也是界面的一部分（第 18 条的入口）。
            builder.AppendLine(Read("Views", "LogWindow.xaml"));

            return builder.ToString();
        }
    }
}

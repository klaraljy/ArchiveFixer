using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media;
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
    /// <item><description>危险模式那套（红按钮 / 风险四条 / 自测）已在 2026-09-25 第 32 条整块退役，界面上一个字都不留；</description></item>
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
            "RunAtFullSpeed",             // 「全速」（跟着并发档走）
            "SpaceModeText",
            "OpenSettingsCommand"         // 「改…」跳设置窗口
        };

        /// <summary>
        /// **有意为之的第二入口**（用户 2026-09-25 第 27 条）：同一项出现在两页里，
        /// 在这一份清单里逐条登记 + 一句为什么，别让"同一项只许出现在一页"那条断言误报。
        ///
        /// <para>它与"两个控件改同一个值、用户看到改了没反应"那种缺陷的区别在于：
        /// 这里两个入口读写的**是同一个真值**（<c>Settings.ExtractToOriginalDirectory</c> +
        /// <c>Settings.CustomOutputDirectory</c>），而且在①页改完会显式通知②页刷新（反过来也一样），
        /// 有专门的用例钉住（<c>OutputLocationOnTaskTabTests</c>）。</para>
        /// </summary>
        private static readonly Dictionary<string, string> DeliberateSecondEntries = new(StringComparer.Ordinal)
        {
            ["OutputLocationDisplay"] = "①页那一格显示的省略路径（第 27 条）。它只是显示，不存值。",

            ["OutputLocationFollowsArchive"] = "①页那一格的「未指定位置」开关（第 27 条）。它写的就是②页落点判据本身。",

            ["OutputLocationToolTip"] = "①页那一格的完整路径提示（第 27 条）。只读。",

            ["SelectOutputDirectoryCommand"] = "①页「选择…」（第 27 条）。与②页那把「选择」同一个动作、同一份值。",

            ["CopyOutputLocationCommand"] = "①页那一格右键的「复制完整路径」（第 27 条）。只读，不改任何值。"
        };

        /// <summary>
        /// 同一件事的**值**在 ViewModel 上的名字（不进 XAML，所以不按"①页里找得到"判）。
        ///
        /// <para><c>SelectedOutputDirectory</c> 就是①页那一格显示的那个值：XAML 绑的是
        /// <c>OutputLocationDisplay</c>（省略之后给人看的），值本身仍在 MainViewModel 上，
        /// 与②页共用的也是它。</para>
        /// </summary>
        private static readonly Dictionary<string, string> DeliberateSecondEntryValues = new(StringComparer.Ordinal)
        {
            ["SelectedOutputDirectory"] = "①页与②页共用的那一个输出位置值（第 27 条）。"
        };

        // ================================================================ ① 六个选项卡

        [Fact]
        public void 主窗口是六个选项卡_标题与顺序都照方案()
        {
            XDocument document = Load(Path.Combine("src", "ArchiveFixer", "MainWindow.xaml"));

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

            /*
             * 用户 2026-09-26 把"设置要手动保存"整块推翻了，随后**连那一行告知也删掉**：
             * 「保存全部设置」按钮、底部那个「设置已加载。」的框、以及后来那行"设置会自动保存"
             * （他原话："这个鬼东西太突兀了，而且这个又相当于是应该，但你却非要标出来，
             * 而且在切换选项卡的时候也会显示出来……现在彻底删除"）**都必须不在**；
             * 底栏只留"正在处理…"（忙时那一个指示器）。
             * ⛔ 这条是**反向**断言：以后谁再把"设置相关的一行字 / 一个按钮"加回底栏，这里立刻红
             * （设置本身仍然是改了就自动存，见 SettingsAutoSaveTests）。
             */
            Assert.DoesNotContain("保存全部设置", mainWindow, StringComparison.Ordinal);
            Assert.DoesNotContain("SaveSettingsCommand", mainWindow, StringComparison.Ordinal);
            Assert.DoesNotContain("SettingsEditor.Message", mainWindow, StringComparison.Ordinal);
            Assert.DoesNotContain("SettingsAutoSaveNote", mainWindow, StringComparison.Ordinal);
            Assert.Contains("正在处理…", mainWindow, StringComparison.Ordinal);

            foreach (string moved in new[]
                     {
                         "MaxParallelChoice", "ParallelAdviceText", "DangerButtonStyle", "RestHandlingAfterVerify",
                         "TaskDataGrid", "LogList", "GlobalPasswordBox", "OpenPackingCommand"
                     })
            {
                Assert.DoesNotContain(moved, mainWindow, StringComparison.Ordinal);
            }
        }

        // ================================================================ ③ 删除操作的新家（原「危险模式」已退役）

        /// <summary>
        /// 2026-09-25 第 32 条：②页底部那套「高风险区（危险模式 + 自测凭证 + 风险四条 + 红横幅）」
        /// 被用户**整块删掉**，那条需求变成③页「2 删除操作」的第三档（红字 + 选中时常驻提示）。
        /// 这一条钉的就是"旧的一套一个字都不许留在界面上"。
        /// </summary>
        [Fact]
        public void 危险模式那一套已退役_界面上一个字都不留()
        {
            string extraction = Read("Views", "Tabs", "ExtractionTab.xaml");
            string taskTab = Read("Views", "Tabs", "TaskTab.xaml");
            string mainViewModel = Read("ViewModels", "MainViewModel.cs");

            foreach (string retired in new[]
                     {
                         "ToggleDangerModeCommand", "DangerButtonStyle", "DangerModeRiskLines",
                         "DangerModeSummaryText", "DangerModeSelfTestText", "DangerousSpaceModeEnabled",
                         "Text=\u0022高风险区\u0022"
                     })
            {
                Assert.DoesNotContain(retired, extraction, StringComparison.Ordinal);
                Assert.DoesNotContain(retired, mainViewModel, StringComparison.Ordinal);
            }

            // ①页那一行"危险模式已开启"的小白字也一起没了。
            Assert.DoesNotContain("DangerModeActiveOneLineHint", taskTab, StringComparison.Ordinal);

            // 新家：③页两组单选 + 选中「彻底删除」时常驻的那条提示。
            string cleanup = Read("Views", "Tabs", "CleanupTab.xaml");

            Assert.Contains("SettingsEditor.SourceHandling", cleanup, StringComparison.Ordinal);
            Assert.Contains("SettingsEditor.RestHandling", cleanup, StringComparison.Ordinal);
            Assert.Contains("IsRestDeleteSelected", cleanup, StringComparison.Ordinal);
        }

        // ================================================================ ④ 日志区可分拖（第 18 条）

        [Fact]
        public void 日志区与列表之间有上下拖的分隔条_并且行高是星号伸缩()
        {
            XDocument document = Load(Path.Combine("src", "ArchiveFixer", "Views", "Tabs", "TaskTab.xaml"));

            XElement splitter = Assert.Single(document.Descendants(Presentation + "GridSplitter"));

            Assert.Equal("Rows", (string?)splitter.Attribute("ResizeDirection"));
            Assert.Equal("6", (string?)splitter.Attribute("Height"));
            Assert.Equal("PreviousAndNext", (string?)splitter.Attribute("ResizeBehavior"));

            List<XElement> rows = document.Descendants(Presentation + "RowDefinition").ToList();

            /*
             * 比例（用户 2026-09-26："将界面比例调整好"）：日志行 22*、列表行 78* 且最小 180 ——
             * 列表拿大头，分隔条拖到底也压不掉列表（"只有两个框变大"那种病就是从固定高度来的）。
             * ⚠ 断言的是**关系**（都是星号伸缩 + 列表那份更大 + 各自有下限），⛔ 不锁死具体数字：
             * 哪天再调一次比例不该把这条测试改成"改数字"。
             */
            XElement logRow = Assert.Single(rows, row => (string?)row.Attribute(X + "Name") == "LogRow");

            Assert.EndsWith("*", (string?)logRow.Attribute("Height"), StringComparison.Ordinal);
            Assert.Equal("72", (string?)logRow.Attribute("MinHeight"));

            XElement listRow = Assert.Single(rows, row => (string?)row.Attribute("MinHeight") == "180");

            Assert.EndsWith("*", (string?)listRow.Attribute("Height"), StringComparison.Ordinal);

            int listWeight = int.Parse(((string?)listRow.Attribute("Height"))![..^1], System.Globalization.CultureInfo.InvariantCulture);
            int logWeight = int.Parse(((string?)logRow.Attribute("Height"))![..^1], System.Globalization.CultureInfo.InvariantCulture);

            Assert.True(listWeight > logWeight, $"列表那一行必须比日志那一行大（现在 {listWeight}* vs {logWeight}*）");

            // 固定高度的行一个都不许有（第 20 条：拉大窗口时必须跟着伸缩）。
            Assert.DoesNotContain(rows, row => ((string?)row.Attribute("Height"))?.EndsWith("px", StringComparison.Ordinal) == true);

            // 日志窗口（放大用）是独立窗口，与①页读同一个集合。
            Assert.True(File.Exists(Path.Combine(ViewsDirectory, "LogWindow.xaml")));

            string logWindow = Read("Views", "LogWindow.xaml");

            Assert.Contains("ItemsSource=\"{Binding Logs}\"", logWindow, StringComparison.Ordinal);

            /*
             * 日志**按级别上色**（用户 2026-09-26："用不同颜色的字体，就比如说用红色、黄色、黑色字体，
             * 表示危险、警告、正常的操作"）：两处日志（①页与放大窗口）都必须把 Foreground 绑到
             * 级别上，⛔ 不许只有一处上色。
             */
            foreach (string file in new[] { Path.Combine("Views", "Tabs", "TaskTab.xaml"), Path.Combine("Views", "LogWindow.xaml") })
            {
                string text = file.Contains("LogWindow", StringComparison.Ordinal) ? logWindow : Read(file);

                Assert.Contains(
                    "Foreground=\"{Binding Level, Converter={StaticResource StatusToBrushConverter}}\"",
                    text,
                    StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// 日志三级 → 颜色：**红 = ERROR、黄 = WARN、黑 = INFO**（用户 2026-09-26 点名的那三种）。
        ///
        /// <para>⛔ 反向约束：INFO 不许用绿色（那条路以前把 INFO 归进了"成功色"，于是一片绿，
        /// "哪一行真出事了"看不出来）—— 任务状态列的成功色不受影响。</para>
        /// </summary>
        [Fact]
        public void 日志三级分别是红黄黑()
        {
            var converter = new Converters.StatusToBrushConverter();

            Brush Convert(string level) => (Brush)converter.Convert(level, typeof(Brush), null!, CultureInfo.InvariantCulture);

            Color warn = ((SolidColorBrush)Convert("WARN")).Color;
            Color error = ((SolidColorBrush)Convert("ERROR")).Color;
            Color info = ((SolidColorBrush)Convert("INFO")).Color;

            // 红：红分量最大。
            Assert.True(error.R > error.G && error.R > error.B, $"ERROR 该是红的，实际 {error}");
            Assert.Equal(converter.DefaultBrush, Convert("INFO"));
            Assert.NotEqual(converter.DefaultBrush, Convert("WARN"));

            // 黄/橙：红绿都高、蓝低（不锁死具体色值，只钉"黄系"这个事实）。
            Assert.True(warn.R > 180 && warn.G > 90 && warn.B < 80, $"WARN 该是黄/橙系，实际 {warn}");
            Assert.NotEqual(error, warn);

            // 黑（默认色）也得是深色，否则"正常操作"会看不清。
            var black = (SolidColorBrush)converter.DefaultBrush;
            Assert.True(black.Color.R < 96 && black.Color.G < 96 && black.Color.B < 96, $"默认色该是深色，实际 {black.Color}");

            // 任务状态列的成功色不受影响（它仍然要是绿的）。
            Color success = ((SolidColorBrush)Convert(StatusText.ExtractSuccess)).Color;
            Assert.True(success.G > success.R && success.G > success.B, $"成功态该是绿的，实际 {success}");
        }

        // ================================================================ ⑤ 菜单只剩三组 + 命令不丢

        [Fact]
        public void 顶部菜单只剩三组_工具菜单已经取消()
        {
            XDocument document = Load(Path.Combine("src", "ArchiveFixer", "MainWindow.xaml"));

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
            XDocument mainWindow = Load(Path.Combine("src", "ArchiveFixer", "MainWindow.xaml"));

            List<string> headers = mainWindow
                .Descendants(Presentation + "TabItem")
                .Select(item => (string?)item.Attribute("Header") ?? string.Empty)
                .ToList();

            Assert.Contains("设置", headers);
            Assert.Contains("打包", headers);

            string viewModel = Read("ViewModels", "MainViewModel.cs");

            // 自动保存（用户 2026-09-26）：主视图模型里必须有这条唯一出口，⛔ 不许再有"手动保存"命令。
            Assert.Contains("AutoSaveSettingsIfChanged", viewModel, StringComparison.Ordinal);
            Assert.Contains("FlushSettingsAutoSave", viewModel, StringComparison.Ordinal);
            Assert.DoesNotContain("SaveSettingsCommand", viewModel, StringComparison.Ordinal);
            Assert.Contains("TabRequested", viewModel, StringComparison.Ordinal);
        }

        // ================================================================ ⑥ 窗口尺寸（第 20 条）

        [Fact]
        public void 主窗口默认尺寸与最小尺寸符合约定()
        {
            XDocument document = Load(Path.Combine("src", "ArchiveFixer", "MainWindow.xaml"));

            Assert.Equal("1400", (string?)document.Root?.Attribute("Width"));
            Assert.Equal("900", (string?)document.Root?.Attribute("Height"));

            /*
             * 最小宽度 2026-09-25 由 1000 抬到 1180（第 27 条）：①页工具条中间那一格
             * 「输出位置」要有余量 —— 1000px 时右边那串按钮（全选…一键处理…特定解压）约 900px，
             * 路径格会被压到几乎看不见、右端的「未指定位置」还可能被裁掉。
             * 默认 1400 下完全够用，这条只防"用户把窗口拖到最小"那一种情况。
             */
            Assert.Equal("1180", (string?)document.Root?.Attribute("MinWidth"));
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
                // ① 任务（用户 2026-09-24：「特定解压」总开关就在一键处理旁边）
                ["UseSpecialExtraction"] = "TaskTab",

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

                // ② 的第 17 条那一格：一键处理的确认框可以关掉（也在确认框自己的勾选项里写着）
                ["SkipOneClickConfirm"] = "ExtractionTab",

                // ③ 清理与删除
                ["RestRemovalDefaultMode"] = "CleanupTab",

                /*
                 * ⚠ 源包操作 / 删除操作这两组**不在这份清单里**：它们绑的是
                 * SettingsEditor.SourceHandling / SettingsEditor.RestHandling（SettingsViewModel 的解析属性，
                 * 与解压时的口径同一份实现），而这份清单钉的是 "Settings.<原名>" 这种直接绑定。
                 * 它们的界面落点由 危险模式那一套已退役_界面上一个字都不留 那一条钉住。
                 */
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

                // ⑥ 的「日志」那一组里 2026-09-25（第 44 条追加之二）补的「详细日志（排查用）」。
                ["VerboseLog"] = "SettingsTab",
                ["LowProcessPriority"] = "SettingsTab",
                ["RememberLastOutputDirectory"] = "SettingsTab",

                /*
                 * ⑥ 的第 36 条那一组：解压前的四条安全上限。
                 * 它们以前是硬编码的（4 GiB 单文件 / 20 GiB 总量），用户被拦下时界面上一个字都没有。
                 */
                ["MaxSingleExtractedFileGiB"] = "SettingsTab",
                ["MaxExtractedTotalGiB"] = "SettingsTab",
                ["MaxExtractedFileCount"] = "SettingsTab",
                ["MaxExtractionRatio"] = "SettingsTab",

                // ② 的第 42 条那一格（"容器里装的是分卷第 1 卷时，自动接上同目录的后续卷"，默认关）。
                ["AssembleSplitVolumesFromContainer"] = "ExtractionTab"
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
                //    例外只认 DeliberateSecondEntries 里逐条登记过的那些（第 27 条的输出位置）。
                if (DeliberateSecondEntries.ContainsKey(property))
                {
                    continue;
                }

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

        /// <summary>
        /// 第二入口的白名单本身也要有据可查（用户 2026-09-25 第 27 条）：
        /// 登记的每一项都必须**真的**挂在①页上，而且**不许**绕过 ViewModel 直接再绑一遍
        /// <c>Settings.*</c> —— 那才会变成"两个控件改同一个值"的经典缺陷。
        /// </summary>
        [Fact]
        public void 输出位置的第二入口_每一项都真挂在任务页且不重复绑设置项()
        {
            string taskTab = Read("Views", "Tabs", "TaskTab.xaml");
            string viewModelSource = Read("ViewModels", "MainViewModel.cs");

            foreach ((string name, string reason) in DeliberateSecondEntries)
            {
                Assert.True(
                    taskTab.Contains(name, StringComparison.Ordinal),
                    $"白名单里登记了 {name}（{reason}），但①页上找不到它 —— 白名单过时了");

                Assert.False(
                    taskTab.Contains("Settings." + name, StringComparison.Ordinal),
                    $"{name} 不许在①页直接绑 Settings.*（那会让两处各改一份值）");
            }

            foreach ((string name, string reason) in DeliberateSecondEntryValues)
            {
                Assert.True(
                    viewModelSource.Contains(name, StringComparison.Ordinal),
                    $"白名单里登记了 {name}（{reason}），但 MainViewModel 上没有它 —— 白名单过时了");
            }

            // 反向：清单里没登记的、原来禁止的东西，一个都不许溜回①页。
            foreach (string forbidden in MustNotBeOnTaskTab)
            {
                Assert.DoesNotContain(forbidden, taskTab, StringComparison.Ordinal);
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

                /*
                 * 续解的中间层（用户 2026-09-27）：原来这里是「终端落法」两档 + 「同名塌缩」勾选框，
                 * 那一组控件连同设置项一起删了（落点固定，不再让用户选），位置让给这一格开关。
                 */
                ["OmitMiddleContinuationLayers"] = "ExtractionTab",

                /*
                 * ②页「特定解压」那一栏（用户 2026-09-24：规则清单的家在②页，总开关在①页）。
                 *
                 * ⚠ 它是**列表式**的设置项（和 EnginePriority 一样存一串 Id），所以它落在
                 * SettingsViewModel 上是那一栏的条目集合、落盘是 Settings.SpecialExtractionRules ——
                 * 界面里没有第二条写死规则的路径（加规则只动注册表）。
                 */
                ["SpecialExtractionRules"] = "ExtractionTab",
                ["SpecialExtractionToolTip"] = "TaskTab",
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
                ["RemoveRememberedBookCommand"] = "PasswordTab"
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

        private static string ViewsDirectory => Path.Combine(XamlBindingScan.RepositoryRoot, "src", "ArchiveFixer", "Views");

        private static string TabsDirectory => Path.Combine(ViewsDirectory, "Tabs");

        private static string Read(params string[] parts)
        {
            string path = Path.Combine(XamlBindingScan.RepositoryRoot, "src", "ArchiveFixer");

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

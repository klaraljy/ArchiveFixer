using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// ①「任务」页的「输出位置」那一格（用户 2026-09-25 第 27 条）。
    ///
    /// <para><b>用户原话</b>：「输出的指定位置可以放在主界面进行选择，这个没有问题，选项卡里面的也可以留着，
    /// 在添加文件夹和全选中间还有那么多的位置，如果选择解压到位置名字太长，可以简写，这只是个比方
    /// <c>E:\DeepSeekProjects\ArchiveFixer\ArchiveFixer.Tests\obj\Release\net8.0-windows\ref</c>
    /// 可以写成这样 <c>E:\DeepSeekProjects\ArchiveFixer\...\net8.0-windows\ref</c>，
    /// 反正我要求的就是我们最好能够看到完整的解压地址」。</para>
    ///
    /// <para>这一组钉三件事：</para>
    /// <list type="number">
    /// <item><description><b>省略规则</b>是一个纯函数（<see cref="PathMiddleEllipsis"/>）：长路径首段 + <c>\...\</c> + 末两段、
    /// 短路径原样、空 / UNC / 盘根都不崩；</description></item>
    /// <item><description><b>两处联动、只有一个真值</b>：①页那两个属性读写的就是
    /// <c>Settings.CustomOutputDirectory</c> 与 <c>Settings.ExtractToOriginalDirectory</c>，
    /// ②页改完①页立刻跟着变（反过来也一样），而且会真的发出属性变更通知（不是靠"重读一遍才发现"）；</description></item>
    /// <item><description><b>界面上真的挂了这一格</b>（XAML 里那一串绑定名逐条在），落在①页；
    /// ②页那个入口一个字没动（两页都留着，用户明确说"选项卡里面的也可以留着"）。</description></item>
    /// </list>
    ///
    /// <para><b>测试纪律</b>：构造 <c>MainViewModel</c> 会写进程级静态（递归工作区根），
    /// 所以整类进 <c>ArchiveFixerGlobalState</c> 集合、与其它同类用例串行，并在装配后立刻还原；
    /// 数据根 / 日志全在临时目录里，绝不碰用户真实数据。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class OutputLocationOnTaskTabTests : IDisposable
    {
        private readonly string _root;

        public OutputLocationOnTaskTabTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerOutputLocation", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 省略规则（纯函数）

        /// <summary>用户给的那个例子必须**逐字**成立（这条就是这一格的验收标准）。</summary>
        [Fact]
        public void 长路径_按用户给的例子中间省略()
        {
            Assert.Equal(
                @"E:\DeepSeekProjects\ArchiveFixer\...\net8.0-windows\ref",
                PathMiddleEllipsis.Elide(@"E:\DeepSeekProjects\ArchiveFixer\ArchiveFixer.Tests\obj\Release\net8.0-windows\ref"));
        }

        /// <summary>短路径（段数 ≤ 4）原样返回 —— 省略反而更长，而且会让人以为路径被改了。</summary>
        [Theory]
        [InlineData(@"E:\")]
        [InlineData(@"E:\输出")]
        [InlineData(@"E:\输出\222")]
        [InlineData(@"E:\输出\222\内容物")]
        [InlineData(@"C:\Windows")]
        public void 短路径_原样返回(string path)
        {
            Assert.Equal(path, PathMiddleEllipsis.Elide(path));
        }

        [Fact]
        public void 空路径_返回空串()
        {
            Assert.Equal(string.Empty, PathMiddleEllipsis.Elide(null));
            Assert.Equal(string.Empty, PathMiddleEllipsis.Elide(string.Empty));
            Assert.Equal(string.Empty, PathMiddleEllipsis.Elide("   "));
        }

        /// <summary>UNC 与"认不出的怪形状"一律不崩（最差也只是原样返回）。</summary>
        [Fact]
        public void UNC与怪形状_不崩且规则一致()
        {
            // 段数 ≤ 4 的 UNC 原样；更长的按"服务器 + 共享 + 一层目录"当头部。
            Assert.Equal(@"\\server\share", PathMiddleEllipsis.Elide(@"\\server\share"));
            Assert.Equal(
                @"\\server\share\folder1\...\folder3\folder4",
                PathMiddleEllipsis.Elide(@"\\server\share\folder1\folder2\folder3\folder4"));

            // 正斜杠归一后再分段（从别处粘过来的路径也认）。
            Assert.Equal(
                @"E:\aaaa\bbbb\...\eeee\ffff",
                PathMiddleEllipsis.Elide("E:/aaaa/bbbb/cccc/dddd/eeee/ffff"));

            // 盘根 + 结尾分隔符 / 只有分隔符：原样返回，不抛异常（短路径一个字都不改）。
            Assert.Equal(@"E:\a\b\c\", PathMiddleEllipsis.Elide(@"E:\a\b\c\"));
            Assert.Equal(@"\\", PathMiddleEllipsis.Elide(@"\\"));
            Assert.Equal(@"\", PathMiddleEllipsis.Elide(@"\"));

            // 长路径带结尾分隔符：按段切完照样省略（结尾那个分隔符不产生空段）。
            Assert.Equal(@"E:\aaaa\bbbb\...\dddd\eeee", PathMiddleEllipsis.Elide(@"E:\aaaa\bbbb\cccc\dddd\eeee\"));

            /*
             * 省略只在真的变短时才做：5 段的短路径省下来还不如省略号本身长，
             * 那就原样显示（免得"省略"反而更长、还让人以为路径被改了）。
             */
            Assert.Equal(@"E:\a\b\c\d", PathMiddleEllipsis.Elide(@"E:\a\b\c\d"));
        }

        // ================================================================ ② 两处联动、只有一个真值

        /// <summary>默认档（未指定位置）：那一行明说产物落在每个包自己的目录，而不是显示成空白。</summary>
        [Fact]
        public void 未指定位置_显示那句话而不是空白()
        {
            MainViewModel vm = CreateViewModel();

            Assert.True(vm.OutputLocationFollowsArchive);
            Assert.Equal(StatusText.OutputLocationUnspecifiedText, vm.OutputLocationDisplay);
            Assert.Equal(StatusText.OutputLocationFollowsArchiveHint, vm.OutputLocationToolTip);
        }

        /// <summary>
        /// ①页选一个位置 = 写进**同一个**设置项（`Settings.CustomOutputDirectory`），
        /// 同时把落点切到"指定位置"那一档；那一行显示的是**省略后**的路径，
        /// 完整路径在 ToolTip 里（用户要的就是"能看到完整的解压地址"）。
        /// </summary>
        [Fact]
        public void 任务页选位置_写进同一份设置_且ToolTip给完整路径()
        {
            MainViewModel vm = CreateViewModel();
            const string chosen = @"E:\DeepSeekProjects\ArchiveFixer\ArchiveFixer.Tests\obj\Release\net8.0-windows\ref";

            vm.SelectedOutputDirectory = chosen;

            Assert.Equal(chosen, vm.Settings.CustomOutputDirectory);
            Assert.False(vm.Settings.ExtractToOriginalDirectory);
            Assert.Equal(@"E:\DeepSeekProjects\ArchiveFixer\...\net8.0-windows\ref", vm.OutputLocationDisplay);
            Assert.Equal("完整路径：" + chosen, vm.OutputLocationToolTip);

            // ②页看到的是**同一份值**（它的路径输入框读的就是这个属性）。
            Assert.Equal(chosen, vm.SettingsEditor.CustomOutputDirectory);

            // 指定了位置 → ②页「指定位置」那条路径输入框是可用的（IsCustomOutputEnabled = !落点在源目录）。
            Assert.True(vm.SettingsEditor.IsCustomOutputEnabled);
        }

        /// <summary>
        /// ②页改落点 → ①页那一行**立刻**跟着变，而且是"发出通知"的那种变
        /// （不是靠下次重读才发现 —— 用户会以为改了没反应）。
        /// </summary>
        [Fact]
        public void 解压方式页改落点_任务页那一行立刻跟着变()
        {
            MainViewModel vm = CreateViewModel();
            var raised = new List<string?>();

            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.SettingsEditor.CustomOutputDirectory = @"D:\输出";

            Assert.Equal(@"D:\输出", vm.SelectedOutputDirectory);
            Assert.Contains(nameof(MainViewModel.OutputLocationDisplay), raised);
            Assert.Contains(nameof(MainViewModel.OutputLocationFollowsArchive), raised);

            // 换回"未指定位置"：两边的真值一起翻，①页显示回到那句话。
            vm.SettingsEditor.OutputPlacement = OutputPlacementOption.ArchiveNamedSubfolder;

            Assert.True(vm.Settings.ExtractToOriginalDirectory);
            Assert.Equal(StatusText.OutputLocationUnspecifiedText, vm.OutputLocationDisplay);

            // ②页也立刻看到同一件事（两处读的是同一份真值，不是各存一份）。
            Assert.Equal(OutputPlacementOption.ArchiveNamedSubfolder, vm.SettingsEditor.OutputPlacement);
            Assert.True(vm.SettingsEditor.Settings.ExtractToOriginalDirectory);
        }

        /// <summary>
        /// **用户真机报的那一条**（2026-09-25 第 31 条）：在①页点「选择…」挑一个目录之后，
        /// ②页「落点（解压到哪）」那几个控件**必须重新求值**。
        ///
        /// <para>为什么以前全绿却仍然坏：②页控件绑的是 <see cref="SettingsViewModel"/> 算出来的属性，
        /// 而 <c>AppSettings</c> 是普通 POCO；①页那条路以前只改设置**不发通知** ——
        /// 于是"只看值"的断言（<c>SettingsEditor.CustomOutputDirectory == chosen</c>）恒真，
        /// 界面却还停在老的『同名子文件夹』单选 + 灰着的『（不适用）』路径框。
        /// 所以这一条钉的是**通知本身**，这也是他嘴里"两处不同步、很意外"的机制。</para>
        ///
        /// <para>走的是真命令（<c>SelectOutputDirectoryCommand</c> → 私有 <c>SelectOutputDirectory()</c>），
        /// 与用户点按钮的路径**逐句相同**，不是绕过命令直接写属性。</para>
        /// </summary>
        [Fact]
        public void 任务页选择目录命令_解压方式页那几个控件必须收到通知()
        {
            const string chosen = @"<测试目录>\测试\CCC";

            MainViewModel vm = CreateViewModel(out _, chosen);
            var raised = new List<string?>();

            vm.SettingsEditor.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            Assert.True(vm.SelectOutputDirectoryCommand.CanExecute(null), "「选择…」按钮此刻应该是可点的");
            vm.SelectOutputDirectoryCommand.Execute(null);

            // 值：两页读的是同一份真值。
            Assert.Equal(chosen, vm.Settings.CustomOutputDirectory);
            Assert.False(vm.Settings.ExtractToOriginalDirectory);
            Assert.Equal(OutputPlacementOption.CustomNamedSubfolder, vm.SettingsEditor.OutputPlacement);
            Assert.True(vm.SettingsEditor.IsCustomOutputEnabled);

            // 通知：②页那几个绑定的属性一个都不能漏（漏一个，界面上就有一块停在旧状态）。
            Assert.Contains(nameof(SettingsViewModel.OutputPlacement), raised);
            Assert.Contains(nameof(SettingsViewModel.IsCustomOutputEnabled), raised);
            Assert.Contains(nameof(SettingsViewModel.OutputPlacementSummary), raised);
            Assert.Contains(nameof(SettingsViewModel.CustomOutputDirectory), raised);

            // ①页那一行自己也要跟着走（省略显示 + 完整路径）。
            Assert.Equal(chosen, vm.Settings.CustomOutputDirectory);
            Assert.False(vm.OutputLocationFollowsArchive);
            Assert.Contains("CCC", vm.OutputLocationToolTip, StringComparison.Ordinal);
        }

        /// <summary>
        /// 直接写 <see cref="MainViewModel.SelectedOutputDirectory"/>（拖放 / 恢复记忆 / 启动填充都走它）
        /// 也必须招呼②页 —— 这一条挡的是"只修了按钮那条路"。
        /// </summary>
        [Fact]
        public void 直接设置输出目录_也要招呼解压方式页()
        {
            MainViewModel vm = CreateViewModel();
            var raised = new List<string?>();

            vm.SettingsEditor.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.SelectedOutputDirectory = @"D:\输出";

            Assert.Contains(nameof(SettingsViewModel.OutputPlacement), raised);
            Assert.Contains(nameof(SettingsViewModel.IsCustomOutputEnabled), raised);
            Assert.Equal(OutputPlacementOption.CustomNamedSubfolder, vm.SettingsEditor.OutputPlacement);
        }

        /// <summary>
        /// ①页那个「未指定位置」开关：写的就是落点判据本身，并会招呼②页刷新一遍。
        /// </summary>
        [Fact]
        public void 任务页那个开关_与解压方式页是同一个值()
        {
            MainViewModel vm = CreateViewModel();

            vm.SelectedOutputDirectory = @"D:\输出";
            Assert.False(vm.OutputLocationFollowsArchive);

            vm.OutputLocationFollowsArchive = true;

            Assert.True(vm.Settings.ExtractToOriginalDirectory);
            Assert.Equal(OutputPlacementOption.ArchiveNamedSubfolder, vm.SettingsEditor.OutputPlacement);
            Assert.Equal(StatusText.OutputLocationUnspecifiedText, vm.OutputLocationDisplay);

            // 取消勾选但又没挑目录：必须说清下一步点哪里，绝不能显示成"已经指定好了"。
            vm.OutputLocationFollowsArchive = false;
            vm.SelectedOutputDirectory = string.Empty;

            Assert.False(vm.Settings.ExtractToOriginalDirectory);
            Assert.Equal(StatusText.OutputLocationNotChosenText, vm.OutputLocationDisplay);
        }

        /// <summary>
        /// 复制入口：①页右键那一项复制的是**完整**路径；没有指定位置时如实写日志，不假装复制成功。
        /// </summary>
        [Fact]
        public void 复制完整路径_复制的是全文而不是省略版()
        {
            CapturingClipboardViewModel vm = CreateViewModel(out LogService log);
            const string chosen = @"E:\DeepSeekProjects\ArchiveFixer\ArchiveFixer.Tests\obj\Release\net8.0-windows\ref";

            vm.SelectedOutputDirectory = chosen;
            vm.CopyOutputLocationCommand.Execute(null);

            Assert.Equal(chosen, Assert.Single(vm.ClipboardTexts));

            // 未指定位置：不写剪贴板，只留一条说得清的日志。
            vm.OutputLocationFollowsArchive = true;
            vm.ClipboardTexts.Clear();

            vm.CopyOutputLocationCommand.Execute(null);

            Assert.Empty(vm.ClipboardTexts);
            Assert.Contains(
                log.Logs.Select(item => item.Message),
                line => line.Contains("没有可复制的输出路径", StringComparison.Ordinal));
        }

        // ================================================================ ③ 界面上真的挂了（XAML）

        /// <summary>
        /// 这一格落在①页主操作条的**星号列**（「添加文件夹」与「全选」之间），
        /// 而且它必须能收缩（路径 TextTrimming + 单元格 ClipToBounds）——
        /// 用户点名"不许把全选 / 一键处理挤走"。
        /// </summary>
        [Fact]
        public void 任务页XAML_那一格挂在添加文件夹与全选之间且能收缩()
        {
            string taskTab = Read("Views", "Tabs", "TaskTab.xaml");

            Assert.Contains("OutputLocationDisplay", taskTab, StringComparison.Ordinal);
            Assert.Contains("OutputLocationFollowsArchive", taskTab, StringComparison.Ordinal);
            Assert.Contains("OutputLocationToolTip", taskTab, StringComparison.Ordinal);
            Assert.Contains("SelectOutputDirectoryCommand", taskTab, StringComparison.Ordinal);
            Assert.Contains("CopyOutputLocationCommand", taskTab, StringComparison.Ordinal);

            Assert.Contains(@"TextTrimming=""CharacterEllipsis""", taskTab, StringComparison.Ordinal);
            Assert.Contains(@"ClipToBounds=""True""", taskTab, StringComparison.Ordinal);

            // 落点：那一格必须在 `Grid.Column="1"`（星号列 = 添加文件夹与全选之间），
            // 不许塞进右边那串按钮的 WrapPanel（用户原话："在添加文件夹和全选中间还有那么多的位置"）。
            int cellStart = taskTab.IndexOf(@"<Grid Grid.Column=""1"" Margin=""12,0""", StringComparison.Ordinal);
            int wrapPanelStart = taskTab.IndexOf(@"<WrapPanel Grid.Column=""2""", StringComparison.Ordinal);

            Assert.True(cellStart > 0, "①页主操作条里找不到「输出位置」那一格");
            Assert.True(wrapPanelStart > cellStart, "那一格必须在右边那串按钮**之前**（也就是中间那一列）");

            // 文案只能来自 StatusText（AGENTS.md §7）。
            Assert.Contains("StatusText.OutputLocationLabel", taskTab, StringComparison.Ordinal);
            Assert.Contains("StatusText.OutputLocationFollowsArchiveLabel", taskTab, StringComparison.Ordinal);
            Assert.Contains("StatusText.OutputLocationCopyMenuText", taskTab, StringComparison.Ordinal);
        }

        /// <summary>
        /// 勾选那几个按钮**属于列表**（用户 2026-09-26："现在将一键处理那一行里有四个对文件操作的选项框
        /// 给移到下面来，移动到列表板块上端，这个之前占着绝大部分的位置，我输出的目录文件位置都挡的看不见了"）。
        ///
        /// <para>判据用**位置**：这四个命令必须出现在「任务列表」那一段里，⛔ 不许留在主操作条那一段
        /// （主操作条只剩 添加文件 / 添加文件夹 / 输出位置 / 只解压 / 一键处理 / 特定解压）。</para>
        /// </summary>
        [Fact]
        public void 勾选那四个按钮在任务列表那一段_不在主操作条里()
        {
            string taskTab = Read("Views", "Tabs", "TaskTab.xaml");

            int toolbarStart = taskTab.IndexOf("主操作条", StringComparison.Ordinal);
            int listStart = taskTab.IndexOf("任务列表", StringComparison.Ordinal);

            Assert.True(toolbarStart > 0, "①页里找不到主操作条那一段");
            Assert.True(listStart > toolbarStart, "①页里找不到任务列表那一段（它在主操作条后面）");

            foreach (string command in new[]
                     {
                         "SelectAllTasksCommand",
                         "SelectNoneTasksCommand",
                         "InvertTaskSelectionCommand",
                         "RemoveCheckedTasksCommand"
                     })
            {
                int index = taskTab.IndexOf(command, StringComparison.Ordinal);

                Assert.True(index > listStart, $"{command} 必须在任务列表那一段里（用户要求移到列表上端）");
            }
        }

        /// <summary>②页那个入口一个字没动（用户："选项卡里面的也可以留着"）。</summary>
        [Fact]
        public void 解压方式页的输出位置入口照旧()
        {
            string extraction = Read("Views", "Tabs", "ExtractionTab.xaml");

            Assert.Contains("SettingsEditor.OutputPlacement", extraction, StringComparison.Ordinal);
            Assert.Contains("SettingsEditor.CustomOutputDirectory", extraction, StringComparison.Ordinal);
            Assert.Contains("SettingsEditor.SelectOutputDirectoryCommand", extraction, StringComparison.Ordinal);
        }

        /// <summary>
        /// **进②页就对齐**（2026-09-25 第 31 条的第二道保险）：①页那条路已经统一发通知，
        /// 但只要将来有人再加一条写设置的路径漏了通知，用户切过去还是会看到旧状态 ——
        /// 所以切到②「解压方式」页时再按最新设置求值一次。这里钉的是**接线真的在**
        /// （TabControl 的 SelectionChanged + ②页那个 TabItem 有名字 + 处理器里确实调了刷新）。
        /// </summary>
        [Fact]
        public void 切到解压方式页_会再对齐一次落点显示()
        {
            string window = Read("MainWindow.xaml");
            string code = Read("MainWindow.xaml.cs");

            Assert.Contains(@"SelectionChanged=""MainTabControl_SelectionChanged""", window, StringComparison.Ordinal);
            Assert.Contains(@"x:Name=""ExtractionTabItem""", window, StringComparison.Ordinal);
            Assert.Contains("MainTabControl_SelectionChanged", code, StringComparison.Ordinal);
            Assert.Contains("NotifyOutputPlacementChanged", code, StringComparison.Ordinal);
            Assert.Contains("ExtractionTabItem", code, StringComparison.Ordinal);
        }

        // ================================================================ 装配

        private CapturingClipboardViewModel CreateViewModel()
        {
            return CreateViewModel(out _);
        }

        private CapturingClipboardViewModel CreateViewModel(out LogService logService)
        {
            return CreateViewModel(out logService, folderToChoose: null);
        }

        /// <summary>
        /// 装配一套真 VM。<paramref name="folderToChoose"/> 非空时，注入一个"用户挑好了这个目录"的假对话框
        /// —— 无界面宿主下真 <see cref="DialogService.ShowFolderBrowserDialog"/> 一律返回空串，
        /// 那样①页「选择…」这条真实路径根本走不到（2026-09-25 第 31 条就是为了走通它才把它改成 virtual）。
        /// </summary>
        private CapturingClipboardViewModel CreateViewModel(out LogService logService, string? folderToChoose)
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
            string previousSevenZipPath = Engines.ToolLocator.Default.CustomSevenZipExePath;

            logService = new LogService(pathService);

            try
            {
                return new CapturingClipboardViewModel(
                    new FileScanService(),
                    new ArchiveDetectService(),
                    new RenameService(),
                    new Engines.SevenZip.SevenZipEngine(),
                    new PasswordService(),
                    logService,
                    settingsService,
                    pathService,
                    new TaskSummaryService(),
                    new ClipboardService(),
                    folderToChoose == null ? new DialogService() : new StubFolderDialogService(folderToChoose));
            }
            finally
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
                Engines.ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;
            }
        }

        /// <summary>假对话框：直接回答"用户挑好了这个目录"（其余行为与真的一样）。</summary>
        private sealed class StubFolderDialogService : DialogService
        {
            private readonly string _folder;

            public StubFolderDialogService(string folder)
            {
                _folder = folder;
            }

            public override string ShowFolderBrowserDialog() => _folder;
        }

        /// <summary>
        /// 截住"实际写进剪贴板的文本"（与 <c>ViewStateGuardTests</c> 同一手法）：
        /// 无头进程里 <c>Clipboard.SetText</c> 必然不可用（没有 STA / OLE），
        /// 只能从 <see cref="MainViewModel.WriteClipboardText"/> 这个唯一出口观察。
        /// </summary>
        private sealed class CapturingClipboardViewModel : MainViewModel
        {
            public CapturingClipboardViewModel(
                FileScanService fileScanService,
                ArchiveDetectService archiveDetectService,
                RenameService renameService,
                IArchiveEngine archiveEngine,
                PasswordService passwordService,
                LogService logService,
                SettingsService settingsService,
                PathService pathService,
                TaskSummaryService taskSummaryService,
                ClipboardService clipboardService,
                DialogService dialogService)
                : base(
                    fileScanService,
                    archiveDetectService,
                    renameService,
                    archiveEngine,
                    passwordService,
                    logService,
                    settingsService,
                    pathService,
                    taskSummaryService,
                    clipboardService,
                    dialogService)
            {
            }

            public List<string> ClipboardTexts { get; } = new();

            internal override bool WriteClipboardText(string sanitizedText)
            {
                ClipboardTexts.Add(sanitizedText ?? string.Empty);
                return true;
            }
        }

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
    }
}

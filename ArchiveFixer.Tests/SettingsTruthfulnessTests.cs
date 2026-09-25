using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「设置项不许撒谎」这一组（2026-09-22 专项体检 · 清单第 1/3 条）。
    ///
    /// <para>三类问题各自钉一条：</para>
    /// <list type="number">
    /// <item><description><b>死开关</b>：配置里有一项、代码从来不读 —— 删掉它，并且旧配置里留着这个键
    /// 也不能影响启动（<c>System.Text.Json</c> 忽略未知成员）。</description></item>
    /// <item><description><b>静默丢弃用户输入</b>：工具路径填了个不存在的文件时，
    /// <c>AppSettings.Normalize</c> 会把它清空；保存路径必须在**清空之前**拦住并说清改法，
    /// 绝不能让"设置已保存"这句话盖住一次静默丢失。</description></item>
    /// <item><description><b>文案与行为对不上</b>：设置窗口里几句话曾经与代码实际行为不符
    /// （"拖拽导入后自动扫描"其实管三种导入入口、"主窗口里临时选的目录"那个入口早没了、
    /// 「自动重命名已存在文件」对 RAR 引擎退化成"跳过"）。这里把改好的措辞钉住，
    /// 免得下次改代码时又把界面甩在后面。</description></item>
    /// </list>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class SettingsTruthfulnessTests : IDisposable
    {
        private readonly string _root;

        public SettingsTruthfulnessTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSettingsTruth", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 死开关

        [Fact]
        public void 从不被读取的密码Trim开关_已经删掉()
        {
            // 它曾经是 AppSettings 上的一个属性（默认 true），但全项目没有任何一处读它，
            // 界面上也没有这一项 —— 用户在 appsettings.json 里把它改成 false，
            // 以为密码会被 Trim，实际一个字节的行为都不会变。那就是一句写在配置文件里的谎话。
            Assert.Null(typeof(AppSettings).GetProperty("PreservePasswordLeadingTrailingSpaces"));
        }

        [Fact]
        public void 旧配置里还留着删掉的键_不影响启动也不影响其它设置()
        {
            string dataRoot = Path.Combine(_root, "data");
            Directory.CreateDirectory(dataRoot);

            string settingsPath = Path.Combine(dataRoot, "appsettings.json");

            File.WriteAllText(
                settingsPath,
                """
                {
                  "PreservePasswordLeadingTrailingSpaces": false,
                  "MaxPasswordAttemptsPerLayer": 42,
                  "SourceHandling": "KeepInPlace"
                }
                """);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            AppSettings loaded = new SettingsService(pathService).Load();

            // 认不出的键被忽略，其它值照常读出来（升级不该让用户重配一遍）。
            Assert.Equal(42, loaded.MaxPasswordAttemptsPerLayer);
            Assert.Equal(SourceHandlingMode.KeepInPlace, AppSettings.ParseSourceHandling(loaded.SourceHandling));
        }

        [Fact]
        public void 改名预览开关的配置键改不动行为_这一事实写在属性注释里()
        {
            /*
             * PreviewBeforeRename 是**刻意**保留的：主流程恒为 true（不变量 3），
             * 而 Models/RenameOptions.cs 的 FromSettings 仍会读它，删属性会编译不过。
             * 既然删不掉，就必须把"这个键不产生行为差异"写在它能被读到的地方 ——
             * 否则它就是一个"看起来能关掉预览"的假开关。
             */
            PropertyInfo? property = typeof(AppSettings).GetProperty(nameof(AppSettings.PreviewBeforeRename));

            Assert.NotNull(property);

            string source = File.ReadAllText(
                Path.Combine(XamlBindingScan.RepositoryRoot, "ArchiveFixer", "Models", "AppSettings.cs"));

            Assert.Contains("这个配置键对本程序的行为没有任何影响", source, StringComparison.Ordinal);
        }

        // ================================================================ ② 工具路径

        [Fact]
        public void 工具路径填了不存在的文件_保存被拦下并说明改法()
        {
            SettingsViewModel viewModel = CreateViewModel();

            // 用户在设置窗口里手填的路径（发生在构造函数之后 —— 构造时会 Normalize 一次）。
            string typed = Path.Combine(_root, "并不存在的-7z.exe");

            viewModel.Settings.CustomSevenZipExePath = typed;

            viewModel.SaveCommand.Execute(null);

            // 没有关窗（DialogResult 仍为 null）= 保存没有发生。
            Assert.Null(viewModel.DialogResult);
            Assert.Contains("不存在", viewModel.Message, StringComparison.Ordinal);
            Assert.Contains("清空这一格", viewModel.Message, StringComparison.Ordinal);

            // 最要紧的一条：用户填的值**还在**（不是"被清空 + 告诉你保存成功"）。
            Assert.Equal(typed, viewModel.Settings.CustomSevenZipExePath);

            // 走正常的 Normalize 一定会被清空 —— 那正是必须拦在前面的原因。
            var probe = new AppSettings { CustomSevenZipExePath = typed };
            probe.Normalize();

            Assert.Equal(string.Empty, probe.CustomSevenZipExePath);
        }

        [Fact]
        public void UnRAR路径填了不存在的文件_同样被拦下()
        {
            SettingsViewModel viewModel = CreateViewModel();

            viewModel.Settings.CustomUnRarExePath = Path.Combine(_root, "并不存在的-UnRAR.exe");

            viewModel.SaveCommand.Execute(null);

            Assert.Null(viewModel.DialogResult);
            Assert.Contains("UnRAR.exe", viewModel.Message, StringComparison.Ordinal);
            Assert.Contains("内置", viewModel.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 工具路径留空或指向真实文件_保存通过()
        {
            string realFile = Path.Combine(_root, "7z.exe");
            File.WriteAllText(realFile, "占位（只验证路径存在）");

            SettingsViewModel withRealPath = CreateViewModel("data-a");
            withRealPath.Settings.CustomSevenZipExePath = realFile;
            withRealPath.SaveCommand.Execute(null);

            Assert.True(withRealPath.DialogResult);
            Assert.Equal(realFile, withRealPath.Settings.CustomSevenZipExePath);

            SettingsViewModel empty = CreateViewModel("data-b");
            empty.SaveCommand.Execute(null);

            Assert.True(empty.DialogResult);
        }

        private SettingsViewModel CreateViewModel(string dataFolder = "data")
        {
            return new SettingsViewModel(
                AppSettings.CreateDefault(),
                new SettingsService(new PathService
                {
                    DataRootDirectory = Path.Combine(_root, dataFolder)
                }));
        }

        [Fact]
        public void 路径校验本身_空与非空两种口径()
        {
            Assert.True(SettingsViewModel.ValidateToolExePath(null, "7z.exe 路径", "留空=内置", out string emptyMessage));
            Assert.Equal(string.Empty, emptyMessage);

            Assert.True(SettingsViewModel.ValidateToolExePath("   ", "7z.exe 路径", "留空=内置", out _));

            Assert.False(SettingsViewModel.ValidateToolExePath(
                Path.Combine(_root, "没有这个文件.exe"),
                "7z.exe 路径",
                "留空=内置",
                out string missingMessage));

            Assert.Contains("7z.exe 路径", missingMessage, StringComparison.Ordinal);
            Assert.Contains("留空=内置", missingMessage, StringComparison.Ordinal);
        }

        // ================================================================ ③ 文案与行为

        [Fact]
        public void 设置界面的自动扫描文案_覆盖三种导入入口()
        {
            string xaml = ReadSettingsSurfaceXaml();

            // 这个开关在 ScanCoordinator.AddPathsAsync 里判，管的是所有导入入口，
            // 不只拖拽 —— 旧文案只提拖拽，会让关掉它的人以为用按钮添加还会自动扫描。
            Assert.Contains("导入后自动扫描（拖拽 / 添加文件 / 添加文件夹）", xaml, StringComparison.Ordinal);
            Assert.Contains("重新扫描", xaml, StringComparison.Ordinal);
        }

        [Fact]
        public void 设置界面不再提主窗口那个不存在的临时输出目录入口()
        {
            string xaml = ReadSettingsSurfaceXaml();

            Assert.DoesNotContain(
                "主窗口里临时选的目录也不会写进设置",
                xaml,
                StringComparison.Ordinal);

            Assert.Contains("主窗口里没有临时选输出目录的入口", xaml, StringComparison.Ordinal);
        }

        [Fact]
        public void 设置界面如实说明覆盖策略的引擎差异()
        {
            string xaml = ReadSettingsSurfaceXaml();

            // 界面给了「自动重命名已存在文件」这一档，但它只对 7-Zip 有效：
            // UnRAR 落到 -o-（不覆盖、也不改名）。不说清楚就是"界面说一套、实际做另一套"。
            Assert.Contains("自动重命名已存在文件（仅 7-Zip 支持）", xaml, StringComparison.Ordinal);
            Assert.Contains("UnRAR", xaml, StringComparison.Ordinal);
            Assert.Contains("跳过已存在文件", xaml, StringComparison.Ordinal);
        }

        [Fact]
        public void 每个设置项的绑定路径都能在对应的ViewModel上找到()
        {
            /*
             * 静态兜底：WPF 对"绑定名写错"是**静默**的（那一格永远是空的）。
             * 运行时的绑定跟踪由 UiBindingReachabilityTests 负责；这一条只用**字符串级**检查
             * 兜住全部设置界面（用户 2026-09-24 第 11 条之后是 MainWindow + 六个选项卡；
             * 原来只有 SettingsWindow.xaml 一个文件）。
             *
             * ⚠ 清单**一件都不能少**：每一条都必须在界面上找得到 ——
             * "配置里能改、界面里找不到"对用户就是一句谎。
             */
            string xaml = ReadSettingsSurfaceXaml();

            string[] appSettingsBound = new[]
            {
                "RecursiveScan", "AutoScanAfterDrop", "ScanMode", "UnknownFormatAction", "DefaultExtension",
                "ConflictAction", "TestBeforeExtract", "OpenOutputFolderWhenDone", "ReportDangerousEntries",
                "CollectResultsToDirectory", "CollectTargetDirectory", "RecursionMode", "MaxRecursionDepth",
                "OverwriteMode", "MaxParallelExtractCount", "RestRemovalDefaultMode",

                /*
                 * ⚠ 2026-09-25 第 32 条删掉了两格，所以这里也少了两条：
                 * · DeleteSourceAfterExtract —— 手动「只解压」专用的删源开关退役（手动档与一键档读同一套设置）；
                 * · RestHandlingAfterVerify / SourceHandling —— 它们是③页两组单选，绑的是
                 *   SettingsEditor 的解析属性（不是 Settings.<原名>），由 InterfaceRefactorTests
                 *   的 危险模式那一套已退役 那一条负责钉住。
                 */
                "TryEmptyPasswordFirst", "UseGlobalPasswordForAllTasks", "EnableSidecarPassword",
                "MaxPasswordAttemptsPerLayer", "EnableLog", "LowProcessPriority", "RememberLastOutputDirectory",
                "CustomSevenZipExePath",

                /*
                 * 扫描侧的三项（2026-09-23 补的界面入口）。
                 *
                 * 它们本来就有实现（FileScanService 真的按它们过滤），但设置里一直没有入口 ——
                 * "配置里能改、界面里找不到"对用户就是一句谎。列进这份清单，防止以后被顺手删掉。
                 */
                "IncludeHiddenFiles", "IncludeSystemFiles", "MaxFileSizeLimit",

                /*
                 * 2026-09-24 第 15 条补的「解压前提醒」开关。
                 * 它和上面那些一样：界面上有这一格，代码里就**必须**真的读它
                 * （ExtractionCoordinator.ConfirmBatchRemindersAsync 的第一道判断）。
                 */
                "RemindBeforeExtract",

                /*
                 * 2026-09-24 第 15 / 17 条补的两个开关：
                 * · RemindJunkAfterImport —— 导入文件夹后就提醒无用物（ScanCoordinator 真的读它）；
                 * · SkipOneClickConfirm   —— 一键处理不再弹确认框（OneClickCoordinator 真的读它）。
                 * 两个都必须在界面上有"再打开"的入口，否则勾一次就再也回不来了。
                 */
                "RemindJunkAfterImport", "SkipOneClickConfirm",

                /*
                 * 2026-09-24「特定解压」的总开关（①任务页、一键处理旁边）。
                 * 它必须真的被解压管线读到 —— ExtractionCoordinator 的决定性判断就是
                 * SpecialExtractionPlan.FromSettings(Settings) 里的第一句（关着时规则清单一律不算）。
                 */
                "UseSpecialExtraction",

                /*
                 * 2026-09-25 第 25 条追加的「失败时保留中间产物」（③清理与删除页的「工作区残留」那一组里）。
                 * 它必须真的被收尾读到 —— ExtractionCoordinator.CleanupFailedTaskWorkspace
                 * 与 RecursiveExtractor 的失败清理都按它决定"清不清现场"，默认关 = 失败不留残留。
                 */
                "KeepFailedWorkspace"
            };

            foreach (string name in appSettingsBound)
            {
                Assert.True(
                    xaml.Contains("Settings." + name, StringComparison.Ordinal),
                    $"设置界面（MainWindow + 六个选项卡）里没有绑定 Settings.{name}（清单过时了？）");

                Assert.NotNull(typeof(AppSettings).GetProperty(name));
            }
        }

        /// <summary>
        /// 设置界面的**全部** XAML：主窗口 + 六个选项卡。
        ///
        /// <para>
        /// 为什么不再是单个文件（2026-09-24 第 11 条）：设置窗口退休了，它的控件按功能
        /// 搬进了②解压方式 / ③清理与删除 / ④密码 / ⑥设置四页。这几条断言钉的是
        /// "设置界面上写了什么"，与"写在哪个文件里"无关，所以扫并集 ——
        /// 逐项落在哪一页由 <c>InterfaceRefactorTests</c> 那张更细的表钉住。
        /// </para>
        /// </summary>
        private static string ReadSettingsSurfaceXaml()
        {
            string root = XamlBindingScan.RepositoryRoot;

            var builder = new System.Text.StringBuilder();

            builder.AppendLine(File.ReadAllText(Path.Combine(root, "ArchiveFixer", "MainWindow.xaml")));

            string tabsDirectory = Path.Combine(root, "ArchiveFixer", "Views", "Tabs");

            Assert.True(Directory.Exists(tabsDirectory), $"读不到选项卡目录：{tabsDirectory}");

            foreach (string file in Directory.EnumerateFiles(tabsDirectory, "*.xaml")
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine(File.ReadAllText(file));
            }

            return builder.ToString();
        }
    }
}

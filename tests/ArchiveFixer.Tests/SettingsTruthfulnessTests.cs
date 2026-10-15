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
                Path.Combine(XamlBindingScan.RepositoryRoot, "src", "ArchiveFixer", "Models", "AppSettings.cs"));

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
        public void 导入后自动扫描已成系统固定值_界面上不再有这一格()
        {
            /*
             * ⚠ 2026-10-14（用户最终设计：「设置：删除：工作区目录、启动时、扫描」+ 当场逐项拍板）：
             * 这一格连同「扫描与识别」整组移出界面，值钉成"开"。
             * ⛔ 判据不是删掉就完事 —— 改钉两件事实：界面上没有这一格，且值真的是系统值。
             */
            string xaml = ReadSettingsSurfaceXaml();

            Assert.DoesNotContain("导入后自动扫描", xaml, StringComparison.Ordinal);
            Assert.True(new ArchiveFixer.Models.AppSettings().AutoScanAfterDrop);
            Assert.True(new ArchiveFixer.Models.AppSettings().RecursiveScan);
        }

        [Fact]
        public void 输出位置入口不止一处这件事_仍然写着_现在在展示框的详细说明里()
        {
            /*
             * 老文案写"主窗口里没有临时选输出目录的入口"，这一条**当时是假的**：
             * ①页主操作栏就有「输出位置：… 选择…」+「未指定位置」那颗开关，
             * 写的就是同一个 Settings.CustomOutputDirectory —— 而当时的用例把这句话钉住了，
             * 于是"界面说反话"被测试保护着（2026-09-29 复核逮到）。
             *
             * ⚠ 2026-10-14：那一格（记住上次输出目录）已随「启动时行为」整组移出界面、值钉成"开"，
             * 但**这句事实必须继续写着** —— 它现在住在⑥页展示框的详细说明里 ⇒ 判据跟着搬。
             */
            Assert.Contains("「任务」页", ArchiveFixer.Models.StatusText.BoxDetailStartupBehaviorDetail, StringComparison.Ordinal);
            Assert.Contains("「解压方式」页", ArchiveFixer.Models.StatusText.BoxDetailStartupBehaviorDetail, StringComparison.Ordinal);

            Assert.DoesNotContain(
                "主窗口里没有临时选输出目录的入口",
                ReadSettingsSurfaceXaml(),
                StringComparison.Ordinal);
        }

        [Fact]
        public void 一键确认框的折叠区标题来自状态文案常量()
        {
            string xaml = File.ReadAllText(Path.Combine(XamlBindingScan.RepositoryRoot, "src", "ArchiveFixer", "Views", "OneClickOptionsWindow.xaml"));

            /*
             * 同一个标题曾经有两个版本：XAML 手写「落点 / 源包操作 / 删除操作」（用户 2026-09-27 拍板的三组），
             * 而 StatusText 里那条常量还停在上上一版（终端落法 / 源包处理）且**没人引用**。
             * AGENTS.md §7：界面状态字符串统一引用 StatusText 常量，禁止手写中文字面量。
             */
            Assert.Contains("Header=\"{x:Static models:StatusText.OneClickConfirmExpanderHeader}\"", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("Header=\"本次改一下", xaml, StringComparison.Ordinal);
            Assert.Contains("/ 源包操作 / 删除操作", ArchiveFixer.Models.StatusText.OneClickConfirmExpanderHeader, StringComparison.Ordinal);
        }

        [Fact]
        public void 覆盖策略的引擎差异照旧写着_只是从下拉框挪进了详细说明()
        {
            /*
             * 界面原来给了「自动重命名已存在文件」这一档，但它只对 7-Zip 有效：
             * UnRAR 落到 -o-（不覆盖、也不改名）。不说清楚就是"界面说一套、实际做另一套"。
             *
             * ⚠ 2026-10-14：那一栏已从②页删掉、值**钉成**「自动重命名已存在文件」（用户当场拍板），
             * ⇒ 这条引擎差异**更要紧**（用户连选都选不了了）⇒ 它必须继续写在详细说明里。
             */
            Assert.Contains("UnRAR", ArchiveFixer.Models.StatusText.NestedOverwriteDetail, StringComparison.Ordinal);
            Assert.Contains("跳过已存在文件", ArchiveFixer.Models.StatusText.NestedOverwriteDetail, StringComparison.Ordinal);

            Assert.Equal(
                "AutoRenameExisting",
                ArchiveFixer.Models.AppSettings.CreateDefault().OverwriteMode);

            // 界面上不再有那一栏（值固定），所以也不该再有它的选项文字。
            Assert.DoesNotContain("自动重命名已存在文件（仅 7-Zip 支持）", ReadSettingsSurfaceXaml(), StringComparison.Ordinal);
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
                /*
                 * ⚠ 2026-10-14（用户最终设计 + 当场逐项拍板）：下面这些项**已经移出界面、值固定成系统默认**
                 * ⇒ 它们不再属于"界面上必须找得到"这份清单（不在界面上 = 没有入口，正是设计要的）。
                 * 它们的值与"界面上没有入口"这两件事由 `FixedDefaultsTests` 逐项钉住：
                 * · RecursiveScan / AutoScanAfterDrop / ScanMode / UnknownFormatAction / DefaultExtension
                 * · ConflictAction / OverwriteMode / RecursionMode（嵌套三档已删）
                 * · IncludeHiddenFiles / IncludeSystemFiles / MaxFileSizeLimit
                 * · LowProcessPriority / RememberLastOutputDirectory
                 */
                "TestBeforeExtract", "OpenOutputFolderWhenDone", "ReportDangerousEntries",
                "CollectResultsToDirectory", "CollectTargetDirectory", "MaxRecursionDepth",
                "MaxParallelExtractCount",

                /*
                 * ⚠ 2026-09-25 第 32 条删掉了两格，所以这里也少了两条：
                 * · DeleteSourceAfterExtract —— 手动「只解压」专用的删源开关退役（手动档与一键档读同一套设置）；
                 * · RestHandlingAfterVerify / SourceHandling —— 它们是③页两组单选，绑的是
                 *   SettingsEditor 的解析属性（不是 Settings.<原名>），由 InterfaceRefactorTests
                 *   的 危险模式那一套已退役 那一条负责钉住。
                 */
                "TryEmptyPasswordFirst", "UseGlobalPasswordForAllTasks", "EnableSidecarPassword",
                "MaxPasswordAttemptsPerLayer", "EnableLog",
                "CustomSevenZipExePath",

                /*
                 * ⚠ 2026-10-14：扫描侧那三项（IncludeHiddenFiles / IncludeSystemFiles / MaxFileSizeLimit）
                 * 随「扫描与识别」整组移出界面、值钉成系统默认 ⇒ 从这份清单里去掉（见上面那段说明）。
                 */

                /*
                 * 2026-09-24 第 15 条补的「解压前提醒」开关。
                 * 它和上面那些一样：界面上有这一格，代码里就**必须**真的读它
                 * （ExtractionCoordinator.ConfirmBatchRemindersAsync 的第一道判断）。
                 */
                "RemindBeforeExtract",

                /*
                 * 2026-09-24 第 15 / 17 条补的两个开关：
                 * · SkipOneClickConfirm —— 一键处理不再弹确认框（OneClickCoordinator 真的读它），
                 *   必须在界面上有"再打开"的入口，否则勾一次就再也回不来了。
                 * · RemindJunkAfterImport —— ⛔ 2026-10-04 已按用户口径**从界面上整格删掉**
                 *   （原话：「要删除的是第二个你已经停止显示的，就是底层自动会做的」）：
                 *   无用物是"导入一完成就自动移出列表"这件事底层自动做的，界面不再摆那一格。
                 *   ⚠ `AppSettings.RemindJunkAfterImport` 属性**留着**（不动设置序列化，旧键安静忽略），
                 *   所以它不在这份"界面上必须找得到"的清单里。
                 */
                "SkipOneClickConfirm",

                /*
                 * 2026-09-24「特定解压」的总开关（①任务页、一键处理旁边）。
                 * 它必须真的被解压管线读到 —— ExtractionCoordinator 的决定性判断就是
                 * SpecialExtractionPlan.FromSettings(Settings) 里的第一句（关着时规则清单一律不算）。
                 */
                "UseSpecialExtraction",

                /*
                 * ⚠ 2026-10-14（用户最终设计：「设置：删除：工作区目录」+ 清理方式「工作区」改成默认）：
                 * 「失败时保留中间产物」这一格**连界面一起删了**（值钉成 false = 失败/取消也清干净）
                 * ⇒ 从这份"界面上必须找得到"的清单里去掉；值与"没有入口"由 `FixedDefaultsTests` 钉住。
                 */

                /*
                 * 2026-09-25 第 36 条：解压前的四条安全上限（⑥设置 →「安全上限」）。
                 * 以前它们是硬编码在 ResourceBudgetOptions 里的 4 GiB / 20 GiB —— 界面上一格都没有，
                 * 用户被拦下时只能看到一句像"这个包坏了"的判决。四条都必须在界面上找得到，
                 * 而且必须真的被读到（ExtractionCoordinator.BudgetLimits 是唯一出口）。
                 */
                "MaxSingleExtractedFileGiB", "MaxExtractedTotalGiB",
                "MaxExtractedFileCount", "MaxExtractionRatio",

                /*
                 * ⚠ 2026-10-14：`KeepFailedWorkspace`（「失败时保留中间产物」）已按最终设计
                 * 连界面一起删（工作区改成系统默认：失败/取消也清干净）⇒ 从这份清单里去掉，
                 * 值与"没有入口"由 `FixedDefaultsTests` 钉住。
                 */

                /*
                 * 2026-09-25 第 44 条追加之二：⑥设置 →「日志」里的「详细日志（排查用）」。
                 *
                 * 在它之前，"成功任务的全部细节"只存在于一个**测试口子**（KeepTaskDetailInLog）——
                 * 用户想排查单个包只能改代码。它必须真的被读到：
                 * ExtractionCoordinator.VerboseTaskLogEnabled 就是唯一出口
                 * （与那个测试口子合成同一个行为，⛔ 不许各写一份判断）。
                 */
                "VerboseLog",

                /*
                 * 2026-09-25 第 42 条方案 A（他选的"做，默认关"）：②页的
                 * 「容器里装的是分卷第 1 卷时，自动接上同目录的后续卷」。
                 *
                 * 它必须真的被管线读到 —— ExtractionCoordinator 里那句
                 * "引擎刚说过分卷缺失 + 开关打开 + 容器里抠出过东西"就是唯一入口。
                 */
                "AssembleSplitVolumesFromContainer"
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

            builder.AppendLine(File.ReadAllText(Path.Combine(root, "src", "ArchiveFixer", "MainWindow.xaml")));

            string tabsDirectory = Path.Combine(root, "src", "ArchiveFixer", "Views", "Tabs");

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

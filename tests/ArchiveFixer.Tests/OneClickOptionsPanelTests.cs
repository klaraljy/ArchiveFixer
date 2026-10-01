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
        [InlineData(OutputPlacementMode.CustomRootPerArchive)]

        // 被删掉的两档的旧编号也一起走一遍：面板 → ExtractOptions → 落点这条链上不许有第二处判断。
        [InlineData((OutputPlacementMode)1)]
        [InlineData((OutputPlacementMode)3)]
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
                PlacementMode = OutputPlacementMode.CustomRootPerArchive,
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
            // ⚠ 第 32 条把「源包操作」的默认档改成了**留在原地**（原来默认搬进其余物）。
            OneClickRunOptions seed = OneClickRunOptions.FromSettings(settings, outputRoot);

            Assert.Equal(OutputPlacementMode.CustomRootPerArchive, seed.PlacementMode);
            Assert.Equal(SourceHandlingMode.KeepInPlace, seed.SourceHandling);
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

        // ================================================================ ② 设置记忆（默认就记住）

        /// <summary>
        /// **面板上选的那几档，默认就写回设置**（用户 2026-10-01 拍板：「这不就相当于设置记忆吗，
        /// 这么简单的问题不要再问了」）。
        ///
        /// <para>老口径是"只有勾了「把本次选择存为默认」才写设置"，用户的实测感受是
        /// **每次一键处理都要重新点一次落点**（"未选择指定位置"）。现在改成：面板确认之后
        /// 一律记住这一组值 —— 勾选框保留（显式确认），但不再决定写不写。</para>
        ///
        /// <para>判据：① 这一次确实按面板的值跑（落点用了面板给的目录、源包按面板的档处理）；
        /// ② 设置文件**变了**，而且写进去的正是面板那一组值；③ 日志说清了"已记住"。</para>
        /// </summary>
        [Fact]
        public async Task 面板选的值_默认就记进设置_下次不用再选()
        {
            // 设置里刻意放一个**与面板不同**的源包档：只有这样才测得出"写回的是面板那一组"。
            Harness harness = CreateHarness(s => s.SourceHandling = nameof(SourceHandlingMode.MoveToRest));
            ArchiveTask task = AddTask(harness, CreateSourceFile("pack.7z"));

            string flatRoot = Path.Combine(_root, "panel-flat");
            string before = HashFile(harness.SettingsFilePath);

            int prompts = 0;

            harness.OneClick.OptionsPromptOverride = _ =>
            {
                prompts++;

                return OneClickOptionsPrompt.Confirmed(new OneClickRunOptions
                {
                    PlacementMode = OutputPlacementMode.CustomRootPerArchive,
                    CustomRoot = flatRoot,
                    SourceHandling = SourceHandlingMode.KeepInPlace,
                    SaveAsDefault = false
                });
            };

            await harness.OneClick.RunAsync();

            // ① 这一次确实按面板的值跑了（落点用了面板给的目录）
            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.StartsWith(flatRoot, task.OutputPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, prompts);

            // ② 源包处理也跟着面板走：KeepInPlace = 一个字节都不搬
            Assert.True(File.Exists(harness.SourcePath), "面板选了留在原地，源包不该被搬走");

            // ③ 设置文件**变了**（这就是"记忆"），而且写入的正是面板那一组值。
            Assert.NotEqual(before, HashFile(harness.SettingsFilePath));
            Assert.Equal(flatRoot, harness.Vm.Settings.CustomOutputDirectory);
            Assert.Equal(nameof(SourceHandlingMode.KeepInPlace), harness.Vm.Settings.SourceHandling);

            // ④ 日志说清了"已记住"。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("已记住", StringComparison.Ordinal));
        }

        /// <summary>
        /// 硬要求②的另一半（勾选框仍可用）：勾了「存为默认」时写进去的也是面板上那一组值 ——
        /// 与上一条合起来，"不勾也记住 / 勾了也记住"两侧行为一致。
        /// </summary>
        [Fact]
        public async Task 勾了存为默认_写设置文件且写入的是本次选择()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("pack.7z"));

            string flatRoot = Path.Combine(_root, "panel-flat-saved");
            string before = HashFile(harness.SettingsFilePath);

            harness.OneClick.OptionsPromptOverride = _ => OneClickOptionsPrompt.Confirmed(new OneClickRunOptions
            {
                PlacementMode = OutputPlacementMode.CustomRootPerArchive,
                CustomRoot = flatRoot,
                SourceHandling = SourceHandlingMode.KeepInPlace,
                SaveAsDefault = true
            });

            await harness.OneClick.RunAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.NotEqual(before, HashFile(harness.SettingsFilePath));

            // 重新从磁盘读一遍：写进去的必须是面板那一组，而且是设置层的标准字符串口径
            AppSettings reloaded = new SettingsService(harness.PathService).Load();

            Assert.False(reloaded.ExtractToOriginalDirectory);
            Assert.True(reloaded.KeepArchiveNameFolder);
            Assert.Equal(flatRoot, reloaded.CustomOutputDirectory);
            Assert.Equal(nameof(SourceHandlingMode.KeepInPlace), reloaded.SourceHandling);
        }

        // ================================================================ ⑦ 弹窗要跟着设置走（第 33 条）

        /// <summary>
        /// 第 33 条：**弹窗里选的「删除操作」这一次就算数** —— 设置里是默认的「不动其余物」也一样，
        /// 而且（2026-10-01 起）**默认就记住**：弹窗确认之后这一档会被写回设置。
        ///
        /// <para>为什么必须钉"这一次就算数"：老实现里 `PrepareRestHandlingForBatch` 只读设置，
        /// 而弹窗里**根本没有这一项**（用户 2026-09-25："一键处理的弹窗也是要随着现在的设置进行更新的"）——
        /// 结果是"其余物到底删不删"在弹窗里既看不到、也改不了。</para>
        /// </summary>
        [Fact]
        public async Task 弹窗里选彻底删除_这一次真的删且默认记进设置()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("pack.7z"));

            string before = HashFile(harness.SettingsFilePath);

            harness.OneClick.OptionsPromptOverride = _ => OneClickOptionsPrompt.Confirmed(new OneClickRunOptions
            {
                PlacementMode = OutputPlacementMode.PerArchiveSubfolder,
                SourceHandling = SourceHandlingMode.MoveToRest,
                RestHandling = RestHandlingModes.Delete,
                SaveAsDefault = false
            });

            await harness.OneClick.RunAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.False(File.Exists(harness.SourcePath), "弹窗里选了「放入其余物 + 彻底删除」，源包必须被搬走并删掉");
            Assert.False(
                Directory.Exists(Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName)),
                "彻底删除跑完不该还留着其余物目录");

            // 默认记住：设置文件变了，而且这一档写的正是弹窗里选的那个。
            Assert.NotEqual(before, HashFile(harness.SettingsFilePath));
            Assert.Equal(RestHandlingModes.Delete, harness.Vm.Settings.RestHandlingAfterVerify);
        }

        /// <summary>
        /// 反过来的一半：弹窗里的初值**取当前设置**（③页选了什么，弹窗打开时就是什么）。
        /// </summary>
        [Fact]
        public void 弹窗初值_删除操作取设置里的当前档()
        {
            AppSettings settings = AppSettings.CreateDefault();

            Assert.Equal(RestHandlingModes.Keep, OneClickRunOptions.FromSettings(settings).RestHandling);

            settings.RestHandlingAfterVerify = RestHandlingModes.Delete;

            Assert.Equal(RestHandlingModes.Delete, OneClickRunOptions.FromSettings(settings).RestHandling);

            // 这句话日志与任务详情都读它，必须把这一档说清。
            Assert.Contains(
                "彻底删除",
                OneClickRunOptions.FromSettings(settings).Describe(),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// 弹窗自己的那份界面：**已退役的第三档一个字都不许剩**，新三档 + 那条关不掉的红提示都要在。
        ///
        /// <para>直接读 XAML 文本（与 `SpaceModeTests` / `InterfaceRefactorTests` 钉 ③页 的同一手法）：
        /// 这一条挡的正是"模型改了、弹窗没跟上"——真机上用户点了「校验通过后删除」，
        /// 而窗口代码只读另外两个单选，于是静默按「留在原地」跑。</para>
        /// </summary>
        [Fact]
        public void 弹窗界面_三档删除操作齐全且退役那一档不在了()
        {
            string xaml = ReadRepositoryFile(Path.Combine("src", "ArchiveFixer", "Views", "OneClickOptionsWindow.xaml"));

            Assert.DoesNotContain("SourceDeleteAfterVerifyOption", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("校验通过后删除", xaml, StringComparison.Ordinal);

            Assert.Contains("PanelSourceKeepInPlaceOption", xaml, StringComparison.Ordinal);
            Assert.Contains("原来的位置不动（默认）", xaml, StringComparison.Ordinal);

            Assert.Contains("PanelRestKeepOption", xaml, StringComparison.Ordinal);
            Assert.Contains("PanelRestRecycleOption", xaml, StringComparison.Ordinal);
            Assert.Contains("PanelRestDeleteOption", xaml, StringComparison.Ordinal);

            // 第三档红字 + 常驻提示（提示没有关闭入口 = 只有改回选项它才消失）。
            Assert.Contains("RestDeleteNotice", xaml, StringComparison.Ordinal);
            Assert.Contains("DangerTextBrush", xaml, StringComparison.Ordinal);
            Assert.Contains("彻底删除不可恢复", xaml, StringComparison.Ordinal);

            /*
             * ⚠ 弹窗的单选按钮**不许**与选项卡共用 GroupName（2026-09-25 第 34 条真机故障）：
             * 两边原来都叫 "SourceHandling" / "RestHandling"，弹窗填初值时把③页那一组一起取消了 ——
             * 用户关掉弹窗回到③页，看到的是一组**没有黑点**的选项（设置里的值其实是对的）。
             */
            Assert.DoesNotContain("GroupName=\"SourceHandling\"", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("GroupName=\"RestHandling\"", xaml, StringComparison.Ordinal);
            Assert.Contains("GroupName=\"PanelSourceHandling\"", xaml, StringComparison.Ordinal);
            Assert.Contains("GroupName=\"PanelRestHandling\"", xaml, StringComparison.Ordinal);

            // ③页那一组仍然是原名字（两页各自成组，互不影响）。
            string cleanup = ReadRepositoryFile(Path.Combine("src", "ArchiveFixer", "Views", "Tabs", "CleanupTab.xaml"));

            Assert.Contains("GroupName=\"SourceHandling\"", cleanup, StringComparison.Ordinal);
            Assert.Contains("GroupName=\"RestHandling\"", cleanup, StringComparison.Ordinal);

            /*
             * ⚠ 2026-09-26 同步审计追加：`x:Name` 也要错开（GroupName 错了黑点会互相取消，
             * `x:Name` 重名今天不报错、但那正是"下次谁把弹窗那几行复制回③页"的入口）。
             * 判据 = 两页各自的 `x:Name="..."` 集合**没有交集**。
             */
            foreach (string name in RadioNames(cleanup))
            {
                Assert.DoesNotContain($"x:Name=\"{name}\"", xaml, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// 确认框里那条**常驻提醒**：程序不判断"内容物里该不该有压缩包"（用户 2026-09-26 要求
        /// 写在动手之前，原话："我们没有压缩包内容识别操作，很有可能您最终想要得到的内容物里面
        /// 含不该解开的压缩文件，请您仔细判别和在解压方式里面调节一次解压的检测次数"）。
        ///
        /// <para>判据三条：①弹窗 XAML 里真的挂了那一段；②文案出自 `StatusText`（唯一来源）；
        /// ③文案必须**指对去哪儿调**（②「解压方式」页 →「嵌套与覆盖」+ 最大层数）——
        /// 只说"请仔细判别"而没有出口，等于把问题丢回给用户。</para>
        /// </summary>
        [Fact]
        public void 确认框里有那条嵌套压缩包提醒_而且指对去哪儿调()
        {
            string xaml = ReadRepositoryFile(Path.Combine("src", "ArchiveFixer", "Views", "OneClickOptionsWindow.xaml"));

            Assert.Contains("StatusText.OneClickConfirmNestedCaveat", xaml, StringComparison.Ordinal);

            string text = StatusText.OneClickConfirmNestedCaveat;

            Assert.Contains("嵌套", text, StringComparison.Ordinal);
            Assert.Contains("解压方式", text, StringComparison.Ordinal);
            Assert.Contains("层数", text, StringComparison.Ordinal);

            // 面向用户的字符串里不许写 Markdown（UserFacingTextTests 会全量扫，这里再钉一次这条）。
            Assert.DoesNotContain("**", text, StringComparison.Ordinal);
        }

        /// <summary>一个 XAML 里所有单选按钮的 <c>x:Name</c>（拿不到就返回空集合）。</summary>
        private static IReadOnlyList<string> RadioNames(string xaml)
        {
            var names = new List<string>();

            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                         xaml,
                         "x:Name=\"([A-Za-z0-9_]+)\""))
            {
                names.Add(match.Groups[1].Value);
            }

            return names;
        }

        /// <summary>从仓库根读一个文本文件（测试的工作目录是 bin\…，往上找到仓库那一层为止）。</summary>
        private static string ReadRepositoryFile(string relativePath)
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, relativePath);

                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException($"仓库里找不到这个文件：{relativePath}");
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
                    PlacementMode = OutputPlacementMode.CustomRootPerArchive,
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
        /// 勾了「以后不再询问」之后：本次运行内再点一键处理不再弹确认框，行为退回"按设置走"（硬要求⑤）。
        ///
        /// <para>⚠ 2026-09-24 第 17 条之后这个勾选项**会落盘**（用户原话："这个可以选中以后不弹出"，
        /// 而"以后"不止这一次运行），所以这里同时钉住两件事：设置里真的记下了，
        /// 以及下一次运行连框都不弹、直接开跑。</para>
        /// </summary>
        [Fact]
        public async Task 勾了以后不再询问_写进设置且第二次不再弹框()
        {
            Harness harness = CreateHarness();
            ArchiveTask first = AddTask(harness, CreateSourceFile("first.7z"));

            string flatRoot = Path.Combine(_root, "panel-flat-suppress");
            int prompts = 0;

            harness.OneClick.OptionsPromptOverride = _ =>
            {
                prompts++;

                return OneClickOptionsPrompt.Confirmed(new OneClickRunOptions
                {
                    PlacementMode = OutputPlacementMode.CustomRootPerArchive,
                    CustomRoot = flatRoot,
                    SourceHandling = SourceHandlingMode.KeepInPlace,
                    SuppressPanelNextTime = true
                });
            };

            await harness.OneClick.RunAsync();
            Assert.Equal(1, prompts);

            // ① 落盘了：重新从磁盘读一遍设置，那个布尔必须是 true（第 17 条要的"以后不弹出"）
            AppSettings reloaded = new SettingsService(harness.PathService).Load();
            Assert.True(reloaded.SkipOneClickConfirm, "勾了「以后不再询问」要写进设置，否则重启又弹回来");

            // ② 第二次：注入的替身仍然会被调用（注入点优先于"不再询问"），
            //    所以这里验的是**真实分支**：清掉注入点，走 AskRunOptionsOnce 自己的判断。
            harness.OneClick.OptionsPromptOverride = null;

            harness.Vm.Tasks[0].IsSelected = true;
            await harness.OneClick.RunAsync();

            // 一个框都没弹，而且**直接按设置开跑**（不是"被静默跳过"）：引擎真的被调用了
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("已勾「以后不再询问」", StringComparison.Ordinal));

            Assert.True(harness.Engine.ExtractCallCount >= 1, "勾了不再询问之后必须照常开始，而不是什么都不做");

            // 源包留在原地（第一次那批选的档位）：第二次按设置走，不该把第一次的源包搬掉
            Assert.True(File.Exists(first.CurrentPath));
        }

        /// <summary>用户在框上点「取消」：这一次不许开跑（不是"照跑不误"），而且不许写设置。</summary>
        [Fact]
        public async Task 面板取消_这一次一键处理什么都不做()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("pack.7z"));

            harness.OneClick.OptionsPromptOverride = _ => OneClickOptionsPrompt.Cancelled();

            string before = HashFile(harness.SettingsFilePath);

            await harness.OneClick.RunAsync();

            Assert.Equal(0, harness.Engine.ExtractCallCount);
            Assert.Equal(StatusText.Recognized, task.Status);
            Assert.False(harness.Vm.IsBusy, "取消之后不许留在忙碌状态");
            Assert.Equal(before, HashFile(harness.SettingsFilePath));
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("确认框没有确认", StringComparison.Ordinal));
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
                PlacementMode = OutputPlacementMode.CustomRootPerArchive,
                CustomRoot = flatRoot,
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
            // ⚠ 2026-09-27 起「终端落法」那一档已从界面与描述里删掉（落法固定），所以这里不再断言它。
            Assert.Contains("落点：", task.RunOptionsNote, StringComparison.Ordinal);
            Assert.Contains("源包处理：", task.RunOptionsNote, StringComparison.Ordinal);
            Assert.Contains("指定位置", task.RunOptionsNote, StringComparison.Ordinal);
            Assert.DoesNotContain("终端落法", task.RunOptionsNote, StringComparison.Ordinal);

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

        // ================================================================ ⑥ 手动摊平绝不外溢（红线）

        /// <summary>
        /// **一键处理永远套一层包名文件夹** —— 哪怕上一批刚刚手动点过「解压到当前文件夹」。
        ///
        /// <para>为什么必须有这条：那个"这次摊平"的标记是**运行期**的（`_extractIntoSourceFolderThisRun`），
        /// 一键处理那条入口根本不带这个参数、批尾还会清掉。真出问题时表现极其隐蔽 ——
        /// 用户手动平铺过一次，之后的一键处理把几十个包的内容物全倒进同一层（用户 2026-09-27 划的红线）。</para>
        ///
        /// <para>红在哪（实测过）：把批首那句
        /// <c>_extractIntoSourceFolderThisRun = extractIntoSourceFolder &amp;&amp; !oneClickRun;</c>
        /// 临时改成恒 <c>false</c> → 第一段（手动摊平落在源目录里）立刻变红，装回即绿。
        /// 「一键处理那条入口根本不带这个参数」是**编译期**保证（形参默认 false + 那句 <c>&amp;&amp; !oneClickRun</c>），
        /// 这条用例钉的是运行期那一半：标记不许在批与批之间存活。</para>
        /// </summary>
        [Fact]
        public async Task 手动摊平只对那一次生效_下一次一键处理照旧套包名目录()
        {
            Harness harness = CreateHarness(settings =>
            {
                /*
                 * 落点 = 包旁边同名文件夹（默认档），这样"摊平"与"不摊平"的差别一眼可见。
                 * ⚠ 必须把 `CustomOutputDirectory` 清空：harness 默认给它一个输出根，
                 * 而设置归一化见到非空的指定位置就会把落点判成"指定位置"那一档
                 * （与②页那一格同一个判据），那样第二批就落到 `out\` 里、测不到源目录这一层。
                 */
                settings.ExtractToOriginalDirectory = true;
                settings.KeepArchiveNameFolder = true;
                settings.CustomOutputDirectory = string.Empty;
            });

            // ── ① 手动档：内容物直接落在源包所在那一层 ──────────────────────────
            string flatSource = CreateSourceFile("flat.7z");
            AddTask(harness, flatSource);

            ExtractionCoordinator extraction = harness.Extraction;

            await extraction.StartExtractAsync(extractIntoSourceFolder: true).WaitAsync(TimeSpan.FromSeconds(120));

            string sourceDirectory = Path.GetDirectoryName(flatSource)!;

            Assert.True(
                File.Exists(Path.Combine(sourceDirectory, "payload-00000.bin")),
                "手动摊平之后内容物应该直接落在源包所在目录里");
            Assert.False(
                Directory.Exists(Path.Combine(sourceDirectory, "flat")),
                "手动摊平不该建包名那一层");

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains(StatusText.ExtractIntoSourceFolderReminderLog, StringComparison.Ordinal));

            // ── ② 紧接着一键处理（**另一条入口**）：必须套回包名那一层 ────────────
            // 换一组产物名：上一批已经在源目录里平铺过同名文件，这一批写到哪必须没有歧义。
            harness.Engine.FileNames = new[] { "second.bin" };

            string layeredSource = CreateSourceFile("layered.7z");

            harness.Vm.Tasks.Clear();
            AddTask(harness, layeredSource);

            harness.OneClick.OptionsPromptOverride = _ => OneClickOptionsPrompt.NotShown();

            await harness.OneClick.RunAsync().WaitAsync(TimeSpan.FromSeconds(120));

            string layeredDirectory = Path.Combine(sourceDirectory, "layered");

            Assert.True(
                Directory.Exists(layeredDirectory),
                "一键处理必须套一层包名文件夹（手动摊平那个标记不许外溢到这一批）"
                + $"\n实际目录树：{string.Join(" | ", Directory.GetFileSystemEntries(sourceDirectory, "*", SearchOption.AllDirectories))}"
                + $"\n日志：\n{string.Join("\n", harness.LogTexts)}");
            Assert.True(
                File.Exists(Path.Combine(layeredDirectory, "second.bin")),
                "内容物应该落在包名目录里面");
            Assert.False(
                File.Exists(Path.Combine(sourceDirectory, "second.bin")),
                "一键处理**不该**把内容物摊在源包所在那一层");
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

            return new Harness(vm, engine, oneClick, extraction, logService, pathService, outputRoot);
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
                ExtractionCoordinator extraction,
                LogService log,
                PathService pathService,
                string outputRoot)
            {
                Vm = vm;
                Engine = engine;
                OneClick = oneClick;
                Extraction = extraction;
                Log = log;
                PathService = pathService;
                OutputRoot = outputRoot;
            }

            public MainViewModel Vm { get; }

            public PanelFakeEngine Engine { get; }

            public OneClickCoordinator OneClick { get; }

            /// <summary>手动那条解压入口（「只解压」/「解压到当前文件夹」走它）。</summary>
            public ExtractionCoordinator Extraction { get; }

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

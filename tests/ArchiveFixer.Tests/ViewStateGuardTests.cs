using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// MainViewModel 这一层的守卫：
    /// ① 忙碌标志是**嵌套计数**（内层协调器结束不许把外层的忙清掉，异常路径也必须复位）；
    /// ② 密码列表窗口开着的时候，密码本摘要就要跟着变；
    /// ③ 剪贴板四个入口统一走脱敏出口（AGENTS.md §6 第 5 条）。
    ///
    /// 构造 MainViewModel 会写两个进程级静态（7z 路径、递归工作区根目录），
    /// 所以本类与 InnerLayerContinuationTests 同属一个"不并行"的集合，并在装配后立刻还原静态。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class ViewStateGuardTests : IDisposable
    {
        private readonly string _root;

        public ViewStateGuardTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerViewState", Guid.NewGuid().ToString("N"));
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

        // ---------------------------------------------------------------- ① 忙碌嵌套

        [Fact]
        public async Task 嵌套忙碌_内层结束不会复位外层标志()
        {
            Fixture fixture = CreateFixture();

            Assert.False(fixture.Vm.IsBusy);
            Assert.False(fixture.Vm.StopCommand.CanExecute(null), "不忙的时候「停止后续」不该可点");

            // 外层：一键处理进入"忙"
            fixture.Vm.EnterBusy();

            Assert.True(fixture.Vm.IsBusy);
            Assert.True(fixture.Vm.StopCommand.CanExecute(null), "外层还在跑，「停止后续」必须可点");
            Assert.True(fixture.Vm.CancelCurrentCommand.CanExecute(null));

            /*
             * 内层：ScanCoordinator 自己会 IsBusy = true / finally IsBusy = false
             * （AutoScanAfterDrop 打开时它内部还会再嵌一层识别）。
             * 旧实现里这一句跑完之后 IsBusy 已经变成 false —— 外层还在跑，
             * 按钮却灰了、守卫也失效，用户能在间隙里再点一次解压。
             */
            await fixture.Vm.AddPathsAsync(new[] { fixture.SampleFile });

            Assert.NotEmpty(fixture.Vm.Tasks);
            Assert.True(fixture.Vm.IsBusy, "内层的 finally 不许把外层的忙碌状态清掉");
            Assert.True(fixture.Vm.StopCommand.CanExecute(null));

            fixture.Vm.ExitBusy();

            Assert.False(fixture.Vm.IsBusy);
            Assert.False(fixture.Vm.StopCommand.CanExecute(null));
            Assert.True(fixture.Vm.AddFilesCommand.CanExecute(null), "全部退出后普通命令必须恢复可点");
        }

        [Fact]
        public void 异常退出后忙碌标志必须复位()
        {
            Fixture fixture = CreateFixture();

            try
            {
                fixture.Vm.EnterBusy();

                throw new InvalidOperationException("模拟一键处理中途抛异常");
            }
            catch (InvalidOperationException)
            {
                // 协调器的 catch 就是这么写的：异常照常上抛，复位放在 finally。
            }
            finally
            {
                fixture.Vm.ExitBusy();
            }

            Assert.False(fixture.Vm.IsBusy);
            Assert.False(fixture.Vm.StopCommand.CanExecute(null));
            Assert.True(fixture.Vm.AddFilesCommand.CanExecute(null), "异常之后界面不许被永久灰掉");
        }

        [Fact]
        public void 多退一次不会让计数变成负数_界面不会被永久灰掉()
        {
            Fixture fixture = CreateFixture();

            // 没有对应 EnterBusy 的退出：夹在 0，绝不能变成负数
            // （负数之后所有 Enter/Exit 都配不平，界面会一直显示"忙"）。
            fixture.Vm.ExitBusy();
            Assert.False(fixture.Vm.IsBusy);

            fixture.Vm.EnterBusy();
            Assert.True(fixture.Vm.IsBusy);

            fixture.Vm.ExitBusy();
            Assert.False(fixture.Vm.IsBusy);
            Assert.True(fixture.Vm.AddFilesCommand.CanExecute(null));
        }

        [Fact]
        public void 多层嵌套时只有最外层退出才复位()
        {
            Fixture fixture = CreateFixture();

            fixture.Vm.EnterBusy();
            fixture.Vm.EnterBusy();
            fixture.Vm.EnterBusy();

            Assert.True(fixture.Vm.IsBusy);

            fixture.Vm.ExitBusy();
            Assert.True(fixture.Vm.IsBusy, "还有两层没退出");

            fixture.Vm.ExitBusy();
            Assert.True(fixture.Vm.IsBusy, "还有一层没退出");

            fixture.Vm.ExitBusy();
            Assert.False(fixture.Vm.IsBusy);
        }

        // ---------------------------------------------------------------- ② 密码本摘要实时刷新

        [Fact]
        public void 密码列表窗口开着时导入密码本_主界面摘要立刻刷新()
        {
            Fixture fixture = CreateFixture();

            Assert.Contains("未加载", fixture.Vm.PasswordBookSummary, StringComparison.Ordinal);

            // 窗口用的 ViewModel 与主界面共用同一个 PasswordService（MainViewModel.OpenPasswordList 就是这么建的）
            var listViewModel = new PasswordListViewModel(fixture.PasswordService, new DialogService());

            Action unhook = MainViewModel.HookPasswordBookSummaryRefresh(
                listViewModel,
                fixture.Vm.RefreshPasswordBookSummary);

            // 窗口里"导入密码本"内部做的两步：服务导入 + 列表重载。
            // 注意：此时窗口**还没关**（真实流程里 ShowDialog 尚未返回）。
            string bookPath = WritePasswordBook("主密码\n备用密码\n");
            fixture.PasswordService.ImportPasswordList(bookPath);
            listViewModel.ReloadFromService();

            Assert.Contains("2 条", fixture.Vm.PasswordBookSummary, StringComparison.Ordinal);

            // 退订之后不再跟着变：证明刷新确实来自这条接线，而不是别的路径顺手刷新了
            unhook();

            fixture.PasswordService.ImportPasswordList(WritePasswordBook("第三条\n"));
            listViewModel.ReloadFromService();

            Assert.Contains("2 条", fixture.Vm.PasswordBookSummary, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- ③ 剪贴板脱敏

        [Fact]
        public void 剪贴板入口统一脱敏_不出现明文密码()
        {
            const string secret = "SecretPass123";

            CapturingFixture fixture = CreateCapturingFixture();

            var task = new ArchiveTask(Path.Combine(_root, "locked.7z"))
            {
                Index = 1,
                Status = StatusText.ExtractFailed,
                PasswordStatus = StatusText.PasswordNeed,
                // 错误原文里夹着一条"密码：xxx"形态的内容 —— 这正是 PasswordMasker 要吃掉的东西
                ErrorMessage = "解压失败：密码：" + secret
            };

            fixture.Vm.CopyTaskInfoCommand.Execute(task);
            fixture.Vm.CopyTaskPathCommand.Execute(task);
            fixture.Vm.CopyTaskErrorCommand.Execute(task);

            Assert.Equal(3, fixture.Vm.ClipboardTexts.Count);

            Assert.All(
                fixture.Vm.ClipboardTexts,
                text => Assert.DoesNotContain(secret, text, StringComparison.Ordinal));

            // 任务信息：错误信息那行被脱敏（密码状态那行是"密码状态："，不含冒号紧邻，不该被误伤）
            Assert.Contains("密码：******", fixture.Vm.ClipboardTexts[0], StringComparison.Ordinal);
            Assert.Contains("密码状态：" + StatusText.PasswordNeed, fixture.Vm.ClipboardTexts[0], StringComparison.Ordinal);

            // 路径入口：普通路径原样保留（脱敏不该把正常内容也改掉）
            Assert.Equal(task.CurrentPath, fixture.Vm.ClipboardTexts[1]);

            // 错误信息入口：只剩脱敏后的形态
            Assert.Equal("解压失败：密码：******", fixture.Vm.ClipboardTexts[2]);
        }

        // ---------------------------------------------------------------- 装配

        private sealed class Fixture
        {
            public required MainViewModel Vm { get; init; }

            public required PasswordService PasswordService { get; init; }

            public required string SampleFile { get; init; }
        }

        private sealed class CapturingFixture
        {
            public required CapturingClipboardViewModel Vm { get; init; }
        }

        /// <summary>
        /// 真引擎外面不需要包一层（这一组测试不解压），但 MainViewModel 要求一个 IArchiveEngine。
        /// </summary>
        private Fixture CreateFixture()
        {
            TestServices services = BuildServices();

            MainViewModel vm = CreateViewModel(services, (engine, log) => new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                engine,
                services.PasswordService,
                log,
                services.SettingsService,
                services.PathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService()));

            return new Fixture
            {
                Vm = vm,
                PasswordService = services.PasswordService,
                SampleFile = services.SampleFile
            };
        }

        private CapturingFixture CreateCapturingFixture()
        {
            TestServices services = BuildServices();

            var vm = (CapturingClipboardViewModel)CreateViewModel(services, (engine, log) => new CapturingClipboardViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                engine,
                services.PasswordService,
                log,
                services.SettingsService,
                services.PathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService()));

            return new CapturingFixture { Vm = vm };
        }

        private MainViewModel CreateViewModel(TestServices services, Func<SevenZipEngine, LogService, MainViewModel> factory)
        {
            // MainViewModel 的构造会写这两个进程级静态，构造完立刻还原（同 InnerLayerContinuationTests）。
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            MainViewModel vm = factory(new SevenZipEngine(), services.LogService);

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            return vm;
        }

        private sealed class TestServices
        {
            public required PathService PathService { get; init; }

            public required SettingsService SettingsService { get; init; }

            public required PasswordService PasswordService { get; init; }

            public required LogService LogService { get; init; }

            public required string SampleFile { get; init; }
        }

        /// <summary>全套服务都落在临时目录里，绝不碰用户的目录与设置（AGENTS.md §8）。</summary>
        private TestServices BuildServices()
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
            settings.CustomSevenZipExePath = string.Empty;
            settingsService.Save(settings);

            string sampleFile = Path.Combine(_root, "samples", "plain.7z");
            Directory.CreateDirectory(Path.GetDirectoryName(sampleFile)!);

            // 真的 7z 魔数：识别阶段会把它当成 7z，不会走"格式未知"的分支。
            File.WriteAllBytes(sampleFile, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 });

            return new TestServices
            {
                PathService = pathService,
                SettingsService = settingsService,
                PasswordService = new PasswordService { DataRootDirectory = dataRoot },
                LogService = new LogService(pathService),
                SampleFile = sampleFile
            };
        }

        private string WritePasswordBook(string text)
        {
            string path = Path.Combine(_root, "密码本-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        /// <summary>
        /// 截住"实际写进剪贴板的文本"。
        /// 无头进程里 <c>Clipboard.SetText</c> 必然不可用（没有 STA / OLE），
        /// 所以只能从 <see cref="MainViewModel.WriteClipboardText"/> 这个唯一出口观察 ——
        /// 这也正是"四个入口都过脱敏"能被验证的原因。
        /// </summary>
        private sealed class CapturingClipboardViewModel : MainViewModel
        {
            public CapturingClipboardViewModel(
                FileScanService fileScanService,
                ArchiveDetectService archiveDetectService,
                RenameService renameService,
                ArchiveFixer.Engines.IArchiveEngine archiveEngine,
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

            internal override bool WriteClipboardText(string? text)
            {
                ClipboardTexts.Add(text ?? string.Empty);
                return true;
            }
        }
    }
}

using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 用户 2026-09-24 第 15 条的回归测试：**导入之后就要提醒无用物，而且要能从列表里删掉**。
    ///
    /// <para>用户原话：「列表要能删无用物；每次操作的选完文件夹，就要出一个无用物提醒，
    /// 用户可以选中关闭以后就不用触发了」。</para>
    ///
    /// <para>这一组钉四件事：</para>
    /// <list type="number">
    /// <item><description>导入之后就扫（而不是等他点一键处理）、判据与 §9.7 同一套；</description></item>
    /// <item><description><b>能被列出来</b>：导入文件夹时无用物自己也会进任务列表，
    /// 所以"属于本批任务就不报"这条保护必须只覆盖**是压缩包**的任务（否则提醒永远是空的 —— 见
    /// <c>SourceJunkScanner.CollectProtectedPaths</c> 的说明）；</description></item>
    /// <item><description>程序对无用物**一个都不动**，"从列表里移除"只动任务列表；</description></item>
    /// <item><description>勾「以后不再提醒」写进设置，之后导入一次都不弹。</description></item>
    /// </list>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class ImportJunkReminderTests : IDisposable
    {
        private readonly string _root;

        public ImportJunkReminderTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerImportJunk", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 扫得出来

        /// <summary>
        /// 真窗口宿主（这里用注入的替身）里：导入文件夹之后弹一次提醒，里面列出的正是那些无用物，
        /// 而且**任务列表里那些行也能被列出来**（它们同样是任务 —— 见类注释第 2 条）。
        /// </summary>
        [Fact]
        public async Task 导入文件夹之后_无用物被列出来()
        {
            string folder = CreateSourceFolder();

            Harness harness = CreateHarness(new DialogService());
            var reminder = new ReminderStub();
            harness.Vm.JunkReminderOverride = reminder.Ask;

            await harness.Vm.AddPathsAsync(new[] { folder });

            Assert.Equal(1, reminder.Calls);

            // 提醒正文里两个无用物都在，源包（.7z）不在
            Assert.Contains("说明.txt", reminder.LastMessage, StringComparison.Ordinal);
            Assert.Contains("网址.url", reminder.LastMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("pack.7z", reminder.LastMessage, StringComparison.Ordinal);

            // 日志里也有这一条（可追溯）
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("导入后提醒：", StringComparison.Ordinal)
                        && line.Contains("说明.txt", StringComparison.Ordinal));

            // 程序对无用物一个都没动
            Assert.True(File.Exists(Path.Combine(folder, "说明.txt")));
            Assert.True(File.Exists(Path.Combine(folder, "网址.url")));
        }

        /// <summary>点「从列表里移除这些」：任务列表里那两行没了，源文件照样在。</summary>
        [Fact]
        public async Task 选从列表里移除_只动列表不动源文件()
        {
            string folder = CreateSourceFolder();

            Harness harness = CreateHarness(new DialogService());
            var reminder = new ReminderStub { Keep = false };
            harness.Vm.JunkReminderOverride = reminder.Ask;

            await harness.Vm.AddPathsAsync(new[] { folder });

            Assert.Equal(1, reminder.Calls);

            // 两个无用物已经从列表里消失
            Assert.DoesNotContain(
                harness.Vm.Tasks,
                task => task.FileName.Equals("说明.txt", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                harness.Vm.Tasks,
                task => task.FileName.Equals("网址.url", StringComparison.OrdinalIgnoreCase));

            // 源文件一个字节都没动
            Assert.True(File.Exists(Path.Combine(folder, "说明.txt")));
            Assert.True(File.Exists(Path.Combine(folder, "网址.url")));

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("从任务列表里移除", StringComparison.Ordinal));
        }

        /// <summary>点「知道了」：什么都不动（列表也保持原样）。</summary>
        [Fact]
        public async Task 选知道了_列表与源文件都不动()
        {
            string folder = CreateSourceFolder();

            Harness harness = CreateHarness(new DialogService());
            var reminder = new ReminderStub { Keep = true };
            harness.Vm.JunkReminderOverride = reminder.Ask;

            await harness.Vm.AddPathsAsync(new[] { folder });

            Assert.Equal(1, reminder.Calls);
            Assert.Contains(
                harness.Vm.Tasks,
                task => task.FileName.Equals("说明.txt", StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(Path.Combine(folder, "说明.txt")));
        }

        // ================================================================ ② 无界面宿主 / 开关

        /// <summary>
        /// 无界面宿主（单测、控制台宿主）：**不弹窗、不死等**，只写一行日志 —— 导入照常完成。
        /// </summary>
        [Fact]
        public async Task 无界面宿主_只写日志不弹窗()
        {
            string folder = CreateSourceFolder();

            Harness harness = CreateHarness(new DialogService());

            await harness.Vm.AddPathsAsync(new[] { folder });

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains(StatusText.ImportJunkReminderNoHostLog, StringComparison.Ordinal));

            Assert.True(File.Exists(Path.Combine(folder, "说明.txt")));
        }

        /// <summary>勾了「以后不再提醒」：写进设置，之后导入一次都不提醒。</summary>
        [Fact]
        public async Task 勾了以后不再提醒_写进设置且之后不再提醒()
        {
            string folder = CreateSourceFolder();

            Harness harness = CreateHarness(new DialogService());
            var reminder = new ReminderStub { Keep = true, OptionChecked = true };
            harness.Vm.JunkReminderOverride = reminder.Ask;

            await harness.Vm.AddPathsAsync(new[] { folder });

            Assert.Equal(1, reminder.Calls);

            // ① 写进设置（跨重启有效）
            AppSettings reloaded = new SettingsService(harness.PathService).Load();
            Assert.False(reloaded.RemindJunkAfterImport);

            // ② 再导入一次：一个框都不弹
            await harness.Vm.AddPathsAsync(new[] { folder });

            Assert.Equal(1, reminder.Calls);

            // ③ 而且它也是可逆的：把开关打开就恢复提醒
            harness.Vm.SaveRemindJunkAfterImport(true);

            Assert.True(new SettingsService(harness.PathService).Load().RemindJunkAfterImport);
        }

        /// <summary>设置里关掉开关：导入之后**一次都不扫、不提醒**（连日志都不写）。</summary>
        [Fact]
        public async Task 开关关掉_导入后完全不提醒()
        {
            string folder = CreateSourceFolder();

            Harness harness = CreateHarness(
                new DialogService(),
                settings => settings.RemindJunkAfterImport = false);

            var reminder = new ReminderStub();
            harness.Vm.JunkReminderOverride = reminder.Ask;

            await harness.Vm.AddPathsAsync(new[] { folder });

            Assert.Equal(0, reminder.Calls);
            Assert.DoesNotContain(
                harness.LogTexts,
                line => line.Contains("导入后提醒：", StringComparison.Ordinal));
        }

        // ================================================================ ③ 列表里删无用物

        /// <summary>「移除勾选的」：只移除勾选的那些，源文件不动，没有勾选时只提示一句。</summary>
        [Fact]
        public async Task 移除勾选的任务_只动列表()
        {
            string folder = CreateSourceFolder();

            Harness harness = CreateHarness(new DialogService(), settings => settings.RemindJunkAfterImport = false);

            await harness.Vm.AddPathsAsync(new[] { folder });

            List<ArchiveTask> tasks = harness.Vm.Tasks.ToList();
            Assert.True(tasks.Count >= 2);

            // 一个都不勾：只提示，不动
            foreach (ArchiveTask task in tasks)
            {
                task.IsSelected = false;
            }

            harness.Vm.RemoveCheckedTasksCommand.Execute(null);

            Assert.Equal(tasks.Count, harness.Vm.Tasks.Count);

            // 勾其中一个：只移除它
            ArchiveTask doomed = harness.Vm.Tasks[0];
            string doomedPath = doomed.CurrentPath;

            doomed.IsSelected = true;
            harness.Vm.RemoveCheckedTasksCommand.Execute(null);

            Assert.DoesNotContain(harness.Vm.Tasks, task => ReferenceEquals(task, doomed));
            Assert.True(File.Exists(doomedPath), "移除任务不许动源文件");
            Assert.True(File.Exists(Path.Combine(folder, "说明.txt")));
        }

        // ================================================================ 装配

        /// <summary>造一个文件夹：一个（假）包 + 两个打包者常带的无用物。</summary>
        private string CreateSourceFolder()
        {
            string folder = Path.Combine(_root, "src");
            Directory.CreateDirectory(folder);

            File.WriteAllText(Path.Combine(folder, "pack.7z"), "not a real archive - the engine is faked in these tests");
            File.WriteAllText(Path.Combine(folder, "说明.txt"), "这是打包者附带的说明，不是压缩包");
            File.WriteAllText(Path.Combine(folder, "网址.url"), "[InternetShortcut]");

            return folder;
        }

        private Harness CreateHarness(DialogService dialogService, Action<AppSettings>? configure = null)
        {
            string dataRoot = Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CacheRootDirectory = dataRoot;
            settings.CustomOutputDirectory = Path.Combine(_root, "out");
            settings.ExtractToOriginalDirectory = false;
            settings.RecursionMode = "SingleLayer";

            // 导入后**不要**自动扫描：这一组测的是"导入 + 提醒"，不是识别（假包识别出来也不是归档）。
            settings.AutoScanAfterDrop = false;

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var logService = new LogService(pathService);

            string? previousWorkspaceRoot = Extraction.RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = Engines.ToolLocator.Default.CustomSevenZipExePath;

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                new JunkReminderFakeEngine(),
                new PasswordService(),
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                dialogService);

            Extraction.RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            Engines.ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            return new Harness(vm, logService, pathService);
        }

        private sealed class Harness
        {
            public Harness(MainViewModel vm, LogService log, PathService pathService)
            {
                Vm = vm;
                Log = log;
                PathService = pathService;
            }

            public MainViewModel Vm { get; }

            public LogService Log { get; }

            public PathService PathService { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);
        }

        /// <summary>
        /// 可控的提醒替身：记下被问了几次、正文是什么，并按设置回答"知道了 / 从列表里移除"与勾选状态。
        /// （真窗口在无界面宿主里不会弹，所以只能用替身证明"问过没有、问的是什么"。）
        /// </summary>
        private sealed class ReminderStub
        {
            /// <summary>true = 主按钮「知道了」；false = 次按钮「从列表里移除这些」。</summary>
            public bool Keep { get; set; } = true;

            public bool OptionChecked { get; set; }

            public int Calls { get; private set; }

            public string LastMessage { get; private set; } = string.Empty;

            public ImportJunkAnswer Ask(string message)
            {
                Calls++;
                LastMessage = message;

                return new ImportJunkAnswer { Keep = Keep, OptionChecked = OptionChecked };
            }
        }

        /// <summary>本组用例不解压：一个永远不可用的假引擎就够（构造函数只读它的能力位）。</summary>
        private sealed class JunkReminderFakeEngine : Engines.IArchiveEngine
        {
            public string Id => "none";

            public string DisplayName => "假引擎";

            public string Version => "1.0";

            public bool IsAvailable => false;

            public Engines.EngineCapabilities Capabilities { get; } = new();

            public Task<Engines.ArchiveProbeResult> ProbeAsync(
                Engines.ArchiveRequest request,
                System.Threading.CancellationToken cancellationToken = default) =>
                Task.FromResult(new Engines.ArchiveProbeResult { IsArchive = false });

            public Task<Engines.ArchiveListResult> ListAsync(
                Engines.ArchiveRequest request,
                System.Threading.CancellationToken cancellationToken = default) =>
                Task.FromResult(new Engines.ArchiveListResult());

            public Task<Engines.ArchiveOperationResult> TestAsync(
                Engines.ArchiveRequest request,
                System.Threading.CancellationToken cancellationToken = default) =>
                Task.FromResult(new Engines.ArchiveOperationResult { Success = false });

            public Task<Engines.ArchiveOperationResult> ExtractAsync(
                Engines.ArchiveRequest request,
                ExtractOptions options,
                System.Threading.CancellationToken cancellationToken = default) =>
                Task.FromResult(new Engines.ArchiveOperationResult { Success = false });
        }
    }
}

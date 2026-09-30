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
    /// 工作区残留的扫描与清理（用户 2026-09-24 第 22 条）。
    ///
    /// <para><b>起因</b>：用户实测在 <c>&lt;程序目录&gt;\data\work</c> 下攒了 10 个目录 / 5.7 GB，
    /// 问"为什么失败后你会留下这个残留"。保留是**故意的**（失败 / 取消时那是用户唯一的一份产物线索，
    /// AGENTS.md §6 不变量 12/13），但程序以前只写一行没体积的日志、也没有清理入口。</para>
    ///
    /// <para>这一组钉两件事：① 残留**量得准**（体积 / 文件数 / 量不出来不假装 0）；
    /// ② 清理**只动它该动的**（工作区根目录的直属子目录，别的什么都不碰，删前必须确认）。</para>
    ///
    /// <para>⚠ 必须与其它同类用例**串行**跑（<c>ArchiveFixerGlobalState</c>，见
    /// <c>InnerLayerContinuationTests</c> 顶部的 CollectionDefinition）：这一组要构造
    /// <c>MainViewModel</c>，而它的构造函数会写进程级静态（7z 路径、递归工作区根，以及
    /// 2026-09-24 第 23 条之后"当前生效的工作区根"），并行跑会互相抢 ——
    /// 实测过一次全量里这条偶发翻红、单跑必过（就是少了这个特性）。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class WorkspaceCleanupTests : IDisposable
    {
        private readonly string _root;

        public WorkspaceCleanupTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerWorkspaceCleanup", Guid.NewGuid().ToString("N"));
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

        // ================================================================ 量得准

        /// <summary>扫描列出每个残留目录的体积、文件数与时间（用户要"看得见"）。</summary>
        [Fact]
        public void 扫描_列出每个残留的体积与文件数()
        {
            string workRoot = CreateWorkRoot();

            CreateLeftover(workRoot, "0001_auto.7z.001-933d88ea", ("a.bin", 2048), ("b.bin", 1024));
            CreateLeftover(workRoot, "0002_auto.7z.001-381ff338", ("c.bin", 4096));

            IReadOnlyList<WorkspaceLeftover> leftovers = WorkspaceCleanupService.Scan(workRoot);

            Assert.Equal(2, leftovers.Count);

            WorkspaceLeftover first = leftovers.Single(item => item.Name == "0001_auto.7z.001-933d88ea");

            Assert.Equal(2, first.FileCount);
            Assert.Equal(3072, first.TotalBytes);
            Assert.False(first.MeasurementFailed);
            Assert.True(first.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-5));

            Assert.Equal(7168, WorkspaceCleanupService.TotalBytesOf(leftovers));
            Assert.Contains("个目录", WorkspaceCleanupService.Describe(leftovers), StringComparison.Ordinal);
        }

        /// <summary>工作区根目录下的**散文件**不是工作区（那是别的工具留下的，一个都不许删）。</summary>
        [Fact]
        public void 扫描_根目录下的散文件不算工作区()
        {
            string workRoot = CreateWorkRoot();

            File.WriteAllText(Path.Combine(workRoot, "readme.txt"), "别人放在这里的文件");
            CreateLeftover(workRoot, "0001_auto.7z.001-933d88ea", ("a.bin", 16));

            IReadOnlyList<WorkspaceLeftover> leftovers = WorkspaceCleanupService.Scan(workRoot);

            Assert.Single(leftovers);
            Assert.Equal("0001_auto.7z.001-933d88ea", leftovers[0].Name);
        }

        /// <summary>工作区根目录不存在 / 为空串：返回空列表，不抛。</summary>
        [Fact]
        public void 扫描_根目录不存在时返回空列表()
        {
            Assert.Empty(WorkspaceCleanupService.Scan(Path.Combine(_root, "不存在的目录")));
            Assert.Empty(WorkspaceCleanupService.Scan(null));
            Assert.Empty(WorkspaceCleanupService.Scan("   "));
        }

        /// <summary>体积换算写成人话（界面与日志都用它）。</summary>
        [Theory]
        [InlineData(0, "0 字节")]
        [InlineData(512, "512 字节")]
        [InlineData(2048, "2.0 KB")]
        [InlineData(5 * 1024 * 1024, "5.0 MB")]
        [InlineData(3L * 1024 * 1024 * 1024, "3.00 GB")]
        public void 体积换算(long bytes, string expected)
        {
            Assert.Equal(expected, WorkspaceCleanupService.FormatSize(bytes));
        }

        // ================================================================ 只动它该动的

        /// <summary>清理：勾了哪个删哪个，其余留着。</summary>
        [Fact]
        public void 清理_只删传进来的那些目录()
        {
            string workRoot = CreateWorkRoot();

            string doomed = CreateLeftover(workRoot, "0001_auto.7z.001-933d88ea", ("a.bin", 16));
            string kept = CreateLeftover(workRoot, "0002_auto.7z.001-381ff338", ("b.bin", 16));

            IReadOnlyList<WorkspaceCleanupOutcome> outcomes = WorkspaceCleanupService.Cleanup(
                workRoot,
                new[] { doomed });

            Assert.Single(outcomes);
            Assert.True(outcomes[0].Deleted);
            Assert.False(Directory.Exists(doomed));
            Assert.True(Directory.Exists(kept), "没勾的目录一个字节都不许动");
        }

        /// <summary>
        /// 越界一律不删（红线，不变量 13）：工作区**外面**的目录、工作区根目录**本身**、
        /// 比直属子目录**更深**的层级，三种都要拒绝，而且都要说清为什么。
        /// </summary>
        [Fact]
        public void 清理_越界路径一个字节都不动()
        {
            string workRoot = CreateWorkRoot();
            string leftover = CreateLeftover(workRoot, "0001_auto.7z.001-933d88ea", ("a.bin", 16));

            string outside = Path.Combine(_root, "外面的目录");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");

            string deeper = Path.Combine(leftover, "layer-001");
            Directory.CreateDirectory(deeper);

            IReadOnlyList<WorkspaceCleanupOutcome> outcomes = WorkspaceCleanupService.Cleanup(
                workRoot,
                new[] { outside, workRoot, deeper });

            Assert.Equal(3, outcomes.Count);
            Assert.All(outcomes, outcome => Assert.False(outcome.Deleted));

            Assert.True(Directory.Exists(outside), "工作区外面的目录绝不许删");
            Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
            Assert.True(Directory.Exists(workRoot), "工作区根目录本身绝不许删");
            Assert.True(Directory.Exists(deeper), "更深的层级不是我们造的目录，绝不许删");
            Assert.True(Directory.Exists(leftover));

            Assert.All(
                outcomes,
                outcome => Assert.Contains("不是工作区根目录的直属子目录", outcome.Message, StringComparison.Ordinal));
        }

        /// <summary>目录本来就不在了（用户自己删过）算成功，不报成失败。</summary>
        [Fact]
        public void 清理_目录已不存在算成功()
        {
            string workRoot = CreateWorkRoot();
            string missing = Path.Combine(workRoot, "0001_auto.7z.001-933d88ea");

            IReadOnlyList<WorkspaceCleanupOutcome> outcomes = WorkspaceCleanupService.Cleanup(
                workRoot,
                new[] { missing });

            Assert.Single(outcomes);
            Assert.True(outcomes[0].Deleted);
        }

        /// <summary>清理不动根目录下的散文件（用户放在那儿的东西，不归我们管）。</summary>
        [Fact]
        public void 清理_根目录下的散文件留着()
        {
            string workRoot = CreateWorkRoot();

            string stray = Path.Combine(workRoot, "readme.txt");
            File.WriteAllText(stray, "别人放在这里的文件");

            string doomed = CreateLeftover(workRoot, "0001_auto.7z.001-933d88ea", ("a.bin", 16));

            WorkspaceCleanupService.Cleanup(workRoot, new[] { doomed });

            Assert.False(Directory.Exists(doomed));
            Assert.True(File.Exists(stray));
        }

        // ================================================================ 界面那一层

        /// <summary>
        /// 启动扫描之后：界面上那一行有内容、「清理工作区」可点；点了（确认框答"是"）真的清掉，
        /// 清完那一行消失。无界面宿主里确认框的降级方向是"取消"，所以这里注入一个会答"是"的替身
        /// —— 否则"用户确认后才删"这条永远走不到真正删除那一半。
        /// </summary>
        [Fact]
        public async Task 界面_有残留就显示一行_确认后清掉()
        {
            string dataRoot = Path.Combine(_root, "data");
            string workRoot = Path.Combine(dataRoot, "work");

            Directory.CreateDirectory(workRoot);

            string leftover = CreateLeftover(workRoot, "0001_auto.7z.001-933d88ea", ("a.bin", 2048));

            var dialogService = new ConfirmingDialogService();
            VmHarness harness = CreateViewModel(dataRoot, dialogService);
            MainViewModel vm = harness.Vm;

            Assert.True(vm.HasWorkspaceLeftovers);
            Assert.Contains("1 个目录", vm.WorkspaceLeftoverBanner, StringComparison.Ordinal);
            Assert.Equal(2048, vm.WorkspaceLeftoverTotalBytes);
            Assert.True(vm.ClearWorkspaceLeftoversCommand.CanExecute(null));

            vm.ClearWorkspaceLeftoversCommand.Execute(null);

            Assert.False(Directory.Exists(leftover), "确认之后要真的删掉");
            Assert.False(vm.HasWorkspaceLeftovers);
            Assert.Equal(string.Empty, vm.WorkspaceLeftoverBanner);
            Assert.False(vm.ClearWorkspaceLeftoversCommand.CanExecute(null));

            // 日志里说清删了几个、释放了多少（可追溯）
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("工作区清理：", StringComparison.Ordinal)
                        && line.Contains("释放", StringComparison.Ordinal));

            await Task.CompletedTask;
        }

        /// <summary>确认框答"否"（无界面宿主的默认降级方向）：一个目录都不删。</summary>
        [Fact]
        public void 界面_不确认就不删()
        {
            string dataRoot = Path.Combine(_root, "data2");
            string workRoot = Path.Combine(dataRoot, "work");

            Directory.CreateDirectory(workRoot);

            string leftover = CreateLeftover(workRoot, "0001_auto.7z.001-933d88ea", ("a.bin", 2048));

            VmHarness harness = CreateViewModel(dataRoot, new DialogService());
            MainViewModel vm = harness.Vm;

            Assert.True(vm.HasWorkspaceLeftovers);

            vm.ClearWorkspaceLeftoversCommand.Execute(null);

            Assert.True(Directory.Exists(leftover), "没人点过确认，一个字节都不许删");
            Assert.True(vm.HasWorkspaceLeftovers);
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("工作区清理已取消", StringComparison.Ordinal));
        }

        /// <summary>没有残留时点它：只提示一句，不弹确认框、不删任何东西。</summary>
        [Fact]
        public void 界面_没有残留时只提示一句()
        {
            string dataRoot = Path.Combine(_root, "data3");
            Directory.CreateDirectory(Path.Combine(dataRoot, "work"));

            VmHarness harness = CreateViewModel(dataRoot, new DialogService());
            MainViewModel vm = harness.Vm;

            Assert.False(vm.HasWorkspaceLeftovers);
            Assert.Equal(string.Empty, vm.WorkspaceLeftoverBanner);
            Assert.False(vm.ClearWorkspaceLeftoversCommand.CanExecute(null));
        }

        // ================================================================ 装配

        private string CreateWorkRoot()
        {
            string workRoot = Path.Combine(_root, "work");
            Directory.CreateDirectory(workRoot);
            return workRoot;
        }

        private static string CreateLeftover(string workRoot, string name, params (string FileName, int Bytes)[] files)
        {
            string directory = Path.Combine(workRoot, name);
            Directory.CreateDirectory(Path.Combine(directory, "layer-001", "output"));

            foreach ((string fileName, int bytes) in files)
            {
                File.WriteAllBytes(
                    Path.Combine(directory, "layer-001", "output", fileName),
                    new byte[bytes]);
            }

            return directory;
        }

        /// <summary>
        /// 造一个 MainViewModel：它的构造里就会扫一次工作区（<c>LogLeftoverWorkspaces</c>），
        /// 所以"界面看得见残留"这件事不需要额外触发。
        ///
        /// <para>日志断言走 <see cref="LogService"/> 而不是 <c>Vm.Logs</c>：后者是**屏幕**日志，
        /// 由 <c>Application.Current.Dispatcher.BeginInvoke</c> 投递 —— 无界面宿主里根本没有 Application，
        /// 那条路永远不会执行（与其它测试同一口径）。</para>
        /// </summary>
        private static VmHarness CreateViewModel(string dataRoot, DialogService dialogService)
        {
            Directory.CreateDirectory(dataRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.AutoScanAfterDrop = false;
            settingsService.Save(settings);

            var logService = new LogService(pathService);

            // MainViewModel 的构造会写进程级静态：先存后还原（与其它同类用例同一套做法）。
            string? previousWorkspaceRoot = Extraction.RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = Engines.ToolLocator.Default.CustomSevenZipExePath;

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                new NeverAvailableEngine(),
                new PasswordService(),
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                dialogService);

            Extraction.RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            Engines.ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            return new VmHarness(vm, logService);
        }

        private sealed class VmHarness
        {
            public VmHarness(MainViewModel vm, LogService logService)
            {
                Vm = vm;
                LogService = logService;
            }

            public MainViewModel Vm { get; }

            public LogService LogService { get; }

            public IEnumerable<string> LogTexts => LogService.Logs.Select(item => item.DisplayText);
        }

        /// <summary>确认框一律答"是"的替身（只在测试里用；无界面宿主默认答"否"）。</summary>
        private sealed class ConfirmingDialogService : DialogService
        {
            public override bool ShowConfirm(
                string message,
                string optionText,
                bool optionCheckedByDefault,
                string detail,
                out bool optionChecked)
            {
                optionChecked = false;
                return true;
            }
        }

        /// <summary>本组用例不碰解压：一个永远不可用的假引擎就够（构造函数只读它的能力位）。</summary>
        private sealed class NeverAvailableEngine : ArchiveFixer.Engines.IArchiveEngine
        {
            public string Id => "none";

            public string DisplayName => "假引擎";

            public string Version => "1.0";

            public bool IsAvailable => false;

            public ArchiveFixer.Engines.EngineCapabilities Capabilities { get; } = new();

            public Task<ArchiveFixer.Engines.ArchiveProbeResult> ProbeAsync(
                ArchiveFixer.Engines.ArchiveRequest request,
                System.Threading.CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveFixer.Engines.ArchiveProbeResult { IsArchive = false });

            public Task<ArchiveFixer.Engines.ArchiveListResult> ListAsync(
                ArchiveFixer.Engines.ArchiveRequest request,
                System.Threading.CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveFixer.Engines.ArchiveListResult());

            public Task<ArchiveFixer.Engines.ArchiveOperationResult> TestAsync(
                ArchiveFixer.Engines.ArchiveRequest request,
                System.Threading.CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveFixer.Engines.ArchiveOperationResult { Success = false });

            public Task<ArchiveFixer.Engines.ArchiveOperationResult> ExtractAsync(
                ArchiveFixer.Engines.ArchiveRequest request,
                Models.ExtractOptions options,
                System.Threading.CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveFixer.Engines.ArchiveOperationResult { Success = false });
        }
    }
}

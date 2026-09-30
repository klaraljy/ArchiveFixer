using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 用户 2026-09-28「一键处理**开工前**自动把无用物从列表里移掉」的守门用例（A 条）。
    ///
    /// <para>用户原话：「这次导入文件没有移除无用物的弹窗提醒，但是列表里面还在，我以为按了一键处理
    /// 你们就会清除掉，但是他没有」→「1.要」；口径是他自己划的那条线：
    /// 「他们是无用物，只是对你们解压没有用，不是垃圾」。</para>
    ///
    /// <para>这一组钉三件事：</para>
    /// <list type="number">
    /// <item><description><b>真的移掉</b>：<c>ScanCoordinator.RemoveJunkTasksFromListAsync</c> 一调，
    /// 无用物就不在任务列表里了（这才是用户要的"按一下全搞定"）；</description></item>
    /// <item><description><b>红线：磁盘一个字节都不动</b> —— 不删、不搬、不改名，内容与最后写入时间都对照一遍；</description></item>
    /// <item><description><b>判据窄，不误伤真包</b>：名字像无用物、魔数却是真包（伪装包）的那一个必须留在列表里
    /// —— 这正是 <c>SourceJunkScanner</c> 判据 3 存在的理由。</description></item>
    /// </list>
    ///
    /// <para>夹具照 <c>ImportJunkReminderTests</c> 那一套装配（假引擎 + 无界面宿主），
    /// 只多一个直接构造出来的 <c>ScanCoordinator</c>：一键处理里调的就是它这一个方法。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class OneClickJunkAutoRemoveTests : IDisposable
    {
        private readonly string _root;

        public OneClickJunkAutoRemoveTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerJunkAutoRemove", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 真的移掉 + 红线

        /// <summary>
        /// 一键处理开工前那一下：列表里的无用物被移掉，**磁盘上的文件一个字节都没动**，
        /// 而伪装成 <c>.jpg</c> 的真包（名字也像无用物）必须留在列表里。
        /// </summary>
        [Fact]
        public async Task 开工前移无用物_只动列表_磁盘一个字节都没动()
        {
            Harness harness = CreateHarness();

            string junk = CreateFile(harness, "说明.txt", Encoding.UTF8.GetBytes("这是打包者附带的说明，不是压缩包"));
            string pack = CreateFile(harness, "真包.jpg", ZipHeaderBytes());

            /*
             * 两个任务都按"还没识别出是压缩包"建档（IsArchive = false）—— 这正是导入文件夹之后的真实形态：
             * 打包者附带的 .txt 自己也会进任务列表，而伪装包在识别之前谁也不知道它是包。
             *
             * ⚠ 真包那个**故意**不给 IsArchive = true：那样它会被"属于本批任务"这条保护直接罩住，
             * 这条用例就测不到魔数体检了。这里要的恰恰是"名字像无用物，只靠 PK 头救回来"。
             */
            ArchiveTask junkTask = AddTask(harness, junk);
            ArchiveTask packTask = AddTask(harness, pack);

            // 前提先钉一遍，否则这条用例可能什么都没证明。
            Assert.True(SourceJunkScanner.LooksLikeJunkCandidate("说明.txt"));
            Assert.True(
                SourceJunkScanner.LooksLikeJunkCandidate("真包.jpg"),
                "前提：真包的名字必须**也**像无用物（.jpg 在名单里），否则魔数那一步根本没被考到");
            Assert.True(await new MagicArchiveProber().IsArchiveAsync(pack), "前提：真包必须能被魔数认出来");

            byte[] junkBytesBefore = File.ReadAllBytes(junk);
            DateTime junkWriteTimeBefore = File.GetLastWriteTimeUtc(junk);

            int removed = await harness.Scan.RemoveJunkTasksFromListAsync(harness.Vm.Tasks.ToList());

            // ① 真的移掉了，而且只移掉那一个。
            Assert.Equal(1, removed);
            Assert.DoesNotContain(harness.Vm.Tasks, task => ReferenceEquals(task, junkTask));
            Assert.DoesNotContain(
                harness.Vm.Tasks,
                task => task.FileName.Equals("说明.txt", StringComparison.OrdinalIgnoreCase));

            // ② 红线：磁盘上的文件还在原位，内容与最后写入时间都没变（不是"删了又建一个同名的"）。
            Assert.True(File.Exists(junk), "红线：这里只许动列表，绝不许删 / 搬 / 改名磁盘上的文件");
            Assert.Equal(junkBytesBefore, File.ReadAllBytes(junk));
            Assert.Equal(junkWriteTimeBefore, File.GetLastWriteTimeUtc(junk));

            // ③ 真包没被移掉（名字像无用物，是魔数证明它是包）。
            Assert.Contains(harness.Vm.Tasks, task => ReferenceEquals(task, packTask));
            Assert.True(File.Exists(pack));

            // 日志如实说清"移了几个、没动文件"（用户要能追溯）。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("无用物从列表里移掉", StringComparison.Ordinal) &&
                        line.Contains("说明.txt", StringComparison.Ordinal) &&
                        line.Contains("源文件一个字节都没动", StringComparison.Ordinal));
        }

        // ================================================================ ② 判据窄：没有无用物就一个都不动

        /// <summary>
        /// 列表里全是真包（哪怕名字都像无用物）：一个都不许移，列表与整个源目录**逐项原样**。
        /// </summary>
        [Fact]
        public async Task 全是真包时_一个都不移且源目录逐项原样()
        {
            Harness harness = CreateHarness();

            string first = CreateFile(harness, "真包A.jpg", ZipHeaderBytes());
            string second = CreateFile(harness, "真包B.jpg", ZipHeaderBytes());

            AddTask(harness, first);
            AddTask(harness, second);

            string before = DescribeSourceDirectory(harness.SourceDirectory);

            int removed = await harness.Scan.RemoveJunkTasksFromListAsync(harness.Vm.Tasks.ToList());

            Assert.Equal(0, removed);
            Assert.Equal(2, harness.Vm.Tasks.Count);
            Assert.Equal(before, DescribeSourceDirectory(harness.SourceDirectory));

            // 什么都没移掉时不该写那句"已经移掉 N 个"（免得日志里出现一条没发生过的事）。
            Assert.DoesNotContain(
                harness.LogTexts,
                line => line.Contains("无用物从列表里移掉", StringComparison.Ordinal));
        }

        // ================================================================ ③ 空表不许出意外

        /// <summary>
        /// 空列表 / null：直接返回 0，不抛、不写日志 —— 这个方法是"开工前顺手做一下"，
        /// 绝不能因为它把一整批一键处理拦死。
        /// </summary>
        [Fact]
        public async Task 空列表与null_直接返回0且什么都不做()
        {
            Harness harness = CreateHarness();

            Assert.Equal(0, await harness.Scan.RemoveJunkTasksFromListAsync(null));
            Assert.Equal(0, await harness.Scan.RemoveJunkTasksFromListAsync(Array.Empty<ArchiveTask>()));
            Assert.Empty(harness.Vm.Tasks);
        }

        // ================================================================ 装配

        private sealed class Harness
        {
            public Harness(MainViewModel vm, ScanCoordinator scan, LogService log, string sourceDirectory)
            {
                Vm = vm;
                Scan = scan;
                Log = log;
                SourceDirectory = sourceDirectory;
            }

            public MainViewModel Vm { get; }

            public ScanCoordinator Scan { get; }

            public LogService Log { get; }

            public string SourceDirectory { get; }

            /// <summary>屏幕日志的文本（无 WPF 应用的测试进程里照样会被填充）。</summary>
            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);
        }

        private Harness CreateHarness()
        {
            string dataRoot = Path.Combine(_root, "data");
            string sourceDirectory = Path.Combine(_root, "src");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(sourceDirectory);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = Path.Combine(_root, "out");
            settings.ExtractToOriginalDirectory = false;
            settings.RecursionMode = "SingleLayer";

            // 这一组只测"开工前那一下"，导入与识别都不走。
            settings.AutoScanAfterDrop = false;
            settingsService.Save(settings);

            var logService = new LogService(pathService);

            // MainViewModel 的构造会顺手写两个进程级静态：先存后还原（与其它管线测试同一套做法）。
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                new NoopEngine(),
                new PasswordService(),
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());

            return new Harness(vm, scan, logService, sourceDirectory);
        }

        private string CreateFile(Harness harness, string fileName, byte[] content)
        {
            string path = Path.Combine(harness.SourceDirectory, fileName);
            File.WriteAllBytes(path, content);
            return path;
        }

        private static ArchiveTask AddTask(Harness harness, string sourcePath)
        {
            var task = new ArchiveTask(sourcePath, harness.Vm.Tasks.Count + 1)
            {
                // 刻意按"还没认出是压缩包"建档，见用例里的说明。
                IsArchive = false,
                Status = StatusText.Recognized,
                ExtensionStatus = StatusText.ExtensionNormal,
                IsSelected = true
            };

            harness.Vm.Tasks.Add(task);
            return task;
        }

        /// <summary>源目录的"事实快照"：名字 + 字节数 + 最后写入时间（红线要拿它逐项对照）。</summary>
        private static string DescribeSourceDirectory(string directory)
        {
            return string.Join(
                "|",
                Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(path => $"{Path.GetFileName(path)}:{new FileInfo(path).Length}:{File.GetLastWriteTimeUtc(path).Ticks}"));
        }

        /// <summary>
        /// 一个"文件头就是 ZIP"的真包（与 <c>JunkReminderTests.ZipHeaderBytes</c> 同一个形状）：
        /// 魔数认得出来，但内容是不是一个完整 ZIP 与这条用例无关。
        /// </summary>
        private static byte[] ZipHeaderBytes()
        {
            var bytes = new List<byte> { 0x50, 0x4B, 0x03, 0x04 };

            while (bytes.Count < 64)
            {
                bytes.Add(0x00);
            }

            return bytes.ToArray();
        }

        /// <summary>本组用例既不解压也不识别：一个永远不可用的假引擎就够（构造函数只读它的能力位）。</summary>
        private sealed class NoopEngine : IArchiveEngine
        {
            public string Id => "none";

            public string DisplayName => "假引擎";

            public string Version => "1.0";

            public bool IsAvailable => false;

            public EngineCapabilities Capabilities { get; } = new();

            public Task<ArchiveProbeResult> ProbeAsync(
                ArchiveRequest request,
                System.Threading.CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveProbeResult { IsArchive = false });

            public Task<ArchiveListResult> ListAsync(
                ArchiveRequest request,
                System.Threading.CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveListResult());

            public Task<ArchiveOperationResult> TestAsync(
                ArchiveRequest request,
                System.Threading.CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveOperationResult { Success = false });

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                System.Threading.CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveOperationResult { Success = false });
        }
    }
}

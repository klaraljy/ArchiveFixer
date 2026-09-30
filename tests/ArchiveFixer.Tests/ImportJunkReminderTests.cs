using ArchiveFixer.Detection;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// <b>导入时的无用物处理</b>的回归测试。
    ///
    /// <para>⚠ 类名是历史遗留（它以前测的是那个「导入后提醒」弹窗）。用户 2026-09-28 把口径改死了：
    /// 原话「为什么不在检测到的时候就直接移除」「当选择文件的时候，移除无用物的弹窗，没有弹出来
    /// 我还以为你又没弄好，但是在点击一键处理之后，他一出来，但是在我点击之前他一直还是勾选着，
    /// 你为什么要这样」→ <b>导入一完成就自动把无用物移出任务列表</b>，那个提醒框退休。</para>
    ///
    /// <para>这一组钉四件事：</para>
    /// <list type="number">
    /// <item><description>导入之后**立刻**移出列表（不用等一键处理，也不用等任何弹窗）；</description></item>
    /// <item><description><b>红线：磁盘一个字节都不动</b> —— 不删、不改名、不搬走，内容与最后写入时间都对照；
    /// 日志里写明移了几个、是哪些，并说清"源文件一个字节都没动"；</description></item>
    /// <item><description><b>判据窄，不误伤真包</b>：名字像无用物、魔数却是真包（伪装包）的必须留在列表里；
    /// 分卷组的后续卷（没有魔数可看）也必须靠"同组兄弟卷"那条保护留下来；</description></item>
    /// <item><description>那个已退休的开关（<c>AppSettings.RemindJunkAfterImport</c>）**不再控制**这件事：
    /// 开着关着都照样移出列表，但设置项本身读写与序列化一个字都没动。</description></item>
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

        // ================================================================ ① 导入即移出 + 红线

        /// <summary>
        /// 导入文件夹之后**立刻**把无用物移出列表：两个没用物不在列表里了，真包还在，
        /// 而磁盘上三个文件**一个字节都没动**（内容与最后写入时间逐项对照）。
        /// </summary>
        [Fact]
        public async Task 导入之后_无用物立刻被移出列表_磁盘一个字节都没动()
        {
            string folder = CreateSourceFolder();
            string junkText = Path.Combine(folder, "说明.txt");
            string junkUrl = Path.Combine(folder, "网址.url");

            byte[] textBefore = File.ReadAllBytes(junkText);
            byte[] urlBefore = File.ReadAllBytes(junkUrl);
            DateTime textTimeBefore = File.GetLastWriteTimeUtc(junkText);
            DateTime urlTimeBefore = File.GetLastWriteTimeUtc(junkUrl);

            Harness harness = CreateHarness();

            await harness.Vm.AddPathsAsync(new[] { folder });

            // ① 两个无用物**已经不在列表里**（不用等一键处理，也不用等任何弹窗）。
            Assert.DoesNotContain(
                harness.Vm.Tasks,
                task => task.FileName.Equals("说明.txt", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                harness.Vm.Tasks,
                task => task.FileName.Equals("网址.url", StringComparison.OrdinalIgnoreCase));

            // ② 真包留在列表里（这一档不动真东西）。
            Assert.Contains(
                harness.Vm.Tasks,
                task => task.FileName.Equals("pack.7z", StringComparison.OrdinalIgnoreCase));

            // ③ 红线：磁盘上的文件还在原位，内容与最后写入时间都没变（不是"删了又建一个同名的"）。
            Assert.True(File.Exists(junkText), "红线：只许动列表，绝不许删 / 搬 / 改名磁盘上的文件");
            Assert.True(File.Exists(junkUrl), "红线：只许动列表，绝不许删 / 搬 / 改名磁盘上的文件");
            Assert.Equal(textBefore, File.ReadAllBytes(junkText));
            Assert.Equal(urlBefore, File.ReadAllBytes(junkUrl));
            Assert.Equal(textTimeBefore, File.GetLastWriteTimeUtc(junkText));
            Assert.Equal(urlTimeBefore, File.GetLastWriteTimeUtc(junkUrl));

            // ④ 日志写明"移了几个、是哪几个、没动文件"（用户要能追溯）。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("导入完成：已自动把 2 个无用物从任务列表里移出", StringComparison.Ordinal) &&
                        line.Contains("说明.txt", StringComparison.Ordinal) &&
                        line.Contains("网址.url", StringComparison.Ordinal) &&
                        line.Contains("源文件一个字节都没动", StringComparison.Ordinal));
        }

        // ================================================================ ② 判据窄：不误伤真包

        /// <summary>
        /// 名字像无用物、魔数却是真包的那一个（伪装包）必须**留在列表里**：
        /// 这正是 <c>SourceJunkScanner</c> 判据 3 存在的理由，也是"导入即移出"最容易出事的地方。
        /// </summary>
        [Fact]
        public async Task 名字像无用物的真包_不会被移出列表()
        {
            string folder = Path.Combine(_root, "src-disguised");
            Directory.CreateDirectory(folder);

            string disguised = Path.Combine(folder, "真包.jpg");
            File.WriteAllBytes(disguised, ZipHeaderBytes());
            File.WriteAllText(Path.Combine(folder, "说明.txt"), "这是打包者附带的说明，不是压缩包");

            // 前提先钉一遍，否则这条用例可能什么都没证明。
            Assert.True(SourceJunkScanner.LooksLikeJunkCandidate("真包.jpg"), "前提：名字必须也像无用物");
            Assert.True(await new MagicArchiveProber().IsArchiveAsync(disguised), "前提：真包必须能被魔数认出来");

            Harness harness = CreateHarness();

            await harness.Vm.AddPathsAsync(new[] { folder });

            // 真包留下、说明移出（只差"名字像不像"这一件事，靠魔数分开）。
            Assert.Contains(
                harness.Vm.Tasks,
                task => task.FileName.Equals("真包.jpg", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                harness.Vm.Tasks,
                task => task.FileName.Equals("说明.txt", StringComparison.OrdinalIgnoreCase));

            Assert.True(File.Exists(disguised), "红线：真包一个字节都不许动");
        }

        /// <summary>
        /// <b>分卷组的后续卷不许被当成无用物移出列表</b>（用户 2026-09-28 真机那条保护）。
        ///
        /// <para>后续卷是裸切块、**没有文件头魔数**，名字里又带"说明"这类字眼时，
        /// 只按名字 + 魔数判就会把它叫成无用物；一旦被移出列表，整组只剩第一卷，**再也解不开**。
        /// 保护是 <c>SourceJunkScanner.CollectProtectedPaths</c> 里那条"同目录同组的兄弟卷"——
        /// 本用例就是钉住它别被拆掉。</para>
        ///
        /// <para>⚠ 两行是**手工**建的：整组一次导入时 <c>VolumeGroupingService</c> 会把它们并成一行
        /// （后续卷压根不是任务行）；而"后续卷自己占一行"在真机上出现过 ——
        /// 首卷后来才补齐的那条「首卷补齐后并组」就是它。</para>
        /// </summary>
        [Fact]
        public async Task 分卷组的后续卷_不会被移出列表()
        {
            string folder = Path.Combine(_root, "src-volume");
            Directory.CreateDirectory(folder);

            string first = Path.Combine(folder, "说明.7z.001");
            string second = Path.Combine(folder, "说明.7z.002");

            File.WriteAllBytes(first, SevenZipHeaderBytes());
            File.WriteAllBytes(second, new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 });

            // 前提：两卷都真的像无用物候选、后续卷真的没有魔数（否则这条用例测不到那条保护）。
            Assert.True(SourceJunkScanner.LooksLikeJunkCandidate("说明.7z.001"));
            Assert.True(SourceJunkScanner.LooksLikeJunkCandidate("说明.7z.002"));
            Assert.False(
                await new MagicArchiveProber().IsArchiveAsync(second),
                "前提：后续卷必须是魔数认不出来的（裸切块），这才需要那条保护");
            Assert.True(
                await new MagicArchiveProber().IsArchiveAsync(first),
                "前提：第一卷必须认得出是包 —— 那条保护是靠它认领兄弟卷的");

            Harness harness = CreateHarness();

            AddTask(harness, first, isArchive: true);
            AddTask(harness, second, isArchive: false);

            int removed = await harness.Scan.RemoveJunkTasksFromListAsync(harness.Vm.Tasks.ToList());

            // 一个都不许移：第一卷自己是真包，后续卷被同组的兄弟卷认领。
            Assert.Equal(0, removed);
            Assert.Equal(2, harness.Vm.Tasks.Count);
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
        }

        // ================================================================ ③ 已退休的开关不再控制这件事

        /// <summary>
        /// 那个开关（<c>RemindJunkAfterImport</c>）**关着也照样移出列表** —— 它已经不再控制导入时的行为；
        /// 但设置项本身仍然读写正常（序列化一个字都没动，用户盘上的 <c>appsettings.json</c> 还认得它）。
        /// </summary>
        [Fact]
        public async Task 开关关着也照样移出列表_设置项本身仍然可读写()
        {
            string folder = CreateSourceFolder();

            Harness harness = CreateHarness(settings => settings.RemindJunkAfterImport = false);

            await harness.Vm.AddPathsAsync(new[] { folder });

            Assert.DoesNotContain(
                harness.Vm.Tasks,
                task => task.FileName.Equals("说明.txt", StringComparison.OrdinalIgnoreCase));

            // 设置项照旧：能写、能落盘、能读回来（"先留着、别动设置序列化"）。
            harness.Vm.SaveRemindJunkAfterImport(true);

            Assert.True(new SettingsService(harness.PathService).Load().RemindJunkAfterImport);
        }

        // ================================================================ ④ 列表里手动删任务

        /// <summary>「移除勾选的」：只移除勾选的那些，源文件不动，没有勾选时只提示一句。</summary>
        [Fact]
        public async Task 移除勾选的任务_只动列表()
        {
            string folder = Path.Combine(_root, "src-manual");
            Directory.CreateDirectory(folder);

            // 这一条测的是**手动移除**这条命令，所以放两个真包（不会被导入时那次清理碰到的）。
            File.WriteAllText(Path.Combine(folder, "pack-a.7z"), "not a real archive - the engine is faked in these tests");
            File.WriteAllText(Path.Combine(folder, "pack-b.7z"), "not a real archive - the engine is faked in these tests");

            Harness harness = CreateHarness();

            await harness.Vm.AddPathsAsync(new[] { folder });

            List<ArchiveTask> tasks = harness.Vm.Tasks.ToList();

            Assert.Equal(2, tasks.Count);

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
            Assert.True(File.Exists(Path.Combine(folder, "pack-a.7z")));
            Assert.True(File.Exists(Path.Combine(folder, "pack-b.7z")));
        }

        /*
         * ===== 删掉的两条老用例（⛔ 不是"改成假装跑过"，是真的测不到了）=====
         *
         * ①「选知道了_列表与源文件都不动」—— 它测的是那个提醒框的主按钮「知道了」。
         *    提醒框已经退休（用户 2026-09-28：「为什么不在检测到的时候就直接移除」），
         *    导入路径里再也不弹这个框，"点知道了会怎样"这个行为在程序里已经不存在，
         *    所以这条用例删除；现在的口径由上面「导入之后_无用物立刻被移出列表_磁盘一个字节都没动」钉住。
         *
         * ②「勾了以后不再提醒_写进设置且之后不再提醒」—— 同理：提醒框退休之后没有"提醒"可关，
         *    那个开关也不再控制导入时的行为（见「开关关着也照样移出列表_设置项本身仍然可读写」）。
         *    老用例的一半（设置项仍然写得进、读得回）保留在那一条里，剩下的一半测的是一个不存在的行为。
         */

        // ================================================================ 装配

        /// <summary>造一个文件夹：一个（假）包 + 两个打包者常带的无用物。</summary>
        private static string CreateSourceFolderAt(string folder)
        {
            Directory.CreateDirectory(folder);

            File.WriteAllText(Path.Combine(folder, "pack.7z"), "not a real archive - the engine is faked in these tests");
            File.WriteAllText(Path.Combine(folder, "说明.txt"), "这是打包者附带的说明，不是压缩包");
            File.WriteAllText(Path.Combine(folder, "网址.url"), "[InternetShortcut]");

            return folder;
        }

        private string CreateSourceFolder() => CreateSourceFolderAt(Path.Combine(_root, "src"));

        /// <summary>
        /// 手工建一行任务（与 <c>OneClickJunkAutoRemoveTests</c> 同一套做法）。
        ///
        /// <para>为什么有的用例要手工建：整组一次导入时 <c>VolumeGroupingService</c> 会把分卷并成一行，
        /// 而"分卷组的后续卷自己占一行"这种形状在真机上出现过（首卷后来才补齐）——
        /// 要钉住那条兄弟卷保护，只能把这个形状直接摆出来。</para>
        /// </summary>
        private static ArchiveTask AddTask(Harness harness, string sourcePath, bool isArchive)
        {
            var task = new ArchiveTask(sourcePath, harness.Vm.Tasks.Count + 1)
            {
                IsArchive = isArchive,
                Status = StatusText.Recognized,
                ExtensionStatus = StatusText.ExtensionNormal,
                IsSelected = true
            };

            harness.Vm.Tasks.Add(task);
            return task;
        }

        private Harness CreateHarness(Action<AppSettings>? configure = null)
        {
            string dataRoot = Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = Path.Combine(_root, "out");
            settings.ExtractToOriginalDirectory = false;
            settings.RecursionMode = "SingleLayer";

            /*
             * 导入后**不要**自动扫描：这一组测的是"导入时的无用物处理"，不是识别
             * （那几个假包识别出来也不是归档）。无用物的判据不依赖识别结果 ——
             * 名字 + 魔数体检自己就能定案，所以关掉识别照样测得到。
             */
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
                new DialogService());

            Extraction.RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            Engines.ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            // 分卷那条用例要直接调"把无用物移出列表"这一个方法（导入路径调的也是它）。
            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());

            return new Harness(vm, scan, logService, pathService);
        }

        private sealed class Harness
        {
            public Harness(MainViewModel vm, ScanCoordinator scan, LogService log, PathService pathService)
            {
                Vm = vm;
                Scan = scan;
                Log = log;
                PathService = pathService;
            }

            public MainViewModel Vm { get; }

            public ScanCoordinator Scan { get; }

            public LogService Log { get; }

            public PathService PathService { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);
        }

        /// <summary>
        /// 一个"文件头就是 ZIP"的真包（魔数认得出来，但内容是不是一个完整 ZIP 与本组用例无关）。
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

        /// <summary>一个"文件头就是 7z"的假包（7z 的签名 <c>37 7A BC AF 27 1C</c> + 补零）。</summary>
        private static byte[] SevenZipHeaderBytes()
        {
            var bytes = new List<byte> { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };

            while (bytes.Count < 64)
            {
                bytes.Add(0x00);
            }

            return bytes.ToArray();
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// 「空间不足」模式在**多层（续解）链**上的回收口径（用户 2026-09-29 第 1 条与第 4 条）。
    ///
    /// <para><b>用户的两句话就是本文件要钉的东西</b>：</para>
    /// <list type="number">
    /// <item><description>"如果原包不止一层解压，如有四层，那你岂不是要将其余物的空间占比换成原包的四倍，
    /// 但是如果你是每一层就删一遍，这样也还行，只不过是两倍的情况" —— 所以峰值必须是
    /// **当前层 + 下一层**（≈ 两倍单层），不是"层数 × 单层"；</description></item>
    /// <item><description>"我觉得可以将用户选择空间不足的情况直接按照之前手动挡操作的来看，就是'解压成功就直接删除解压包'……
    /// 原包是已经没有了但是留下的不会破，他有可能是第二层或者第三层" —— 所以**每一层**跑完就删它自己那一层的源包
    /// （最外层 = 用户给的包，续解层 = 上一层交出来的内层包），而且**已经删掉的上层不回滚**。</description></item>
    /// </list>
    ///
    /// <para><b>为什么必须用真 7z</b>（AGENTS.md §11"验收必须在真样本 / 真机只读副本上跑通"）：
    /// 合成假引擎证明得了"我按自己以为的形状处理得对"，证明不了"真的 7z 一层层套出来的东西我处理得对"。
    /// 所以这里整条链都当真：真 7z.exe 造包、真 <see cref="SevenZipEngine"/>、真 <see cref="MainViewModel"/>
    /// 与真的解压管线。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class ChainSpaceReclaimTests : IDisposable
    {
        /// <summary>最深一层那个包的密码（刻意**不**进密码本，用来造"这一层失败"）。</summary>
        private const string UnknownInnerPassword = "测试用未知密码";

        private readonly string _root;
        private readonly string _sevenZip;

        public ChainSpaceReclaimTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerChainReclaim", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _sevenZip = SevenZipFactAttribute.LocateSevenZipPath();
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
                // 临时目录清不掉不影响测试结论。
            }
        }

        // ================================================================ 第 1 条：每一层立刻回收

        /// <summary>
        /// <b>第 1 条的核心</b>：四层真链 + 空间不足模式 → 每一层跑完，**那一层的源包立刻不在盘上**，
        /// 而且同一时刻盘上的包**不超过两个**（当前层 + 下一层），不是"四倍单层"。
        /// </summary>
        [SevenZipFact]
        public async Task 空间不足模式_四层真链_每层跑完那一层的源包立刻不在盘上_峰值不超过两层()
        {
            const int layers = 4;

            Harness harness = CreateHarness();
            string outer = BuildChain(layers);

            /*
             * "峰值"怎么量：**采样**（每 10 毫秒数一遍盘上有几个 .7z）。
             * 采样只会**少看**、绝不会多看，所以"最多看到 N 个"这种上界断言是安全的 ——
             * 真要是攒着不清，中间那些层的存在时间是几百毫秒级，采样必然看得见。
             */
            using var peak = new PackagePeakSampler(_root);

            await harness.AddPathsAsync(outer);

            harness.Vm.SpaceTightMode = true;

            peak.Start();

            OneClickOutcome outcome = await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            peak.Stop();

            // ① 四层全部成功（这条链没有任何失败的理由）。
            Assert.Equal(layers, outcome.Rounds);
            Assert.All(harness.Vm.Tasks, task => Assert.Equal(TaskOutcome.Succeeded, task.Outcome));

            // ② 内容物出现了（不然"包都没了"可能只是整条链没跑）。
            Assert.NotEmpty(FindFiles("final.txt"));

            // ③ 四个包一个都不剩：每一层都在它自己那一层跑完时被收走了。
            string[] remaining = FindFiles("*.7z");
            Assert.True(
                remaining.Length == 0,
                "空间不足模式下每一层的源包都该在那一层跑完时被删掉，实际还剩：" + string.Join("、", remaining));

            // ④ 删除动作走的是**同一个执行体**（SourceCleanupService），而且**每一层各一次**
            //    —— 老口径只有最外层那一次（内层包攒到链尾由"删除操作"那一档统一清）。
            Assert.Equal(layers, harness.DeleteFileSystem.DeletedPaths.Count);

            // ⑤ 每一层删除的那一刻，盘上还留着的**别的包**不超过 1 个（当前层 + 下一层 = 两层量级）。
            Assert.True(
                harness.DeleteFileSystem.MaxPackagesSeenAtDelete <= 1,
                "删除某一层源包的那一刻，盘上不该还攒着更多层的包；实测最多 "
                + harness.DeleteFileSystem.MaxPackagesSeenAtDelete + " 个");

            // ⑥ 全程采样到的峰值同样不超过两层（第 1 条："只不过是两倍的情况"）。
            Assert.True(
                peak.Max <= 2,
                $"多层链的峰值必须是「当前层 + 下一层」（≤2 个包），实测最多同时存在 {peak.Max} 个："
                + string.Join("、", peak.PeakSnapshot));

            // ⑦ 每一层都要**说出来**（"我的包哪去了"必须能事后回答）。
            int purgeLines = harness.Log.Logs.Count(
                entry => entry.Message.Contains("已立刻永久删除", StringComparison.Ordinal));

            Assert.True(
                purgeLines == layers,
                $"每一层都该有一条「已立刻永久删除」的日志（共 {layers} 条），实际 {purgeLines} 条。日志："
                + string.Join(" ｜ ", harness.Log.Logs.Select(entry => entry.Level + ":" + entry.Message)));
        }

        /// <summary>
        /// 第 4 条点名的那个形状：**某一层失败 ⇒ 只影响那一层的源包**，已经删掉的上层**不回滚**
        /// （用户原话："原包是已经没有了但是留下的不会破，他有可能是第二层或者第三层"）。
        ///
        /// <para>造法：最深一层那个包设了密码、密码本里没有它 → 轮到它时必然失败。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 空间不足模式_最深一层解不开_那一层的包还在_而已经删掉的上层不回滚()
        {
            const int layers = 3;

            Harness harness = CreateHarness();
            string outer = BuildChain(layers, innermostPassword: UnknownInnerPassword);

            await harness.AddPathsAsync(outer);

            harness.Vm.SpaceTightMode = true;

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            // ① 最外层与中间那层都被收走了（它们各自那一层是成功的）。
            Assert.False(File.Exists(outer), "最外层那一层成功了，它的源包该被收走");

            string[] remaining = FindFiles("*.7z");

            // ② 失败那一层的包**必须还在**（不变量 1 的红线：失败 ⇒ 源包原地不动）。
            Assert.Contains(
                remaining,
                path => string.Equals(Path.GetFileName(path), $"level{layers}.7z", StringComparison.OrdinalIgnoreCase));

            // ③ 更浅的两层**不回滚**（用户明确接受这一点，见类注释）：只剩最深那一层那个包。
            Assert.Single(remaining);

            // ④ 内容物没出来（这一层真的失败了，不是"其实成功了"）。
            Assert.Empty(FindFiles("final.txt"));

            // ⑤ 失败那一层要落成失败，而不是"部分完成"或者"成功"（不变量 6）。
            Assert.Contains(harness.Vm.Tasks, task => task.Outcome == TaskOutcome.Failed);

            // ⑥ 只有两层真的回收过（删成功的两层的日志各一条）。
            Assert.Equal(
                layers - 1,
                harness.DeleteFileSystem.DeletedPaths.Count);
        }

        /// <summary>
        /// <b>反向对照</b>：同一条四层链，**没开**那个模式 → 一个源包都不许删
        /// （默认两档就是"原来的位置不动 + 其余物不动"）。
        /// </summary>
        [SevenZipFact]
        public async Task 对照组_没开空间不足模式_四层链一个源包都不许删()
        {
            const int layers = 4;

            Harness harness = CreateHarness();
            string outer = BuildChain(layers);

            await harness.AddPathsAsync(outer);

            Assert.False(harness.Vm.SpaceTightMode);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            Assert.True(File.Exists(outer), "没开那个模式时最外层源包必须原地不动");

            string[] remaining = FindFiles("*.7z");

            Assert.Equal(layers, remaining.Length);
            Assert.Empty(harness.DeleteFileSystem.DeletedPaths);
        }

        // ================================================================ 造真包 / 断言辅助

        /// <summary>
        /// 造一条嵌套链：<c>outer.7z → level2.7z → … → levelN.7z → final.txt</c>
        /// （与 <c>InnerLayerContinuationTests.BuildChain</c> 同一套造法；
        /// 这里多一个"最深一层加密码"的开关，用来造"某一层解不开"）。
        /// </summary>
        private string BuildChain(int levelCount, string? innermostPassword = null)
        {
            Assert.True(levelCount >= 2, "至少要有 outer + 一层内层包");

            string build = Path.Combine(_root, "chain-" + levelCount);
            Directory.CreateDirectory(build);

            File.WriteAllText(Path.Combine(build, "final.txt"), "最深一层的内容\n", new UTF8Encoding(false));

            if (string.IsNullOrEmpty(innermostPassword))
            {
                Run7z(build, "a", "-t7z", $"level{levelCount}.7z", "final.txt");
            }
            else
            {
                Run7z(
                    build,
                    "a",
                    "-t7z",
                    $"level{levelCount}.7z",
                    "-p" + innermostPassword,
                    "-mhe=on",
                    "final.txt");
            }

            for (int level = levelCount - 1; level >= 2; level--)
            {
                Run7z(build, "a", "-t7z", $"level{level}.7z", $"level{level + 1}.7z");
            }

            string sourceDirectory = Path.Combine(_root, "src");
            Directory.CreateDirectory(sourceDirectory);

            string outer = Path.Combine(sourceDirectory, "outer.7z");
            Run7z(build, "a", "-t7z", outer, "level2.7z");

            /*
             * 造包用的中间层（level2..levelN）**必须清掉**：它们是"造样本的脚手架"，
             * 不是管线产物。留着的话本类的两条断言会自欺欺人 ——
             * 数"盘上还剩几个包"时会把脚手架也算进去（实测：四层链数出 7 个）。
             * outer.7z 里已经装着 level2.7z，后面的链路一个字节都不需要这个目录。
             */
            Directory.Delete(build, recursive: true);

            return outer;
        }

        private void Run7z(string workingDirectory, params object[] args)
        {
            var psi = new ProcessStartInfo(_sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            foreach (object a in args)
            {
                psi.ArgumentList.Add(a.ToString() ?? string.Empty);
            }

            using Process p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);

            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"7z 失败（exit {p.ExitCode}）：{string.Join(' ', psi.ArgumentList)}\n{stdout}\n{stderr}");
            }
        }

        /// <summary>在本次测试的临时根下面按文件名找文件（包会被搬到别的目录，所以按名字找）。</summary>
        private string[] FindFiles(string pattern) =>
            Directory.Exists(_root)
                ? Directory.GetFiles(_root, pattern, SearchOption.AllDirectories)
                : Array.Empty<string>();

        private Harness CreateHarness()
        {
            string dataRoot = Path.Combine(_root, "data");
            string outputRoot = Path.Combine(_root, "out");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CacheRootDirectory = dataRoot;
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;
            settings.TryEmptyPasswordFirst = false;

            // 刻意选"最不删东西"的两档：任何删除都只可能来自那个模式本身（对照组的判据也在这里）。
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            settings.CustomSevenZipExePath = string.Empty;

            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            /*
             * MainViewModel 的构造会顺手写两个进程级静态（7z 路径、递归工作区根目录）——
             * 这里先存下来、构造完立刻还原（与 InnerLayerContinuationTests 同一套做法）。
             */
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

            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var extraction = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());

            var deleteFileSystem = new RecordingDeleteFileSystem(_root);
            extraction.SourcePackageDeleteFileSystem = deleteFileSystem;

            /*
             * 默认档下**成功的任务在日志里只留一行**（用户 2026-09-25 第 44/45 条：一批几十个包时
             * 每个包十几行没法看），中间那些 INFO 会被收尾丢掉 —— 而本类要断言的"每一层都说过
             * 它收了源包"正是那些中间行。所以这里显式声明"这个用例要看细节"
             * （与 SpaceTightModeTests 同一做法，⛔ 不改产品的默认呈现）。
             */
            extraction.KeepTaskDetailInLog = true;

            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());

            return new Harness(vm, oneClick, logService, deleteFileSystem);
        }

        private sealed class Harness
        {
            private readonly OneClickCoordinator _oneClick;

            public Harness(
                MainViewModel vm,
                OneClickCoordinator oneClick,
                LogService log,
                RecordingDeleteFileSystem deleteFileSystem)
            {
                Vm = vm;
                _oneClick = oneClick;
                Log = log;
                DeleteFileSystem = deleteFileSystem;
            }

            public MainViewModel Vm { get; }

            public LogService Log { get; }

            public RecordingDeleteFileSystem DeleteFileSystem { get; }

            public Task AddPathsAsync(params string[] paths) => Vm.AddPathsAsync(paths);

            public Task<OneClickOutcome> RunOneClickAsync() => _oneClick.RunPipelineAsync(Vm.Tasks.ToList());
        }

        /// <summary>
        /// 记账式的"删源包"执行体（产品代码默认走真实文件系统，这里只替换执行体、判据一个字不改）。
        ///
        /// <para>它要回答两个问题：<b>删了几次、每次删的时候盘上还留着几个包</b> ——
        /// 后者就是"每层立刻回收"与"攒到链尾一起删"的分水岭。</para>
        /// </summary>
        private sealed class RecordingDeleteFileSystem : ISourceDeleteFileSystem
        {
            private readonly string _root;

            public RecordingDeleteFileSystem(string root) => _root = root;

            public List<string> DeletedPaths { get; } = new();

            /// <summary>每次删除那一刻，盘上**别的** <c>*.7z</c> 最多有几个。</summary>
            public int MaxPackagesSeenAtDelete { get; private set; }

            public bool FileExists(string path) => File.Exists(path);

            public void DeleteFile(string path)
            {
                int others = Directory.Exists(_root)
                    ? Directory.GetFiles(_root, "*.7z", SearchOption.AllDirectories)
                        .Count(file => !string.Equals(file, path, StringComparison.OrdinalIgnoreCase))
                    : 0;

                MaxPackagesSeenAtDelete = Math.Max(MaxPackagesSeenAtDelete, others);
                DeletedPaths.Add(path);

                File.Delete(path);
            }
        }

        /// <summary>每 10 毫秒数一遍盘上的 <c>*.7z</c>，记下同时存在的最大值（多层链的"峰值"）。</summary>
        private sealed class PackagePeakSampler : IDisposable
        {
            private readonly string _root;
            private readonly List<string> _lastSnapshot = new();
            private volatile bool _running;
            private Task? _loop;

            public PackagePeakSampler(string root) => _root = root;

            public int Max { get; private set; }

            /// <summary>峰值那一刻盘上的包（断言失败时看得见是哪几个）。</summary>
            public IReadOnlyList<string> PeakSnapshot => _lastSnapshot;

            public void Start()
            {
                _running = true;

                _loop = Task.Run(
                    async () =>
                    {
                        while (_running)
                        {
                            Observe();
                            await Task.Delay(10).ConfigureAwait(false);
                        }
                    });
            }

            public void Stop()
            {
                _running = false;

                try
                {
                    _loop?.Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // 采样线程收不干净不影响结论。
                }
            }

            public void Dispose() => Stop();

            private void Observe()
            {
                try
                {
                    if (!Directory.Exists(_root))
                    {
                        return;
                    }

                    string[] packages = Directory.GetFiles(_root, "*.7z", SearchOption.AllDirectories);

                    if (packages.Length > Max)
                    {
                        Max = packages.Length;

                        _lastSnapshot.Clear();
                        _lastSnapshot.AddRange(packages.Select(Path.GetFileName).OfType<string>());
                    }
                }
                catch
                {
                    // 采样是观测行为：目录正在被搬动时读不到就当这一帧没看见。
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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
    /// 「源包被删掉」这条路的回归测试（2026-09-25 第 32 条之后 = 源包放入其余物 + 删除操作=彻底删除；旧档名 DeleteAfterVerify 已删）
    /// （用户 2026-09-24 第 19.4 条）。
    ///
    /// <para>
    /// 用户原话的意思：设置里选了"校验通过后彻底删除源包"，**选中文件夹**时里面那些包一个都没被删，
    /// 而**选中单个文件**时删了。
    /// </para>
    /// <para>
    /// 诊断结论（见 <c>docs/需求变更.md</c> 末节）：差别不在"文件夹"这个动作本身 ——
    /// 扫描文件夹与扫描单文件生成的任务**完全一样**（<c>FileScanService.ScanPathsAsync</c> 两条路都走
    /// <c>ScanFile</c>）。真正的原因是**形状**：
    /// 文件夹里的包绝大多数是"第一层只出内层包"的多层包（假 MP4 + 尾部 ZIP + 内层加密分卷），
    /// 于是它们被记账成"留到链结束后补做"；而链结束后的那一段（<c>RunDeferredSourceMoveWork</c>）
    /// 老代码只实现了 <c>MoveToRest</c>，那条"删源包"的老档走到那里写一句"按该档不搬源包"就返回了 ——
    /// **等于什么都没做**。单个文件那种"第一层直接出内容物"的形状不经过延期，所以当场就删掉了。
    /// </para>
    /// </summary>
    public class SourceDeleteAfterVerifyTests : IDisposable
    {
        private readonly string _root;

        public SourceDeleteAfterVerifyTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerDeleteVerify", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响测试结论。
            }
        }

        // ================================================================ ① 文件夹展开出的单个包：成功就删

        /// <summary>
        /// 用户要求的那条：**选中文件夹**（走真正的"添加文件夹"扫描路径）展开出来的任务，
        /// 成功 + 校验通过 → 源文件被删，与单个文件的路径**完全一致**。
        /// </summary>
        [Fact]
        public async Task 文件夹展开出的单个包_校验通过后源文件被删()
        {
            Harness harness = CreateHarness(settings =>
            {
                // 2026-09-25 第 32 条之后"删源包"由两档组合表达：
                // 源包放入其余物 + 删除操作=彻底删除（与旧 DeleteAfterVerify 等价）。
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
            });

            string source = harness.CreateSourceInFolder("folder", "pack.7z");

            ArchiveTask task = await harness.ScanFolderAndAddTask(Path.GetDirectoryName(source)!);
            harness.Engine.SetProducts(("payload-00000.bin", 8));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.True(task.IsOutputVerified);
            Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

            Assert.False(File.Exists(source), "文件夹展开出来的包成功之后，源文件必须被删");
            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("其余物已彻底删除", StringComparison.Ordinal));
        }

        // ================================================================ ② 失败：一律不删

        [Fact]
        public async Task 失败_一个字节都不删()
        {
            Harness harness = CreateHarness(settings =>
            {
                // 2026-09-25 第 32 条之后"删源包"由两档组合表达：
                // 源包放入其余物 + 删除操作=彻底删除（与旧 DeleteAfterVerify 等价）。
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
            });

            string source = harness.CreateSourceInFolder("folder", "pack.7z");
            ArchiveTask task = await harness.ScanFolderAndAddTask(Path.GetDirectoryName(source)!);

            harness.Engine.ExtractFailure = new ArchiveOperationResult
            {
                Success = false,
                Status = StatusText.Corrupted,
                Message = "文件损坏",
                DetectedErrorType = "Corrupted"
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.True(File.Exists(source), "失败时源包必须原地不动");
        }

        /// <summary>同一个红线的"校验未通过"版：解压出来了但产物对不上 → 照样一个字节都不删。</summary>
        [Fact]
        public async Task 校验未通过_一个字节都不删_而且日志里说清为什么()
        {
            Harness harness = CreateHarness(settings =>
            {
                // 2026-09-25 第 32 条之后"删源包"由两档组合表达：
                // 源包放入其余物 + 删除操作=彻底删除（与旧 DeleteAfterVerify 等价）。
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
            });

            string source = harness.CreateSourceInFolder("folder", "pack.7z");
            ArchiveTask task = await harness.ScanFolderAndAddTask(Path.GetDirectoryName(source)!);

            harness.Engine.SetProducts(("payload-00000.bin", 8));
            harness.Engine.ExpectedTotalSize = 9999;   // 清单说 9999 字节，实际只有 8 → 校验不过

            await harness.Coordinator.StartExtractAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.True(File.Exists(source), "校验未通过时源包必须原地不动");

            // 为什么没删必须说清楚（老代码在"没删"时一声不吭 —— 那正是用户"没删也没解释"的来源）：
            // ① 日志里那行校验结论带着三个数字；② 任务上的原因也带（失败清单里看得见）。
            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("结果校验 —— 校验未通过", StringComparison.Ordinal) &&
                     x.Message.Contains("预期 1 个文件 / 9999 字节", StringComparison.Ordinal) &&
                     x.Message.Contains("实际 1 个 / 8 字节", StringComparison.Ordinal));

            Assert.Contains("产物校验未通过", task.ErrorMessage, StringComparison.Ordinal);
        }

        // ================================================================ ③ 分卷组：整组一起删

        [Fact]
        public async Task 分卷组成功_整组被删()
        {
            Harness harness = CreateHarness(settings =>
            {
                // 2026-09-25 第 32 条之后"删源包"由两档组合表达：
                // 源包放入其余物 + 删除操作=彻底删除（与旧 DeleteAfterVerify 等价）。
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
            });

            string folder = Path.Combine(_root, "src", "volumes");
            Directory.CreateDirectory(folder);

            string first = Path.Combine(folder, "222.7z.001");
            string second = Path.Combine(folder, "222.7z.002");
            File.WriteAllText(first, new string('a', 64));
            File.WriteAllText(second, new string('b', 64));

            // 走真正的扫描路径（分卷归组也在那一步做）：一组分卷 = 一个任务。
            ArchiveTask task = await harness.ScanFolderAndAddTask(folder);

            Assert.True(task.IsVolumeGroup, "前提：扫描应当把这两卷归成一组");
            Assert.Equal(2, task.VolumeCount);

            harness.Engine.SetProducts(("payload-00000.bin", 8));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.False(File.Exists(first), "第一卷没有被删");
            Assert.False(File.Exists(second), "第二卷被落下了（整组必须一起删）");
        }

        // ================================================================ ④ 取消：一律不删

        [Fact]
        public async Task 取消_一个字节都不删()
        {
            Harness harness = CreateHarness(settings =>
            {
                // 2026-09-25 第 32 条之后"删源包"由两档组合表达：
                // 源包放入其余物 + 删除操作=彻底删除（与旧 DeleteAfterVerify 等价）。
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
            });

            string source = harness.CreateSourceInFolder("folder", "pack.7z");
            ArchiveTask task = await harness.ScanFolderAndAddTask(Path.GetDirectoryName(source)!);

            // 解压已经做完、进入收尾（收尾第一件事就是列目录）时按下「取消当前」。
            harness.Engine.OnList = _ =>
            {
                if (harness.Engine.Extracted)
                {
                    harness.Coordinator.CancelCurrentTask();
                }
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.Cancelled, task.Status);
            Assert.True(File.Exists(source), "取消时源包必须原地不动");
        }

        // ================================================================ ⑤ 多层包：链结束后才删（诊断出来的那条路）

        /// <summary>
        /// <b>本次诊断出来的那条路（修复前 100% 不删）</b>：一键处理 + 第一层只出内层包
        /// （真实形状：假 MP4 → 内层加密分卷）→ 源包处理被延期 → 链跑完、内容物出来之后**按档位删**。
        ///
        /// <para>
        /// 修复前的行为：<c>RunDeferredSourceMoveWork</c> 里 <c>sourceHandling != MoveToRest</c> 直接返回，
        /// 源包永远留在原地 —— 用户看到的正是"选中文件夹时里面东西没删"。
        /// </para>
        /// </summary>
        [SevenZipFact]
        public async Task 一键处理_第一层只出中间件_链跑完后按删除档删源包()
        {
            string sevenZip = SevenZipFactAttribute.LocateSevenZipPath();

            Harness harness = CreateHarness(
                settings =>
                {
                    settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                    settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
                },
                engine: new SevenZipEngine());

            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;
            ToolLocator.Default.CustomSevenZipExePath = sevenZip;
            ToolLocator.Default.Invalidate();

            try
            {
                // 外层 7z（-mhe 加密）里装的不是内容物，而是**内层加密分卷** —— 这就是"多层包"的形状。
                string innerDirectory = Path.Combine(_root, "inner");
                Directory.CreateDirectory(innerDirectory);
                File.WriteAllText(Path.Combine(innerDirectory, "readme.txt"), "内层内容物");

                string innerArchive = Path.Combine(innerDirectory, "inner.7z");
                RunSevenZip(sevenZip, new[] { "a", "-t7z", "-pDemo#Pass1", innerArchive, Path.Combine(innerDirectory, "readme.txt") });

                string outerStage = Path.Combine(_root, "outer-stage");
                Directory.CreateDirectory(outerStage);
                File.Copy(innerArchive, Path.Combine(outerStage, "inner.7z"), overwrite: true);

                string outerArchive = Path.Combine(_root, "src", "outer.7z");
                Directory.CreateDirectory(Path.GetDirectoryName(outerArchive)!);
                RunSevenZip(sevenZip, new[] { "a", "-t7z", "-pDemo#Pass1", "-mhe=on", outerArchive, Path.Combine(outerStage, "inner.7z") });

                harness.SetPasswordCandidates("Demo#Pass1");

                ArchiveTask task = harness.AddTask(outerArchive);

                OneClickOutcome outcome = await harness.RunOneClickAsync();

                Assert.True(outcome.ContinuationLayers >= 1, "前提：这一单必须是「第一层只出中间件」的多层包");

                Assert.Equal(StatusText.ExtractSuccess, task.Status);
                Assert.Equal(OutputVerificationOutcome.Passed, task.OutputVerification);
                Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

                // 内容物确实出来了。
                Assert.Contains(
                    Directory.EnumerateFiles(harness.OutputRoot, "readme.txt", SearchOption.AllDirectories),
                    path => File.ReadAllText(path) == "内层内容物");

                // 源包被删（修复前这里是"还在"）。
                Assert.False(File.Exists(outerArchive), "链跑完、内容物出来之后，删除档必须把源包删掉");

                /*
                 * ⚠ 这一条才是本用例的**牙齿**（2026-09-25 第 32 条收尾时补的）：
                 * 上面那条 `File.Exists(outerArchive)` 光看"源包还在不在原位"是**判不出**删除有没有发生的 ——
                 * 源包按「源包操作」被搬进其余物之后原位也没有了，于是"该删的没删"能一路绿灯。
                 * 其余物目录本身在不在，才是用户看到的那件事（"源包确实没有了，但是其余物还在"）。
                 */
                Assert.False(string.IsNullOrWhiteSpace(task.RestDirectoryPath), "前提：这一单必须记下了其余物位置");
                Assert.False(
                    Directory.Exists(task.RestDirectoryPath),
                    $"选了彻底删除，链尾必须把其余物那一份删掉，实际还在：{task.RestDirectoryPath}");

                // 第 32 条之后这句话改口径了：源包先按「源包操作」进其余物，链尾再按「删除操作」处理那一份
                // 其余物 —— 日志如实写"链结束后的其余物处理"，不再是旧的"链结束后的清理"。
                Assert.Contains(
                    harness.Log.Logs,
                    x => x.Message.Contains("链结束后的其余物处理", StringComparison.Ordinal));
            }
            finally
            {
                ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;
                ToolLocator.Default.Invalidate();
            }
        }

        /// <summary>
        /// 链里有一层校验没过 → **不许删**（"失败 / 校验未通过一律不删"这条红线在多层的路上同样成立）。
        /// </summary>
        [Fact]
        public async Task 一键处理_链里有一层校验没过_不删源包()
        {
            Harness harness = CreateHarness(settings =>
            {
                // 2026-09-25 第 32 条之后"删源包"由两档组合表达：
                // 源包放入其余物 + 删除操作=彻底删除（与旧 DeleteAfterVerify 等价）。
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
            });

            string source = harness.CreateSourceInFolder("folder", "outer.7z");
            ArchiveTask task = harness.AddTask(source);

            // 第一层只出中间件 → 延期；第二层（人工加的续解任务）校验不过。
            harness.Engine.SetProducts(("inner.7z", 16));

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.Equal(1, outcome.Rounds);
            Assert.Equal(SourcePackageMoveState.DeferredToChainEnd, task.SourcePackageMove);

            ArchiveTask continuation = harness.AddContinuationTask(task, "inner.7z");

            // 清单说 99 个文件，实际只落 1 个 → 校验不过（顺序要紧：SetProducts 会重置预期值）。
            harness.Engine.SetProducts(("content.bin", 8));
            harness.Engine.ExpectedFileCount = 99;

            await harness.Coordinator.StartExtractForOneClickAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, continuation.Status);

            await harness.Coordinator.CompleteRootSourcePackagesAfterChainAsync(
                new[] { task },
                new[] { task, continuation });

            Assert.True(File.Exists(source), "链里有一层校验没过时，源包必须原地不动");
        }

        /// <summary>调真实 7z.exe（用 ArgumentList 安全传参，不拼命令行字符串 —— 不变量 10）。</summary>
        private static void RunSevenZip(string sevenZipPath, string[] arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = sevenZipPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (string argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 7z.exe");

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(60_000), "7z.exe 超时未退出");
            Assert.Equal(0, process.ExitCode);
        }

        // ================================================================ 装配

        private Harness CreateHarness(Action<AppSettings>? configure = null, IArchiveEngine? engine = null)
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

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var fake = engine as FakeEngine ?? new FakeEngine();
            IArchiveEngine effectiveEngine = engine ?? fake;

            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                effectiveEngine,
                passwordService,
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            var coordinator = new ExtractionCoordinator(vm, effectiveEngine, passwordService, pathService, new DialogService());
            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var oneClick = new OneClickCoordinator(vm, scan, rename, coordinator, new DialogService());

            return new Harness(vm, fake, passwordService, coordinator, oneClick, logService, outputRoot, dataRoot);
        }

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                FakeEngine engine,
                PasswordService passwordService,
                ExtractionCoordinator coordinator,
                OneClickCoordinator oneClick,
                LogService log,
                string outputRoot,
                string dataRoot)
            {
                Vm = vm;
                Engine = engine;
                PasswordService = passwordService;
                Coordinator = coordinator;
                OneClick = oneClick;
                Log = log;
                OutputRoot = outputRoot;
                DataRoot = dataRoot;
            }

            public MainViewModel Vm { get; }

            public FakeEngine Engine { get; }

            public PasswordService PasswordService { get; }

            public ExtractionCoordinator Coordinator { get; }

            public OneClickCoordinator OneClick { get; }

            public LogService Log { get; }

            public string OutputRoot { get; }

            public string DataRoot { get; }

            public string CreateSourceInFolder(string folderName, string fileName)
            {
                string directory = Path.Combine(DataRoot, "..", "src", folderName);
                Directory.CreateDirectory(directory);

                string path = Path.Combine(directory, fileName);
                File.WriteAllText(path, "不是真的压缩包（这一组用的是假引擎）");
                return path;
            }

            /// <summary>
            /// 走**真正的"添加文件夹"路径**（<c>FileScanService.ScanPathsAsync</c> + 分卷归组）造任务，
            /// 而不是手工 <c>new ArchiveTask</c> —— 用户报的是"选中文件夹"这条路上的行为，
            /// 任务是怎么造出来的正是要覆盖的部分。
            /// </summary>
            public async Task<ArchiveTask> ScanFolderAndAddTask(string folder)
            {
                var scanService = new FileScanService();

                List<ArchiveTask> scanned = await scanService.ScanPathsAsync(
                    new[] { folder },
                    new ScanOptions { RecursiveScan = true, ScanMode = "ScanAllFiles" });

                Assert.NotEmpty(scanned);

                ArchiveTask task = scanned[0];
                task.IsSelected = true;
                task.Status = StatusText.Recognized;
                task.Index = Vm.Tasks.Count + 1;

                Vm.Tasks.Add(task);
                return task;
            }

            public ArchiveTask AddTask(string sourcePath)
            {
                var task = new ArchiveTask(sourcePath, Vm.Tasks.Count + 1)
                {
                    IsArchive = true,
                    DetectedFormat = "7Z",
                    ExtensionStatus = StatusText.ExtensionNormal,
                    Status = StatusText.Recognized,
                    IsSelected = true
                };

                Vm.Tasks.Add(task);
                return task;
            }

            /// <summary>人工造一个"续解出来的内层包"任务（它落在父任务输出目录的其余物里）。</summary>
            public ArchiveTask AddContinuationTask(ArchiveTask parent, string fileName)
            {
                string restDirectory = parent.RestDirectoryPath;

                if (string.IsNullOrWhiteSpace(restDirectory))
                {
                    restDirectory = Path.Combine(parent.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName);
                }

                parent.IsSelected = false;

                string path = Path.Combine(restDirectory, fileName);

                var continuation = new ArchiveTask(path, Vm.Tasks.Count + 1)
                {
                    IsArchive = true,
                    DetectedFormat = "7Z",
                    ExtensionStatus = StatusText.ExtensionNormal,
                    Status = StatusText.Recognized,
                    IsSelected = true,
                    ParentOutputDirectory = parent.OutputPath,
                    ParentTaskName = parent.FileName
                };

                Vm.Tasks.Add(continuation);
                return continuation;
            }

            public void SetPasswordCandidates(params string[] values)
            {
                PasswordService.ClearPasswords();

                foreach (string value in values)
                {
                    PasswordService.AddPassword(value);
                }
            }

            /// <summary>跑一键处理的流程部分（不弹任何对话框 —— 测试里没人在那儿点确定）。</summary>
            public Task<OneClickOutcome> RunOneClickAsync() => OneClick.RunPipelineAsync(Vm.Tasks.ToList());
        }

        private sealed class FakeEngine : IArchiveEngine
        {
            private List<(string Name, int Size)> _products = new() { ("payload-00000.bin", 8) };

            public int ExpectedFileCount { get; set; } = 1;

            public long ExpectedTotalSize { get; set; } = 8;

            /// <summary>非 null = 解压直接返回这个失败结论。</summary>
            public ArchiveOperationResult? ExtractFailure { get; set; }

            /// <summary>列目录前回调（收尾第一件事就是列目录：用来在那一刻按取消）。</summary>
            public Action<ArchiveRequest>? OnList { get; set; }

            public bool Extracted { get; private set; }

            public void SetProducts(params (string Name, int Size)[] products)
            {
                _products = products.ToList();
                ExpectedFileCount = products.Length;
                ExpectedTotalSize = products.Sum(p => (long)p.Size);
            }

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
                => Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                OnList?.Invoke(request);

                return Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    FileCount = ExpectedFileCount,
                    TotalUncompressedSize = ExpectedTotalSize,
                    Entries = Enumerable.Range(0, ExpectedFileCount)
                        .Select(i => new ArchiveEntry { Path = $"entry-{i:D5}.bin", Size = 1 })
                        .ToList(),
                    EngineId = "fake",
                    EngineVersion = "1.0"
                });
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(Succeeded());

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                if (ExtractFailure != null)
                {
                    return Task.FromResult(ExtractFailure);
                }

                string output = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);

                    foreach ((string name, int size) in _products)
                    {
                        File.WriteAllBytes(Path.Combine(output, name), new byte[size]);
                    }
                }

                Extracted = true;

                return Task.FromResult(Succeeded());
            }

            private static ArchiveOperationResult Succeeded() => new()
            {
                Success = true,
                Status = StatusText.ExtractSuccess,
                Message = "解压成功",
                DetectedErrorType = "None"
            };
        }
    }
}

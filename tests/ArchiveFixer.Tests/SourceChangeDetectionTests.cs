using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
    /// **不变量 11：源文件变化后不得继续使用旧识别结果**（AGENTS.md §6 第 11 条）的端到端测试。
    ///
    /// <para>与其它管线测试同形：真 <see cref="MainViewModel"/> + 真解压管线，只把归档引擎换成假引擎。
    /// 这一组要证明的是"跑起来真的会那样"，而不是"纯模型算得对"（后者由 <c>快照</c> 那一条覆盖）：</para>
    /// <list type="number">
    /// <item><description><b>扫描后改内容（大小变）</b> → 状态「源文件已变化」、**引擎一次都没被调用**、
    /// 源包原地不动、无产物、无 <c>其余物</c>；</description></item>
    /// <item><description><b>只改修改时间（大小不变）</b> → 同样判变化（不变量 11 就是这么写的：
    /// 记的是"大小 + 修改时间"，不比对内容哈希）；</description></item>
    /// <item><description><b>没改</b> → 照常成功（哨兵：别把这条红线做成"谁都拦"）；</description></item>
    /// <item><description><b>解压前文件被删</b> → 判变化，理由写明"文件不见了"；</description></item>
    /// <item><description><b>分卷组改了第二个卷</b> → 判变化，理由**点出是哪一个卷**；</description></item>
    /// <item><description><b>没有快照的老任务</b>（测试直接 <c>new</c> 出来的任务）→ **不拦**，
    /// 开工前补拍一次后正常跑完。</description></item>
    /// </list>
    ///
    /// <para>MainViewModel 的构造会写两个进程级静态（7z 路径 / 递归工作区根目录），
    /// 所以与其它管线测试同一组串行，并在装配后立刻还原。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class SourceChangeDetectionTests : IDisposable
    {
        private readonly string _root;

        public SourceChangeDetectionTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSourceChange", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 改了内容：停下

        [Fact]
        public async Task 扫描后改了内容_状态落源文件已变化_并且引擎一次都没被调用()
        {
            Harness harness = CreateHarness();

            string source = CreateSourceFile("222.7z", 4096);
            ArchiveTask task = AddTask(harness, source);

            // 扫描（识别）完成 → 快照落下来；此后才是"用户改文件"的那一刻。
            Assert.True(task.HasSourceSnapshot, "扫描之后必须已经有一份基准快照");
            Assert.NotNull(task.SourceSnapshotTime);

            task.OutputPath = Path.Combine(harness.OutputRoot, "222");

            // 改内容（大小真的变了）。
            File.WriteAllText(source, new string('x', 8192));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.SourceChanged, task.Status);

            // 理由里必须点名"哪一个文件、哪一项变了"。
            Assert.Contains("源文件已变化", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("222.7z", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("大小", task.ErrorMessage, StringComparison.Ordinal);

            // 出路也要给出来（与"分卷缺失"同一口径的重新扫描）。
            Assert.Contains("重新扫描", task.ErrorMessage, StringComparison.Ordinal);

            // ② 引擎一次都没被调用 —— 这是"不许继续使用旧识别结果"最硬的判据。
            Assert.Equal(0, harness.Engine.TotalCalls);
            Assert.Equal(0, harness.Engine.ListCalls);
            Assert.Equal(0, harness.Engine.ExtractCalls);
            Assert.Equal(0, harness.Engine.TestCalls);

            // ③ 不变量 1 的红线照旧：源包原地不动、无产物、无其余物、无中间品。
            Assert.True(File.Exists(source), "源包必须原地不动");
            Assert.Equal(8192, new FileInfo(source).Length);
            Assert.False(Directory.Exists(task.OutputPath), $"不该建输出目录：{task.OutputPath}");
            Assert.False(
                Directory.Exists(Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName)),
                "不该生成其余物");
            Assert.Empty(Directory.GetFiles(harness.OutputRoot, "*", SearchOption.AllDirectories));

            // ④ 失败清单里必须看得到它（不变量 6 的同一精神：没做成的事不许消失）。
            var summary = new TaskSummaryService();

            Assert.True(summary.IsFailedStatus(StatusText.SourceChanged));
            Assert.Contains(StatusText.SourceChanged, summary.BuildFailedListText(harness.Vm.Tasks));
        }

        // ================================================================ ② 只改时间：照样停下

        [Fact]
        public async Task 扫描后只改了修改时间_大小没变_同样判为源文件已变化()
        {
            Harness harness = CreateHarness();

            string source = CreateSourceFile("333.7z", 2048);
            ArchiveTask task = AddTask(harness, source);

            task.OutputPath = Path.Combine(harness.OutputRoot, "333");

            long lengthBefore = new FileInfo(source).Length;

            /*
             * 只动时间戳，**一个字节都不写**（不变量 11 记的是"大小 + 修改时间"，
             * 所以"大小没变"不是放行的理由）。
             *
             * 刻意设成一个**很久以前**的时刻：不能靠"写一下 mtime 自然会往前走" ——
             * 测试跑得够快时两次 stat 会落在同一个时间刻度上，那样这条用例会变成假绿。
             */
            DateTime changed = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(source, changed);

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.SourceChanged, task.Status);
            Assert.Equal(lengthBefore, new FileInfo(source).Length);   // 大小确实没变
            Assert.Contains("修改时间", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("333.7z", task.ErrorMessage, StringComparison.Ordinal);

            // 同样：引擎一次都没被调用、源包原地不动、没有产物。
            Assert.Equal(0, harness.Engine.TotalCalls);
            Assert.True(File.Exists(source));
            Assert.False(Directory.Exists(task.OutputPath));
            Assert.Empty(Directory.GetFiles(harness.OutputRoot, "*", SearchOption.AllDirectories));
        }

        // ================================================================ ③ 没改：正常成功

        [Fact]
        public async Task 源文件没变时_照常解压成功()
        {
            Harness harness = CreateHarness();

            string source = CreateSourceFile("444.7z", 1024);
            ArchiveTask task = AddTask(harness, source);

            WireSuccessfulExtraction(harness, "payload.mp4", "内容物");

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.NotEqual(StatusText.SourceChanged, task.Status);
            Assert.True(task.IsOutputVerified, "输出校验应当通过（假引擎的产物与清单一致）");
            Assert.True(harness.Engine.ExtractCalls > 0, "没变化就该真的调引擎");
            Assert.True(File.Exists(Path.Combine(task.OutputPath, "payload.mp4")));

            // 源文件留在原地（这一组默认 KeepInPlace）。
            Assert.True(File.Exists(source));
            Assert.False(string.IsNullOrWhiteSpace(task.SourceSnapshotTime?.ToString()));
        }

        // ================================================================ ④ 文件被删

        [Fact]
        public async Task 解压前文件被删_判源文件已变化_理由写明文件不见了()
        {
            Harness harness = CreateHarness();

            string source = CreateSourceFile("555.7z", 1024);
            ArchiveTask task = AddTask(harness, source);

            task.OutputPath = Path.Combine(harness.OutputRoot, "555");

            File.Delete(source);

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.SourceChanged, task.Status);
            Assert.Contains("文件不见了", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("555.7z", task.ErrorMessage, StringComparison.Ordinal);

            // 引擎没被调用 —— 这一条同时证明它没有被"文件不存在"那条旧路径吃掉结论。
            Assert.Equal(0, harness.Engine.TotalCalls);
            Assert.False(Directory.Exists(task.OutputPath));
        }

        // ================================================================ ⑤ 分卷组：动了第二个卷

        [Fact]
        public async Task 分卷组里改了第二个卷_判源文件已变化_并且点名是哪一个卷()
        {
            Harness harness = CreateHarness();

            string first = CreateSourceFile("666.7z.001", 1024);
            string second = CreateSourceFile("666.7z.002", 1024);

            ArchiveTask task = AddTask(harness, first);

            // 一组分卷 = 一个任务（AGENTS.md §9.3）：整组都进快照。
            task.IsVolumeGroup = true;
            task.IsVolumeComplete = true;
            task.VolumeGroupKey = "666";
            task.VolumePaths.Add(first);
            task.VolumePaths.Add(second);

            task.CaptureSourceSnapshot();

            task.OutputPath = Path.Combine(harness.OutputRoot, "666");

            // 只动**第二卷**（第一卷一个字节都没变）。
            File.WriteAllText(second, new string('y', 3072));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.SourceChanged, task.Status);

            // 理由必须点出是哪一个卷 —— 只说"文件变了"用户不知道该去找哪一卷。
            Assert.Contains("666.7z.002", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("分卷", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("大小", task.ErrorMessage, StringComparison.Ordinal);

            // 第一卷没被冤枉地点名（它确实没变）。
            Assert.DoesNotContain("666.7z.001（", task.ErrorMessage, StringComparison.Ordinal);

            Assert.Equal(0, harness.Engine.TotalCalls);
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
            Assert.False(Directory.Exists(task.OutputPath));
        }

        // ================================================================ ⑥ 老任务：没有快照就补拍

        [Fact]
        public async Task 没有快照的老任务_不拦_开工前补拍之后正常跑完()
        {
            Harness harness = CreateHarness();

            string source = CreateSourceFile("777.7z", 1024);

            /*
             * 刻意**不走扫描**：直接 new 一个已识别的任务 —— 模拟"这个功能落地之前就建好的老任务"、
             * 或者测试 / 别的宿主直接塞进来的任务。它没有快照。
             */
            var task = new ArchiveTask(source, 1)
            {
                IsArchive = true,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal,
                Status = StatusText.Recognized,
                IsSelected = true
            };

            harness.Vm.Tasks.Add(task);

            Assert.False(task.HasSourceSnapshot, "这条用例的前提就是「没有基准」");

            WireSuccessfulExtraction(harness, "payload.mp4", "内容物");

            await harness.Coordinator.StartExtractAsync();

            // 不拦：照常成功，而且基准也补上了（下一次运行才有得比）。
            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.True(task.HasSourceSnapshot, "开工前必须补拍一次基准");
            Assert.True(harness.Engine.ExtractCalls > 0);
            Assert.True(File.Exists(Path.Combine(task.OutputPath, "payload.mp4")));
        }

        // ================================================================ ⑦ 纯模型：快照判定

        [Fact]
        public void 快照比对_大小_时间_缺失三种差异都要认出来()
        {
            string directory = Path.Combine(_root, "snapshot");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, "sample.7z");
            File.WriteAllText(path, "0123456789");

            SourceFileSnapshot snapshot = SourceFileSnapshot.Capture(new[] { path });

            // ① 什么都没动 → 不算变化。
            Assert.False(snapshot.Compare(new[] { path }).Changed);

            // ② 大小变了。
            File.WriteAllText(path, "0123456789ABCDEF");

            SourceChangeResult bySize = snapshot.Compare(new[] { path });

            Assert.True(bySize.Changed);
            Assert.Equal(SourceChangeKind.Size, Assert.Single(bySize.Changes).Kind);
            Assert.Contains("大小", SourceFileSnapshot.DescribeChange(bySize), StringComparison.Ordinal);

            // ③ 大小一样、只有修改时间变了 → 同样算变化（不变量 11 的原文口径）。
            File.WriteAllText(path, "0123456789");
            File.SetLastWriteTimeUtc(path, new DateTime(2019, 5, 6, 7, 8, 9, DateTimeKind.Utc));

            SourceChangeResult byTime = snapshot.Compare(new[] { path });

            Assert.True(byTime.Changed);
            Assert.Equal(SourceChangeKind.LastWriteTime, Assert.Single(byTime.Changes).Kind);
            Assert.Contains("修改时间", SourceFileSnapshot.DescribeChange(byTime), StringComparison.Ordinal);

            // ④ 文件不见了（"当时在不在"也要记，否则"一直都不在"会被每次都报成变化）。
            File.Delete(path);

            SourceChangeResult byMissing = snapshot.Compare(new[] { path });

            Assert.True(byMissing.Changed);
            Assert.Equal(SourceChangeKind.Missing, Assert.Single(byMissing.Changes).Kind);
            Assert.Contains("文件不见了", SourceFileSnapshot.DescribeChange(byMissing), StringComparison.Ordinal);

            // ⑤ 快照拍的**那一刻**就不在 → 后来还是不在 → 不算变化（判据缺失 ≠ 判据不通过）。
            SourceFileSnapshot ofMissing = SourceFileSnapshot.Capture(new[] { path });

            SourceChangeResult afterMissing = ofMissing.Compare(new[] { path });

            Assert.False(
                afterMissing.Changed,
                "当时就不在、现在还是不在 → 不该算变化；实际差异："
                + string.Join(" / ", afterMissing.Changes.Select(c => $"{c.Kind}:{c.FileName}")));
        }

        // ================================================================ ⑧ 新状态：三处同改

        /// <summary>
        /// AGENTS.md §7：新增状态必须同时更新 <c>StatusText</c> + <c>StatusToBrushConverter</c> +
        /// <c>TaskSummaryService</c>，三处缺一不可（<c>OneClickCoordinator.IsFailureStatus</c> 是第四处，
        /// 它决定一键处理汇总里的"失败 N"）。这条用例把四处一次性钉住。
        /// </summary>
        [Fact]
        public void 新状态源文件已变化_文案配色统计四处都已接通()
        {
            // ① 文案
            Assert.Equal("源文件已变化", StatusText.SourceChanged);

            // ② 配色：与"解压失败"同属失败侧，绝不能给成功色
            var converter = new Converters.StatusToBrushConverter();

            Assert.Same(
                converter.ErrorBrush,
                converter.Convert(StatusText.SourceChanged, typeof(object), null!, null!));
            Assert.NotSame(
                converter.SuccessBrush,
                converter.Convert(StatusText.SourceChanged, typeof(object), null!, null!));

            // ③ 统计：算"解压失败"分项，并进失败清单
            var service = new TaskSummaryService();
            var task = new ArchiveTask(@"C:\t\222.7z", 1) { Status = StatusText.SourceChanged };

            TaskSummary summary = service.BuildSummary(new[] { task });

            Assert.Equal(1, summary.ExtractFailedCount);
            Assert.Equal(0, summary.PasswordErrorCount);
            Assert.True(service.IsFailedStatus(StatusText.SourceChanged));
            Assert.True(service.IsFailedOrUnknownTask(task));
            Assert.Contains(StatusText.SourceChanged, service.BuildFailedListText(new[] { task }));

            // ④ 一键处理的"失败"分项与"这一轮处理过了"的判据（漏一处会让汇总把终止态说成"未处理"）
            Assert.True(OneClickCoordinator.IsFailureStatus(task));
            Assert.True(OneClickCoordinator.IsHandled(task));
        }

        // ================================================================ 装配

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                FakeEngine engine,
                ExtractionCoordinator coordinator,
                LogService log,
                string outputRoot)
            {
                Vm = vm;
                Engine = engine;
                Coordinator = coordinator;
                Log = log;
                OutputRoot = outputRoot;
            }

            public MainViewModel Vm { get; }

            public FakeEngine Engine { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            public string OutputRoot { get; }
        }

        private Harness CreateHarness()
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

            // 与危险模式那组同一档：这一组要断言"源包到底动没动"，所以固定"留在原地"。
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.DeleteSourceAfterExtract = false;

            settingsService.Save(settings);

            var engine = new FakeEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            // MainViewModel 的构造会顺手写两个进程级静态：先存后还原（与其它管线测试同一套）。
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

            var coordinator = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());

            // 这个用例拿「细节日志」当行为证据（第 44 条之后，成功时默认只留两行）。
            coordinator.KeepTaskDetailInLog = true;

            return new Harness(vm, engine, coordinator, logService, outputRoot);
        }

        /// <summary>
        /// 建一个"已经扫描过"的任务：识别结果 + **快照**都齐（走的是与主流程同一条路：
        /// 先应用识别结果，再 <see cref="ArchiveTask.CaptureSourceSnapshot"/>）。
        /// </summary>
        private ArchiveTask AddTask(Harness harness, string sourcePath)
        {
            var task = new ArchiveTask(sourcePath, harness.Vm.Tasks.Count + 1);

            task.ApplyDetectResult(
                new DetectResult
                {
                    Format = "7Z",
                    SuggestedExtension = ".7z",
                    IsArchive = true,
                    IsKnownFormat = true,
                    Message = "识别为 7Z 压缩包"
                },
                StatusText.ExtensionNormal);

            task.IsSelected = true;

            // 与 ScanCoordinator 的落点一致：识别一完成就拍快照。
            task.CaptureSourceSnapshot();

            harness.Vm.Tasks.Add(task);
            return task;
        }

        private string CreateSourceFile(string fileName, int size)
        {
            string directory = Path.Combine(_root, "src");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, fileName);

            // 用真实大小的文件（不是"写一句话"）：这一组要断言的正是**大小**这一类差异。
            using FileStream stream = File.Create(path);
            stream.SetLength(size);

            return path;
        }

        private static void WireSuccessfulExtraction(Harness harness, string contentFileName, string content)
        {
            int size = System.Text.Encoding.UTF8.GetByteCount(content);

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(contentFileName, size));

            harness.Engine.OnExtractAsync = request =>
            {
                Directory.CreateDirectory(request.OutputPath!);
                File.WriteAllText(Path.Combine(request.OutputPath!, contentFileName), content);

                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = true,
                    Status = StatusText.ExtractSuccess,
                    Message = "解压成功",
                    DetectedErrorType = "None"
                });
            };
        }

        private static ArchiveListResult ListResult(string fileName, int size)
        {
            return new ArchiveListResult
            {
                Success = true,
                FileCount = 1,
                TotalUncompressedSize = size,
                Entries = new List<ArchiveEntry> { new() { Path = fileName, Size = size } },
                EngineId = "fake",
                EngineVersion = "1.0"
            };
        }

        /// <summary>可控的假引擎（与其它管线测试同形）+ **调用计数**（这一组最硬的判据靠它）。</summary>
        private sealed class FakeEngine : IArchiveEngine
        {
            private int _listCalls;
            private int _extractCalls;
            private int _testCalls;

            public Func<ArchiveRequest, Task<ArchiveListResult>>? OnListAsync { get; set; }

            public Func<ArchiveRequest, Task<ArchiveOperationResult>>? OnExtractAsync { get; set; }

            public int ListCalls => Volatile.Read(ref _listCalls);

            public int ExtractCalls => Volatile.Read(ref _extractCalls);

            public int TestCalls => Volatile.Read(ref _testCalls);

            public int TotalCalls => ListCalls + ExtractCalls + TestCalls;

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
                Interlocked.Increment(ref _listCalls);

                return OnListAsync != null
                    ? OnListAsync(request)
                    : Task.FromResult(ListResult("payload.bin", 1));
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _testCalls);

                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = true,
                    Status = StatusText.TestPassed,
                    DetectedErrorType = "None"
                });
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _extractCalls);

                return OnExtractAsync != null
                    ? OnExtractAsync(request)
                    : Task.FromResult(new ArchiveOperationResult
                    {
                        Success = true,
                        Status = StatusText.ExtractSuccess,
                        DetectedErrorType = "None"
                    });
            }
        }
    }
}

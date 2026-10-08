using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.WinRar;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 用户 2026-09-30 真机（`AAA` 那批 5 个任务：成功 0 / 失败 4 / 未处理 1）现场的三处缺陷，
    /// 一处一组用例。日志逐字见用户给的导出文件；每条用例都做过红检（撤掉修复即变红）。
    ///
    /// <list type="number">
    /// <item><description><b>整组改名之后，同一个批次里"被顺带改了名"的其它任务也要搬到新名字上</b>：
    /// 以前只同步驱动改名的那一单，别的任务继续指着已经不在磁盘上的旧名字 →
    /// 下一道不变量 11 比对报「源文件已变化（文件不见了）」，那一单连解压都没开始。</description></item>
    /// <item><description><b>RAR 的"一句两义"不许单独定原因</b>：<c>在加密文件 X 里校验和错误。文件已损坏或密码错误。</c>
    /// 既不是"文件损坏"（判成它就不再试其余候选）也不是"密码错误"（那会把真损坏说成密码错）。</description></item>
    /// <item><description><b>缺卷预检拦下也要落失败终态</b>：以前只写状态与原因、不落结束时间，
    /// 机器终态永远停在 <c>Pending</c> —— 批末汇总说「未处理 1」、批末诊断说「分卷缺失」，同一件事两处口径。</description></item>
    /// </list>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class RealMachineDefectFixesTests : IDisposable
    {
        private readonly string _root;

        public RealMachineDefectFixesTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerAaa", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 整组改名后的任务同步

        /// <summary>
        /// 真机现场（日志 L14-L17 / L37-L41 / L60 / L65）：同一个目录里 2 卷的名字都带垃圾
        /// （网盘那套 `删除` 尾巴），两个任务各自指着其中一卷。第一个任务开工前的"整组改名"
        /// 把**两卷一起**改成了标准名，第二个任务却没人同步 —— 8 秒后它被不变量 11 报成
        /// 「源文件已变化：文件不见了」，一个字节都没解。
        /// </summary>
        [Fact]
        public async Task 整组改名之后_同批别的任务也要搬到新名字_不再报源文件已变化()
        {
            Harness harness = CreateHarness();

            string first = CreateSourceFile("set.7z.001删除");
            string second = CreateSourceFile("set.7z.002删除");

            ArchiveTask firstTask = AddTask(harness, first);
            ArchiveTask secondTask = AddTask(harness, second);

            // 第一个任务是"这一组"的行：整组两卷都记在它身上（改名后这两项也必须跟着换）。
            firstTask.IsVolumeGroup = true;
            firstTask.IsVolumeComplete = true;
            firstTask.VolumePaths.Add(first);
            firstTask.VolumePaths.Add(second);
            firstTask.VolumeInfoText = "2 卷";

            await harness.Coordinator.StartExtractAsync();

            // ① 两个任务都指着磁盘上真实存在的新名字。
            Assert.Equal("set.7z.001", Path.GetFileName(firstTask.CurrentPath));
            Assert.Equal("set.7z.002", Path.GetFileName(secondTask.CurrentPath));
            Assert.True(File.Exists(firstTask.CurrentPath), "第一卷改名后任务路径必须存在");
            Assert.True(File.Exists(secondTask.CurrentPath), "第二卷改名后**别的任务**的路径也必须存在");

            // ② 那一组任务身上的 VolumePaths 也要跟着换（不许留着旧名字）。
            Assert.Equal(
                new[] { firstTask.CurrentPath, secondTask.CurrentPath },
                firstTask.VolumePaths.ToArray());

            // ③ 两个任务的快照都要与新名字对得上（路径换了不是"源文件变了"）。
            Assert.False(firstTask.CompareWithSourceSnapshot()?.Changed ?? true, "第一份快照必须重拍到新名字上");
            Assert.False(secondTask.CompareWithSourceSnapshot()?.Changed ?? true, "被顺带改名的任务同样必须重拍快照");

            // ④ 谁都不许再报"源文件已变化"（红检点：撤掉同步即出现这一行）。
            string all = string.Join("\n", harness.LogTexts);
            Assert.DoesNotContain(StatusText.SourceChanged, all, StringComparison.Ordinal);
            Assert.NotEqual(StatusText.SourceChanged, firstTask.Status);
            Assert.NotEqual(StatusText.SourceChanged, secondTask.Status);
        }

        /// <summary>
        /// **对照**（⛔ 不许放宽成"文件不在也算没变"）：快照拍完之后源文件真被改过，
        /// 照样必须拦下并报「源文件已变化」——这一条是 AGENTS.md §6 不变量 11 的底线。
        /// </summary>
        [Fact]
        public async Task 源文件真被改过_仍然要报源文件已变化()
        {
            Harness harness = CreateHarness();

            string source = CreateSourceFile("plain.7z");
            ArchiveTask task = AddTask(harness, source);

            // 拍完基准之后把内容改掉（文件名没变、路径没变，只有内容变了）。
            task.CaptureSourceSnapshot();
            File.AppendAllText(source, "用户在这中间动过这个文件");

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.SourceChanged, task.Status);
            Assert.Equal(TaskOutcome.Failed, task.Outcome);

            // 引擎一次都不许被调用（不变量 11：识别结果作废）。
            Assert.Empty(harness.Engine.ExtractCalls);

            Assert.Contains(harness.LogTexts, line => line.Contains(StatusText.SourceChanged, StringComparison.Ordinal));
        }

        // ================================================================ ② RAR 的"一句两义"

        /// <summary>
        /// **RAR 那句歧义原话在解析层就不许单独定原因**（真机日志 L89-L90）。
        ///
        /// <para>样本是本机 <c>Rar.exe</c> 现造的 RAR4 + <c>-p&lt;密码&gt;</c> 包（只读调用、不入库）：
        /// 本机实测这种包用**错密码**跑 <c>x</c> 的退出码就是 <c>3</c>，中文版 UnRAR 打的是
        /// 「在加密文件 X 里校验和错误。文件已损坏或密码错误。」—— 一句话里两种可能。
        /// 没有本机 Rar.exe 时本用例自己跳过（⛔ 不伪装成验过）。</para>
        /// </summary>
        [Fact]
        public void RAR那句两义原话_不许单独定成损坏_也不许当成密码错()
        {
            string? rarExe = RarSampleSet.LocateRarExe();

            Assert.True(rarExe != null, "这一组要本机已装的 WinRAR（Rar.exe）才能造样本 —— 没有就跳过。");

            string encrypted = BuildRarSample(rarExe!, "enc4.rar", encrypted: true);
            string plain = BuildRarSample(rarExe!, "plain4.rar", encrypted: false);

            const string ChineseAmbiguous = "在加密文件 555.mp4 里校验和错误。文件已损坏或密码错误。";

            // ① 加密包 + 退出码 3 = 两义：不许判成"已损坏"（那会让候选循环当场停下），
            //    也不许判成"密码错误"（那会把真损坏说成密码错）。
            Assert.True(UnRarOutputParser.LooksLikePasswordOrCorrupted(3, encrypted));

            ArchiveOperationResult result = new UnRarProcessRunner().AnalyzeResult(
                3,
                ChineseAmbiguous,
                string.Empty,
                "wrong-password",
                TimeSpan.FromSeconds(1),
                encrypted,
                EngineOperation.Extract);

            Assert.Equal(EngineErrorTypes.PasswordOrCorrupted, result.DetectedErrorType);
            Assert.False(result.IsCorrupted, "两义那一档绝不许落成 CorruptedArchive（候选循环见它就停）");
            Assert.True(result.IsPasswordOrCorrupted);
            Assert.NotEqual(StatusText.Corrupted, result.Status);
            Assert.NotEqual(StatusText.WrongPassword, result.Status);

            // ② 结论必须**同时**保留两种可能，并把引擎原话带出来。
            Assert.Contains("密码", result.Message, StringComparison.Ordinal);
            Assert.Contains("数据", result.Message, StringComparison.Ordinal);
            Assert.Contains(ChineseAmbiguous, result.Message, StringComparison.Ordinal);

            // ③ 对照：**没加密**的包退出码 3 只剩"数据坏"一种解释，照旧判损坏、照旧不再换候选。
            Assert.False(UnRarOutputParser.LooksLikePasswordOrCorrupted(3, plain));

            ArchiveOperationResult plainResult = new UnRarProcessRunner().AnalyzeResult(
                3,
                "555.mp4 - checksum error",
                string.Empty,
                string.Empty,
                TimeSpan.FromSeconds(1),
                plain,
                EngineOperation.Extract);

            Assert.Equal(EngineErrorTypes.CorruptedArchive, plainResult.DetectedErrorType);
            Assert.True(plainResult.IsCorrupted);
        }

        /// <summary>
        /// **两义那一档不许提前终结候选循环**（真机：11 个候选只试了第 1 个）。
        ///
        /// <para>两条路都要验：<c>SingleLayer</c>（普通解压那条候选循环）与
        /// <c>SingleChain</c>（递归那条 —— 真机走的正是它，`RecursiveExtractor` 见到 IsCorrupted 就 Stop）。
        /// 断言只有一条硬的：**候选全部被试过**（引擎调用次数 = 候选数）。</para>
        /// </summary>
        [Theory]
        [InlineData("SingleLayer")]
        [InlineData("SingleChain")]
        public async Task 两义那一档不许提前终结候选循环_候选全部试完(string recursionMode)
        {
            Harness harness = CreateHarness(new[] { "候选-1", "候选-2", "候选-3" }, recursionMode);
            harness.Engine.OnExtractAsync = request =>
            {
                // 每一次都回"两义"那一档（真机现场：中文 UnRAR 对每个错候选都这么报）。
                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = false,
                    ExitCode = 3,
                    Status = StatusText.PasswordOrCorrupted,
                    Message = "密码可能不对，也可能这个包的数据坏了（在加密文件 555.mp4 里校验和错误。文件已损坏或密码错误。）。",
                    DetectedErrorType = EngineErrorTypes.PasswordOrCorrupted
                });
            };

            string source = CreateSourceFile("plain.7z");
            ArchiveTask task = AddTask(harness, source);
            task.CaptureSourceSnapshot();

            await harness.Coordinator.StartExtractAsync();

            // ① 三个候选一个都不许被跳过（红检点：撤回"继续试下一个"的处置即只剩 1 次调用）。
            Assert.Equal(3, harness.Engine.ExtractCalls.Count);

            // ② 结论同时保留两种可能 + 引擎原话。
            Assert.Equal(StatusText.PasswordOrCorrupted, task.Status);
            Assert.Equal(TaskOutcome.Failed, task.Outcome);
            Assert.True(task.ErrorMessage.Contains("密码", StringComparison.Ordinal), "实际原因：" + task.ErrorMessage);
            Assert.True(task.ErrorMessage.Contains("数据", StringComparison.Ordinal), "实际原因：" + task.ErrorMessage);

            // ③ 判定那一步的机器结论也要对得上（它是"删源 / 搬源 / 续解"四道门读的那一位）。
            Assert.False(TaskOutcomeClassifier.IsSuccessStatus(task));
        }

        // ================================================================ ③ 缺卷预检的机器终态

        /// <summary>
        /// 真机现场（日志 L71-L72 / L112 / L124 / L130）：缺卷预检把任务拦下了（判得对），
        /// 但机器终态留在 <c>Pending</c> —— 汇总说「未处理 1」、批末诊断说「分卷缺失：1 个 —— 222.z01」。
        ///
        /// <para>⚠ **口径变更（2026-10-08 用户拍板，本条按新口径改写）**：原断言是"终态必须是失败"，
        /// 用户新指令：「最后一轮检测到了缺失分卷，**这就是部分完成**，因为连完整的都没有，这就不是程序的错误」；
        /// 「如果是完整的…但是有错误，比如说密码、和压缩包卷尾缺失了一块，这就是失败的情况了」。
        /// ⇒ 判据是**完整性**：缺卷落「部分完成」，失败只留给"完整却解不开"。</para>
        ///
        /// <para><b>怎么判的</b>：`TaskOutcomeClassifier`（递归停因 `MissingVolume` ⇒ `PartiallyCompleted`）、
        /// `StatusToBrushConverter`（缺卷从错误色移到警告色）、`IsFailureStatus`/`IsExtractFailureStatus`
        /// （缺卷移出失败名单）、`OneClickCoordinator.IsHandled`（单列它，免得被读成"没轮到"）、
        /// `TaskSummaryService`（失败清单移出 + 与"部分完成"同桶）。冲突就在本条与它断言的四处。</para>
        /// </summary>
        [Fact]
        public async Task 缺卷预检拦下_机器终态必须是部分完成()
        {
            Harness harness = CreateHarness();

            string first = CreateSourceFile("vol.7z.001");
            ArchiveTask task = AddTask(harness, first);

            task.IsVolumeGroup = true;
            task.IsVolumeComplete = false;
            task.VolumePaths.Add(first);
            task.MissingVolumeNames.Add("vol.7z.002");
            task.VolumeInfoText = "2 卷，缺 vol.7z.002";

            await harness.Coordinator.StartExtractAsync();

            // ① 状态与原因一个字没改（⛔ 不许靠改中文修这个缺陷）。
            Assert.Equal(StatusText.VolumeMissing, task.Status);
            Assert.Contains("vol.7z.002", task.ErrorMessage);

            // ② 机器终态 = **部分完成**（用户 2026-10-08 口径），而且"这一单已经结束"（EndTime）也要落。
            Assert.Equal(TaskOutcome.PartiallyCompleted, task.Outcome);
            Assert.True(task.EndTime.HasValue, "开工前拦下的任务同样要落结束时间（否则终态收口补不上）");

            // ③ 缺卷**仍在"要处理的清单"里**（不变量 7：必须报"缺哪几个"），但"处理过"这件事也要成立
            //    （否则汇总会说"一键处理已停止/未处理 N"）。⚠ 2026-10-08：用户口径把它的**分桶与配色**
            //    挪成"部分完成"，⛔ 没有把它从清单里拿掉。
            Assert.True(TaskOutcomeClassifier.IsFailureStatus(task.Status));
            Assert.True(OneClickCoordinator.IsHandled(task));

            BatchSummaryReport report = BatchSummaryDiagnosticsRules.Build(new[] { task });

            BatchProblemGroup group = Assert.Single(report.Groups);
            Assert.Equal(BatchProblemKind.MissingVolume, group.Kind);

            // ④ 引擎一次都没被调用（不变量 7 一个字没放松）。
            Assert.Empty(harness.Engine.ExtractCalls);
        }

        // ================================================================ 新增状态：三处同改（AGENTS.md §7）

        /// <summary>
        /// 两义那一档是**新状态**：按 §7 三处（文案 + 配色 + 统计/失败清单）一次接通，
        /// 并与批末诊断的归组、引擎解析层的映射对齐 —— 少接一处这条就红。
        /// </summary>
        [Fact]
        public void 新增状态_密码错误或文件损坏_三处都已接通()
        {
            // ① 文案：两种可能都在，⛔ 既不是"密码错误"也不是"文件损坏"。
            Assert.Equal("密码错误或文件损坏", StatusText.PasswordOrCorrupted);
            Assert.NotEqual(StatusText.Corrupted, StatusText.PasswordOrCorrupted);
            Assert.NotEqual(StatusText.WrongPassword, StatusText.PasswordOrCorrupted);

            // ② 配色：这一单没拿到产物 ⇒ 失败色（与它的统计分桶"解压失败"一致）。
            var converter = new Converters.StatusToBrushConverter();

            Assert.Same(
                converter.ErrorBrush,
                converter.Convert(StatusText.PasswordOrCorrupted, typeof(object), null!, null!));
            Assert.NotSame(
                converter.SuccessBrush,
                converter.Convert(StatusText.PasswordOrCorrupted, typeof(object), null!, null!));

            // ③ 统计：算"解压失败"，**不算**"密码错误"、也不算"文件损坏"（统计层面同样不许单独定原因）。
            var service = new TaskSummaryService();
            var task = new ArchiveTask(@"C:\t\amb.rar")
            {
                Status = StatusText.PasswordOrCorrupted,
                Outcome = TaskOutcome.Failed
            };

            TaskSummary summary = service.BuildSummary(new[] { task });

            Assert.Equal(1, summary.ExtractFailedCount);
            Assert.Equal(0, summary.PasswordErrorCount);
            Assert.Equal(0, summary.CorruptedCount);
            Assert.True(service.IsFailedStatus(StatusText.PasswordOrCorrupted));
            Assert.Contains(StatusText.PasswordOrCorrupted, service.BuildFailedListText(new[] { task }));

            // ④ 批末诊断：归"密码"那一组（它的注脚本来就写着"可能"，不会断言"就是没有密码"）。
            Assert.Equal(BatchProblemKind.Password, BatchSummaryDiagnosticsRules.Classify(task));

            // ⑤ 引擎解析层的映射（唯一出口）。
            Assert.Equal(
                StatusText.PasswordOrCorrupted,
                UnRarOutputParser.ErrorTypeToTaskStatus(EngineErrorTypes.PasswordOrCorrupted));
        }

        // ================================================================ 装配

        private string CreateSourceFile(string fileName)
        {
            string directory = Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, fileName);
            File.WriteAllText(path, "fake content - the engine is faked in these tests");
            return path;
        }

        /// <summary>造一个 RAR4 样本（<paramref name="encrypted"/> 时带 <c>-p&lt;密码&gt;</c>，数据加密）。</summary>
        private string BuildRarSample(string rarExe, string fileName, bool encrypted)
        {
            string directory = Path.Combine(_root, "rar-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            // 不可压缩载荷：保证是"存起来"的条目（RAR4 + `-p` 用错密码时才会走校验和那条路）。
            var payload = new byte[64 * 1024];
            new Random(20260930).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(directory, "555.mp4"), payload);

            string archive = Path.Combine(directory, fileName);

            var args = new List<string> { "a", "-ma4", "-m0", "-ep1", "-idq" };

            if (encrypted)
            {
                args.Add("-p<sample-password>");
            }

            args.Add(archive);
            args.Add(Path.Combine(directory, "555.mp4"));

            RarSampleSet.RunRar(rarExe, directory, _ => { }, args.ToArray());

            Assert.True(File.Exists(archive), "RAR 样本没造出来：" + archive);
            return archive;
        }

        private Harness CreateHarness(IReadOnlyList<string>? passwords = null, string recursionMode = "SingleLayer")
        {
            string dataRoot = Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N"));
            string outputRoot = Path.Combine(_root, "out-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = recursionMode;
            settings.AutoScanAfterDrop = false;
            settings.MaxParallelExtractCount = 1;
            settings.TestBeforeExtract = false;
            settings.TryEmptyPasswordFirst = false;
            settings.MaxPasswordAttemptsPerLayer = 10;

            // 源包档位固定"留在原地"：这三处缺陷都不该顺带测源包搬运 / 删除。
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;

            settingsService.Save(settings);

            var engine = new FakeEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            int index = 0;

            foreach (string password in passwords ?? Array.Empty<string>())
            {
                index++;

                passwordService.Passwords.Add(new PasswordItem
                {
                    Value = password,
                    Source = "ImportedList",
                    IsEnabled = true,
                    Remark = $"真机缺陷用例候选 {index}"
                });
            }

            // MainViewModel 的构造会顺手写两个进程级静态（7z 路径、递归工作区根目录）：先存后还原。
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

            var coordinator = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService())
            {
                // 这些用例拿"细节日志"当行为证据（默认档成功时只留两行）。
                KeepTaskDetailInLog = true
            };

            return new Harness(vm, engine, coordinator, logService);
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
            return task;
        }

        private sealed class Harness
        {
            public Harness(MainViewModel vm, FakeEngine engine, ExtractionCoordinator coordinator, LogService log)
            {
                Vm = vm;
                Engine = engine;
                Coordinator = coordinator;
                Log = log;
            }

            public MainViewModel Vm { get; }

            public FakeEngine Engine { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);
        }

        /// <summary>可控的假引擎（与 <c>PipelineWiringTests</c> / <c>WorkspaceLeftoverShellTests</c> 同一套做法）。</summary>
        private sealed class FakeEngine : IArchiveEngine
        {
            public List<ArchiveRequest> ExtractCalls { get; } = new();

            public Func<ArchiveRequest, Task<ArchiveOperationResult>>? OnExtractAsync { get; set; }

            public Func<ArchiveRequest, Task<ArchiveListResult>>? OnListAsync { get; set; }

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

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                OnListAsync != null
                    ? OnListAsync(request)
                    : Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(Failure("WrongPassword", StatusText.WrongPassword, "密码错误"));

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                ExtractCalls.Add(request);

                return OnExtractAsync != null
                    ? OnExtractAsync(request)
                    : Task.FromResult(Failure("UnknownError", StatusText.ExtractFailed, "假引擎：这次没有配置结果"));
            }

            private static ArchiveOperationResult Failure(string errorType, string status, string message) => new()
            {
                Success = false,
                Status = status,
                Message = message,
                DetectedErrorType = errorType
            };
        }
    }
}

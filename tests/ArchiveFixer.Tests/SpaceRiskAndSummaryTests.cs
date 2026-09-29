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
using ArchiveFixer.Views;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 用户 2026-09-29 的第 2 条与第 3 条：
    /// **多层 + 空间小不许"中途才报"**，以及**批末那个汇总框要按结果分颜色**。
    ///
    /// <para>第 2 条落成两件事：① 动手前就把"多层解压可能中途空间不足"写进批首日志与那个唯一确认框；
    /// ② 中途真撞上时允许弹**一次**纯提示（非模态、一个按钮、不阻塞后续任务；手动档照旧只写日志）。</para>
    ///
    /// <para>第 3 条落成一个判据出口（<see cref="BatchSummarySeverityRules"/>）+ 一条顶部色带：
    /// 蓝 = 全成功 / 橙 = 有部分完成或跳过 / 红 = 有失败，正文一律白底黑字。</para>
    /// </summary>
    public class SpaceRiskAndSummaryTests : IDisposable
    {
        private readonly string _root;

        public SpaceRiskAndSummaryTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSpaceRisk", Guid.NewGuid().ToString("N"));
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
            finally
            {
                // 进程级静态的降级日志按用例清干净，免得别的用例数到这一条。
                DialogService.ClearFallbackLog();
            }
        }

        // ================================================================ 第 3 条：三档严重度

        [Fact]
        public void 汇总严重度_全部成功是蓝()
        {
            Assert.Equal(
                BatchSummarySeverity.Success,
                BatchSummarySeverityRules.FromOutcomes(new[] { TaskOutcome.Succeeded, TaskOutcome.Succeeded }));
        }

        [Fact]
        public void 汇总严重度_有部分完成是橙()
        {
            Assert.Equal(
                BatchSummarySeverity.Partial,
                BatchSummarySeverityRules.FromOutcomes(new[] { TaskOutcome.Succeeded, TaskOutcome.PartiallyCompleted }));
        }

        [Fact]
        public void 汇总严重度_有失败是红_哪怕别的都成功()
        {
            Assert.Equal(
                BatchSummarySeverity.Failed,
                BatchSummarySeverityRules.FromOutcomes(
                    new[] { TaskOutcome.Succeeded, TaskOutcome.Succeeded, TaskOutcome.Failed }));
        }

        /// <summary>
        /// <b>边界（刻意定成橙）</b>：整批只有"跳过"。
        ///
        /// <para>跳过通常是他自己在同名冲突框里选的，不是失败；但**这一批确实有东西没做成** ——
        /// 报成蓝色会让他以为"全好了"，然后去输出目录里找一个根本不存在的产物。
        /// 用户把"跳过"与"部分完成"并列归进"非致命问题"那一档，这里照他的口径办。</para>
        /// </summary>
        [Fact]
        public void 汇总严重度_只有跳过算橙不算蓝()
        {
            Assert.Equal(
                BatchSummarySeverity.Partial,
                BatchSummarySeverityRules.FromOutcomes(new[] { TaskOutcome.Skipped, TaskOutcome.Skipped }));
        }

        [Fact]
        public void 汇总严重度_取消与没轮到也算橙()
        {
            Assert.Equal(
                BatchSummarySeverity.Partial,
                BatchSummarySeverityRules.FromOutcomes(new[] { TaskOutcome.Cancelled, TaskOutcome.Pending }));
        }

        [Fact]
        public void 汇总严重度_一个任务都没有算蓝()
        {
            // "什么都没发生"用蓝色最诚实（没有失败、也没有没做成的事）。
            Assert.Equal(BatchSummarySeverity.Success, BatchSummarySeverityRules.FromOutcomes(Array.Empty<TaskOutcome>()));
            Assert.Equal(BatchSummarySeverity.Success, BatchSummarySeverityRules.FromTasks(null));
        }

        [Fact]
        public void 汇总严重度_校验判否即使终态写着成功也算红()
        {
            var task = new ArchiveTask
            {
                Outcome = TaskOutcome.Succeeded,
                OutputVerification = OutputVerificationOutcome.Failed
            };

            Assert.Equal(BatchSummarySeverity.Failed, BatchSummarySeverityRules.FromTasks(new[] { task }));
        }

        /// <summary>
        /// 配色：三档的**色带**必须各不相同，而**正文一律白底黑字**
        /// （用户原话："上30%的位置是蓝色，然后下面是白底，黑字，我什么时候说过白字的"）。
        /// </summary>
        [Fact]
        public void 汇总配色_三档色带各不相同_而正文一律白底黑字()
        {
            string success = AppDialogWindow.ResolveSummaryBannerBrushKey(BatchSummarySeverity.Success);
            string partial = AppDialogWindow.ResolveSummaryBannerBrushKey(BatchSummarySeverity.Partial);
            string failed = AppDialogWindow.ResolveSummaryBannerBrushKey(BatchSummarySeverity.Failed);

            Assert.Equal(3, new[] { success, partial, failed }.Distinct(StringComparer.Ordinal).Count());

            // 兜底色（App.xaml 的资源取不到时用）三档也得能分开，而且是"蓝 / 橙 / 红"。
            var successColor = AppDialogWindow.ResolveSummaryBannerFallbackColor(BatchSummarySeverity.Success);
            var partialColor = AppDialogWindow.ResolveSummaryBannerFallbackColor(BatchSummarySeverity.Partial);
            var failedColor = AppDialogWindow.ResolveSummaryBannerFallbackColor(BatchSummarySeverity.Failed);

            Assert.True(successColor.B > successColor.R && successColor.B > successColor.G, "全成功那一档的色带必须是蓝的");
            Assert.True(partialColor.R > partialColor.B && partialColor.G > partialColor.B, "有没做成的必须是橙的");
            Assert.True(failedColor.R > failedColor.G && failedColor.R > failedColor.B, "有失败的必须是红的");

            // 正文：白底 + 黑字，三档一样（判据只有一处，窗口不按档位改正文颜色）。
            Assert.Equal("PanelBrush", AppDialogWindow.SummaryBodyBackgroundBrushKey);
            Assert.Equal("TextPrimaryBrush", AppDialogWindow.SummaryBodyForegroundBrushKey);
            Assert.Equal("TextOnAccentBrush", AppDialogWindow.SummaryBannerForegroundBrushKey);
        }

        /// <summary>
        /// 接线：一键处理跑完之后，那个汇总框必须**带着严重度**弹出来 —— 全成功那一批是蓝的。
        /// </summary>
        [Fact]
        public async Task 一键处理汇总框_全成功那一批是蓝的()
        {
            Harness harness = CreateHarness();

            string source = harness.CreateSource("pack-0.7z", 512);
            await harness.AddTasksAsync(source);

            await harness.OneClick.RunAsync();

            (string Message, BatchSummarySeverity Severity) summary = Assert.Single(harness.Dialog.BatchSummaries);

            Assert.Equal(BatchSummarySeverity.Success, summary.Severity);
            Assert.Contains("一键处理完成", summary.Message, StringComparison.Ordinal);
        }

        /// <summary>反向：有失败的那一批必须是红的（⛔ 判据只读机器终态，不读那段中文汇总）。</summary>
        [Fact]
        public async Task 一键处理汇总框_有失败那一批是红的()
        {
            Harness harness = CreateHarness();

            string source = harness.CreateSource("pack-0.7z", 512);
            await harness.AddTasksAsync(source);

            harness.Engine.ExtractFailure = new ArchiveOperationResult
            {
                Success = false,
                Status = StatusText.Corrupted,
                Message = "文件损坏",
                DetectedErrorType = "Corrupted"
            };

            await harness.OneClick.RunAsync();

            (string Message, BatchSummarySeverity Severity) summary = Assert.Single(harness.Dialog.BatchSummaries);

            Assert.Equal(BatchSummarySeverity.Failed, summary.Severity);
        }

        // ================================================================ 第 2 条：判据（纯函数）

        [Fact]
        public void 多层空间风险_多层且各包加起来的量超过可用空间才报()
        {
            MultiLayerSpaceRisk risk = MultiLayerSpaceRiskRules.Evaluate(
                multiLayerPossible: true,
                maxLayers: 5,
                totalDemandBytes: 4000,
                availableBytes: 3000);

            Assert.True(risk.Applies);
            Assert.Equal(1000, risk.ShortfallBytes);
            Assert.False(risk.Undetermined);
        }

        [Fact]
        public void 多层空间风险_装得下就不报()
        {
            Assert.False(MultiLayerSpaceRiskRules.Evaluate(true, 5, 3000, 3000).Applies);
            Assert.False(MultiLayerSpaceRiskRules.Evaluate(true, 5, 2000, 3000).Applies);
        }

        [Fact]
        public void 多层空间风险_只解一层时不算这个风险()
        {
            // 单层不会"越解越紧"（没有内层包再展开那一份），这一条提醒说的是多层。
            Assert.False(MultiLayerSpaceRiskRules.Evaluate(false, 1, 9000, 100).Applies);
        }

        [Fact]
        public void 多层空间风险_取不到可用空间时不报_也不谎报()
        {
            MultiLayerSpaceRisk risk = MultiLayerSpaceRiskRules.Evaluate(true, 5, 9000, SpaceReservationLedger.UnknownAvailable);

            Assert.False(risk.Applies);
            Assert.True(risk.Undetermined);
        }

        [Fact]
        public void 多层空间风险_求和是饱和加法_不会因为溢出变成负数()
        {
            Assert.Equal(long.MaxValue, MultiLayerSpaceRiskRules.SumDemand(new[] { long.MaxValue, 1024 }));
        }

        // ================================================================ 第 2 条：批首看得见 + 中途弹一次

        /// <summary>
        /// 批首那条 WARN：**多层 + 各包加起来超过可用空间** ⇒ 日志里必须先说一句，
        /// 而且要告诉他去哪儿开那个模式（用户原话："好是可以提醒用户打开空间不足"）。
        /// </summary>
        [Fact]
        public async Task 批首日志_多层且总量超可用_必须写明并指路空间不足模式()
        {
            Harness harness = CreateHarness();

            string[] sources = Enumerable.Range(0, 3)
                .Select(i => harness.CreateSource($"pack-{i}.7z", 2000))
                .ToArray();

            await harness.AddTasksAsync(sources);

            await harness.OneClick.RunPipelineAsync(harness.Vm.Tasks.ToList());

            string? line = harness.LogTexts.FirstOrDefault(
                text => text.Contains("多层解压可能中途空间不足", StringComparison.Ordinal));

            Assert.NotNull(line);
            Assert.Contains("多层解压可能中途空间不足", line, StringComparison.Ordinal);
            Assert.Contains("「空间不足」模式", line, StringComparison.Ordinal);
        }

        /// <summary>
        /// 同一句话还必须出现在**那个唯一确认框**里（用户："在批首的日志与确认框里写明"）——
        /// 而且两处用的是同一段文案（判据与措辞都只有一处出口）。
        /// </summary>
        [Fact]
        public async Task 确认框_同一句话也必须出现()
        {
            Harness harness = CreateHarness();

            string[] sources = Enumerable.Range(0, 3)
                .Select(i => harness.CreateSource($"pack-{i}.7z", 2000))
                .ToArray();

            await harness.AddTasksAsync(sources);

            OneClickConfirmFacts facts = await harness.Coordinator.BuildConfirmFactsAsync(
                harness.Vm.Tasks.ToList(),
                pendingOptions: null,
                reminders: null,
                CancellationToken.None);

            Assert.Contains("多层解压可能中途空间不足", facts.NoticeEcho, StringComparison.Ordinal);
            Assert.Contains("「空间不足」模式", facts.NoticeEcho, StringComparison.Ordinal);
        }

        /// <summary>装得下时一个字都不许写（狼来了只会让人学会忽略提醒）。</summary>
        [Fact]
        public async Task 批首日志_装得下时一个字都不写()
        {
            Harness harness = CreateHarness(availableBytes: 100 * 1024 * 1024);

            await harness.AddTasksAsync(harness.CreateSource("pack-0.7z", 512));

            await harness.OneClick.RunPipelineAsync(harness.Vm.Tasks.ToList());

            Assert.DoesNotContain(
                harness.LogTexts,
                text => text.Contains("多层解压可能中途空间不足", StringComparison.Ordinal));
        }

        /// <summary>
        /// 中途真撞上空间不足：一键档弹**一次**纯提示（非模态、一个按钮、不阻塞后续任务），
        /// 后面同样被拦下的任务**不再弹**（用户："同一批只弹一次（合并计数）"）。
        ///
        /// <para>判据读的是 <see cref="DialogService.FallbackLog"/>：测试宿主没有 WPF 界面，
        /// 通知型对话框在那里降级成一条记录 —— 弹了几次、弹的是哪一类框都看得见
        /// （与既有 <c>SpaceTightModeTests</c> 同一套做法）。</para>
        /// </summary>
        [Fact]
        public async Task 中途空间不足_一键档只弹一次纯提示()
        {
            DialogService.ClearFallbackLog();

            Harness harness = CreateHarness();

            string[] sources = Enumerable.Range(0, 3)
                .Select(i => harness.CreateSource($"pack-{i}.7z", 2000))
                .ToArray();

            await harness.AddTasksAsync(sources);

            await harness.OneClick.RunPipelineAsync(harness.Vm.Tasks.ToList());

            // 三个包、可用空间只够一个 → 后两个被空间门拦下。
            Assert.Contains(
                harness.Vm.Tasks,
                task => task.Status == StatusText.DiskSpaceInsufficient);

            int notices = CountFallbackEntries("ShowSpaceBlockedNotice");

            Assert.Equal(1, notices);

            string notice = Assert.Single(
                DialogService.FallbackLog,
                entry => entry.Contains("[ShowSpaceBlockedNotice]", StringComparison.Ordinal));

            // 措辞要点明"可以从哪些省空间做法里选"（用户点名的那三条）。
            Assert.Contains("清理「其余物」", notice, StringComparison.Ordinal);
            Assert.Contains("输出盘", notice, StringComparison.Ordinal);
            Assert.Contains("「空间不足」模式", notice, StringComparison.Ordinal);
        }

        /// <summary>
        /// 手动档**照旧只写日志**（用户原话："手动档照旧"）：他坐在屏幕前，任务状态当场变红，
        /// 不需要再飘一个非模态窗口。
        /// </summary>
        [Fact]
        public async Task 中途空间不足_手动档照旧只写日志()
        {
            DialogService.ClearFallbackLog();

            Harness harness = CreateHarness();

            string[] sources = Enumerable.Range(0, 3)
                .Select(i => harness.CreateSource($"pack-{i}.7z", 2000))
                .ToArray();

            await harness.AddTasksAsync(sources);

            await harness.Coordinator.StartExtractAsync();

            Assert.Contains(
                harness.Vm.Tasks,
                task => task.Status == StatusText.DiskSpaceInsufficient);

            Assert.Equal(0, CountFallbackEntries("ShowSpaceBlockedNotice"));

            // 日志那一条照旧（"不许静默跳过"）。
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("空间不足，未解压", StringComparison.Ordinal));
        }

        private static int CountFallbackEntries(string context) =>
            DialogService.FallbackLog.Count(entry => entry.Contains("[" + context + "]", StringComparison.Ordinal));

        // ================================================================ 宿主

        /// <summary>
        /// 假引擎 + 假盘（可用空间由用例给定）。用假引擎是有意的：
        /// 这一组测的是**判据与提示**，不是 7z 的能力 —— 真 7z 那一组在 <c>ChainSpaceReclaimTests</c>。
        /// </summary>
        private Harness CreateHarness(long availableBytes = 3000)
        {
            string dataRoot = Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N"));
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
            settings.RemindBeforeExtract = false;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;

            /*
             * 并发 ≥ 2 是有意的：**逐任务的空间门拦的是"同时跑的几个"**（账本里记着在跑任务的预留）。
             * 并发 1 时任务是排队跑的，前一个收尾会把预留还回去 —— 于是每个包单独看都放得下、
             * 一个都不会被拦（这条也正是"空间够不够"与"能不能同时跑"两件事的分界）。
             */
            settings.MaxParallelExtractCount = 4;
            settingsService.Save(settings);

            var engine = new FakeEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);
            var dialog = new RecordingDialogService();

            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;

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
                dialog);

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;

            var coordinator = new ExtractionCoordinator(vm, engine, passwordService, pathService, dialog)
            {
                // 假盘：可用空间由用例给定，余量取 0（这一组要算的是"需求之和 vs 可用"）。
                SpaceProbeOverride = _ => availableBytes,
                SpaceReserveOverride = 0
            };

            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), dialog);
            var rename = new RenameCoordinator(vm, scan, new RenameService(), dialog);
            var oneClick = new OneClickCoordinator(vm, scan, rename, coordinator, dialog)
            {
                OptionsPromptOverride = _ => OneClickOptionsPrompt.NotShown()
            };

            return new Harness(vm, engine, coordinator, oneClick, dialog, logService, outputRoot, _root);
        }

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                FakeEngine engine,
                ExtractionCoordinator coordinator,
                OneClickCoordinator oneClick,
                RecordingDialogService dialog,
                LogService log,
                string outputRoot,
                string root)
            {
                Vm = vm;
                Engine = engine;
                Coordinator = coordinator;
                OneClick = oneClick;
                Dialog = dialog;
                Log = log;
                OutputRoot = outputRoot;
                Root = root;
            }

            public MainViewModel Vm { get; }

            public FakeEngine Engine { get; }

            public ExtractionCoordinator Coordinator { get; }

            public OneClickCoordinator OneClick { get; }

            public RecordingDialogService Dialog { get; }

            public LogService Log { get; }

            public string OutputRoot { get; }

            public string Root { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(entry => entry.DisplayText);

            /// <summary>造一个指定字节数的源文件（空间需求就按它算：内容物按源包 1.0 倍估）。</summary>
            public string CreateSource(string fileName, int size)
            {
                string directory = Path.Combine(Root, "src");
                Directory.CreateDirectory(directory);

                string path = Path.Combine(directory, fileName);
                File.WriteAllBytes(path, new byte[size]);

                return path;
            }

            /// <summary>
            /// 一次把这一批的源文件全加进列表。
            ///
            /// <para>⚠ **必须一次加完**：<c>Vm.AddPathsAsync</c> 是"替换语义"（先清空整张表再添加，
            /// 用户 2026-09-24 第 12 条），分几次调只会剩下最后一个任务 —— 那样"三个包抢一份空间"
            /// 这个场景根本搭不起来（实测踩到过）。</para>
            /// </summary>
            public Task AddTasksAsync(params string[] paths) => Vm.AddPathsAsync(paths);
        }

        /// <summary>记录"批末汇总框弹了什么"的假对话框（其余走真实实现 → 无界面宿主时降级成日志）。</summary>
        private sealed class RecordingDialogService : DialogService
        {
            private readonly object _lock = new();

            public List<(string Message, BatchSummarySeverity Severity)> BatchSummaries { get; } = new();

            public override void ShowBatchSummary(string message, BatchSummarySeverity severity)
            {
                lock (_lock)
                {
                    BatchSummaries.Add((message, severity));
                }
            }
        }

        /// <summary>假引擎：列目录给一份清单，解压写出几个文件（与 SpaceTightModeTests 同一形态）。</summary>
        private sealed class FakeEngine : IArchiveEngine
        {
            private List<(string Name, int Size)> _products = new() { ("payload.bin", 8) };

            public ArchiveOperationResult? ExtractFailure { get; set; }

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
                => Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    FileCount = _products.Count,
                    TotalUncompressedSize = _products.Sum(p => (long)p.Size),
                    Entries = _products
                        .Select(p => new ArchiveEntry { Path = p.Name, Size = p.Size })
                        .ToList(),
                    EngineId = "fake",
                    EngineVersion = "1.0"
                });

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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 用户 2026-09-24 第 12 条的三件事（假引擎 + 真管线，跑的是界面背后那条真实链路）：
    ///
    /// <list type="number">
    /// <item><description><b>一律只认勾选</b>：一个都没勾 → 引擎**一次都不许被调用**，只给提示；
    /// 勾了两个 → 只有那两个被执行（没勾的那个连一次引擎调用都收不到）。</description></item>
    /// <item><description><b>点「添加」就清空重来</b>：「添加文件 / 添加文件夹」= 替换整张表；
    /// 显式入口「追加到列表」才保留原有任务。</description></item>
    /// <item><description><b>卡死</b>：批量添加 N 项**只触发一次全量统计重算**、集合**只通知一次** ——
    /// 这条是照着只读诊断的结论钉的（几百次逐项 Add / 逐项重算就是界面被喂满的原因）。</description></item>
    /// </list>
    ///
    /// <para><b>口径来源</b>：用户原话"我即使没有特地的没有去选中，你为什么还要去操作，
    /// <b>你只需要操作我选中的文件，其他的不用管</b>"、"每次我新选择了其他的，无论是什么，
    /// 你都要将列表彻底清空"。⛔ 那条"没勾就退回当前点中的那一行"的兜底已经删除，
    /// 这些用例就是防止它被悄悄加回来。</para>
    ///
    /// <para><b>装配纪律</b>：构造 <c>MainViewModel</c> 会写两个进程级静态（7z 路径、递归工作区根目录），
    /// 装配后立刻还原（与其它管线测试同一套），并且与它们一样不与其他集合并行。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public sealed class CheckedOnlySelectionTests : IDisposable
    {
        private readonly string _root;

        public CheckedOnlySelectionTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerCheckedOnly", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 一个都没勾 = 零个任务被执行

        [Fact]
        public async Task 一个都没勾时点一键处理_引擎一次都没被调用且只给提示()
        {
            Harness harness = CreateHarness("zero-checked");

            ArchiveTask first = harness.AddTask("111.7z", isSelected: false);
            ArchiveTask second = harness.AddTask("222.7z", isSelected: false);

            // 当前行**故意**停在第一个上：旧口径会拿它开刀，新口径必须一个都不动。
            harness.Vm.SelectedTask = first;

            DialogService.ClearFallbackLog();

            harness.Vm.OneClickProcessCommand.Execute(null);

            await WaitUntilAsync(
                () => DialogService.FallbackLog.Count > 0,
                TimeSpan.FromSeconds(10));

            // ① 提示在（来自唯一文案来源 StatusText.NoCheckedTaskPromptFormat）。
            string prompt = DialogService.FallbackLog.Last();

            Assert.Contains("没有勾选任何任务", prompt, StringComparison.Ordinal);
            Assert.Contains("一键处理", prompt, StringComparison.Ordinal);
            Assert.Contains("只处理你勾选的任务", prompt, StringComparison.Ordinal);

            // ② **零个任务被执行**：引擎的四个动作一次都没被叫到。
            Assert.Empty(harness.Engine.Calls);

            // ③ 列表与任务状态一个字节都没动。
            Assert.Equal(2, harness.Vm.Tasks.Count);
            Assert.All(harness.Vm.Tasks, task => Assert.False(task.IsSelected));

            // ④ 也没有被标成"处理中/成功"这类假状态。
            Assert.Equal(StatusText.Recognized, first.Status);
            Assert.Equal(StatusText.Recognized, second.Status);
        }

        // ================================================================ ② 勾了两个 = 只执行这两个

        [Fact]
        public async Task 勾了两个时_只有这两个被执行_没勾的那一个引擎一次都没收到()
        {
            Harness harness = CreateHarness("two-checked");

            ArchiveTask checkedFirst = harness.AddTask("111.7z", isSelected: true);
            ArchiveTask checkedSecond = harness.AddTask("222.7z", isSelected: true);
            ArchiveTask notChecked = harness.AddTask("333.7z", isSelected: false);

            // 当前行停在没勾的那个上 —— 它绝不该出现在任何一次引擎调用里。
            harness.Vm.SelectedTask = notChecked;

            harness.Engine.OnListAsync = request => Task.FromResult(
                ListResult(Path.GetFileName(request.ArchivePath) + ".txt", 8));

            harness.Engine.OnExtractAsync = request =>
            {
                Directory.CreateDirectory(request.OutputPath!);
                File.WriteAllText(
                    Path.Combine(request.OutputPath!, Path.GetFileName(request.ArchivePath) + ".txt"),
                    "内容",
                    new UTF8Encoding(false));

                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = true,
                    Status = StatusText.ExtractSuccess,
                    Message = "解压成功",
                    DetectedErrorType = "None"
                });
            };

            await harness.Coordinator.StartExtractAsync();

            await WaitUntilAsync(() => !harness.Vm.IsBusy, TimeSpan.FromSeconds(30));

            // ① 引擎只被问过勾选的那两个（所有动作合起来看）。
            List<string> touched = harness.Engine.TouchedArchivePaths
                .Select(SafeFileName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Assert.Equal(new[] { "111.7z", "222.7z" }, touched);

            // ② 没勾的那个一次都没被碰过（单独再断言一遍，失败信息更直白）。
            Assert.DoesNotContain(
                harness.Engine.TouchedArchivePaths,
                path => path.EndsWith("333.7z", StringComparison.OrdinalIgnoreCase));

            // ③ 真产物只有两份（内容物落盘，说明"被执行"不是嘴说的）。
            Assert.True(Directory.Exists(checkedFirst.OutputPath), "勾选的任务应当有输出目录");
            Assert.True(Directory.Exists(checkedSecond.OutputPath), "勾选的任务应当有输出目录");
            Assert.False(Directory.Exists(notChecked.OutputPath), "没勾的任务绝不该产生输出目录");
        }

        // ================================================================ ③ 添加 = 替换整张表

        [Fact]
        public async Task 添加文件夹_替换整张表_列表条数等于新目录条数()
        {
            Harness harness = CreateHarness("replace");

            // 先手动灌一批"上一次选的"任务进去（模拟用户上一轮导入的结果）。
            harness.AddTask("旧A.7z", isSelected: true);
            harness.AddTask("旧B.7z", isSelected: true);

            string folder = Path.Combine(_root, "replace", "新目录");
            Directory.CreateDirectory(folder);

            for (int i = 1; i <= 3; i++)
            {
                File.WriteAllText(Path.Combine(folder, $"新{i}.7z"), "x", new UTF8Encoding(false));
            }

            await harness.Vm.AddPathsAsync(new[] { folder });

            // 替换语义：条数 == 新目录里的条数，一个旧任务都不许留下。
            Assert.Equal(3, harness.Vm.Tasks.Count);
            Assert.DoesNotContain(
                harness.Vm.Tasks,
                task => task.FileName.StartsWith("旧", StringComparison.Ordinal));
            Assert.All(
                harness.Vm.Tasks,
                task => Assert.StartsWith("新", task.FileName, StringComparison.Ordinal));

            // 序号重排过（1..N，界面第一列会显示它）。
            Assert.Equal(new[] { 1, 2, 3 }, harness.Vm.Tasks.Select(task => task.Index).ToArray());

            // 「当前行」不能还指着已经被替换掉的对象。
            Assert.Null(harness.Vm.SelectedTask);
        }

        // ================================================================ ④ 追加到列表 = 保留原有任务

        [Fact]
        public async Task 追加到列表_保留原有任务且新任务排在末尾()
        {
            Harness harness = CreateHarness("append");

            ArchiveTask kept = harness.AddTask("原有A.7z", isSelected: true);

            string folder = Path.Combine(_root, "append", "追加目录");
            Directory.CreateDirectory(folder);

            for (int i = 1; i <= 2; i++)
            {
                File.WriteAllText(Path.Combine(folder, $"追加{i}.7z"), "x", new UTF8Encoding(false));
            }

            await harness.Vm.AddPathsAsync(new[] { folder }, ImportMode.Append);

            Assert.Equal(3, harness.Vm.Tasks.Count);

            // 原有的那个必须还在，而且仍在最前面（追加不重排已有任务的相对顺序）。
            Assert.Same(kept, harness.Vm.Tasks[0]);
            Assert.Equal("原有A.7z", harness.Vm.Tasks[0].FileName);
            Assert.Equal(2, harness.Vm.Tasks.Count(task => task.FileName.StartsWith("追加", StringComparison.Ordinal)));

            // 勾选状态不受影响（追加不是"重新选择"，不许动用户已经勾好的东西）。
            Assert.True(kept.IsSelected);

            // 序号整体重排成 1..N（新任务带的是它自己那一批的号）。
            Assert.Equal(new[] { 1, 2, 3 }, harness.Vm.Tasks.Select(task => task.Index).ToArray());
        }

        // ================================================================ ⑤ 卡死诊断的钉子

        [Fact]
        public async Task 批量添加N项_只触发一次全量统计重算且集合只通知一次()
        {
            /*
             * 只读诊断（2026-09-24）的结论里，与"导入几百项"直接相关的一条是：
             * 逐项 Tasks.Add + 逐项全表重算会把界面线程喂满。
             * 这条用例把那句话变成数字：
             *   · Summary 属性只被刷 1 次（一次导入 = 一次全量统计重算）；
             *   · Tasks 的集合变更通知与 N 无关（批量替换 = 一次 Reset）。
             * 旧写法（逐项 Add + 逐项刷）会让这两个数字随 N 涨 —— 那样这条用例就红。
             */
            Harness harness = CreateHarness("bulk");

            const int count = 120;

            string folder = Path.Combine(_root, "bulk", "批量目录");
            Directory.CreateDirectory(folder);

            for (int i = 0; i < count; i++)
            {
                File.WriteAllText(Path.Combine(folder, $"{i:D3}.7z"), "x", new UTF8Encoding(false));
            }

            // AutoScanAfterDrop 会额外做一整轮识别（那本身是另一件事），这里只量"导入"这一段。
            harness.Vm.Settings.AutoScanAfterDrop = false;

            int summaryRefreshes = 0;

            harness.Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.Summary))
                {
                    summaryRefreshes++;
                }
            };

            int collectionNotifications = 0;

            harness.Vm.Tasks.CollectionChanged += (_, _) => collectionNotifications++;

            await harness.Vm.AddPathsAsync(new[] { folder });

            Assert.Equal(count, harness.Vm.Tasks.Count);

            // ① 全量统计重算：**恰好 1 次**（不是 N 次，也不是 2 次）。
            Assert.Equal(1, summaryRefreshes);

            // ② 集合通知与 N 无关：替换 = 清空 + 一次批量填入（上限 2 次），绝不随 N 增长。
            Assert.True(
                collectionNotifications <= 2,
                $"批量导入 {count} 项触发了 {collectionNotifications} 次集合变更通知 —— 应当合并成一次批量通知");
            Assert.True(
                collectionNotifications < count,
                "集合通知次数不许随条数增长（那正是界面被喂满的原因）");

            /*
             * ③ 批量入列（Reset 通知）之后，**每一条的勾选监听仍然挂着**：
             * Reset 事件不带 OldItems/NewItems，漏挂的话"勾一下 → 汇总里的选中数跟着变"就断了
             *（2026-09-22 修过的"界面在撒谎"会从这个新入口复活）。
             */
            ArchiveTask first = harness.Vm.Tasks[0];

            first.IsSelected = !first.IsSelected;

            Assert.Equal(
                harness.Vm.Tasks.Count(task => task.IsSelected),
                harness.Vm.Summary.SelectedCount);
        }

        // ================================================================ 装配

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                CountingEngine engine,
                ExtractionCoordinator coordinator,
                string sourceDirectory)
            {
                Vm = vm;
                Engine = engine;
                Coordinator = coordinator;
                SourceDirectory = sourceDirectory;
            }

            public MainViewModel Vm { get; }

            public CountingEngine Engine { get; }

            public ExtractionCoordinator Coordinator { get; }

            public string SourceDirectory { get; }

            /// <summary>造一个真实存在的源文件并加进任务列表（引擎是假的，内容不重要）。</summary>
            public ArchiveTask AddTask(string fileName, bool isSelected)
            {
                string path = Path.Combine(SourceDirectory, fileName);

                File.WriteAllText(path, "fake archive - engine is faked in these tests", new UTF8Encoding(false));

                var task = new ArchiveTask(path, Vm.Tasks.Count + 1)
                {
                    IsArchive = true,
                    DetectedFormat = "7Z",
                    ExtensionStatus = StatusText.ExtensionNormal,
                    Status = StatusText.Recognized,
                    IsSelected = isSelected
                };

                Vm.Tasks.Add(task);
                Vm.UpdateSummary();

                return task;
            }
        }

        private Harness CreateHarness(string runName)
        {
            string runRoot = Path.Combine(_root, runName);
            string dataRoot = Path.Combine(runRoot, "data");
            string outputRoot = Path.Combine(runRoot, "out");
            string sourceDirectory = Path.Combine(runRoot, "src");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);
            Directory.CreateDirectory(sourceDirectory);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CacheRootDirectory = dataRoot;
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.MaxParallelExtractCount = 1;

            settingsService.Save(settings);

            var engine = new CountingEngine();
            var passwordService = new PasswordService { DataRootDirectory = dataRoot };
            var logService = new LogService(pathService);

            // MainViewModel 的构造会顺手写两个进程级静态：先存后还原（与其它管线测试同一套）。
            string? previousWorkspaceRoot = ArchiveFixer.Extraction.RecursiveExtractor.ConfiguredWorkspaceRoot;
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

            ArchiveFixer.Extraction.RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            var coordinator = new ExtractionCoordinator(
                vm,
                engine,
                passwordService,
                pathService,
                new DialogService());

            return new Harness(vm, engine, coordinator, sourceDirectory);
        }

        private static ArchiveListResult ListResult(string fileName, int size)
        {
            return new ArchiveListResult
            {
                Success = true,
                FileCount = 1,
                TotalUncompressedSize = size,
                Entries = new List<ArchiveEntry> { new() { Path = fileName, Size = size } },
                EngineId = "counting",
                EngineVersion = "1.0"
            };
        }

        private static string SafeFileName(string path)
        {
            try
            {
                return Path.GetFileName(path);
            }
            catch (ArgumentException)
            {
                return path;
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();

            while (watch.Elapsed < timeout)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(20);
            }

            Assert.Fail($"等待条件超时（{timeout.TotalSeconds:F0} 秒）");
        }

        /// <summary>
        /// 记账用的假引擎：**每一次**动作都把被问到的归档路径记下来。
        ///
        /// 这些用例关心的不是解压本身，而是"程序到底对哪些文件动过手"——
        /// 所以断言落在 <see cref="Calls"/> / <see cref="TouchedArchivePaths"/> 上。
        /// </summary>
        private sealed class CountingEngine : IArchiveEngine
        {
            private readonly List<string> _touched = new();

            public Func<ArchiveRequest, Task<ArchiveListResult>>? OnListAsync { get; set; }

            public Func<ArchiveRequest, Task<ArchiveOperationResult>>? OnExtractAsync { get; set; }

            /// <summary>四个动作的全部调用记录（动作名 + 归档路径），供"一次都没被调用"直接断言。</summary>
            public List<string> Calls { get; } = new();

            public IReadOnlyList<string> TouchedArchivePaths
            {
                get
                {
                    lock (_touched)
                    {
                        return _touched.ToList();
                    }
                }
            }

            public string Id => "counting";

            public string DisplayName => "记账假引擎";

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

            public Task<ArchiveProbeResult> ProbeAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default)
            {
                Record("probe", request);

                return Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });
            }

            public Task<ArchiveListResult> ListAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default)
            {
                Record("list", request);

                return OnListAsync != null
                    ? OnListAsync(request)
                    : Task.FromResult(ListResult("payload.bin", 1));
            }

            public Task<ArchiveOperationResult> TestAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default)
            {
                Record("test", request);

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
                Record("extract", request);

                return OnExtractAsync != null
                    ? OnExtractAsync(request)
                    : Task.FromResult(new ArchiveOperationResult
                    {
                        Success = true,
                        Status = StatusText.ExtractSuccess,
                        DetectedErrorType = "None"
                    });
            }

            private void Record(string action, ArchiveRequest request)
            {
                string path = request?.ArchivePath ?? string.Empty;

                lock (_touched)
                {
                    Calls.Add(action + " " + path);
                    _touched.Add(path);
                }
            }
        }
    }
}

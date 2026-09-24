using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 落点四条规则的**管线级**回归（用户 2026-09-24 第 13 条）。
    ///
    /// <para>
    /// 纯函数的四条规则在 <see cref="OutputPlacementTests"/> 里逐条钉死；这里补的是**只有跑一遍管线
    /// 才看得见**的三件事：
    /// </para>
    /// <list type="number">
    /// <item><description><b>"绝不弄乱原文件夹"的反向断言</b>（用户原话）：未指定位置时，
    /// 源目录里**除了新子文件夹不新增任何东西**，文件夹那一档也不产生额外的同名子文件夹层；</description></item>
    /// <item><description><b>共用落点</b>（"添加文件夹 + 指定位置"）：文件夹里的包都落进
    /// <c>BBB\222\</c> 同一层，不会被"目录已存在且非空就改名 <c>222(1)</c>"拆开；</description></item>
    /// <item><description><b>旧配置迁移</b>：旧的 <c>appsettings.json</c>（含被删掉的两档对应的布尔组合）
    /// 读进来不炸，落到保留的两档上。</description></item>
    /// </list>
    ///
    /// <para>引擎是假的（<see cref="PlacementFakeEngine"/>），所以这些用例不依赖 7z，也不碰任何真实包。</para>
    /// </summary>
    public sealed class PlacementRulesTests : IDisposable
    {
        private readonly string _root;

        public PlacementRulesTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerPlacementRules", Guid.NewGuid().ToString("N"));
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

        // ================================================================ 规则二：文件夹 + 未指定位置

        /// <summary>
        /// 用户在「添加文件夹」里选了 <c>111\222</c>：产物必须全部留在 <c>222</c> **里面**。
        ///
        /// <para>反向断言（用户原话"我们解压绝对不能将原来的文件夹给弄混乱"）：跑完一整批之后，
        /// 源目录里**只多出产物子文件夹**，别的什么都没多 —— 没有 <c>222\222\</c> 这一层，
        /// 也没有东西被扔到上一层 <c>111\</c> 去。</para>
        /// </summary>
        [Fact]
        public async Task 文件夹未指定位置_源目录里只多出产物子文件夹()
        {
            Harness harness = CreateHarness("folder-inplace", settings =>
            {
                settings.ExtractToOriginalDirectory = true;
            });

            string packageFolder = Path.Combine(harness.SourceRoot, "111", "222");
            Directory.CreateDirectory(packageFolder);

            string first = WriteFakeArchive(packageFolder, "a.rar");
            string second = WriteFakeArchive(packageFolder, "b.7z");

            IReadOnlyList<ArchiveTask> tasks = await harness.ImportFolderAsync(packageFolder);

            Assert.Equal(2, tasks.Count);
            Assert.All(tasks, task => Assert.Equal(SourceSelectionKind.Folder, task.SourceSelectionKind));

            string before = DescribeDirectory(packageFolder);
            string parentBefore = DescribeDirectory(Path.Combine(harness.SourceRoot, "111"));

            await harness.RunAsync(tasks);

            Assert.All(tasks, task => Assert.Equal(StatusText.ExtractSuccess, task.Status));

            // 落点：就在所选文件夹里面，一个包一层（名字 = 各自包基名）。
            Assert.Equal(Path.Combine(packageFolder, "a"), tasks[0].OutputPath);
            Assert.Equal(Path.Combine(packageFolder, "b"), tasks[1].OutputPath);

            // ① 源目录（222）里新增的条目**只有**那两层产物子文件夹。
            string[] newEntries = Directory.GetFileSystemEntries(packageFolder)
                .Select(entry => Path.GetFileName(entry) ?? string.Empty)
                .Where(name => !before.Contains(name + ";", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Assert.Equal(new[] { "a", "b" }, newEntries);

            // ② 绝不新建同名层 222\222，也绝不把任何东西扔到上一层 111\。
            Assert.False(Directory.Exists(Path.Combine(packageFolder, "222")), "不该多出 222\\222 这一层");

            string[] parentNewEntries = Directory.GetFileSystemEntries(Path.Combine(harness.SourceRoot, "111"))
                .Select(entry => Path.GetFileName(entry) ?? string.Empty)
                .Where(name => !parentBefore.Contains(name + ";", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            Assert.Empty(parentNewEntries);

            // ③ 源包一个字节都没动（这一批用的是 KeepInPlace）。
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
        }

        // ================================================================ 规则四：文件夹 + 指定位置

        /// <summary>
        /// 用户在「添加文件夹」里选了 <c>222</c>，并指定了解压位置 <c>BBB</c>：
        /// 在 BBB 里建一个和选中文件夹同名的子文件夹（<c>BBB\222\</c>），再在里面操作。
        ///
        /// <para>重点在**共用落点**：同一个文件夹里的所有包都落进这一层，
        /// 不能因为"目录已存在且非空"被改名成 <c>222(1)</c>（那样用户要的"都放进 BBB\222\"当场被拆开）。</para>
        /// </summary>
        [Fact]
        public async Task 文件夹指定位置_所有包都落进以选中文件夹命名的那一层()
        {
            Harness harness = CreateHarness("folder-custom", settings =>
            {
                settings.ExtractToOriginalDirectory = false;
                settings.CustomOutputDirectory = Path.Combine(_root, "folder-custom", "BBB");
                settings.KeepArchiveNameFolder = true;

                // 源包搬进其余物（默认档）：顺带把"共用根时其余物按包名分层"也钉住。
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
            });

            string packageFolder = Path.Combine(harness.SourceRoot, "111", "222");
            Directory.CreateDirectory(packageFolder);

            WriteFakeArchive(packageFolder, "a.rar");
            WriteFakeArchive(packageFolder, "b.7z");

            IReadOnlyList<ArchiveTask> tasks = await harness.ImportFolderAsync(packageFolder);

            await harness.RunAsync(tasks);

            string shared = Path.Combine(harness.CustomRoot, "222");

            Assert.All(tasks, task => Assert.Equal(StatusText.ExtractSuccess, task.Status));
            Assert.All(tasks, task => Assert.Equal(shared, task.OutputPath));

            Assert.False(Directory.Exists(Path.Combine(harness.CustomRoot, "222(1)")), "共用落点不许被拆成一串 222(1)");
            Assert.True(File.Exists(Path.Combine(shared, "a.rar.txt")));
            Assert.True(File.Exists(Path.Combine(shared, "b.7z.txt")));

            // 共用根 → 其余物按包基名分层（决策 D-10），不然两个包的分卷/中间件会互相撞名。
            Assert.True(Directory.Exists(Path.Combine(shared, "其余物", "a")));
            Assert.True(Directory.Exists(Path.Combine(shared, "其余物", "b")));
        }

        // ================================================================ 规则一 / 规则三：选文件

        [Fact]
        public async Task 文件未指定位置_每个包在它旁边建一个同名子文件夹()
        {
            Harness harness = CreateHarness("file-inplace", settings =>
            {
                settings.ExtractToOriginalDirectory = true;
            });

            string packages = Path.Combine(harness.SourceRoot, "111");
            Directory.CreateDirectory(packages);

            string first = WriteFakeArchive(packages, "222.rar");
            string second = WriteFakeArchive(packages, "333.7z.001");

            IReadOnlyList<ArchiveTask> tasks = await harness.ImportFilesAsync(new[] { first, second });

            string before = DescribeDirectory(packages);

            await harness.RunAsync(tasks);

            Assert.Equal(StatusText.ExtractSuccess, tasks[0].Status);
            Assert.Equal(Path.Combine(packages, "222"), tasks[0].OutputPath);

            // 分卷组（333.7z.001）也只建一层 333\，不是 333.7z\。
            Assert.Equal(Path.Combine(packages, "333"), tasks[1].OutputPath);

            string[] newEntries = Directory.GetFileSystemEntries(packages)
                .Select(entry => Path.GetFileName(entry) ?? string.Empty)
                .Where(name => !before.Contains(name + ";", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Assert.Equal(new[] { "222", "333" }, newEntries);
        }

        [Fact]
        public async Task 文件指定位置_每个包各占一个子文件夹()
        {
            Harness harness = CreateHarness("file-custom", settings =>
            {
                settings.ExtractToOriginalDirectory = false;
                settings.CustomOutputDirectory = Path.Combine(_root, "file-custom", "BBB");
            });

            string packages = Path.Combine(harness.SourceRoot, "111");
            Directory.CreateDirectory(packages);

            string first = WriteFakeArchive(packages, "222.rar");
            string second = WriteFakeArchive(packages, "333.mp4");

            IReadOnlyList<ArchiveTask> tasks = await harness.ImportFilesAsync(new[] { first, second });

            await harness.RunAsync(tasks);

            Assert.All(tasks, task => Assert.Equal(StatusText.ExtractSuccess, task.Status));
            Assert.Equal(Path.Combine(harness.CustomRoot, "222"), tasks[0].OutputPath);

            // 内嵌归档（伪装成 mp4 的双面文件）名字同样取对：333.mp4 → 333\。
            Assert.Equal(Path.Combine(harness.CustomRoot, "333"), tasks[1].OutputPath);
        }

        // ================================================================ 旧配置迁移

        /// <summary>
        /// 旧 <c>appsettings.json</c>（那两个布尔正是"摊平"两档的写法）读进来：
        /// **不抛异常**，并且迁移到保留的两档上（落点照样建同名子文件夹）。
        /// </summary>
        [Theory]
        [InlineData(true, false, "", OutputPlacementMode.PerArchiveSubfolder)]
        [InlineData(false, false, @"D:\BBB", OutputPlacementMode.CustomRootPerArchive)]
        [InlineData(false, true, @"D:\BBB", OutputPlacementMode.CustomRootPerArchive)]
        [InlineData(true, true, "", OutputPlacementMode.PerArchiveSubfolder)]
        public void 旧配置读进来被迁移到保留的两档(
            bool extractToOriginalDirectory,
            bool keepArchiveNameFolder,
            string customOutputDirectory,
            OutputPlacementMode expected)
        {
            string dataRoot = Path.Combine(_root, "legacy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };

            string json =
                "{\n" +
                $"  \"ExtractToOriginalDirectory\": {extractToOriginalDirectory.ToString().ToLowerInvariant()},\n" +
                $"  \"KeepArchiveNameFolder\": {keepArchiveNameFolder.ToString().ToLowerInvariant()},\n" +
                $"  \"CustomOutputDirectory\": \"{customOutputDirectory.Replace("\\", "\\\\")}\"\n" +
                "}";

            File.WriteAllText(pathService.SettingsFilePath, json, new UTF8Encoding(false));

            // 读旧配置不许炸（Normalize 里做迁移）。
            AppSettings loaded = new SettingsService(pathService).Load();

            Assert.True(loaded.KeepArchiveNameFolder, "旧配置迁移后第二个布尔归一成 true（没有'不建子文件夹'的档了）");

            Assert.Equal(
                expected,
                OutputPlacement.FromLegacyFlags(
                    loaded.ExtractToOriginalDirectory,
                    loaded.KeepArchiveNameFolder,
                    loaded.CustomOutputDirectory));

            // 而且真的算出"建同名子文件夹"的落点，绝不摊在 111\ 或根上。
            var pathServiceForPlacement = new PathService { DataRootDirectory = dataRoot };

            string output = pathServiceForPlacement.BuildOutputPath(
                new ArchiveTask(@"C:\111\222.rar"),
                new ExtractOptions
                {
                    ExtractToOriginalDirectory = loaded.ExtractToOriginalDirectory,
                    KeepArchiveNameFolder = loaded.KeepArchiveNameFolder,
                    CustomOutputDirectory = loaded.CustomOutputDirectory
                });

            Assert.EndsWith(@"222", output, StringComparison.Ordinal);
            Assert.NotEqual(@"C:\111", output);
        }

        // ================================================================ 装配

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                PlacementFakeEngine engine,
                ExtractionCoordinator coordinator,
                FileScanService scan,
                string sourceRoot,
                string customRoot)
            {
                Vm = vm;
                Engine = engine;
                Coordinator = coordinator;
                Scan = scan;
                SourceRoot = sourceRoot;
                CustomRoot = customRoot;
            }

            public MainViewModel Vm { get; }

            public PlacementFakeEngine Engine { get; }

            public ExtractionCoordinator Coordinator { get; }

            public FileScanService Scan { get; }

            public string SourceRoot { get; }

            public string CustomRoot { get; }

            /// <summary>按「添加文件夹」的语义导入（任务上带 SourceSelectionKind.Folder）。</summary>
            public async Task<IReadOnlyList<ArchiveTask>> ImportFolderAsync(string folder)
            {
                return await ImportAsync(new[] { folder });
            }

            /// <summary>按「添加文件」的语义导入。</summary>
            public async Task<IReadOnlyList<ArchiveTask>> ImportFilesAsync(IEnumerable<string> files)
            {
                return await ImportAsync(files);
            }

            public async Task RunAsync(IReadOnlyList<ArchiveTask> tasks)
            {
                await Coordinator.StartExtractAsync();

                await WaitUntilAsync(() => !Vm.IsBusy, TimeSpan.FromSeconds(60));

                // 整批跑完（每个任务都有终态时刻）：不然下面的落点断言可能看着"对的"其实还没跑。
                Assert.All(tasks, task => Assert.True(task.EndTime.HasValue, $"{task.FileName} 没有跑完"));
            }

            private async Task<IReadOnlyList<ArchiveTask>> ImportAsync(IEnumerable<string> paths)
            {
                List<ArchiveTask> scanned = await Scan.ScanPathsAsync(
                    paths,
                    new ScanOptions { ScanMode = "ScanAllFiles", RecursiveScan = false },
                    CancellationToken.None);

                var imported = new List<ArchiveTask>();

                foreach (ArchiveTask task in scanned)
                {
                    /*
                     * 识别这一步在真机上是 ArchiveDetectService 干的（魔数 + 引擎列目录）。
                     * 这条用例的源文件是假的，所以直接写识别结论 —— 与 CheckedOnlySelectionTests 同一套做法。
                     */
                    task.IsArchive = true;
                    task.DetectedFormat = "7Z";
                    task.ExtensionStatus = StatusText.ExtensionNormal;
                    task.Status = StatusText.Recognized;
                    task.IsSelected = true;
                    task.Index = imported.Count + 1;

                    Vm.Tasks.Add(task);
                    imported.Add(task);
                }

                return imported;
            }
        }

        private Harness CreateHarness(string runName, Action<AppSettings>? configure = null)
        {
            string runRoot = Path.Combine(_root, runName);
            string dataRoot = Path.Combine(runRoot, "data");
            string customRoot = Path.Combine(runRoot, "BBB");
            string sourceRoot = Path.Combine(runRoot, "src");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(sourceRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CacheRootDirectory = dataRoot;

            /*
             * "指定位置"那一格默认**留空**：主界面的「输出目录」框一旦有值就会把
             * "解压到压缩包所在目录"那一档顶掉（MainViewModel.SelectedOutputDirectory 的既有行为），
             * 而未指定位置的两条规则正是要验那个默认档。指定的两档由用例自己填。
             */
            settings.CustomOutputDirectory = string.Empty;
            settings.ExtractToOriginalDirectory = true;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;

            // 用例自己决定源包怎么处理；默认"一个字节都不搬"（源目录的反向断言要靠它）。
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.MaxParallelExtractCount = 1;

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var engine = new PlacementFakeEngine();
            var passwordService = new PasswordService { DataRootDirectory = dataRoot };
            var logService = new LogService(pathService);
            var scan = new FileScanService();

            // MainViewModel 的构造会顺手写两个进程级静态：先存后还原（与其它管线测试同一套）。
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            var vm = new MainViewModel(
                scan,
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

            var coordinator = new ExtractionCoordinator(
                vm,
                engine,
                passwordService,
                pathService,
                new DialogService());

            return new Harness(vm, engine, coordinator, scan, sourceRoot, customRoot);
        }

        /// <summary>写一个"假包"（内容无所谓，引擎是假的）。</summary>
        private static string WriteFakeArchive(string directory, string fileName)
        {
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, fileName);
            File.WriteAllText(path, "not a real archive - the engine is faked in these tests", new UTF8Encoding(false));
            return path;
        }

        /// <summary>目录第一层条目的快照（用来断言"只多了这几样东西"）。</summary>
        private static string DescribeDirectory(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();

            foreach (string entry in Directory.GetFileSystemEntries(directory).OrderBy(x => x, StringComparer.Ordinal))
            {
                builder.Append(Path.GetFileName(entry)).Append(';');
            }

            return builder.ToString();
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
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
        /// 假引擎：列目录声明"有一个和包同名的 txt"，解压时真的把它写进引擎输出目录（= 暂存目录）。
        /// 于是输出校验能通过，定稿搬运也真的有东西可搬。
        /// </summary>
        private sealed class PlacementFakeEngine : IArchiveEngine
        {
            public string Id => "placement-fake";

            public string DisplayName => "落点用例假引擎";

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
                string name = ContentFileName(request.ArchivePath);

                return Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    FileCount = 1,
                    TotalUncompressedSize = 1,
                    Entries = new List<ArchiveEntry> { new() { Path = name, Size = 1 } },
                    EngineId = Id,
                    EngineVersion = Version
                });
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Succeeded());
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                string output = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);
                    File.WriteAllText(Path.Combine(output, ContentFileName(request.ArchivePath)), "x");
                }

                return Task.FromResult(Succeeded());
            }

            private static string ContentFileName(string? archivePath)
            {
                return Path.GetFileName(archivePath ?? "payload") + ".txt";
            }

            private static ArchiveOperationResult Succeeded()
            {
                return new ArchiveOperationResult
                {
                    Success = true,
                    Status = StatusText.ExtractSuccess,
                    Message = "解压成功",
                    DetectedErrorType = "None"
                };
            }
        }
    }
}

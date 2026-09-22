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
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「一键处理」把源包移进其余物 / 手动「只解压」永远不动源包（决策 D-9 / D-11 / D-12）。
    ///
    /// <para>
    /// 这一组是**红线测试**，三条各自钉死：
    /// ① **手动「只解压」这条路，即使设置成 <c>MoveToRest</c>，也绝对不动源包**、也不生成 <c>其余物</c>
    ///    （AGENTS.md §6 第 1 条：用户放开的只是"一键处理"这一条路径）；
    /// ② 一键处理默认档：整组源包进 <c>其余物</c>，内容物一点不受影响；
    /// ③ 搬不动（只读 / 被占用）时任务标「部分完成」并写明"内容物已好，源包未能移入其余物"，
    ///    内容物结论不受影响（不变量 6：跑了一半的事不许只报成功）。
    /// </para>
    /// <para>
    /// 引擎是假的（照着 ExtractionPipelineFixTests 的同一套装配）：这里要验的是**收尾那几步**
    /// ——定稿、校验、归集、源包处理——解压本身不是被测对象。
    /// </para>
    /// </summary>
    public class SourcePackageRestMoveTests : IDisposable
    {
        private readonly string _root;

        public SourcePackageRestMoveTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerRestMove", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 红线：只解压不动源包

        /// <summary>
        /// **本轮的核心红线**：手动「只解压」走的是地基路径，设置成"移入其余物"也一样——
        /// 源文件仍在原位、内容不变，而且连 <c>其余物</c> 目录都不该被建出来。
        /// </summary>
        [Fact]
        public async Task 红线_手动只解压_设置MoveToRest也绝不动源包()
        {
            Harness harness = CreateHarness(settings => settings.SourceHandling = "MoveToRest");

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // 源包一个字节都不许动。
            Assert.True(File.Exists(source), "手动「只解压」把源包搬走/删掉了 —— 这是不变量 1 的红线");
            Assert.Equal("not a real archive - the engine is faked in these tests", File.ReadAllText(source));

            // 内容物照常产出。
            Assert.Equal(5, CountFiles(task.OutputPath));

            // 「其余物」连建都不该建（没有任何中间件、也没有搬源包）。
            Assert.False(
                Directory.Exists(Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName)),
                "手动「只解压」不该生成其余物目录");
        }

        // ================================================================ ② 一键处理默认档

        [Fact]
        public async Task 一键处理_默认档_源包移入其余物_内容物不受影响()
        {
            Harness harness = CreateHarness();

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            string workDirectory = harness.PathService.BuildTaskWorkDirectory(task);

            await harness.Coordinator.StartExtractForOneClickAsync();

            string restDirectory = Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName);
            string movedSource = Path.Combine(restDirectory, "pack.7z");

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.True(task.IsOutputVerified);

            // 源包：原位没有了，其余物里有；内容一个字节都没变。
            Assert.False(File.Exists(source), "一键处理默认档应该把源包搬进其余物");
            Assert.True(File.Exists(movedSource), $"源包没有落到 {movedSource}");
            Assert.Equal("not a real archive - the engine is faked in these tests", File.ReadAllText(movedSource));

            // 内容物照常（其余物里那份源包不算内容物）。
            Assert.Equal(5, CountFiles(task.OutputPath, excludeArtifactDirectory: true));
            Assert.Contains(
                Directory.GetFiles(task.OutputPath, "*.bin"),
                file => Path.GetFileName(file) == "payload-00000.bin");

            // 任务对象跟着改：CurrentPath 指向新位置的源包。
            // 这一条不是"顺手"：一键处理的续解扫描靠它把"刚搬走的源包"排除掉，
            // 不改的话源包会被当成一个新内层包再解一遍（内容物凭空多一份）。
            Assert.Equal(movedSource, task.CurrentPath);

            // 日志必须说清"从哪搬到哪"。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("源包移入其余物", StringComparison.Ordinal) &&
                        line.Contains(source, StringComparison.Ordinal) &&
                        line.Contains(movedSource, StringComparison.Ordinal));

            // 工作区照常清理：它必须按**暂存目录**反推（源包搬家后 CurrentPath 的哈希变了，
            // 按 CurrentPath 重算会算到另一个目录上，于是近 1 GB 中间件静默留在工作区）。
            Assert.False(Directory.Exists(workDirectory), "源包搬家之后工作区没被清理");
        }

        [Fact]
        public async Task 一键处理_留在原地档_不动源包也不建其余物()
        {
            Harness harness = CreateHarness(settings => settings.SourceHandling = "KeepInPlace");

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            await harness.Coordinator.StartExtractForOneClickAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.True(File.Exists(source));
            Assert.Equal(source, task.CurrentPath);
            Assert.False(Directory.Exists(Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName)));
        }

        [Fact]
        public async Task 一键处理_删除档_校验通过后删源包()
        {
            Harness harness = CreateHarness(settings => settings.SourceHandling = "DeleteAfterVerify");

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            await harness.Coordinator.StartExtractForOneClickAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.False(File.Exists(source), "DeleteAfterVerify 档应当在校验通过后删掉源包");
            Assert.Equal(5, CountFiles(task.OutputPath));
        }

        // ================================================================ ③ 失败与跨盘

        [Fact]
        public async Task 一键处理_源包搬不动_任务标部分完成_内容物不受影响()
        {
            Harness harness = CreateHarness();

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            // 模拟"源包只读 / 被别的程序占用"：同盘改名直接抛。
            harness.FileSystem.MoveFailure = new IOException("文件被占用");

            await harness.Coordinator.StartExtractForOneClickAsync();

            // 内容物已经好了：产物一个不少，输出校验仍然通过。
            Assert.Equal(5, CountFiles(task.OutputPath));
            Assert.True(task.IsOutputVerified);

            // 但任务整体没做完 —— 不许只报成功（不变量 6）。
            Assert.Equal(StatusText.PartiallyCompleted, task.Status);
            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.Contains("内容物已好，源包未能移入其余物", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("内容物已好，源包未能移入其余物", task.VerifyMessage, StringComparison.Ordinal);

            // 源包原地不动，其余物目录不该被建出来。
            Assert.True(File.Exists(source));
            Assert.False(Directory.Exists(Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName)));
        }

        [Fact]
        public async Task 一键处理_分卷组整组一起移入其余物()
        {
            Harness harness = CreateHarness();

            string first = CreateSourceFile("222.7z.001");
            string second = CreateSourceFile("222.7z.002");

            var task = new ArchiveTask(first, 1)
            {
                IsArchive = true,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal,
                Status = StatusText.Recognized,
                IsSelected = true,
                IsVolumeGroup = true,
                VolumeGroupKey = Path.Combine(_root, "src") + "|222"
            };

            task.VolumePaths.Add(first);
            task.VolumePaths.Add(second);

            harness.Vm.Tasks.Add(task);

            await harness.Coordinator.StartExtractForOneClickAsync();

            string restDirectory = Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName);

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.False(File.Exists(first), "第一卷没有被搬走");
            Assert.False(File.Exists(second), "第二卷被落下了（决策 D-12：整组一起移）");
            Assert.True(File.Exists(Path.Combine(restDirectory, "222.7z.001")));
            Assert.True(File.Exists(Path.Combine(restDirectory, "222.7z.002")));

            // 分卷清单也要跟着改到新位置（后续清理 / 续解都读它）。
            Assert.Equal(
                new[] { Path.Combine(restDirectory, "222.7z.001"), Path.Combine(restDirectory, "222.7z.002") },
                task.VolumePaths.ToArray());
        }

        [Fact]
        public async Task 一键处理_跨盘搬运_复制成功后再删原件()
        {
            Harness harness = CreateHarness();

            // 强制"跨盘"：走复制 + 删原件那条路（真机上要造第二个卷，这里由假文件系统给结论）。
            harness.FileSystem.SameVolume = false;

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            await harness.Coordinator.StartExtractForOneClickAsync();

            string movedSource = Path.Combine(
                task.OutputPath,
                ProcessArtifactLayout.ArtifactDirectoryName,
                "pack.7z");

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.True(harness.FileSystem.CopyWasUsed);
            Assert.False(harness.FileSystem.MoveWasUsed, "跨盘路径不许走同盘改名");
            Assert.False(File.Exists(source), "复制成功之后原件才该消失");
            Assert.True(File.Exists(movedSource));
            Assert.Contains(harness.LogTexts, line => line.Contains("跨盘", StringComparison.Ordinal));
        }

        // ================================================================ 归集之后源包跟着走

        [Fact]
        public async Task 一键处理_归集开启时_源包跟着落到归集目录的其余物里()
        {
            string collectRoot = Path.Combine(_root, "collect");

            Harness harness = CreateHarness(settings =>
            {
                settings.CollectResultsToDirectory = true;
                settings.CollectTargetDirectory = collectRoot;
            });

            string source = CreateSourceFile("pack.7z");
            ArchiveTask task = AddTask(harness, source);

            await harness.Coordinator.StartExtractForOneClickAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.StartsWith(collectRoot, task.CollectedPath, StringComparison.OrdinalIgnoreCase);

            /*
             * 归集把整个产物目录搬走了（其余物跟着走），所以源包必须落进**归集之后**的那个其余物里。
             * 落在原来那个已经不存在的位置上，等于把源包扔进一个空目录 —— 用户再也找不到。
             */
            string movedSource = Path.Combine(
                task.CollectedPath,
                ProcessArtifactLayout.ArtifactDirectoryName,
                "pack.7z");

            Assert.False(File.Exists(source));
            Assert.True(File.Exists(movedSource), $"源包没有跟着归集走：{movedSource}");
            Assert.Equal(5, CountFiles(task.CollectedPath, excludeArtifactDirectory: true));
        }

        // ================================================================ 装配

        private Harness CreateHarness(Action<AppSettings>? configure = null)
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

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var engine = new FakeEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);
            var fileSystem = new SwitchableSourceMoveFileSystem();

            // MainViewModel 的构造会顺手写两个进程级静态：先存后还原，
            // 免得别的测试拿到我这边马上要删的临时目录（与 ExtractionPipelineFixTests 同一套做法）。
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

            var coordinator = new ExtractionCoordinator(
                vm, engine, passwordService, pathService, new DialogService(), fileSystem);

            return new Harness(vm, engine, coordinator, logService, pathService, fileSystem);
        }

        private string CreateSourceFile(string fileName)
        {
            string directory = Path.Combine(_root, "src");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, fileName);
            File.WriteAllText(path, "not a real archive - the engine is faked in these tests");
            return path;
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

        private static int CountFiles(string? directory, bool excludeArtifactDirectory = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return 0;
                }

                if (!excludeArtifactDirectory)
                {
                    return Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length;
                }

                return Directory
                    .GetFiles(directory, "*", SearchOption.AllDirectories)
                    .Count(file => !file.StartsWith(
                        Path.Combine(directory, ProcessArtifactLayout.ArtifactDirectoryName) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return -1;
            }
        }

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                FakeEngine engine,
                ExtractionCoordinator coordinator,
                LogService log,
                PathService pathService,
                SwitchableSourceMoveFileSystem fileSystem)
            {
                Vm = vm;
                Engine = engine;
                Coordinator = coordinator;
                Log = log;
                PathService = pathService;
                FileSystem = fileSystem;
            }

            public MainViewModel Vm { get; }

            public FakeEngine Engine { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            /// <summary>屏幕日志的文本（无 WPF 应用的测试进程里照样会被填充）。</summary>
            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);

            public PathService PathService { get; }

            public SwitchableSourceMoveFileSystem FileSystem { get; }
        }

        /// <summary>
        /// 可控的假引擎：解压时往引擎输出目录（= 暂存目录）写 5 个文件，
        /// 列目录返回同样 5 个条目（于是输出校验能通过）。与 ExtractionPipelineFixTests 的同一套。
        /// </summary>
        private sealed class FakeEngine : IArchiveEngine
        {
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
                return Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    FileCount = 5,
                    TotalUncompressedSize = 0,
                    Entries = Enumerable.Range(0, 5)
                        .Select(i => new ArchiveEntry { Path = $"payload-{i:D5}.bin", Size = 1 })
                        .ToList(),
                    EngineId = "fake",
                    EngineVersion = "1.0"
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

                    for (int i = 0; i < 5; i++)
                    {
                        File.WriteAllText(Path.Combine(output, $"payload-{i:D5}.bin"), "x");
                    }
                }

                return Task.FromResult(Succeeded());
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

        /// <summary>
        /// 只接管"同盘 / 跨盘"这个判断与失败注入，真正的复制 / 删除仍落在临时目录里。
        /// </summary>
        private sealed class SwitchableSourceMoveFileSystem : ISourceMoveFileSystem
        {
            private readonly ISourceMoveFileSystem _inner = FileSystemSourceMoveFileSystem.Instance;

            public bool SameVolume { get; set; } = true;

            public Exception? MoveFailure { get; set; }

            public bool MoveWasUsed { get; private set; }

            public bool CopyWasUsed { get; private set; }

            public bool FileExists(string path) => _inner.FileExists(path);

            public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

            public bool CreateDirectory(string path) => _inner.CreateDirectory(path);

            public long GetFileSize(string path) => _inner.GetFileSize(path);

            public bool IsSameVolume(string sourcePath, string targetPath) => SameVolume;

            public void MoveFile(string source, string target)
            {
                MoveWasUsed = true;

                if (MoveFailure != null)
                {
                    throw MoveFailure;
                }

                _inner.MoveFile(source, target);
            }

            public void CopyFile(string source, string target)
            {
                CopyWasUsed = true;
                _inner.CopyFile(source, target);
            }

            public void DeleteFile(string path) => _inner.DeleteFile(path);
        }
    }
}

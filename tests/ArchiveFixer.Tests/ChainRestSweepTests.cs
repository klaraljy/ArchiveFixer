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
    /// 用户 2026-09-28 拍板的那一条：**链尾把内层包收进其余物时，"这一轮没跑成的内层包"也算过程物**。
    ///
    /// <para>用户原话：「自动清掉，这个算没有成功的过程物，而且原包还在就不用怕，如果原包放在了其余物里面
    /// 一起删除，成功了就刚好是我们要达到的地方，失败了也不会删除」—— 判据只有一条：
    /// <b>外层这一单成功 + 输出校验通过</b> 时，成品目录里那些"外层解出来、这一轮没跑成"的内层包
    /// 跟源包一样收进其余物（删除档下跟着一起清掉）；外层没成功时**一个字节都不搬**。</para>
    ///
    /// <para>走的是真入口 <c>ExtractionCoordinator.CompleteRootSourcePackagesAfterChainAsync</c>
    /// —— 它就是链尾收内层包（<c>CollectChainInnerPackagesIntoRestAsync</c>）的唯一调用点，
    /// 所以这里连"链尾会不会真的调它"一起钉住，比反射直调私有方法更贴近真管线。</para>
    ///
    /// <para>这一组钉两面：</para>
    /// <list type="number">
    /// <item><description><b>外层成功</b>：那一组内层分卷**每一卷**都进其余物
    /// （⛔ 不许像 WinRAR 那样只搬 <c>.001</c>，真机 2026-09-28 就是这么漏的）；</description></item>
    /// <item><description><b>外层失败 / 校验没过</b>：一个都不搬 —— 文件留在原处，其余物里没有它们，任务路径也不许被改。</description></item>
    /// </list>
    ///
    /// <para>引擎是假的（照 <c>SourcePackageRestMoveTests</c> 那一套装配）：这里要验的是**收尾那一步**，
    /// 解压本身不是被测对象。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class ChainRestSweepTests : IDisposable
    {
        /// <summary>假引擎写出来的那一个内容物（有它才算"这一单真的解出了东西"）。</summary>
        private const string ContentFileName = "content.bin";

        private readonly string _root;

        public ChainRestSweepTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerChainRestSweep", Guid.NewGuid().ToString("N"));
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

        /// <summary>夹具要摆的根任务终态档（判据只读这两个机器字段：<c>Outcome</c> + <c>OutputVerification</c>）。</summary>
        public enum RootState
        {
            /// <summary>成功 + 输出校验通过 —— 唯一允许搬内层包的那一档。</summary>
            Succeeded,

            /// <summary>这一单失败了。</summary>
            Failed,

            /// <summary>状态是成功，但输出校验没过（两个条件必须同时成立）。</summary>
            VerifyFailed
        }

        // ================================================================ ① 外层成功：整组收下

        /// <summary>
        /// 外层成功 + 校验通过：成品目录里那组"这一轮没跑成"的内层分卷（3 卷）**每一卷**都进其余物，
        /// 成品目录里一卷不剩，续解任务的路径跟着改到新位置。
        /// </summary>
        [Fact]
        public async Task 外层成功且校验通过_没跑成的内层分卷组整组进其余物()
        {
            Harness harness = CreateHarness();
            ChainFixture chain = await BuildChainFixtureAsync(harness, RootState.Succeeded);

            // 搬之前：整组都在成品目录里（前提，免得后面断言的是一个空夹具）。
            Assert.All(chain.VolumePaths, volume => Assert.True(File.Exists(volume)));

            await harness.Coordinator.CompleteRootSourcePackagesAfterChainAsync(
                new[] { chain.Root }, new[] { chain.Root, chain.UnrunContinuation });

            // ① 每一卷都进了其余物 —— 少一卷就是真机那个"只搬 .001、其余留在成品目录"的缺陷。
            foreach (string volume in chain.VolumePaths)
            {
                string moved = Path.Combine(chain.RestDirectory, Path.GetFileName(volume));

                Assert.True(File.Exists(moved), $"分卷组只搬了一部分：{Path.GetFileName(volume)} 没进其余物");

                // ② 而且成品目录里不再留一份（是搬走，不是复制）。
                Assert.False(File.Exists(volume), $"{Path.GetFileName(volume)} 搬完之后不该还留在成品目录里");
            }

            // 整个输出根下就只有其余物里那一份，一共 3 个（没有多出 (1) 副本、也没有漏卷）。
            string[] copies = FilesNamedUnder(harness.OutputRoot, "inner.7z.");

            Assert.Equal(3, copies.Length);
            Assert.All(copies, path => Assert.Equal(chain.RestDirectory, Path.GetDirectoryName(path)));

            // ③ 任务对象跟着改位置（续解扫描靠它把"刚搬走的内层包"排除掉，不改就会再解一遍）。
            Assert.Equal(
                Path.Combine(chain.RestDirectory, "inner.7z.001"),
                chain.UnrunContinuation.CurrentPath);

            // 日志一行说清搬了什么（用户第 45 条：一行说完，不写两条完整路径）。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("内层包已移入其余物", StringComparison.Ordinal) &&
                        line.Contains("inner.7z.001", StringComparison.Ordinal));

            // ④ 内容物与根任务的结论一点没被这次搬运改坏。
            Assert.True(File.Exists(Path.Combine(chain.Root.OutputPath, ContentFileName)));
            Assert.Equal(TaskOutcome.Succeeded, chain.Root.Outcome);
            Assert.Equal(OutputVerificationOutcome.Passed, chain.Root.OutputVerification);
        }

        // ================================================================ ② 外层没成功：一个字节都不搬（红线）

        /// <summary>
        /// <b>红线</b>：根任务不是"成功 + 校验通过"时，那组没跑成的内层包**一个字节都不搬** ——
        /// 文件留在原处（内容照旧），其余物里没有它们，续解任务的路径也不许被改。
        ///
        /// <para>两档各来一次：外层这一单**失败**、外层成功了但**校验没过**。
        /// 后者专门用来钉"两个条件必须同时成立"（Outcome 还是 Succeeded，只有校验枚举不通过）。</para>
        /// </summary>
        [Theory]
        [InlineData(RootState.Failed)]
        [InlineData(RootState.VerifyFailed)]
        public async Task 外层没成功时_一个字节都不搬(RootState state)
        {
            Harness harness = CreateHarness();
            ChainFixture chain = await BuildChainFixtureAsync(harness, state);

            await harness.Coordinator.CompleteRootSourcePackagesAfterChainAsync(
                new[] { chain.Root }, new[] { chain.Root, chain.UnrunContinuation });

            // ① 红线：三卷都还在成品目录里（原位置、原大小）。
            for (int i = 0; i < chain.VolumePaths.Length; i++)
            {
                string volume = chain.VolumePaths[i];

                Assert.True(File.Exists(volume), $"{Path.GetFileName(volume)} 被搬走了 —— 外层没成功时一个字节都不许动");
                Assert.Equal(
                    i == chain.VolumePaths.Length - 1 ? 512L : 1024L,
                    new FileInfo(volume).Length);
            }

            // ② 其余物里一个都没有（其余物目录可能压根没建出来，两种都算对）。
            if (Directory.Exists(chain.RestDirectory))
            {
                Assert.Empty(FilesNamedUnder(chain.RestDirectory, "inner.7z."));
            }

            // ③ 整个输出根下，那三卷只出现在成品目录里（没有第二份）。
            string[] copies = FilesNamedUnder(harness.OutputRoot, "inner.7z.");

            Assert.Equal(
                chain.VolumePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                copies);

            // ④ 任务对象的路径也不许被改（改了就等于告诉续解扫描"它已经搬走了"）。
            Assert.Equal(chain.VolumePaths[0], chain.UnrunContinuation.CurrentPath);

            /*
             * ⑤ 但"如实点名"那一条照旧：链尾必须说清成品目录里还留着几个内层包 ——
             * 用户看不到文件、也看不到这句话，就会以为程序又忘了搬（真机原话："这个你没有删除，
             * 也许是你没有移到其余物里面"）。
             */
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("还留着 3 个内层包", StringComparison.Ordinal));
        }

        // ================================================================ 夹具

        /// <summary>一条链的现场：根任务、那个"这一轮没跑成"的续解任务、内层分卷组、其余物目录。</summary>
        private sealed class ChainFixture
        {
            public ArchiveTask Root { get; init; } = null!;

            public ArchiveTask UnrunContinuation { get; init; } = null!;

            public string[] VolumePaths { get; init; } = Array.Empty<string>();

            public string RestDirectory { get; init; } = string.Empty;
        }

        /// <summary>
        /// 摆出"链尾那一刻"的现场：
        /// <list type="number">
        /// <item><description>根任务**真的跑一轮**（假引擎写一个内容物）—— 于是 OutputPath / 其余物 / 校验结论
        /// 都是真管线给的，而不是手搓的字符串；</description></item>
        /// <item><description>在成品目录里放一组**外层解出来、这一轮没跑成**的内层分卷（3 卷，
        /// 照真机那 4 卷的形状：末卷更小）；</description></item>
        /// <item><description>造一个"是续解任务、但没有任何成功结论"的续解任务指向它们。</description></item>
        /// </list>
        /// </summary>
        private async Task<ChainFixture> BuildChainFixtureAsync(Harness harness, RootState state)
        {
            string source = CreateSourceFile("outer.7z");
            ArchiveTask root = AddTask(harness, source);

            harness.Engine.FileNames = new[] { ContentFileName };

            if (state == RootState.Failed)
            {
                harness.Engine.ExtractFailure = new ArchiveOperationResult
                {
                    Success = false,
                    Status = StatusText.Corrupted,
                    Message = "文件损坏",
                    DetectedErrorType = "Corrupted"
                };
            }
            else if (state == RootState.VerifyFailed)
            {
                /*
                 * 引擎声明落盘 99 个条目、实际只写出 1 个 → 输出校验不通过
                 * （这条路的真实现场见 SourcePackageRestMoveTests.形状B_内容物校验没过_源包留在原地）。
                 * 这一档的 Outcome 仍是 Succeeded，只有校验枚举不通过 —— 正好考"两个条件必须同时成立"。
                 */
                harness.Engine.ExpectedFileCountOverride = 99;
            }

            await harness.Coordinator.StartExtractForOneClickAsync();

            bool rootPassed = root.Outcome == TaskOutcome.Succeeded &&
                              root.OutputVerification == OutputVerificationOutcome.Passed;

            Assert.True(
                state == RootState.Succeeded ? rootPassed : !rootPassed,
                $"前提：这一档的实际终态是 Outcome={root.Outcome} / 校验={root.OutputVerification}，与用例的前提不符");

            /*
             * ⚠ 前提：根任务必须走"当场搬 / 不延期"那一支。记成 DeferredToChainEnd 的根任务在链尾会走
             * 补搬分支，**根本不进"收内层包"这一段** —— 那样这两条用例就静默地什么都没测到。
             */
            Assert.NotEqual(SourcePackageMoveState.DeferredToChainEnd, root.SourcePackageMove);
            Assert.False(string.IsNullOrWhiteSpace(root.OutputPath), "前提：根任务的最终输出目录必须已经定了");

            // 外层解出来、这一轮没跑成的内层分卷组：直接写进成品目录（真机上它们就是外层包的内容物）。
            Directory.CreateDirectory(root.OutputPath);

            string[] volumes = new[] { "inner.7z.001", "inner.7z.002", "inner.7z.003" }
                .Select(name => Path.Combine(root.OutputPath, name))
                .ToArray();

            for (int i = 0; i < volumes.Length; i++)
            {
                // 末卷更小：真分卷组就是这个形状。
                File.WriteAllBytes(volumes[i], new byte[i == volumes.Length - 1 ? 512 : 1024]);
            }

            var continuation = new ArchiveTask(volumes[0], harness.Vm.Tasks.Count + 1)
            {
                IsArchive = true,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal,
                Status = StatusText.Recognized,
                IsSelected = false,

                // IsContinuationTask 的唯一判据（内层包就是这么带着父任务落点走的）。
                ParentOutputDirectory = root.OutputPath,
                ParentTaskName = root.FileName,

                // 内层包本身是分卷组：链尾取清单读的就是 VolumePaths。
                IsVolumeGroup = true,
                VolumeGroupKey = Path.Combine(root.OutputPath, "inner") + "|inner"
            };

            foreach (string volume in volumes)
            {
                continuation.VolumePaths.Add(volume);
            }

            harness.Vm.Tasks.Add(continuation);

            /*
             * ⚠ 刻意**不设** Outcome / OutputVerification：默认 Pending / NotAttempted 就是"这一轮根本没跑成"
             * 的机器状态（真机那两个内层卷正是这样，见 AGENTS.md §11 的 amb909 那一次）。
             */
            Assert.Equal(TaskOutcome.Pending, continuation.Outcome);
            Assert.Equal(OutputVerificationOutcome.NotAttempted, continuation.OutputVerification);

            return new ChainFixture
            {
                Root = root,
                UnrunContinuation = continuation,
                VolumePaths = volumes,

                // 其余物目录以根任务记下的那一条为准（定稿时可能因为撞名变成「其余物(1)」）；
                // 没记过就按链尾那套算法回落。
                RestDirectory = string.IsNullOrWhiteSpace(root.RestDirectoryPath)
                    ? Path.Combine(root.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName)
                    : root.RestDirectoryPath
            };
        }

        /// <summary>某个目录下（含子目录）文件名以指定前缀开头的全部文件，按路径排序（"有没有第二份副本"要用它）。</summary>
        private static string[] FilesNamedUnder(string directory, string fileNamePrefix)
        {
            if (!Directory.Exists(directory))
            {
                return Array.Empty<string>();
            }

            return Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).StartsWith(fileNamePrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        // ================================================================ 装配

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

            /*
             * 源包进其余物：根任务收尾时就会"成功 + 校验通过 + 当场把源包搬进其余物（Done）"，
             * 链尾走的是补收内层包那条路（而不是延期补搬那条）。
             */
            settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
            settingsService.Save(settings);

            var engine = new FakeEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            // MainViewModel 的构造会顺手写两个进程级静态：先存后还原（与其它管线测试同一套做法）。
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
                vm, engine, passwordService, pathService, new DialogService());

            // 这些用例拿「细节日志」当行为证据（第 44 条之后，成功时默认只留摘要行）。
            coordinator.KeepTaskDetailInLog = true;

            return new Harness(vm, engine, coordinator, logService, outputRoot);
        }

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

            /// <summary>本测试实例的输出根（"整个输出根下只有一份"这类断言要用它递归找）。</summary>
            public string OutputRoot { get; }

            /// <summary>屏幕日志的文本（无 WPF 应用的测试进程里照样会被填充）。</summary>
            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);
        }

        private string CreateSourceFile(string fileName)
        {
            string directory = Path.Combine(_root, "src");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, fileName);

            // 引擎是假的，内容是什么都无所谓。
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

        /// <summary>
        /// 可控的假引擎（与 <c>SourcePackageRestMoveTests</c> 的同一套）：解压时往引擎输出目录
        /// （= 暂存目录）写 <see cref="FileNames"/> 里的那些文件，列目录返回同样的条目
        /// （于是输出校验能通过）；三个钩子用来造"外层失败 / 校验没过"的形状。
        /// </summary>
        private sealed class FakeEngine : IArchiveEngine
        {
            /// <summary>解压时写出的文件名（也是列目录返回的条目）。</summary>
            public IReadOnlyList<string> FileNames { get; set; } = new[] { ContentFileName };

            /// <summary>非 null = 解压直接返回这个失败结论（用来验"外层失败时一个字节都不搬"）。</summary>
            public ArchiveOperationResult? ExtractFailure { get; set; }

            /// <summary>列目录时报出的条目数；比真实产物多（校验就会不通过）。</summary>
            public int? ExpectedFileCountOverride { get; set; }

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
                IReadOnlyList<string> fileNames = FileNames;
                int fileCount = ExpectedFileCountOverride ?? fileNames.Count;

                return Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    FileCount = fileCount,
                    TotalUncompressedSize = 0,
                    Entries = Enumerable.Range(0, fileCount)
                        .Select(i => new ArchiveEntry
                        {
                            Path = i < fileNames.Count ? fileNames[i] : $"extra-{i:D5}.bin",
                            Size = 1
                        })
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
                if (ExtractFailure != null)
                {
                    return Task.FromResult(ExtractFailure);
                }

                string output = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);

                    foreach (string name in FileNames)
                    {
                        File.WriteAllText(Path.Combine(output, name), "x");
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
    }
}

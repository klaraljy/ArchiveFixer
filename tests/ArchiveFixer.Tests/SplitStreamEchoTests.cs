using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **「只出了一个与源包等大的垃圾」不许报成功**（用户 2026-09-28 红检里实测到的假成功）。
    ///
    /// <para><b>现象</b>：**单个**被改烂名字的 7z 第一卷（同目录里没有能配对的后续卷）会被 7-Zip
    /// 当成「通用分片流（Split）」解掉 —— 退出码 0、只解出与这一卷等大的垃圾，
    /// 而老口径下"预期 1 条 / 实际 1 个文件、字节数也对得上"→ 判**通过** → 报「解压成功」，
    /// 输出目录里根本没有包里的内容物。这违反红线「部分成功不得显示为成功」。</para>
    ///
    /// <para>这一组钉三层：①写盘前那道闸门认得出这种形状（<c>amb909.7.01</c> 这种**两段数字**的名字
    /// 老判据认不出来）；②收尾时量产物形状的**最后一道闸门**认得出来；③真 7z + 真管线跑一遍 ——
    /// 不报成功、源包一个字节不动、输出目录里不留垃圾当内容物，而**正常完整包照旧成功**（别误伤）。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class SplitStreamEchoTests : IDisposable
    {
        private const int PayloadBytes = 2621440; // 2.5 MiB（-v1m 切出来是两满卷 + 一个尾卷）

        private readonly string _root;

        public SplitStreamEchoTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSplitEcho", Guid.NewGuid().ToString("N"));
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
                // 清不掉只是脏一点。
            }
        }

        // ================================================================ ① 写盘前那道闸门

        [Fact]
        public void 单个伪装第一卷_两段数字的名字也要判成坏链()
        {
            /*
             * 真机形状：`amb909.7z.001` 被改成 `amb909.7.01`。7-Zip 的通用分片处理器按"末尾一段纯数字"
             * 认盘号，于是把它当第 1 片、拼出条目 `amb909.7`（= 文件名去掉那一段），大小 = 这一卷本身。
             * ⛔ 老判据要求"名字能被 VolumeGroupDetector 认出卷号（三位数那套）"，`.01` 认不出来 → 放行。
             */
            string path = CreateFile(Path.Combine(_root, "mangle"), "amb909.7.01", 1024 * 1024);

            ArchiveListResult listing = new()
            {
                Success = true,
                FileCount = 1,
                TotalUncompressedSize = 1048576,
                IsRawSplitStream = true,
                Entries = new List<ArchiveEntry> { new() { Path = "amb909.7", Size = 1048576 } }
            };

            Assert.True(
                RawSplitStreamDetector.IsBrokenVolumeChain(listing, path, true, 1),
                "这就是红检逮到的那一档：不拦的话会解出一个与源包等大的垃圾、还报「解压成功」");

            // 组信息没拿到（VolumeCount = 0）时同样要拦得住。
            Assert.True(RawSplitStreamDetector.IsBrokenVolumeChain(listing, path, true, 0));
        }

        [Fact]
        public void 卷齐或内容不是归档时_绝不误拦()
        {
            string directory = Path.Combine(_root, "negatives");
            string path = CreateFile(directory, "amb909.7.01", 1024 * 1024);

            ArchiveListResult echo = new()
            {
                Success = true,
                FileCount = 1,
                TotalUncompressedSize = 1048576,
                IsRawSplitStream = true,
                Entries = new List<ArchiveEntry> { new() { Path = "amb909.7", Size = 1048576 } }
            };

            // 卷齐（一组多卷都在）：拼起来正是用户要的东西。
            Assert.False(RawSplitStreamDetector.IsBrokenVolumeChain(echo, path, true, 3));

            // 内容不是归档（视频被切成 X.001 那种）：通用分片本身是合法的。
            Assert.False(RawSplitStreamDetector.IsBrokenVolumeChain(echo, path, false, 1));

            // 名字末尾没有可分片号 → 7-Zip 不会这么认它。
            string plain = CreateFile(directory, "amb909.bin", 1024 * 1024);
            Assert.False(RawSplitStreamDetector.IsBrokenVolumeChain(echo, plain, true, 1));

            // 条目名不是"文件自己"（真的是归档里的东西）。
            ArchiveListResult realListing = new()
            {
                Success = true,
                FileCount = 1,
                TotalUncompressedSize = 4096,
                IsRawSplitStream = true,
                Entries = new List<ArchiveEntry> { new() { Path = "payload.bin", Size = 4096 } }
            };

            Assert.False(RawSplitStreamDetector.IsBrokenVolumeChain(realListing, path, true, 1));
        }

        // ================================================================ ② 收尾那道闸门

        [Fact]
        public void 产物只有一个与源包等大的同名垃圾_认得出()
        {
            string source = CreateFile(Path.Combine(_root, "echo"), "amb909.7.01", 1_048_576);
            string stage = Path.Combine(_root, "echo", "stage");
            Directory.CreateDirectory(stage);

            // 7-Zip 的产物：名字 = 源包名去掉末尾那段分片号，大小 = 源包本身。
            File.WriteAllBytes(Path.Combine(stage, "amb909.7"), new byte[1_048_576]);

            Assert.True(RawSplitStreamDetector.LooksLikeSplitStreamEcho(source, true, stage));
        }

        [Fact]
        public void 收尾闸门_形状差一点就不判()
        {
            string source = CreateFile(Path.Combine(_root, "echo-neg"), "amb909.7.01", 1_048_576);

            // ① 多了一个文件
            string twoFiles = Path.Combine(_root, "echo-neg", "stage-two");
            Directory.CreateDirectory(twoFiles);
            File.WriteAllBytes(Path.Combine(twoFiles, "amb909.7"), new byte[1_048_576]);
            File.WriteAllBytes(Path.Combine(twoFiles, "notes.txt"), new byte[8]);
            Assert.False(RawSplitStreamDetector.LooksLikeSplitStreamEcho(source, true, twoFiles));

            // ② 多了一层目录
            string withDirectory = Path.Combine(_root, "echo-neg", "stage-dir");
            Directory.CreateDirectory(Path.Combine(withDirectory, "inner"));
            File.WriteAllBytes(Path.Combine(withDirectory, "amb909.7"), new byte[1_048_576]);
            Assert.False(RawSplitStreamDetector.LooksLikeSplitStreamEcho(source, true, withDirectory));

            // ③ 名字不是"源包去掉末尾那段分片号"
            string otherName = Path.Combine(_root, "echo-neg", "stage-name");
            Directory.CreateDirectory(otherName);
            File.WriteAllBytes(Path.Combine(otherName, "data.bin"), new byte[1_048_576]);
            Assert.False(RawSplitStreamDetector.LooksLikeSplitStreamEcho(source, true, otherName));

            // ④ 大小对不上（真的解出了别的东西）
            string otherSize = Path.Combine(_root, "echo-neg", "stage-size");
            Directory.CreateDirectory(otherSize);
            File.WriteAllBytes(Path.Combine(otherSize, "amb909.7"), new byte[4096]);
            Assert.False(RawSplitStreamDetector.LooksLikeSplitStreamEcho(source, true, otherSize));

            // ⑤ 源包内容不是归档 → 通用分片是合法的，不判
            Assert.False(RawSplitStreamDetector.LooksLikeSplitStreamEcho(source, false, Path.Combine(_root, "echo-neg", "stage-two")));

            // ⑥ 源包名字末尾没有分片号
            string plain = CreateFile(Path.Combine(_root, "echo-neg"), "amb909.bin", 1_048_576);
            Assert.False(RawSplitStreamDetector.LooksLikeSplitStreamEcho(plain, true, Path.Combine(_root, "echo-neg", "stage-two")));

            // ⑦ 暂存目录不在 / 传空
            Assert.False(RawSplitStreamDetector.LooksLikeSplitStreamEcho(source, true, Path.Combine(_root, "echo-neg", "不存在")));
            Assert.False(RawSplitStreamDetector.LooksLikeSplitStreamEcho(source, true, null));
        }

        // ================================================================ ③ 假引擎：列不出清单那条岔路

        /// <summary>
        /// **收尾闸门真正扛事的场合**：引擎**列不出清单**（加密头 / 直读 / 抠取这些岔路）时，
        /// 老口径只做"输出目录非空"的底线校验 —— 于是"解出一个与源包等大的垃圾"会被判**通过**、
        /// 报「解压成功」。这条闸门就是补这个洞的。
        ///
        /// <para>写盘前那道闸门在这条路上**根本不会触发**（它要引擎列得出清单），所以这一条专项钉住收尾那道。</para>
        /// </summary>
        [Fact]
        public async Task 列不出清单但只解出等大垃圾_收尾那一步必须识破且不报成功()
        {
            Harness harness = CreateHarness(new EchoEngine(), moveSourceToRest: true);
            string source = harness.CreateSourceFile("amb909.7.01", 1_048_576);
            ArchiveTask task = harness.AddTask(source);

            await harness.Coordinator.StartExtractAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.Equal(StatusText.ExtractFailed, task.Status);
            Assert.Contains("通用分片", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Equal(OutputVerificationOutcome.Failed, task.OutputVerification);
            Assert.NotEqual(TaskOutcome.Succeeded, task.Outcome);

            // 三条不可逆/误导动作一条都不许发生。
            Assert.True(File.Exists(source), "源包被搬走或删掉了");
            Assert.Empty(Directory.GetFiles(harness.OutputRoot, "*", SearchOption.AllDirectories));
            Assert.False(Directory.Exists(Path.Combine(source, ProcessArtifactLayout.ArtifactDirectoryName)));
        }

        [Fact]
        public async Task 列不出清单但解出了别的东西_照旧成功_别误伤()
        {
            // 反向守门：同一个"列不出清单"的岔路，若产物是别的东西（名字/大小都不是源包自己）就该照旧成功。
            Harness harness = CreateHarness(new EchoEngine { ProduceEcho = false }, moveSourceToRest: false);
            string source = harness.CreateSourceFile("amb909.7.01", 4096);
            ArchiveTask task = harness.AddTask(source);

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
        }

        // ================================================================ ④ 真 7z + 真管线

        [SevenZipFact]
        public async Task 真七z_单个被改烂名字的第一卷_不许报成功也不许动源包()
        {
            /*
             * 现场复原：三卷 7z 的**第一卷**被改成 `amb909.7.01`，后续两卷不在（"没有可配对的后续卷"）。
             * 老行为：7-Zip 把它当通用分片、退出码 0、解出 1 MiB 垃圾，管线报「解压成功」。
             * 现在必须：不报成功（分卷缺失 / 失败）、源包原地不动、其余物不生成、输出目录里没有垃圾。
             */
            string source = NewDirectory("mangled-only-first");
            MakePayload(source);

            Run7z(source, "a", "-t7z", "-mx0", "-v1m", "amb909.7z", "data.bin");

            string first = Path.Combine(source, "amb909.7.01");
            File.Move(Path.Combine(source, "amb909.7z.001"), first);

            // 后续两卷拿掉 —— 这才是"单个伪装的第一卷"（组里只有自己一卷）。
            File.Delete(Path.Combine(source, "amb909.7z.002"));
            File.Delete(Path.Combine(source, "amb909.7z.003"));

            byte[] sourceBytesBefore = File.ReadAllBytes(first);

            Harness harness = CreateHarness(moveSourceToRest: true);
            ArchiveTask task = await AddTaskAsync(harness, first);

            await harness.Coordinator.StartExtractAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.NotEqual(TaskOutcome.Succeeded, task.Outcome);

            // 源包一个字节都没动（"不报成功"与"不许触发不可逆动作"是同一件事的两面）。
            Assert.True(File.Exists(first), "源包被搬走或删掉了");
            Assert.Equal(sourceBytesBefore, File.ReadAllBytes(first));

            // 其余物没生成（源包搬运的前提是"校验通过"，这里必须没通过）。
            Assert.False(
                Directory.Exists(Path.Combine(source, ProcessArtifactLayout.ArtifactDirectoryName)),
                "不该在源目录生成其余物目录");

            Assert.Empty(Directory.GetDirectories(
                harness.OutputRoot,
                ProcessArtifactLayout.ArtifactDirectoryName,
                SearchOption.AllDirectories));

            // 输出目录里没有把垃圾当内容物：既没有那个"与源包等大的垃圾"，也没有任何内容物。
            Assert.Empty(Directory.GetFiles(harness.OutputRoot, "*", SearchOption.AllDirectories));

            // 失败原因要写清楚（不是默默跳过）。
            Assert.False(string.IsNullOrWhiteSpace(task.ErrorMessage));
            Assert.Contains("分卷", task.ErrorMessage, StringComparison.Ordinal);
        }

        [SevenZipFact]
        public async Task 真七z_正常完整包_照旧成功_别误伤()
        {
            string source = NewDirectory("complete");
            byte[] payload = MakePayload(source);

            Run7z(source, "a", "-t7z", "-mx0", "complete.7z", "data.bin");

            string archive = Path.Combine(source, "complete.7z");

            Harness harness = CreateHarness(moveSourceToRest: true);
            ArchiveTask task = await AddTaskAsync(harness, archive);

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

            string[] found = Directory.GetFiles(harness.OutputRoot, "data.bin", SearchOption.AllDirectories);
            Assert.True(found.Length > 0, "正常包没解出内容物");
            Assert.Equal(payload, File.ReadAllBytes(found[0]));

            // 完整包不该被新闸门误判：源包照旧按设置搬进其余物（其余物在**输出目录**里，契约 §3.2）。
            Assert.False(File.Exists(archive), "完整包成功后源包应当已按设置移入其余物");

            string[] rest = Directory.GetDirectories(
                harness.OutputRoot,
                ProcessArtifactLayout.ArtifactDirectoryName,
                SearchOption.AllDirectories);

            Assert.True(rest.Length > 0, "正常完整包应当照旧生成其余物目录");
            Assert.True(
                Directory.GetFiles(rest[0], "complete.7z", SearchOption.AllDirectories).Length > 0,
                "源包没进其余物");
        }

        // ── 样本与断言 ──

        private byte[] MakePayload(string directory)
        {
            var payload = new byte[PayloadBytes];
            new Random(20260928).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(directory, "data.bin"), payload);

            return payload;
        }

        private string NewDirectory(string name)
        {
            string directory = Path.Combine(_root, name);
            Directory.CreateDirectory(directory);

            return directory;
        }

        private static string CreateFile(string directory, string fileName, int size)
        {
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, fileName);
            File.WriteAllBytes(path, new byte[size]);

            return path;
        }

        // ── 真 7z ──

        private void Run7z(string workingDirectory, params string[] args)
        {
            string sevenZip = SevenZipFactAttribute.LocateSevenZipPath();

            var psi = new ProcessStartInfo(sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(120_000), "7z 超时");
            Assert.True(process.ExitCode == 0, $"7z 失败：{stdout}{stderr}");
        }

        // ── 管线 ──

        private static async Task<ArchiveTask> AddTaskAsync(Harness harness, string path)
        {
            var task = new ArchiveTask(path, harness.Vm.Tasks.Count + 1) { IsSelected = true };

            await new ArchiveDetectService().ApplyDetectResultAsync(task);
            task.CaptureSourceSnapshot();
            harness.Vm.Tasks.Add(task);

            return task;
        }

        private Harness CreateHarness(bool moveSourceToRest) =>
            CreateHarness(new Engines.SevenZip.SevenZipEngine(), moveSourceToRest);

        private Harness CreateHarness(IArchiveEngine engine, bool moveSourceToRest)
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
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;
            settings.MaxParallelExtractCount = 1;

            // 源包档位刻意取默认的「放入其余物」：真成功了源包就会被搬走 ——
            // 这样"源包还在原地"才是一条有意义的断言（红线：不许触发不可移动作）。
            settings.SourceHandling = moveSourceToRest
                ? nameof(SourceHandlingMode.MoveToRest)
                : nameof(SourceHandlingMode.KeepInPlace);

            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            settingsService.Save(settings);

            var logService = new LogService(pathService);

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                engine,
                new PasswordService(),
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new ConfirmDialogService());

            var coordinator = new ExtractionCoordinator(
                vm,
                engine,
                new PasswordService(),
                pathService,
                new ConfirmDialogService());

            coordinator.KeepTaskDetailInLog = true;

            return new Harness(vm, coordinator, logService, outputRoot, dataRoot);
        }

        /// <summary>
        /// 模拟"7-Zip 把单个伪装的第一卷当通用分片拼了一遍"的假引擎：**列不出清单**（这条岔路才考得到收尾那道闸门），
        /// 解压却"成功"地写出一个与源包等大的文件、名字正好是源包名去掉末尾那段分片号。
        /// </summary>
        private sealed class EchoEngine : IArchiveEngine
        {
            /// <summary>false = 写一个正常的小产物（反向守门用）。</summary>
            public bool ProduceEcho { get; init; } = true;

            public string Id => "echo";

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

            public Task<ArchiveProbeResult> ProbeAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });

            public Task<ArchiveListResult> ListAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveListResult
                {
                    Success = false,
                    Message = "列不出清单（本用例模拟加密头 / 直读那条岔路）"
                });

            public Task<ArchiveOperationResult> TestAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveOperationResult { Success = true, DetectedErrorType = "None" });

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                string output = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);

                    if (ProduceEcho)
                    {
                        string sourceName = Path.GetFileName(request.ArchivePath ?? string.Empty);

                        // 7-Zip 通用分片的产物名 = 源包名去掉末尾那段纯数字分片号。
                        string stripped = sourceName[..sourceName.LastIndexOf('.')];
                        long size = new FileInfo(request.ArchivePath!).Length;

                        File.WriteAllBytes(Path.Combine(output, stripped), new byte[size]);
                    }
                    else
                    {
                        File.WriteAllBytes(Path.Combine(output, "payload.bin"), new byte[16]);
                    }
                }

                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = true,
                    Status = StatusText.ExtractSuccess,
                    Message = "解压成功",
                    DetectedErrorType = "None"
                });
            }
        }

        private sealed class ConfirmDialogService : DialogService
        {
            public override bool ShowConfirm(
                string message,
                string optionText,
                bool optionCheckedByDefault,
                string detail,
                out bool optionChecked)
            {
                optionChecked = optionCheckedByDefault;
                return true;
            }
        }

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                ExtractionCoordinator coordinator,
                LogService log,
                string outputRoot,
                string dataRoot)
            {
                Vm = vm;
                Coordinator = coordinator;
                Log = log;
                OutputRoot = outputRoot;
                DataRoot = dataRoot;
            }

            public MainViewModel Vm { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            public string OutputRoot { get; }

            public string DataRoot { get; }

            /// <summary>造一个指定大小的源文件（大小要对得上"等大垃圾"的判据）。</summary>
            public string CreateSourceFile(string fileName, int size)
            {
                string directory = Path.Combine(DataRoot, "..", "src");
                Directory.CreateDirectory(directory);

                string path = Path.Combine(directory, fileName);
                File.WriteAllBytes(path, new byte[size]);

                return path;
            }

            /// <summary>
            /// 直接建任务（不跑识别服务）：本组要考的是**结论与不可逆动作**，
            /// 而"这是不是一个归档"由内容级识别另有用例钉着。
            /// </summary>
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

                task.CaptureSourceSnapshot();
                Vm.Tasks.Add(task);

                return task;
            }
        }
    }
}

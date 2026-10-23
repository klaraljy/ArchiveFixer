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



        // ================================================================ ② 收尾那道闸门



        // ================================================================ ③ 假引擎：列不出清单那条岔路



        // ================================================================ ④ 真 7z + 真管线



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
            settings.AutoScanAfterDrop = false;
            settings.MaxParallelExtractCount = 1;

            // 源包档位：需要验证“成功后源包不在了”的用例选「彻底删除」（就地删，不搬其余物）。
            settings.SourceHandling = moveSourceToRest
                ? nameof(SourceHandlingMode.DeleteInPlace)
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

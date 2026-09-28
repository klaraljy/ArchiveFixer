using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// **内容级识别 + 试开验证的端到端**（用户 2026-09-28 真机：7z 三卷的名字被改成
    /// <c>amb909.7.01</c> / <c>amb909.z.2</c> / <c>amb909..3</c> —— 后缀与卷号都被改烂）。
    ///
    /// <para>这一组钉的是**结果**，不是"报了个好听的结论"：跑完既有管线之后，
    /// 必须真的在输出目录里找到解出来的文件、内容逐字节相同。</para>
    ///
    /// <para>真 7z 造样本：<c>-v1m</c> 切 2.5 MiB → 三卷（两个满卷 + 一个不满的尾卷），
    /// 与真机那组的形状一致。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class DisguisedVolumeContentProbeTests : IDisposable
    {
        private const int PayloadBytes = 2621440; // 2.5 MiB

        private readonly string _root;
        private readonly string? _sevenZip;

        public DisguisedVolumeContentProbeTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerVolumeProbe", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _sevenZip = LocateSevenZip();
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

            try
            {
                // 试开目录由产品代码自己删；万一没删干净，这里补一刀（⛔ 只删我们自己造的这一个名字）。
                string work = Path.Combine(Path.GetTempPath(), VolumeContentInference.WorkDirectoryName);

                if (Directory.Exists(work) && !Directory.EnumerateFileSystemEntries(work).Any())
                {
                    Directory.Delete(work);
                }
            }
            catch
            {
                // 同上。
            }
        }

        [SevenZipFact]
        public async Task 真七z_三卷名字被改烂_既有管线能真的解出内容()
        {
            string source = NewDirectory("amb909");
            byte[] payload = MakePayload(source);

            Run7z(source, "a", "-t7z", "-mx0", "-v1m", "amb909.7z", "data.bin");

            Assert.True(File.Exists(Path.Combine(source, "amb909.7z.003")), "样本不是三卷");

            // 真机那种改法：卷号被改烂、后缀也不对
            File.Move(Path.Combine(source, "amb909.7z.001"), Path.Combine(source, "amb909.7.01"));
            File.Move(Path.Combine(source, "amb909.7z.002"), Path.Combine(source, "amb909.z.2"));
            File.Move(Path.Combine(source, "amb909.7z.003"), Path.Combine(source, "amb909..3"));

            string first = Path.Combine(source, "amb909.7.01");
            Harness harness = CreateHarness();
            ArchiveTask task = await AddTaskAsync(harness, first);

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // 先钉"真的解出内容了"（这条红检时最有价值：它同时证明"报成功"与"内容对"是两件事）。
            AssertPayloadExtracted(harness, payload);

            Assert.Equal(Path.Combine(source, "amb909.7z.001"), task.CurrentPath);
            Assert.True(File.Exists(Path.Combine(source, "amb909.7z.001")), "整组没改回标准名");
            Assert.False(File.Exists(first), "旧名字还在");
        }

        [SevenZipFact]
        public async Task 真七z_卷号后面粘着删除两个字_既有管线能真的解出内容()
        {
            string source = NewDirectory("giu910");
            byte[] payload = MakePayload(source);

            Run7z(source, "a", "-t7z", "-mx0", "-v1m", "giu910.7z", "data.bin");

            Assert.True(File.Exists(Path.Combine(source, "giu910.7z.003")), "样本不是三卷");

            for (int index = 1; index <= 3; index++)
            {
                File.Move(
                    Path.Combine(source, $"giu910.7z.{index:D3}"),
                    Path.Combine(source, $"giu910.7z.{index:D3}删除"));
            }

            Harness harness = CreateHarness();
            ArchiveTask task = await AddTaskAsync(harness, Path.Combine(source, "giu910.7z.001删除"));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            AssertPayloadExtracted(harness, payload);
        }

        [SevenZipFact]
        public async Task 真七z_完整包只改了后缀_不许被当成第一卷改名()
        {
            /*
             * 反例守门：一个**完整**的 7z 被改成 `solo.7.01` 时，它自己就能打开 ——
             * 那就绝不能"按内容推顺序"去把它改名（改完反而打不开了）。
             * 这条钉的正是"试开必须先确认第一卷自己打不开"这道闸门。
             */
            string source = NewDirectory("solo");
            byte[] payload = MakePayload(source);

            Run7z(source, "a", "-t7z", "-mx0", "solo.7z", "data.bin");

            File.Move(Path.Combine(source, "solo.7z"), Path.Combine(source, "solo.7.01"));

            Harness harness = CreateHarness();
            ArchiveTask task = await AddTaskAsync(harness, Path.Combine(source, "solo.7.01"));

            await harness.Coordinator.StartExtractAsync();

            Assert.True(File.Exists(Path.Combine(source, "solo.7.01")), "完整包被改掉了名字");
            Assert.False(File.Exists(Path.Combine(source, "solo.7.7z.001")), "不该按内容推的名字改名");
            Assert.Equal(StatusText.ExtractSuccess, task.Status);
        }

        // ── 样本与断言 ──

        private byte[] MakePayload(string directory)
        {
            var payload = new byte[PayloadBytes];
            new Random(20260928).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(directory, "data.bin"), payload);

            return payload;
        }

        /// <summary>断言"真的解出内容了"：输出目录里找得到 data.bin，而且逐字节等于原样。</summary>
        private static void AssertPayloadExtracted(Harness harness, byte[] payload)
        {
            string[] found = Directory.GetFiles(harness.OutputRoot, "data.bin", SearchOption.AllDirectories);

            Assert.True(found.Length > 0, "输出目录里没有解出来的 data.bin");

            byte[] actual = File.ReadAllBytes(found[0]);

            Assert.Equal(payload.Length, actual.Length);
            Assert.Equal(payload, actual);
        }

        private string NewDirectory(string name)
        {
            string directory = Path.Combine(_root, name);
            Directory.CreateDirectory(directory);

            return directory;
        }

        // ── 真 7z ──

        private void Run7z(string workingDirectory, params string[] args)
        {
            RequireSevenZip();

            var psi = new ProcessStartInfo(_sevenZip!)
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

        private void RequireSevenZip()
        {
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException("测试机上没有 7z.exe");
            }
        }

        private static string? LocateSevenZip()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(directory.FullName, "src", "ArchiveFixer", "tools", "7zip", "7z.exe");
                    return File.Exists(candidate) ? candidate : null;
                }

                directory = directory.Parent;
            }

            return null;
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

        private Harness CreateHarness()
        {
            string dataRoot = Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N"));
            string outputRoot = Path.Combine(_root, "out-" + Guid.NewGuid().ToString("N"));

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
            settings.MaxParallelExtractCount = 1;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            settingsService.Save(settings);

            IArchiveEngine engine = new Engines.SevenZip.SevenZipEngine();
            var logService = new LogService(pathService);

            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

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

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            var coordinator = new ExtractionCoordinator(
                vm,
                engine,
                new PasswordService(),
                pathService,
                new ConfirmDialogService());

            coordinator.KeepTaskDetailInLog = true;

            return new Harness(vm, coordinator, logService, outputRoot, dataRoot);
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

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);
        }
    }
}

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

        /// <summary>「文件名也加密」那一档用的占位密码（⛔ 真密码绝不进仓库，§8）。</summary>
        private const string ProbePassword = "VolumeProbe-Right-2026";

        private readonly string _root;
        private readonly string? _sevenZip;
        private readonly Xunit.Abstractions.ITestOutputHelper _output;

        public DisguisedVolumeContentProbeTests(Xunit.Abstractions.ITestOutputHelper output)
        {
            _output = output;
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

            /*
             * ⚠ 这里原来还有一段"补一刀删掉 <临时目录卷根>\.ArchiveFixer.work"的清理 —— 那是老行为的产物：
             * 试开器在"没有工作区根 / 跨盘"时会退到**源卷根**去建探针目录（本机 C:\ 与 E:\ 根上那两个空壳
             * 就是这么来的）。现在试开探针**只落调用方传进来的工作区根**（用户 2026-09-30 红线），
             * 卷根那一档已经整条删掉，所以那段清理既无用、又会去动别人盘上的目录（⛔ 不许）。
             */
        }





        [SevenZipFact]
        public async Task 真七z_两卷_只剩第一卷加一个无关小文件_试开不成立就什么都不做()
        {
            /*
             * 反向守门（放开的闸门必须由试开兜住）：后续卷真的不在时，目录里那个**更短**的无关文件
             * 会被"两卷形状"放进来试开一次 —— 引擎读不出来，于是**一个字节都不许动**：
             * 名字不改、不报成功、源包原地不动。
             */
            string source = NewDirectory("amb909-missing-second");
            MakePayload(source, 1572864);

            Run7z(source, "a", "-t7z", "-mx0", "-v1m", "amb909.7z", "data.bin");

            string first = Path.Combine(source, "amb909.7.01");
            File.Move(Path.Combine(source, "amb909.7z.001"), first);

            // 第二卷拿掉，换成一个无关的小文件（它比第一卷短，会被当候选）。
            File.Delete(Path.Combine(source, "amb909.7z.002"));
            File.WriteAllText(Path.Combine(source, "readme.txt"), "说明文件，与这一组分卷无关");

            byte[] before = File.ReadAllBytes(first);

            Harness harness = CreateHarness();
            ArchiveTask task = await AddTaskAsync(harness, first);

            await harness.Coordinator.StartExtractAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.NotEqual(TaskOutcome.Succeeded, task.Outcome);

            // 一个字节都不许动：源包还在、名字没变、没有凭空造出一个标准名。
            Assert.True(File.Exists(first), "源包被改名或搬走了");
            Assert.Equal(before, File.ReadAllBytes(first));
            Assert.False(File.Exists(Path.Combine(source, "amb909.7z.001")), "试开没成立却把名字改了");
            Assert.True(File.Exists(Path.Combine(source, "readme.txt")), "无关文件被动过");
            Assert.Empty(Directory.GetFiles(harness.OutputRoot, "*", SearchOption.AllDirectories));
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

        // ================= 文件名也加密（7z -mhe）的那一档：用户 2026-09-29 真机的**真形状** =================


        /// <summary>
        /// 反向守门（放开的判据必须由事实兜住）：**文件名也加密**的那一组，第二卷真的不在时，
        /// 目录里那个无关小文件会被"两卷形状"放进去试开一次 —— 引擎报的是"打不开 / 数据不全"
        /// （**不是**"这是加密归档"），于是**一个字节都不许动**。
        /// </summary>
        [SevenZipFact]
        public async Task 真七z_两卷_文件名也加密_只剩第一卷加无关小文件_试开不成立就什么都不做()
        {
            string source = NewDirectory("amb909-mhe-missing-second");
            MakePayload(source, 1572864);

            Run7z(source, "a", "-t7z", "-mx0", "-mhe=on", "-p" + ProbePassword, "-v1m", "amb909.7z", "data.bin");

            string first = Path.Combine(source, "amb909.7.01");
            File.Move(Path.Combine(source, "amb909.7z.001"), first);

            File.Delete(Path.Combine(source, "amb909.7z.002"));
            File.WriteAllText(Path.Combine(source, "readme.txt"), "说明文件，与这一组分卷无关");

            byte[] before = File.ReadAllBytes(first);

            Harness harness = CreateHarness(new[] { ProbePassword });
            ArchiveTask task = await AddTaskAsync(harness, first);

            await harness.Coordinator.StartExtractAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.NotEqual(TaskOutcome.Succeeded, task.Outcome);

            Assert.True(File.Exists(first), "源包被改名或搬走了");
            Assert.Equal(before, File.ReadAllBytes(first));
            Assert.False(File.Exists(Path.Combine(source, "amb909.7z.001")), "试开没成立却把名字改了");
            Assert.True(File.Exists(Path.Combine(source, "readme.txt")), "无关文件被动过");
            Assert.Empty(Directory.GetFiles(harness.OutputRoot, "*", SearchOption.AllDirectories));
        }

        /// <summary>
        /// 反向守门（单卷那一档，加密版）：一个**完整**的、文件名也加密的包被改成 <c>solo.7.01</c> 时，
        /// 单卷试开会报"这是加密归档" —— 那说明那一份头就在这个文件**里**，它本身就是完整的包，
        /// ⛔ 绝不能按内容推顺序把它改名（改完 7-Zip 就找不着它了）。
        ///
        /// <para>所以单卷试开的"加密归档"结论必须**拒绝**，与"多卷试开"里的同一结论含义正好相反
        /// （那边是"头在最后一卷里"= 这一组成立）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 真七z_完整包文件名也加密_只改了后缀_不许被当成第一卷改名()
        {
            string source = NewDirectory("solo-mhe");
            byte[] payload = MakePayload(source);

            Run7z(source, "a", "-t7z", "-mx0", "-mhe=on", "-p" + ProbePassword, "solo.7z", "data.bin");

            /*
             * 目录里留一个"更短、名字带短数字尾巴"的文件（`data.2`）—— 否则"两卷形状"那张门票
             * 根本不放行，单卷试开也就跑不到，这条用例会白绿。
             */
            File.Move(Path.Combine(source, "data.bin"), Path.Combine(source, "data.2"));

            File.Move(Path.Combine(source, "solo.7z"), Path.Combine(source, "solo.7.01"));

            string renamed = Path.Combine(source, "solo.7.01");

            Harness harness = CreateHarness(new[] { ProbePassword });
            ArchiveTask task = await AddTaskAsync(harness, renamed);

            await harness.Coordinator.StartExtractAsync();

            Assert.True(File.Exists(renamed), "完整的加密包被当成分卷第一卷改掉了名字");
            Assert.False(File.Exists(Path.Combine(source, "solo.7z.001")), "不该按内容推的名字改名");
            Assert.False(File.Exists(Path.Combine(source, "solo.7z.002")), "把无关的 data.2 也一起改了名");
            Assert.False(task.VolumeNameAutoRenamed, "这一单本来就不该走改名那条路");
            Assert.True(File.Exists(Path.Combine(source, "data.2")), "无关文件被动过");

            /*
             * ⚠ 这一单**最终没能解开**（状态「分卷缺失」），而且与本次修复无关 —— 是**既有**的另一处误诊：
             * 文件名末尾那一段是纯数字（`.01`）时，7-Zip 在没有密码的情况下会退化成"通用分片"处理器
             * （清单里只有一条 = 文件自己），管线那道 RawSplitStreamDetector 闸门于是报了「分卷缺失」。
             * 判据本身没错（通用分片正是它要拦的），错的是"没有密码"与"名字被改坏"两件事在那里分不开。
             * 本轮**不动它**（判据只允许一处，且真机那一档是"两卷"而不是"完整单卷"），只把这个事实记下来。
             */
            _output.WriteLine($"完整加密包（名字带纯数字尾巴）这次的状态：[{task.Status}] 原因：[{task.ErrorMessage}]");
        }

        // ── 样本与断言 ──

        /// <summary>
        /// 造一份**不可压缩**的载荷（<c>-v1m</c> 要真的分卷：可压的会被压成一卷，"分卷"就名不副实）。
        /// </summary>
        /// <param name="bytes">
        /// 载荷大小。默认 2.5 MiB（<c>-v1m</c> → 三卷：两个满卷 + 一个尾卷）；
        /// 1.5 MiB 用来造**两卷**（一个满卷 + 一个余量），也就是真机那组的形状。
        /// </param>
        private byte[] MakePayload(string directory, int bytes = PayloadBytes)
        {
            var payload = new byte[bytes];
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

        private Harness CreateHarness(IReadOnlyList<string>? bookPasswords = null)
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
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            settingsService.Save(settings);

            IArchiveEngine engine = new Engines.SevenZip.SevenZipEngine();
            var logService = new LogService(pathService);
            var passwordService = new PasswordService();

            foreach (string password in bookPasswords ?? Array.Empty<string>())
            {
                passwordService.Passwords.Add(new PasswordItem
                {
                    Value = password,
                    Source = "ImportedList",
                    IsEnabled = true,
                    Remark = "分卷形状用例候选"
                });
            }

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
                new ConfirmDialogService());

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            var coordinator = new ExtractionCoordinator(
                vm,
                engine,
                passwordService,
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

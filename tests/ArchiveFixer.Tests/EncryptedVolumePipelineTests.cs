using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **加密 + 分卷 + 密码本**的端到端（用户 2026-09-29 给的三套真包，内容**全是加密的**）。
    ///
    /// <para><b>为什么还要这一组</b>：真样本（<c>RealVolumeSampleTests</c>）的每个 mp4 都是
    /// <c>Encrypted = +</c>、空密码报 <c>Wrong password</c>，而任务里没有密码、程序也不许猜 ——
    /// 所以那三套只能验到"认组 / 定序 / 整组改回标准名 / 引擎真的打开了这一组"，
    /// **验不到最后一段**：拿对密码之后，磁盘上解出来的字节到底对不对。</para>
    ///
    /// <para>这一组自己造**加密的分卷**，名字按真机那样改烂，密码放进密码本，再走**既有管线**：
    /// <list type="number">
    /// <item><description><b>加密的 RAR4 三卷</b>（WinRAR 的 <c>Rar.exe</c> 是唯一能写 RAR 的工具）——
    /// 卷标记从名字里彻底抹掉，只能靠内容认组；</description></item>
    /// <item><description><b>加密的跨盘 zip</b>（内置 7z 造）—— 名字缀上「删除」这类网盘垃圾，
    /// 靠"分卷标记 + 粘着的垃圾"这一条把整组认回来。</description></item>
    /// </list>
    /// 每一条都有一个**没密码的对照**：必须如实报「密码错误」、输出目录**一个字节都没有**
    /// —— 否则"成功"就可能来自某条绕过密码的路，而不是真的解开了。</para>
    ///
    /// <para>⚠ 密码一律用占位符（AGENTS.md §8）；样本只在临时目录里现造、绝不入库；
    /// <c>Rar.exe</c> 只调用不复制（共享软件，见 <c>docs/引擎与外部工具.md</c> §4）。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class EncryptedVolumePipelineTests : IDisposable
    {
        /// <summary>测试用密码占位符（**不是**真实密码，AGENTS.md §8）。</summary>
        private const string SamplePassword = "<sample-password>";

        private readonly string _root;

        public EncryptedVolumePipelineTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerEncryptedVolume", Guid.NewGuid().ToString("N"));
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
                // 清不掉只是脏一点（几 MB，用户自己会清临时目录）。
            }
        }

        // ── 加密的 RAR4 三卷 ──

        /// <summary>
        /// 加密的 RAR4 三卷 → 名字改烂（卷标记一点不剩）→ 密码本命中 → **逐字节一致**。
        /// 对照：同一组样本、密码本里没有它 → 必须报「密码错误」且输出目录为空。
        /// </summary>
        [RarExeFact]
        public async Task 加密的RAR4三卷_名字被改烂_密码本命中_解出来逐字节一致()
        {
            byte[] payload = BuildPayload(2_621_440);

            string build = NewDirectory("build-rar4");

            File.WriteAllBytes(Path.Combine(build, "data.bin"), payload);

            RunRar(build, "a", "-ma4", "-m0", "-v1m", "-p" + SamplePassword, "enc4.rar", "data.bin");

            string[] volumes = Directory.GetFiles(build, "enc4.part*.rar").OrderBy(path => path).ToArray();

            Assert.Equal(3, volumes.Length);

            /*
             * 两组各用一份**副本**：第一组跑完名字就被管线改了，对照跑必须拿到一模一样的原始形状。
             * 卷标记一个都不留（真机那种"名字被改烂"的极端形状）—— 认组只能靠内容。
             */
            string[] garbled = { "k1.rar", "k2.rar", "k3.rar" };

            string noPassword = CopyVolumes(volumes, "no-password", garbled);
            string withPassword = CopyVolumes(volumes, "with-password", garbled);

            // ── 对照：密码本里没有它 ──
            Harness bare = CreateHarness(password: null);

            ArchiveTask bareTask = await AddTaskAsync(bare, Path.Combine(noPassword, garbled[0]));

            await bare.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.WrongPassword, bareTask.Status);
            Assert.Empty(Directory.GetFiles(bare.OutputRoot, "*", SearchOption.AllDirectories));

            // ── 正跑：密码本里有它 ──
            Harness harness = CreateHarness(SamplePassword);

            ArchiveTask task = await AddTaskAsync(harness, Path.Combine(withPassword, garbled[0]));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.True(task.VolumeNameAutoRenamed, "不是管线按内容改名的那一条路");

            // 整组改回了标准名（以第一卷的名字为基名），旧名字一个不剩。
            foreach (string name in new[] { "k1.part1.rar", "k1.part2.rar", "k1.part3.rar" })
            {
                Assert.True(File.Exists(Path.Combine(withPassword, name)), $"整组没改回标准名：缺 {name}");
            }

            foreach (string name in garbled)
            {
                Assert.False(File.Exists(Path.Combine(withPassword, name)), $"旧名字还在：{name}");
            }

            // **逐字节一致** —— 这才叫"真的解出来了"。
            string[] extracted = Directory.GetFiles(harness.OutputRoot, "data.bin", SearchOption.AllDirectories);

            Assert.Single(extracted);
            Assert.Equal(payload.Length, new FileInfo(extracted[0]).Length);
            Assert.Equal(payload, File.ReadAllBytes(extracted[0]));
        }

        // ── 加密的跨盘 zip（名字缀着网盘垃圾） ──

        /// <summary>
        /// 加密的跨盘 zip → 每个片名缀上「删除」→ 密码本命中 → **逐字节一致**。
        /// 对照同上：没密码必须报「密码错误」且一个字节都不落盘。
        /// </summary>
        [SevenZipFact]
        public async Task 加密的七z分卷zip_名字缀了删除垃圾_密码本命中_解出来逐字节一致()
        {
            byte[] payload = BuildPayload(2_621_440);

            string build = NewDirectory("build-zip");

            File.WriteAllBytes(Path.Combine(build, "data.bin"), payload);

            RunSevenZip(build, "a", "-tzip", "-v1m", "-p" + SamplePassword, "enc.zip", "data.bin");

            string[] volumes = Directory.GetFiles(build, "enc.zip.*").OrderBy(path => path).ToArray();

            Assert.Equal(3, volumes.Length);

            /*
             * 真机形状（2026-09-28 那次事故）：网盘给每个片名缀上「删除」——
             * `enc.zip.001删除`。名字路必须靠"分卷标记 + 粘着的垃圾"把整组认回来。
             */
            string[] junked = volumes.Select(path => Path.GetFileName(path) + "删除").ToArray();

            string noPassword = CopyVolumes(volumes, "no-password", junked);
            string withPassword = CopyVolumes(volumes, "with-password", junked);

            Harness bare = CreateHarness(password: null);

            ArchiveTask bareTask = await AddTaskAsync(bare, Path.Combine(noPassword, junked[0]));

            await bare.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.WrongPassword, bareTask.Status);
            Assert.Empty(Directory.GetFiles(bare.OutputRoot, "*", SearchOption.AllDirectories));

            Harness harness = CreateHarness(SamplePassword);

            ArchiveTask task = await AddTaskAsync(harness, Path.Combine(withPassword, junked[0]));

            await harness.Coordinator.StartExtractAsync();

            string listing = string.Join(" | ", Directory.GetFiles(withPassword).Select(Path.GetFileName));

            /*
             * 先看名字：7-Zip 用**带垃圾的名字**根本打不开这一组（实测 `7z l enc.zip.001删除` 报 ERRORS、
             * `Type = zip`），只有改回 `<基名>.zip.001` 才认成 `Type = Split / Volumes = 3`。
             * 所以"整组回到标准名"是解压成立的前提，它的失败必须比状态断言先暴露出来。
             */
            foreach (string name in new[] { "enc.zip.001", "enc.zip.002", "enc.zip.003" })
            {
                Assert.True(File.Exists(Path.Combine(withPassword, name)), $"整组没回到标准名：缺 {name}（现在有：{listing}）");
            }

            foreach (string name in junked)
            {
                Assert.False(File.Exists(Path.Combine(withPassword, name)), $"带垃圾的名字还在：{name}（现在有：{listing}）");
            }

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            string[] extracted = Directory.GetFiles(harness.OutputRoot, "data.bin", SearchOption.AllDirectories);

            Assert.Single(extracted);
            Assert.Equal(payload.Length, new FileInfo(extracted[0]).Length);
            Assert.Equal(payload, File.ReadAllBytes(extracted[0]));
        }

        // ── 造样本的小工具 ──

        private static byte[] BuildPayload(int length)
        {
            var payload = new byte[length];

            // -v1m 要真的分卷：载荷必须**不可压缩**（可压的会被压成一卷，"分卷"就名不副实）。
            new Random(20260929).NextBytes(payload);

            return payload;
        }

        private string NewDirectory(string name)
        {
            string directory = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N")[..8]);

            Directory.CreateDirectory(directory);

            return directory;
        }

        /// <summary>把整组卷**复制**到自己的目录里，并按给定的名字重命名（原样本一个字节都不动）。</summary>
        private string CopyVolumes(IReadOnlyList<string> volumes, string directoryName, IReadOnlyList<string> names)
        {
            string directory = NewDirectory(directoryName);

            for (int index = 0; index < volumes.Count; index++)
            {
                File.Copy(volumes[index], Path.Combine(directory, names[index]));
            }

            return directory;
        }

        /// <summary>本机 <c>Rar.exe</c>（路径来自产品自己的唯一出口 <see cref="Engines.ToolLocator"/>）。</summary>
        private static void RunRar(string workingDirectory, params string[] args) =>
            RunProcess(Engines.ToolLocator.Default.RarExePath, workingDirectory, args);

        private static void RunSevenZip(string workingDirectory, params string[] args)
        {
            string sevenZip = SevenZipFactAttribute.LocateSevenZipPath()
                ?? throw new InvalidOperationException("找不到 7z.exe");

            RunProcess(sevenZip, workingDirectory, args);
        }

        private static void RunProcess(string exe, string workingDirectory, IReadOnlyList<string> args)
        {
            var psi = new ProcessStartInfo(exe)
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

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 " + exe);

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(300_000), exe + " 超时");
            Assert.True(process.ExitCode == 0, $"{exe} 失败：{stdout}{stderr}");
        }

        // ── 既有管线（与 RealVolumeSampleTests 同一套夹具，只是密码本可以由用例喂） ──

        private static async Task<ArchiveTask> AddTaskAsync(Harness harness, string path)
        {
            var task = new ArchiveTask(path, harness.Vm.Tasks.Count + 1) { IsSelected = true };

            await new ArchiveDetectService().ApplyDetectResultAsync(task);
            task.CaptureSourceSnapshot();
            harness.Vm.Tasks.Add(task);

            return task;
        }

        /// <summary>搭一套管线；<paramref name="password"/> 非空时先写一本密码本再导进去。</summary>
        private Harness CreateHarness(string? password)
        {
            string dataRoot = NewDirectory("data");
            string outputRoot = NewDirectory("out");

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

            /*
             * ⚠ 同一个 PasswordService 实例必须同时给 ViewModel 与 ExtractionCoordinator ——
             * 候选密码是从**协调器手上那一份**取的，喂错一份就会出现"密码本里明明有却报密码错误"。
             */
            var passwordService = new PasswordService();

            if (!string.IsNullOrEmpty(password))
            {
                string bookPath = Path.Combine(dataRoot, "密码本.txt");

                File.WriteAllText(bookPath, password + Environment.NewLine, new UTF8Encoding(false));
                passwordService.ImportPasswordList(bookPath);
            }

            IArchiveEngine engine = new Engines.SevenZip.SevenZipEngine();
            var logService = new LogService(pathService);

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

            var coordinator = new ExtractionCoordinator(
                vm,
                engine,
                passwordService,
                pathService,
                new ConfirmDialogService());

            coordinator.KeepTaskDetailInLog = true;

            return new Harness(vm, coordinator, outputRoot);
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
            public Harness(MainViewModel vm, ExtractionCoordinator coordinator, string outputRoot)
            {
                Vm = vm;
                Coordinator = coordinator;
                OutputRoot = outputRoot;
            }

            public MainViewModel Vm { get; }

            public ExtractionCoordinator Coordinator { get; }

            public string OutputRoot { get; }
        }
    }
}

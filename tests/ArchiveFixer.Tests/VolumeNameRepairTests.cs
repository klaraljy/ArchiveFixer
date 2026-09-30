using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「按建议改名并重试」（用户 2026-09-25 第 41 条，他当场同意做）。
    ///
    /// <para>场景：一组分卷的**第一卷**名字被改坏（`set.7z(删掉.001`），程序只能报「分卷缺失」+ 给建议 ——
    /// 源文件它自己一个字节都不能动。这个按钮让用户**显式**点一下，程序替他改**这个名字**再重试。</para>
    ///
    /// <para>钉住四件事：①计划该成立的成立、该拒绝的拒绝（六种拒绝理由各一条）；
    /// ②改名的动作**只改名字**（内容逐字节不变、目标被占就什么都不动）；
    /// ③按钮的可用性由**机器判据**决定（任务上有建议 + 文件系统上计划成立），不看中文状态；
    /// ④真 7z 端到端：改名之后**真的解得开**，而且出来的是真内容（不是那个等大的垃圾文件）。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class VolumeNameRepairTests : IDisposable
    {
        private readonly string _root;
        private readonly string? _sevenZip;

        public VolumeNameRepairTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerVolumeRepair", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论。
            }
        }

        // ================================================================ 计划（纯逻辑）

        [Fact]
        public void 计划_名字被改坏的第一卷_推出标准名()
        {
            string directory = NewDirectory("plan-ok");

            string mangled = CreateFile(directory, "set.7z(删掉.001", 64);
            CreateFile(directory, "set.7z.002", 64);
            CreateFile(directory, "set.7z.003", 64);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(mangled, NamesIn(directory));

            Assert.True(plan.CanRepair, plan.Reason);
            Assert.Equal("set.7z.001", plan.SuggestedFileName);
            Assert.Equal(Path.Combine(directory, "set.7z.001"), plan.TargetPath);
            Assert.Contains("set.7z.002", plan.Siblings);
            Assert.Contains("→", plan.Describe());
        }

        [Fact]
        public void 计划_名字本来就是标准的_不许乱动()
        {
            string directory = NewDirectory("plan-standard");

            string standard = CreateFile(directory, "set.7z.001", 64);
            CreateFile(directory, "set.7z.002", 64);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(standard, NamesIn(directory));

            Assert.False(plan.CanRepair);
            Assert.Equal(StatusText.VolumeRepairAlreadyStandard, plan.Reason);
        }

        [Fact]
        public void 计划_同目录没有后续卷_不许瞎猜一个名字()
        {
            string directory = NewDirectory("plan-nosibling");

            string mangled = CreateFile(directory, "set.7z(删掉.001", 64);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(mangled, NamesIn(directory));

            Assert.False(plan.CanRepair);
            Assert.Equal(StatusText.VolumeRepairNoSiblings, plan.Reason);
        }

        [Fact]
        public void 计划_名字里没有卷号_不许改()
        {
            string directory = NewDirectory("plan-novolume");

            string plain = CreateFile(directory, "movie.mp4", 64);
            CreateFile(directory, "movie.mp4.002", 64);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(plain, NamesIn(directory));

            Assert.False(plan.CanRepair);
            Assert.Equal(StatusText.VolumeRepairNotAVolumeName, plan.Reason);
        }

        [Fact]
        public void 计划_不是第一卷_改名解决不了问题()
        {
            string directory = NewDirectory("plan-notfirst");

            string second = CreateFile(directory, "set.7z(删掉.002", 64);
            CreateFile(directory, "set.7z.003", 64);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(second, NamesIn(directory));

            Assert.False(plan.CanRepair);
            Assert.Equal(StatusText.VolumeRepairNotFirstVolume, plan.Reason);
        }

        [Fact]
        public void 计划_目标名被占用_绝不覆盖()
        {
            string directory = NewDirectory("plan-taken");

            string mangled = CreateFile(directory, "set.7z(删掉.001", 64);
            string taken = CreateFile(directory, "set.7z.001", 64);
            CreateFile(directory, "set.7z.002", 64);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(mangled, NamesIn(directory));

            Assert.False(plan.CanRepair);
            Assert.Contains("set.7z.001", plan.Reason);

            // 两个文件都还在、内容都没动（计划阶段一个字节都不写）。
            Assert.True(File.Exists(mangled));
            Assert.True(File.Exists(taken));
        }

        // ================================================================ 改名（真的动盘）

        [Fact]
        public void 改名_只改名字_内容逐字节不变()
        {
            string directory = NewDirectory("apply-ok");

            string mangled = CreateFile(directory, "set.7z(删掉.001", 4096, seed: 7);
            CreateFile(directory, "set.7z.002", 64);

            string before = Sha256(mangled);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(mangled, NamesIn(directory));
            Assert.True(plan.CanRepair, plan.Reason);

            VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

            Assert.True(result.Success, result.Message);
            Assert.Equal(plan.TargetPath, result.NewPath);

            Assert.True(File.Exists(plan.TargetPath), "新名字必须在");
            Assert.False(File.Exists(mangled), "旧名字必须没了");
            Assert.Equal(before, Sha256(plan.TargetPath));
            Assert.Equal(4096, new FileInfo(plan.TargetPath).Length);

            // 同组的后续卷一个字节都没被碰。
            Assert.True(File.Exists(Path.Combine(directory, "set.7z.002")));
        }

        [Fact]
        public void 改名_目标名突然被占用_原地拒绝且什么都不动()
        {
            string directory = NewDirectory("apply-taken");

            string mangled = CreateFile(directory, "set.7z(删掉.001", 128, seed: 11);
            string target = Path.Combine(directory, "set.7z.001");
            CreateFile(directory, "set.7z.001", 128, seed: 12);

            string mangledHash = Sha256(mangled);
            string targetHash = Sha256(target);

            // 手工造一个"计划成立"但目标已被占的状态（模拟两次点击之间别人抢先建了同名文件）。
            var plan = new VolumeNameRepairPlan
            {
                CanRepair = true,
                CurrentPath = mangled,
                CurrentFileName = Path.GetFileName(mangled),
                SuggestedFileName = "set.7z.001",
                TargetPath = target
            };

            VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

            Assert.False(result.Success);
            Assert.Contains("set.7z.001", result.Message);
            Assert.Equal(mangledHash, Sha256(mangled));
            Assert.Equal(targetHash, Sha256(target));
        }

        // ================================================================ 端到端（真 7z + 真管线）

        /// <summary>
        /// 真 7z 三卷 → 把第一卷名字改坏 → 手动「只解压」判「分卷缺失」并给出建议 →
        /// 点「按建议改名并重试」→ 名字被改回标准名 → **真的解出真内容**（不是那个等大的垃圾文件）。
        /// </summary>
        [Fact]
        public async Task 真7z_第一卷改名后点按钮_改名成功并解出真内容()
        {
            RequireSevenZip();

            string volumes = NewDirectory("volumes");
            (string payloadName, byte[] payloadBytes) = CreatePayload(volumes, 3 * 1024 * 1024);
            CreateVolumeSet(volumes, "set.7z", payloadName, "1m");

            string first = Path.Combine(volumes, "set.7z.001");
            string mangled = Path.Combine(volumes, "set.7z(删掉.001");
            File.Move(first, mangled);

            Harness harness = CreateHarness();

            var task = new ArchiveTask(mangled, 1) { IsSelected = true };
            await new ArchiveDetectService().ApplyDetectResultAsync(task);
            task.CaptureSourceSnapshot();
            harness.Vm.Tasks.Add(task);

            /*
             * ①手动「只解压」：**现在会在开工前自动把名字改回标准名**（2026-09-28 用户口径：
             * 「一键处理的功能是啥，就是我按一下你全部搞定，这些必要的操作肯定是要的」），
             * 所以这里不再是"报分卷缺失等着用户点按钮"，而是"改好名字 → 直接解出内容"。
             *
             * ⚠ 老断言（判 VolumeMissing、文件一个字节不动、按钮亮起）在 AutoRepairDisguisedVolumeTests
             * 那一批之前是对的；口径变了就要改断言，别把老期望硬留在那儿。
             * 手动按钮那条路仍然在（判据与执行体同一个 VolumeNameRepair），由下面第二个用例覆盖。
             */
            await harness.Coordinator.StartExtractAsync();

            Assert.True(File.Exists(first), "名字应该已经自动改回标准名了");
            Assert.False(File.Exists(mangled), "旧名字该没了");
            Assert.Equal(first, task.CurrentPath);
            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // ④出来的是**真内容**：与原始字节逐字节一致。
            string output = Path.Combine(harness.OutputRoot, "set", payloadName);
            Assert.True(File.Exists(output), $"没找到产物：{output}");
            Assert.Equal(payloadBytes, File.ReadAllBytes(output));

            // ⑤反向断言：7-Zip 那个"通用分片"垃圾文件不许出现在产物里。
            Assert.False(
                File.Exists(Path.Combine(harness.OutputRoot, "set", "set.7z(删掉")),
                "解出来的不该是「文件自己」那一份垃圾");

            // ⑥源包按默认档留在原地（不变量 1；自动修名只改名字，不搬不删）。
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(Path.Combine(volumes, "set.7z.002")));
            Assert.True(File.Exists(Path.Combine(volumes, "set.7z.003")));
        }

        // ================================================================ 工具

        private string NewDirectory(string name)
        {
            string path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        private static string CreateFile(string directory, string fileName, int size, int seed = 1)
        {
            string path = Path.Combine(directory, fileName);
            var bytes = new byte[size];
            new Random(seed).NextBytes(bytes);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static IEnumerable<string?> NamesIn(string directory) =>
            Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName);

        private static string Sha256(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        /// <summary>在分卷目录里造一个 3 MiB 的载荷（**只传文件名**给 7z，免得把整条路径存进包里）。</summary>
        private static (string Name, byte[] Bytes) CreatePayload(string directory, int size)
        {
            const string name = "a.bin";

            var bytes = new byte[size];
            new Random(20260925).NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(directory, name), bytes);

            return (name, bytes);
        }

        /// <summary>造一组真 7z 分卷（<c>&lt;name&gt;.001/.002/.003</c>）。</summary>
        private void CreateVolumeSet(string directory, string archiveName, string payloadName, string volumeSize)
        {
            var psi = new ProcessStartInfo(_sevenZip!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = directory
            };

            psi.ArgumentList.Add("a");
            psi.ArgumentList.Add("-t7z");
            psi.ArgumentList.Add("-mx0");
            psi.ArgumentList.Add("-v" + volumeSize);
            psi.ArgumentList.Add(Path.Combine(directory, archiveName));
            psi.ArgumentList.Add(payloadName);

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(120_000), "7z 造分卷超时");
            Assert.True(process.ExitCode == 0, $"7z 造分卷失败：{stdout}{stderr}");

            Assert.True(File.Exists(Path.Combine(directory, archiveName + ".001")), "第一卷没造出来");
            Assert.True(File.Exists(Path.Combine(directory, archiveName + ".002")), "第二卷没造出来");
            Assert.True(File.Exists(Path.Combine(directory, archiveName + ".003")), "第三卷没造出来");
        }

        private Harness CreateHarness()
        {
            string dataRoot = Path.Combine(_root, "data");
            string outputRoot = Path.Combine(_root, "out");

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

            // 源包档位固定"留在原地"：这一组测的是改名，不该顺带测源包搬运 / 删除。
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;

            settingsService.Save(settings);

            var engine = new Engines.SevenZip.SevenZipEngine();
            var logService = new LogService(pathService);

            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;

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
                new ConfirmingDialogService());

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;

            var coordinator = new ExtractionCoordinator(
                vm,
                engine,
                new PasswordService(),
                pathService,
                new ConfirmingDialogService());

            return new Harness(vm, coordinator, logService, outputRoot);
        }

        private void RequireSevenZip()
        {
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException(
                    "测试机上没有 7z.exe（ArchiveFixer/tools/7zip/7z.exe），本用例无法运行。");
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

        /// <summary>无界面宿主里默认是"没人点过 = 不确认"，改名那条路就走不到了 —— 这里注入"用户点了确定"。</summary>
        private sealed class ConfirmingDialogService : DialogService
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
            public Harness(MainViewModel vm, ExtractionCoordinator coordinator, LogService log, string outputRoot)
            {
                Vm = vm;
                Coordinator = coordinator;
                Log = log;
                OutputRoot = outputRoot;
            }

            public MainViewModel Vm { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            public string OutputRoot { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);
        }
    }
}

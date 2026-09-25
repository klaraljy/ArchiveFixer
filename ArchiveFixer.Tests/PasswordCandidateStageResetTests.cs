using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 用户 2026-09-25 真机铁证的回归测试：**上一次错密码留下的 0 字节桩文件，不能把下一个候选废掉**。
    ///
    /// <para><b>现场（用户日志 2026-09-25 08:27:53–54，十条候选）</b>：偶数候选全是
    /// "引擎说成功但产物 2 个 / 0 字节"，奇数候选全是"密码错误" —— 完美奇偶交替。
    /// 链条查出来是这样：</para>
    /// <list type="number">
    /// <item><description>7z 用**错密码**解一个 AES ZIP 时，会先在输出目录里建出 0 字节的桩文件，
    /// **然后**才报 Wrong password（退出码 2）—— 用故意错的密码实测复现；</description></item>
    /// <item><description>候选循环在"密码错误"那一支直接 continue，**没有清掉这些桩文件**；</description></item>
    /// <item><description>下一次尝试带着 `-aos`（"跳过已存在文件"，来自设置里的默认覆盖档）跑：
    /// 7z 看到文件已存在就**跳过、退出码 0**，我们随后校验到"0 字节"，把这个候选当废票扔掉；</description></item>
    /// <item><description>于是**排在错密码候选后面的那个正确候选，永远没机会真正解一次** ——
    /// 用户密码本里的第一条正是这样被废掉的（他是第二条候选）。</description></item>
    /// </list>
    ///
    /// <para><b>修法</b>：候选循环里每换一个候选就从**空目录**开始（<c>DiscardStageProductsAsync</c>），
    /// 并且我们自己的暂存提取一律用 `-aoa`（覆盖写，不跟着设置里那一档走）。</para>
    ///
    /// <para>这一组用**真 7z**：假引擎不会"先建桩文件再报错"，所以假引擎测不出这个 bug。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class PasswordCandidateStageResetTests : IDisposable
    {
        /// <summary>包里的内容物文本（解出来后逐字比对）。</summary>
        private const string PayloadText = "stage-reset payload 2026-09-25\n第二行内容\n";

        private const string CorrectPassword = "Stage-Reset-Right-2026";
        private const string WrongPassword = "Stage-Reset-Wrong-2026";

        private readonly string _root;

        public PasswordCandidateStageResetTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerStageReset", Guid.NewGuid().ToString("N"));
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

        /// <summary>
        /// **最小复现**：候选链只有两个 —— 先试空密码（会失败并留下桩文件），再试正确密码。
        ///
        /// <para>修复前：第二个候选被桩文件挡掉（`-aos` 跳过 → 退出码 0 → 校验到 0 字节）→ 整包失败。
        /// 修复后：第二个候选从空目录开始 → 真的解出来了。</para>
        /// </summary>
        [Fact]
        public async Task 空密码留下的零字节桩文件_不能废掉下一个正确候选()
        {
            await RunScenarioAsync(new[] { CorrectPassword });
        }

        /// <summary>
        /// 同一个 bug 的第二种排布：**错密码**候选排在正确密码前面（用户的真实排布就是这样 ——
        /// 他密码本里第一条是对的，前面还夹着空密码与别的条目）。
        /// </summary>
        [Fact]
        public async Task 错误候选留下的桩文件_也不能废掉后面的正确候选()
        {
            await RunScenarioAsync(new[] { WrongPassword, CorrectPassword });
        }

        /// <summary>
        /// 候选链里**只有错密码**：必须如实失败（不能因为桩文件被判成"成功"）。
        /// 这一条钉住"修复没有把失败判成成功"。
        /// </summary>
        [Fact]
        public async Task 只有错密码时_仍然如实失败()
        {
            string package = BuildEncryptedZipPackage();

            Harness harness = CreateHarness(new[] { WrongPassword });
            ArchiveTask task = AddTask(harness, package);

            await harness.Coordinator.StartExtractAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.False(
                Directory.Exists(task.OutputPath) && Directory.GetFiles(task.OutputPath, "*", SearchOption.AllDirectories).Any(IsNonEmptyFile),
                "失败时不许留下任何非空产物（0 字节桩文件更不算产物）");
        }

        private async Task RunScenarioAsync(string[] bookPasswords)
        {
            string package = BuildEncryptedZipPackage();

            Harness harness = CreateHarness(bookPasswords);
            ArchiveTask task = AddTask(harness, package);

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // 内容物真的解出来了：不是 0 字节桩文件，内容逐字相同
            string payload = Assert.Single(Directory.GetFiles(task.OutputPath, "payload.bin", SearchOption.AllDirectories));

            Assert.True(new FileInfo(payload).Length > 0, "解出来的内容物是 0 字节 —— 正是这次要修的那个症状");
            Assert.Equal(PayloadText, File.ReadAllText(payload));

            // 源包一个字节都不动（这一组固定"留在原地"档）
            Assert.True(File.Exists(package), "源包不该被搬走");
        }

        private static bool IsNonEmptyFile(string path)
        {
            try
            {
                return new FileInfo(path).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 用**内置 7z** 造一个 AES-256 加密的 ZIP（内容物是一个 <c>payload.bin</c>）。
        ///
        /// <para>为什么必须是 AES：这个 bug 的触发点正是"7z 用错密码时先建 0 字节桩文件、再报错"，
        /// 而 AES ZIP 上这个行为稳定复现（老式 ZipCrypto 的报错方式不同）。</para>
        /// </summary>
        private string BuildEncryptedZipPackage()
        {
            string stage = Path.Combine(_root, "stage");
            string packageDirectory = Path.Combine(_root, "packages");

            Directory.CreateDirectory(stage);
            Directory.CreateDirectory(packageDirectory);

            File.WriteAllText(Path.Combine(stage, "payload.bin"), PayloadText);

            string package = Path.Combine(packageDirectory, "pack.zip");

            var startInfo = new ProcessStartInfo
            {
                FileName = ToolLocator.Default.SevenZipExePath,
                WorkingDirectory = stage,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            startInfo.ArgumentList.Add("a");
            startInfo.ArgumentList.Add("-tzip");
            startInfo.ArgumentList.Add("-mem=AES256");
            startInfo.ArgumentList.Add("-p" + CorrectPassword);
            startInfo.ArgumentList.Add(package);
            startInfo.ArgumentList.Add("payload.bin");

            using Process process = Process.Start(startInfo)!;

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"造样本失败（7z 退出码 {process.ExitCode}）");
            Assert.True(File.Exists(package), "样本没有造出来");

            return package;
        }

        private Harness CreateHarness(IEnumerable<string> bookPasswords)
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
            settings.MaxParallelExtractCount = 1;
            settings.MaxPasswordAttemptsPerLayer = 10;

            // 源包档位固定"留在原地"：这一组测的是候选循环，不该顺带测源包搬运。
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);

            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            foreach (string password in bookPasswords)
            {
                passwordService.Passwords.Add(new PasswordItem
                {
                    Value = password,
                    Source = "ImportedList",
                    IsEnabled = true,
                    Remark = "阶段重置用例候选"
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
                new DialogService());

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            var coordinator = new ExtractionCoordinator(
                vm,
                engine,
                passwordService,
                pathService,
                new DialogService());

            return new Harness(vm, coordinator, logService, pathService);
        }

        private static ArchiveTask AddTask(Harness harness, string sourcePath)
        {
            var task = new ArchiveTask(sourcePath, harness.Vm.Tasks.Count + 1)
            {
                IsArchive = true,
                DetectedFormat = "ZIP",
                ExtensionStatus = StatusText.ExtensionNormal,
                Status = StatusText.Recognized,
                IsSelected = true
            };

            task.CaptureSourceSnapshot();

            harness.Vm.Tasks.Add(task);

            return task;
        }

        private sealed class Harness
        {
            public Harness(MainViewModel vm, ExtractionCoordinator coordinator, LogService log, PathService pathService)
            {
                Vm = vm;
                Coordinator = coordinator;
                Log = log;
                PathService = pathService;
            }

            public MainViewModel Vm { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            public PathService PathService { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);
        }
    }
}

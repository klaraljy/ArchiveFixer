using ArchiveFixer.Engines.SevenZip;
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
    /// **候选表被改过之后，循环上界必须跟着重算**（2026-09-26 真机逮到的缺陷）。
    ///
    /// <para>现场（我在一次真机验证里跑出来的，日志逐字）：</para>
    /// <code>
    /// 加密的包.7z：开始解压，密码候选 9/10，尝试密码 ******
    /// 加密的包.7z：这个密码候选不对，继续试下一个。
    /// [ERROR] 任务失败：加密的包.7z，Index was out of range. Must be non-negative and less than the size of the collection.
    /// </code>
    ///
    /// <para>根因：`maxPasswordAttempts` 是**列目录之前**算的（`Math.Min(候选数, 每层上限)`），
    /// 而"整包已加密 → 跳过空密码那一档"会在**之后**把空密码候选 `RemoveAll` 掉 ——
    /// 候选数 ≤ 上限时两者本该相等，少了那一个之后循环最后就多跑一次，`candidates[i]` 越界。
    /// 结论还会落成一句"未知错误"（用户完全无法行动）。</para>
    ///
    /// <para>这一组钉两条：①**不许再抛越界**；②候选都试完之后，结论是"密码不对"这类
    /// 说得清的东西，而不是「达到密码尝试上限」（少了那一个候选之后并没有被上限截断）。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class PasswordCandidateBoundTests : IDisposable
    {
        private const string RightPassword = "Bound-Right-2026";

        private readonly string _root;
        private readonly string? _sevenZip;

        public PasswordCandidateBoundTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerBound", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _sevenZip = FindSevenZip();
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, true);
                }
            }
            catch
            {
                // 清理失败不影响结论。
            }
        }

        /// <summary>
        /// 真 7z 端到端：`-mhe=on` 的加密包 + **候选数正好等于每层上限**（空密码 + 2 个错密码，上限 3）
        /// → 跳过空密码之后候选表剩 2 个，循环上界必须跟着变成 2。
        /// </summary>
        [Fact]
        public async Task 跳过空密码之后_候选循环不许越界_结论也要说得清()
        {
            RequireSevenZip();

            string archive = BuildEncryptedPackage("bound.7z");

            Harness harness = CreateHarness(new[] { "错的-1", "错的-2" }, attemptLimit: 3);
            harness.Vm.Tasks.Clear();

            await AddTaskAsync(harness, archive);

            // ⛔ 这一句以前会抛 ArgumentOutOfRangeException（真机上是"未知错误"）。
            await harness.Coordinator.StartExtractAsync();

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);

            Assert.Equal(TaskOutcome.Failed, task.Outcome);

            string all = string.Join("\n", harness.LogTexts);

            // ① 越界异常一个字都不许出现。
            Assert.DoesNotContain("Index was out of range", all, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("未知错误", all, StringComparison.Ordinal);

            // ② 空密码那一档确实被跳过了（这就是"候选表少一个"的来源）。
            Assert.Contains(harness.LogTexts, line => line.Contains("跳过「空密码」", StringComparison.Ordinal));

            // ③ 结论必须说得出原因 —— 少了那一个之后并没有被上限截断，
            //    所以不许报成「达到密码尝试上限」。
            //    （包是 `-mhe=on`：清单读不出来，所以这里的结论是"可能加密了文件名 / 需要密码"那一类，
            //     而不是泛泛的「密码错误」—— 两者对这个包都算说得清，关键是**不许是"未知错误"**。）
            Assert.DoesNotContain(StatusText.PasswordAttemptLimitReached, task.ErrorMessage, StringComparison.Ordinal);
            Assert.True(
                task.ErrorMessage.Contains("密码", StringComparison.Ordinal)
                || task.ErrorMessage.Contains("加密", StringComparison.Ordinal),
                "失败原因必须说清是密码 / 加密相关，实际：" + task.ErrorMessage);
        }

        // ================================================================ 装配

        private sealed class Harness
        {
            public Harness(MainViewModel vm, ExtractionCoordinator coordinator, LogService log)
            {
                Vm = vm;
                Coordinator = coordinator;
                Log = log;
            }

            public MainViewModel Vm { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);
        }

        private Harness CreateHarness(IReadOnlyList<string> candidates, int attemptLimit)
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
            settings.MaxPasswordAttemptsPerLayer = attemptLimit;
            settings.TryEmptyPasswordFirst = true;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;

            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            int index = 0;

            foreach (string password in candidates)
            {
                index++;

                passwordService.Passwords.Add(new PasswordItem
                {
                    Value = password,
                    Source = "ImportedList",
                    IsEnabled = true,
                    Remark = $"越界用例候选 {index}"
                });
            }

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

            var coordinator = new ExtractionCoordinator(
                vm,
                engine,
                passwordService,
                pathService,
                new DialogService());

            return new Harness(vm, coordinator, logService);
        }

        private static async Task<ArchiveTask> AddTaskAsync(Harness harness, string path)
        {
            var task = new ArchiveTask(path, harness.Vm.Tasks.Count + 1) { IsSelected = true };

            // 走与"导入之后自动识别"同一条路（这个 harness 把 AutoScanAfterDrop 关了，所以显式识别一次）。
            await new ArchiveDetectService().ApplyDetectResultAsync(task);

            task.CaptureSourceSnapshot();
            harness.Vm.Tasks.Add(task);

            return task;
        }

        /// <summary>造一个真加密包（`-mhe=on`：连文件名一起加密，所以清单读不出来）。</summary>
        private string BuildEncryptedPackage(string fileName)
        {
            string stage = Path.Combine(_root, "stage-" + Guid.NewGuid().ToString("N"));
            string packages = Path.Combine(_root, "pkg-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(stage);
            Directory.CreateDirectory(packages);

            File.WriteAllText(Path.Combine(stage, "payload.bin"), "bound-test-payload");

            string archive = Path.Combine(packages, fileName);

            Run7z(stage, "a", "-t7z", "-mx0", "-mhe=on", "-p" + RightPassword, archive, "payload.bin");

            Assert.True(File.Exists(archive), "加密包没造出来：" + archive);

            return archive;
        }

        private void Run7z(string workDir, params string[] args)
        {
            var info = new ProcessStartInfo(_sevenZip!)
            {
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (string arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using Process? process = Process.Start(info);

            Assert.NotNull(process);

            process!.WaitForExit(60_000);

            Assert.Equal(0, process.ExitCode);
        }

        private static string? FindSevenZip()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, "src", "ArchiveFixer", "tools", "7zip", "7z.exe");

                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            return null;
        }

        private void RequireSevenZip()
        {
            Assert.True(_sevenZip != null, "这一组要内置 7-Zip（src\\ArchiveFixer\\tools\\7zip\\7z.exe）才能跑。");
        }
    }
}

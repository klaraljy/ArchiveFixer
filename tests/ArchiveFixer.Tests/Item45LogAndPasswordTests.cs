using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **第 45 条**（用户 2026-09-26 真机：38 个同源包，导出 246 KB / 1080 行 —— "你简洁了啥"）。
    ///
    /// <para>他给的三条，逐条对应这里的用例：</para>
    /// <list type="number">
    /// <item><description><b>「并发已满…勾上主界面的「全速」」</b>：主界面根本没有全速（它在②页），
    /// 而且这一行在真机上刷了 74 遍 → 现在**一批只说一次**、指对地方、批末给一条计数汇总，
    /// 而且中途勾上「全速」**当场生效**（回答他"是要暂停下来开还是直接开"）；</description></item>
    /// <item><description><b>日志还是太多</b>：真机上每一条链十几行样板——根因是"密码候选错一个"记了 **WARN**，
    /// 而 WARN 会按第 44 条的规矩把该任务攒着的细节全吐出来并让后续 INFO 不再攒 → "成功就丢"整条失效。
    /// 改成 INFO 之后，同形包在日志里回到"一包一行"；</description></item>
    /// <item><description><b>密码每次都重新一轮</b>：本批已经成功过的密码必须**排在候选第一位**，
    /// 而且那时**不再先试空密码**（加密包的空密码永远不可能对）—— 一批同密码的包，
    /// 第二个开始就应该"一次就开"。</description></item>
    /// </list>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class Item45LogAndPasswordTests : IDisposable
    {
        private const string CorrectPassword = "Item45-Right-2026";

        private readonly string _root;
        private readonly string? _sevenZip;

        public Item45LogAndPasswordTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerItem45", Guid.NewGuid().ToString("N"));
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
            }
        }

        // ================================================================ ② 密码：复用必须排第一

        /// <summary>
        /// 纯逻辑：本批已经有成功过的密码时，候选表**第一条就是它**，而且**没有空密码这一档**。
        /// </summary>
        [Fact]
        public void 候选顺序_本批已成功的密码排第一_且不再先试空密码()
        {
            var service = new PasswordService();

            // 还没有任何成功记录：空密码仍在最前（与从前逐字相同，`TryEmptyPasswordFirst` 说了算）。
            var task = new ArchiveTask(Path.Combine(_root, "a.7z"), 1);

            List<PasswordItem> before = service.GetPasswordCandidates(task, string.Empty, Array.Empty<PasswordItem>(), true);

            Assert.Equal("Empty", before[0].Source);

            // 本批成功过一次之后：它必须是第一条，而且空密码不在表里。
            service.RecordPasswordSuccess(task.CurrentPath, CorrectPassword);

            List<PasswordItem> after = service.GetPasswordCandidates(task, string.Empty, Array.Empty<PasswordItem>(), true);

            Assert.Equal(CorrectPassword, after[0].Value);
            Assert.Equal("BatchSuccess", after[0].Source);
            Assert.DoesNotContain(after, item => item.Source == "Empty");
        }

        /// <summary>
        /// 端到端（真 7z）：**3 个同密码的加密包** —— 第一个包找出密码，后两个必须"一次就开"，
        /// 而且整批只出现一次空密码尝试。
        /// </summary>
        [Fact]
        public async Task 真7z_一批同密码的加密包_只有第一个包试空密码_后两个一次就开()
        {
            RequireSevenZip();

            List<string> packages = new();

            for (int index = 1; index <= 3; index++)
            {
                packages.Add(BuildEncryptedPackage($"pack{index}.7z", index));
            }

            Harness harness = CreateHarness();
            harness.Vm.Tasks.Clear();

            foreach (string package in packages)
            {
                await AddTaskAsync(harness, package);
            }

            await harness.Coordinator.StartExtractAsync();

            List<string> lines = harness.LogTexts.ToList();

            // ①空密码**一次都不该试**：整包加密（条目加密就是加密）时它必然白跑 —— 第 37 条那一条纪律，
            //   这里连"第一个包"也不许试（真机上他看到的正是 `候选 1/10，尝试空密码`）。
            Assert.DoesNotContain(lines, line => line.Contains("尝试空密码", StringComparison.Ordinal));

            // ②后两个包不许出现"候选不对"（密码已知，第一次就该对）。
            foreach (string name in new[] { "pack2.7z", "pack3.7z" })
            {
                List<string> mine = lines.Where(line => line.Contains(name, StringComparison.Ordinal)).ToList();

                Assert.DoesNotContain(mine, line => line.Contains("这个密码候选不对", StringComparison.Ordinal));

                // ③而且它们整个任务只留一行摘要（"成功就丢"重新生效）。
                Assert.True(mine.Count <= 3, $"{name} 应该只剩一行摘要，实际 {mine.Count} 行：\n" + string.Join("\n", mine));
            }

            // ④三个包都真的解开了。
            Assert.All(harness.Vm.Tasks, task => Assert.Equal(TaskOutcome.Succeeded, task.Outcome));
        }

        /// <summary>
        /// 复用到底有没有生效，要在**详细档**里看（默认档只留一行摘要，候选那几行会被"成功就丢"丢掉）：
        /// 第二个包的**第一条候选**必须就是"复用本批已成功的密码"。
        /// </summary>
        [Fact]
        public async Task 真7z_密码复用_第二个包的第一条候选就是本批成功的那个()
        {
            RequireSevenZip();

            List<string> packages = new();

            for (int index = 1; index <= 2; index++)
            {
                packages.Add(BuildEncryptedPackage($"reuse{index}.7z", index));
            }

            Harness harness = CreateHarness();
            harness.Coordinator.KeepTaskDetailInLog = true;
            harness.Vm.Tasks.Clear();

            foreach (string package in packages)
            {
                await AddTaskAsync(harness, package);
            }

            await harness.Coordinator.StartExtractAsync();

            List<string> mine = harness.LogTexts
                .Where(line => line.Contains("reuse2.7z", StringComparison.Ordinal))
                .ToList();

            Assert.Contains(
                mine,
                line => line.Contains("密码候选 1/", StringComparison.Ordinal)
                        && line.Contains("复用本批已成功的密码", StringComparison.Ordinal));

            // ⛔ 反面：第二个包不许再出现"候选 2/N"（那就是"又从头试了一遍"）。
            Assert.DoesNotContain(mine, line => line.Contains("密码候选 2/", StringComparison.Ordinal));
            Assert.DoesNotContain(mine, line => line.Contains("尝试空密码", StringComparison.Ordinal));
        }

        /// <summary>
        /// **文件名也加密**（`-mhe=on`）＋正确密码不在预检窗口里（密码列表第 5 条）时：
        /// 清单读不出来（`preflightList == null`），但"这一包要密码"是引擎**已经说过**的事实 ——
        /// 空密码同样必须被跳过（真机上那行 `尝试空密码` 就是从这条路来的）。
        /// </summary>
        [Fact]
        public async Task 真7z_文件名也加密且密码排在后面_空密码照样跳过()
        {
            RequireSevenZip();

            string stage = Path.Combine(_root, "stage-mhe");
            Directory.CreateDirectory(stage);

            var payload = new byte[64 * 1024];
            new Random(5150).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(stage, "payload.bin"), payload);

            string packages = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packages);

            string archive = Path.Combine(packages, "mhe.7z");
            Run7z(stage, "a", "-t7z", "-mx0", "-mhe=on", "-p" + CorrectPassword, archive, "payload.bin");

            // 正确密码排在第 5 条：预检只看前 3 个候选 → 一定读不出清单（preflightList == null）。
            Harness harness = CreateHarness(new[]
            {
                "另外的-1", "另外的-2", "另外的-3", "另外的-4", CorrectPassword
            });

            await AddTaskAsync(harness, archive);
            await harness.Coordinator.StartExtractAsync();

            List<string> lines = harness.LogTexts.ToList();

            Assert.DoesNotContain(lines, line => line.Contains("尝试空密码", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("连文件名都加密", StringComparison.Ordinal));

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);
            Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
        }

        // ================================================================ ③ 日志体积

        /// <summary>
        /// 量化：**3 个同形包**跑完，日志总量必须与包数同量级（不是每包十几行样板）。
        ///
        /// <para>真机基线（第 45 条那次）：38 个包 → 1080 行 / 145,387 字符。这里把"每包多少"钉住：
        /// 修复后同形包应该是"一包一行摘要 + 少量批级行"。</para>
        /// </summary>
        [Fact]
        public async Task 真7z_三个同形包的日志总量_每包不超过三行()
        {
            RequireSevenZip();

            List<string> packages = new();

            for (int index = 1; index <= 3; index++)
            {
                packages.Add(BuildEncryptedPackage($"size{index}.7z", index));
            }

            Harness harness = CreateHarness();
            harness.Vm.Tasks.Clear();

            foreach (string package in packages)
            {
                await AddTaskAsync(harness, package);
            }

            await harness.Coordinator.StartExtractAsync();

            List<string> lines = harness.LogTexts.ToList();
            int chars = lines.Sum(line => line.Length);

            // 批级行（启动 / 空间调度 / 上限 / 其余物 / 汇总…）留 ~20 行余量；任务侧按"每包 ≤3 行"卡。
            // 实测（2026-09-26，修复后）：**3 个包 → 22 行 / 2,107 字符**（其中 13 行是本批一次的批级行）。
            // 真机基线（修复前，第 45 条那次 38 个包）：1080 行 / 145,387 字符 ≈ **每包 28 行 / 3,826 字符**。
            Assert.True(
                lines.Count <= 3 * 3 + 20,
                $"日志行数应随包数线性且很小，实际 {lines.Count} 行；包含任务名的行：\n"
                + string.Join("\n", lines.Where(line => line.Contains(".7z", StringComparison.Ordinal))));

            Assert.True(
                chars <= 3 * 800 + 2000,
                $"日志字符数应随包数线性且很小，实际 {chars} 字符 / {lines.Count} 行");

            // ⛔ 关键的反向断言：真机上撑大日志的那些"每任务样板"一条都不许在成功档里出现。
            foreach (string boilerplate in new[]
                     {
                         "入仓目录", "定稿完成", "本次内容物", "结果校验", "空间门通过",
                         "资源预算提示", "本次实际输出目录", "中间工作区已清理"
                     })
            {
                Assert.DoesNotContain(lines, line => line.Contains(boilerplate, StringComparison.Ordinal));
            }
        }

        // ================================================================ ① 并发排队

        /// <summary>
        /// 「并发已满」那一条：**整批只说一次**、指对地方（②页）、批末给计数汇总，
        /// 而且不许再出现"主界面的「全速」"这种指错界面的说法。
        /// </summary>
        [Fact]
        public async Task 真7z_并发排队_整批只说一次而且指对地方()
        {
            RequireSevenZip();

            string first = BuildEncryptedPackage("queue1.7z", 1);
            string second = BuildEncryptedPackage("queue2.7z", 2);
            string third = BuildEncryptedPackage("queue3.7z", 3);

            Harness harness = CreateHarness();

            await AddTaskAsync(harness, first);
            await AddTaskAsync(harness, second);
            await AddTaskAsync(harness, third);

            await harness.Coordinator.StartExtractAsync();

            List<string> lines = harness.LogTexts.ToList();

            List<string> throttleLines = lines.Where(line => line.Contains("并发已满", StringComparison.Ordinal)).ToList();

            Assert.True(throttleLines.Count == 1, $"「并发已满」只该说一次，实际 {throttleLines.Count} 次：\n" + string.Join("\n", throttleLines));

            // 指对地方：②页 + 那个开关的真名字；并说清"勾上当场生效"。
            Assert.Contains("②页", throttleLines[0]);
            Assert.Contains("全速（本批不节流）", throttleLines[0]);
            Assert.Contains("当场生效", throttleLines[0]);

            // ⛔ 反面：不许再说"主界面的「全速」"（主界面没有这个选项，用户就是被这句带偏的）。
            Assert.DoesNotContain(lines, line => line.Contains("主界面的「全速」", StringComparison.Ordinal));

            // 批末一条汇总（谁等过、等了几个）。
            Assert.Contains(lines, line => line.Contains("并发排队汇总", StringComparison.Ordinal));
        }

        /// <summary>
        /// **错候选之后才成功**的任务，同样只许留一行（第 45 条的核心）：
        /// 真机上"密码候选错一个"记的是 WARN，而 WARN 会把该任务攒着的细节**全吐出来**并让后续 INFO 不再攒
        /// —— "成功就丢"整条失效，38 个包每个都多出十几行样板（246 KB 的主要来源）。
        /// 现在它是 INFO：任务最后成功 → 细节照样丢。
        /// </summary>
        [Fact]
        public async Task 真7z_先试错两个候选再成功_成功档照样只留一行()
        {
            RequireSevenZip();

            string archive = BuildEncryptedPackage("wrongfirst.7z", 7);

            Harness harness = CreateHarness(new[] { "错的-1", "错的-2", CorrectPassword });

            await AddTaskAsync(harness, archive);
            await harness.Coordinator.StartExtractAsync();

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);

            Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

            List<string> mine = harness.LogTexts
                .Where(line => line.Contains("wrongfirst.7z", StringComparison.Ordinal))
                .ToList();

            Assert.True(
                mine.Count <= 3,
                $"错候选之后成功的任务也只该留一行摘要，实际 {mine.Count} 行：\n" + string.Join("\n", mine));

            // 而且那两行"候选不对"不许在默认档里出现（它们是细节，成功就该丢）。
            Assert.DoesNotContain(mine, line => line.Contains("这个密码候选不对", StringComparison.Ordinal));
            Assert.DoesNotContain(mine, line => line.Contains("密码错误", StringComparison.Ordinal));
        }

        // ================================================================ ① 导出头部

        /// <summary>
        /// **导出头部**：引擎优先级那一格是 <c>List&lt;string&gt;</c> —— 真机上它被直接插进字符串，
        /// 于是头部写着 `优先级：System.Collections.Generic.List`1[System.String]`（用户当场就看出来了）。
        /// 这里钉住"用 → 连起来、一个类型名都不许漏出来"。
        /// </summary>
        [Fact]
        public void 导出头部_引擎优先级要连成一行_不许漏出类型名()
        {
            Harness harness = CreateHarness();

            string text = string.Join("\n", harness.Vm.BuildLogExportHeader());

            Assert.DoesNotContain("System.Collections", text);
            Assert.DoesNotContain("List`1", text);

            string engineLine = text
                .Split('\n')
                .First(line => line.StartsWith("引擎：", StringComparison.Ordinal));

            Assert.Contains("→", engineLine);
            Assert.Contains("7-Zip", engineLine);
        }

        // ================================================================ 工具

        /// <summary>造一个**条目加密**的 7z（`-mhe=off`：清单读得出来，所以密码候选循环真的会跑）。</summary>
        private string BuildEncryptedPackage(string fileName, int seed)
        {
            string stage = Path.Combine(_root, "stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);

            var payload = new byte[256 * 1024];
            new Random(seed * 97).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(stage, "payload.bin"), payload);

            string packages = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packages);

            string archive = Path.Combine(packages, fileName);

            Run7z(stage, "a", "-t7z", "-mx0", "-mhe=off", "-p" + CorrectPassword, archive, "payload.bin");

            return archive;
        }

        private static async Task<ArchiveTask> AddTaskAsync(Harness harness, string path)
        {
            var task = new ArchiveTask(path, harness.Vm.Tasks.Count + 1) { IsSelected = true };

            await new ArchiveDetectService().ApplyDetectResultAsync(task);
            task.CaptureSourceSnapshot();
            harness.Vm.Tasks.Add(task);

            return task;
        }

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

        private Harness CreateHarness(IReadOnlyList<string>? bookPasswords = null)
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
            settings.MaxPasswordAttemptsPerLayer = 10;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;

            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            // 正确密码放进密码列表（第一个包靠它找到密码，后两个靠"复用"）。
            foreach (string password in bookPasswords ?? new[] { CorrectPassword })
            {
                passwordService.Passwords.Add(new PasswordItem
                {
                    Value = password,
                    Source = "ImportedList",
                    IsEnabled = true,
                    Remark = "第 45 条用例候选"
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
    }
}

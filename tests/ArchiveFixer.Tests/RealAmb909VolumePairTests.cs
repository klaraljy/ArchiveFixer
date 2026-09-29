using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 真机那一对分卷（<c>amb909.7.01</c> = 2,147,483,648 B + <c>amb909.z.2</c> = 1,890,791,346 B）的
    /// **只读副本**验收开关（用户 2026-09-29 当场质疑："什么叫复刻，你做的程序到底有没有用"）。
    ///
    /// <para><b>为什么必须是真样本副本</b>：合成样本只能证明"按我以为的格式写出来的字节，我处理得对"
    /// —— 上一次的验收正是栽在这里（合成样本走通了、真机上那条路**根本没走到**）。
    /// 只有真文件的副本才有资格回答"用户手上那份东西，程序现在能不能处理"。</para>
    ///
    /// <para>副本按项目规矩放仓库同级的 <c>_tmp\ArchiveFixer\amb909-copy\</c>（可用环境变量
    /// <c>ARCHIVEFIXER_REAL_VOLUME_PAIR_DIR</c> 换位置）；<b>原件在 H: 盘上，只读、绝不改动</b>。
    /// 用例只在自己临时目录里的副本上动手。副本不在就**跳过并说明**（不是失败）。</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RealAmb909PairFactAttribute : FactAttribute
    {
        /// <summary>副本目录的环境变量名（换机器不用改代码）。</summary>
        internal const string DirectoryEnvironmentVariable = "ARCHIVEFIXER_REAL_VOLUME_PAIR_DIR";

        /// <summary>「这一对分卷有可用密码」时才会跑的那一档用的环境变量名。</summary>
        internal const string PasswordEnvironmentVariable = "ARCHIVEFIXER_REAL_VOLUME_PASSWORD";

        private const string RelativePairDirectory = "amb909-copy";

        internal static readonly string PairRoot = ResolvePairRoot();

        public RealAmb909PairFactAttribute()
        {
            if (string.IsNullOrEmpty(SevenZipFactAttribute.LocateSevenZipPath()))
            {
                Skip = "测试机上没有可用的 7z.exe，跳过真机副本用例。";

                return;
            }

            if (!File.Exists(Path.Combine(PairRoot, "amb909.7.01")) || !File.Exists(Path.Combine(PairRoot, "amb909.z.2")))
            {
                Skip = @"真机那一对分卷的副本不在（<仓库同级>\_tmp\ArchiveFixer\amb909-copy），跳过真机副本用例。";
            }
        }

        private static string ResolvePairRoot()
        {
            string? configured = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);

            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(RealVolumeSampleFactAttribute.TempRoot, RelativePairDirectory)
                : configured!;
        }
    }

    /// <summary>「文件名也加密」那一档的密码用例开关（密码由**用户**放进环境变量，⛔ 绝不进仓库 / 日志）。</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RealAmb909PairPasswordFactAttribute : FactAttribute
    {
        public RealAmb909PairPasswordFactAttribute()
        {
            if (string.IsNullOrEmpty(SevenZipFactAttribute.LocateSevenZipPath()))
            {
                Skip = "测试机上没有可用的 7z.exe，跳过真机副本用例。";

                return;
            }

            if (!File.Exists(Path.Combine(RealAmb909PairFactAttribute.PairRoot, "amb909.7.01")))
            {
                Skip = @"真机那一对分卷的副本不在（<仓库同级>\_tmp\ArchiveFixer\amb909-copy），跳过真机副本用例。";

                return;
            }

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RealAmb909PairFactAttribute.PasswordEnvironmentVariable)))
            {
                Skip = "没给这一组的密码（环境变量 " + RealAmb909PairFactAttribute.PasswordEnvironmentVariable
                       + "），没法列条目 / 解内容，跳过这一步。";
            }
        }
    }

    /// <summary>
    /// **真机副本验收**（用户 2026-09-29 / 2026-09-30 两次点名：验收必须用他真文件的只读副本）。
    ///
    /// <para>真机现场：<c>amb909.7.01</c>（2 GiB，开头 <c>37 7A BC AF 27 1C</c>）+ <c>amb909.z.2</c>
    /// （1.89 GB，7-Zip 认不出 = 裸续卷）。这两个文件**本来是一组完整的两卷 7z**，
    /// 只是当年造包时开了 <c>-mhe</c>（**文件名也加密**）：卷齐时 7-Zip 报的是
    /// <c>Cannot open encrypted archive. Wrong password?</c>，卷不齐时报 <c>Unexpected end of archive</c>
    /// —— 老代码只认"列得出清单"，于是把"缺密码"误报成「分卷缺失」（见 <c>VolumeProbeVerifier</c> 的注释）。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class RealAmb909VolumePairTests : IDisposable
    {
        /// <summary>真机那两个文件的字节数（**事实**，写在 AGENTS.md §11 与真机日志里）。</summary>
        private const long FirstVolumeBytes = 2147483648;

        private const long SecondVolumeBytes = 1890791346;

        /// <summary>9/28 那次成功解压的事实：4 个文件 / 4,038,274,626 字节（真机日志原文）。</summary>
        private const int PayloadFileCount = 4;

        private const long PayloadTotalBytes = 4038274626;

        private readonly string _root;
        private readonly ITestOutputHelper _output;

        public RealAmb909VolumePairTests(Xunit.Abstractions.ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerRealPair", Guid.NewGuid().ToString("N"));
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
                // 清不掉只是脏一点（4 GB，用户自己会清）。
            }
        }

        /// <summary>
        /// **改名 + 归档可达性**（不需要密码，永远跑）：既有管线跑一遍真副本，
        /// 整组必须改回 <c>amb909.7z.001</c> / <c>.002</c>，而且引擎必须认得出"这一组是一份完整的加密归档"。
        /// </summary>
        [RealAmb909PairFact]
        public async Task 真机副本_两卷整组改回标准名_而且引擎认得出这是一份完整归档()
        {
            (string first, string second) = CopyPair();

            string beforeFirst = Sha256(first);
            string beforeSecond = Sha256(second);

            Harness harness = CreateHarness();
            ArchiveTask firstTask = await AddTaskAsync(harness, first);
            ArchiveTask secondTask = await AddTaskAsync(harness, second);

            await harness.Coordinator.StartExtractAsync();

            string standardFirst = Path.Combine(Path.GetDirectoryName(first)!, "amb909.7z.001");
            string standardSecond = Path.Combine(Path.GetDirectoryName(first)!, "amb909.7z.002");

            Assert.True(File.Exists(standardFirst), "整组没改回标准名：缺 amb909.7z.001");
            Assert.True(File.Exists(standardSecond), "整组没改回标准名：缺 amb909.7z.002");
            Assert.False(File.Exists(first), "旧名字还在：amb909.7.01");
            Assert.False(File.Exists(second), "旧名字还在：amb909.z.2");
            Assert.True(firstTask.VolumeNameAutoRenamed, "不是管线按内容改名的那一条路");

            // **只改了名字**：两个文件的字节逐字节与副本一致（真机源文件更是一个字节都没碰）。
            Assert.Equal(beforeFirst, Sha256(standardFirst));
            Assert.Equal(beforeSecond, Sha256(standardSecond));
            Assert.Equal(FirstVolumeBytes, new FileInfo(standardFirst).Length);
            Assert.Equal(SecondVolumeBytes, new FileInfo(standardSecond).Length);

            /*
             * 「引擎认得出这一组」的**机器证据**（这是本地副本上能拿到的最硬的事实）：
             * 卷齐的加密归档 → EncryptedHeaders（头读到了，只是要密码）；
             * 卷不齐 / 拿别的文件顶替 → Cannot open the file as archive + Unexpected end of archive。
             * 两种结论在本地副本上都实测过（AGENTS.md §11 记着）。
             */
            var engine = new Engines.SevenZip.SevenZipEngine();
            ArchiveListResult list = await engine.ListAsync(ArchiveRequest.For(standardFirst, null), default);

            Assert.False(list.IsRawSplitStream, "引擎把这一组当成「一段通用分片」了 —— 说明卷没接上");
            Assert.Equal(EngineErrorTypes.EncryptedHeaders, list.ErrorType);

            // 老行为在这里报的是「分卷缺失」——那句诊断是错的（卷齐得很，缺的是密码）。
            Assert.NotEqual(StatusText.VolumeMissing, firstTask.Status);

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("已按标准名改好", StringComparison.Ordinal));

            _output.WriteLine($"第一个任务终态：状态=[{firstTask.Status}] 终态=[{firstTask.Outcome}] 原因=[{firstTask.ErrorMessage}]");
            _output.WriteLine($"第二个任务终态：状态=[{secondTask.Status}] 终态=[{secondTask.Outcome}] 原因=[{secondTask.ErrorMessage}]");
            _output.WriteLine(
                "⚠ 没验到的：这一组的**条目名与解出的字节**（文件名也加密，没有密码列不出清单）。"
                + "9/28 的真机日志记着这一组当时解出 4 个文件 / 4,038,274,626 字节；"
                + $"把密码放进环境变量 {RealAmb909PairFactAttribute.PasswordEnvironmentVariable} 可补跑那一步。");
        }

        /// <summary>
        /// **列出条目 + 解出内容**（要密码；密码由用户放进环境变量，⛔ 不进仓库 / 日志）：
        /// 既有管线端到端跑真副本，必须解出 4 个文件、合计 4,038,274,626 字节。
        /// </summary>
        [RealAmb909PairPasswordFact]
        public async Task 真机副本_有密码时_既有管线真的解出这一组的内容()
        {
            string password = Environment.GetEnvironmentVariable(RealAmb909PairFactAttribute.PasswordEnvironmentVariable)!;

            (string first, string _) = CopyPair();

            string directory = Path.GetDirectoryName(first)!;
            string standardFirst = Path.Combine(directory, "amb909.7z.001");

            /*
             * 先单独量一次"清单"（产品自己的引擎）——这是"引擎打开了这一组、列出条目与字节数"的直接证据，
             * 而且与 9/28 真机日志里那份清单可以对上。
             */
            var engine = new Engines.SevenZip.SevenZipEngine();
            ArchiveListResult scan = await engine.ListAsync(ArchiveRequest.For(first, password), default);

            Assert.True(scan.Success, $"有密码也列不出清单：{scan.Message}");
            Assert.Equal(PayloadFileCount, scan.FileCount);
            Assert.Equal(PayloadTotalBytes, scan.TotalUncompressedSize);

            Harness harness = CreateHarness(new[] { password });
            ArchiveTask task = await AddTaskAsync(harness, first);

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.True(File.Exists(standardFirst), "整组没改回标准名：缺 amb909.7z.001");

            string[] extracted = Directory.GetFiles(harness.OutputRoot, "*", SearchOption.AllDirectories);

            Assert.Equal(PayloadFileCount, extracted.Length);
            Assert.Equal(PayloadTotalBytes, extracted.Sum(path => new FileInfo(path).Length));

            _output.WriteLine(
                $"解出 {extracted.Length} 个文件 / {extracted.Sum(path => new FileInfo(path).Length)} 字节："
                + string.Join("、", extracted.Select(Path.GetFileName)));
        }

        // ── 副本与管线 ──

        /// <summary>把真机那一对（只读副本）拷进本次用例自己的目录，保持真机的两个名字。</summary>
        private (string First, string Second) CopyPair()
        {
            string directory = Path.Combine(_root, "amb909");
            Directory.CreateDirectory(directory);

            string first = Path.Combine(directory, "amb909.7.01");
            string second = Path.Combine(directory, "amb909.z.2");

            File.Copy(Path.Combine(RealAmb909PairFactAttribute.PairRoot, "amb909.7.01"), first);
            File.Copy(Path.Combine(RealAmb909PairFactAttribute.PairRoot, "amb909.z.2"), second);

            // 副本必须与真机的字节数**一字不差**，否则后面的结论不成立。
            Assert.Equal(FirstVolumeBytes, new FileInfo(first).Length);
            Assert.Equal(SecondVolumeBytes, new FileInfo(second).Length);

            return (first, second);
        }

        private static string Sha256(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
            using var hasher = SHA256.Create();

            return Convert.ToHexString(hasher.ComputeHash(stream));
        }

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
            var passwordService = new PasswordService();

            foreach (string password in bookPasswords ?? Array.Empty<string>())
            {
                passwordService.Passwords.Add(new PasswordItem
                {
                    Value = password,
                    Source = "ImportedList",
                    IsEnabled = true,
                    Remark = "真机副本用例候选"
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
                new ConfirmDialogService());

            var coordinator = new ExtractionCoordinator(
                vm,
                engine,
                passwordService,
                pathService,
                new ConfirmDialogService());

            coordinator.KeepTaskDetailInLog = true;

            return new Harness(vm, coordinator, logService, outputRoot);
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「特定解压 · 每个包只留一层内容」（用户 2026-09-24 拍板）的**真样本端到端**验收。
    ///
    /// <para>
    /// 跑的是**真链路**：真 7z.exe 造的样本、真 <see cref="MainViewModel"/> + 各 Coordinator +
    /// 真单层解压管线（与 <c>InnerLayerContinuationTests</c> 同一套造样本与装配做法）。
    /// 断言的是**逐字比对最终目录树**，不是 Contains —— 用户要的正是
    /// "<c>222\1111\内容物</c>"这一个形状，差一层就是没做到。
    /// </para>
    ///
    /// <para>
    /// 本类声明进 <c>ArchiveFixerGlobalState</c> 集合（不与其他集合并行）：
    /// <see cref="MainViewModel"/> 的构造会写两个进程级静态（内置 7z 路径、递归工作区根），
    /// 与别的真 7z 用例并行会互相踩（见该集合的说明）。
    /// </para>
    ///
    /// <para>样本全部自己造（临时目录 + 项目内置 7z.exe），不读用户目录里的任何文件（AGENTS.md §8）。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class SpecialExtractionPipelineTests : IDisposable
    {
        private const string PayloadText = "最里面那一层的内容物\n";
        private const string OuterPayloadText = "外层包自己的 payload\n";
        private const string InnerPayloadText = "内层包自己的 payload\n";

        /// <summary>其余物目录名（唯一实现在 <see cref="ProcessArtifactLayout"/>，这里不另抄一份）。</summary>
        private static readonly string RestName = ProcessArtifactLayout.ArtifactDirectoryName;

        private readonly string _root;
        private readonly string _sevenZip;

        public SpecialExtractionPipelineTests()
        {
            _sevenZip = LocateSevenZip();
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSpecialExtraction", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论（句柄可能还被 7z 释放中）。
            }
        }

        // ================================================================ ① 规则关：与现在完全一样

        /// <summary>
        /// 规则**关**：单链多层包按判定表 4 套一层 —— <c>222\1111\内容文件夹\payload.txt</c>。
        ///
        /// <para>
        /// 这一条同时钉住另外三件事：① 反向断言（<c>222\</c> 下只有 <c>1111</c>，
        /// 内容物绝不许落到包名层之外）；② 其余物位置不变（<c>222\1111\其余物\</c>）；
        /// ③ 没开特定解压时任务详情里**不该**出现"特定解压"那一行。
        /// </para>
        /// </summary>
        [Fact]
        public async Task 规则关_单链多层包_按判定表套一层()
        {
            Harness harness = CreateHarness("222", settings => settings.UseSpecialExtraction = false);
            string package = BuildSingleChainPackage(harness.SourceRoot, "1111.7z");

            await harness.AddPathsAsync(package);
            await harness.RunOneClickAsync();

            Assert.Equal(
                new[]
                {
                    "1111",
                    Path.Combine("1111", RestName),
                    Path.Combine("1111", RestName, "1111.7z"),
                    Path.Combine("1111", "内容文件夹"),
                    Path.Combine("1111", "内容文件夹", "payload.txt")
                },
                Tree(harness.SourceRoot));

            // 内容物那一层里面就是内容物本来的样子（不是空壳、不是别的东西）。
            Assert.Equal(
                PayloadText,
                File.ReadAllText(Path.Combine(harness.SourceRoot, "1111", "内容文件夹", "payload.txt")));

            // 反向断言：包名层那一层保留，内容不许落到 222\ 去。
            Assert.Equal(new[] { "1111" }, DirectoryNames(harness.SourceRoot));
            Assert.Empty(Directory.GetFiles(harness.SourceRoot));

            // 其余物没跑到别处去。
            Assert.False(Directory.Exists(Path.Combine(harness.SourceRoot, RestName)));
            Assert.True(File.Exists(Path.Combine(harness.SourceRoot, "1111", RestName, "1111.7z")));

            Assert.DoesNotContain(
                harness.Vm.Tasks,
                task => task.RunOptionsNote.Contains("特定解压", StringComparison.Ordinal));
        }

        // ================================================================ ② 规则开：用户要的那一句

        /// <summary>
        /// 规则**开**：同一个样本，那一层不再套 —— <c>222\1111\payload.txt</c>（用户原话：
        /// "我想要的是这种 222\1111\内容物"）。
        ///
        /// <para>同时还钉住：包名层永远保留、其余物位置不变、日志与任务详情能看出用了哪条规则。</para>
        /// </summary>
        [Fact]
        public async Task 规则开_单链多层包_内容物直接落在包名层里()
        {
            Harness harness = CreateHarness("222", EnableSpecialExtraction);
            string package = BuildSingleChainPackage(harness.SourceRoot, "1111.7z");

            await harness.AddPathsAsync(package);

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);
            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.Equal(1, outcome.Rounds);
            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // 逐字比对目录树：内容物直接落在包名层里，其余物仍在包名层下面。
            Assert.Equal(
                new[]
                {
                    "payload.txt",
                    RestName,
                    Path.Combine(RestName, "1111.7z")
                },
                Tree(Path.Combine(harness.SourceRoot, "1111")));

            Assert.Equal(PayloadText, File.ReadAllText(Path.Combine(harness.SourceRoot, "1111", "payload.txt")));

            // 反向断言：222\ 下除了 1111 没有别的（内容物绝不许跑到包名层之外）。
            Assert.Equal(new[] { "1111" }, DirectoryNames(harness.SourceRoot));
            Assert.Empty(Directory.GetFiles(harness.SourceRoot));
            Assert.False(Directory.Exists(Path.Combine(harness.SourceRoot, RestName)));

            // 可追溯：日志与任务详情都要能回答"这次用了哪条特定规则"。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("特定解压", StringComparison.Ordinal)
                        && line.Contains("每个包只留一层内容", StringComparison.Ordinal));

            Assert.Contains("特定解压", task.RunOptionsNote, StringComparison.Ordinal);
            Assert.Contains("每个包只留一层内容", task.RunOptionsNote, StringComparison.Ordinal);

            // 失败清单第二级与「复制任务信息」读的是同一份依据。
            var summaryService = new TaskSummaryService();

            Assert.Contains(
                summaryService.BuildFailureDetailLines(task),
                line => line.Contains("特定解压", StringComparison.Ordinal));

            Assert.Contains(
                "每个包只留一层内容",
                MainViewModel.BuildTaskInfoText(task),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// 关掉总开关：**规则清单里写着什么都不算**，行为与基线逐字相同。
        ///
        /// <para>
        /// 做法是同一个样本跑两遍（开关关 + 清单里有规则 / 开关关 + 清单为空），
        /// 两棵树逐字比一遍 —— 用户点名要的保证是"关掉时一键处理的行为一个字都不变"。
        /// </para>
        /// </summary>
        [Fact]
        public async Task 关掉总开关_规则清单里写着也不算_与基线逐字相同()
        {
            Harness withRules = CreateHarness("222-rules", settings =>
            {
                settings.UseSpecialExtraction = false;
                settings.SpecialExtractionRules = new List<string> { SpecialExtractionRules.SingleContentLayer };
            });

            await withRules.AddPathsAsync(BuildSingleChainPackage(withRules.SourceRoot, "1111.7z"));
            await withRules.RunOneClickAsync();

            Harness withoutRules = CreateHarness("222-no-rules", settings =>
            {
                settings.UseSpecialExtraction = false;
                settings.SpecialExtractionRules = new List<string>();
            });

            await withoutRules.AddPathsAsync(BuildSingleChainPackage(withoutRules.SourceRoot, "1111.7z"));
            await withoutRules.RunOneClickAsync();

            var expected = new[]
            {
                "1111",
                Path.Combine("1111", RestName),
                Path.Combine("1111", RestName, "1111.7z"),
                Path.Combine("1111", "内容文件夹"),
                Path.Combine("1111", "内容文件夹", "payload.txt")
            };

            Assert.Equal(expected, Tree(withRules.SourceRoot));
            Assert.Equal(expected, Tree(withoutRules.SourceRoot));
        }

        // ================================================================ ③ 多分支：不塌 + WARN

        /// <summary>
        /// 多分支包（<c>A\X\…</c> + <c>A\Y\…</c>）：规则开着也**不塌**，
        /// 按原判定表保守套一层；产物与关掉时**逐字相同**；并且必须有一条 WARN 说清原因。
        /// </summary>
        [Fact]
        public async Task 多分支包_规则开也不塌_产物与关掉时逐字相同()
        {
            Harness off = CreateHarness("222-off", settings => settings.UseSpecialExtraction = false);

            await off.AddPathsAsync(BuildBranchPackage(off.SourceRoot, "1111.7z"));
            await off.RunOneClickAsync();

            Assert.Equal(
                new[]
                {
                    "1111",
                    Path.Combine("1111", "A"),
                    Path.Combine("1111", "A", "X"),
                    Path.Combine("1111", "A", "X", "a.txt"),
                    Path.Combine("1111", "A", "Y"),
                    Path.Combine("1111", "A", "Y", "b.txt"),
                    Path.Combine("1111", RestName),
                    Path.Combine("1111", RestName, "1111.7z")
                },
                Tree(off.SourceRoot));

            // 同一个样本（同名同内容）在另一份干净目录里再跑一遍，开关打开。
            Harness on = CreateHarness("222-on", EnableSpecialExtraction);

            await on.AddPathsAsync(BuildBranchPackage(on.SourceRoot, "1111.7z"));
            await on.RunOneClickAsync();

            Assert.Equal(Tree(off.SourceRoot), Tree(on.SourceRoot));

            // 绝不静默：WARN 说清是哪条规则、为什么没按它走。
            Assert.Contains(
                on.LogTexts,
                line => line.Contains("WARN", StringComparison.Ordinal)
                        && line.Contains("特定解压", StringComparison.Ordinal)
                        && line.Contains("并列", StringComparison.Ordinal));
        }

        // ================================================================ ④ 同名冲突：改名，绝不覆盖

        /// <summary>
        /// 塌缩之后撞名（用户红线：**绝不覆盖**）：外层包已经产出 <c>222\1111\payload.txt</c>，
        /// 内层包塌缩后也要往同一个名字落 —— 按设置里的冲突档（默认自动改名）落成
        /// <c>payload(1).txt</c>，外层那一份**一个字节都不许变**。
        ///
        /// <para>
        /// 这个现场只有"塌缩"才会出现：不塌时内层产物落在 <c>1111\X\payload.txt</c>，
        /// 与 <c>1111\payload.txt</c> 根本不撞。
        /// </para>
        /// </summary>
        [Fact]
        public async Task 塌缩后撞名_按冲突档自动改名_不覆盖()
        {
            Harness harness = CreateHarness("222", settings =>
            {
                EnableSpecialExtraction(settings);

                // 默认档就应该是"自动改名"（绝不覆盖）——这里显式钉一遍，免得将来默认值被人改掉。
                settings.ConflictAction = ConflictActions.AutoRename;
            });

            await harness.AddPathsAsync(BuildNestedSameNamePackage(harness.SourceRoot, "1111.7z"));

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            // 第 2 轮 = 内层包续解（撞名就发生在那一轮）。
            Assert.Equal(2, outcome.Rounds);

            string contentDirectory = Path.Combine(harness.SourceRoot, "1111");

            // 外层那一份**原样**（内容一个字节都没被顶掉）。
            Assert.Equal(OuterPayloadText, File.ReadAllText(Path.Combine(contentDirectory, "payload.txt")));

            // 内层那一份落在自动改名的位置上。
            Assert.Equal(InnerPayloadText, File.ReadAllText(Path.Combine(contentDirectory, "payload(1).txt")));

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("同名冲突", StringComparison.Ordinal)
                        && line.Contains("绝不覆盖", StringComparison.Ordinal));

            /*
             * 反向对照：**关掉规则**时同一个样本根本不撞名（内层产物落在 1111\X\ 里）——
             * 于是上面那条"改了名"确实来自"塌缩后撞名"，不是别的原因。
             */
            Harness off = CreateHarness("222-off", settings => settings.UseSpecialExtraction = false);

            await off.AddPathsAsync(BuildNestedSameNamePackage(off.SourceRoot, "1111.7z"));
            await off.RunOneClickAsync();

            Assert.True(
                File.Exists(Path.Combine(off.SourceRoot, "1111", "X", "payload.txt")),
                "关掉规则时内层产物应该留在它自己那一层里");

            Assert.False(
                File.Exists(Path.Combine(off.SourceRoot, "1111", "payload(1).txt")),
                "关掉规则时不该出现改名产物（说明上面那次撞名确实是塌缩造成的）");
        }

        // ================================================================ ⑤ 确认框那行

        [Fact]
        public async Task 确认框正文_开着特定解压才有那一行()
        {
            Harness on = CreateHarness("222-on", EnableSpecialExtraction);

            await on.AddPathsAsync(BuildSingleChainPackage(on.SourceRoot, "1111.7z"));

            OneClickConfirmFacts facts = await on.Extraction.BuildConfirmFactsAsync(
                on.Vm.Tasks.ToList(),
                null,
                null);

            Assert.StartsWith(StatusText.SpecialExtractionConfirmLabel, facts.SpecialExtractionEcho, StringComparison.Ordinal);
            Assert.Contains("每个包只留一层内容", facts.SpecialExtractionEcho, StringComparison.Ordinal);

            // 关掉开关：那一行**整行不出现**（空串 = 界面里收起）。
            Harness off = CreateHarness("222-off", settings => settings.UseSpecialExtraction = false);

            await off.AddPathsAsync(BuildSingleChainPackage(off.SourceRoot, "1111.7z"));

            OneClickConfirmFacts offFacts = await off.Extraction.BuildConfirmFactsAsync(
                off.Vm.Tasks.ToList(),
                null,
                null);

            Assert.Equal(string.Empty, offFacts.SpecialExtractionEcho);
        }

        // ================================================================ 样本构造

        /// <summary>
        /// 单链多层包：包内是 <c>A\内容文件夹\payload.txt</c>（两层文件夹，每层都只有一个子目录）。
        ///
        /// <para>规则关 → 判定表 4 套出最深那层（<c>内容文件夹</c>）；规则开 → 那一层不套。</para>
        /// </summary>
        private string BuildSingleChainPackage(string sourceRoot, string packageName)
        {
            string build = Path.Combine(_root, "build-single-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(build);

            WriteText(Path.Combine(build, @"A\内容文件夹\payload.txt"), PayloadText);

            Directory.CreateDirectory(sourceRoot);

            string package = Path.Combine(sourceRoot, packageName);

            Run7z(build, "a", "-t7z", package, @"A\内容文件夹\payload.txt");

            return package;
        }

        /// <summary>
        /// 多分支包：包内是 <c>A\X\a.txt</c> 与 <c>A\Y\b.txt</c>（<c>A</c> 下面两个并列文件夹）。
        /// 规则开着也**不塌**。
        /// </summary>
        private string BuildBranchPackage(string sourceRoot, string packageName)
        {
            string build = Path.Combine(_root, "build-branch-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(build);

            WriteText(Path.Combine(build, @"A\X\a.txt"), "x");
            WriteText(Path.Combine(build, @"A\Y\b.txt"), "y");

            Directory.CreateDirectory(sourceRoot);

            string package = Path.Combine(sourceRoot, packageName);

            Run7z(build, "a", "-t7z", package, @"A\X\a.txt", @"A\Y\b.txt");

            return package;
        }

        /// <summary>
        /// 塌缩后撞名的现场：外层包里有 <c>payload.txt</c> + 内层包 <c>inner.7z</c>；
        /// 内层包里也有一个 <c>payload.txt</c>（单链，规则开会塌到同一层）。
        /// </summary>
        private string BuildNestedSameNamePackage(string sourceRoot, string packageName)
        {
            string build = Path.Combine(_root, "build-nested-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(build);

            string innerBuild = Path.Combine(_root, "build-inner-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(innerBuild);

            WriteText(Path.Combine(innerBuild, @"X\payload.txt"), InnerPayloadText);

            Run7z(innerBuild, "a", "-t7z", Path.Combine(build, "inner.7z"), @"X\payload.txt");

            WriteText(Path.Combine(build, "payload.txt"), OuterPayloadText);

            Directory.CreateDirectory(sourceRoot);

            string package = Path.Combine(sourceRoot, packageName);

            Run7z(build, "a", "-t7z", package, "payload.txt", "inner.7z");

            return package;
        }

        // ================================================================ 装配与工具

        /// <summary>打开总开关并按第一条规则跑（每个用例都写一遍太啰嗦，抽一处）。</summary>
        private static void EnableSpecialExtraction(AppSettings settings)
        {
            settings.UseSpecialExtraction = true;
            settings.SpecialExtractionRules = new List<string> { SpecialExtractionRules.SingleContentLayer };
        }

        /// <summary>
        /// 一套真实装配：真 MainViewModel + 真各 Coordinator + 真 7z 引擎。
        ///
        /// <para>
        /// <paramref name="runId"/> 决定这一次的**源目录**（<c>&lt;root&gt;\&lt;runId&gt;</c>）——
        /// 跑两遍比对的那几条用例各用一份干净目录，免得第二遍撞上第一遍留下的"输出目录已存在"。
        /// </para>
        /// </summary>
        private Harness CreateHarness(string runId, Action<AppSettings>? configure = null)
        {
            string dataRoot = Path.Combine(_root, "data-" + runId);
            string sourceRoot = Path.Combine(_root, runId);

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(sourceRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();

            settings.CacheRootDirectory = dataRoot;

            // 未指定位置：产物落在源包旁边那一层（用户现场就是 222\1111\…）。
            settings.ExtractToOriginalDirectory = true;
            settings.CustomOutputDirectory = string.Empty;
            settings.KeepArchiveNameFolder = true;
            settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
            settings.CustomSevenZipExePath = string.Empty;

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

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

            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var extraction = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());

            // 这个用例拿「细节日志」当行为证据（第 44 条之后，成功时默认只留两行）。
            extraction.KeepTaskDetailInLog = true;
            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());

            return new Harness(vm, oneClick, extraction, logService, sourceRoot);
        }

        /// <summary>目录树的**逐字**快照（相对 <paramref name="root"/> 的全部条目，排序后比对）。</summary>
        private static List<string> Tree(string root)
        {
            if (!Directory.Exists(root))
            {
                return new List<string>();
            }

            return Directory
                .EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string[] DirectoryNames(string root) =>
            Directory.GetDirectories(root)
                .Select(path => Path.GetFileName(path) ?? string.Empty)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        private static string LocateSevenZip()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(dir.FullName, "ArchiveFixer", "tools", "7zip", "7z.exe");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                dir = dir.Parent;
            }

            string local = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            if (File.Exists(local))
            {
                return local;
            }

            throw new InvalidOperationException("找不到内置 7z.exe。");
        }

        private void Run7z(string workingDirectory, params object[] args)
        {
            var psi = new ProcessStartInfo(_sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            foreach (object a in args)
            {
                psi.ArgumentList.Add(a.ToString() ?? string.Empty);
            }

            using Process p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);

            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"7z 失败（exit {p.ExitCode}）：{string.Join(' ', psi.ArgumentList)}\n{stdout}\n{stderr}");
            }
        }

        private static void WriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        private sealed class Harness
        {
            private readonly OneClickCoordinator _oneClick;

            public Harness(
                MainViewModel vm,
                OneClickCoordinator oneClick,
                ExtractionCoordinator extraction,
                LogService log,
                string sourceRoot)
            {
                Vm = vm;
                _oneClick = oneClick;
                Extraction = extraction;
                Log = log;
                SourceRoot = sourceRoot;
            }

            public MainViewModel Vm { get; }

            public ExtractionCoordinator Extraction { get; }

            public LogService Log { get; }

            /// <summary>这一次的源目录（也是"未指定位置"档下的成品根）。</summary>
            public string SourceRoot { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);

            public Task AddPathsAsync(params string[] paths) => Vm.AddPathsAsync(paths);

            /// <summary>跑一键处理的**流程部分**（不弹任何对话框）。</summary>
            public Task<OneClickOutcome> RunOneClickAsync() => _oneClick.RunPipelineAsync(Vm.Tasks.ToList());
        }
    }
}

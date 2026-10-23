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


        // ================================================================ ② 规则开：用户要的那一句



        // ================================================================ ③ 多分支：不塌 + WARN


        // ================================================================ ④ 同名冲突：改名，绝不覆盖


        // ================================================================ ⑤ 确认框那行


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


            // 未指定位置：产物落在源包旁边那一层（用户现场就是 222\1111\…）。
            settings.ExtractToOriginalDirectory = true;
            settings.CustomOutputDirectory = string.Empty;
            settings.KeepArchiveNameFolder = true;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
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
                    string candidate = Path.Combine(dir.FullName, "src", "ArchiveFixer", "tools", "7zip", "7z.exe");

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

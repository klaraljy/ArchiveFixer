using System;
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
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **「内容物压缩文件不解压」在真链路上的行为**（用户 2026-10-04 拍板的新功能）。
    ///
    /// <para><b>用户原话</b>：「只要文件名里面包含着这个字符就不能动，例如 <c>小明</c>，
    /// 只要内容物里面有文件的名称包含了"小明"的这些压缩文件碰都不要碰，例如 <c>小明.zip</c>、
    /// <c>小明.part1.rar</c>……包含的也同样是」。</para>
    ///
    /// <para><b>要证的只有两件事</b>：① 命中关键词的内层包**真的没被解开**（它的内容不在盘上）、
    /// 而且**原样留着**（既没被改名、也没被搬进其余物、更没被删）；
    /// ② 没命中的内层包**照旧被解开**（这条功能不是"一刀切不解压"）。</para>
    ///
    /// <para><b>红检</b>：把唯一出口 <c>ContentKeepRules.FindMatch</c> 改成恒 <c>null</c>
    /// ⇒ 命中那两条用例变红（`小明内容.txt` 会被解出来）。
    /// ⛔ 反过来，把出厂默认档（空关键词列表）也做成"命中"同样会红 —— 用例 ② 的对照就是它。</para>
    /// </summary>
    public class ContentKeepKeywordPipelineTests : IDisposable
    {
        private readonly string _root;
        private readonly string _sevenZip;
        private readonly ITestOutputHelper _output;

        public ContentKeepKeywordPipelineTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerKeepKeyword", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _sevenZip = SevenZipFactAttribute.LocateSevenZipPath();
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
                // 临时目录清不掉不影响测试结论。
            }
        }

        /// <summary>
        /// <b>用例 A（默认档：按轮次续解）</b>：<c>outer.7z</c> 里装着 <c>小明.zip</c>（内含
        /// <c>小明内容.txt</c>）与 <c>layer1.txt</c>；关键词 = <c>小明</c>。
        ///
        /// <para>预期：<c>小明内容.txt</c> **一个字节都没被解出来**，而 <c>小明.zip</c> **原样在盘上**
        /// （它是内容物，定稿归位到目标目录不算碰）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 用例A_默认档_命中关键词的内层包不解开也不动()
        {
            Harness harness = CreateHarness(recursionMode: "SingleLayer", keepKeywords: new[] { "小明" });
            string outer = BuildOuter(innerName: "小明.zip", innerPayloadName: "小明内容.txt", innerPayload: "留着的内容");

            await harness.AddPathsAsync(outer);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            _output.WriteLine(
                $"【用例A】盘上文件：{string.Join("、", FindFiles("*").Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal))}");

            // ① 根源包那一层成功了（内容物出来了）—— 不然下面的结论可能只是"整条链没跑"。
            Assert.NotEmpty(FindFiles("layer1.txt"));

            // ② 命中关键词的内层包**没被解开**：它的内容一个字节都不在盘上。
            Assert.Empty(FindFiles("小明内容.txt"));

            /*
             * ③ 而且它**原样留着**、而且**留在内容物那一层**（不在「其余物」里）——
             * "碰都不碰"的完整含义：既不进其余物（进去就会被链尾那一档按设置删掉），
             * 也没有被改名。判据只看路径里有没有那层目录名。
             */
            string[] kept = FindFiles("小明.zip");

            Assert.NotEmpty(kept);

            Assert.All(
                kept,
                path => Assert.DoesNotContain(
                    ProcessArtifactLayout.ArtifactDirectoryName,
                    path,
                    StringComparison.Ordinal));

            // ④ 日志如实说"因为命中关键词所以不解开"。
            Assert.Contains(
                harness.Log.Logs,
                entry => entry.Message.Contains("内容物保留关键词", StringComparison.Ordinal)
                         && entry.Message.Contains("小明.zip", StringComparison.Ordinal));
        }

        /// <summary>
        /// <b>用例 B（对照）</b>：同一个夹具，只有内层包的名字换成 <c>小红.zip</c>（不含关键词）
        /// ⇒ **照旧被解开**。这条与用例 A 一起证明"命中才不解、没命中照旧解"。
        /// </summary>
        [SevenZipFact]
        public async Task 用例B_对照_没命中关键词的内层包照旧解开()
        {
            Harness harness = CreateHarness(recursionMode: "SingleLayer", keepKeywords: new[] { "小明" });
            string outer = BuildOuter(innerName: "小红.zip", innerPayloadName: "小红内容.txt", innerPayload: "该解出来的内容");

            await harness.AddPathsAsync(outer);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            _output.WriteLine(
                $"【用例B】盘上文件：{string.Join("、", FindFiles("*").Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal))}");

            // 没命中 ⇒ 照旧解出来（这条功能不是"一刀切不解压"）。
            Assert.NotEmpty(FindFiles("小红内容.txt"));
            Assert.NotEmpty(FindFiles("layer1.txt"));
        }

        /// <summary>
        /// <b>用例 C（递归核心那条路）</b>：<c>RecursionMode = SingleChain</c> 时内层包由
        /// <c>RecursiveExtractor.ProbeInnerArchivesAsync</c> 找 —— 命中关键词的那一份**不当内层归档**
        /// （不解开、也不改名）。
        /// </summary>
        [SevenZipFact]
        public async Task 用例C_递归核心那条路_命中关键词的也不当内层归档()
        {
            Harness harness = CreateHarness(recursionMode: "SingleChain", keepKeywords: new[] { "小明" });
            string outer = BuildOuter(innerName: "小明.zip", innerPayloadName: "小明内容.txt", innerPayload: "留着的内容");

            await harness.AddPathsAsync(outer);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            Assert.NotEmpty(FindFiles("layer1.txt"));
            Assert.Empty(FindFiles("小明内容.txt"));
            Assert.NotEmpty(FindFiles("小明.zip"));

            Assert.Contains(
                harness.Log.Logs,
                entry => entry.Message.Contains("内容物保留关键词", StringComparison.Ordinal));
        }

        // ================================================================ 夹具

        /// <summary>
        /// 造 <c>outer.7z</c>：里面装着 <paramref name="innerName"/>（一个内含
        /// <paramref name="innerPayloadName"/> 的真 zip）以及一个第 1 层自己的内容文件 <c>layer1.txt</c>。
        /// </summary>
        private string BuildOuter(string innerName, string innerPayloadName, string innerPayload)
        {
            string build = Path.Combine(_root, "build-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(build);

            File.WriteAllText(Path.Combine(build, innerPayloadName), innerPayload, new UTF8Encoding(false));
            Run7z(build, "a", "-tzip", innerName, innerPayloadName);

            File.WriteAllText(Path.Combine(build, "layer1.txt"), "第 1 层自己的内容\n", new UTF8Encoding(false));

            string sourceDirectory = Path.Combine(_root, "src");
            Directory.CreateDirectory(sourceDirectory);

            string outer = Path.Combine(sourceDirectory, "outer.7z");
            Run7z(build, "a", "-t7z", outer, innerName, "layer1.txt");

            Directory.Delete(build, recursive: true);

            return outer;
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

        private string[] FindFiles(string pattern) =>
            Directory.Exists(_root)
                ? Directory.GetFiles(_root, pattern, SearchOption.AllDirectories)
                : Array.Empty<string>();

        private Harness CreateHarness(string recursionMode, string[] keepKeywords)
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
            settings.RecursionMode = recursionMode;
            settings.AutoScanAfterDrop = false;
            settings.TryEmptyPasswordFirst = false;

            // 源包留在原地（这个用例只关心"内容物解不解开"）。
            settings.SourceHandling = SourceHandlingMode.KeepInPlace.ToString();
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            settings.CustomSevenZipExePath = string.Empty;
            settings.ContentKeepKeywords = keepKeywords.ToList();

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

            extraction.KeepTaskDetailInLog = true;

            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());

            return new Harness(vm, oneClick, logService);
        }

        private sealed class Harness
        {
            private readonly OneClickCoordinator _oneClick;

            public Harness(MainViewModel vm, OneClickCoordinator oneClick, LogService log)
            {
                Vm = vm;
                _oneClick = oneClick;
                Log = log;
            }

            public MainViewModel Vm { get; }

            public LogService Log { get; }

            public Task AddPathsAsync(params string[] paths) => Vm.AddPathsAsync(paths);

            public Task<OneClickOutcome> RunOneClickAsync() => _oneClick.RunPipelineAsync(Vm.Tasks.ToList());
        }
    }
}

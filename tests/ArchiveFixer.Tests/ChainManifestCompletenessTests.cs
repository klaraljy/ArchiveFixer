using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
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
    /// **L3 预期清单来自哪一层** 的端到端守门用例（用户 2026-09-30 真机报的 bug）。
    ///
    /// <para><b>现场（真机日志 `ArchiveFixer-本次操作_20260930_204627.txt`）</b>：
    /// `（T244）管理1.mp4` 是伪装后缀的双面文件（真身是"从偏移 56086230 处抠出来的内嵌 ZIP"），
    /// ZIP 里是 `Sociology.7z`，再解一层才是 8 个内容物。用户设了"源包放入其余物 + 其余物彻底删除"，
    /// 结果：解压成功、校验通过、8 个文件定稿 —— 可日志第 92 行写着
    /// "完整性：无法确认（没有可用的归档清单可核对…）…源包留在原地（未移入其余物）"，
    /// 第 97 行还承诺"整条续解链跑完后再按「删除操作」处理"，第 112 行链就跑完了，
    /// 然后**什么都没有**：源包还在、其余物压根没生成。</para>
    ///
    /// <para><b>根因</b>：`ExtractionCoordinator.PostProcessSuccessAsync` 的
    /// `recursionOutranOuterManifest = expandedLayers &gt; 1` 一见"展开 &gt; 1 层"就把预期整份丢掉，
    /// 连**产出最终内容物的那一层**（叶子层）的清单也一起丢 —— 而那一层在解压前就列过目录。
    /// 于是 `ManifestCrossChecked = false` ⇒ L4 判「判不出」⇒ 所有不可逆动作全被自家闸门挡住。</para>
    ///
    /// <list type="number">
    /// <item><description>形状 1（用户形状）：伪装壳 → 内嵌 ZIP → 内层 7z → 内容物。两层**都**列得出清单，
    /// 所以必须**可证完整**，源包按设置被处理。</description></item>
    /// <item><description>形状 2（真的取不到清单）：引擎列不出目录 ⇒ 仍「判不出」+ 一个字节不动 + 日志点名原因。</description></item>
    /// <item><description>形状 3（链尾收尾）：多层链跑完 ⇒ 按设置处理；中途失败 ⇒ 不动。</description></item>
    /// <item><description>形状 4（不回退）：单层常规包 + 其余物保留 ⇒ 照旧。</description></item>
    /// </list>
    ///
    /// <para>样本全部自己造（临时目录 + 项目内置 7z.exe），不读用户目录里的任何文件（AGENTS.md §8）。
    /// 判据只读机器终态 / 结构化结果（终态枚举、校验枚举、L4 三态、盘上文件），⛔ 不比中文文案 ——
    /// 需要断言"日志写了这句话"时一律引用 <see cref="StatusText"/> 里的常量，不手抄中文。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class ChainManifestCompletenessTests : IDisposable
    {
        private const string InnerPassword = "InnerPass1";

        private const int FakeVideoPrefixLength = 32768;

        private readonly string _root;
        private readonly string _sevenZip;

        public ChainManifestCompletenessTests()
        {
            _sevenZip = LocateSevenZip();
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerChainManifest", Guid.NewGuid().ToString("N"));
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

        // ================================================================ 形状 1：复现用户形状


        // ================================================================ 形状 2：真的取不到清单


        // ================================================================ 形状 5：多层链里的**叶子层**列不出清单


        // ================================================================ 形状 3：续解链末尾



        // ================================================================ 形状 4：既有行为不许回退


        // ================================================================ ②页「嵌套压缩包」三档必须**真的是三档**



        // ================================================================ 形状 6：展开**恰好一层** + 加密头


        // ================================================================ 样本

        private const string OuterVolumePassword = "OuterPass1";

        /// <summary>
        /// 造用户的真实现场：`X.mp4` = 假 MP4 头 + 尾部一个完整 ZIP，ZIP 里是 `Y.7z`（`Y\file.bin`）。
        ///
        /// <para>造法必须是"先把 ZIP 整个建好、再在前面拼数据"：这样 ZIP 的内部偏移才保持相对它自己，
        /// 才是那个"7z 打不开、必须按偏移抠出来"的形态（与 <c>EmbeddedArchiveTests</c> 同一口径）。</para>
        /// </summary>
        private string BuildDisguisedContainerWithInner7z(string containerName, string innerName)
        {
            string inner = BuildPlainPackage(innerName);

            string stage = Path.Combine(_root, "zip-stage-" + containerName);
            Directory.CreateDirectory(stage);
            File.Copy(inner, Path.Combine(stage, innerName), overwrite: true);

            string plainZip = Path.Combine(_root, containerName + ".zip");
            ZipFile.CreateFromDirectory(stage, plainZip);

            string packageDirectory = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packageDirectory);

            string container = Path.Combine(packageDirectory, containerName);

            using (var output = new FileStream(container, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                output.Write(BuildFakeVideoPrefix());
                output.Write(File.ReadAllBytes(plainZip));
            }

            return container;
        }

        /// <summary>
        /// 造**两层都不伪装**的链：`outer.7z` 里只有 `leaf.7z`，`leaf.7z` 里才是 `content.txt`。
        ///
        /// <para>与 <see cref="BuildDisguisedContainerWithInner7z"/> 是同一种"两层"形状，只是外壳不伪装 ——
        /// 形状 5 要的是"多层链"这个事实本身（第 0 层列得出清单、叶子层列不出），
        /// 不需要"内嵌归档抠副本"那一层噪音。</para>
        /// </summary>
        private string BuildTwoLayerPlainChain(string outerName, string leafName)
        {
            string leafStage = Path.Combine(_root, leafName + "-stage");
            Directory.CreateDirectory(leafStage);
            File.WriteAllText(Path.Combine(leafStage, "content.txt"), "叶子层才有的最终数据\n", new UTF8Encoding(false));

            string packageDirectory = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packageDirectory);

            string leaf = Path.Combine(packageDirectory, leafName);

            // 相对名传给 7z（工作目录就是 stage），归档里的条目名才稳定。
            Run7z(leafStage, "a", "-t7z", leaf, "content.txt");

            string outerStage = Path.Combine(_root, outerName + "-stage");
            Directory.CreateDirectory(outerStage);
            File.Copy(leaf, Path.Combine(outerStage, leafName), overwrite: true);

            string outer = Path.Combine(packageDirectory, outerName);

            Run7z(outerStage, "a", "-t7z", outer, leafName);

            return outer;
        }

        /// <summary>`Y.7z` = 只有一条 `Y\file.bin` 的真 7z（不加密：这一条测的是"清单拿不拿得到"）。</summary>
        private string BuildPlainPackage(string name)
        {
            string stage = Path.Combine(_root, name + "-stage");
            Directory.CreateDirectory(Path.Combine(stage, "Y"));

            File.WriteAllBytes(Path.Combine(stage, "Y", "file.bin"), new byte[2048]);

            string packageDirectory = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packageDirectory);

            string package = Path.Combine(packageDirectory, name);

            // 相对名传给 7z（工作目录就是 stage），归档里的条目名才稳定。
            Run7z(stage, "a", "-t7z", package, "Y");

            return package;
        }

        /// <summary>
        /// `single-hp.7z` = 一条 `Y\file.bin` 的真 7z，**加密头（`-mhe=on`）**、密码 = <see cref="InnerPassword"/>。
        ///
        /// <para>与 <see cref="BuildPlainPackage"/> 的唯一区别就是 <c>-mhe=on</c> —— 而这一条正是形状 6 的关键：
        /// 不给密码（空密码）连**目录**都列不出来，所以"现问引擎列一份"必然失败，
        /// 只有用"解压时那个正确候选列出来的本层清单"才拿得到预期。</para>
        /// </summary>
        private string BuildEncryptedHeadersPackage(string name)
        {
            string stage = Path.Combine(_root, name + "-stage");
            Directory.CreateDirectory(Path.Combine(stage, "Y"));

            File.WriteAllBytes(Path.Combine(stage, "Y", "file.bin"), new byte[2048]);

            string packageDirectory = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packageDirectory);

            string package = Path.Combine(packageDirectory, name);

            Run7z(stage, "a", "-t7z", package, "-p" + InnerPassword, "-mhe=on", "Y");

            return package;
        }

        /// <summary>
        /// 造"第一层只出过程物"的两层链：`outer.7z` 里只有 `inner.7z.001/.002…` 一组加密分卷，
        /// 分卷里才是内容物。返回的是那个外壳（用例只导入它，内层分卷由它解出来）。
        /// </summary>
        private string BuildVolumeChain()
        {
            string innerStage = Path.Combine(_root, "chain-inner-stage");
            Directory.CreateDirectory(innerStage);
            File.WriteAllText(Path.Combine(innerStage, "content.txt"), "第二层才有的最终数据\n", new UTF8Encoding(false));

            // 得有料才切得开：100 KB 随机填充按 40 KB 一卷 → 至少 3 卷。
            byte[] filler = new byte[100 * 1024];
            new Random(20260930).NextBytes(filler);
            File.WriteAllBytes(Path.Combine(innerStage, "big.bin"), filler);

            string packageDirectory = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packageDirectory);

            // 分卷必须切得开：40 KB 一卷。
            Run7z(
                innerStage,
                "a",
                "-t7z",
                Path.Combine(packageDirectory, "inner.7z"),
                "-v40k",
                "-p" + OuterVolumePassword,
                "-mhe=on",
                "content.txt",
                "big.bin");

            List<string> volumes = Directory.GetFiles(packageDirectory, "inner.7z.*")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Assert.True(volumes.Count >= 2, "分卷样本没有切出两卷以上");

            string outerStage = Path.Combine(_root, "chain-outer-stage");
            Directory.CreateDirectory(outerStage);

            foreach (string volume in volumes)
            {
                File.Copy(volume, Path.Combine(outerStage, Path.GetFileName(volume)), overwrite: true);
            }

            string outer = Path.Combine(packageDirectory, "outer.7z");

            Run7z(
                outerStage,
                "a",
                "-t7z",
                outer,
                volumes.Select(Path.GetFileName).ToArray()!);

            // 造完就把散卷从源目录里收走：用例只导入外壳，避免同一份卷被导入两次。
            foreach (string volume in volumes)
            {
                File.Delete(volume);
            }

            return outer;
        }

        /// <summary>假 MP4 头：前 12 字节是真格式的 ftyp box，后面填随机字节（固定种子，样本可复现）。</summary>
        private static byte[] BuildFakeVideoPrefix()
        {
            byte[] prefix = new byte[FakeVideoPrefixLength];
            new Random(20260921).NextBytes(prefix);

            byte[] boxSize = { 0x00, 0x00, 0x00, 0x20 };
            byte[] ftyp = Encoding.ASCII.GetBytes("ftypisom");

            Array.Copy(boxSize, 0, prefix, 0, boxSize.Length);
            Array.Copy(ftyp, 0, prefix, 4, ftyp.Length);

            return prefix;
        }

        /// <summary>产物目录里所有叫「其余物」的目录（用户可见的其余物就是这个名字，唯一出口 <c>ProcessArtifactLayout</c>）。</summary>
        private static string[] FindRestDirectories(string outputRoot)
        {
            if (!Directory.Exists(outputRoot))
            {
                return Array.Empty<string>();
            }

            return Directory.GetDirectories(outputRoot, "*", SearchOption.AllDirectories)
                .Where(path => ProcessArtifactLayout.IsArtifactDirectoryName(path))
                .ToArray();
        }

        /// <summary>用例失败时把日志一起打出来（现场在日志里，光看断言读不出发生了什么）。</summary>
        private static string Diagnose(Harness harness)
        {
            return "\n日志：\n" + string.Join("\n", harness.LogTexts)
                   + "\n产物目录树：\n"
                   + (Directory.Exists(harness.OutputRoot)
                       ? string.Join(" | ", Directory.GetFileSystemEntries(harness.OutputRoot, "*", SearchOption.AllDirectories))
                       : "(输出目录不存在)");
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
                if (a is IEnumerable<string> many)
                {
                    foreach (string s in many)
                    {
                        psi.ArgumentList.Add(s);
                    }
                }
                else
                {
                    psi.ArgumentList.Add(a.ToString() ?? string.Empty);
                }
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

        // ================================================================ 装配

        private static string LocateSevenZip()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, "src", "ArchiveFixer", "tools", "7zip", "7z.exe");

                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            string local = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            return File.Exists(local) ? local : string.Empty;
        }

        private Harness CreateHarness(
            string passwordBookText,
            Action<AppSettings>? configure = null,
            IArchiveEngine? engineOverride = null)
        {
            string dataRoot = Path.Combine(_root, "data");
            string outputRoot = Path.Combine(_root, "out-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            string bookPath = Path.Combine(_root, "密码本.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(bookPath)!);
            File.WriteAllText(bookPath, passwordBookText, new UTF8Encoding(false));

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.PasswordBookPath = bookPath;
            settings.CustomSevenZipExePath = string.Empty;

            configure?.Invoke(settings);
            settingsService.Save(settings);

            IArchiveEngine engine = engineOverride ?? new SevenZipEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            /*
             * MainViewModel 的构造会顺手写两个进程级静态（7z 路径、递归工作区根目录）。
             * 先存下来、构造完立刻还原：不然别的测试会拿着"我这边马上要删掉的临时目录"当工作区去解压。
             */
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
            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());

            return new Harness(vm, oneClick, extraction, outputRoot, logService);
        }

        private sealed class Harness
        {
            private readonly OneClickCoordinator _oneClick;

            public Harness(
                MainViewModel vm,
                OneClickCoordinator oneClick,
                ExtractionCoordinator coordinator,
                string outputRoot,
                LogService log)
            {
                Vm = vm;
                _oneClick = oneClick;
                Coordinator = coordinator;
                OutputRoot = outputRoot;
                Log = log;
            }

            public MainViewModel Vm { get; }

            /// <summary>手动「只解压」那条路（⛔ 没有一键处理的轮次续解，用来单独看"这一单自己解到第几层"）。</summary>
            public ExtractionCoordinator Coordinator { get; }

            public string OutputRoot { get; }

            public LogService Log { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);

            public Task AddPathsAsync(params string[] paths) => Vm.AddPathsAsync(paths);

            /// <summary>跑一键处理的流程部分（不弹任何对话框 —— 测试里没人在那儿点确定）。</summary>
            public Task<OneClickOutcome> RunOneClickAsync() => _oneClick.RunPipelineAsync(Vm.Tasks.ToList());
        }

        /// <summary>
        /// 假引擎：**只对叶子层列不出目录**，其余一切原样转给真 7-Zip 引擎。
        ///
        /// <para>为什么需要它：<see cref="ListFailsEngine"/> 是"每一层都列不出"，
        /// 做不出形状 5 要的"第 0 层列得出来、只有产出最终内容物的叶子层列不出来"——
        /// 而这一档正是检验等级 L3 那条"改拿叶子层清单"规则的**反面**
        /// （拿不到就如实判「判不出」，⛔ 不许退回去拿第 0 层的顶替）。</para>
        ///
        /// <para>解压照样成功：预检那次列目录失败**不拦**（<c>RecursiveExtractor</c> 里
        /// "拦下来会让正常包也解不开，兜底是解压后的落点校验"），所以这一档的产物是好的、
        /// 只是没有清单可核对 —— 与真机里的加密头 / 损坏同形。</para>
        /// </summary>
        private sealed class LeafListFailsEngine : IArchiveEngine
        {
            private readonly SevenZipEngine _inner = new();
            private readonly string _leafName;

            public LeafListFailsEngine(string leafName)
            {
                _leafName = leafName;
            }

            public string Id => _inner.Id;

            public string DisplayName => _inner.DisplayName;

            public string Version => _inner.Version;

            public bool IsAvailable => _inner.IsAvailable;

            public EngineCapabilities Capabilities => _inner.Capabilities;

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
                => _inner.ProbeAsync(request, cancellationToken);

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                string name = Path.GetFileName(request.ArchivePath);

                return name.StartsWith(_leafName, StringComparison.OrdinalIgnoreCase)
                    ? Task.FromResult(ArchiveListResult.Failure(
                        EngineErrorTypes.EncryptedHeaders,
                        "Cannot open encrypted archive. Wrong password?",
                        Id,
                        Version))
                    : _inner.ListAsync(request, cancellationToken);
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
                => _inner.TestAsync(request, cancellationToken);

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
                => _inner.ExtractAsync(request, options, cancellationToken);
        }

        /// <summary>
        /// 假引擎：**能解开、但列不出目录**（真机里的加密头 / 损坏就是这一档）。
        /// 只用于形状 2 —— "真的取不到清单" 那一档。
        /// </summary>
        private sealed class ListFailsEngine : IArchiveEngine
        {
            public string Id => "list-fails-fake";

            public string DisplayName => "列不出清单的假引擎";

            public string Version => "1.0";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; } = new()
            {
                CanProbe = true,
                CanList = true,
                CanTest = true,
                CanExtract = true,
                SupportsPassword = true
            };

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new ArchiveProbeResult
                {
                    IsArchive = true,
                    Format = "7Z",
                    Message = "假引擎：认得，但列不出清单"
                });
            }

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(ArchiveListResult.Failure(
                    EngineErrorTypes.EncryptedHeaders,
                    "Cannot open encrypted archive. Wrong password?",
                    Id,
                    Version));
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new ArchiveOperationResult { Success = true, Status = StatusText.ExtractSuccess });
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                // 真的写出一个文件：这一档的要点是"产物没问题，只是没有清单可核对"。
                Directory.CreateDirectory(request.OutputPath!);
                File.WriteAllBytes(Path.Combine(request.OutputPath!, "produced.bin"), new byte[1024]);

                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = true,
                    Status = StatusText.ExtractSuccess
                });
            }
        }
    }
}

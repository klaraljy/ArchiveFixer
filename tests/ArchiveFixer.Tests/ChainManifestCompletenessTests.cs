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

        /// <summary>
        /// **复现用户形状**：`X.mp4` = 带偏移的 ZIP 容器（假 MP4 头 + 完整 ZIP），ZIP 里是 `Y.7z`，
        /// `Y.7z` 里是 `Y\file.bin`。设 源包=放入其余物 + 删除操作=彻底删除，跑一键处理。
        ///
        /// <para>断言（全是机器终态）：L4 = 可证完整、`OutputManifestCrossChecked = true`、
        /// 源包已按设置被处理掉、其余物按设置被处理掉、日志里有"可证完整"那一行依据。</para>
        ///
        /// <para>⚠ 修复前这条是**红**的：L4 判「判不出」，源包原封不动、其余物压根不存在。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 形状1_伪装壳里的内层7z_两层都列得出清单_必须可证完整_源包按设置被处理()
        {
            string container = BuildDisguisedContainerWithInner7z("X.mp4", "Y.7z");

            Harness harness = CreateHarness(
                $"{InnerPassword}\n",
                settings =>
                {
                    // 走**递归核心**：第 0 层（抠出来的内嵌 ZIP）与第 1 层（Y.7z）在一单里跑完
                    // —— 正是真机日志第 47/62 行的形状。
                    settings.RecursionMode = "SingleChain";
                    settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                    settings.RestHandlingAfterVerify = RestHandlingModes.Delete;

                    /*
                     * 「详细日志」必须开：成功任务的那几行 INFO 细节默认**只进缓冲、随成功一起丢掉**
                     * （第 44 条的日志体积口径），要断言"日志里有可证完整的依据"就得开它 ——
                     * 与真机日志里"要看成功任务的全过程请勾上「详细日志」再跑一次"是同一口径。
                     */
                    settings.VerboseLog = true;
                });

            await harness.AddPathsAsync(container);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);

            Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
            Assert.Equal(OutputVerificationOutcome.Passed, task.OutputVerification);

            // ⛔ 这两个就是本次 bug 的开关：修复前它们是 false / Undeterminable。
            Assert.True(
                task.OutputManifestCrossChecked,
                "两层都列得出清单（第 0 层是抠出来的内嵌 ZIP、第 1 层是 Y.7z），必须真的逐条核对过。"
                + Diagnose(harness));

            Assert.Equal(ResultCompleteness.Complete, ResultCompletenessClassifier.Classify(task).State);

            // 内容物照常在。
            string[] payloads = Directory.GetFiles(harness.OutputRoot, "file.bin", SearchOption.AllDirectories);
            Assert.True(payloads.Length == 1, $"内容物应该正好一份，实际 {payloads.Length} 份。{Diagnose(harness)}");

            // 源包按"源包处理=放入其余物 + 删除操作=彻底删除"被处理掉：源文件不在、其余物也不在了。
            Assert.False(File.Exists(container), $"源包应该已按设置被彻底删除。{Diagnose(harness)}");
            Assert.Empty(FindRestDirectories(harness.OutputRoot));

            // 日志里必须有"可证完整"那一行依据（引用常量比对，不手抄中文）。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains(StatusText.CompletenessCompleteFormat, StringComparison.Ordinal));

            Assert.Equal(0, outcome.ContinuationLayers);
        }

        // ================================================================ 形状 2：真的取不到清单

        /// <summary>
        /// **真的取不到清单那一档**：引擎能解开、但列不出目录（真机里的加密头 `-mhe` / 损坏就是这一档）。
        ///
        /// <para>断言：L4 仍「判不出」、`OutputManifestCrossChecked = false`、源包一个字节不动、
        /// 其余物不生成，而且日志**点名是哪一层、为什么**（用户 2026-09-30：
        /// "日志不许再让人猜"）。</para>
        /// </summary>
        [Fact]
        public async Task 形状2_引擎列不出清单_仍判不出完整性_源包一个字节不动_日志点名原因()
        {
            var engine = new ListFailsEngine();

            Harness harness = CreateHarness(
                string.Empty,
                settings =>
                {
                    settings.RecursionMode = "SingleLayer";
                    settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                    settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
                },
                engine);

            // 样本本身要认得出来（识别走魔数）：真正的 ZIP，只是引擎"列不出清单"。
            string stage = Path.Combine(_root, "opaque-stage");
            Directory.CreateDirectory(stage);
            File.WriteAllBytes(Path.Combine(stage, "produced.bin"), new byte[1024]);

            string package = Path.Combine(_root, "opaque.zip");
            ZipFile.CreateFromDirectory(stage, package);

            await harness.AddPathsAsync(package);

            await harness.RunOneClickAsync();

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);

            Assert.False(task.OutputManifestCrossChecked);
            Assert.Equal(ResultCompleteness.Undeterminable, ResultCompletenessClassifier.Classify(task).State);

            // 红线：判不出 ⇒ 一个字节都不动。
            Assert.True(File.Exists(package), "判不出完整性时源包必须原地不动。");
            Assert.Empty(FindRestDirectories(harness.OutputRoot));

            /*
             * 日志必须说清"为什么没删源包"，而且要**点名是哪一层、什么原因**
             * —— 引用常量比对（⛔ 不手抄中文，⛔ 不比整句文案）。
             */
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("为什么没删源包", StringComparison.Ordinal)
                        && line.Contains("第 0 层", StringComparison.Ordinal));
        }

        // ================================================================ 形状 3：续解链末尾

        /// <summary>
        /// **续解链末尾的收尾必须真的执行**（真机日志第 97 行的承诺）。
        ///
        /// <para>形状：`outer.7z`（加密）里只有 `inner.7z.001/.002` 这一组分卷 —— 第一层**一个内容物都没有**，
        /// 源包因此记账成"留到链结束后补搬"；第二层才解出内容物。链尾那一步必须
        /// **既补搬源包、又按「彻底删除」把其余物处理掉**（老代码在这里静默 return）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 形状3_多层链跑完_链尾按设置处理源包与其余物()
        {
            string outer = BuildVolumeChain();

            Harness harness = CreateHarness(
                $"{OuterVolumePassword}\n",
                settings =>
                {
                    settings.RecursionMode = "SingleLayer";
                    settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                    settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
                    settings.VerboseLog = true;
                });

            // 只导入外壳：内层分卷是它解出来的（就是真机"第一层只出内层包"那个形状）。
            await harness.AddPathsAsync(outer);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            // 形状确实是"第一层只出过程物、第二层才出内容物"。
            Assert.True(outcome.ContinuationLayers >= 1, $"应该是多层链。{Diagnose(harness)}");

            string[] payloads = Directory.GetFiles(harness.OutputRoot, "content.txt", SearchOption.AllDirectories);
            Assert.True(payloads.Length == 1, $"第二层的内容物应该正好一份，实际 {payloads.Length} 份。{Diagnose(harness)}");

            // 链尾真的收尾了：源包 + 过程物（内层分卷）都不在，其余物目录也不在。
            Assert.False(File.Exists(outer), $"链尾应该按「彻底删除」处理掉源包。{Diagnose(harness)}");
            Assert.Empty(Directory.GetFiles(harness.OutputRoot, "inner.7z.*", SearchOption.AllDirectories));
            Assert.Empty(FindRestDirectories(harness.OutputRoot));

            Assert.True(
                harness.LogTexts.Any(
                    line => line.Contains(StatusText.CompletenessCompleteFormat, StringComparison.Ordinal)),
                "链尾收尾那一档必须留下\"可证完整\"的依据。" + Diagnose(harness));
        }

        /// <summary>
        /// **链中途失败 ⇒ 一个字节都不动**（同一条链的对照面）。
        ///
        /// <para>样本与上一条同形状，只是内层分卷的密码**不在密码本里** —— 第二层解不开，
        /// 链没跑完 ⇒ 源包与其余物都必须留在原地（不变量 1）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 形状3对照_链中途失败_源包与其余物一个字节都不动()
        {
            string outer = BuildVolumeChain();

            // 密码本故意给一个错的：第二层必然解不开。
            Harness harness = CreateHarness(
                "WrongPassword0\n",
                settings =>
                {
                    settings.RecursionMode = "SingleLayer";
                    settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                    settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
                });

            await harness.AddPathsAsync(outer);

            await harness.RunOneClickAsync();

            Assert.True(File.Exists(outer), "链没跑完时源包必须原地不动。");

            // 其余物即便已经生成（第一层的过程物就在里面），也一个字节都不许被删。
            Assert.NotEmpty(FindRestDirectories(harness.OutputRoot));
        }

        // ================================================================ 形状 4：既有行为不许回退

        /// <summary>
        /// **不回退**：单层常规包 + 源包=放入其余物 + 其余物=**保留** —— 老口径下就成立的那条路，
        /// 改动之后必须逐字照旧（源包进其余物、其余物留着、内容物照常、可证完整）。
        /// </summary>
        [SevenZipFact]
        public async Task 形状4_单层常规包_其余物保留档照旧()
        {
            string package = BuildPlainPackage("plain.7z");

            Harness harness = CreateHarness(
                $"{InnerPassword}\n",
                settings =>
                {
                    settings.RecursionMode = "SingleLayer";
                    settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                    settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
                });

            await harness.AddPathsAsync(package);

            await harness.RunOneClickAsync();

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);

            Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
            Assert.True(task.OutputManifestCrossChecked, Diagnose(harness));
            Assert.Equal(ResultCompleteness.Complete, ResultCompletenessClassifier.Classify(task).State);

            // 源包进其余物；其余物**保留**（保留下来的那一份里就是源包本身）。
            Assert.False(File.Exists(package), $"源包应该被搬进其余物。{Diagnose(harness)}");

            string rest = Assert.Single(FindRestDirectories(harness.OutputRoot));
            Assert.True(
                File.Exists(Path.Combine(rest, Path.GetFileName(package))),
                $"其余物里应该有源包。{Diagnose(harness)}");

            Assert.Single(Directory.GetFiles(harness.OutputRoot, "file.bin", SearchOption.AllDirectories));
        }

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

            return new Harness(vm, oneClick, outputRoot, logService);
        }

        private sealed class Harness
        {
            private readonly OneClickCoordinator _oneClick;

            public Harness(MainViewModel vm, OneClickCoordinator oneClick, string outputRoot, LogService log)
            {
                Vm = vm;
                _oneClick = oneClick;
                OutputRoot = outputRoot;
                Log = log;
            }

            public MainViewModel Vm { get; }

            public string OutputRoot { get; }

            public LogService Log { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);

            public Task AddPathsAsync(params string[] paths) => Vm.AddPathsAsync(paths);

            /// <summary>跑一键处理的流程部分（不弹任何对话框 —— 测试里没人在那儿点确定）。</summary>
            public Task<OneClickOutcome> RunOneClickAsync() => _oneClick.RunPipelineAsync(Vm.Tasks.ToList());
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

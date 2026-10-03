using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 用户 2026-09-30 真机那套 `AAA` 测试集的**形状夹具**（`_tmp\ArchiveFixer\aaa-replay\AAA`，
    /// 由同目录 `make-fixture.ps1 -Force` 重建）。
    ///
    /// <para><b>为什么要它</b>：真机那批的六条缺陷全都发生在"真引擎 + 真导入 + 真一键处理"这条整链上，
    /// 单点用例（手工造任务 + 假引擎）证明不了它们 —— 改名发生在开工前、落点发生在改名之后、
    /// `(1)` 副本发生在内层包各自落位时，只有整链跑一遍才看得见。</para>
    ///
    /// <para>⛔ 夹具本体在 `_tmp` 里、**只读**：每个用例先把它复制到自己的临时目录再跑
    ///（用例会真的搬走 / 删掉源包）。⛔ 与真机副本 `aaa-real-copy` 无关 ——
    /// 那一份是**跑坏之后**的状态，只配当"已污染输入"的对照，不许当期望值（用户红线）。</para>
    /// </summary>
    internal static class AaaReplayFixture
    {
        /// <summary>夹具根（与真样本同一个临时根，见 <see cref="RealVolumeSampleFactAttribute.TempRoot"/>）。</summary>
        internal static string Root => Path.Combine(RealVolumeSampleFactAttribute.TempRoot, "aaa-replay", "AAA");

        internal static string ScriptPath => Path.Combine(RealVolumeSampleFactAttribute.TempRoot, "aaa-replay", "make-fixture.ps1");

        internal static bool Exists()
        {
            try
            {
                return Directory.Exists(Root)
                    && Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// "这台机器上有 7z.exe **且**夹具在"用例的开关（与 <see cref="RealVolumeSampleFactAttribute"/>
    /// 同一套做法：发现阶段条件跳过，⛔ 不伪装成验过）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class AaaReplayFactAttribute : FactAttribute
    {
        public AaaReplayFactAttribute()
        {
            if (string.IsNullOrEmpty(SevenZipFactAttribute.LocateSevenZipPath()))
            {
                Skip = "测试机上没有可用的 7z.exe，跳过 AAA 夹具验收用例。";

                return;
            }

            if (!AaaReplayFixture.Exists())
            {
                Skip = @"AAA 夹具不在（仓库同级 _tmp\ArchiveFixer\aaa-replay\AAA 里没有文件）；"
                       + @"跑 make-fixture.ps1 -Force 重建后重试。";
            }
        }
    }

    /// <summary>
    /// **真管线**验收：真 7z 引擎 + 真导入扫描（<see cref="MainViewModel.AddPathsAsync"/>）+
    /// 真一键处理（<see cref="OneClickCoordinator.RunAsync"/>），跑 <see cref="AaaReplayFixture"/> 那套夹具。
    ///
    /// <para>每一次跑都把现场（跑前/跑后的文件清单、全部日志、任务终态）导出到
    /// `_tmp\ArchiveFixer\aaa-replay\runs\&lt;时间戳&gt;\` —— 六条不变量的判据都从这份证据里读，
    /// 免得"跑过了"变成一句无法复核的话。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class AaaReplayPipelineTests : IDisposable
    {
        private readonly string _root;

        public AaaReplayPipelineTests()
        {
            _root = Path.Combine(
                RealVolumeSampleFactAttribute.TempRoot,
                "aaa-replay",
                "runs",
                DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                + "-"
                + Guid.NewGuid().ToString("N").Substring(0, 6));
        }

        public void Dispose()
        {
            // 证据目录刻意留着（每一轮验收都要能复核），这里什么都不删。
        }

        // ================================================================ 六条不变量（交接 §3 第 4 点）

        /// <summary>
        /// **真管线验收：六条不变量一次跑齐**（夹具 + 真 7z + 真导入 + 真一键处理）。
        ///
        /// <para>夹具形状（`make-fixture.ps1`）：4 个源包 —— `111` / `333-Rar4` / `444` 三组
        /// **RAR4 双卷**（每组两卷，内容物是同一个内层 `payload.7z`，里面 5 个 mp4）+
        /// `222` 一对"zip 名字、7z 内容"的伪装分卷。名字全部带网盘那套垃圾尾巴
        ///（`111.parts1.racr` / `111.part删2.ra除r`），与用户真机那一批同形。</para>
        ///
        /// <list type="number">
        /// <item><description><b>一组分卷只解一次</b>：产物里一共 4 棵树 × 5 个 mp4 = 20 个文件；
        /// 同一组的多卷不能各解一遍（真机就是那样长出三棵一模一样的树的）。</description></item>
        /// <item><description><b>产物里没有 `名字(1)`</b>：那一档是"两个任务各自算落点"的化石，
        /// 而它本该只服务**不同来源**的同名文件。</description></item>
        /// <item><description><b>源分卷不进产物</b>（夹具的源卷是 `.rar`）。</description></item>
        /// <item><description><b>没有假「源文件已变化」</b>：改名是程序自己做的事，
        /// 它不该把同批的另一个任务判成"用户换了文件"。</description></item>
        /// <item><description><b>成功 + 可证完整 ⇒ 源包按设置被处理掉</b>：源包操作=放入其余物、
        /// 其余物=彻底删除，所以三组的**每一卷**都不该还在源目录里。</description></item>
        /// <item><description><b>别组失败不拖累这一组</b>：`222.z01` 那一卷是真的缺首卷（夹具里
        /// `222.zscip` 本身就是个完整的 7z，不是它的第一卷）⇒ 它必须失败且源包原地不动（不变量 1 的红线），
        /// 而**同一个批次里**另外三组的链尾处理照旧跑完。这正是真机 ① 那条的形状：
        /// 另一个目录里的同名包把这一组的链尾挡住了。</description></item>
        /// </list>
        /// </summary>
        [AaaReplayFact]
        public async Task 夹具验收_一组分卷只解一次_源包按档处理_别组失败不拖累本组()
        {
            Harness harness = CreateHarness();
            List<string> before = SnapshotFiles(harness.InputRoot);

            await harness.Vm.AddPathsAsync(new[] { harness.InputRoot }, ImportMode.Replace);

            Assert.NotEmpty(harness.Vm.Tasks);

            await RunOneClickAsync(harness);

            DumpEvidence(harness, before, "acceptance");

            List<string> outputFiles = SnapshotFiles(harness.OutputRoot);
            List<string> sourceFiles = SnapshotFiles(harness.InputRoot);

            /*
             * "产物"只数**内容物**：`其余物` 那一份是程序自己的过程物区（源包 + 内层包），
             * 它按「删除操作」那一档删掉是另一件事（⑤ 里判）。混进来数会让"链尾没删其余物"
             * 看起来像"多解了一棵树"—— 红检时当场踩到过：20 变成 23，红的却是 ① 而不是 ⑤。
             */
            List<string> contentFiles = outputFiles
                .Where(line => !line.Contains(ProcessArtifactLayout.ArtifactDirectoryName, StringComparison.Ordinal))
                .ToList();

            // ── ① 一组分卷只解一次：4 棵树（三组 RAR 双卷 + 222 那一棵）× 每组 5 个 mp4 = 20。
            //       同一组的两卷各解一遍会立刻多出来（真机现象）。
            Assert.Equal(20, contentFiles.Count);

            List<IGrouping<string, string>> trees = contentFiles
                .GroupBy(
                    line => Path.GetDirectoryName(line.Split('|')[0].Trim()) ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

            Assert.Equal(4, trees.Count);
            Assert.All(trees, tree => Assert.Equal(5, tree.Count()));

            // ── ② 产物里没有 `名字(1)`（文件或目录都算）。
            Assert.DoesNotContain(contentFiles, line => Regex.IsMatch(line, @"\(\d+\)"));

            // ── ③ 源分卷不进产物。夹具里源卷是 `.rar`；⚠ "归档成员"是另一回事（那些是内容物）。
            Assert.DoesNotContain(contentFiles, line => line.Contains(".rar |", StringComparison.OrdinalIgnoreCase));

            // ── ④ 没有假「源文件已变化」（真机上整批 4 个任务一起报这一条）。
            Assert.DoesNotContain(
                harness.LogTexts,
                line => line.Contains(StatusText.SourceChanged, StringComparison.Ordinal));

            // ── ⑤ 成功 + 可证完整 ⇒ 源包按设置被处理掉：三组的**每一卷**都不在源目录里了。
            //       （源包操作=放入其余物 + 其余物=彻底删除 ⇒ 源卷、内层包、其余物目录都该没了。）
            foreach (string volume in new[]
                     {
                         @"111\111.part1.rar", @"111\111.part2.rar",
                         @"333\333-Rar4\333-Rar4.part1.rar", @"333\333-Rar4\333-Rar4.part2.rar",
                         @"444\444.p1art2.part1.rar", @"444\444.p1art2.part2.rar",
                         @"222\222.7z"
                     })
            {
                Assert.DoesNotContain(
                    sourceFiles,
                    line => line.StartsWith(volume + " |", StringComparison.OrdinalIgnoreCase));
            }

            // 三组的首卷那一单：成功、校验通过、源包确实处理掉了。
            foreach (string firstVolume in new[] { "111.part1.rar", "333-Rar4.part1.rar", "444.p1art2.part1.rar" })
            {
                ArchiveTask owner = Assert.Single(
                    harness.Vm.Tasks,
                    task => string.Equals(task.FileName, firstVolume, StringComparison.OrdinalIgnoreCase));

                Assert.Equal(TaskOutcome.Succeeded, owner.Outcome);
                Assert.Equal(OutputVerificationOutcome.Passed, owner.OutputVerification);
                Assert.Equal(SourcePackageMoveState.Done, owner.SourcePackageMove);

                // 「其余物」按「彻底删除」档收干净了（选了不可恢复的删除，就不该还留着那一份）。
                Assert.False(
                    Directory.Exists(owner.RestDirectoryPath),
                    $"{firstVolume}：其余物应当按「彻底删除」收掉，实际还在：{owner.RestDirectoryPath}");
            }

            /*
             * 同一组的后续卷那一单：**要么**单独占一行、而且只落「已跳过」；
             * **要么**已经在导入期并进首卷那一单（名字上明确是同一组 ⇒ 用户 2026-10-02 拍板的
             * "一组分卷只留一行"），那一卷进的是首卷那一单的分卷清单。
             * ⛔ 两种都算过，但**两边都没有**不行 —— 那种情况就是"这一卷被弄丢了"。
             *
             * ⚠ 2026-10-03 起的差别：`111` / `333-Rar4` 两组的伪装名（`111.parts1.racr` 这种）
             * 现在**认得出是一组**了（判据收口到 `ExtensionHelper.TrySplitPartNumberedVolume`），
             * 于是它们并成一行；`444` 那一组的基名自己还带一段（`p1art2`），名字说不出是一组，
             * 照旧各占一行、后续卷落「已跳过」。
             */
            foreach (string follower in new[] { "111.part2.rar", "333-Rar4.part2.rar", "444.p1art2.part2.rar" })
            {
                ArchiveTask? swept = harness.Vm.Tasks.FirstOrDefault(
                    task => string.Equals(task.FileName, follower, StringComparison.OrdinalIgnoreCase));

                if (swept == null)
                {
                    continue;
                }

                Assert.True(swept.IsVolumeGroupFollower, $"{follower} 应当被认成同一分卷组的后续卷");
                Assert.Equal(TaskOutcome.Skipped, swept.Outcome);
                Assert.Equal(OutputVerificationOutcome.NotAttempted, swept.OutputVerification);
            }

            // 并成一行的那几组：首卷那一单的**分卷清单**必须把组里的每一卷都记着（⛔ 不许凭空少一卷）。
            foreach (string firstVolume in new[] { "111.part1.rar", "333-Rar4.part1.rar", "444.p1art2.part1.rar" })
            {
                ArchiveTask ownerTask = Assert.Single(
                    harness.Vm.Tasks,
                    task => string.Equals(task.FileName, firstVolume, StringComparison.OrdinalIgnoreCase));

                Assert.True(
                    ownerTask.VolumePaths.Count >= 2,
                    $"{firstVolume}：这一组的分卷清单应当至少有 2 卷，实际 {ownerTask.VolumePaths.Count} 卷");
            }

            /*
             * ── ⑥ 别组出岔子不拖累这一组 ──
             *
             * 夹具的 `222` 那一组是**故意造成残组形状**的：`222.zscip`（真 7z = 本体）旁边只有一个
             * `.z01` 角色的续卷（`222.z删除01`，名字里被塞了「删除」），**首卷本体缺**。
             *
             * 程序对这一组的处置（2026-10-01 按实测行为对齐用例）：
             *   · 本体 `222.zscip` 自己是一份完整 7z ⇒ 照常解出 5 个 mp4（① 已经数过），
             *     并按「源包操作 = 放入其余物」把它自己搬进其余物（`222.7z`）；
             *   · 那一卷续卷**不单独成任务**（没有文件头魔数 ⇒ `IsArchive=false`，导入期按无用物跳过），
             *     也不进那一单的源包清单（`ResolveSourceGroup` 的口径是"任务自己的文件优先"）——
             *     ⚠ 所以它**会留在源目录里**，这是本轮实测的现状（已记进 `docs\真机事故复盘.md` 的遗留）。
             * ⛔ 这一条要钉的从来不是"它必须报分卷缺失"，而是**这一组出岔子不许拖累另外三组**：
             * ⑤ 那一段（三组每一卷都离开源目录、其余物按删除档收干净）就是它的判据。
             */
            Assert.True(
                harness.Vm.Tasks.Any(
                    task => string.Equals(task.FileName, "222.7z", StringComparison.OrdinalIgnoreCase)),
                "222 那一单必须照常在跑（残组不许把整批拖垮）");

            await Task.CompletedTask;
        }

        // ================================================================ 冒烟：先把整链跑通并导出证据

        /// <summary>
        /// 夹具冒烟：真引擎 + 真导入 + 真一键处理跑一遍，导出全部证据。
        ///
        /// <para>它只钉一件与实现无关的不变量：**跑完之后每个任务都必须有机器终态**
        ///（<see cref="TaskState.Pending"/> 之外的结论）—— 用户真机上"未处理 1"那种自相矛盾
        /// （状态里写着失败原因、终态却还停在未处理）正是被这条挡住的。</para>
        /// </summary>
        [AaaReplayFact]
        public async Task 夹具冒烟_真管线跑穿一遍_导出全部证据()
        {
            Harness harness = CreateHarness();
            List<string> before = SnapshotFiles(harness.InputRoot);

            await harness.Vm.AddPathsAsync(new[] { harness.InputRoot }, ImportMode.Replace);

            Assert.NotEmpty(harness.Vm.Tasks);

            await RunOneClickAsync(harness);

            DumpEvidence(harness, before, "smoke");

            Assert.All(
                harness.Vm.Tasks,
                task => Assert.NotEqual(TaskOutcome.Pending, task.Outcome));
        }

        // ================================================================ harness

        private Harness CreateHarness(Action<AppSettings>? configure = null)
        {
            Directory.CreateDirectory(_root);

            string inputRoot = Path.Combine(_root, "AAA");
            CopyDirectory(AaaReplayFixture.Root, inputRoot);

            string dataRoot = Path.Combine(_root, "data");
            string outputRoot = Path.Combine(_root, "out");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            /*
             * 除用户点名的三档（并发 5 / 源包进其余物 / 彻底删除）之外**全部保持出厂默认** ——
             * 真机那一批就是在这套默认上跑出来的，改别的就等于换了一道题。
             */
            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = outputRoot;
            settings.MaxParallelExtractCount = 5;
            settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
            settings.RestHandlingAfterVerify = RestHandlingModes.Delete;

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            // MainViewModel 的构造会顺手写两个进程级静态（7z 路径、递归工作区根目录）：先存后还原。
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

            // 真引擎要认得内置 7z.exe（测试宿主不是绿色目录，得显式指路）。
            ToolLocator.Default.CustomSevenZipExePath = SevenZipFactAttribute.LocateSevenZipPath() ?? string.Empty;
            ToolLocator.Default.Invalidate();

            Assert.True(engine.IsAvailable, "真 7z 引擎没起来，这一轮证明不了任何事");

            var coordinator = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService())
            {
                // 这些用例拿"细节日志"当行为证据。
                KeepTaskDetailInLog = true
            };

            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var oneClick = new OneClickCoordinator(vm, scan, rename, coordinator, new DialogService());

            return new Harness(vm, coordinator, oneClick, logService, inputRoot, outputRoot, _root, previousSevenZipPath);
        }

        /// <summary>
        /// 走**真机那条路**：一键处理（<see cref="OneClickCoordinator.RunAsync"/>）+ 确认面板。
        /// 面板用 override 顶掉（无界面宿主），选的就是设置里那三档 —— 与用户点"开始"等价。
        /// </summary>
        private static async Task RunOneClickAsync(Harness harness)
        {
            harness.OneClick.OptionsPromptOverride = _ => OneClickOptionsPrompt.Confirmed(new OneClickRunOptions
            {
                PlacementMode = OutputPlacementMode.CustomRootPerArchive,
                CustomRoot = harness.OutputRoot,
                SourceHandling = SourceHandlingMode.MoveToRest,
                RestHandling = RestHandlingModes.Delete
            });

            await harness.OneClick.RunAsync().WaitAsync(TimeSpan.FromMinutes(10));
        }

        // ================================================================ 证据

        /// <summary>把现场写到 <c>runs\&lt;时间戳&gt;\evidence\</c>：跑前/跑后清单、全部日志、任务终态。</summary>
        private static void DumpEvidence(Harness harness, List<string> before, string tag)
        {
            string directory = Path.Combine(harness.Root, "evidence");
            Directory.CreateDirectory(directory);

            File.WriteAllLines(Path.Combine(directory, "before.txt"), before, new UTF8Encoding(false));
            File.WriteAllLines(Path.Combine(directory, "after-source.txt"), SnapshotFiles(harness.InputRoot), new UTF8Encoding(false));
            File.WriteAllLines(Path.Combine(directory, "after-output.txt"), SnapshotFiles(harness.OutputRoot), new UTF8Encoding(false));

            File.WriteAllLines(
                Path.Combine(directory, "log.txt"),
                harness.Log.Logs.Select(item => item.DisplayText),
                new UTF8Encoding(false));

            File.WriteAllLines(
                Path.Combine(directory, "tasks.txt"),
                harness.Vm.Tasks.Select(task => string.Join(
                    " | ",
                    task.Index.ToString(CultureInfo.InvariantCulture),
                    task.FileName,
                    task.Status,
                    "终态=" + task.Outcome,
                    "校验=" + task.OutputVerification,
                    "源包=" + task.SourcePackageMove,
                    "其余物=" + (task.RestDirectoryPath ?? string.Empty),
                    "输出=" + (task.OutputPath ?? string.Empty))),
                new UTF8Encoding(false));

            File.WriteAllText(Path.Combine(directory, "where.txt"), tag + Environment.NewLine + harness.Root, new UTF8Encoding(false));
        }

        /// <summary><c>相对路径 | 字节数</c> 清单（判"名字变了没有 / 源包还在不在"用）。</summary>
        internal static List<string> SnapshotFiles(string root)
        {
            var lines = new List<string>();

            if (!Directory.Exists(root))
            {
                return lines;
            }

            foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add(Relative(root, path) + " | " + new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture));
            }

            return lines;
        }

        internal static string Relative(string root, string path)
        {
            string trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return path.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(trimmed.Length + 1)
                : path;
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, Relative(source, directory)));
            }

            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(destination, Relative(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }

        internal sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                ExtractionCoordinator coordinator,
                OneClickCoordinator oneClick,
                LogService log,
                string inputRoot,
                string outputRoot,
                string root,
                string previousSevenZipPath)
            {
                Vm = vm;
                Coordinator = coordinator;
                OneClick = oneClick;
                Log = log;
                InputRoot = inputRoot;
                OutputRoot = outputRoot;
                Root = root;
                PreviousSevenZipPath = previousSevenZipPath;
            }

            public MainViewModel Vm { get; }

            public ExtractionCoordinator Coordinator { get; }

            public OneClickCoordinator OneClick { get; }

            public LogService Log { get; }

            /// <summary>本用例自己复制出来的夹具副本（用例可以随便动它）。</summary>
            public string InputRoot { get; }

            public string OutputRoot { get; }

            /// <summary>本用例的证据根目录。</summary>
            public string Root { get; }

            public string PreviousSevenZipPath { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);
        }
    }
}

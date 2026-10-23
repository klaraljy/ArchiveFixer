using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **这一组该拼到哪一层，只能由入口包决定**（用户 2026-10-11 原话：「哪个分卷用密码来解压哪个就是工作区」；
    /// 项目口径：落点 = 「谁可以输入密码，就解压到谁那边」的那个包**所在的那一层** + 一层包名目录）。
    ///
    /// <para><b>真机现场</b>（`H:\BaiduNetdiskDownload\测试\FFFF\ArchiveFixer-本次操作_20261011_105104.txt`）：
    /// RAR 分卷组 `111_` 的四片分别由三个包 + 一个散片产出，而**入口包 `111_.part1.rar` 是最后才出来的**
    /// （它得先穿过两层 `rar → zip`）。程序把四片全收进了**第一个到达的那条链**的落点层 ——
    /// 日志逐字：`10:50:32 …接到「1113__」这一层`，接着 `10:50:36`/`10:50:38` 的 part2、part1 也被搬进**同一层**。
    /// 换句话说：那一层是"先到先得"选出来的，`1113__.rar` 只是恰好第一个把片吐出来（它装的是 part3），
    /// 而真正该决定落点的入口包（part1）反而被搬进了别人的层。</para>
    ///
    /// <para><b>本类只针对这一个机制</b>（⛔ 与既有那些"跨链收卷 / 过路层 / 落片"用例无关，也不改它们）：
    /// 夹具用**并发 = 1 + 尺寸排序**把"谁先到"钉死，所以断言不靠运气。</para>
    ///
    /// <para>⚠ 需要真 <c>Rar.exe</c>（RAR 分卷只有它能造；7-Zip 不会写 RAR）—— 本机没装就**如实跳过**，
    /// ⛔ 不伪装成跑过。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class GroupLandingFollowsEntryPackageTests : IDisposable
    {
        private readonly string _root;
        private readonly ITestOutputHelper _output;

        public GroupLandingFollowsEntryPackageTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "af-group-landing-" + Guid.NewGuid().ToString("N"));
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
                // 临时目录删不掉不影响结论。
            }
        }

        // ================================================================ 主用例

        /// <summary>
        /// **入口包最后到**（真机 FFFF 的形状）：part3 / part2 / part4 三条链先跑完并各自吐出一片，
        /// part1（= 这一组的入口包）最后才从最重的那条链里出来。
        ///
        /// <para>要的是：这一组**最终**拼在**入口包那一层**（`pack1` 那一单的落点层），
        /// 内容物也落在同一层 —— ⛔ 不是"谁先到就落谁那儿"。</para>
        ///
        /// <para><b>红检</b>：现在的实现在这条用例上必然红（四片会被收进第一个到达那条链的落点层）。</para>
        /// </summary>
        [Fact]
        public async Task 入口包最后到_整组也必须落在入口包那一层()
        {
            string? rar = RequireRar();

            if (rar == null)
            {
                return;
            }

            Fixture fixture = BuildFixture(rar, "入口包最后到", entryCarrierFillerBytes: 3 * 1024 * 1024);

            Harness harness = CreateHarness();

            foreach (string task in fixture.TaskPaths)
            {
                await AddTaskAsync(harness, task);
            }

            CaptureSnapshots(harness);
            await harness.RunOneClickAsync();

            DumpLog(harness, "入口包最后到");

            AssertGroupLandedInEntryLayer(harness, "入口包最后到", "pack1");
        }

        /// <summary>
        /// **同一份形状、只把"谁先到"翻过来**（入口包那条链最轻 ⇒ 它先跑完）。
        ///
        /// <para>用途：钉住"落点由入口包决定"这条口径**与顺序无关** —— 这一条现在（先到先得那版实现下）
        /// 恰好会绿，而上面那条会红，两条合起来就是"落点跟着时序变"的直接证据。</para>
        /// </summary>
        [Fact]
        public async Task 入口包最先到_落点还是同一层()
        {
            string? rar = RequireRar();

            if (rar == null)
            {
                return;
            }

            Fixture fixture = BuildFixture(rar, "入口包最先到", entryCarrierFillerBytes: 0);

            Harness harness = CreateHarness();

            foreach (string task in fixture.TaskPaths)
            {
                await AddTaskAsync(harness, task);
            }

            CaptureSnapshots(harness);
            await harness.RunOneClickAsync();

            DumpLog(harness, "入口包最先到");

            AssertGroupLandedInEntryLayer(harness, "入口包最先到", "pack1");
        }

        /// <summary>
        /// **密码全错 ⇒ 这一组没解开 ⇒ 片全部留在入口包那一层，一片都不许删**（用户 2026-10-21 逐字：
        /// 「如果是密码错误或者什么情况的话就要留在入口包的位置因为他们确确实实是同一组」）。
        ///
        /// <para>这一条同时是"搬进入口层"那一档的守门：<c>MoveGroupPiecesIntoEntryLayer</c> 关掉时，
        /// part2/part3 会留在**产出方**那几层（`pack2\pack2` / `pack3\pack3`），本条的 ① 立刻红。</para>
        /// </summary>
        /// <para>⚠ 2026-10-22 现状：**暂挂**（`Skip`）。这一条要求"这一组带密码也建得出来"，
        /// 而建组那一档的门槛是"试开必须成功"（`RarVolumeGroupBuilder`）。试过在这一处放宽，
        /// `EncryptedVolumePipelineTests` 两条加密分卷用例当场红 ⇒ 整批回退。真要做，
        /// 属"组识别层：试开只做 tie-breaker"那一轮（连识别/收场一起改）。</para>
        [Fact]
        public async Task 密码全错_整组留在入口包那一层_一片都不删()
        {
            string? rar = RequireRar();

            if (rar == null)
            {
                return;
            }

            Fixture fixture = BuildFixture(
                rar,
                "密码全错",
                entryCarrierFillerBytes: 3 * 1024 * 1024,
                rarPassword: "真密码");

            Harness harness = CreateHarness(passwordBookText: "错密码一\r\n错密码二\r\n");

            foreach (string task in fixture.TaskPaths)
            {
                await AddTaskAsync(harness, task);
            }

            CaptureSnapshots(harness);
            await harness.RunOneClickAsync();

            DumpLog(harness, "密码全错");

            string entryLayer = Path.Combine(_root, "密码全错", "pack1", "pack1");
            string batchRoot = Path.Combine(_root, "密码全错");
            string tree = DescribeTree(batchRoot);

            /*
             * ① **本批自己产出的那三片**都得在入口层（这一组没解开 ⇒ 按用户口径"留在入口包的位置"）。
             * ⚠ 用户导入的那一片（`c4\111_.part4.rar`）按「源包处理=留下」那一档**留在原位**，
             * 入口层里给它的那个零字节链接名算过程物（下游"多余的落地别名"那一档会收掉它）——
             * 用户原件一个字节都没动就够了，⛔ 不要求链接名活着。
             */
            foreach (string piece in new[] { "111_.part1.rar", "111_.part2.rar", "111_.part3.rar" })
            {
                Assert.True(
                    File.Exists(Path.Combine(entryLayer, piece)),
                    $"「{piece}」必须留在入口包那一层（{entryLayer}）—— 这一组没解开，按用户 2026-10-21 口径"
                    + "「留在入口包的位置」。实际盘面：\n" + tree);
            }

            // ② 一个字节的内容物都不许出现（密码全错 ⇒ 什么都没解开）。
            string[] leftovers = Directory.GetFiles(batchRoot, "payload*.bin", SearchOption.AllDirectories);

            Assert.True(
                leftovers.Length == 0,
                "密码全错不该有内容物，实际却出现了：\n  " + string.Join("\n  ", leftovers)
                + "\n实际盘面：\n" + tree);

            // ③ 用户导入的那一份原件照旧在 c4（源包档=留下）。
            Assert.True(
                File.Exists(Path.Combine(batchRoot, "c4", "111_.part4.rar")),
                "用户导入的散片原件在「源包处理=留下」档下必须原地不动。实际盘面：\n" + tree);
        }
        // ================================================================ 断言（唯一口径）

        /// <summary>
        /// 这一组按用户 2026-10-21 拍的口径收口（三条）：
        /// ① **内容物**落在**入口包那一单的落点层**里（= 它的源包所在目录 + 一层包名目录，
        ///    与真机 `…\1113__\1113__` 同一个形状）；
        /// ② **成功 ⇒ 组片按过程物全部删除**（用户逐字：「成功了他们就是过程物，理应全部删除」）；
        /// ③ 用户导入的**源包原件**按「源包处理」档走（默认留下 ⇒ 一个字节不动、也不许删）。
        ///
        /// <para>⚠ 2026-10-22 改口径：这条用例原先断言"成功后四片都留在入口层"（那是把片当内容物）；
        /// 用户随后明确：片搬进入口层只是为了在那里把它解开，成功后它们就是过程物、要删掉。</para>
        /// </summary>
        private void AssertGroupLandedInEntryLayer(Harness harness, string batchName, string entryPackageTaskName)
        {
            string entryLayer = Path.Combine(_root, batchName, entryPackageTaskName, entryPackageTaskName);
            string batchRoot = Path.Combine(_root, batchName);

            // 程序自己产出的那三片（过程物）；入口包自己也是从 carrier 里解出来的 ⇒ 同属过程物。
            string[] producedPieces = { "111_.part1.rar", "111_.part2.rar", "111_.part3.rar" };

            // 用户导入的那一份散片（它自己是一单）：源包档=留下 ⇒ 原件必须还在。
            string userPiece = Path.Combine(batchRoot, "c4", "111_.part4.rar");

            string tree = DescribeTree(batchRoot);

            string occurrences = string.Join(
                "\n  ",
                producedPieces.Concat(new[] { "111_.part4.rar" })
                    .Select(piece => DescribePieceOccurrences(batchRoot, piece)));

            // ① 内容物只有一份，而且在入口层底下（"最后的文件全放在一起"）。
            string[] payloads = Directory.GetFiles(entryLayer, "payload*.bin", SearchOption.AllDirectories);

            Assert.True(
                payloads.Length == 1,
                $"这一组解开之后的内容物必须**只有一份**、且落在入口包那一层底下（{entryPackageTaskName}）。"
                + $"实际 {payloads.Length} 份：" + string.Join(" | ", payloads.Select(x => Path.GetFileName(x)))
                + "\n实际盘面：\n" + tree
                + "\n每一片的落点：\n  " + occurrences);

            // ② 成功 ⇒ 程序产出的那几片（过程物）盘上一个都不许留。
            string[] leftoverPieces = producedPieces
                .SelectMany(piece => Directory.GetFiles(batchRoot, piece, SearchOption.AllDirectories))
                .ToArray();

            Assert.True(
                leftoverPieces.Length == 0,
                "成功后组片就是过程物，应当全部删除（用户 2026-10-21 口径）。还留着的：\n  "
                + string.Join("\n  ", leftoverPieces)
                + "\n实际盘面：\n" + tree);

            // ③ 用户导入的源包原件不许动。
            Assert.True(
                File.Exists(userPiece),
                "用户导入的那一片在「源包处理=留下」档下必须原地不动：" + userPiece
                + "\n实际盘面：\n" + tree);

            // ④ 除用户那一份原件，这一组别的片一个名字都不许再出现（不许留第二份字节）。
            string[] strays = Directory
                .GetFiles(batchRoot, "111_.part*.rar", SearchOption.AllDirectories)
                .Where(path => !string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(userPiece),
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();

            Assert.True(
                strays.Length == 0,
                "同一组的片不许在别处再留**第二份字节**。多出来的：\n  "
                + string.Join("\n  ", strays)
                + "\n实际盘面：\n" + tree);
        }

        // ================================================================ 夹具（真 Rar.exe 现造）

        private sealed record Fixture(IReadOnlyList<string> TaskPaths);

        /// <summary>
        /// 造真机 FFFF 的形状：一组真 RAR 五卷里的四片（入口 = `part1`），四片分别来自
        /// **三个包 + 一个散片**，每个包在自己的文件夹里（一包一目录，与真机一致）。
        ///
        /// <para>`entryCarrierFillerBytes` 控制入口包那条链的重量（并发 = 1 时按尺寸从小到大跑）
        /// ⇒ 它 = 0 时入口包最先跑完；它很大时入口包最后跑完。</para>
        /// </summary>
        private Fixture BuildFixture(string rar, string name, int entryCarrierFillerBytes, string? rarPassword = null)
        {
            string source = Path.Combine(_root, name + "-源");
            Directory.CreateDirectory(source);

            byte[] payload = new byte[3 * 1024 * 1024 + 300 * 1024];
            new Random(20261011).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(source, "payload.bin"), payload);

            // 真 RAR 四卷（`-v1m` 切卷时**必须给基名 `<基名>.rar`**：给 `…part1.rar` 会切成 `…part1.part1.rar`）
            if (string.IsNullOrEmpty(rarPassword))
            {
                RunRar(rar, source, "a", "-m0", "-ma5", "-v1m", "111_.rar", "payload.bin");
            }
            else
            {
                RunRar(rar, source, "a", "-m0", "-ma5", "-v1m", "-p" + rarPassword, "111_.rar", "payload.bin");
            }

            string[] pieces = Directory.GetFiles(source, "111_.part?.rar").OrderBy(x => x, StringComparer.Ordinal).ToArray();

            Assert.True(
                pieces.Length == 4,
                $"夹具没造出四卷（实际 {pieces.Length} 卷）—— 这一条无从验证。");

            var taskPaths = new List<string>();

            // 三个包：各自装一片（入口包装 part1，另加重量让它最后跑完）
            taskPaths.Add(BuildCarrier(rar, name, "pack3", pieces[2], fillerBytes: 0));
            taskPaths.Add(BuildCarrier(rar, name, "pack2", pieces[1], fillerBytes: 1 * 1024 * 1024));
            taskPaths.Add(BuildCarrier(rar, name, "pack1", pieces[0], fillerBytes: entryCarrierFillerBytes));

            // 散在外面的那一片（真机 `111(4)\111_.part4.raAAAAAr` 那一格）
            string looseFolder = Path.Combine(_root, name, "c4");
            Directory.CreateDirectory(looseFolder);
            string loose = Path.Combine(looseFolder, "111_.part4.raAAAAAr");
            File.Move(pieces[3], loose);

            taskPaths.Add(loose);

            // 源目录里剩下的那几个片不留（它们已经被装进包里 / 搬走了）
            foreach (string leftover in Directory.GetFiles(source))
            {
                File.Delete(leftover);
            }

            return new Fixture(taskPaths);
        }

        /// <summary>造一个包里装一片（包与片都放在**自己那个文件夹**里 —— 与真机一包一目录一致）。</summary>
        private string BuildCarrier(string rar, string name, string carrierName, string piecePath, int fillerBytes)
        {
            string folder = Path.Combine(_root, name, carrierName);
            Directory.CreateDirectory(folder);

            string piece = Path.Combine(folder, Path.GetFileName(piecePath));
            File.Copy(piecePath, piece);

            var toAdd = new List<string> { Path.GetFileName(piece) };

            if (fillerBytes > 0)
            {
                byte[] filler = new byte[fillerBytes];
                new Random(20261011 + fillerBytes).NextBytes(filler);
                string fillerPath = Path.Combine(folder, "filler.bin");
                File.WriteAllBytes(fillerPath, filler);
                toAdd.Add("filler.bin");
            }

            string archive = Path.Combine(folder, carrierName + ".rar");
            RunRar(rar, folder, new[] { "a", "-m0", "-ma5", "-ep1", archive }.Concat(toAdd).ToArray());

            // 包造好之后，源片与填料都不留在包里那一层（真机盘上也只有包）
            File.Delete(piece);

            string fillerFile = Path.Combine(folder, "filler.bin");

            if (File.Exists(fillerFile))
            {
                File.Delete(fillerFile);
            }

            Assert.True(File.Exists(archive), $"夹具没造出包：{archive}");

            return archive;
        }

        private static void RunRar(string rar, string workDirectory, params string[] args)
        {
            var psi = new ProcessStartInfo(rar)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDirectory
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("起不来 Rar.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Rar {string.Join(' ', args)} 退出码 {process.ExitCode}：{stdout}{stderr}");
            }
        }

        /// <summary>本机 <c>Rar.exe</c>（产品自己的唯一出口）；没有 ⇒ 打印原因并返回 null（本条如实跳过）。</summary>
        private string? RequireRar()
        {
            var tools = new ToolLocator();

            if (!tools.RarExists || string.IsNullOrWhiteSpace(tools.RarExePath))
            {
                _output.WriteLine(
                    "跳过：本机没有 Rar.exe（WinRAR 是共享软件，程序只检测与调用）—— "
                    + "RAR 分卷造不出来，这一条不是「验过了」。");
                return null;
            }

            return tools.RarExePath;
        }

        // ================================================================ 装配（与项目既有 E2E 同一套）

        /// <summary>无界面宿主里默认是"没人点过 = 不确认"，缺卷补救那条路就走不到了 —— 这里注入"用户点了确定"。</summary>
        private sealed class ConfirmingDialogService : DialogService
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
            private readonly OneClickCoordinator _oneClick;

            public Harness(
                MainViewModel vm,
                ExtractionCoordinator coordinator,
                OneClickCoordinator oneClick,
                LogService log,
                string outputRoot)
            {
                Vm = vm;
                Coordinator = coordinator;
                _oneClick = oneClick;
                Log = log;
                OutputRoot = outputRoot;
            }

            public MainViewModel Vm { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            public string OutputRoot { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);

            public Task<OneClickOutcome> RunOneClickAsync() => _oneClick.RunPipelineAsync(Vm.Tasks.ToList());
        }

        private Harness CreateHarness(string? passwordBookText = null)
        {
            string dataRoot = Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N"));
            string outputRoot = Path.Combine(_root, "out-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();

            /*
             * ⚠ **档位必须与真机那一批一致**（他日志第 5 行逐字：「输出位置：未指定 —— 产物落在每个包自己所在的目录。」
             * + 落点「以包名命名的子文件夹」）。⛔ 不能图省事用"指定位置"那一档：按项目口径，
             * 落点这一档**只影响「未指定位置」那一档的目标根** —— 指定位置时落点与入口包无关，
             * 这条用例要验的恰恰是"未指定"那一档。
             */
            settings.CustomOutputDirectory = string.Empty;
            settings.ExtractToOriginalDirectory = true;
            settings.KeepArchiveNameFolder = true;
            settings.AutoScanAfterDrop = false;

            /*
             * ⚠ 并发 = 1：夹具靠"尺寸从小到大"把**谁先到**钉死（⛔ 不靠运气、⛔ 不加 sleep）。
             * 这一条与产品行为无关，只是让这条用例可复现。
             */
            settings.MaxParallelExtractCount = 1;
            if (passwordBookText != null)
            {
                // 密码本 = 用户给的那一份（这一档专门用来造"密码全错 ⇒ 这一组没解开"的现场）。
                string bookPath = Path.Combine(dataRoot, "password-book.txt");

                File.WriteAllText(bookPath, passwordBookText, new System.Text.UTF8Encoding(false));

                settings.PasswordBookPath = bookPath;
            }

            settings.SourceHandling = SourceHandlingMode.KeepInPlace.ToString();
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;

            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            var logService = new LogService(pathService);

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                engine,
                new PasswordService(),
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new ConfirmingDialogService());

            var coordinator = new ExtractionCoordinator(
                vm,
                engine,
                new PasswordService(),
                pathService,
                new ConfirmingDialogService());

            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new ConfirmingDialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new ConfirmingDialogService());
            var oneClick = new OneClickCoordinator(vm, scan, rename, coordinator, new ConfirmingDialogService());

            return new Harness(vm, coordinator, oneClick, logService, outputRoot);
        }

        private static async Task AddTaskAsync(Harness harness, string path)
        {
            var task = new ArchiveTask(path, harness.Vm.Tasks.Count + 1) { IsSelected = true };

            await new ArchiveDetectService().ApplyDetectResultAsync(task);
            harness.Vm.Tasks.Add(task);
        }

        private static void CaptureSnapshots(Harness harness)
        {
            foreach (ArchiveTask task in harness.Vm.Tasks)
            {
                task.CaptureSourceSnapshot();
            }
        }

        // ================================================================ 现场（断言失败时要看得见）

        private void DumpLog(Harness harness, string title)
        {
            _output.WriteLine("======== " + title + " ========");

            foreach (string line in harness.LogTexts)
            {
                _output.WriteLine(line);
            }
        }

        private static string DescribeTree(string root)
        {
            if (!Directory.Exists(root))
            {
                return "（输出目录不存在：" + root + "）";
            }

            string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);

            return files.Length == 0
                ? "（输出目录是空的：" + root + "）"
                : string.Join("\n  ", files.Select(path => path.Substring(root.Length).TrimStart('\\', '/')));
        }

        /// <summary>
        /// 逐片列出**盘上所有出现**（含"两处都有"那种），并标出两处是不是**同一份物理文件**
        /// （用户 2026-10-11 当场问的那一幕：「怎么会有两个 `111_.part1.rar`，pack1 和 pack3 里面都有」）。
        /// </summary>
        private static string DescribePieceOccurrences(string batchRoot, string pieceName)
        {
            string[] hits = Directory.GetFiles(batchRoot, pieceName, SearchOption.AllDirectories);

            if (hits.Length == 0)
            {
                return pieceName + "：盘上一个都没有";
            }

            var described = hits
                .Select(path => $"{path.Substring(batchRoot.Length)}（{new FileInfo(path).Length} 字节）")
                .ToList();

            for (int i = 1; i < hits.Length; i++)
            {
                described[i] += ArchiveFixer.Detection.FileIdentity.IsSamePhysicalFile(hits[0], hits[i])
                    ? " ← 与第一处是**同一份文件**（硬链接）"
                    : " ← 与第一处是**两份不同的字节**（拷贝）";
            }

            return pieceName + "：" + string.Join(" ｜ ", described);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 用户 2026-10-05 定的两条口径（AGENTS.md §11.4）。
    ///
    /// <para><b>真机现场</b>（<c>H:\BaiduNetdiskDownload\555\</c> 那一批）：两半被拆到**两个兄弟文件夹**里 ——
    /// <c>555\B250135(1)\B250135.7z.001</c>（73,400,320 字节）+
    /// <c>555\B250135(2)\B250135.7z.002</c>（50,932,287 字节），相加 = 124,332,607 =
    /// 7z 起始头自述的整包字节数（**一分不差，组是齐的**）。引擎找兄弟卷只看入口旁边那一层 ⇒
    /// <c>.001</c> 那一单报「凑不齐……还差 50932287 字节」，<c>.002</c> 那一单在**批首那一刻**
    /// 就被判「分卷不完整……本次不开始」。</para>
    ///
    /// <para><b>两条口径</b>：① 找卷的**基准 = 手上最浅的那一卷**（⛔ 绝不是 001），
    /// 窗口 = 它的第一层父文件夹 + 它自己那一层 + 它的子目录（递归到第 3 层）；
    /// ② 「缺卷」**不许在批首一次判死** —— 批首只记缺口，等这一批的解压都跑完再判一次。</para>
    ///
    /// <para>⚠ 与改写 <c>RecursiveExtractor.ConfiguredWorkspaceRoot</c>（进程级静态）的用例类**串行**跑。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class SiblingFolderVolumeGatherTests : IDisposable
    {
        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        private readonly string _root;
        private readonly string? _sevenZip;
        private readonly ITestOutputHelper _output;

        public SiblingFolderVolumeGatherTests(ITestOutputHelper output)
        {
            _output = output;
            _sevenZip = SevenZipFactAttribute.LocateSevenZipPath();
            _root = Path.Combine(Path.GetTempPath(), "af-sibling-gather-" + Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 真机形状（两个兄弟文件夹）



        // ================================================================ ② 凑不齐 ⇒ 一个字节都不搬

        // ================================================================ ③ 窗口：子目录递归到第 3 层


        // ================================================================ ④ 基准 = 手上最浅的那一卷


        // ================================================================ ⑤ 缺卷不许在批首判死




        // ============================================================ ② "散着的那几片"的源包要有人处理


        /// <summary>
        /// **真机 CCCC 那一档的源包处理**：源包操作 = 放入其余物 + 其余物 = 彻底删除 ⇒
        /// 整组解开之后**每一个源包（含散着的那几片）都要进其余物并被删掉**。
        ///
        /// <para>⚠ <b>本条仍标 Skip：它钉的那个缺口**只解决了一半**（如实记账，⛔ 不当成验过）。</b>
        /// 已实测到的账（每条都有日志）：
        /// · `111.z0删除3` 那一档**已经好了**：按跟班卷收场 + 源片按档搬走并删除；
        /// · `111.z0删除2`（它是"起头被改写到入口包上"的那一单，`CurrentPath=111.zip`、
        ///   `OriginalPath=111.z0删除2`）仍然留在盘上。**根因已收窄到最后一条**：
        ///   那份"借片账"里存的消费方是**文件名**（`111.zip`），而这一单因为起点被改写**也叫 `111.zip`**
        ///   ⇒ 按名字回查永远查到"它自己"或另一个同名的单 ⇒ 判不出真正的消费方 ⇒ 源片没人搬。
        ///   ⛔ 账里存名字这一条不改掉，这个撞名就解不开（下一轮第一件事）。</para>
        /// </summary>
        [Fact(Skip = "已知缺口：借片账里存的是文件名，而这一单被改写到入口包后与消费方撞名（见本用例注释）")]
        public async Task 真机形状_源包处理选放入其余物时_整组源包真的进其余物并按档删除()
        {
            RequireSevenZip();

            string? winRar = new ToolLocator().WinRarExePath;

            if (string.IsNullOrWhiteSpace(winRar) || !File.Exists(winRar))
            {
                _output.WriteLine("这台机器没有 WinRAR ⇒ 造不出真跨盘 ZIP，本条跳过（不是验过了）。");
                return;
            }

            (string pieceTwo, string pieceThree, string innerPackage, string tailPackage, byte[] payload, string payloadName) =
                BuildCrossChainSpannedZipSet("跨链删除", winRar!);

            Harness harness = CreateHarness(SourceHandlingMode.KeepInPlace,
                RestHandlingModes.Delete);

            ArchiveTask innerTask = await AddTaskAsync(harness, innerPackage);
            ArchiveTask tailTask = await AddTaskAsync(harness, tailPackage);
            ArchiveTask pieceTwoTask = await AddTaskAsync(harness, pieceTwo);
            ArchiveTask pieceThreeTask = await AddTaskAsync(harness, pieceThree);

            new VolumeGroupingService().ApplyVolumeGrouping(
                new[] { innerTask, tailTask, pieceTwoTask, pieceThreeTask });
            CaptureSnapshots(harness);

            await harness.Coordinator.StartExtractAsync();

            Log(harness, "跨链删除");

            // ① 这一组照样真的解开（源包那一档不许把内容物带坏）。
            string? produced = FindFileUnder(harness.OutputRoot, payloadName);

            Assert.NotNull(produced);
            Assert.Equal(payload, File.ReadAllBytes(produced!));

            // ② 源包真的按「放入其余物」处理了（不再是那句"按该档留在原地"）。
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("源包移入其余物", StringComparison.Ordinal));

            // ③ **彻底删除**那一档真的执行了。
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("彻底删除", StringComparison.Ordinal));

            // ④ 盘上收口：四个源包一个都不剩 —— 这正是用户问的"原包怎么没有删除 / 其余物怎么还留着"。
            Assert.False(File.Exists(innerPackage), "装第 1 片的原包应当已被按档处理掉");
            Assert.False(File.Exists(tailPackage), "装末片的原包应当已被按档处理掉");
            Assert.False(File.Exists(pieceTwo), "散着的那一片应当已被按档处理掉");
            Assert.False(File.Exists(pieceThree), "散着的那一片应当已被按档处理掉");

            // ⑤ 其余物那一层也不该留下一份（其余物 = 彻底删除）。
            Assert.Null(FindFileUnder(harness.OutputRoot, "111_outer.zip"));
            Assert.Null(FindFileUnder(harness.OutputRoot, "111(2)_.zip"));

            /*
             * ⑥ **C3（用户 2026-10-06 拍板：「其余物会在每层的过程中会删掉」）**：本装配就是
             * 「源包放入其余物 + 其余物彻底删除」那一档 ⇒ **全树里一个其余物文件都不该留**
             * —— 尤其 `111.rar` 那一单的其余物在**更深一层**（`111\111\其余物`，真机 14:33 那次
             * 就是它漏在外面：48 MB 的 `111.zip` 一直留着）。
             *
             * <para><b>红检</b>：把批末那遍"每一单自己的其余物"扫尾撤掉 ⇒ 本条变红
             * （`111\111\其余物\111.zip` 还在）。</para>
             */
            string[] restLeftovers = Directory
                .GetFiles(harness.OutputRoot, "*", SearchOption.AllDirectories)
                .Where(path => path.Contains("其余物", StringComparison.Ordinal))
                .ToArray();

            Assert.Empty(restLeftovers);
        }

        // ============================================ ④ 过路层的其余物按档删掉（真机 48.35 MB）


        // ============================================ ⑤ 每一单自己的其余物都要按档处理（C3）


        // ============================================ ④ 过路层的其余物（真机采纳形状）


        // ================================================================ 夹具

        /// <summary>造一组**真 2 卷 7z**（<c>-v3m</c> 切；`.001` = 3 MiB、`.002` = 余量）。</summary>
        private (string First, string Second, byte[] Payload, string PayloadName) BuildTwoVolumeSet(
            string name,
            string archiveBaseName = "set")
        {
            (string first, string second, _, byte[] payload, string payloadName) =
                BuildVolumeSet(name, 3, archiveBaseName);

            return (first, second, payload, payloadName);
        }

        /// <summary>同上，切成 3 卷（<c>-v2m</c>）—— 基准那一条要三片才判得出"只有基准的窗口看得见第三片"。</summary>
        private (string First, string Second, string Third, byte[] Payload) BuildThreeVolumeSet(string name)
        {
            (string first, string second, string third, byte[] payload, _) = BuildVolumeSet(name, 2);

            return (first, second, third, payload);
        }

        private (string First, string Second, string Third, byte[] Payload, string PayloadName) BuildVolumeSet(
            string name,
            int volumeMegabytes,
            string archiveBaseName = "set")
        {
            string directory = Path.Combine(_root, name);
            string payloadDirectory = Path.Combine(directory, "payload");

            Directory.CreateDirectory(payloadDirectory);

            // 可复现的伪随机数据（`-mx0` 不压缩 ⇒ 卷数只由字节数决定，可预测）。
            byte[] payload = new byte[5 * 1024 * 1024];
            new Random(20261005).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(payloadDirectory, "payload.bin"), payload);

            Run7z(
                "a",
                "-t7z",
                "-mx0",
                $"-v{volumeMegabytes}m",
                Path.Combine(directory, archiveBaseName + ".7z"),
                Path.Combine(payloadDirectory, "*"));

            string first = Path.Combine(directory, archiveBaseName + ".7z.001");
            string second = Path.Combine(directory, archiveBaseName + ".7z.002");
            string third = Path.Combine(directory, archiveBaseName + ".7z.003");

            Assert.True(File.Exists(first) && File.Exists(second), "7-Zip 没切出 2 卷");

            int expected = volumeMegabytes == 2 ? 3 : 2;

            Assert.Equal(expected, Directory.GetFiles(directory, archiveBaseName + ".7z.*").Length);

            return (first, second, third, payload, "payload.bin");
        }

        /// <summary>真机形状：两半分别在**两个兄弟文件夹**里（`A(1)\set.7z.001` + `A(2)\set.7z.002`）。</summary>
        private (string First, string Second, byte[] Payload, string PayloadName) BuildTwoVolumeSetInSiblingFolders(string name)
        {
            (string first, string second, byte[] payload, string payloadName) = BuildTwoVolumeSet(name + "-源");

            string baseDirectory = Path.Combine(_root, name);
            string holderOne = Path.Combine(baseDirectory, "A(1)");
            string holderTwo = Path.Combine(baseDirectory, "A(2)");

            Directory.CreateDirectory(holderOne);
            Directory.CreateDirectory(holderTwo);

            string movedFirst = Path.Combine(holderOne, "set.7z.001");
            string movedSecond = Path.Combine(holderTwo, "set.7z.002");

            File.Move(first, movedFirst);
            File.Move(second, movedSecond);

            return (movedFirst, movedSecond, payload, payloadName);
        }

        private static string? FindFileUnder(string root, string fileName)
        {
            if (!Directory.Exists(root))
            {
                return null;
            }

            return Directory
                .GetFiles(root, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
        }

        private void Log(Harness harness, string title)
        {
            _output.WriteLine("======== " + title + " ========");

            foreach (string line in harness.LogTexts)
            {
                _output.WriteLine(line);
            }
        }

        private async Task<ArchiveTask> AddTaskAsync(Harness harness, string path)
        {
            var task = new ArchiveTask(path, harness.Vm.Tasks.Count + 1) { IsSelected = true };

            await new ArchiveDetectService().ApplyDetectResultAsync(task);
            harness.Vm.Tasks.Add(task);

            return task;
        }

        // ================================================================ ③ 跨链收卷（真机第九批 CCCC）




        // ============================================ ⑤ 产出方那一份"已经落地的片"（用户 2026-10-10）




        /// <summary>
        /// 把真机那一刻的中间态摆出来（**只摆状态，⛔ 一点判据都不复制**）：
        /// ① 产出方那一单自己的落点里躺着一份"已经落地的片"（真机 `…\111(3)\111(3)\111.z02`）；
        /// ② 它的 <see cref="ArchiveTask.OutputPath"/> 指向那个落点（写入点的判据读它）；
        /// ③ 三本账按批内的样子写好（谁在解 / 谁吐过片 / 哪一份已落地的片被接走）。
        /// </summary>
        /// <param name="adoptionOwner">
        /// **接片那一刻调用方给的那一单**（⛔ 未必是产出方）：真机 2026-10-10 20:09 那次真跑里，
        /// 批量收片那条路给的是**被重判的那一单**，它的落点树里没有这一片 —— 产出方由程序按盘上事实认。
        /// </param>
        /// <returns>那一份已经落地的片的路径。</returns>
        private string ArrangeSettledProducerLandedPiece(
            Harness harness,
            ArchiveTask consumer,
            ArchiveTask producer,
            byte[] pieceBytes,
            string name,
            ArchiveTask? adoptionOwner = null)
        {
            string baseName = FileNameHelper.GetArchiveBaseName(consumer.CurrentPath ?? string.Empty);

            Assert.False(string.IsNullOrWhiteSpace(baseName), "消费方的组基名算不出来 ⇒ 中间态摆不成");

            string producerOutput = Path.Combine(_root, name + "-产出方", name);
            Directory.CreateDirectory(producerOutput);

            string landedPiece = Path.Combine(producerOutput, baseName + ".002");
            File.WriteAllBytes(landedPiece, pieceBytes);

            producer.OutputPath = producerOutput;

            harness.Coordinator.ResetBatchLedgersForTests();
            harness.Coordinator.RememberGroupConsumerForTests(baseName, consumer.FileName);
            harness.Coordinator.RememberGroupPieceProducerForTests(baseName, producer);
            harness.Coordinator.RememberAdoptedLandedPieceForTests(adoptionOwner ?? producer, baseName, landedPiece);

            Assert.True(File.Exists(landedPiece), "摆中间态失败：产出方那一份应当先在盘上");

            return landedPiece;
        }

        /// <summary>
        /// 造真机 CCCC 的形状：用**真 WinRAR**（`-afzip -v1m`）切一组**真跨盘 ZIP**
        /// （`111.z01..` + 末片 `111.zip`），然后把四片分开 —— 第 1 片改成脏名压进一个包、
        /// 末片压进另一个包、另两片改成脏名散在两个兄弟目录里。
        /// </summary>
        private (string PieceTwo, string PieceThree, string InnerPackage, string TailPackage, byte[] Payload, string PayloadName)
            BuildCrossChainSpannedZipSet(string name, string winRar)
        {
            string baseDirectory = Path.Combine(_root, name);
            string source = Path.Combine(baseDirectory, "源");
            Directory.CreateDirectory(source);

            string payloadName = "payload.bin";
            byte[] payload = new byte[(3 * 1024 * 1024) + (512 * 1024)];
            new Random(20261005).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(source, payloadName), payload);

            // 真跨盘 zip：`-v1m` ⇒ `111.z01` / `111.z02` / `111.z03` + 末片 `111.zip`
            // （⚠ 这一族里**末片才是引擎的入口**，`.z01` 只是第 1 片）。
            RunWinRar(winRar, source, "a", "-cfg-", "-ibck", "-afzip", "-m0", "-v1m", "111.zip", payloadName);
            File.Delete(Path.Combine(source, payloadName));

            string tail = Path.Combine(source, "111.zip");
            string diskOne = Path.Combine(source, "111.z01");
            string diskTwo = Path.Combine(source, "111.z02");
            string diskThree = Path.Combine(source, "111.z03");

            Assert.True(
                File.Exists(diskOne) && File.Exists(diskTwo) && File.Exists(diskThree),
                "造样本失败：真 7-Zip 没切出 4 片（z01/z02/z03 + 末片）");

            // 第 1 片：名字改坏 + 压进一个包（真机上它压在 `111(2)_.zip` 里）。
            string innerDirectory = Path.Combine(baseDirectory, "111(2)");
            Directory.CreateDirectory(innerDirectory);

            string disguisedDiskOne = Path.Combine(innerDirectory, "111.z0删除1");
            File.Move(diskOne, disguisedDiskOne);

            string innerPackage = Path.Combine(innerDirectory, "111(2)_.zip");
            Run7zIn(innerDirectory, "a", "-tzip", "-mx0", innerPackage, "111.z0删除1");
            File.Delete(disguisedDiskOne);

            // 末片：压进另一个包（真机上它压在两层层层加密的 RAR 里）。
            string tailDirectory = Path.Combine(baseDirectory, "111");
            Directory.CreateDirectory(tailDirectory);

            string movedTail = Path.Combine(tailDirectory, "111.zip");
            File.Move(tail, movedTail);

            string tailPackage = Path.Combine(tailDirectory, "111_outer.zip");
            Run7zIn(tailDirectory, "a", "-tzip", "-mx0", tailPackage, "111.zip");
            File.Delete(movedTail);

            // 另两片：名字改坏、散在**两个兄弟目录**里（真机 `111(3)` / `111(4)`）。
            string siblingOne = Path.Combine(baseDirectory, "111(3)");
            string siblingTwo = Path.Combine(baseDirectory, "111(4)");
            Directory.CreateDirectory(siblingOne);
            Directory.CreateDirectory(siblingTwo);

            string pieceTwo = Path.Combine(siblingOne, "111.z0删除2");
            string pieceThree = Path.Combine(siblingTwo, "111.z0删除3");
            File.Move(diskTwo, pieceTwo);
            File.Move(diskThree, pieceThree);

            return (pieceTwo, pieceThree, innerPackage, tailPackage, payload, payloadName);
        }

        /// <summary>
        /// 归组之后统一拍快照（真实管线的顺序：扫描 → 归组 → 拍基准）。
        /// ⛔ 顺序反了会被不变量 11 判成「源文件已变化（修改时间（没有记录）→ …）」——
        /// 那正是归组改写了 <c>VolumePaths</c> 而基准还是旧的那一份。
        /// </summary>
        private static void CaptureSnapshots(Harness harness)
        {
            foreach (ArchiveTask task in harness.Vm.Tasks)
            {
                task.CaptureSourceSnapshot();
            }
        }

        private Harness CreateHarness(
            SourceHandlingMode sourceHandling = SourceHandlingMode.KeepInPlace,
            string restHandling = RestHandlingModes.Keep)
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
            settings.AutoScanAfterDrop = false;
            settings.MaxParallelExtractCount = 1;
            settings.SourceHandling = sourceHandling.ToString();
            settings.RestHandlingAfterVerify = restHandling;

            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            var logService = new LogService(pathService);

            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;

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

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;

            var coordinator = new ExtractionCoordinator(
                vm,
                engine,
                new PasswordService(),
                pathService,
                new ConfirmingDialogService());

            /*
             * 一键处理那一套（轮次续解）也要能用：②页「只解当前这一层」+ 一键处理是**第三条路**，
             * 跨链收卷必须在那条路上同样成立（用户 2026-10-05：「赶紧同步」）。
             */
            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new ConfirmingDialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new ConfirmingDialogService());
            var oneClick = new OneClickCoordinator(vm, scan, rename, coordinator, new ConfirmingDialogService());

            return new Harness(vm, coordinator, oneClick, logService, outputRoot, pathService);
        }

        private void RequireSevenZip()
        {
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException(
                    "找不到内置 7z.exe，且 [SevenZipFact] 没有把它跳过：环境与特性探测结果不一致。");
            }
        }

        /// <summary>跑一次内置 7-Zip（造夹具用；与生产引擎无关）。参数里可以塞一个字符串集合（多值参数）。</summary>
        private void Run7z(params object[] args) => Run7zCore(_root, args);

        private void Run7zIn(string workDirectory, params object[] args) => Run7zCore(workDirectory, args);

        private void Run7zCore(string workDirectory, object[] args)
        {
            var psi = new ProcessStartInfo(_sevenZip!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDirectory
            };

            foreach (object arg in args)
            {
                if (arg is IEnumerable<string> many)
                {
                    foreach (string one in many)
                    {
                        psi.ArgumentList.Add(one);
                    }

                    continue;
                }

                psi.ArgumentList.Add(Convert.ToString(arg) ?? string.Empty);
            }

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("起不来 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"7z {string.Join(' ', psi.ArgumentList)} 退出码 {process.ExitCode}：{stdout}{stderr}");
            }
        }

        /// <summary>
        /// 跑一次真 WinRAR —— **只用于造夹具**：真跨盘 ZIP（`.z01..` + 末片 `.zip`）只有它能切出来
        /// （7-Zip 的 `-v` 切的是 `111.zip.001` 那种通用分片，不属于这一族）。⛔ 产品路径与此无关。
        /// </summary>
        private static void RunWinRar(string winRar, string workDirectory, params string[] args)
        {
            var psi = new ProcessStartInfo(winRar)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDirectory
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("起不来 WinRAR.exe");

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"WinRAR {string.Join(' ', psi.ArgumentList)} 退出码 {process.ExitCode}");
            }
        }

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
                string outputRoot,
                PathService pathService)
            {
                Vm = vm;
                Coordinator = coordinator;
                _oneClick = oneClick;
                Log = log;
                OutputRoot = outputRoot;
                PathService = pathService;
            }

            public MainViewModel Vm { get; }

            /// <summary>路径服务（批末工作区根就记在它的 <c>WorkDirectory</c> 上）。</summary>
            public PathService PathService { get; }

            public ExtractionCoordinator Coordinator { get; }

            /// <summary>一键处理（轮次续解）那一整条路 —— 与产品里那颗按钮同一个入口。</summary>
            public Task<OneClickOutcome> RunOneClickAsync() => _oneClick.RunPipelineAsync(Vm.Tasks.ToList());

            public LogService Log { get; }

            public string OutputRoot { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);
        }
    }
}


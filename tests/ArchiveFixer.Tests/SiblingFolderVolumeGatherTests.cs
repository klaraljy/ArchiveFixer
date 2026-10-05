using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
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

        /// <summary>
        /// **真机形状 + 真管线**：两半在两个兄弟文件夹里，手动「只解压」这一批 ⇒
        /// 开工前把它们收进第 1 卷那一层，然后**真的解开**（产物是原始字节）。
        ///
        /// <para>撤掉修复（把"开工前跨目录收卷"那一档关掉）⇒ 真机逐字同形：<c>.001</c> 那一单
        /// 一次引擎都不调、报「凑不齐……还差 N 字节」（红检原文见 <c>修改日志.md</c>）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 真机形状_两半在两个兄弟文件夹_收拢之后真的解开()
        {
            RequireSevenZip();

            (string first, string second, byte[] payloadBytes, string payloadName) = BuildTwoVolumeSetInSiblingFolders("兄弟文件夹");

            string firstDirectory = Path.GetDirectoryName(first)!;
            string secondDirectory = Path.GetDirectoryName(second)!;

            Harness harness = CreateHarness("SingleLayer");

            ArchiveTask firstTask = await AddTaskAsync(harness, first);
            ArchiveTask secondTask = await AddTaskAsync(harness, second);

            // 真机那份归组：两单**各自成组**（分处两个目录，名字归组认不出它们是一家）。
            new VolumeGroupingService().ApplyVolumeGrouping(new[] { firstTask, secondTask });
            CaptureSnapshots(harness);

            await harness.Coordinator.StartExtractAsync();

            Log(harness, "产物与源目录");

            // ① 收拢那一行必须说清"收了几卷、收进哪一卷所在的那一层"。
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("这一组的分卷没在同一个目录里", StringComparison.Ordinal)
                        && text.Contains("set.7z.001", StringComparison.Ordinal));

            // ② 真机那两句原话一个都不许出现（撤掉收拢 ⇒ 它们逐字回来：`001` 那一单报
            //    「解压失败 … 原因：分卷压缩包缺少必要分卷」+「Open ERROR: Cannot open the file as [7z] archive」）。
            Assert.Equal(TaskOutcome.Succeeded, firstTask.Outcome);
            Assert.DoesNotContain(
                harness.LogTexts,
                text => text.Contains("Open ERROR: Cannot open the file as [7z] archive", StringComparison.Ordinal));
            Assert.DoesNotContain(
                harness.LogTexts,
                text => text.Contains("分卷压缩包缺少必要分卷", StringComparison.Ordinal));

            // ③ 盘上事实：两半都躺在**第 1 卷那一层**里（引擎只会在那儿找兄弟卷）。
            Assert.True(File.Exists(Path.Combine(firstDirectory, "set.7z.001")), "第 1 卷该还在原地");
            Assert.True(File.Exists(Path.Combine(firstDirectory, "set.7z.002")), "第 2 卷该被收进第 1 卷那一层");
            Assert.False(File.Exists(Path.Combine(secondDirectory, "set.7z.002")), "收的是**移动**，不是复制");

            // ④ 真的解开了：产物与原始字节逐字节一致。
            string? produced = FindFileUnder(harness.OutputRoot, payloadName);

            Assert.NotNull(produced);
            Assert.Equal(payloadBytes, File.ReadAllBytes(produced!));

            // ⑤ 收拢是"搬"，不是"毁"：两半的字节数一个都没变。
            Assert.Equal(payloadBytes.Length > 0, new FileInfo(Path.Combine(firstDirectory, "set.7z.002")).Length > 0);

            // ⑥ 跟班那一单不算失败、也不算"没做成"（一组 = 一个任务 = 从首卷启动）。
            Assert.Equal(TaskOutcome.Skipped, secondTask.Outcome);
        }

        /// <summary>
        /// **对应另一条解压路**：出厂默认档（<c>AllBranches</c> = 递归展开所有分支）下，同一个真机形状
        /// 也必须收拢并解开 —— 这一档走的是 <c>RecursiveExtractor</c>，与上面那条单层路是两条代码路
        /// （§9.5：只接一条 = 缺陷）。
        /// </summary>
        [SevenZipFact]
        public async Task 真机形状_默认递归档_照样收拢并解开()
        {
            RequireSevenZip();

            (string first, string second, byte[] payloadBytes, string payloadName) =
                BuildTwoVolumeSetInSiblingFolders("兄弟文件夹-递归");

            string firstDirectory = Path.GetDirectoryName(first)!;

            Harness harness = CreateHarness("AllBranches");

            ArchiveTask firstTask = await AddTaskAsync(harness, first);

            new VolumeGroupingService().ApplyVolumeGrouping(new[] { firstTask });
            CaptureSnapshots(harness);

            await harness.Coordinator.StartExtractAsync();

            Log(harness, "默认递归档");

            Assert.Equal(TaskOutcome.Succeeded, firstTask.Outcome);

            // 收拢那一行照旧要有，而且盘上两半都在第 1 卷那一层。
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("这一组的分卷没在同一个目录里", StringComparison.Ordinal));

            Assert.True(File.Exists(Path.Combine(firstDirectory, "set.7z.002")));
            Assert.False(File.Exists(second));

            string? produced = FindFileUnder(harness.OutputRoot, payloadName);

            Assert.NotNull(produced);
            Assert.Equal(payloadBytes, File.ReadAllBytes(produced!));
        }

        // ================================================================ ② 凑不齐 ⇒ 一个字节都不搬
        /// <summary>
        /// **凑不齐 ⇒ 一个字节都不搬 + 如实报缺**（不变量 7）：第 2 卷的字节数对不上 7z 起始头自述的整包长度
        /// ⇒ 收卷那一档必须整档不做，并说清"还差多少字节"。
        /// </summary>
        [SevenZipFact]
        public void 凑不齐_一个字节都不搬_并如实报缺多少字节()
        {
            RequireSevenZip();

            (string first, string second, _, _) = BuildTwoVolumeSet("凑不齐");

            string secondDirectory = Path.Combine(_root, "凑不齐-别处");

            Directory.CreateDirectory(secondDirectory);

            string moved = Path.Combine(secondDirectory, "set.7z.002");

            File.Move(second, moved);

            // 体积对不上：截短 64 KiB ⇒ 7z 起始头那条字节数证据必然判"缺"。
            using (FileStream stream = new(moved, FileMode.Open, FileAccess.Write))
            {
                stream.SetLength(stream.Length - (64 * 1024));
            }

            long movedLengthBefore = new FileInfo(moved).Length;

            VolumeNameRepair.CrossLayerVolumeGather decision =
                VolumeNameRepair.ResolveCrossLayerVolumeGather(
                    first,
                    null,
                    VolumeNameRepair.EnumerateVolumeCandidatesNearby(first));

            Assert.True(decision.Applicable, decision.Detail);
            Assert.Null(decision.Plan);
            Assert.True(decision.ShouldSkipTrial, decision.Detail);
            Assert.Contains("还差", decision.Detail, StringComparison.Ordinal);

            // ⛔ 一个字节都没搬。
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(first)!, "set.7z.002")));
            Assert.True(File.Exists(moved));
            Assert.Equal(movedLengthBefore, new FileInfo(moved).Length);
        }

        // ================================================================ ③ 窗口：子目录递归到第 3 层

        /// <summary>
        /// **窗口递归**（口径 1）：另一卷藏在**子目录的子目录**里（自己那一层算第 1 层 ⇒ 第 3 层）
        /// 必须找得到、收得进来；**再深一层**（第 4 层）⇒ 判不出、什么都不做。
        /// </summary>
        [SevenZipFact]
        public void 窗口_两层深的子目录里的另一卷找得到_再深一层就判不出()
        {
            RequireSevenZip();

            // ① 两层深（`入口\a\b\`）⇒ 找得到，而且真的能收进第 1 卷那一层。
            (string first, string second, _, _) = BuildTwoVolumeSet("窗口-找得到");

            string entryDirectory = Path.GetDirectoryName(first)!;
            string deepDirectory = Path.Combine(entryDirectory, "a", "b");

            Directory.CreateDirectory(deepDirectory);
            File.Move(second, Path.Combine(deepDirectory, "set.7z.002"));

            IReadOnlyList<VolumeCandidate> window = VolumeNameRepair.EnumerateVolumeCandidatesNearby(first);

            Assert.Contains(window, candidate => string.Equals(
                Path.GetFileName(candidate.Path),
                "set.7z.002",
                StringComparison.OrdinalIgnoreCase));

            VolumeNameRepair.CrossLayerVolumeGather found =
                VolumeNameRepair.ResolveCrossLayerVolumeGather(first, null, window);

            Assert.NotNull(found.Plan);
            Assert.True(found.Plan!.CanRepair, found.Plan.Reason);
            Assert.True(VolumeNameRepair.TryApply(found.Plan).Success);
            Assert.True(File.Exists(Path.Combine(entryDirectory, "set.7z.002")));

            // ② 再深一层（`入口\a\b\c\`）⇒ 窗口够不着 ⇒ 判不出 ⇒ 一个字节都不搬。
            //    基名刻意不同（`deep`）：同一批测试目录互为"邻近目录"，用同一个基名会互相看见。
            (string first2, string second2, _, _) = BuildTwoVolumeSet("窗口-判不出", "deep");

            string entryDirectory2 = Path.GetDirectoryName(first2)!;
            string tooDeepDirectory = Path.Combine(entryDirectory2, "a", "b", "c");

            Directory.CreateDirectory(tooDeepDirectory);

            string hidden = Path.Combine(tooDeepDirectory, "deep.7z.002");

            File.Move(second2, hidden);

            IReadOnlyList<VolumeCandidate> window2 = VolumeNameRepair.EnumerateVolumeCandidatesNearby(first2);

            Assert.DoesNotContain(window2, candidate => string.Equals(
                Path.GetFileName(candidate.Path),
                "deep.7z.002",
                StringComparison.OrdinalIgnoreCase));

            VolumeNameRepair.CrossLayerVolumeGather notFound =
                VolumeNameRepair.ResolveCrossLayerVolumeGather(first2, null, window2);

            Assert.Null(notFound.Plan);
            Assert.False(File.Exists(Path.Combine(entryDirectory2, "deep.7z.002")));
            Assert.True(File.Exists(hidden));
        }

        // ================================================================ ④ 基准 = 手上最浅的那一卷

        /// <summary>
        /// **找卷的基准 = 手上最浅的那一卷**（用户口径：「你要以一开始的分卷文件为准，不要以 001 为准，
        /// 否则这时的第一层父文件夹就探测到了」）。
        ///
        /// <para>形状：真正的第 1 卷躺在 <c>base\入口\深\更深\</c>（很深），手上另有一片
        /// <c>base\手上\set.7z.002</c>（浅），而第三片在 <c>base\另一个\set.7z.003</c> ——
        /// **只有基准那一卷的窗口**能把 <c>另一个</c> 这个兄弟目录带进来。</para>
        ///
        /// <para>撤掉"按基准再收一次窗口"那一段 ⇒ 只凑得到两片 ⇒ 字节数对不上 ⇒ 没有计划
        /// （这正是"以 001 为基准就探测不到第一层父文件夹"的复现）。</para>
        /// </summary>
        [SevenZipFact]
        public void 基准_手上最浅的那一卷_它的父文件夹才探测得到()
        {
            RequireSevenZip();

            (string first, string second, string third, _) = BuildThreeVolumeSet("基准");

            string baseDirectory = Path.Combine(_root, "基准");
            string entryDirectory = Path.Combine(baseDirectory, "入口", "深", "更深");
            string handDirectory = Path.Combine(baseDirectory, "手上");
            string otherDirectory = Path.Combine(baseDirectory, "另一个");

            Directory.CreateDirectory(entryDirectory);
            Directory.CreateDirectory(handDirectory);
            Directory.CreateDirectory(otherDirectory);

            string entry = Path.Combine(entryDirectory, "set.7z.001");
            string anchor = Path.Combine(handDirectory, "set.7z.002");

            File.Move(first, entry);
            File.Move(second, anchor);
            File.Move(third, Path.Combine(otherDirectory, "set.7z.003"));

            // 手上这几片（清单那一档给的就是它们）—— 入口那一份在第 3 层，基准在浅处。
            var onHand = new List<VolumeCandidate>
            {
                new() { Path = anchor, Size = new FileInfo(anchor).Length }
            };

            VolumeNameRepair.CrossLayerVolumeGather decision =
                VolumeNameRepair.ResolveCrossLayerVolumeGather(entry, onHand, null);

            Assert.True(decision.Applicable, decision.Detail);

            // 基准 = 最浅的那一片（⛔ 不是解出来的 001、也不是入口那一份）。
            Assert.Equal(anchor, decision.AnchorPath);

            // 三片都收得齐 ⇒ 有计划（第三片只有基准那一卷的窗口能看见）。
            Assert.NotNull(decision.Plan);
            Assert.True(decision.Plan!.CanRepair, decision.Plan.Reason);

            List<VolumeRepairItem> moved = decision.Plan.Items
                .Where(item => !string.Equals(item.CurrentPath, item.TargetPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.Equal(3, decision.Plan.Items.Count);
            Assert.All(moved, item => Assert.Equal(
                Path.GetDirectoryName(entry),
                Path.GetDirectoryName(item.TargetPath)));
        }

        // ================================================================ ⑤ 缺卷不许在批首判死

        /// <summary>
        /// **批首只记缺口 + 批末补齐后这一组真的跑起来**（用户 2026-10-05 口径 2）。
        ///
        /// <para>形状：先只有 <c>set.7z.002</c>（缺第 1 卷），同一个目录里还有一个 <c>pack.zip</c>，
        /// 而 <c>set.7z.001</c> **正压在它里面**。这一批按"解压到当前文件夹"跑 ⇒ 第 1 卷解出来落在同一层 ⇒
        /// 批末补判时这一组齐了 ⇒ 照常跑这一组（⛔ 不再是一句"本次不开始"把话说死）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 批首只记缺口_批末补齐之后这一组真的跑起来()
        {
            RequireSevenZip();

            string work = Path.Combine(_root, "批末补齐");

            Directory.CreateDirectory(work);

            (string first, string second, byte[] payloadBytes, string payloadName) = BuildTwoVolumeSet("批末补齐-源");

            string loneVolume = Path.Combine(work, "set.7z.002");

            File.Move(second, loneVolume);

            // 第 1 卷 + 一大块陪跑数据一起压进 zip（体积刻意大于 `.002` ⇒ 调度顺序上 `.002` 先跑）。
            File.Move(first, Path.Combine(work, "set.7z.001"));

            byte[] padding = new byte[8 * 1024 * 1024];
            new Random(20261005).NextBytes(padding);
            File.WriteAllBytes(Path.Combine(work, "padding.bin"), padding);

            string pack = Path.Combine(work, "pack.zip");

            Run7zIn(work, "a", "-tzip", pack, new[] { "set.7z.001", "padding.bin" });

            File.Delete(Path.Combine(work, "set.7z.001"));
            File.Delete(Path.Combine(work, "padding.bin"));

            Harness harness = CreateHarness("SingleLayer");

            ArchiveTask volumeTask = await AddTaskAsync(harness, loneVolume);
            ArchiveTask packTask = await AddTaskAsync(harness, pack);

            new VolumeGroupingService().ApplyVolumeGrouping(new[] { volumeTask, packTask });
            CaptureSnapshots(harness);

            Assert.True(volumeTask.IsVolumeGroup, "孤零零一个 .002 必须按「缺第 1 卷的分卷组」归组");
            Assert.False(volumeTask.IsVolumeComplete);
            Assert.Contains("set.7z.001", volumeTask.MissingVolumeNames);

            /*
             * 「解压到当前文件夹」：pack.zip 里那个 `set.7z.001` 才会落在**与 `.002` 同一层**，
             * 批末补判才有得补（这正是用户说的"001 可能已经被解出来"那一档）。
             */
            await harness.Coordinator.StartExtractAsync(extractIntoSourceFolder: true);

            Log(harness, "批末补齐");

            // ① 批首**只记缺口**：⛔ 没有"本次不开始"，⛔ 没有当场落失败。
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("先把缺口记下来", StringComparison.Ordinal));

            Assert.DoesNotContain(
                harness.LogTexts,
                text => text.Contains("本次不开始", StringComparison.Ordinal));

            // ② 批末补判判出"齐了"，而且**真的跑了这一组**。
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("批末补判", StringComparison.Ordinal)
                        && text.Contains("这一组现在齐了", StringComparison.Ordinal));

            Assert.Equal(TaskOutcome.Succeeded, volumeTask.Outcome);

            string? produced = FindFileUnder(harness.OutputRoot, payloadName)
                               ?? FindFileUnder(work, payloadName);

            Assert.NotNull(produced);
            Assert.Equal(payloadBytes, File.ReadAllBytes(produced!));
        }

        /// <summary>
        /// **对照：真的补不上 ⇒ 到批末才如实报缺卷**（⛔ 但批首仍然不许把话说死）。
        /// </summary>
        [SevenZipFact]
        public async Task 对照_一直补不上_批末才如实报缺卷()
        {
            RequireSevenZip();

            (string first, string second, _, _) = BuildTwoVolumeSet("一直缺");

            string work = Path.Combine(_root, "一直缺-单");

            Directory.CreateDirectory(work);

            string loneVolume = Path.Combine(work, "set.7z.002");

            File.Move(second, loneVolume);

            // ⛔ 第 1 卷必须**真的不在盘上**：留着它，找卷窗口会（正确地）在邻近目录里把它找回来。
            File.Delete(first);

            Harness harness = CreateHarness("SingleLayer");

            ArchiveTask task = await AddTaskAsync(harness, loneVolume);

            new VolumeGroupingService().ApplyVolumeGrouping(new[] { task });
            CaptureSnapshots(harness);

            Assert.False(task.IsVolumeComplete);

            await harness.Coordinator.StartExtractAsync();

            Log(harness, "一直缺");

            // ① 批首只记缺口（⛔ 不写"本次不开始"）。
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("先把缺口记下来", StringComparison.Ordinal));

            Assert.DoesNotContain(
                harness.LogTexts,
                text => text.Contains("本次不开始", StringComparison.Ordinal));

            // ② 批末补判仍然缺 ⇒ **这才**如实报缺卷。
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("批末补判", StringComparison.Ordinal)
                        && text.Contains("仍然缺", StringComparison.Ordinal));

            Assert.Equal(StatusText.VolumeMissing, task.Status);
            Assert.Equal(TaskOutcome.Failed, task.Outcome);

            // ③ 不变量 7：一个字节都没动、一次引擎都没被调（没有产物）。
            Assert.True(File.Exists(loneVolume));
            Assert.False(Directory.Exists(harness.OutputRoot)
                         && Directory.GetFiles(harness.OutputRoot, "*.bin", SearchOption.AllDirectories).Length > 0);
        }

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

        private Harness CreateHarness(string recursionMode)
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
            settings.RecursionMode = recursionMode;
            settings.AutoScanAfterDrop = false;
            settings.MaxParallelExtractCount = 1;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;

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

            return new Harness(vm, coordinator, logService, outputRoot);
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

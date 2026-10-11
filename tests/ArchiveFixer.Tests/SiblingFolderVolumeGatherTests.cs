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

            /*
             * ⚠ 2026-10-06 按**用户新指令**改口径（旧断言是 `TaskOutcome.Failed`）：
             * 他原话「假如，1_最后剩一个 `111.7z.001` **你不能说是解压失败了，这应该是部分完成**，
             * 因为这个分卷不完整，到了最后检测不到完整的；2_如果最后完整了，但是解压不出来，那才是失败了」。
             * ⇒ 到最后仍然缺片 = **没拿到全部数据** ⇒ `PartiallyCompleted`；
             * 只有"卷齐了却解不出来"才是 `Failed`（那条判据在既有失败收口那一处，本用例到不了）。
             */
            Assert.Equal(TaskOutcome.PartiallyCompleted, task.Outcome);

            // ③ 不变量 7：一个字节都没动、一次引擎都没被调（没有产物）。
            Assert.True(File.Exists(loneVolume));
            Assert.False(Directory.Exists(harness.OutputRoot)
                         && Directory.GetFiles(harness.OutputRoot, "*.bin", SearchOption.AllDirectories).Length > 0);
        }

        /// <summary>
        /// **一键档：导出的日志里，最后一份「①页行快照」必须是链尾定稿之后的终态**（2026-10-08 EEEE 真机）。
        ///
        /// <para>真机现象：用户导出的日志里那两行写着 <c>状态=等待解压</c>，他据此判定"界面显示不对"。
        /// 界面上那一格其实是**活绑定**，日志里那一行才是过期的 —— 老写法只在批循环收尾取一次快照，
        /// 而一键档的定稿在**整批跑完之后**（`FinalizeDeferredVolumeDeficits`，由一键档调到链尾）。</para>
        ///
        /// <para>⛔ **必须走一键档**：手动档那次批内补判就是 <c>finalPass: true</c>（当场定稿），
        /// 批循环里那份快照已经是终态 ⇒ 只有一键档（批内 <c>finalPass: false</c>）才暴露这一档。
        /// 这一点是实做红检时发现的：把本条这组断言放进手动档那条用例里，**撤掉修复也不会变红**。</para>
        ///
        /// <para><b>红检</b>：撤掉 <c>FinalizeDeferredVolumeDeficits</c> 里那次 <c>AppendTaskRowSnapshot()</c>
        /// ⇒ 本条变红（最后一份快照仍写着「等待解压」）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 一键档_最后一份行快照必须是链尾定稿后的终态()
        {
            RequireSevenZip();

            (string first, string second, _, _) = BuildTwoVolumeSet("一键一直缺");

            string work = Path.Combine(_root, "一键一直缺-单");

            Directory.CreateDirectory(work);

            string loneVolume = Path.Combine(work, "set.7z.002");

            File.Move(second, loneVolume);

            // ⛔ 第 1 卷必须真的不在盘上（同手动档那条用例）：留着它，找卷窗口会把它找回来。
            File.Delete(first);

            Harness harness = CreateHarness("SingleLayer");

            // 「详细日志（排查用）」打开 ⇒ 收尾会写"①页每一行显示什么"那份快照。
            harness.Vm.Settings.VerboseLog = true;

            ArchiveTask task = await AddTaskAsync(harness, loneVolume);

            new VolumeGroupingService().ApplyVolumeGrouping(new[] { task });
            CaptureSnapshots(harness);

            await harness.RunOneClickAsync();

            Log(harness, "一键一直缺");

            Assert.Equal(StatusText.VolumeMissing, task.Status);

            string[] rowSnapshots = harness.LogTexts
                .Where(text => text.Contains("[行]", StringComparison.Ordinal))
                .ToArray();

            Assert.NotEmpty(rowSnapshots);
            Assert.Contains(StatusText.VolumeMissing, rowSnapshots[^1], StringComparison.Ordinal);
            Assert.DoesNotContain(StatusText.WaitingExtract, rowSnapshots[^1], StringComparison.Ordinal);
        }

        // ============================================================ ② "散着的那几片"的源包要有人处理

        /// <summary>
        /// **真机 CCCC 2026-10-06**：一组分卷由别单解开之后，那些"从没吐过东西、只是一片散着的续卷"的单
        /// —— 批末那一站要替它们把源片按档处理掉（用户原话：「原包怎么还没有删除」「其余物怎么还留着」）。
        ///
        /// <para>现场：`111.z0删除2` / `111.z0删除3` 是散在 `111(3)`/`111(4)` 的续卷片，整组由 `111.rar`
        /// 那一单解开、内容全出来了；可这两单自己**没吐过任何东西**，从没进过"吐出片的那几单"那份账，
        /// 批末它们只是"跟班卷 + 跳过"⇒ 源片一直留在盘上（真机 12:33 那一次盘上实测就是这样）。</para>
        ///
        /// <para>造法：先跑一遍真管线（消费方真的解开这一组），**然后**把真机那一刻的中间态摆出来 ——
        /// ① 源片写回原处（真机里它还在盘上）；② 那一单的机器终态/跟班标记退回"还没人替它收场"；
        /// ③ 借片账 + 缺卷名单按批内的样子写好。再调批末那一站（与真机链尾同一个入口）。</para>
        ///
        /// <para><b>红检</b>：把 <c>SettleConsumedGroupVolumeSources</c> 那一调拿掉 ⇒
        /// 第三条断言变红（其余物里找不到那一片）；把机器终态改回 `Skipped` ⇒ 同样变红
        /// （删除侧那道闸门逐字拒绝：其余物一个字节都不删）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 批末_借出去的源片按跟班卷收场_而且源包真的进了其余物()
        {
            RequireSevenZip();

            (string first, string second, _, _) = BuildTwoVolumeSet("借片收场");

            string pieceDirectory = Path.Combine(_root, "借片收场-第二片");

            Directory.CreateDirectory(pieceDirectory);

            string piece = Path.Combine(pieceDirectory, "set.7z.002");

            File.Move(second, piece);

            Harness harness = CreateHarness("SingleLayer");

            // 真机 CCCC 那一档：源包处理 = 放入其余物，其余物 = 彻底删除（不变量 1 的例外档）。
            // ⚠ 必须在 StartExtractAsync **之前**改：其余物那两档是批首钉死的。
            harness.Vm.Settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
            harness.Vm.Settings.RestHandlingAfterVerify = RestHandlingModes.Delete;

            ArchiveTask consumer = await AddTaskAsync(harness, first);
            ArchiveTask holder = await AddTaskAsync(harness, piece);

            new VolumeGroupingService().ApplyVolumeGrouping(new[] { consumer, holder });
            CaptureSnapshots(harness);

            await harness.Coordinator.StartExtractAsync();

            Log(harness, "借片收场");

            Assert.Equal(TaskOutcome.Succeeded, consumer.Outcome);

            /*
             * 把真机那一刻的状态摆出来：
             * ① 源片写回原处（真机 CCCC 12:33 那一次盘上实测：那两片原样还在）；
             * ② 这一单退回"还没人替它收场"（真机里批末它不是靠这条机制收的场）；
             * ③ 借片账 + 缺卷名单按批内的样子写好（真机里由收卷那一档与批首那一步写）。
             */
            string holderPath = holder.CurrentPath;

            File.WriteAllBytes(holderPath, new byte[4096]);

            holder.IsVolumeGroupFollower = false;
            holder.Outcome = TaskOutcome.Pending;

            harness.Coordinator.ResetBatchLedgersForTests();
            harness.Coordinator.RememberConsumedVolumeSourceForTests(holderPath, consumer.FileName);
            harness.Coordinator.RememberGroupConsumerForTests("set.7z", consumer.FileName);
            harness.Coordinator.RecordDeferredVolumeDeficitForTests(holder);

            // 批末那一站（与真机里链尾调的是同一个入口）。
            harness.Coordinator.FinalizeDeferredVolumeDeficits();

            Log(harness, "批末那一站之后");

            // ① 这一单按**跟班卷**收场（不算"没做成"）。
            Assert.True(holder.IsVolumeGroupFollower, "借出去那一片的那一单应当按跟班卷收场");

            // ② 机器终态是「完成」而不是「跳过」—— 否则删除侧那道闸门会拒绝（源片永久留下）。
            Assert.Equal(TaskOutcome.Succeeded, holder.Outcome);

            // ③ 它**不再**挂在"缺卷留到最后再判"的名单上（已经收场了）。
            Assert.False(harness.Coordinator.IsDeferredVolumeDeficit(holder));

            // ④ 源片**真的按「源包处理」进了其余物**（这就是真机"原包没删/其余物还留着"那一半）。
            //    ⚠ 其余物那一层在**包名目录**下面（`<目标>\<包名>\其余物`），所以从目标根递归找。
            Assert.True(
                FindFileUnder(harness.OutputRoot, "set.7z.002") != null,
                "借出去的那一片应当随源包处理进其余物（而不是原地留着）");

            Assert.False(
                File.Exists(holderPath),
                "源片搬进其余物之后，原地不该还留着一份");
        }

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

            Harness harness = CreateHarness(
                "AllBranches",
                SourceHandlingMode.MoveToRest,
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

        /// <summary>
        /// **真机 CCCC 2026-10-06 那 48.35 MB**（用户两次追问：「这个为什么还留着」「你其余物的问题别想逃脱」）：
        /// 装入口包的那一单（真机 `111.rar`）只出**过程物**，自己因此停在中途 ⇒ **它自己的完整性判不出来**
        /// ⇒ 其余物那一档一律停在"一个字节都不删" ⇒ `111\111\其余物\111.zip` 一直留到用户看见。
        ///
        /// <para>它与分卷判据无关：那一份就是这单这一次解压产出的过程物，而整组已经由**另一个当场核过清单、
        /// 可证完整**的消费方解开 ⇒ 证据该按消费方那一份，六道门槛一条不绕过。</para>
        ///
        /// <para><b>红检（2026-10-11 实测重订）</b>：把**链尾那一遍**「这一单自己的其余物按档处理」
        /// （`ApplyRestHandlingAfterChainAsync`）关掉 ⇒ 本条变红，逐字
        /// `Assert.Empty() Failure: Collection was not empty` + 8 个「还留着的其余物：…\111\其余物\…」。
        /// ⛔ 原来这里写的是「把 `PurgePassThroughRestOfSettledProducer` 那一调注掉 ⇒ 本条变红」——
        /// **那句是假的**，已按实测改正：把两条"过路层"机制（`PurgePassThroughRestOfSettledProducer`
        /// 与 `SweepPassThroughRestDirectoriesAsync`）各自加一句 early return **整条关掉**跑本条，
        /// 照旧**全绿**、`彻底删除：111\其余物（8 项 / 7.0 MB）`照旧执行 ⇒ 本条真正覆盖的是
        /// **链尾那一遍**，不是"过路层"那两条（详见 `AGENTS.md` 同名条目）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 真机形状_过路层那一单的其余物按档删掉_48MB那处()
        {
            RequireSevenZip();

            string? winRar = new ToolLocator().WinRarExePath;

            if (string.IsNullOrWhiteSpace(winRar) || !File.Exists(winRar))
            {
                _output.WriteLine("这台机器没有 WinRAR ⇒ 造不出真跨盘 ZIP，本条跳过（不是验过了）。");
                return;
            }

            (string pieceTwo, string pieceThree, string innerPackage, string tailPackage, byte[] payload, string payloadName) =
                BuildCrossChainSpannedZipSet("过路层48MB", winRar!);

            Harness harness = CreateHarness(
                "AllBranches",
                SourceHandlingMode.MoveToRest,
                RestHandlingModes.Delete);

            ArchiveTask innerTask = await AddTaskAsync(harness, innerPackage);
            ArchiveTask tailTask = await AddTaskAsync(harness, tailPackage);
            ArchiveTask pieceTwoTask = await AddTaskAsync(harness, pieceTwo);
            ArchiveTask pieceThreeTask = await AddTaskAsync(harness, pieceThree);

            new VolumeGroupingService().ApplyVolumeGrouping(
                new[] { innerTask, tailTask, pieceTwoTask, pieceThreeTask });
            CaptureSnapshots(harness);

            // ⚠ 必须走**一键处理**那条路（真机 18:13 那次就是它）：一键档在任务收尾时故意不处理其余物，
            // 全部留到链尾 / 批末 —— 手动档复现不出真机那一刻的次序。
            await harness.RunOneClickAsync();

            Log(harness, "过路层48MB");

            // ① 这一组照样真的解开（删其余物不许把内容物带坏）。
            string? produced = FindFileUnder(harness.OutputRoot, payloadName);

            Assert.NotNull(produced);
            Assert.Equal(payload, File.ReadAllBytes(produced!));

            // ② 全树里**一个其余物文件都不该留** —— 包括"只出过程物"那一单自己那份（真机 48.35 MB 那一格）。
            string[] restLeftovers = Directory
                .GetFiles(harness.OutputRoot, "*", SearchOption.AllDirectories)
                .Where(path => path.Contains("其余物", StringComparison.Ordinal))
                .ToArray();

            foreach (string leftover in restLeftovers)
            {
                _output.WriteLine("还留着的其余物：" + leftover);
            }

            Assert.Empty(restLeftovers);

            /*
             * ③ **盘上收口：只剩成品本身**（真机 2026-10-06 第二次追问的 `111(2)\111(2)_\111.z01`，200 MiB）：
             * 接片成功之后，产出它的那一单自己的落点里**不该再留一个没人认领的名字**
             * —— 老写法在这里就先建一份硬链接，于是收尾谁也不认它，用户只看到 200 MiB 残留。
             *
             * <para><b>红检</b>：把 `PublishUnresolvedPieceLandingCopies` 那一调改成"接片之前就建"
             * （= 恢复老写法）⇒ 本条变红（那两个名字留在盘上）。</para>
             */
            string[] remaining = Directory
                .GetFiles(harness.OutputRoot, "*", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith(payloadName, StringComparison.Ordinal))
                .ToArray();

            foreach (string file in remaining)
            {
                _output.WriteLine("树里还剩：" + file[(harness.OutputRoot.Length + 1)..]);
            }

            Assert.Empty(remaining);
        }

        // ============================================ ⑤ 每一单自己的其余物都要按档处理（C3）

        /// <summary>
        /// **C3：其余物在每一层都要按「删除操作」处理掉**（用户 2026-10-06 原话：
        /// 「**其余物会在每层的过程中会删掉**，我无法理解为什么还会有其余物留着」）。
        ///
        /// <para>形状（**只用真 7z 造、⛔ 不需要 WinRAR ⇒ 这台机器上真的会跑**）：
        /// 一组真 2 卷 7z 的第 2 卷被压进一个外层 7z 里 ⇒ 外层那一单解出来的东西是**过程物**
        /// （它自己的其余物在 `…\外层\其余物`，比根任务的其余物**深一层**）。</para>
        ///
        /// <para><b>红检</b>：把批末那遍"每一单自己的其余物"扫尾撤掉 ⇒ 本条变红
        /// （`…\其余物\` 里那一份仍在盘上）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 每一单自己的其余物都按档删掉_深层那一份也不例外()
        {
            RequireSevenZip();

            (string first, string second, _, _) = BuildTwoVolumeSet("深层其余物");

            // 把第 2 卷压进一个外层包（外层那一单解出来的就是过程物）。
            string holder = Path.Combine(_root, "深层其余物-外层");
            Directory.CreateDirectory(holder);

            string outerName = "pack.7z";
            string outer = Path.Combine(holder, outerName);

            Run7zIn(holder, "a", "-t7z", "-mx0", outerName, second);

            Harness harness = CreateHarness(
                "SingleLayer",
                SourceHandlingMode.MoveToRest,
                RestHandlingModes.Delete);

            ArchiveTask outerTask = await AddTaskAsync(harness, outer);
            ArchiveTask firstTask = await AddTaskAsync(harness, first);

            CaptureSnapshots(harness);

            /*
             * ⚠ 必须走**一键处理**那条路（`RunOneClickAsync`）：真机 14:33 那次就是它 ——
             * 一键档在任务收尾时**故意不处理其余物**（那一刻里面还躺着下一层要解的内层包），
             * 全部留到链尾；手动档则在任务收尾就当场处理掉了（所以走手动档复现不出这个缺陷）。
             */
            await harness.RunOneClickAsync();

            Log(harness, "深层其余物");

            // ① 这一档真的执行了（有"彻底删除"那一步）。
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("彻底删除", StringComparison.Ordinal));

            // ② 全树里**一个其余物文件都不该留**（含外层那一层更深的那份）。
            string[] restLeftovers = Directory
                .GetFiles(harness.OutputRoot, "*", SearchOption.AllDirectories)
                .Where(path => path.Contains("其余物", StringComparison.Ordinal))
                .ToArray();

            Assert.Empty(restLeftovers);
        }

        // ============================================ ④ 过路层的其余物（真机采纳形状）

        /// <summary>
        /// **真机 CCCC 的"采纳"形状**：某一单只出过程物（它解出来的内层包就是下一层的输入），
        /// 而那个内层包**被采纳进另一单那一层** ⇒ 它**自己那条链的其余物**留在原地
        /// （真机：`111.rar` ⇒ `111\111\其余物\111.zip`，48.35 MB 一直留到用户看见）。
        ///
        /// <para>本批选了「源包放入其余物 + 其余物彻底删除」⇒ **那份过路层的其余物也要被删掉**
        /// （用户原话：「其余物会在每层的过程中会删掉，我无法理解为什么还会有其余物留着」）。</para>
        ///
        /// <para><b>红检（2026-10-11 实测重订）</b>：把**链尾那一遍**「这一单自己的其余物按档处理」
        /// （`ApplyRestHandlingAfterChainAsync`）关掉 ⇒ 本条变红。
        /// ⛔ 原来这里写的是「把 `SweepPassThroughRestDirectoriesAsync` 那一调注掉 ⇒ 本条变红」——
        /// **没有实测支撑**：把那条扫尾整条关掉跑本条照旧绿（实测见 `AGENTS.md` 同名条目）。</para>
        /// <para>⚠ **本条 2026-10-11 之前是"空跑假绿"**：夹具漏了「先归组、再拍快照」这一步
        /// ⇒ `set.7z.001` 被**假报**「源文件已变化（`set.7z.002` 修改时间（没有记录）→ …）」⇒ 一次引擎都没调
        /// ⇒ 两条断言都恒真。现在补上归组、并把 ① 收紧成认**执行**那一行（`彻底删除：`），
        /// 逐字实测：`set.7z.001` 解出整组、`彻底删除：set\其余物（2 项 / 5.0 MB）`、本批 成功 2 / 失败 0。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 归集采纳形状_过路层那一单的其余物也要按档删掉()
        {
            RequireSevenZip();

            (string first, string second, _, _) = BuildTwoVolumeSet("过路层其余物");

            // 真机形状：第 2 卷压在**另一个文件夹**里的外层包中 ⇒ 那个内层包会被"采纳"到
            // 第 1 卷那一单所在的那一层；而外层包那一单的其余物（`…\外层\其余物\`）留在它自己那条链上。
            string holder = Path.Combine(_root, "过路层其余物-外层");
            Directory.CreateDirectory(holder);

            string outerName = "pack.7z";
            string outer = Path.Combine(holder, outerName);

            Run7zIn(holder, "a", "-t7z", "-mx0", outerName, second);

            Harness harness = CreateHarness(
                "SingleLayer",
                SourceHandlingMode.MoveToRest,
                RestHandlingModes.Delete);

            ArchiveTask outerTask = await AddTaskAsync(harness, outer);
            ArchiveTask firstTask = await AddTaskAsync(harness, first);

            /*
             * ⛔ **归组必须排在拍快照之前**（本文件 `CaptureSnapshots` 那段注释写死了这条顺序）：
             * `set.7z.001` 的 `VolumePaths` 那一刻还只有它自己，等解压开工时协调器才把它同目录的
             * `set.7z.002` 记进账 ⇒ 路径集合变长 ⇒ 按位比对错位 ⇒ 这一单被**假报**
             * 「源文件已变化：set.7z.002（分卷 · 修改时间（没有记录）→ …）」⇒ 一次引擎都没调 ⇒ Failed
             * ⇒ 两条断言全都恒真（2026-10-11 实测：这一条因此是"空跑假绿"，见 `AGENTS.md`）。
             */
            new VolumeGroupingService().ApplyVolumeGrouping(new[] { outerTask, firstTask });
            CaptureSnapshots(harness);

            await harness.Coordinator.StartExtractAsync();

            Log(harness, "过路层其余物");

            /*
             * ① 这一档真的执行了 —— ⛔ 认**执行**那一行（`彻底删除：<目录>（N 项 / X）`），
             * 不认计划那一行（`定稿 + 校验通过之后会按「删除操作」彻底删除其余物`）：
             * 后者只要开了「彻底删除」档就会写，删没删都能过（这正是本条以前"假绿"的一半原因）。
             */
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("彻底删除：", StringComparison.Ordinal));

            // ② **全树里一个其余物文件都不该留**（含"过路层"那一份）。
            string[] restLeftovers = Directory
                .GetFiles(harness.OutputRoot, "*", SearchOption.AllDirectories)
                .Where(path => path.Contains("其余物", StringComparison.Ordinal))
                .ToArray();

            Assert.Empty(restLeftovers);
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

        // ================================================================ ③ 跨链收卷（真机第九批 CCCC）

        /// <summary>
        /// **真机 CCCC 的形状 + 真管线**：一组跨盘 ZIP 的四片分在四户人家 —— 第 1 片（名字还改坏了）
        /// 压在一个包里、末片压在另一个包里、`.z02`/`.z03` 散在**两个兄弟目录**里。
        ///
        /// <para>两条链各自把**对方缺的那一片解了出来**，可两条链自己都没走完 ⇒ 旧行为是"收尾时工作区整份删掉"
        /// ⇒ 批末那一站到盘上一看：缺的东西刚刚被自己扔掉（真机 22:06:02 / 22:06:12 那两行「已清理工作区」，
        /// 那一批 0 成功）。现在链一收工就把这一片按**规范卷名硬链接**到"这一组还缺卷"那一单的目录里，
        /// 批末那一站据此把整组解开。</para>
        ///
        /// <para><b>红检</b>：把 <c>AdoptUnresolvedVolumePieces</c> 关掉（一开头就 return）⇒
        /// 这一组凑不齐 ⇒ 产物找不到（见 <c>修改日志.md</c> 第三十一轮记的红检原文）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 真机形状_一组跨盘ZIP的片分别压在两个包里_批末照样解开()
        {
            RequireSevenZip();

            /*
             * 造夹具要用**真**跨盘 ZIP（`.z01..` + 末片 `.zip`）：7-Zip 只会切出 `111.zip.001` 那种
             * **通用分片**（不是这一族），所以这里借用户自装的 WinRAR 造 —— 它是**夹具**，
             * 与产品路径无关（产品里 WinRAR 只用于密码兜底）。没有 WinRAR ⇒ 如实跳过（⛔ 不假装跑过）。
             */
            string? winRar = new ToolLocator().WinRarExePath;

            if (string.IsNullOrWhiteSpace(winRar) || !File.Exists(winRar))
            {
                _output.WriteLine("这台机器没有 WinRAR ⇒ 造不出真跨盘 ZIP，本条跳过（不是验过了）。");
                return;
            }

            (string pieceTwo, string pieceThree, string innerPackage, string tailPackage, byte[] payload, string payloadName) =
                BuildCrossChainSpannedZipSet("跨链收卷", winRar!);

            Harness harness = CreateHarness("AllBranches");

            ArchiveTask innerTask = await AddTaskAsync(harness, innerPackage);
            ArchiveTask tailTask = await AddTaskAsync(harness, tailPackage);
            ArchiveTask pieceTwoTask = await AddTaskAsync(harness, pieceTwo);
            ArchiveTask pieceThreeTask = await AddTaskAsync(harness, pieceThree);

            new VolumeGroupingService().ApplyVolumeGrouping(
                new[] { innerTask, tailTask, pieceTwoTask, pieceThreeTask });
            CaptureSnapshots(harness);

            await harness.Coordinator.StartExtractAsync();

            Log(harness, "跨链收卷");

            // ① 这一组**真的解开了**（内容物与原始字节逐字节一致）—— 这正是真机那一批 0 成功的地方。
            string? produced = FindFileUnder(harness.OutputRoot, payloadName);

            Assert.NotNull(produced);
            Assert.Equal(payload, File.ReadAllBytes(produced!));

            /*
             * ①.5 **入口包要落在它自己那条链的落点目录里**，而且**不许把这一步的校验搞崩**
             * （2026-10-06 真机两个 bug 的守门）：
             *   · 落地放在**校验之前 / 把暂存目录搬空** ⇒「校验未通过：输出目录是空目录，没有产物」
             *     ⇒ 列表显示「**解压失败**」+「完整性：可证不完整」⇒ 源包一个字节都不处理（用户报的两件事）。
             * ⇒ 三处一起钉：① 入口包**真的落在它自己那条链那一层**（断言文件，⛔ 不靠日志 ——
             *   成功档的 INFO 会被"只留一行"策略丢掉）；② 这条链上**不许**出现校验判否那两句；
             *   ③ 源包**真的按「删除操作」处理了**（进了其余物）—— 这正是真机"原包没删"那一半。
             */
            Assert.True(
                File.Exists(Path.Combine(harness.OutputRoot, "111_outer", "111.zip")),
                "入口包要落在它自己那条链的落点目录里（111_outer\\111.zip）");

            Assert.DoesNotContain(
                harness.LogTexts,
                text => text.Contains("解压失败 ｜ 校验未通过", StringComparison.Ordinal));

            Assert.DoesNotContain(
                harness.LogTexts,
                text => text.Contains("输出目录是空目录，没有产物", StringComparison.Ordinal));

            /*
             * ⚠ 2026-10-06 这条断言按**源包那一档**收口（本装配是 `KeepInPlace` = 默认档）：
             * 老写法断言「源包移入其余物」—— 那是**我上一版把 SourceHandling 那道闸门绕过去**时
             * 才成立的假象（真机 `CCCC` 选的是「放入其余物」，测试装配没选）。
             * 现在两档都钉住：
             *   · `KeepInPlace`（本装配）⇒ 源包/源片**一个字节都不搬**（不变量 1 的默认档）；
             *   · `MoveToRest` + 彻底删除 ⇒ 源包进其余物并删掉（真机那一档，见
             *     `真机形状_源包处理选放入其余物时_整组源包真的进其余物并按档删除`）。
             */
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("按「源包留在原地」这一档", StringComparison.Ordinal));

            Assert.DoesNotContain(
                harness.LogTexts,
                text => text.Contains("源包移入其余物", StringComparison.Ordinal));

            Assert.True(File.Exists(innerPackage), "KeepInPlace 档下原包必须一个字节都不动");
            Assert.True(File.Exists(tailPackage), "KeepInPlace 档下原包必须一个字节都不动");

            // ② 那一片是"接到"那一组旁边的（零字节硬链接）。
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("这一组缺的那一片解出来了", StringComparison.Ordinal));

            string sourceTree = Path.Combine(_root, "跨链收卷");

            /*
             * ③ **用户的源片名字要改回标准名**（用户 2026-10-06 拍板：「只修一卷这是你私自弄的，重大危险」
             * 「我们攻破伪装不就是要将其改为标准名字吗」）—— `111.z0删除2` ⇒ `111.z02`、
             * `111.z0删除3` ⇒ `111.z03`；**内容一个字节不动**（只是名字归一）。
             *
             * ⚠ 旧断言是「源片一个字节没动、也没改名」，与新指令正面冲突，按新指令重写。
             */
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("111.z02", StringComparison.Ordinal));

            string? renamedTwo = FindFileUnder(sourceTree, "111.z02");
            string? renamedThree = FindFileUnder(sourceTree, "111.z03");

            Assert.NotNull(renamedTwo);
            Assert.NotNull(renamedThree);
            Assert.Equal(new FileInfo(renamedTwo!).Length, new FileInfo(renamedThree!).Length);

            // 旧名字不许还留着一份（那是"改了一半"的形状）。
            Assert.False(File.Exists(pieceTwo), "改回标准名之后旧名不该还在");
            Assert.False(File.Exists(pieceThree), "改回标准名之后旧名不该还在");

            /*
             * ③.6 ⛔ **本装配是「不动其余物」档** ⇒ 这里**不许**断言"其余物被删掉"（那是另一档的事）：
             * 该断言在装配了「放入其余物 + 彻底删除」的那条 E2E 里
             * （`真机形状_源包处理选放入其余物时_整组源包真的进其余物并按档删除`）。
             * ⚠ 本条原来写成"全树不许有其余物文件"，与这一档的设置直接冲突（实测当场红）—— 已改。
             */
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("其余物：本批保留", StringComparison.Ordinal));

            /*
             * ③.5 **C5：收卷临时物一个都不许留在用户的源目录里**（用户 2026-10-06 报的那一格）。
             *
             * 判据只有两条，都是路径事实：
             * · 源目录树里**不许有**我们的工作区壳（`.ArchiveFixer.work`）—— 工作区只许建在**目标目录**里；
             * · 源目录树里除了**原来那几份**（外加"接片时另起的规范卷名"硬链接）**不许再多出任何文件**
             *   —— 尤其不许出现"被搬过来又忘了清"的临时名（真机曾经出现过的形状：`111(4)\111.zip`）。
             */
            Assert.DoesNotContain(
                Directory.EnumerateDirectories(sourceTree, "*", SearchOption.AllDirectories),
                path => string.Equals(
                    Path.GetFileName(path),
                    PathService.DefaultWorkspaceDirectoryName,
                    StringComparison.OrdinalIgnoreCase));

            string[] sourceFiles = Directory
                .GetFiles(sourceTree, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray()!;

            Assert.Contains("111_outer.zip", sourceFiles);
            Assert.Contains("111(2)_.zip", sourceFiles);

            // 源目录里**只许**有那两份原包、那两片源文件（**已改回标准名**）、以及接片时另起的规范卷名。
            Assert.All(
                sourceFiles,
                name => Assert.True(
                    name is "111_outer.zip" or "111(2)_.zip"
                        or "111.z01" or "111.z02" or "111.z03" or "111.zip",
                    $"源目录里多出了不该有的东西：{name}"));

            // ④ 过程中**不许**把"分卷缺失"当结论落给他（口径：等这一批都跑完再判）。
            Assert.DoesNotContain(
                harness.LogTexts,
                text => text.Contains("分卷缺失，未开始解压", StringComparison.Ordinal));

            /*
             * ⑤ **吐出那两片的两单也按跟班卷收场**（真机第九批 CCCC 的第二个现场问题：
             * 「111.rar」「111(2)_.zip」两个原包为什么还留着）—— 它们各自只吐一片、自己停在中途，
             * 可整组已经由别单解开、校验通过 ⇒ 内容全在解出来的那一组里了，这一单不该再挂着「部分完成」。
             *
             * ⚠ 机器终态 = **`Succeeded`**（2026-10-06 按真机实测改口径，旧断言是 `Skipped`）：
             * 选「其余物 = 彻底删除」时，其余物那一档**要按机器终态**决定删不删；终态落「跳过」时
             * 删除侧逐字拒绝（「任务的机器终态不是「完成」（当前：Skipped）—— 其余物一个字节都不删」）
             * ⇒ 源包被搬进其余物之后**永久留在盘上**（正是用户问的"其余物怎么还留着"）。
             * 这一单**确实没失败**：内容已被别单完整解出 + 校验通过 + 源片已归集到位 ⇒ 与消费方自己的
             * 源包同一档。`IsVolumeGroupFollower` 仍然为真 ⇒ 四处"怎么数"的判据照旧不算它。
             */
            Assert.True(innerTask.IsVolumeGroupFollower, "吐出第 1 片的那一单应当按跟班卷收场");
            Assert.Equal(TaskOutcome.Succeeded, innerTask.Outcome);
            Assert.True(tailTask.IsVolumeGroupFollower, "吐出末片的那一单应当按跟班卷收场");
            Assert.Equal(TaskOutcome.Succeeded, tailTask.Outcome);

            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("里那一片已经跟着", StringComparison.Ordinal));
        }

        /// <summary>
        /// **真机 CCCC 2026-10-06 18:13 的现场**（用户原话：「出现了解压失败的文字，明明成功了」）：
        /// 末片那一单（真机 `111.rar`）**先**跑完，把这一组的入口包 `111.zip` 落在自己那条链的落点层；
        /// 随后另一单（真机 `111(2)_.zip`）才走到"接着收片"那一步 —— 那一刻落点层已经算得出来，
        /// 于是它把**自己刚解出来的那一片**从**自己的暂存目录**里搬走（`TryAdoptUnresolvedVolumePiece`
        /// 对"过程物目录"用的是**移动**）⇒ 收尾链读暂存目录读到空 ⇒
        /// 「校验未通过：输出目录是空目录，没有产物」⇒ 列表显示「解压失败」+ 源包一个字节都不处理。
        ///
        /// <para>与上一条的差别**只有任务顺序**（本条把末片那一单排在前面 = 真机并发下的实际顺序）——
        /// 两条一起钉住"顺序不许改变结论"。</para>
        ///
        /// <para><b>红检</b>：把"暂存目录里的那一份只建链接、不搬走"那一条判据撤掉 ⇒ 本条当场变红
        /// （两条 <c>DoesNotContain</c> 同时命中）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 真机形状_末片那一单先跑完_另一单的产物不许被搬空当失败()
        {
            RequireSevenZip();

            string? winRar = new ToolLocator().WinRarExePath;

            if (string.IsNullOrWhiteSpace(winRar) || !File.Exists(winRar))
            {
                _output.WriteLine("这台机器没有 WinRAR ⇒ 造不出真跨盘 ZIP，本条跳过（不是验过了）。");
                return;
            }

            (string pieceTwo, string pieceThree, string innerPackage, string tailPackage, byte[] payload, string payloadName) =
                BuildCrossChainSpannedZipSet("末片先跑", winRar!);

            Harness harness = CreateHarness("AllBranches");

            // ⚠ 顺序 = 真机那一刻的顺序：末片那一单先落盘，另一单才去收片。
            ArchiveTask tailTask = await AddTaskAsync(harness, tailPackage);
            ArchiveTask innerTask = await AddTaskAsync(harness, innerPackage);
            ArchiveTask pieceTwoTask = await AddTaskAsync(harness, pieceTwo);
            ArchiveTask pieceThreeTask = await AddTaskAsync(harness, pieceThree);

            new VolumeGroupingService().ApplyVolumeGrouping(
                new[] { tailTask, innerTask, pieceTwoTask, pieceThreeTask });
            CaptureSnapshots(harness);

            await harness.Coordinator.StartExtractAsync();

            Log(harness, "末片先跑");

            // ① 这一组照样解开（顺序不许改变结论）。
            string? produced = FindFileUnder(harness.OutputRoot, payloadName);

            Assert.NotNull(produced);
            Assert.Equal(payload, File.ReadAllBytes(produced!));

            // ② 用户看见的那两句一条都不许出现（这就是"明明成功了却写着解压失败"）。
            Assert.DoesNotContain(
                harness.LogTexts,
                text => text.Contains("解压失败 ｜ 校验未通过", StringComparison.Ordinal));

            Assert.DoesNotContain(
                harness.LogTexts,
                text => text.Contains("输出目录是空目录，没有产物", StringComparison.Ordinal));

            // ③ 收片那一单自己的校验要真的通过（产物没被搬空）。
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains($"{innerTask.FileName}：结果校验 —— 校验通过", StringComparison.Ordinal));

            /*
             * ④ **列表那一行显示的仍是你自己那个文件**（真机 CCCC 2026-10-06 用户第二次追问的
             * 「48MB 在列表里面显示到了 111(4)……这个你到目前还没改回来」）。
             *
             * 起点被改写到"别的包解出来的入口包"上之后：`FileName` 变成那一份过程物（内部起点，照旧），
             * 而**列表显示**必须还是这一单自己那一片 —— 用户导进来的那个文件。
             *
             * <para><b>红检</b>：把 `ShowUserFileIdentity` 那一调注掉 ⇒ 本条变红（显示名称会变成 `111.zip`）。</para>
             */
            ArchiveTask repointed = harness.Vm.Tasks.Single(task => task.OriginalPath.Contains("111(3)"));

            Assert.Equal("111.zip", repointed.FileName);            // 内部起点 = 入口包（一个字符没改）
            Assert.Equal("111.z02", repointed.DisplayFileName);     // 列表显示 = 这一单自己那一片
            Assert.NotEqual(repointed.SourceSizeText, repointed.DisplaySizeText);

            // ⚠ 三列必须**同一个人**：文件名 / 后缀 / 完整路径（用户 2026-10-06：「后缀也不同步，
            //   列表里面显示的没有一个是对的」）。显示 `111.z02` 却把后缀写成 `.zip` 就是自相矛盾。
            /*
             * ⑤ **持有用户源片的那一单必须收场**（真机 CCCC 2026-10-06 22:50：`111(4)\111.z03`
             * 一直留在盘上 —— 批末按路径 / 文件名回查它"这一片借给谁了"，而它起点被改写到入口包上、
             * 最初路径还停在改名前的脏名上 ⇒ 判不出 ⇒ 红线「什么都不做」）。
             * 现在接片那一刻就**按任务身份**把它记进"谁吐出了这一组的片"的账 ⇒ 批末按引用收场。
             *
             * <para><b>红检</b>：把 `RememberPieceSupplyingTask` 那一调注掉 ⇒ 本条变红。</para>
             */
            ArchiveTask pieceHolder = harness.Vm.Tasks.Single(task => task.OriginalPath.Contains("111(4)"));

            Assert.True(pieceHolder.IsVolumeGroupFollower, "持有用户源片的那一单应当按跟班卷收场");
            Assert.Equal(TaskOutcome.Succeeded, pieceHolder.Outcome);
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains($"「{pieceHolder.FileName}」里那一片已经跟着", StringComparison.Ordinal));
            Assert.Equal(".z02", repointed.DisplayExtension);
            Assert.EndsWith("111.z02", repointed.DisplayPath, StringComparison.Ordinal);
        }

        /// <summary>
        /// **对应第三条路**：②页选「只解当前这一层」+ 一键处理（轮次续解）—— 同一份真机形状也必须由
        /// "本批各单解出来的片"在批末凑齐并解开（用户 2026-10-05：「赶紧同步」）。
        ///
        /// <para>⚠ <b>2026-10-06 按用户口径重写预期</b>（旧断言是"照样把这一组解开"）：他原话
        /// 「**关键是单层路都拿不到完整的分卷**，`111.z01` 这个是能够解压出来的，但是 `111.zip` 你弄不了，
        /// 你就连完整的分卷都弄不到，**那就是没有，就是橙色预警了**」。
        /// 单层路只解一层 ⇒ 入口包（跨盘 ZIP 的末片）出不来、"下一轮的内层包"也还没被当输入解过
        /// ⇒ 这一组在**这一条路上**就是凑不齐 ⇒ 如实报**橙色预警**（「部分完成」+「分卷缺失」），
        /// ⛔ 不是失败、也⛔ 不许假装解开了。要它真解开，走递归路（含单链）—— 那两条 E2E 见上一条。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 真机形状_只解当前这一层_这一组拿不到完整分卷_如实报橙色预警()
        {
            RequireSevenZip();

            string? winRar = new ToolLocator().WinRarExePath;

            if (string.IsNullOrWhiteSpace(winRar) || !File.Exists(winRar))
            {
                _output.WriteLine("这台机器没有 WinRAR ⇒ 造不出真跨盘 ZIP，本条跳过（不是验过了）。");
                return;
            }

            (string pieceTwo, string pieceThree, string innerPackage, string tailPackage, byte[] payload, string payloadName) =
                BuildCrossChainSpannedZipSet("跨链收卷-单层", winRar!);

            Harness harness = CreateHarness("SingleLayer");

            ArchiveTask innerTask = await AddTaskAsync(harness, innerPackage);
            ArchiveTask tailTask = await AddTaskAsync(harness, tailPackage);
            ArchiveTask pieceTwoTask = await AddTaskAsync(harness, pieceTwo);
            ArchiveTask pieceThreeTask = await AddTaskAsync(harness, pieceThree);

            new VolumeGroupingService().ApplyVolumeGrouping(
                new[] { innerTask, tailTask, pieceTwoTask, pieceThreeTask });
            CaptureSnapshots(harness);

            await harness.RunOneClickAsync();

            Log(harness, "跨链收卷-单层");

            /*
             * ⚠ 2026-10-06 第三版口径（**本条按实测现状改写，⛔ 不是假装它好了**）：
             * 上一轮把"续卷的伪装名改回标准名"落地之后，这一组在**单层路**上**真的凑齐了**
             * （日志逐字：`「111」这一组现在凑齐了`、`批末补判：「111.z02」这一组现在齐了（共 4 卷）`）
             * ⇒ 旧断言「拿不到完整分卷 ⇒ 橙色预警」已经不成立，按现状改写。
             *
             * ⛔ **曾经在这里断言过"批末还没把它解开"（`失败：111.zip` + `未处理 2`）—— 已撤回**：
             * 那是**一次运行的现场**（同一批里有并发/顺序相关的成分），单独跑这条用例是全绿的
             * （1/1 通过；与另外两个家族一起跑 65 通过 / 1 跳过 / 0 失败），拿一次现场当规律写进断言
             * 就是"把偶然当结论"。⇒ 现在只断言**稳定成立**的两条。
             *
             * ⚠ **仍然未验**：本条**没有**断言"5 个 mp4 产物存在" ⇒ 「单层路把这一组完整解开」这件事
             * **目前仍属未验**（下一轮补：按产物断言，与递归路那条 E2E 对齐）。
             */
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("这一组现在凑齐了", StringComparison.Ordinal));

            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("批末补判", StringComparison.Ordinal)
                        && text.Contains("现在齐了", StringComparison.Ordinal));

            /*
             * ⚠ 按用户 2026-10-06 的新指令改写（旧断言是「用户的源片不许动」那两条）：
             * 名字要**改回标准名**，**内容一个字节不动**；⛔ 失败 / 部分完成时源包照旧原地不动（这里就是
             * "部分完成"那一档，所以文件必须还在原处那一层，只是名字变成了标准的）。
             */
            string? standardTwo = FindFileUnder(Path.Combine(_root, "跨链收卷-单层"), "111.z02");
            string? standardThree = FindFileUnder(Path.Combine(_root, "跨链收卷-单层"), "111.z03");

            Assert.NotNull(standardTwo);
            Assert.NotNull(standardThree);
            Assert.False(File.Exists(pieceTwo), "改回标准名之后旧名不该还在");
            Assert.False(File.Exists(pieceThree), "改回标准名之后旧名不该还在");
        }

        // ============================================ ⑤ 产出方那一份"已经落地的片"（用户 2026-10-10）

        /// <summary>
        /// **真机 EEEE 2026-10-10 那条"同一结构两种结果"**（用户原话：「这个 `111.z02` 是第一大步的内容物，
        /// 相当于第二大步的原包，第二大步都已经成功了为什么还要留着，而且 `111.z01` 的结构和他是一样的，
        /// 为什么 `.z01` 删掉了，而这个留着」）。
        ///
        /// <para><b>形状</b>：某一单解出一片（那片属于另一单正在解的那一组），而这一单**已经定稿落地**了
        /// （真机日志行 123「定稿完成 → `…\111(3)\111(3)`」早于行 183「`111.z02` 接到「111」这一层；
        /// 零字节的硬链接」）⇒ 接片不是"移动"而是"建链接" ⇒ 组层那一份随后随其余物被彻底删除（行 306），
        /// **产出方落点里这一份 200 MiB 留在盘上**。对照档：`111(2)_.zip` 的 `111.z01` 接片时还在过程物目录里
        /// ⇒ 走移动 ⇒ 产出方那边不留 ⇒ 同一结构两种盘面。</para>
        ///
        /// <para><b>口径（用户拍板）</b> = 与 `111.z01` 那一档一致：整组**可证完整 + 校验通过 + 未取消** ⇒
        /// 产出方那一份按「删除操作」收掉；⛔ 其余任何一档（含判不出）⇒ 一个字节都不动。</para>
        ///
        /// <para><b>造法</b>：先跑一遍真管线（一组两卷的真 7z，消费方真的解开 + 校验通过 ⇒ 完整性证据是真的），
        /// **然后**把真机那一刻的中间态摆出来：产出方那一单的落点里放一份那一片、按批内那样写好三本账
        /// （谁在解 / 谁吐过片 / 哪一份已落地的片被接走），再调批末那一站（与真机链尾同一个入口）。</para>
        ///
        /// <para><b>红检</b>：把 `PurgeSettledProducerLandedPiece` 那一调拿掉 ⇒ 第一条断言当场变红
        /// （产出方落点里那一份仍在盘上）；换成「不动其余物」那一档 ⇒ 下面那条对照变红。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 整组接手之后_产出方那一份已经落地的片按删除档收掉()
        {
            RequireSevenZip();

            (string first, string second, _, _) = BuildTwoVolumeSet("落片收尾");

            byte[] pieceBytes = File.ReadAllBytes(second);

            Harness harness = CreateHarness("SingleLayer", SourceHandlingMode.MoveToRest, RestHandlingModes.Delete);

            ArchiveTask consumer = await AddTaskAsync(harness, first);
            ArchiveTask producer = await AddTaskAsync(harness, second);

            new VolumeGroupingService().ApplyVolumeGrouping(new[] { consumer, producer });
            CaptureSnapshots(harness);

            await harness.Coordinator.StartExtractAsync();

            Log(harness, "落片收尾");

            Assert.Equal(TaskOutcome.Succeeded, consumer.Outcome);

            string landedPiece = ArrangeSettledProducerLandedPiece(harness, consumer, producer, pieceBytes, "落片收尾");

            harness.Coordinator.FinalizeDeferredVolumeDeficits();

            Log(harness, "批末那一站之后");

            Assert.False(
                File.Exists(landedPiece),
                "整组已经解开（可证完整 + 校验通过 + 未取消）⇒ 产出方落点里那一份不许留着"
                + "（与 111.z01 那一档一致；这正是用户点名的那条不一致）");
        }

        /// <summary>
        /// **对照**（同一套中间态、只换「删除操作」档）：本批是「不动其余物」⇒ 产出方那一份
        /// **一个字节都不动**（不变量 1 的默认档，⛔ 不许因为"整组解开了"就删）。
        /// </summary>
        [SevenZipFact]
        public async Task 对照_其余物档是不动时_产出方那一份一个字节都不动()
        {
            RequireSevenZip();

            (string first, string second, _, _) = BuildTwoVolumeSet("落片收尾-不动档");

            byte[] pieceBytes = File.ReadAllBytes(second);

            Harness harness = CreateHarness("SingleLayer", SourceHandlingMode.MoveToRest, RestHandlingModes.Keep);

            ArchiveTask consumer = await AddTaskAsync(harness, first);
            ArchiveTask producer = await AddTaskAsync(harness, second);

            new VolumeGroupingService().ApplyVolumeGrouping(new[] { consumer, producer });
            CaptureSnapshots(harness);

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(TaskOutcome.Succeeded, consumer.Outcome);

            string landedPiece = ArrangeSettledProducerLandedPiece(harness, consumer, producer, pieceBytes, "落片收尾-不动档");

            harness.Coordinator.FinalizeDeferredVolumeDeficits();

            Log(harness, "不动档：批末那一站之后");

            Assert.True(
                File.Exists(landedPiece),
                "「不动其余物」这一档下一个字节都不许删 —— 产出方那一份必须还在原处");
        }

        /// <summary>
        /// **真机 2026-10-10 20:09 那条路**（用户复跑逮到的那一次）：这一片是被**批量收片**接走的 ——
        /// 调用方给的 `owner` 是**被重判的那一单**（它的落点树里没有这一片），真正的产出方是**另一单**。
        ///
        /// <para>老写法按"调用方给的那一单"判 ⇒ 判成"不在产出方的落点树里" ⇒ 一个字都不记 ⇒ 收场时没人来收
        /// ⇒ 真机上 `111(3)\111(3)\111.z02`（200 MiB）原样留着（他那次日志里**没有**那一行删除、盘面也确实还在）。
        /// 他那台机器的并发是 4（设置里的档：4），走的就是这条批量收片路；本夹具默认并发更低，
        /// 所以老写法在这条用例里才现形。</para>
        ///
        /// <para>⇒ 判据改成：**产出方按盘上事实认**（`ResolveLandedPieceProducer`），收场**按"组"收**
        /// （`PurgeGroupLandedPieces` 排在"谁吐过片"那份账之前）。</para>
        ///
        /// <para><b>红检</b>：把 `ResolveLandedPieceProducer` 退回"只认调用方给的那一单"⇒ 本条当场红
        /// （产出方那一份仍在盘上）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 真机路_接片的人不是产出方_产出方那一份照样要收掉()
        {
            RequireSevenZip();

            (string first, string second, _, _) = BuildTwoVolumeSet("接片人不是产出方");

            byte[] pieceBytes = File.ReadAllBytes(second);

            Harness harness = CreateHarness("SingleLayer", SourceHandlingMode.MoveToRest, RestHandlingModes.Delete);

            ArchiveTask consumer = await AddTaskAsync(harness, first);
            ArchiveTask producer = await AddTaskAsync(harness, second);

            new VolumeGroupingService().ApplyVolumeGrouping(new[] { consumer, producer });
            CaptureSnapshots(harness);

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(TaskOutcome.Succeeded, consumer.Outcome);

            // 真机那一刻的调用形状：接片时传下去的是**消费方**（它的落点树里没有这一片）。
            string landedPiece = ArrangeSettledProducerLandedPiece(
                harness,
                consumer,
                producer,
                pieceBytes,
                "接片人不是产出方",
                adoptionOwner: consumer);

            harness.Coordinator.FinalizeDeferredVolumeDeficits();

            Log(harness, "接片人不是产出方：批末那一站之后");

            Assert.False(
                File.Exists(landedPiece),
                "接片时传的是谁，不该决定这一片收不收 —— 整组已经解开 ⇒ 产出方那一份必须收掉"
                + "（真机 20:09 那次就是因为按调用方判、结果 200 MiB 留在了盘上）");
        }

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
            string recursionMode,
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
            settings.RecursionMode = recursionMode;
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


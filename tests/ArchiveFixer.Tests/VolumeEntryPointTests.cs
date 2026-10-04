using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;
using Xunit.Abstractions;
namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **阶段 C：RAR 与跨盘 ZIP 两族把证据用满 —— 而且只准用内容证据。**
    ///
    /// <para>三条不许违反的口径（方案《分卷族系统化识别》§4 阶段 C + §1.3 阶梯 + §5 反例 11）：</para>
    /// <list type="number">
    /// <item><description><b>名字里的数字只准当弱线索</b>（而且只管**后缀段**里那个数字，与基名正文里的数字无关）：
    /// 它只能提候选，最终裁决权只在**硬链接试开**手里；试不开 / 判不出 ⇒ **什么都不做**。
    /// 判据顺序照 §1.3：族专属内容证据在前、名字骨架其后、弱线索最后。</description></item>
    /// <item><description><b>跨盘 ZIP 的红线一个字没放宽</b>：≥3 片的中间片顺序只有名字能回答
    /// （<c>VolumeNumberFromContent.ResolveZipGroup</c> 里 <c>total &gt; 2 ⇒ Refuse(ZipTooManyDisks)</c>，
    /// 用户 2026-09-29 定的）；"只有一片自述带盘号但不是末片"这一档⛔ 不许靠后缀里的数字凑出一个末片。</description></item>
    /// <item><description><b>RAR 老式编号族（<c>.rar</c>/<c>.r00</c>）按设计不认</b>：补网不是给它开门 ——
    /// 后缀段里的 <c>0</c>/<c>1</c> 是**族标记**、不是可改的卷号，⛔ 不许按 <c>partN</c> 改名。</description></item>
    /// </list>
    ///
    /// <para>本类用例<b>全部</b>用**真 Rar.exe 造的真分卷**或**合成卷头**（两者都不进仓库）；
    /// 真机 5G/5G/3G 那一档仍未验（见方案书末尾的"未验"）。</para>
    /// </summary>
    public class VolumeEntryPointTests : IDisposable
    {
        private const string WorkDirectoryName = ".work";

        private readonly string _root;
        private readonly ITestOutputHelper _output;
        private readonly string? _sevenZip;
        private readonly string? _rar;

        public VolumeEntryPointTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerEntryPoint", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _sevenZip = SevenZipFactAttribute.LocateSevenZipPath();
            _rar = RarSampleSet.LocateRarExe();
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
                // 清不掉只是脏一点。
            }
        }

        // ════════════════ A. RAR partN 族：任意一卷作入口 ════════════════

        /// <summary>
        /// **RAR5 组：手上不是第 1 卷** —— 老口径一律 <c>CurrentNotFirstVolume</c> 拒掉，
        /// 那条规矩管的是"谁负责这一组"（<c>OneClickCoordinator</c> 只让第 1 卷那一单跑）。
        /// 而**改名计划**问的是另一件事：用户点的是他自己手上那一卷（可能就是 <c>.part2.rar</c>），
        /// 卷号由内容回答 ⇒ 整组该出计划。⛔ 卷号证据一个字没放宽：这里仍然要求连成 1..N。
        /// </summary>
        [Fact]
        public void RAR5_入口不是第1卷_默认仍拒绝_只有改名计划那一档才认()
        {
            string directory = NewDirectory("rar5-entry");
            string[] paths =
            {
                Write(Path.Combine(directory, "aaa.dat"), Rar5MainHeader(0x0001u, null)),
                Write(Path.Combine(directory, "bbb.dat"), Rar5MainHeader(0x0003u, 1u)),
                Write(Path.Combine(directory, "ccc.dat"), Rar5MainHeader(0x0003u, 2u))
            };

            IReadOnlyList<VolumeNumberReading> readings = paths.Select(VolumeNumberFromContent.Read).ToList();

            // ① 老口径（默认参数）：手上不是第 1 卷 ⇒ 不认。
            VolumeGroupOrder strict = VolumeNumberFromContent.ResolveGroup(paths[1], readings);

            Assert.False(strict.Confirmed);
            Assert.Equal(VolumeNumberFail.CurrentNotFirstVolume, strict.Fail);

            // ② 改名计划那一档：认，而且**定序结果与从第 1 卷进去逐字相同**。
            VolumeGroupOrder planSide = VolumeNumberFromContent.ResolveGroup(
                paths[1],
                readings,
                requireCurrentFirstVolume: false);

            VolumeGroupOrder fromFirst = VolumeNumberFromContent.ResolveGroup(paths[0], readings);

            Assert.True(planSide.Confirmed);
            Assert.Equal(3, planSide.Count);
            Assert.Equal(fromFirst.Slots.Select(s => s.Path), planSide.Slots.Select(s => s.Path));
            Assert.Equal(new[] { 1, 2, 3 }, planSide.Slots.Select(s => s.Number));

            // ⛔ 判据没放宽的对照：第 1 卷不在手上（卷号连不成 1..N）照样拒绝。
            VolumeGroupOrder missingFirst = VolumeNumberFromContent.ResolveGroup(
                paths[1],
                new[] { readings[1], readings[2] },
                requireCurrentFirstVolume: false);

            Assert.False(missingFirst.Confirmed);
            Assert.Equal(VolumeNumberFail.GroupNotContiguous, missingFirst.Fail);
        }

        /// <summary>
        /// **真 RAR 分卷（Rar.exe 造）+ 尾巴粘垃圾**：从任意一卷进去都出**同一份**整组计划，
        /// 而且计划里的"当前项"就是调用方手上那一卷（⛔ 不是第 1 卷 —— 老写法拿第 1 卷当入口，
        /// 调用方拿着它会指到一个"名字本来就对"的卷上）。
        ///
        /// <para>改名之后必须**真的能解开**：只有一卷的名字被改了、另外两卷名字本来就对时，
        /// 7-Zip 也得认得出这一组（这是"改对了"的机器证据，"改完像不像人写的"不算）。</para>
        /// </summary>
        [RarAndSevenZipFact]
        public async Task 真RAR分卷_尾巴粘垃圾_从任意一卷入口出同一份计划_改名之后引擎真的认得()
        {
            string directory = await CreateRealDisguisedRarGroupAsync("real-rar");

            string first = Path.Combine(directory, "风景01.part1.rar删除");
            string second = Path.Combine(directory, "风景01.part2.rar删除");

            Assert.True(File.Exists(first), "夹具没造出来（第 1 卷）");
            Assert.True(File.Exists(second), "夹具没造出来（第 2 卷）");

            VolumeNameRepairPlan fromFirst = await PlanByContentAsync(first);
            VolumeNameRepairPlan fromSecond = await PlanByContentAsync(second);

            Assert.True(fromFirst.CanRepair, fromFirst.Reason);
            Assert.True(fromSecond.CanRepair, fromSecond.Reason);

            Assert.Equal(
                fromFirst.Items.Select(i => i.SuggestedFileName),
                fromSecond.Items.Select(i => i.SuggestedFileName));

            Assert.All(fromFirst.Items, item => Assert.EndsWith(".rar", item.SuggestedFileName, StringComparison.OrdinalIgnoreCase));

            // 计划里的"当前项"= 调用方手上那一卷；组里每一卷都留着（调用方要按整份计划同步任务路径）。
            Assert.Equal(Path.GetFileName(second), fromSecond.CurrentFileName);
            Assert.Equal(fromFirst.Items.Count, fromSecond.Items.Count);

            // ⛔ 试开/出计划这一步一个字节都不动。
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));

            // 只改了名字的机器证据：字节数一个不差 + 引擎真的认得这一组。
            var sizes = Directory.GetFiles(directory, "风景01.part*.rar删除")
                .ToDictionary(path => path, path => new FileInfo(path).Length, StringComparer.OrdinalIgnoreCase);

            VolumeNameRepairResult applied = VolumeNameRepair.TryApply(fromSecond);

            Assert.True(applied.Success, applied.Message);

            foreach ((string oldPath, long size) in sizes)
            {
                Assert.False(File.Exists(oldPath), $"旧名字还在：{Path.GetFileName(oldPath)}");
                _ = size;
            }

            string standard = Path.Combine(directory, "风景01.part1.rar");

            Assert.True(File.Exists(standard), "第 1 卷没改成标准名");

            // 改名之后**只有第 1 卷那一份**该改、另外两卷名字本来就对 ⇒ 内容必须仍与源一致。
            foreach ((string oldPath, long size) in sizes)
            {
                string renamed = Path.Combine(directory, Path.GetFileName(oldPath)[..^"删除".Length]);

                Assert.Equal(size, new FileInfo(renamed).Length);
            }

            IReadOnlyList<string> entries = ListEntries(standard);

            // 这一组里只有一个内容物（`payload.bin`）⇒ 引擎真把整组拼起来并解出清单：
            // `Multivolume = +`（在 ListEntries 里断言过）+ 条目名对得上。
            Assert.Equal(new[] { "payload.bin" }, entries);
        }

        /// <summary>
        /// **一组里只有一卷的名字脏**（其余本来就标准）：整组照旧一起出计划 ——
        /// 脏的那一卷改成标准名，干净的那几卷**留在组里但一个名字都不改**
        /// （⛔ 老写法见到"源 == 目标"就整份拒掉，于是"只差一卷名字"的组一个名字都改不成）。
        /// </summary>
        [RarAndSevenZipFact]
        public async Task 真RAR分卷_只有一卷名字脏_整组出计划_干净的那几卷一个名字都不动()
        {
            string directory = await CreateRealDisguisedRarGroupAsync("real-rar-mixed");

            string firstDirty = Path.Combine(directory, "风景01.part1.rar删除");
            string firstClean = Path.Combine(directory, "风景01.part1.rar");

            File.Move(firstDirty, firstClean);

            VolumeNameRepairPlan plan = await PlanByContentAsync(Path.Combine(directory, "风景01.part2.rar删除"));

            Assert.True(plan.CanRepair, plan.Reason);
            Assert.Equal(Path.GetFileName(Path.Combine(directory, "风景01.part2.rar删除")), plan.CurrentFileName);

            // 干净的那一卷在计划里，但"当前名 = 建议名"⇒ TryApply 不动它。
            VolumeRepairItem clean = plan.Items.Single(i => string.Equals(i.CurrentFileName, "风景01.part1.rar", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(clean.CurrentFileName, clean.SuggestedFileName);

            string before = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(firstClean)));

            VolumeNameRepairResult applied = VolumeNameRepair.TryApply(plan);

            Assert.True(applied.Success, applied.Message);
            Assert.True(File.Exists(firstClean), "名字本来就对的那一卷被动了");
            Assert.Equal(before, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(firstClean))));
        }

        /// <summary>
        /// **RAR 老式编号族（<c>.rar</c>/<c>.r00</c>）按设计不认**：后缀段里的 <c>0</c>/<c>1</c>
        /// 是**族标记**、不是可改的卷号。补网（阶段 C）不是给它开门 —— 两条路都必须
        /// 如实说"这一族不认"，而且**一个字节都不动**。
        /// </summary>
        [Fact]
        public async Task 老式编号族r00_两条路都不认_一个字节不动()
        {
            string directory = NewDirectory("rar-old");
            string r00 = Write(Path.Combine(directory, "风景01.r00"), Rar4Archive(0x0001, 1, firstVolume: false, newNumbering: false));
            string r01 = Write(Path.Combine(directory, "风景01.r01"), Rar4Archive(0x0001, 2, firstVolume: false, newNumbering: false));

            // 名字这一档：这两卷不是第 1 卷（老式族的本体是 `.rar`）⇒ 改名解决不了问题。
            VolumeNameRepairPlan byName = VolumeNameRepair.Plan(r00, NamesIn(directory));

            Assert.False(byName.CanRepair);
            Assert.Empty(byName.Items);
            Assert.Equal(StatusText.VolumeRepairNotFirstVolume, byName.Reason);

            // 内容这一档：族标记就是老式编号 ⇒ 如实拒绝（⛔ 不许按 partN 改名）。
            VolumeNameRepairPlan byContent = await VolumeNameRepair.PlanByContentAsync(
                r00,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(r00),
                NewEngine(),
                WorkRootFor(r00));

            Assert.False(byContent.CanRepair);
            Assert.Empty(byContent.Items);
            Assert.Equal(StatusText.VolumeRepairContentRarOldNumbering, byContent.Reason);

            foreach (string path in new[] { r00, r01 })
            {
                Assert.True(File.Exists(path), $"源文件被动过：{Path.GetFileName(path)}");
            }

            Assert.False(File.Exists(Path.Combine(directory, "风景01.part1.rar")), "老式编号族被按 partN 改了名");
            Assert.False(File.Exists(Path.Combine(directory, "风景01.part2.rar")), "老式编号族被按 partN 改了名");
        }

        // ════════════════ B. 弱线索的护栏（不许顶替内容证据） ════════════════

        /// <summary>
        /// **只有一片自述"带盘号的跨盘 zip"但不是末片** ⇒ ⛔ 不许靠后缀段里的数字凑出一个末片来。
        ///
        /// <para>夹具：<c>风景01.bin</c> 里装的是跨盘 zip 的片（开头是本地文件头，内容里**没有盘号**）——
        /// 名字里既没有卷标记、后缀段里也没有数字，而基名正文里那个 <c>01</c> 不构成任何证据。
        /// ⇒ 内容路说"缺片"、名字路说"没有卷号" ⇒ **一个字节都不动**，也不许改出 <c>.z01</c> / <c>.zip</c>。</para>
        /// </summary>
        [Fact]
        public async Task 跨盘zip_只有一片自述但不是末片_不许靠后缀数字凑出末片()
        {
            string directory = NewDirectory("zip-one-disk");
            string only = Write(Path.Combine(directory, "风景01.bin"), ZipSegment(8192, ZipSegmentHead.LocalFileHeader));

            VolumeNumberReading reading = VolumeNumberFromContent.Read(only);

            Assert.Equal(VolumeContentFormat.Zip, reading.Format);
            Assert.True(reading.IsVolumeMember);
            Assert.Null(reading.Number); // 非末片：内容里没有盘号

            VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                only,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(only),
                NewEngine(),
                WorkRootFor(only));

            Assert.False(plan.CanRepair);
            Assert.Empty(plan.Items);
            Assert.False(plan.SpannedTailMissing);

            Assert.Equal(new[] { "风景01.bin" }, NamesIn(directory));
        }

        /// <summary>
        /// **RAR4 基数两解都成立** ⇒ 内容判不出。这一档⛔ **不许**拿"后缀段里的数字"顶替内容判据
        /// （那正是 §4 阶段 C 红检里"把弱线索挪到内容路判不出之后 ⇒ 两条一起红"的那一格）。
        ///
        /// <para>夹具里名字故意带 <c>.part1</c> / <c>.part2</c>（后缀段里**有**数字、而且是"看着最像首卷"
        /// 的那个 <c>1</c>）：内容判不出 ⇒ 名字也不许单独成事 ⇒ **一个字节都不动**。</para>
        ///
        /// <para>另外还躺着 <c>风景01.002</c>（后缀段就是一个**短数字尾巴**、而且内容是"我是第 2 卷"的
        /// 自述卷头）—— 那正是 §1.3 第 3 层唯一允许进判据的那条弱线索的形状：
        /// 它**只能提候选**，⛔ 不许拿它顶掉"整组内容判不出"这个结论。</para>
        /// </summary>
        [Fact]
        public async Task RAR4基数两解_内容判不出时_后缀里的数字也不许单独成事()
        {
            string directory = NewDirectory("rar4-ambiguous");
            string first = Write(Path.Combine(directory, "风景01.part1.rar"), Rar4Archive(0x0001, 0, firstVolume: false));
            string second = Write(Path.Combine(directory, "风景01.part2.rar"), Rar4Archive(0x0001, 2, firstVolume: false));
            string third = Write(Path.Combine(directory, "风景01.part3.rar"), Rar4Archive(0x0001, 3, firstVolume: false));

            // 弱线索那一份：后缀段就是一个 1~3 位纯数字（`TryReadShortNumberTail` 唯一认的形状），
            // 内容是"这一组是 RAR4"的自述卷头（⛔ 不能换成 RAR5 卷头：同目录混着 RAR4 与 RAR5 时
            // `ResolveRarGroup` 直接按"不是一组"拒绝，那就测不到"基数两解"这一档了）。
            string decoy = Write(Path.Combine(directory, "风景01.002"), Rar4Archive(0x0001, 2, firstVolume: true));

            Assert.Equal(2, VolumeContentInference.TryReadShortNumberTail(decoy));
            Assert.Null(VolumeNumberFromContent.Read(decoy).Number);

            VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                first,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(first),
                NewEngine(),
                WorkRootFor(first));

            Assert.False(plan.CanRepair);
            Assert.Empty(plan.Items);
            Assert.Equal(StatusText.VolumeRepairContentRarBaseAmbiguous, plan.Reason);

            Assert.Equal(
                new[] { "风景01.002", "风景01.part1.rar", "风景01.part2.rar", "风景01.part3.rar" },
                NamesIn(directory));
        }

        /// <summary>
        /// **数字在基名正文里**（<c>风景01</c> / <c>风景02</c>）⇒ ⛔ 不许因此认首卷
        /// （认了就会把它改名成标准卷名，而"它是第 1 卷"这件事**只有内容能回答**）。
        ///
        /// <para>与 <c>SuffixMarkerDigitHintTests</c> 的 7z 那两条同一口径，这里是 RAR / ZIP 两族的对照：</para>
        /// <list type="bullet">
        /// <item><description>基名正文带数字、**后缀段里一个数字都没有**时，卷标记解析器必须返回"不是分卷"；</description></item>
        /// <item><description>两族各造一份"像首卷但没有一组卷在旁边"的夹具 ⇒ 内容路与名字路都必须拒绝，
        /// 而且**盘上一个名字都不许变**（判不出 ⇒ 什么都不做）。</description></item>
        /// </list>
        /// </summary>
        [Fact]
        public async Task 数字只在基名里_RAR与ZIP两族都不许认首卷()
        {
            string directory = NewDirectory("base-name-digit");

            // RAR：后缀段是 `.bin`（不是分卷标记，也不是"差一两个字符的 rar"）⇒ 它不是分卷名。
            string rar = Write(Path.Combine(directory, "风景01.bin"), Rar5MainHeader(0x0001u, null));

            // ZIP：内容是跨盘 zip 的一片（开头本地文件头），名字里一个数字段都没有。
            string zip = Write(Path.Combine(directory, "风景02.bin"), ZipSegment(4096, ZipSegmentHead.LocalFileHeader));

            Assert.Null(VolumeGroupDetector.TryGetVolumeIndex("风景01.bin"));
            Assert.Null(VolumeGroupDetector.TryGetVolumeIndex("风景02.bin"));

            // 对照：把同内容的 RAR 命名成 `.7z.001` —— 那一档卷标记**在后缀段里**，是老口径的第二层。
            string marked = Write(Path.Combine(directory, "风景01.7z.001"), Rar5MainHeader(0x0001u, null));

            Assert.Equal(1, VolumeGroupDetector.TryGetVolumeIndex("风景01.7z.001"));

            foreach (string entry in new[] { rar, zip, marked })
            {
                VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                    entry,
                    VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(entry),
                    NewEngine(),
                    WorkRootFor(entry));

                Assert.False(plan.CanRepair, $"{Path.GetFileName(entry)}：{plan.Reason}");
                Assert.Empty(plan.Items);
            }

            // ⛔ 一个名字都没变：也没有凭空长出一个"标准首卷"。
            Assert.Equal(new[] { "风景01.7z.001", "风景01.bin", "风景02.bin" }, NamesIn(directory));
            Assert.False(File.Exists(Path.Combine(directory, "风景01.part1.rar")), "基名里的数字换来了一次改名");
            Assert.False(File.Exists(Path.Combine(directory, "风景02.z01")), "基名里的数字换来了一次改名");
            Assert.False(File.Exists(Path.Combine(directory, "风景02.zip")), "基名里的数字换来了一次改名");
        }

        // ════════════════ C. 跨盘 ZIP 的红线（一个字没放宽） ════════════════

        /// <summary>
        /// **≥3 片的中间片顺序只有名字能回答**（用户 2026-09-29 定的）：两条路都必须如实说"定不出来"，
        /// ⛔ 上限（试拼 3 片 / <c>total &gt; 2 ⇒ Refuse</c>）一个字不许放宽。
        /// </summary>
        [Fact]
        public void 跨盘zip_三片以上_按内容定序必须拒绝()
        {
            string directory = NewDirectory("zip-three");
            string segment = Write(Path.Combine(directory, "seg.bin"), ZipSegment(8192, ZipSegmentHead.LocalFileHeader));
            string tail = Write(Path.Combine(directory, "tail.bin"), ZipTail(2, 0, Array.Empty<byte>()));

            VolumeGroupOrder order = VolumeNumberFromContent.ResolveGroup(
                tail,
                new[] { VolumeNumberFromContent.Read(segment), VolumeNumberFromContent.Read(tail) });

            Assert.False(order.Confirmed);
            Assert.Equal(VolumeNumberFail.ZipTooManyDisks, order.Fail);
        }

        // ════════════════ D. 「还原」工序接回批首改名（挂点①） ════════════════
        //
        // 用户 2026-10-04（方案 §4 阶段 D）：批首那条路（`RenameService.BuildFixByDetectedFormatFileName`
        // =「智能修正后缀」）与识别结果之间**不许出现两套口径**（AGENTS.md §9.5：同一件事只有一个出口）。
        // 下面两条把口径钉在两件事实上：①「批首改名」与「还原工序」对同一组算出的名字**逐字相同**；
        // ② 批首那一侧**一个内容字节都不动**，而且"格式未知 / 末尾纯数字"两道闸门一条都没放宽。

        /// <summary>
        /// **批首那一侧（改名预览）与还原工序必须给出同一份目标名。**
        ///
        /// <para>夹具：真 Rar.exe 造的 RAR5 三卷，每一卷的 <c>.rar</c> 都缀了「删除」
        /// （<c>风景01.partN.rar删除</c>）—— 这正是方案 §4 阶段 D 点名的那一格。</para>
        ///
        /// <para>⛔ 两条不许违反：<b>替换不是追加</b>（目标名里不许出现 <c>.rar删除.part1.rar</c> 这种
        /// "把伪装名当基名"的形状）、<b>基名一个字不动</b>。</para>
        /// </summary>
        [RarAndSevenZipFact]
        public async Task 批首改名预览_与还原工序逐字同一份目标名_而且一个内容字节都不动()
        {
            string directory = await CreateRealDisguisedRarGroupAsync("batch-first-align");

            string[] before = NamesIn(directory);
            var hashes = before.ToDictionary(
                name => name,
                name => Sha256(Path.Combine(directory, name)),
                StringComparer.OrdinalIgnoreCase);

            // ① 批首那一侧：智能修正后缀算出来的名字（= 识别结果要的那一套）。
            //    ⚠ 必须用**独立的一份副本**：还原工序会在盘上真改名，先跑它的话批首看到的已经是标准名了。
            string previewDirectory = Path.Combine(_root, "batch-first-align-preview");
            Directory.CreateDirectory(previewDirectory);

            foreach (string name in before)
            {
                File.Copy(Path.Combine(directory, name), Path.Combine(previewDirectory, name));
            }

            List<RenamePreviewItem> preview = BuildFixPreview(
                NamesIn(previewDirectory).Select(name => CreateRarTask(Path.Combine(previewDirectory, name))).ToList());

            List<string> batchTargets = preview
                .Select(item => item.NewFileName)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // ② 还原工序那一侧（递归层挂点用的就是它）。
            var restoreLog = new List<string>();
            IReadOnlyList<string> restored = VolumeNameRepair.RestoreDisguisedInnerPackageNames(
                before.Select(name => Path.Combine(directory, name)).ToList(),
                (level, message) => restoreLog.Add($"{level}:{message}"));

            List<string> restoreTargets = restored
                .Select(Path.GetFileName)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList()!;

            // ③ 两道口径**逐字相同**（这就是"识别说该这么改、批首却按另一套算"不许出现的那一格）。
            Assert.Equal(restoreTargets, batchTargets);
            Assert.Equal(new[] { "风景01.part1.rar", "风景01.part2.rar", "风景01.part3.rar" }, restoreTargets);

            // 还原那一步**真的跑过**（日志那一行就是"做过"的机器证据，⛔ 不是靠"文件变成标准名了"倒推）。
            Assert.Contains(
                restoreLog,
                line => line.StartsWith("INFO:", StringComparison.Ordinal)
                        && line.Contains("还原伪装后缀", StringComparison.Ordinal));

            // ⛔ 替换不是追加：目标名里不许把伪装的那一段当基名留下来。
            Assert.All(
                batchTargets,
                name => Assert.DoesNotContain("删除.part", name, StringComparison.Ordinal));

            // ④ 预览**只算不改**：盘上一个名字、一个内容字节都不许动。
            Assert.Equal(before, NamesIn(previewDirectory));

            foreach ((string name, string hash) in hashes)
            {
                Assert.Equal(hash, Sha256(Path.Combine(previewDirectory, name)));
            }

            // ⑤ 还原**只改名字**：改完内容逐字节一致，而且回到标准名之后这一组已经是"标准组"。
            foreach (string path in restored)
            {
                Assert.True(File.Exists(path), $"还原没落盘：{path}");
            }

            foreach ((string name, string hash) in hashes)
            {
                string restoredName = name.Replace("删除", string.Empty, StringComparison.Ordinal);

                Assert.Equal(hash, Sha256(Path.Combine(directory, restoredName)));
            }

            VolumeNameRepairPlan afterRestore = await PlanByContentAsync(Path.Combine(directory, "风景01.part1.rar"));

            Assert.False(afterRestore.CanRepair);
            Assert.Equal(StatusText.VolumeRepairAlreadyStandard, afterRestore.Reason);
        }

        /// <summary>
        /// **两道闸门一条都没放宽**（2026-09-29 真机事故定的）：
        /// ① <b>格式未知 ⇒ 一个字都不改</b>；② <b>末尾是纯数字（那是卷号）⇒ 不当后缀替换</b>。
        ///
        /// <para>两条都要有"本来会改"的对照物，否则"没改"可能只是因为没什么可改的。</para>
        /// </summary>
        [Fact]
        public void 批首改名_格式未知与末尾纯数字_两个名字都不改()
        {
            string directory = NewDirectory("batch-first-gates");

            string unknown = Write(Path.Combine(directory, "风景01.rar删除"), new byte[] { 1, 2, 3 });
            string numeric = Write(Path.Combine(directory, "风景02.7.01"), new byte[] { 4, 5, 6 });

            // 对照物：同样的伪装名，只要格式认得出来，就该改成标准名。
            string control = Write(Path.Combine(directory, "风景03.ra删除r"), Rar5MainHeader(0x0001u, null));

            var controlTask = new ArchiveTask(control)
            {
                DetectedFormat = "RAR",
                SuggestedExtension = ".rar",
                IsArchive = true,
                ExtensionStatus = StatusText.ExtensionMismatch,
                IsSelected = true
            };

            var unknownTask = new ArchiveTask(unknown)
            {
                DetectedFormat = "Unknown",
                SuggestedExtension = string.Empty,
                IsArchive = false,
                ExtensionStatus = StatusText.ExtensionMultiFake,
                IsSelected = true
            };

            var numericTask = new ArchiveTask(numeric)
            {
                DetectedFormat = "7Z",
                SuggestedExtension = ".7z",
                IsArchive = true,
                ExtensionStatus = StatusText.ExtensionMismatch,
                IsSelected = true
            };

            List<RenamePreviewItem> preview = BuildFixPreview(new[] { controlTask, unknownTask, numericTask });

            RenamePreviewItem controlItem = preview.Single(i => i.OriginalFileName == "风景03.ra删除r");
            RenamePreviewItem unknownItem = preview.Single(i => i.OriginalFileName == "风景01.rar删除");
            RenamePreviewItem numericItem = preview.Single(i => i.OriginalFileName == "风景02.7.01");

            // 对照物**真的被改了**（证明下面两条不是"本来就没什么可改"）：
            // `风景03.ra删除r` 的后缀段只差 1 个字符、同一族里唯一 ⇒ 认得出底层 ⇒ 改成标准名。
            Assert.Equal("风景03.rar", controlItem.NewFileName);

            // ① 格式未知 ⇒ 一个字都不改（`风景01.rar删除` 连内容都没有，认不出来）。
            Assert.Equal("风景01.rar删除", unknownItem.NewFileName);

            // ② 末尾那一段是纯数字 ⇒ 那是**卷号**，不当下缀替换。
            Assert.Equal("风景02.7.01", numericItem.NewFileName);

            Assert.Equal(new[] { "风景01.rar删除", "风景02.7.01", "风景03.ra删除r" }, NamesIn(directory));
        }

        /// <summary>
        /// **批级整组改名（挂点①的协调器那一层）与批首改名不打架**：同一组伪装名源包，
        /// 两条路算出来的目标名与盘上结果必须一致 —— 改成标准名、一个内容字节都不动、
        /// 任务账（<c>CurrentPath</c> / <c>VolumePaths</c> / <c>VolumeNameAutoRenamed</c>）跟着更新。
        ///
        /// <para>这正是方案 §4 阶段 D 点名的"识别说该这么改、批首却按另一套算"那一格：
        /// 判据与执行体都只有 <c>VolumeNameRepair</c> 一份（AGENTS.md §9.5），本用例把它钉在盘上。</para>
        /// </summary>
        [RarAndSevenZipFact]
        public async Task 批级整组改名_与批首改名同一份口径_盘上只剩标准名而且内容一字节没动()
        {
            string directory = await CreateRealDisguisedRarGroupAsync("batch-coordinator");

            string[] before = NamesIn(directory);
            var hashes = before.ToDictionary(
                name => name,
                name => Sha256(Path.Combine(directory, name)),
                StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<ArchiveTask> tasks = new VolumeGroupingService()
                .ApplyVolumeGrouping(before.Select(name => CreateRarTask(Path.Combine(directory, name))).ToList());

            Assert.Single(tasks);

            ArchiveTask task = tasks[0];

            // 批首那一侧要的名称（识别结果那一套）：归组之后**一个任务代表整组**，
            // 所以预览里只有这一组的入口那一卷；它给出的名字必须就是整组标准名的第 1 个。
            List<string> wanted = BuildFixPreview(tasks)
                .Select(item => item.NewFileName)
                .ToList();

            Assert.Equal(new[] { "风景01.part1.rar" }, wanted);
            Assert.Equal(Path.Combine(directory, "风景01.part1.rar"), BuildFixPreview(tasks)[0].NewPath, ignoreCase: true);

            Harness harness = CreateHarness();

            await harness.Coordinator.NormalizeDisguisedVolumeNamesForBatchAsync(tasks);

            // 盘上：**整组**三个标准名都在、旧名都没了、内容逐字节一致。
            string[] afterBatch = NamesIn(directory);

            Assert.Equal(new[] { "风景01.part1.rar", "风景01.part2.rar", "风景01.part3.rar" }, afterBatch);
            Assert.Equal(wanted, afterBatch.Take(1));
            Assert.True(task.VolumeNameAutoRenamed, "任务账没记下「分卷名已自动修正」");

            foreach ((string name, string hash) in hashes)
            {
                string renamed = name.Replace("删除", string.Empty, StringComparison.Ordinal);

                Assert.Equal(hash, Sha256(Path.Combine(directory, renamed)));
            }

            // 任务账跟着改名一起走（否则这一单后面会按旧名字找卷 ⇒ 假报缺卷）。
            Assert.Equal(Path.Combine(directory, "风景01.part1.rar"), task.CurrentPath, ignoreCase: true);
            Assert.Contains(Path.Combine(directory, "风景01.part2.rar"), task.VolumePaths);
            Assert.Contains(Path.Combine(directory, "风景01.part3.rar"), task.VolumePaths);

            foreach (string name in before)
            {
                Assert.DoesNotContain(Path.Combine(directory, name), task.VolumePaths);
            }
        }

        // ════════════════ 夹具与助手 ════════════════

        private string NewDirectory(string name)
        {
            string directory = Path.Combine(_root, name);
            Directory.CreateDirectory(directory);

            return directory;
        }

        private string Write(string path, byte[] bytes)
        {
            File.WriteAllBytes(path, bytes);

            return path;
        }

        private static string[] NamesIn(string directory) =>
            Directory.GetFiles(directory)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray()!;

        private static string WorkRootFor(string anyFileInDirectory) =>
            Path.Combine(Path.GetDirectoryName(anyFileInDirectory) ?? ".", WorkDirectoryName);

        private static Engines.IArchiveEngine NewEngine() => new Engines.SevenZip.SevenZipEngine();

        /// <summary>批量改名用的那个任务对象（格式认得出来的 RAR）。</summary>
        private static ArchiveTask CreateRarTask(string path) => new(path)
        {
            DetectedFormat = "RAR",
            SuggestedExtension = ".rar",
            IsArchive = true,
            ExtensionStatus = StatusText.ExtensionMismatch,
            IsSelected = true
        };

        /// <summary>
        /// **批首那条路**：`RenameService.BuildPreview`（选项与 <c>RenameCoordinator</c> 里
        /// `BuildFixByDetectedFormatOptions()` 逐字相同）。
        /// </summary>
        private static List<RenamePreviewItem> BuildFixPreview(IReadOnlyList<ArchiveTask> tasks)
        {
            var options = new RenameOptions
            {
                OperationType = "FixByDetectedFormat",
                TargetExtension = ".7z",
                DeleteExtensionCount = 1,
                ConflictAction = "AutoRename",
                PreviewBeforeRename = true,
                UnknownFormatAction = "MarkUnknown"
            };

            options.Normalize();

            return new RenameService().BuildPreview(tasks, options);
        }

        private static string Sha256(string path) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

        /// <summary>
        /// 批级整组改名要的那套最小宿主（与 <c>VolumeNameRepairTests.CreateHarness</c> 同一套接线：
        /// 设置项固定在"源包留在原地"，因为这一组测的是改名，不该顺带测源包搬运 / 删除）。
        /// </summary>
        private Harness CreateHarness()
        {
            string dataRoot = Path.Combine(_root, "data", Guid.NewGuid().ToString("N"));
            string outputRoot = Path.Combine(_root, "out", Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;
            settings.MaxParallelExtractCount = 1;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;

            settingsService.Save(settings);

            var engine = new Engines.SevenZip.SevenZipEngine();
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

            return new Harness(coordinator);
        }

        private sealed class Harness
        {
            public Harness(ExtractionCoordinator coordinator) => Coordinator = coordinator;

            public ExtractionCoordinator Coordinator { get; }
        }

        /// <summary>无界面宿主里默认是"没人点过 = 不确认"，改名那条路就走不到了 —— 这里注入"用户点了确定"。</summary>
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

        private static Task<VolumeNameRepairPlan> PlanByContentAsync(string entry) =>
            VolumeNameRepair.PlanByContentAsync(
                entry,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(entry),
                NewEngine(),
                WorkRootFor(entry));

        /// <summary>用真 <c>Rar.exe</c> 造一组三卷 RAR5，再把每一卷的 <c>.rar</c> 缀上「删除」（网盘那套）。</summary>
        private async Task<string> CreateRealDisguisedRarGroupAsync(string name)
        {
            string directory = NewDirectory(name);

            var payload = new byte[240_000];
            new Random(20261012).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(directory, "payload.bin"), payload);

            await Task.Run(() => RunRar(directory, "a", "-ma5", "-m0", "-idq", "-ep1", "-v100k", "风景01.rar", "payload.bin"));

            foreach (string file in Directory.GetFiles(directory, "风景01.part*.rar"))
            {
                File.Move(file, file + "删除");
            }

            File.Delete(Path.Combine(directory, "payload.bin"));

            Assert.Equal(3, Directory.GetFiles(directory, "风景01.part*.rar删除").Length);

            return directory;
        }

        private void RunRar(string workingDirectory, params string[] args)
        {
            Assert.False(string.IsNullOrEmpty(_rar), "测试机上没有 Rar.exe");

            var psi = new ProcessStartInfo(_rar!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 Rar.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(120_000), "Rar.exe 超时");
            Assert.True(process.ExitCode == 0, $"Rar.exe 失败：{stdout}{stderr}");
        }

        /// <summary>
        /// 让真 7-Zip 列一遍条目（"改名之后这一组真的能打开"的机器证据）。
        ///
        /// <para>用 <c>-slt</c> 的**键值清单**而不是那张表格：`-ba` 在 7-Zip 26.03 上会把条目行**一起**
        /// 吞掉（实测：`7z l -ba` 的输出里一个条目都没有，只剩空行），而表格还得靠列位置猜。</para>
        /// </summary>
        private IReadOnlyList<string> ListEntries(string archivePath)
        {
            Assert.False(string.IsNullOrEmpty(_sevenZip), "测试机上没有 7z.exe");

            var psi = new ProcessStartInfo(_sevenZip!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            psi.ArgumentList.Add("l");
            psi.ArgumentList.Add("-slt");
            psi.ArgumentList.Add(archivePath);

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(120_000), "7z.exe 超时");
            Assert.True(process.ExitCode == 0, $"7z.exe 退出码 {process.ExitCode}：{stdout}{stderr}");

            _output.WriteLine($"[7z l -slt] {stdout}{stderr}");

            // 引擎真的把这一组当成"多卷"打开了吗（这一条比条目数更直接）。
            Assert.Contains("Multivolume = +", stdout, StringComparison.Ordinal);

            var entries = new List<string>();
            string? pendingName = null;

            foreach (string line in stdout.Split('\n'))
            {
                string text = line.TrimEnd('\r');

                if (text.StartsWith("Path = ", StringComparison.Ordinal))
                {
                    pendingName = text["Path = ".Length..].Trim();
                }
                else if (text.StartsWith("Size = ", StringComparison.Ordinal) && pendingName != null)
                {
                    entries.Add(pendingName);
                    pendingName = null;
                }
            }

            return entries;
        }

        // ── 合成卷头（只造"卷自述"，不造真归档内容） ──

        /// <summary>
        /// RAR5 主归档头：<c>volumeField</c> 有值就是"后续卷 + 自己的绝对卷号"（RAR5 有真样本，
        /// 见 <c>AGENTS.md</c> §11.4），<c>null</c> = 第 1 卷（字段省略）。
        /// </summary>
        private static byte[] Rar5MainHeader(uint archiveFlags, uint? volumeField)
        {
            var body = new List<byte>();

            body.AddRange(Vint(1));             // header type = main
            body.AddRange(Vint(0));             // header flags
            body.AddRange(Vint(archiveFlags));  // archive flags（bit0 = VOLUME）

            if (volumeField.HasValue)
            {
                body.AddRange(Vint(volumeField.Value));
            }

            byte[] sizeField = Vint((ulong)body.Count).ToArray();

            var header = new List<byte> { (byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x01, 0x00 };

            var covered = new List<byte>();
            covered.AddRange(sizeField);
            covered.AddRange(body);

            header.AddRange(BitConverter.GetBytes(TestCrc32(covered)));
            header.AddRange(sizeField);
            header.AddRange(body);

            return header.ToArray();
        }

        /// <summary>
        /// RAR 1.5–4.x：签名 + 主头（13 字节）+ 归档结尾块（卷号在里面）。
        /// <paramref name="newNumbering"/> = <c>false</c> ⇒ **老式编号族**（<c>.rar</c>/<c>.r00</c>）。
        /// </summary>
        private static byte[] Rar4Archive(
            ushort mainFlags,
            ushort volumeField,
            bool firstVolume,
            bool newNumbering = true,
            bool nextVolume = true)
        {
            ushort flags = mainFlags;

            if (newNumbering)
            {
                flags |= 0x0010;
            }

            if (firstVolume)
            {
                flags |= 0x0100;
            }

            var bytes = new List<byte> { (byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x00 };

            var main = new byte[] { 0x73, (byte)(flags & 0xFF), (byte)(flags >> 8), 0x0D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

            bytes.AddRange(BitConverter.GetBytes(TestCrc16(main)));
            bytes.AddRange(main);

            ushort endFlags = 0x0002 | 0x0004 | 0x0008;

            if (nextVolume)
            {
                endFlags |= 0x0001;
            }

            const ushort endSize = 7 + 4 + 2 + 7;

            var end = new List<byte> { 0x7B, (byte)(endFlags & 0xFF), (byte)(endFlags >> 8), (byte)(endSize & 0xFF), (byte)(endSize >> 8) };

            end.AddRange(new byte[] { 0x11, 0x22, 0x33, 0x44 }); // EARC_DATACRC
            end.AddRange(BitConverter.GetBytes(volumeField));      // EARC_VOLNUMBER
            end.AddRange(new byte[7]);                             // EARC_REVSPACE

            bytes.AddRange(BitConverter.GetBytes(TestCrc16(end.ToArray())));
            bytes.AddRange(end);

            return bytes.ToArray();
        }

        /// <summary>跨盘 zip 的**末片**：一小段数据 + EOCD（盘号就写在 EOCD 里）。</summary>
        private static byte[] ZipTail(int diskNumber, int centralDirectoryDisk, byte[] comment)
        {
            var bytes = new List<byte> { 0x50, 0x4B, 0x03, 0x04 };

            bytes.AddRange(new byte[26]);
            bytes.AddRange(new byte[] { 0xAA, 0xBB, 0xCC });
            bytes.AddRange(new byte[] { 0x50, 0x4B, 0x05, 0x06 });
            bytes.AddRange(BitConverter.GetBytes((ushort)diskNumber));
            bytes.AddRange(BitConverter.GetBytes((ushort)centralDirectoryDisk));
            bytes.AddRange(BitConverter.GetBytes((ushort)1));
            bytes.AddRange(BitConverter.GetBytes((ushort)1));
            bytes.AddRange(BitConverter.GetBytes(0u));
            bytes.AddRange(BitConverter.GetBytes(0u));
            bytes.AddRange(BitConverter.GetBytes((ushort)comment.Length));
            bytes.AddRange(comment);

            return bytes.ToArray();
        }

        /// <summary>跨盘 zip 的**非末片**能以哪个签名开头。</summary>
        private enum ZipSegmentHead
        {
            LocalFileHeader,
            SpanningMarker,
            CentralDirectory,
            NotZip
        }

        /// <summary>跨盘 zip 的**非末片**：给定签名开头 + 填充（真样本的切点落在数据中间，结尾没有标记）。</summary>
        private static byte[] ZipSegment(int size, ZipSegmentHead head)
        {
            var bytes = new List<byte>();

            switch (head)
            {
                case ZipSegmentHead.LocalFileHeader:
                    bytes.AddRange(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00, 0x00, 0x00 });
                    break;

                case ZipSegmentHead.SpanningMarker:
                    bytes.AddRange(new byte[] { 0x50, 0x4B, 0x07, 0x08 });
                    break;

                case ZipSegmentHead.CentralDirectory:
                    bytes.AddRange(new byte[] { 0x50, 0x4B, 0x01, 0x02 });
                    break;

                default:
                    bytes.AddRange(new byte[] { 0x2C, 0xE5, 0xA7, 0xA0 });
                    break;
            }

            while (bytes.Count < size)
            {
                bytes.Add(0x5A);
            }

            return bytes.ToArray();
        }

        private static IEnumerable<byte> Vint(ulong value)
        {
            do
            {
                byte current = (byte)(value & 0x7F);
                value >>= 7;

                if (value != 0)
                {
                    current |= 0x80;
                }

                yield return current;
            }
            while (value != 0);
        }

        private static readonly uint[] TestCrcTable = BuildTestCrcTable();

        private static uint[] BuildTestCrcTable()
        {
            var table = new uint[256];

            for (uint index = 0; index < 256; index++)
            {
                uint value = index;

                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }

        private static uint TestCrc32(IReadOnlyList<byte> data)
        {
            uint crc = 0xFFFFFFFF;

            foreach (byte current in data)
            {
                crc = (crc >> 8) ^ TestCrcTable[(crc ^ current) & 0xFF];
            }

            return crc ^ 0xFFFFFFFF;
        }

        private static ushort TestCrc16(IReadOnlyList<byte> data) => (ushort)(TestCrc32(data) & 0xFFFF);
    }

    /// <summary>"这台机器上既有 <c>Rar.exe</c> 又有 7z.exe"才跑的用例（发现阶段条件跳过，⛔ 不伪装成验过）。</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RarAndSevenZipFactAttribute : FactAttribute
    {
        public RarAndSevenZipFactAttribute()
        {
            if (string.IsNullOrEmpty(SevenZipFactAttribute.LocateSevenZipPath()))
            {
                Skip = "测试机上没有可用的 7z.exe，跳过需要真引擎的用例。";

                return;
            }

            if (RarSampleSet.LocateRarExe() == null)
            {
                Skip = "测试机上没有 Rar.exe（WinRAR 目录），造不出真 RAR 分卷，跳过。";
            }
        }
    }
}

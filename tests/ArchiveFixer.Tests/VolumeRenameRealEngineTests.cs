using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **改完名字之后，真引擎到底能不能按新名字解开** —— 补掉 2026-10-04 第四轮唯一那条未验
    /// （那一轮只断言到"计划 / 改名结果 / 基名 / 卷序"，没跑真引擎）。
    ///
    /// <para>断言链五条（用户点名的顺序）：① 识别 / 改名计划成立（基名、卷序、规范名都对）→
    /// ② <see cref="VolumeNameRepair.TryApply"/> 真改名 → ③ **真引擎**（生产同一条路
    /// <see cref="EngineRegistry"/> + <see cref="EngineRouter"/>，⛔ 本文件一行 7z / UnRAR 参数都不拼）
    /// 打开**改过名的首卷**并列出 / 解出内容 → ④ 解出来的字节与原始负载**逐字节相同** →
    /// ⑤ 改名前后**内容 SHA256 一个字节没变**。</para>
    ///
    /// <para>样本全是**真实工具现造**的：7z 分卷由内置 <c>7z.exe</c>（<c>-mx0 -v1m</c>）造、
    /// RAR 分卷由本机 <c>Rar.exe</c> 造（⛔ 绝不进仓库，没装就跳过并说明）。</para>
    ///
    /// <para>⚠ 第五轮在这里**钉住过两处计划层缺口**（只脏归档后缀段不给计划 / 7z 数字族卷标记脏没有「整组自洽」闸门）；
    /// 用户 2026-10-04 第六轮把两处都补上了 ⇒ 那两条**按新结论改写**
    /// （<see cref="结论已变_只脏归档后缀段的两条入口给出同一份规范名_真7zip按新名字解得开"/>
    /// 与 <see cref="反面_孤零零一个脏名卷_计划阶段就停_没有任何标准名入口可开"/> 末段），⛔ 不是放宽断言。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public sealed class VolumeRenameRealEngineTests : IDisposable
    {
        /// <summary>载荷的文件名（造样本时只传这个名字给 7z / Rar，免得把整条路径存进包里）。</summary>
        private const string PayloadName = "payload.bin";

        /// <summary>载荷大小：<c>-v1m</c> 一卷 ⇒ 造得出 4 卷，中间那卷不是首卷也不是末卷。</summary>
        private const int PayloadBytes = 3 * 1024 * 1024;

        private readonly string _root;
        private readonly string _sevenZip;
        private readonly string _rar;
        private readonly string _evidenceDirectory;
        private readonly ITestOutputHelper _output;

        public VolumeRenameRealEngineTests(ITestOutputHelper output)
        {
            _output = output;
            _sevenZip = SevenZipFactAttribute.LocateSevenZipPath();
            _rar = RarVolumeFactAttribute.LocateRarPath();
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerRenameRealEngine", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            // 引擎原话留档（可选）：设了变量才写，⛔ 仓库里不出现任何个人路径。
            _evidenceDirectory = (Environment.GetEnvironmentVariable("ARCHIVEFIXER_E2E_EVIDENCE_DIR") ?? string.Empty).Trim();
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
                // 临时目录清不掉不影响结论。
            }
        }

        // ════════════════════════════ ① 7z 数字族：中间那一卷的**卷标记段**被改脏 ════════════════════════════

        /// <summary>
        /// **卷号段夹杂质**（数字骨架：<c>0a0b2</c> ⇒ <c>002</c>）—— 7z 数字族里"卷标记段本身脏"的
        /// 标准形状，也是上一轮接进改名的那一档（<c>VolumeNameRepair.Plan</c> 从**干净的首卷**进去就能认出来）。
        /// </summary>
        [SevenZipFact]
        public Task 真7z_中间卷的卷号段夹杂质_改名之后真7zip按新名字解得开() =>
            RepairSevenZipVolumesAndOpenWithRealEngineAsync("set.7z.0a0b2", "卷号段夹杂质（0a0b2 ⇒ 002）");

        /// <summary>**后缀段与卷号段都脏**（<c>set.7aaaaz.0a0b2</c>）：骨架档把后缀段归一成 <c>7z</c>、卷号段读成 <c>002</c>。</summary>
        [SevenZipFact]
        public Task 真7z_中间卷的后缀段与卷号段都脏_改名之后真7zip按新名字解得开() =>
            RepairSevenZipVolumesAndOpenWithRealEngineAsync("set.7aaaaz.0a0b2", "后缀段 + 卷号段都脏");

        /// <summary>**卷标记后面粘着垃圾**（网盘给分卷名缀「删除」那种，<c>set.7z.002删除</c>）。</summary>
        [SevenZipFact]
        public Task 真7z_中间卷卷标记粘着垃圾_改名之后真7zip按新名字解得开() =>
            RepairSevenZipVolumesAndOpenWithRealEngineAsync("set.7z.002删除", "卷标记粘垃圾");

        /// <summary>**卷标记后面另起一段后缀**（<c>set.7z.002.txt</c>）。</summary>
        [SevenZipFact]
        public Task 真7z_中间卷卷标记后面另起一段后缀_改名之后真7zip按新名字解得开() =>
            RepairSevenZipVolumesAndOpenWithRealEngineAsync("set.7z.002.txt", "卷标记后另起一段后缀");

        /// <summary>
        /// **只脏归档后缀段**（<c>set.7aaaaz.002</c>：后缀段靠骨架档读成 <c>7z</c>、卷号段是干净的 <c>002</c>）
        /// —— 用户 2026-10-04 第六轮点名要接进改名计划的那一档。
        ///
        /// <para>⚠ 本条的前身是第五轮那条「**钉住现状**：这一档计划不成立」（当时计划层没覆盖，实测
        /// 计划给不出名字、真 7-Zip 打不开；手工改回规范名后立刻解得开）。第六轮把这一形状接进了
        /// <c>FileNameHelper.TryResolveDisguisedVolume</c> 的形状③ ⇒ **结论变了**，断言按新结论改写
        /// （⛔ 不是放宽，是缺口补上了）：计划成立 → 改名 → 真 7-Zip 按新名字解得开 → 字节逐字节相同。</para>
        /// </summary>
        [SevenZipFact]
        public Task 真7z_中间卷只脏归档后缀段_改名之后真7zip按新名字解得开() =>
            RepairSevenZipVolumesAndOpenWithRealEngineAsync("set.7aaaaz.002", "只脏归档后缀段（7aaaaz ⇒ 7z）");

        /// <summary>同上，换成**下划线骨架**（<c>7_______z</c> ⇒ <c>7z</c>）—— 用户原话里点名的另一个形状。</summary>
        [SevenZipFact]
        public Task 真7z_中间卷后缀段被下划线伪装_改名之后真7zip按新名字解得开() =>
            RepairSevenZipVolumesAndOpenWithRealEngineAsync("set.7_______z.002", "只脏归档后缀段（7_______z ⇒ 7z）");

        /// <summary>
        /// 五条断言的共用实现（见类注释）：真 7z 造四卷 → 把**中间那卷**改名成 <paramref name="dirtyName"/> →
        /// 计划 → 真改名 → 真引擎按新名字列出 / 解出 → 逐字节比对 + SHA256 快照比对。
        /// </summary>
        private async Task RepairSevenZipVolumesAndOpenWithRealEngineAsync(string dirtyName, string what)
        {
            RequireSevenZip();

            string directory = NewDirectory("7z-" + MakeSafeName(dirtyName));
            string payloadPath = CreatePayload("stage-7z-" + MakeSafeName(dirtyName));
            byte[] payload = File.ReadAllBytes(payloadPath);

            CreateSevenZipVolumes(directory, payloadPath);

            string first = Path.Combine(directory, "set.7z.001");
            string dirty = Path.Combine(directory, dirtyName);

            File.Move(Path.Combine(directory, "set.7z.002"), dirty);

            Dictionary<string, string> before = SnapshotHashes(directory);

            // ── ① 识别 / 改名计划成立（基名、卷序、规范名都对） ──
            string[] names = NamesIn(directory);
            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(first, names);

            Assert.True(plan.CanRepair, $"「{what}」这一组必须出得来改名计划，实际：{plan.Describe()}");

            VolumeRepairItem only = Assert.Single(plan.Items);
            Assert.Equal(dirtyName, only.CurrentFileName);
            Assert.Equal("set.7z.002", only.SuggestedFileName);
            Assert.Equal(dirty, plan.CurrentPath);
            Assert.Equal(Path.Combine(directory, "set.7z.002"), plan.TargetPath);

            // 包基名（用户口径里的"基名"）：计划里那个规范名剥到最后一层必须是 `set`。
            Assert.Equal("set", OutputPlacement.ResolveArchiveBaseName(only.SuggestedFileName));

            /*
             * 卷序（识别那一层）：脏名也必须读得出"这是第 2 卷"。
             *
             * ⚠ 刻意**不**断言 TryGetFirstVolumeName(脏名) == "set.7z.001"：那个出口回答的是
             * "按**这一卷自己的写法**推同族首卷名"（对 `set.7z.002删除` 给的是 `set.7z.001删除`），
             * 服务的是"缺卷时按用户手上的命名风格点名"，不是本用例要的那件事 ——
             * 本用例要的"规范名"由计划（SuggestedFileName）与组基名（下面两条）回答。
             */
            Assert.Equal(2, VolumeGroupDetector.TryGetVolumeIndex(dirtyName));

            VolumeGroup group = Assert.Single(VolumeGroupDetector.Group(
                Directory.GetFiles(directory).Select(path => new VolumeCandidate { Path = path, Size = new FileInfo(path).Length })));

            Assert.Equal("set.7z", group.BaseName);
            Assert.True(group.KnownVolumeCount >= 3, $"整组至少三卷，实际 {group.KnownVolumeCount} 卷");
            Assert.True(group.IsComplete, $"这一组应当自洽（卷标记连续 + 除末片外等大），实际：{group.Note}");
            Assert.Equal(first, group.FirstVolumePath);

            // 出计划这一步**一个字节都不碰盘**（改名是不可逆动作，计划阶段只准算）。
            AssertHashesUnchanged(directory, before);

            // ── ② TryApply 真改名 ──
            VolumeNameRepairResult applied = VolumeNameRepair.TryApply(plan);

            Assert.True(applied.Success, applied.Message);
            Assert.Equal(Path.Combine(directory, "set.7z.002"), applied.NewPath);
            Assert.True(File.Exists(Path.Combine(directory, "set.7z.002")), "标准名必须在");
            Assert.False(File.Exists(dirty), "旧名字必须没了");

            // ── ⑤ 改名前后内容 SHA256 一个字节没变（名字变了、内容不动；同组其余卷也不动） ──
            Assert.Equal(before["set.7z.001"], Sha256Of(first));
            Assert.Equal(before[dirtyName], Sha256Of(Path.Combine(directory, "set.7z.002")));
            Assert.Equal(
                before.Values.OrderBy(hash => hash, StringComparer.Ordinal),
                Directory.GetFiles(directory).Select(Sha256Of).OrderBy(hash => hash, StringComparer.Ordinal));

            // ── ③ 真引擎（生产同一条路）打开**改过名的首卷**并列出 / 解出 ──
            string outputDirectory = Path.Combine(_root, "out-" + MakeSafeName(dirtyName));
            (ArchiveListResult list, ArchiveOperationResult extract) = await OpenWithRealEngineAsync(first, outputDirectory, what);

            Assert.Equal(EngineIds.SevenZip, list.EngineId);
            Assert.Equal(EngineIds.SevenZip, extract.EngineId);
            Assert.False(string.IsNullOrWhiteSpace(extract.EngineVersion), "报告要能追到具体引擎与版本（不变量 14）");

            // ── ④ 解出来的字节与原始负载逐字节相同 ──
            AssertPayloadRoundTripped(outputDirectory, payload, what);
        }

        // ════════════════════════════ ② RAR partN 族：中间那一卷的卷标记段被改脏 ════════════════════════════

        /// <summary>
        /// 真 <c>Rar.exe</c> 造的 RAR5 四卷，把**中间那卷**改成 <c>444.pa8rt2.rar</c>（卷标记靠骨架档才读成
        /// <c>part2</c>，即上一轮接进改名的那一档）⇒ 整组自洽 ⇒ 只改那一卷。
        /// 然后由**真 UnRAR**（走生产那条 <see cref="EngineRegistry"/> 路）打开改过名的首卷 `444.part1.rar`。
        /// </summary>
        [RarVolumeFact]
        public async Task 真RAR_中间卷的卷标记段改成脏的_改名之后真UnRAR按新名字解得开()
        {
            RequireSevenZip();
            RequireRar();

            const string dirtyName = "444.pa8rt2.rar";

            string directory = NewDirectory("rar-part-skeleton");
            string payloadPath = CreatePayload("stage-rar-part-skeleton");
            byte[] payload = File.ReadAllBytes(payloadPath);

            CreateRarVolumes(directory, payloadPath);

            string first = Path.Combine(directory, "444.part1.rar");
            string dirty = Path.Combine(directory, dirtyName);

            File.Move(Path.Combine(directory, "444.part2.rar"), dirty);

            Dictionary<string, string> before = SnapshotHashes(directory);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(first, NamesIn(directory));

            Assert.True(plan.CanRepair, $"「卷标记段本身脏」这一组必须出得来改名计划，实际：{plan.Describe()}");

            VolumeRepairItem only = Assert.Single(plan.Items);
            Assert.Equal(dirtyName, only.CurrentFileName);
            Assert.Equal("444.part2.rar", only.SuggestedFileName);

            Assert.Equal(2, VolumeGroupDetector.TryGetVolumeIndex(dirtyName));
            Assert.Equal("444.part1.rar", VolumeGroupDetector.TryGetFirstVolumeName(dirtyName));

            VolumeGroup group = Assert.Single(VolumeGroupDetector.Group(
                Directory.GetFiles(directory).Select(path => new VolumeCandidate { Path = path, Size = new FileInfo(path).Length })));

            Assert.Equal("444", group.BaseName);
            Assert.True(group.IsComplete, $"这一组应当自洽，实际：{group.Note}");
            Assert.Equal(first, group.FirstVolumePath);

            AssertHashesUnchanged(directory, before);

            VolumeNameRepairResult applied = VolumeNameRepair.TryApply(plan);

            Assert.True(applied.Success, applied.Message);
            Assert.True(File.Exists(Path.Combine(directory, "444.part2.rar")));
            Assert.False(File.Exists(dirty));

            Assert.Equal(before["444.part1.rar"], Sha256Of(first));
            Assert.Equal(before[dirtyName], Sha256Of(Path.Combine(directory, "444.part2.rar")));
            Assert.Equal(
                before.Values.OrderBy(hash => hash, StringComparer.Ordinal),
                Directory.GetFiles(directory).Select(Sha256Of).OrderBy(hash => hash, StringComparer.Ordinal));

            string outputDirectory = Path.Combine(_root, "out-rar-part-skeleton");
            (ArchiveListResult list, ArchiveOperationResult extract) = await OpenWithRealEngineAsync(
                first,
                outputDirectory,
                "RAR 卷标记段本身脏");

            /*
             * **解压**那一趟必须还是 UnRAR（RAR 族的第一顺位引擎）。
             *
             * ⚠ **列出**那一趟不钉引擎：本机 ToolLocator 解析到的是**已装 WinRAR 目录里那份中文界面
             * UnRAR 6.11**，它列出的清单解析不出来 ⇒ EngineRouter 按既有口径改问 7-Zip
             * （"解压仍优先 UnRAR"，见 AGENTS.md §11.4「本地化 UnRAR 的假清单」）——
             * 所以 list 那一趟是 7-Zip 干的活、extract 这一趟仍是 UnRAR：两条都记进证据里。
             */
            Assert.Equal(EngineIds.WinRar, extract.EngineId);
            Assert.Contains(list.EngineId, new[] { EngineIds.WinRar, EngineIds.SevenZip });

            AssertPayloadRoundTripped(outputDirectory, payload, "RAR 卷标记段本身脏");
        }

        // ════════════════════════════ ③ 反面对照：孤零零一个脏名卷 ════════════════════════════

        /// <summary>
        /// **孤零零一个脏名卷**（<c>444.pa8rt1.rar</c>，同目录里配不出整组）⇒ **什么都不做**：
        /// 计划阶段就停（<see cref="StatusText.VolumeRepairNoSiblings"/>），盘上名字一个都不改
        /// ⇒ **真引擎那条路根本不会被走到**（盘上不存在任何"标准名"的入口可开）。
        ///
        /// <para>样本仍是**真 RAR 分卷**：造一组四卷，只把其中一卷（改名成脏名）单独放进一个目录。</para>
        ///
        /// <para>⚠ 同一个用例末尾**如实钉住**反过来的那一格（7z 数字族的卷号段骨架档没有这道闸门），
        /// 见那一段注释。</para>
        /// </summary>
        [RarVolumeFact]
        public void 反面_孤零零一个脏名卷_计划阶段就停_没有任何标准名入口可开()
        {
            RequireSevenZip();
            RequireRar();

            string holding = NewDirectory("lone-holding");
            string payloadPath = CreatePayload("stage-lone");
            CreateRarVolumes(holding, payloadPath);

            string directory = NewDirectory("lone-rar");
            string lone = Path.Combine(directory, "444.pa8rt1.rar");

            // 只搬来一卷（别的卷留在 holding 里 ⇒ 这一层配不出整组），并把它改成脏名。
            File.Move(Path.Combine(holding, "444.part1.rar"), lone);

            string loneHash = Sha256Of(lone);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(lone, NamesIn(directory));

            Assert.False(plan.CanRepair, $"配不出整组 ⇒ 一个字都不许改，实际：{plan.Describe()}");
            Assert.Equal(StatusText.VolumeRepairNoSiblings, plan.Reason);

            // 计划阶段就停：⛔ 没有执行体、盘上没有任何标准名可开（真引擎那条路压根不会被走到）。
            Assert.Empty(plan.Items);
            Assert.True(File.Exists(lone), "原样原地不动");
            Assert.False(File.Exists(Path.Combine(directory, "444.part1.rar")), "不许造出一个标准名入口");
            Assert.Equal(loneHash, Sha256Of(lone));

            /*
             * ⚠ **顺带钉住另一格：7z 数字族的"卷标记段脏"也要过同一道闸门**（用户 2026-10-04 第六轮拍板）。
             *
             * 第五轮这里写的是「孤零零一个 `set.7z.0a0b1` **也会被改名**」（当时闸门只长在 partN 那一档上，
             * 如实钉住的现状）。第六轮把闸门扩到**全部"猜出来的"名字**（`VolumeNameRepair.IsGuessedVolumeName`
             * 转调 `ExtensionHelper` 的三个既有出口）⇒ 孤立一个的改名计划**不成立**
             * （`VolumeRepairNoSiblings`）—— 断言按新结论改写，⛔ 不是放宽。
             *
             * 样本用**真 7z 卷**（内容与名字同族），只搬来第 1 卷并把它改成脏名 —— 同目录照样配不出整组。
             */
            string sevenZipStage = NewDirectory("lone-7z-stage");
            CreateSevenZipVolumes(sevenZipStage, CreatePayload("stage-lone-7z"));

            string loneSevenZipDirectory = NewDirectory("lone-7z-numeric-skeleton");
            string loneSevenZip = Path.Combine(loneSevenZipDirectory, "set.7z.0a0b1");

            File.Copy(Path.Combine(sevenZipStage, "set.7z.001"), loneSevenZip);

            string loneSevenZipHash = Sha256Of(loneSevenZip);

            VolumeNameRepairPlan loneSevenZipPlan = VolumeNameRepair.Plan(loneSevenZip, NamesIn(loneSevenZipDirectory));

            Assert.False(
                loneSevenZipPlan.CanRepair,
                $"孤立一个「卷标记段靠骨架档认出来」的脏名 ⇒ 一个字都不许改，实际：{loneSevenZipPlan.Describe()}");
            Assert.Equal(StatusText.VolumeRepairNoSiblings, loneSevenZipPlan.Reason);
            Assert.Empty(loneSevenZipPlan.Items);
            Assert.True(File.Exists(loneSevenZip), "原样原地不动");
            Assert.False(File.Exists(Path.Combine(loneSevenZipDirectory, "set.7z.001")), "不许造出一个标准名入口");
            Assert.Equal(loneSevenZipHash, Sha256Of(loneSevenZip));
        }

        // ════════════ ④ 「只脏归档后缀段」接进计划之后（第五轮那条"钉住现状"按新结论改写） ════════════

        /// <summary>
        /// **只脏归档后缀段**（<c>set.7aaaaz.002</c>：后缀段靠骨架档读成 <c>7z</c>、卷号段是干净的 <c>002</c>）
        /// —— 用户 2026-10-04 第六轮把这一形状接进了改名计划。
        ///
        /// <para>⚠ <b>本条是第五轮那条「钉住现状：改名计划不成立」按新结论改写的</b>（⛔ 不是放宽断言）：
        /// 当时的实测是"识别层认得出、计划层给不出名字 ⇒ 真 7-Zip 打不开这一组；手工把那一卷改回规范名之后
        /// 立刻解得开 ⇒ 缺口只在计划层"。第六轮在 <c>FileNameHelper.TryResolveDisguisedVolume</c> 加了
        /// 形状③（**脏归档后缀段**，判据仍只转调既有的归档体还原那把尺子）⇒ 缺口补上了：
        /// 两条入口（干净首卷 / 脏的那一卷）给出**同一份规范名**，改名之后真 7-Zip 解得开、字节逐字节相同。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 结论已变_只脏归档后缀段的两条入口给出同一份规范名_真7zip按新名字解得开()
        {
            RequireSevenZip();

            string directory = NewDirectory("7z-suffix-segment-only");
            string payloadPath = CreatePayload("stage-7z-suffix-segment-only");
            byte[] payload = File.ReadAllBytes(payloadPath);

            CreateSevenZipVolumes(directory, payloadPath);

            string first = Path.Combine(directory, "set.7z.001");
            string dirty = Path.Combine(directory, "set.7aaaaz.002");

            File.Move(Path.Combine(directory, "set.7z.002"), dirty);

            Dictionary<string, string> before = SnapshotHashes(directory);

            // ── 两条入口现在**都**给出计划，而且是逐字同一份规范名（第五轮这两条都是"不改"） ──
            VolumeNameRepairPlan fromFirst = VolumeNameRepair.Plan(first, NamesIn(directory));
            VolumeNameRepairPlan fromDirty = VolumeNameRepair.Plan(dirty, NamesIn(directory));

            Assert.True(fromFirst.CanRepair, $"从干净首卷进去必须出得来计划，实际：{fromFirst.Describe()}");
            Assert.True(fromDirty.CanRepair, $"从脏的那一卷进去也必须出得来计划，实际：{fromDirty.Describe()}");

            Assert.Equal("set.7z.002", fromFirst.SuggestedFileName);
            Assert.Equal("set.7z.002", fromDirty.SuggestedFileName);

            VolumeRepairItem fromFirstItem = Assert.Single(fromFirst.Items);
            VolumeRepairItem fromDirtyItem = Assert.Single(fromDirty.Items);

            Assert.Equal("set.7aaaaz.002", fromFirstItem.CurrentFileName);
            Assert.Equal("set.7aaaaz.002", fromDirtyItem.CurrentFileName);
            Assert.Equal("set.7z.002", fromFirstItem.SuggestedFileName);
            Assert.Equal("set.7z.002", fromDirtyItem.SuggestedFileName);

            // 卷序 / 首卷名这两条照旧认得出来（归组那一档）。
            Assert.Equal(2, VolumeGroupDetector.TryGetVolumeIndex("set.7aaaaz.002"));
            Assert.Equal("set.7z.001", VolumeGroupDetector.TryGetFirstVolumeName("set.7aaaaz.002"));

            // 出计划这一步**一个字节都不碰盘**。
            AssertHashesUnchanged(directory, before);

            // ── ② 真改名 ──
            VolumeNameRepairResult applied = VolumeNameRepair.TryApply(fromFirst);

            Assert.True(applied.Success, applied.Message);
            Assert.True(File.Exists(Path.Combine(directory, "set.7z.002")));
            Assert.False(File.Exists(dirty));

            // ── ⑤ 内容一个字节没变（名字变了、内容不动；同组其余卷也不动） ──
            Assert.Equal(before["set.7aaaaz.002"], Sha256Of(Path.Combine(directory, "set.7z.002")));
            Assert.Equal(
                before.Values.OrderBy(hash => hash, StringComparer.Ordinal),
                Directory.GetFiles(directory).Select(Sha256Of).OrderBy(hash => hash, StringComparer.Ordinal));

            // ── ③④ 真 7-Zip 打开改过名的首卷、解出来的字节与原始负载逐字节相同 ──
            string outputDirectory = Path.Combine(_root, "out-suffix-segment-only");

            (ArchiveListResult list, ArchiveOperationResult _) = await OpenWithRealEngineAsync(
                first,
                outputDirectory,
                "只脏归档后缀段（第六轮接进计划之后）");

            Assert.Equal(EngineIds.SevenZip, list.EngineId);
            AssertPayloadRoundTripped(outputDirectory, payload, "只脏归档后缀段（第六轮接进计划之后）");
        }

        // ════════════════════════════ 基础设施 ════════════════════════════

        /// <summary>
        /// **真引擎那条路**：生产同一条 <see cref="EngineRegistry.CreateDefault"/> + <see cref="EngineRouter"/>
        /// （按魔数选引擎、失败才回退），⛔ 本文件一行 7z / UnRAR 参数都不拼。
        /// </summary>
        private async Task<(ArchiveListResult List, ArchiveOperationResult Extract)> OpenWithRealEngineAsync(
            string firstVolume,
            string outputDirectory,
            string what)
        {
            var router = new EngineRouter(EngineRegistry.CreateDefault());

            ArchiveListResult list = await router.ListAsync(ArchiveRequest.For(firstVolume), CancellationToken.None);

            Record("引擎原话·列出(" + what + ")", list.Message + Environment.NewLine +
                   "引擎=" + list.EngineId + " " + list.EngineVersion + "；条目=" +
                   string.Join("、", list.Entries.Select(entry => entry.Path)));

            Assert.True(list.Success, $"真引擎按新名字列不出这一组（{what}）：{list.Message}");
            Assert.Contains(
                list.Entries,
                entry => string.Equals(Path.GetFileName(entry.Path), PayloadName, StringComparison.OrdinalIgnoreCase));

            ArchiveOperationResult extract = await router.ExtractAsync(
                new ArchiveRequest { ArchivePath = firstVolume, OutputPath = outputDirectory, Password = string.Empty },
                new ExtractOptions(),
                CancellationToken.None);

            Record("引擎原话·解出(" + what + ")", extract.CombinedOutput);

            Assert.True(
                extract.Success,
                $"真引擎按新名字解不开这一组（{what}）：{extract.Status}｜{extract.Message}｜{extract.CombinedOutput}");

            return (list, extract);
        }

        /// <summary>④ 解出来的字节与原始负载**逐字节相同**（不是"存在就算过"）。</summary>
        private void AssertPayloadRoundTripped(string outputDirectory, byte[] payload, string what)
        {
            string[] extracted = Directory.GetFiles(outputDirectory, PayloadName, SearchOption.AllDirectories);

            Assert.True(
                extracted.Length == 1,
                $"应当恰好解出一份 {PayloadName}（{what}），实际 {extracted.Length} 份：{string.Join("、", extracted)}");

            byte[] actual = File.ReadAllBytes(extracted[0]);

            Assert.True(
                payload.AsSpan().SequenceEqual(actual),
                $"解出来的字节与原始负载不一致（{what}）：长度 {actual.Length} ≠ {payload.Length}，" +
                $"SHA256 {Sha256OfBytes(actual)} ≠ {Sha256OfBytes(payload)}");
        }

        /// <summary>
        /// 造一个 3 MiB 的**不可压**载荷（随机字节 ⇒ <c>-v1m</c> 切得出四卷；全零会被压成一卷）。
        ///
        /// <para>⚠ 载荷放在**分卷目录之外**的 stage 目录里（造样本时的 cwd 就是它）：
        /// 分卷目录里必须只剩分卷，否则"这一组自不自洽"的判据会多看到一个不相干的大文件。</para>
        /// </summary>
        private string CreatePayload(string stageName)
        {
            string stage = Path.Combine(_root, stageName);
            Directory.CreateDirectory(stage);

            string payload = Path.Combine(stage, PayloadName);
            var bytes = new byte[PayloadBytes];
            new Random(20261004).NextBytes(bytes);
            File.WriteAllBytes(payload, bytes);

            return payload;
        }

        /// <summary>内置 <c>7z.exe</c> 造一组真 7z 分卷（<c>-mx0 -v1m</c> ⇒ 4 卷）。</summary>
        private void CreateSevenZipVolumes(string directory, string payloadPath)
        {
            RunTool(
                _sevenZip,
                new[] { "a", "-t7z", "-mx0", "-v1m", Path.Combine(directory, "set.7z"), PayloadName },
                Path.GetDirectoryName(payloadPath)!);

            string[] volumes = Directory.GetFiles(directory, "set.7z.*");

            Assert.True(volumes.Length >= 3, $"至少要造出三卷，实际 {volumes.Length} 卷");
            Assert.True(File.Exists(Path.Combine(directory, "set.7z.001")), "第一卷没造出来");
            Assert.True(File.Exists(Path.Combine(directory, "set.7z.002")), "第二卷没造出来");
        }

        /// <summary>本机 <c>Rar.exe</c> 造一组真 RAR5 分卷（<c>-ma5 -m0 -v1m</c> ⇒ <c>444.partN.rar</c>）。</summary>
        private void CreateRarVolumes(string directory, string payloadPath)
        {
            RunTool(
                _rar,
                new[] { "a", "-ma5", "-m0", "-v1m", "-idq", Path.Combine(directory, "444.rar"), PayloadName },
                Path.GetDirectoryName(payloadPath)!);

            string[] volumes = Directory.GetFiles(directory, "444.part*.rar");

            Assert.True(volumes.Length >= 3, $"至少要造出三卷，实际 {volumes.Length} 卷：{string.Join("、", volumes.Select(Path.GetFileName))}");
            Assert.True(File.Exists(Path.Combine(directory, "444.part1.rar")), "第一卷没造出来");
            Assert.True(File.Exists(Path.Combine(directory, "444.part2.rar")), "第二卷没造出来");
        }

        /// <summary>造样本用的外部进程（⛔ 只用来**造样本**；被测那一步一律走 <see cref="EngineRouter"/>）。</summary>
        private void RunTool(string executable, IReadOnlyList<string> args, string workingDirectory)
        {
            var psi = new ProcessStartInfo(executable)
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

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException($"无法启动 {Path.GetFileName(executable)}");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(120_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 已经退了就无所谓。
                }

                throw new InvalidOperationException($"造样本超时：{Path.GetFileName(executable)}");
            }

            Assert.True(
                process.ExitCode == 0,
                $"造样本失败（exit {process.ExitCode}）：{Path.GetFileName(executable)} {string.Join(' ', psi.ArgumentList)}\n{stdout}\n{stderr}");
        }

        private string NewDirectory(string name)
        {
            string path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        private static string[] NamesIn(string directory) =>
            Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).ToArray()!;

        /// <summary>目录快照：文件名 → SHA256（断言 ⑤ 与"计划阶段不碰盘"的判据）。</summary>
        private static Dictionary<string, string> SnapshotHashes(string directory)
        {
            var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in Directory.GetFiles(directory))
            {
                snapshot[Path.GetFileName(path)] = Sha256Of(path);
            }

            return snapshot;
        }

        /// <summary>
        /// "这一层一个字节都没变"：按文件名排序后逐条比 `名字=哈希`。
        /// ⚠ 不用 <c>Assert.Equal(字典, 字典)</c>：那走的是集合比较、**顺序敏感**，
        /// 而 <c>Directory.GetFiles</c> 的返回顺序在改名之后可能变 ⇒ 会假红。
        /// </summary>
        private static void AssertHashesUnchanged(string directory, Dictionary<string, string> before)
        {
            Assert.Equal(
                Describe(before),
                Describe(SnapshotHashes(directory)));

            static string[] Describe(Dictionary<string, string> snapshot) =>
                snapshot
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => pair.Key + "=" + pair.Value)
                    .ToArray();
        }

        private static string Sha256Of(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        private static string Sha256OfBytes(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes));

        /// <summary>目录名里不能出现非法字符（脏名里有「删除」这类，也要能当目录名）。</summary>
        private static string MakeSafeName(string name)
        {
            var builder = new StringBuilder(name.Length);

            foreach (char ch in name)
            {
                builder.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '_');
            }

            return builder.ToString();
        }

        /// <summary>把引擎原话留一份（控制台 + 设了 <c>ARCHIVEFIXER_E2E_EVIDENCE_DIR</c> 时另存一份）。</summary>
        private void Record(string label, string? text)
        {
            string body = (text ?? string.Empty).Trim().Replace("\r\n", "\n", StringComparison.Ordinal);

            _output.WriteLine("[" + label + "] " + body);

            if (_evidenceDirectory.Length == 0)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(_evidenceDirectory);
                File.AppendAllText(
                    Path.Combine(_evidenceDirectory, "volume-rename-real-engine.txt"),
                    "[" + label + "]" + Environment.NewLine + body + Environment.NewLine + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
                // 证据写不出去不影响结论。
            }
        }

        private void RequireSevenZip()
        {
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException("找不到内置 7z.exe，且 [SevenZipFact] 没有把它跳过：环境与特性探测结果不一致。");
            }
        }

        private void RequireRar()
        {
            if (string.IsNullOrEmpty(_rar))
            {
                throw new InvalidOperationException("找不到 Rar.exe，且 [RarVolumeFact] 没有把它跳过：环境与特性探测结果不一致。");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **通用骨架命中**（用户 2026-10-04 拍板的第三档相似算法）：已知骨架的字符序列在该段里
    /// **按顺序**出现就算命中，中间夹的任何字符都当杂质剔除。
    ///
    /// <para>用户原话：「我想要的是**你能够识别到伪装的后缀里面有 `7_______z.003` 的内容**，
    /// 不是仅仅让你改这么简单的一个档」「出现其他的问题你也要弄啊，比如 `333.78a8fuaz.003`，
    /// 这种情况下难道你就瘫痪了吗」。</para>
    ///
    /// <para>三条硬约束（一条都不许松）：① **唯一**（一段命中多个骨架 ⇒ 判不出 ⇒ 什么都不做）；
    /// ② 放宽只作用在**已经有外部证据**的那条路（见 <c>放宽的代价</c> 那条用例把闸门链列清楚）；
    /// ③ 判不出 ⇒ 什么都不做。另外两条纪律：骨架必须**首尾对齐**（杂质只能夹在**中间** ——
    /// 用户口径就是"**中间**夹的任何字符都当杂质剔除"）；卷标记段那一档还要**取最右**
    /// （<c>pa8rt1</c> ⇒ <c>part1</c> 只对"末尾那段真的脏"生效，左边剩下来的段照旧不吃骨架档）。</para>
    /// </summary>
    public class VolumeSkeletonMatchTests : IDisposable
    {
        private readonly string _root;

        public VolumeSkeletonMatchTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSkeleton", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论。
            }
        }

        // ================================================================ 归档后缀段：正例

        [Theory]
        [InlineData("7_______z", "7z")]      // 用户原话那一格
        [InlineData("78a8fuaz", "7z")]       // 用户原话 `333.78a8fuaz.003`
        [InlineData("7aaaaz", "7z")]         // 上一轮口径里的形状（骨架档天然覆盖）
        [InlineData("ra31415926535r", "rar")] // ⚠ 放宽的代价：这条**会**被认成 rar（用户口径）
        [InlineData("ra3r", "rar")]
        [InlineData("zi删除p", "zip")]        // 旧两档就能认（照旧）
        [InlineData("zscip", "zip")]          // 旧两档就能认（照旧）
        public void 归档后缀段_按序命中已知骨架(string segment, string expected)
        {
            Assert.True(
                ExtensionHelper.TryRecoverDisguisedArchiveBody(segment, out string canonical, out _),
                $"`{segment}` 应当命中 `{expected}`");
            Assert.Equal(expected, canonical);
        }

        // ================================================================ 反例（必须一律挡住）

        [Theory]
        [InlineData("7zip")]        // 同时命中 7z 与 zip ⇒ 判不出
        [InlineData("0012")]        // 纯数字尾 ⇒ 不猜
        [InlineData("rarity")]      // `rar` 只到第 3 个字符 ⇒ 骨架没走到段末
        [InlineData("zipper")]      // `zip` 同上
        [InlineData("typo")]        // 什么都命中不了
        [InlineData("bak")]         // `x.001.bak` 的尾巴：另起一段的后缀归还原工序，卷匹配器照旧不认
        [InlineData("txt")]         // `x.7z.001.txt` 的尾巴：同上
        public void 归档后缀段_歧义与骨架没对齐一律不认(string segment)
        {
            Assert.False(
                ExtensionHelper.TryRecoverDisguisedArchiveBody(segment, out _, out _),
                $"`{segment}` 不许被认成任何已知归档后缀");
        }

        [Theory]
        [InlineData("0012")]        // 纯数字尾（尾没对齐）
        [InlineData("z0112")]       // zNN 骨架 + 纯数字尾
        [InlineData("z0a1b2")]      // 数字骨架尾没对齐、zNN 骨架也尾没对齐 ⇒ 一个都不算
        [InlineData("00c1x9")]      // 既有用例钉着的形状：骨架没走到段末 ⇒ 照旧不认
        [InlineData("pa8rt1")]      // partN 骨架**故意不放开**，见下一条用例
        public void 卷标记段_歧义与骨架没对齐一律不认(string segment)
        {
            Assert.False(
                ExtensionHelper.TrySplitVolumeSegmentTolerant(segment, out _, out _),
                $"`{segment}` 不许被认成任何卷标记");
        }

        /// <summary>
        /// ⛔ **partN 骨架只在"取最右"那一格放开**（用户 2026-10-04 第二轮拍板）：`pa8rt1` ⇒ `part1`
        /// 只对 <c>&lt;基名&gt;.&lt;卷标记&gt;.rar</c>（尾巴逐字 <c>rar</c>）生效；**裸的末尾段不吃**
        /// （<c>TrySplitVolumeSegmentTolerant</c> 默认档 = 老口径）。
        ///
        /// <para>为什么必须这么切：`pa8rt1`（该认成 `part1`）与 `p1art2`（AAA 真夹具
        /// `444.p1art2.part2.rar` 的**基名段**，⛔ 不许认成卷标记）是**同一个形状** ——
        /// 字母序列都是 `part`、都夹一个数字，任何按形状的判据都分不开。
        /// 裸的末尾段一旦也吃骨架档，`444.p1art2.part2.rar` 剥完 `part2.rar` 之后剩下的
        /// `444.p1art2` 会被**再剥一次** ⇒ 基名从 `444.p1art2` 变成 `444`
        /// （RAR 族"基名 = partN 段之前的所有点段"那条不变量当场破）。
        /// 红检：把这一格改回"也吃骨架档" ⇒ 本条与
        /// <see cref="卷标记段_取最右_末尾干净时左边那段不许再吃骨架档"/> 一起变红。</para>
        /// </summary>
        [Fact]
        public void 卷标记段_partN骨架只在最右那一格_保住RAR基名规则()
        {
            Assert.False(ExtensionHelper.TrySplitVolumeSegmentTolerant("pa8rt1", out _, out _));
            Assert.False(ExtensionHelper.TrySplitVolumeSegmentTolerant("p1art2", out _, out _));

            // 基名规则本身（这才是"只在最右那一格放开"要保住的东西）。
            Assert.Equal("444.p1art2", FileNameHelper.StripVolumeMarkers("444.p1art2.part2.rar"));
            Assert.Equal("444.p1art2", OutputPlacement.ResolveArchiveBaseName("444.p1art2.part2.rar"));
        }

        /// <summary>卷标记段的正例：数字骨架可以夹杂质（`0a0b1` ⇒ `001`）、`zNN` 同理（都要**首尾对齐**）。</summary>
        [Theory]
        [InlineData("0a0b1", "001", 1)]
        [InlineData("00aaa1", "001", 1)]
        [InlineData("z0a1", "z01", 2)]
        [InlineData("r0a1", "r01", 3)]
        public void 卷标记段_按序命中已知骨架(string segment, string expectedMark, int expectedIndex)
        {
            Assert.True(
                ExtensionHelper.TrySplitVolumeSegmentTolerant(segment, out string mark, out _),
                $"`{segment}` 应当命中 `{expectedMark}`");
            Assert.Equal(expectedMark, mark);

            // 卷序也要跟着对（`x.<段>` 这一整串名字走一遍真判据）。
            Assert.Equal(expectedIndex, VolumeGroupDetector.TryGetVolumeIndex("x." + segment));
        }

        // ================================================================ 「脏归档后缀段」那一档（2026-10-04 第六轮）

        /// <summary>
        /// **只脏归档后缀段**（末段逐字就是合法卷标记，脏的是它前面那一段）：
        /// 判据 <see cref="ExtensionHelper.IsArchiveSegmentByDisguise"/> 读出那一段的真实身份
        /// ⇒ 名字级拆解给出 <c>&lt;基名&gt;.7z.&lt;卷标记&gt;</c>（**替换不追加**、基名一个字不动）。
        ///
        /// <para>用户 2026-10-04 第六轮原话：「把这一形状接进改名计划（<c>VolumeNameRepair</c> 那条路），
        /// 判据照既有口径、⛔ 不许新造第三把尺子」。</para>
        /// </summary>
        [Theory]
        [InlineData("set.7aaaaz.002", "set.7z", "002")]
        [InlineData("set.7_______z.002", "set.7z", "002")]
        [InlineData("333.78a8fuaz.003", "333.7z", "003")]
        [InlineData("a.b.c.7sz.005", "a.b.c.7z", "005")]
        public void 归档后缀段_脏的那一段要归一成真实身份(string fileName, string expectedBase, string expectedSegment)
        {
            Assert.True(
                ExtensionHelper.IsArchiveSegmentByDisguise(fileName, out string recovered),
                $"`{fileName}` 的归档后缀段应当靠容错/骨架档读得出来");
            Assert.Equal("7z", recovered);

            Assert.True(
                FileNameHelper.TryResolveVolumeBaseName(
                    fileName,
                    VolumeBaseNameLevel.DisguisedVolume,
                    out string baseName,
                    out string segment),
                $"`{fileName}` 应当拆得出来");
            Assert.Equal(expectedBase, baseName);
            Assert.Equal(expectedSegment, segment);

            // 归组那一档照旧（这一格本来就对，第六轮只把**计划**那一档补齐）。
            Assert.Equal("333.7z.001", VolumeGroupDetector.TryGetFirstVolumeName("333.78a8fuaz.003"));
        }

        /// <summary>
        /// **反例**（用户 2026-10-04 第六轮点名"`x.rarity` / `x.zipper` 连命中都不算"）：
        /// 半个骨架（没走到段末）或有歧义的段（`7zip` 同时命中 `7z` 与 `zip`）**都不算**"脏归档后缀段"，
        /// 而**本来就干净**的（`set.7z.002`）也不算。
        /// </summary>
        [Theory]
        [InlineData("set.rarity.002")]
        [InlineData("set.zipper.002")]
        [InlineData("set.7zip.002")]
        [InlineData("set.7z.002")]        // 本来就干净 ⇒ 不是"猜出来的"
        [InlineData("set.002")]           // 没有后缀段这一格
        public void 归档后缀段_仿名与干净名都不算猜出来的(string fileName)
        {
            Assert.False(
                ExtensionHelper.IsArchiveSegmentByDisguise(fileName, out _),
                $"`{fileName}` 不该被当成「脏归档后缀段」");
        }

        /// <summary>
        /// **卷标记段要"重建"**那一档的谓词（<see cref="ExtensionHelper.IsVolumeMarkByDisguise"/>）：
        /// `0a0b1` / `z0删1` / `pa8rt1` 都算"要重建"，逐字干净的一律不算。
        ///
        /// <para>用户 2026-10-04 第六轮：「孤立一个 <c>set.7z.0a0b1</c> ⇒ 不改」——
        /// 闸门就是按这一位开的（闸门本体在 <c>VolumeNameRepair.IsGuessedVolumeName</c>）。</para>
        ///
        /// <para>⛔ **边界（与 partN 同口径）**：「**逐字标记 + 粘着的垃圾**」（<c>001删除</c> / <c>001(1)</c>）
        /// **不算要重建** —— 那一段开头就是一个逐字合法的卷标记、改名只删尾巴、卷号一个字符都不动，
        /// 是 2026-09-28 起的老口径（网盘缀「删除」），照旧只按名字改。⚠ 实测：把它也收进闸门 ⇒
        /// <c>VolumeNameRepairGroupTests</c> / <c>RealMachine20261002FixesTests</c> /
        /// <c>RealMachineDefectFixesTests</c> 3 条既有用例变红（夹具是"尺寸不规律 / 散在两个目录"）。</para>
        /// </summary>
        [Theory]
        [InlineData("set.7z.0a0b1", "001", true)]
        [InlineData("222.z0删1", "z01", true)]      // 垃圾夹在卷标记**里面** ⇒ 要重建
        [InlineData("444.pa8rt1.rar", "part1", true)]
        [InlineData("set.7z.001", null, false)]
        [InlineData("x.7z.001.txt", null, false)]   // 脏的是"另起一段的尾巴"，不是卷标记这一格
        [InlineData("set.7aaaaz.002", null, false)] // 脏的是归档后缀段那一格（由另一位回答）
        [InlineData("giu910.7z.001删除", null, false)] // 逐字标记 + 尾巴垃圾 ⇒ 老口径，不过闸门
        [InlineData("x.7z.001(1)", null, false)]
        public void 卷标记段_要重建的与逐字干净的要分得开(string fileName, string? expectedMark, bool expected)
        {
            Assert.Equal(expected, ExtensionHelper.IsVolumeMarkByDisguise(fileName, out string mark));

            if (expected)
            {
                Assert.Equal(expectedMark, mark);
            }
        }

        // ================================================================ 用户原话形状

        /// <summary>
        /// 用户原话形状 `333.78a8fuaz.003`：**看出这是 7z 分卷、`.003` 是卷号**。
        ///
        /// <para>判据落在"归组那一档的基名"上：<c>VolumeGroupDetector.Analyze</c> 认末段是卷标记
        /// ⇒ 基名走唯一出口的归组键档 ⇒ 后缀段 `78a8fuaz` 经骨架档归一成 `7z` ⇒ 基名 `333.7z`
        /// ⇒ 缺的那一卷如实报成 `333.7z.002`。</para>
        /// </summary>
        [Theory]
        [InlineData("333.78a8fuaz.003", 3)]
        [InlineData("333.7aaaaz.001", 1)]
        [InlineData("333.7_______z.001", 1)]
        public void 用户原话形状_伪装后缀里的7z分卷要认得出来(string fileName, int expectedIndex)
        {
            Assert.Equal(expectedIndex, VolumeGroupDetector.TryGetVolumeIndex(fileName));

            // 「第 1 卷该叫什么」也必须是 7z 那一套（`333.7z.001`），不是原样把伪装名抄回去。
            Assert.Equal("333.7z.001", VolumeGroupDetector.TryGetFirstVolumeName(fileName));

            IReadOnlyList<VolumeGroup> groups = VolumeGroupDetector.Group(new[]
            {
                new VolumeCandidate { Path = @"C:\t\" + fileName, Size = 1024 }
            });

            VolumeGroup group = Assert.Single(groups);
            Assert.Equal("333.7z", group.BaseName);
            Assert.Equal(expectedIndex, Assert.Single(group.Volumes.Select(v => VolumeGroupDetector.TryGetVolumeIndex(Path.GetFileName(v.Path)))));
        }

        /// <summary>`.003` 那一格顺带把"缺哪一卷"钉住（缺的是 `333.7z.002`，不是伪装名那一套）。</summary>
        [Fact]
        public void 用户原话形状_缺卷提示也按规范名说()
        {
            IReadOnlyList<VolumeGroup> groups = VolumeGroupDetector.Group(new[]
            {
                new VolumeCandidate { Path = @"C:\t\333.78a8fuaz.003", Size = 1024 }
            });

            VolumeGroup group = Assert.Single(groups);

            Assert.Equal(new[] { "333.7z.001", "333.7z.002" }, group.MissingVolumeNames.ToArray());
            Assert.False(group.IsComplete);
        }

        // ================================================================ 放宽的代价（量出来的事实）

        /// <summary>
        /// **量一条事实**：像 `x.rarity` / `x.zipper` 这种"恰好含 `rar` / `zip` 三个字母的普通文件名"，
        /// 在当前判据与**外部证据闸门链**下**会不会真的被改名**。
        ///
        /// <para>闸门链（逐条在下面钉住）：</para>
        /// <list type="number">
        /// <item><description><b>它们连"命中"都不算</b>：骨架档要求**首尾对齐**（杂质只能夹在**中间**），
        /// 而 `rarity` 的骨架 `rar` 只走到第 3 个字符、`zipper` 的 `zip` 同理 ⇒ 一律不算命中
        /// ⇒ ③b 那一档压根不进，它们就是两个普通文件。</description></item>
        /// <item><description><b>对照组 `x.ra31415926535r` 是真命中</b>（`r`…`a`…`r` 首尾对齐）——
        /// 这一格才是"放宽的代价"该记的那一格：它会被认成"本体后缀被塞了杂质的 RAR 本体"。</description></item>
        /// <item><description><b>真命中那一格也改不了名</b>：旁边有一卷同族的续卷时它会成组，但
        /// <c>VolumeNameRepair.Plan</c> 那条"建议名与每个兄弟卷的基名逐字相等"的闸门判否
        /// （建议名是 `x.rar`，兄弟卷的剥标记结果是 `x`）⇒ <c>CanRepair = false</c>。</description></item>
        /// </list>
        ///
        /// <para>⇒ 结论：**两类都不会被改名**；`ra31415926535r` 那一格会多出一行分卷组。
        /// 这是这条放宽的代价，如实钉在这里（⛔ 不是"猜的"，每一条都由下面的断言量出来）。</para>
        /// </summary>
        [Theory]
        [InlineData("x.rarity", "x.r00", false)]
        [InlineData("x.zipper", "x.z01", false)]
        [InlineData("x.ra31415926535r", "x.r00", true)]
        public void 放宽的代价_含rar或zip字母的普通文件名(string plain, string continuation, bool recognizedAsBody)
        {
            string directory = Path.Combine(_root, plain.Replace('.', '_'));
            Directory.CreateDirectory(directory);

            string segment = plain[(plain.IndexOf('.') + 1)..];
            string plainPath = WriteFile(directory, plain, 1024, seed: 1);
            string continuationPath = WriteFile(directory, continuation, 1024, seed: 2);

            // ① 后缀档的结论（`rarity` / `zipper` 不算命中；`ra31415926535r` 算）。
            Assert.Equal(
                recognizedAsBody,
                ExtensionHelper.TryRecoverDisguisedArchiveBody(segment, out _, out _));

            // ② 孤立一个都成不了组（`rarity`/`zipper` 不是分卷成员；`ra31415926535r` 是"脏本体名"，
            //    而尺寸规律要求桶里至少还有一卷 ⇒ 一起被丢掉）。
            Assert.Empty(VolumeGroupDetector.Group(new[] { new VolumeCandidate { Path = plainPath, Size = 1024 } }));

            // ③ 真命中那一格：旁边有一卷同族续卷时成组（这是"同组形状自洽"那条外部证据）。
            IReadOnlyList<VolumeGroup> groups = VolumeGroupDetector.Group(new[]
            {
                new VolumeCandidate { Path = plainPath, Size = 1024 },
                new VolumeCandidate { Path = continuationPath, Size = 1024 }
            });

            VolumeGroup group = Assert.Single(groups);
            Assert.Equal(recognizedAsBody ? 2 : 1, group.KnownVolumeCount);

            // ④ 但**改不了名**：最后那道"建议名与每个兄弟卷的基名逐字相等"的闸门判否。
            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(plainPath, new[] { plain, continuation });

            Assert.False(
                plan.CanRepair,
                $"`{plain}` 不该被改名（放宽的代价只到「多一行分卷组」为止），实际计划：{plan.Describe()}");

            /*
             * 拒绝的那一句话也钉住（两格卡在不同的闸门上）：
             *   · 不算命中（`rarity` / `zipper`）⇒ 它连"名字里有卷号"都不成立 ⇒ 更早的那道门就拦下了；
             *   · 真命中（`ra31415926535r`）⇒ 走到"从后续卷的名字推不出这一组的标准名"那道门。
             */
            Assert.Equal(
                recognizedAsBody ? StatusText.VolumeRepairNoSuggestion : StatusText.VolumeRepairNotAVolumeName,
                plan.Reason);

            // 盘上两个名字一个都没动。
            Assert.True(File.Exists(plainPath));
            Assert.True(File.Exists(continuationPath));
        }

        /// <summary>
        /// AAA 那一组的**合成复刻**（真夹具 `_tmp\ArchiveFixer\aaa-replay\AAA\444` 那几个文件的形状）：
        /// `444.p1art2.part1.rar` + `444.p1art2.part2.rar` 是 partN 族的一对；旁边那个 `444.p1art2.ra3r`
        /// （骨架档认得出它的后缀是 `rar`）**不许**跟它们并成一组，更不许改变那一对的基名。
        ///
        /// <para>为什么单独钉这一条：`444.p1art2.ra3r` 是探针表里唯一被上一轮放宽改掉一格的名字
        /// （首卷名从 `null` 变成 `444.p1art2.rar`，`ArchiveBaseNameTests` 第 64 行）——
        /// 要证明"改了那一格"不会顺带把真夹具那一组拆散。</para>
        /// </summary>
        [Fact]
        public void AAA形状_ra3r被认成本体后缀之后_不许跟partN那一对并组()
        {
            IReadOnlyList<VolumeGroup> groups = VolumeGroupDetector.Group(new[]
            {
                new VolumeCandidate { Path = @"C:\aaa\444.p1art2.part1.rar", Size = 100 },
                new VolumeCandidate { Path = @"C:\aaa\444.p1art2.part2.rar", Size = 100 },
                new VolumeCandidate { Path = @"C:\aaa\444.p1art2.ra3r", Size = 100 }
            });

            // 只有 partN 那一对成组：`444.p1art2.ra3r` 单独立桶（族标记不同），
            // 而且它自己一个"脏本体名"过不了尺寸规律（桶里没有第二个成员）⇒ 不成组。
            VolumeGroup group = Assert.Single(groups);
            Assert.Equal("444.p1art2", group.BaseName);
            Assert.Equal(2, group.KnownVolumeCount);
            Assert.DoesNotContain(
                group.Volumes,
                v => v.Path.EndsWith("ra3r", StringComparison.OrdinalIgnoreCase));
            Assert.EndsWith("part1.rar", group.FirstVolumePath, StringComparison.OrdinalIgnoreCase);

            // 基名规则一个字没动（RAR = partN 段**之前**的所有点段）。
            Assert.Equal("444.p1art2", FileNameHelper.StripVolumeMarkers("444.p1art2.part2.rar"));
            Assert.Equal("444.p1art2", OutputPlacement.ResolveArchiveBaseName("444.p1art2.part2.rar"));
        }

        // ================================================================ 卷标记段的 partN 骨架（"取最右"，2026-10-04 第二轮）

        /// <summary>
        /// ① **末尾那段真的脏**（用户点名的 `444.pa8rt1.rar`）：靠骨架命中认出卷标记 `part1`、基名 `444`。
        ///
        /// <para>判据只在**"取最右"那一格**（<c>&lt;基名&gt;.&lt;卷标记&gt;.rar</c>，尾巴逐字是 <c>rar</c>）
        /// 才吃 partN 骨架 —— 理由见下一条对照用例。</para>
        /// </summary>
        [Fact]
        public void 卷标记段_partN骨架_末尾段真脏时要认出来()
        {
            Assert.True(
                ExtensionHelper.TrySplitPartNumberedVolume(
                    "444.pa8rt1.rar",
                    out string baseName,
                    out string mark,
                    out int index,
                    out string tail,
                    out bool disguised),
                "`444.pa8rt1.rar` 的卷标记段应当靠骨架命中认出来");

            Assert.Equal("444", baseName);
            Assert.Equal("part1", mark);
            Assert.Equal(1, index);
            Assert.Equal("rar", tail);
            Assert.True(disguised, "名字不标准 ⇒ 不许被当成「名字标准、可以删」的那一档");

            // 整条链上的公开观测面：卷序 / 首卷名 / 剥标记 / 包基名。
            Assert.Equal(1, VolumeGroupDetector.TryGetVolumeIndex("444.pa8rt1.rar"));
            Assert.Equal("444.part1.rar", VolumeGroupDetector.TryGetFirstVolumeName("444.pa8rt1.rar"));
            Assert.Equal("444", FileNameHelper.StripVolumeMarkers("444.pa8rt1.rar"));
            Assert.Equal("444", OutputPlacement.ResolveArchiveBaseName("444.pa8rt1.rar"));
        }

        /// <summary>
        /// ② **对照（防回归的命根）**：`444.p1art2.part2.rar` 仍然取**最右**那个干净的 `part2`
        /// ⇒ 基名保持 `444.p1art2`（⛔ 左边剩下来的 `p1art2` 不许再吃骨架档）。
        ///
        /// <para>`pa8rt1`（该认成 `part1`）与 `p1art2`（⛔ 不许认成卷标记，AAA 真夹具那一组的基名段）
        /// 是**同一个形状** —— "取最右"就是唯一能把它们分开的那条规矩：
        /// 骨架档只作用在**最右**那个卷标记位置上，剥完之后左边剩下来的一律照老口径（不吃骨架）。</para>
        /// </summary>
        [Fact]
        public void 卷标记段_取最右_末尾干净时左边那段不许再吃骨架档()
        {
            Assert.Equal("444.p1art2", FileNameHelper.StripVolumeMarkers("444.p1art2.part2.rar"));
            Assert.Equal("444.p1art2", OutputPlacement.ResolveArchiveBaseName("444.p1art2.part2.rar"));
            Assert.Equal(2, VolumeGroupDetector.TryGetVolumeIndex("444.p1art2.part2.rar"));
            Assert.Equal("444.p1art2.part1.rar", VolumeGroupDetector.TryGetFirstVolumeName("444.p1art2.part2.rar"));

            IReadOnlyList<VolumeGroup> groups = VolumeGroupDetector.Group(new[]
            {
                new VolumeCandidate { Path = @"C:\t\444.p1art2.part2.rar", Size = 1024 }
            });

            Assert.Equal("444.p1art2", Assert.Single(groups).BaseName);

            // 落到代码里的那一格：**裸的末尾段**不吃 partN 骨架，"卷标记 + 逐字 rar"那一档才吃。
            Assert.False(ExtensionHelper.TrySplitVolumeSegmentTolerant("p1art2", out _, out _));
            Assert.True(ExtensionHelper.TrySplitVolumeSegmentTolerant(
                "pa8rt1",
                out string mark,
                out _,
                allowPartNumberedSkeleton: true));
            Assert.Equal("part1", mark);

            // 尾巴伪装的（`444.pa删rt2.r除ar`，AAA 第二卷那种）照旧走老两档，不受影响。
            Assert.True(ExtensionHelper.TrySplitPartNumberedVolume(
                "444.pa删rt2.r除ar", out string rarBase, out string rarMark, out int rarIndex, out _, out _));
            Assert.Equal("444", rarBase);
            Assert.Equal("part2", rarMark);
            Assert.Equal(2, rarIndex);
        }

        /// <summary>
        /// ③ 脏首卷名：用户给的字面名字 <c>0a0b1.7z.001</c> —— 末段是干净的 <c>001</c>、脏的是基名
        /// ⇒ 本来就是一个标准的第 1 卷（基名 `0a0b1.7z`）。
        /// </summary>
        [Fact]
        public void 卷标记段_脏首卷名_用户给的字面名字()
        {
            Assert.Equal(1, VolumeGroupDetector.TryGetVolumeIndex("0a0b1.7z.001"));
            Assert.Equal("0a0b1.7z.001", VolumeGroupDetector.TryGetFirstVolumeName("0a0b1.7z.001"));

            IReadOnlyList<VolumeGroup> groups = VolumeGroupDetector.Group(new[]
            {
                new VolumeCandidate { Path = @"C:\t\0a0b1.7z.001", Size = 1024 }
            });

            Assert.Equal("0a0b1.7z", Assert.Single(groups).BaseName);
        }

        /// <summary>
        /// ③b **脏在卷标记段**那一档（数字骨架）：<c>333.7z.0a0b1</c> ⇒ 卷标记 `001`、卷序 1、基名 `333.7z`。
        ///
        /// <para>⚠ 单独一个"名字被伪装过"的卷**成不了组**（尺寸规律要求桶里至少还有另一卷 ——
        /// 既有口径"认错比不认更糟"，本条用例不碰它）⇒ 这里给一组两卷才看得见组基名。</para>
        /// </summary>
        [Fact]
        public void 卷标记段_脏卷标记_要跟干净的第二卷归成一组()
        {
            Assert.Equal(1, VolumeGroupDetector.TryGetVolumeIndex("333.7z.0a0b1"));
            Assert.Equal("333.7z.001", VolumeGroupDetector.TryGetFirstVolumeName("333.7z.0a0b1"));

            IReadOnlyList<VolumeGroup> groups = VolumeGroupDetector.Group(new[]
            {
                new VolumeCandidate { Path = @"C:\t\333.7z.0a0b1", Size = 1024 },
                new VolumeCandidate { Path = @"C:\t\333.7z.002", Size = 1024 }
            });

            VolumeGroup group = Assert.Single(groups);
            Assert.Equal("333.7z", group.BaseName);
            Assert.Equal(2, group.KnownVolumeCount);
            Assert.True(group.IsComplete);
        }

        private static string WriteFile(string directory, string name, int size, int seed)
        {
            string path = Path.Combine(directory, name);
            var bytes = new byte[size];
            new Random(seed).NextBytes(bytes);
            File.WriteAllBytes(path, bytes);
            return path;
        }
    }
}

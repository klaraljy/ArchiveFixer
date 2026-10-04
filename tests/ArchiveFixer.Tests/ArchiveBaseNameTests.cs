using System;
using System.Collections.Generic;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **阶段 A：基名收成唯一出口之后的行为等价对照表**（用户 2026-10-03 批准落地）。
    ///
    /// <para>背景：基名原先散在 **5 处各写一份** ——
    /// <c>VolumeGroupDetector.JoinBaseName</c>、<c>FileNameHelper.StripVolumeMarkers</c>、
    /// <c>OutputPlacement.ResolveArchiveBaseName</c>、<c>VolumeNameRepair.TrySplitDisguised</c>、
    /// <c>VolumeNumberFromContent.TryDeriveStem</c>。收口这一轮**只搬家、不改结论**：
    /// 每一条期望值都是**改之前那 5 处旧实现的真实输出**（改之前/改之后各跑一次探针，
    /// 95 行逐字相同），这里把它钉成常驻回归网。</para>
    ///
    /// <para>⛔ 期望值一个都不许"顺手调准"：改了哪一格，就是改了既有结论，必须先拍板。</para>
    ///
    /// <para>⚠ <b>2026-10-04 改了一格</b>（`444.p1art2.ra3r` 的「首卷名」：<c>null</c> → <c>444.p1art2.rar</c>）：
    /// 用户当天拍板给"去杂质之后是什么"加了**第三档（通用骨架命中）** —— `ra3r` 里 `r`…`a`…`r` 按序出现
    /// ⇒ 命中 `rar` ⇒ 这个文件被 <c>VolumeGroupDetector.Analyze</c> 的 ③b 认成"本体后缀被塞了杂质的 RAR 本体"
    /// ⇒ 首卷名是 `444.p1art2.rar`。这正是用户点名的 `ra31415926535r` 那条口径的**代价**（同一条规则的短版本），
    /// 如实钉在这里；其余 52 行逐字未动（正反例见 <c>VolumeSkeletonMatchTests</c>）。</para>
    ///
    /// <para>三族的基名规则（方案 §1 ②）：
    /// RAR = <c>partN</c> 段**之前的所有点段**（<c>444.p1art2.part2.rar</c> → <c>444.p1art2</c>，
    /// ⛔ 不是 <c>.rar</c> 前面那段）；7z / ZIP = 锚点名剥掉**卷标记段 + 归档后缀段**
    /// （<c>111.7z.001</c> → <c>111</c>）。</para>
    /// </summary>
    public class ArchiveBaseNameTests
    {
        /// <summary>
        /// 一行 = 一个名字在**5 个消费点**上的真实输出。列的顺序与生产代码里那 5 处的顺序一致：
        /// <list type="number">
        /// <item><description><c>StripMarkers</c> 档 ← <c>FileNameHelper.StripVolumeMarkers</c>（剥法出口，保留归档后缀段）</description></item>
        /// <item><description><c>PackageName</c> 档 ← <c>OutputPlacement.ResolveArchiveBaseName</c>（包基名，连归档后缀段一起剥）</description></item>
        /// <item><description><c>AnchorStem</c> 档 ← <c>VolumeNumberFromContent.TryDeriveStem</c>（按锚点 + 该族后缀推；三种格式各一列）</description></item>
        /// <item><description><c>VolumeGroupDetector.TryGetFirstVolumeName</c> ← <c>Analyze</c> 的归组键（S1 的公开观测面）</description></item>
        /// <item><description><c>DisguisedVolume</c> 档 ← <c>VolumeNameRepair.TrySplitDisguised</c>（伪装卷名拆解：(基名, 规范卷段)）</description></item>
        /// <item><description><c>FileNameHelper.GetArchiveBaseName</c>（落点用的那一层，它转调剥法出口）</description></item>
        /// </list>
        /// </summary>
        public static IEnumerable<object?[]> Cases() => new[]
        {
            // 名字, 剥法出口, 包基名, 锚点7z, 锚点zip, 锚点rar, 首卷名(可空), 伪装基名(可空), 伪装卷段(可空), GetArchiveBaseName
            Row("111.7z.001", "111.7z", "111", "111", "111.7z", "111.7z", "111.7z.001", null, null, "111"),
            Row("风景01.7z.001", "风景01.7z", "风景01", "风景01", "风景01.7z", "风景01.7z", "风景01.7z.001", null, null, "风景01"),
            Row("444.p1art2.part2.rar", "444.p1art2", "444.p1art2", "444.p1art2.part2.rar", "444.p1art2.part2.rar", "444.p1art2", "444.p1art2.part1.rar", null, null, "444"),
            Row("444.p1art2.partN.rar", "444.p1art2.partN.rar", "444.p1art2.partN", "444.p1art2.partN.rar", "444.p1art2.partN.rar", "444.p1art2.partN", "444.p1art2.partN.rar", null, null, "444.p1art2.partN"),
            Row("444.---.part1.rar", "444.---", "444.---", "444.---.part1.rar", "444.---.part1.rar", "444.---", "444.---.part1.rar", null, null, "444"),
            Row("X.part1.rar删除", "X", "X", "X.part1.rar删除", "X.part1.rar删除", "X", "X.part1.rar删除", "X", "part1.rar", "X"),
            Row("giu910.7z.001删除", "giu910.7z", "giu910", "giu910", "giu910.7z", "giu910.7z", "giu910.7z.001删除", "giu910.7z", "001", "giu910"),
            Row("x.zip", "x.zip", "x", "x.zip", "x", "x.zip", "x.zip", null, null, "x"),
            Row("111.zip", "111.zip", "111", "111.zip", "111", "111.zip", "111.zip", null, null, "111"),
            Row("x.z01", "x", "x", "x", "x", "x", "x.zip", null, null, "x"),
            Row("x.7z.001", "x.7z", "x", "x", "x.7z", "x.7z", "x.7z.001", null, null, "x"),
            Row("222.7z.001", "222.7z", "222", "222", "222.7z", "222.7z", "222.7z.001", null, null, "222"),
            Row("222.7z.001.txt", "222", "222", "222.7z.001.txt", "222.7z.001.txt", "222.7z.001.txt", "222.7z.001.txt", "222.7z", "001", "222"),
            Row("x.7z.001.txt", "x", "x", "x.7z.001.txt", "x.7z.001.txt", "x.7z.001.txt", "x.7z.001.txt", "x.7z", "001", "x"),
            Row("amb909.7z删除.001", "amb909.7z删除", "amb909.7z删除", "amb909", "amb909.7z删除", "amb909.7z删除", "amb909.7z.001", null, null, "amb909"),
            Row("amb909.7sz.00c1", "amb909.7sz", "amb909.7sz", "amb909", "amb909.7sz", "amb909.7sz", "amb909.7z.001", "amb909.7z", "001", "amb909"),
            Row("x.001.bak", "x.001.bak", "x.001.bak", "x.001.bak", "x.001.bak", "x.001.bak", null, "x", "001", "x.001"),
            Row("movie.rar", "movie.rar", "movie", "movie.rar", "movie.rar", "movie", "movie.rar", null, null, "movie"),
            Row("movie.mkv.rar", "movie.mkv.rar", "movie.mkv", "movie.mkv.rar", "movie.mkv.rar", "movie.mkv", "movie.mkv.rar", null, null, "movie.mkv"),
            Row("222.rar.jpg", "222.rar.jpg", "222", "222.rar.jpg", "222.rar.jpg", "222.rar.jpg", null, null, null, "222.rar"),
            Row("444.p1art2.part1.rar", "444.p1art2", "444.p1art2", "444.p1art2.part1.rar", "444.p1art2.part1.rar", "444.p1art2", "444.p1art2.part1.rar", null, null, "444"),
            Row("444.p1art2.ra3r", "444.p1art2.ra3r", "444.p1art2.ra3r", "444.p1art2.ra3r", "444.p1art2.ra3r", "444.p1art2", "444.p1art2.rar", null, null, "444.p1art2"),
            Row("111.parst1.racr", "111", "111", "111.parst1.racr", "111.parst1.racr", "111", "111.part1.racr", "111", "part1.rar", "111"),
            Row("x.part1.rar", "x", "x", "x.part1.rar", "x.part1.rar", "x", "x.part1.rar", null, null, "x"),
            Row("111.part1.rar", "111", "111", "111.part1.rar", "111.part1.rar", "111", "111.part1.rar", null, null, "111"),
            Row("x.001.part1.rar", "x", "x", "x.001.part1.rar", "x.001.part1.rar", "x", "x.001.part1.rar", null, null, "x"),
            Row("111", "111", "111", "111", "111", "111", null, null, null, "111"),
            Row("111.7z.002", "111.7z", "111", "111", "111.7z", "111.7z", "111.7z.001", null, null, "111"),
            Row("VOLUME.7Z.001删除", "VOLUME.7Z", "VOLUME", "VOLUME", "VOLUME.7Z", "VOLUME.7Z", "VOLUME.7Z.001删除", "VOLUME.7Z", "001", "VOLUME"),
            Row("x.z0删除3", "x", "x", "x", "x", "x", "x.zip", "x", "z03", "x"),
            Row("x.7z.删除001", "x.7z", "x", "x", "x.7z", "x.7z", "x.7z.001", "x.7z", "001", "x"),
            Row("volume.7z.001.zip", "volume.7z.001.zip", "volume", "volume.7z.001.zip", "volume.7z", "volume.7z.001.zip", "volume.7z.001.zip", null, null, "volume.7z.001"),
            Row("volume.7z.0012", "volume.7z.0012", "volume.7z.0012", "volume", "volume.7z", "volume.7z", null, null, null, "volume.7z"),
            Row("rar-android-722.132.apk", "rar-android-722.132.apk", "rar-android-722.132.apk", "rar-android-722.132.apk", "rar-android-722.132.apk", "rar-android-722.132.apk", null, "rar-android-722", "132", "rar-android-722.132"),
            Row("x.001", "x", "x", "x", "x", "x", "x.001", null, null, "x"),
            Row("archive.001", "archive", "archive", "archive", "archive", "archive", "archive.001", null, null, "archive"),
            Row("x.r00", "x", "x", "x", "x", "x", "x.rar", null, null, "x"),
            Row("x.part02删除", "x", "x", "x", "x", "x", "x.part01删除", "x", "part02", "x"),
            Row("111.7z.00删除文字1", "111.7z", "111", "111", "111.7z", "111.7z", "111.7z.001", "111.7z", "001", "111"),
            Row("x.7z.001(1)", "x.7z", "x", "x", "x.7z", "x.7z", "x.7z.001(1)", "x.7z", "001", "x"),
            Row("a.b.c.d.7z.005", "a.b.c.d.7z", "a.b.c.d", "a.b.c.d", "a.b.c.d.7z", "a.b.c.d.7z", "a.b.c.d.7z.001", null, null, "a.b.c.d"),
            Row("movie.2024.rar", "movie.2024.rar", "movie.2024", "movie.2024.rar", "movie.2024.rar", "movie", "movie.2024.rar", null, null, "movie.2024"),
            Row("222.zscip", "222.zscip", "222.zscip", "222.zscip", "222", "222.zscip", "222.zip", null, null, "222"),
            Row("222.zi删除p", "222.zi删除p", "222.zi删除p", "222.zi删除p", "222", "222.zi删除p", "222.zip", null, null, "222"),
            Row("x.tar.gz", "x.tar.gz", "x", "x.tar.gz", "x.tar.gz", "x.tar.gz", null, null, null, "x"),
            Row("x.7z.001删除.txt", "x", "x", "x.7z.001删除.txt", "x.7z.001删除.txt", "x.7z.001删除.txt", "x.7z.001.txt", "x.7z", "001", "x"),
        };

        [Theory]
        [MemberData(nameof(Cases))]
        public void 唯一基名出口_五个消费点的输出逐字不变(
            string name,
            string stripMarkers,
            string packageName,
            string stemSevenZip,
            string stemZip,
            string stemRar,
            string? firstVolumeName,
            string? disguisedBaseName,
            string? disguisedSegment,
            string getArchiveBaseName)
        {
            const string path = @"C:\t\";

            // ① StripMarkers 档（剥法出口：保留归档后缀段）
            Assert.Equal(stripMarkers, Resolve(name, VolumeBaseNameLevel.StripMarkers));

            // ② PackageName 档（包基名：连归档后缀段一起剥）
            Assert.Equal(packageName, Resolve(name, VolumeBaseNameLevel.PackageName));

            // ③ AnchorStem 档（按锚点名字 + 该族后缀推）
            Assert.Equal(stemSevenZip, Resolve(name, VolumeBaseNameLevel.AnchorStem, "7z"));
            Assert.Equal(stemZip, Resolve(name, VolumeBaseNameLevel.AnchorStem, "zip"));
            Assert.Equal(stemRar, Resolve(name, VolumeBaseNameLevel.AnchorStem, "rar"));

            // ④ 归组那一处（S1）的公开观测面：由 Analyze 的归组键推出的"第 1 卷名字"
            Assert.Equal(firstVolumeName, VolumeGroupDetector.TryGetFirstVolumeName(name));

            // ⑤ 伪装卷名拆解（S4）：基名 + 规范卷段
            bool split = FileNameHelper.TryResolveVolumeBaseName(
                name,
                VolumeBaseNameLevel.DisguisedVolume,
                out string baseName,
                out string segment);

            Assert.Equal(disguisedBaseName != null, split);
            Assert.Equal(disguisedBaseName ?? string.Empty, baseName);
            Assert.Equal(disguisedSegment ?? string.Empty, segment);

            // ⚠ 三个**旧实现**本身也一起钉住（它们现在只是转调，⛔ 不许变成"出口改了、它们没跟着改"）
            Assert.Equal(stripMarkers, FileNameHelper.StripVolumeMarkers(name));
            Assert.Equal(packageName, OutputPlacement.ResolveArchiveBaseName(path + name));
            Assert.Equal(getArchiveBaseName, FileNameHelper.GetArchiveBaseName(path + name));
        }

        [Theory]
        [InlineData("amb909.7z删除", "amb909.7z")]
        [InlineData("x.7sz", "x.7z")]
        [InlineData("x.7删z", "x.7z")]
        [InlineData("rar-android-722.132", "rar-android-722.132")]
        [InlineData("444.p1art2", "444.p1art2")]
        public void 归组键那一档_只做归一化不剥标记(string rawBaseName, string expected)
        {
            /*
             * S1（VolumeGroupDetector.JoinBaseName）的标记段定位是它自己的分支逻辑，
             * 交给出口的是"已经剥掉标记段的那段正文" ⇒ 这一档只归一化（剥粘在后缀段上的垃圾 + 后缀段归一），
             * 同一组各卷的基名必须逐字相等，否则会散成两组（2026-09-28 真机）。
             */
            Assert.Equal(expected, Resolve(rawBaseName, VolumeBaseNameLevel.VolumeGroupKey));
        }

        [Fact]
        public void 判不出时_每一档都不许硬剪名字()
        {
            // ⛔ 红线：判不出 ⇒ 什么都不做（兜底永远落在这一档）。
            Assert.False(FileNameHelper.TryResolveVolumeBaseName(
                string.Empty,
                VolumeBaseNameLevel.PackageName,
                out _,
                out _));

            Assert.False(FileNameHelper.TryResolveVolumeBaseName(
                "x.part1.rar",
                VolumeBaseNameLevel.DisguisedVolume,
                out _,
                out _));

            // AnchorStem 要"该族后缀"：不给就一个字节都不推
            Assert.False(FileNameHelper.TryResolveVolumeBaseName(
                "111.7z.001",
                VolumeBaseNameLevel.AnchorStem,
                out _,
                out _));
        }

        /// <summary>
        /// **两个都叫"包基名"的量**（2026-10-04 只读探针实测：同一串名字给出不同的值）—— 逐格钉住
        /// "它们各自是什么"，并说明**为什么不能合成一个值**。
        ///
        /// <list type="bullet">
        /// <item><description><see cref="VolumeBaseNameLevel.PackageName"/>（包基名）←
        /// <c>OutputPlacement.ResolveArchiveBaseName</c>：落点/包目名用，只剥**已知归档后缀**。</description></item>
        /// <item><description><see cref="VolumeBaseNameLevel.ArchiveBaseName"/>（归档基名）←
        /// <c>FileNameHelper.GetArchiveBaseName</c>：给"同一个包的不同成员"当**匹配键**
        /// （`EngineRouter` / `RecursiveExtractor` / `RestVolumeCompletenessGate`），天真剥一层后缀。</description></item>
        /// </list>
        ///
        /// <para>⛔ **合并成一个值 = 改结论**，最要命的是 `222.zscip` 那一格：归档基名把它与 `222.z01`
        /// 归一成同一个基名 —— `RestVolumeCompletenessGate`（"其余物里的分卷是半套就什么都不做"
        /// 那道防数据丢失闸门）就靠这一格；包基名那一档不归一 ⇒ 闸门当场放行（2026-09-30 真机
        /// 25 GB 被永久删除正是这个形状）。合并要用户拍板，⛔ 不许顺手统一。</para>
        /// </summary>
        [Theory]
        [InlineData("444.p1art2.part2.rar", "444.p1art2", "444")]
        [InlineData("222.rar.jpg", "222", "222.rar")]
        [InlineData("amb909.7sz.00c1", "amb909.7sz", "amb909")]
        [InlineData("222.zscip", "222.zscip", "222")]
        [InlineData("222.z01", "222", "222")]
        [InlineData("movie.2024", "movie.2024", "movie")]
        public void 包基名与归档基名是两个问题_各自的值逐格钉住(
            string name,
            string expectedPackageName,
            string expectedArchiveBaseName)
        {
            Assert.Equal(expectedPackageName, OutputPlacement.ResolveArchiveBaseName(name));
            Assert.Equal(expectedArchiveBaseName, FileNameHelper.GetArchiveBaseName(name));

            // 两个值都必须由**唯一基名出口**的两档算出来（⛔ 不许任何一处自己再算一遍）。
            Assert.Equal(expectedPackageName, Resolve(name, VolumeBaseNameLevel.PackageName));
            Assert.Equal(expectedArchiveBaseName, Resolve(name, VolumeBaseNameLevel.ArchiveBaseName));
        }

        private static string Resolve(string name, VolumeBaseNameLevel level, string? extension = null) =>
            FileNameHelper.TryResolveVolumeBaseName(name, level, out string baseName, out _, extension)
                ? baseName
                : "<false>";

        private static object?[] Row(
            string name,
            string stripMarkers,
            string packageName,
            string stemSevenZip,
            string stemZip,
            string stemRar,
            string? firstVolumeName,
            string? disguisedBaseName,
            string? disguisedSegment,
            string getArchiveBaseName) =>
            new object?[]
            {
                name,
                stripMarkers,
                packageName,
                stemSevenZip,
                stemZip,
                stemRar,
                firstVolumeName,
                disguisedBaseName,
                disguisedSegment,
                getArchiveBaseName
            };
    }
}

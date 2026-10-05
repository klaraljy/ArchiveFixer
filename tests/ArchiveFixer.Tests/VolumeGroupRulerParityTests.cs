using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests;

/// <summary>
/// 同一个问题「这几片是不是同一个包」，全项目有**两把尺子**；本用例把两把尺子的答案逐格钉住。
///
/// <para><b>两把尺子分别是</b>：</para>
/// <list type="number">
/// <item><description><b>折叠尺</b>（递归层把一组卷折叠成 1 个内层归档用的）
/// = <see cref="RecursiveExtractor.IsSameGroupContinuationVolume"/>：同目录 +
/// 「是后续卷」（<c>FileNameHelper.IsVolumeContinuationPart</c>，<b>只看最后一个扩展名</b>）+
/// <c>GetArchiveBaseName</c> 逐字相等。</description></item>
/// <item><description><b>搬运删除尺</b>（整组一起进「其余物」/ 整组一起删用的）
/// = <see cref="ExtractionWorkspace.IsSameGroupMemberInSameDirectory"/>：同目录 +
/// 「是分卷件」（<c>FileNameHelper.IsVolumePartFileName</c>，<b>含"跨段伪装"分支</b>）+
/// <c>VolumeGroupDetector.BelongsToSameGroup</c>。</description></item>
/// </list>
///
/// <para><b>⛔ 两把宽窄不同是刻意设计，不许"顺手统一成一把握"</b>（用户 2026-10-05 拍板：只加守门用例、
/// 不合并）：搬运删除那把是<b>改名保护闸门</b>（宁可多认一片，也绝不把分卷名改坏 ——
/// <c>RenameService</c> / <c>SourceJunkScanner</c> / <c>ExtractionCoordinator</c> 等处也在用它）；
/// 折叠那把管<b>组卷</b>（要准，别把普通文件当成续卷）。答错的方向也不一样：
/// 折叠尺答错 = 少折叠一片 ⇒ 多当一个分支 / 多解一次（慢，<b>不丢数据</b>）；
/// 搬运删除尺答错 = 少带走或多带走一片 ⇒ <b>不可逆</b>（删除侧另有两道闸门兜底：事实表
/// <c>movedFromTo</c> 与 <c>RestVolumeCompletenessGate</c>，判不出就整组一个字节都不删）。</para>
///
/// <para><b>今天允许的差异恰好 4 格</b>，全在 7z 数字族「卷标记后面还挂着一个点段」这一族
/// （<c>x.7z.002.txt</c> / <c>.bak</c> / <c>.rar</c>）：折叠尺认不出、搬运删除尺认得出。
/// <b>盘上后果一样</b>：真 7z 实测（4 卷 / <c>-v2m</c>）只有第 1 卷带魔数 <c>37 7A BC AF 27 1C</c>，
/// 第 2 卷起是纯中间字节（<c>A3 21 41 FD …</c>）⇒ 递归那份内层包名单只收
/// <c>MagicArchiveProber.IsArchiveAsync</c> 认的 ⇒ 折叠尺窄不窄在那里看不见。</para>
///
/// <para><b>这条用例守什么</b>：以后谁改分卷名规则（<c>ExtensionHelper</c> / <c>FileNameHelper</c>），
/// 只要让任一格答案变了、或冒出一个<b>新的</b>不一致格子，这里当场红 —— 逼他回来解释这个差异是不是有意的。
/// 历史上"同一判据写两遍、只改一处"翻过车（复盘 §51：三处各写一遍 <c>tail == "rar"</c>，
/// 尾巴一粘垃圾三处同时失效）。</para>
/// </summary>
public sealed class VolumeGroupRulerParityTests
{
    /// <summary>语料用的假目录：两把尺子都只做字符串运算，不碰盘。</summary>
    private const string Dir = @"Z:\corpus";

    private static string Full(string name) => Path.Combine(Dir, name);

    private sealed record Cell(string First, string Candidate, string Shape, bool Fold, bool MoveDelete);

    /// <summary>
    /// 名字语料：33 格覆盖四种分卷族（数字 / zip 跨盘 / rar 老式 / rar 新式）+ 各种脏名字
    /// （卷标记粘垃圾、标记里夹垃圾、跨段伪装、骨架还原、伪装本体）+ 三格"必须不认"的对照。
    /// </summary>
    private static readonly Cell[] Corpus =
    {
        new("x.7z.001", "x.7z.002", "7z 数字族·干净", true, true),
        new("volume.001", "volume.002", "数字族·无归档后缀", true, true),
        new("x.7z.001删除", "x.7z.002删除", "7z 数字族·卷标记粘垃圾", true, true),
        new("x.7删z.00除1", "x.7删z.00除2", "7z 数字族·标记里夹垃圾", true, true),
        new("x.7z.001.txt", "x.7z.002.txt", "跨段伪装（标记后挂点段）", false, true),
        new("set.7aaaaz.001", "set.7aaaaz.002", "7z 归档后缀段骨架", true, true),
        new("333.7z.001", "333.78a8fuaz.003", "7z 归档后缀段骨架（另一形）", true, true),
        new("set.7z.001", "set.7_______z.002", "7z 骨架·中间夹下划线", true, true),
        new("x.part1.rar", "x.part2.rar", "RAR 新式", true, true),
        new("x.part01.rar", "x.part02.rar", "RAR 新式·补零", true, true),
        new("x.part1.rar删除", "x.part2.rar删除", "RAR 新式·尾巴粘垃圾", true, true),
        new("444.pa8rt1.rar", "444.part2.rar", "RAR 新式·卷标记骨架", true, true),
        new("444.p1art2.part1.rar", "444.p1art2.part2.rar", "RAR 新式·基名自带一段", true, true),
        new("x.rar", "x.r00", "RAR 老式", true, true),
        new("x.rar", "x.r01", "RAR 老式·第二续卷", true, true),
        new("x.zip", "x.z01", "zip 跨盘", true, true),
        new("x.zip", "x.z02", "zip 跨盘·第二续卷", true, true),
        new("222.zscip", "222.z删除01", "伪装本体 + 脏卷标记", true, true),
        new("111.7z.001", "111", "中间卷名字全丢", false, false),
        new("x.7z.001", "x.7z.001.txt", "候选=跨段伪装的首卷", false, true),
        new("x.7z.001", "y.7z.002", "对照·不同基名", false, false),
        new("x.7z.001", "movie.mp4", "对照·非归档", false, false),
        new("x.7z.001", "x.7z", "对照·本体（无卷号）", false, false),
        new("x.part1.rar", "x.part2.rar删除", "半边脏（只后一卷粘垃圾）", true, true),
        new("x.7z.001", "x.7z.002sc", "续卷尾巴粘字母", true, true),
        new("x.rar", "x.rar.002.bak", "跨段伪装·rar 本体族", false, false),
        new("x.7z.001", "x.7z.002.bak", "跨段伪装·7z 数字族", false, true),
        new("x.zip", "x.zip.002.bak", "跨段伪装·zip 族", false, false),
        new("x.rar", "x.001.bak", "跨段伪装·老规格（AGENTS 点名的形状）", false, false),
        new("x.rar", "x.r00.bak", "跨段伪装·老式续卷挂尾巴", false, false),
        new("x.zip", "x.z01.bak", "跨段伪装·跨盘 zip 续卷挂尾巴", false, false),
        new("x.7z.001", "x.7z.002.rar", "分卷段后挂 .rar（既有写法）", false, true),
        new("x.part1.rar", "x.part2.rar.bak", "part 族·尾巴挂点段", false, false),
    };

    /// <summary>今天允许两把尺子答案不同的格子（按语料顺序；多一格少一格都算漂移）。</summary>
    private static readonly string[] AllowedDifferences =
    {
        "x.7z.001.txt + x.7z.002.txt",
        "x.7z.001 + x.7z.001.txt",
        "x.7z.001 + x.7z.002.bak",
        "x.7z.001 + x.7z.002.rar",
    };

    private static bool FoldRuler(string first, string candidate)
        => RecursiveExtractor.IsSameGroupContinuationVolume(Full(candidate), Full(first));

    private static bool MoveDeleteRuler(string first, string candidate)
        => ExtractionWorkspace.IsSameGroupMemberInSameDirectory(Full(candidate), Full(first), first);

    [Fact]
    public void 两把尺子逐格比对_每一格的答案都被钉住()
    {
        var wrong = new List<string>();

        foreach (Cell cell in Corpus)
        {
            bool fold = FoldRuler(cell.First, cell.Candidate);
            bool moveDelete = MoveDeleteRuler(cell.First, cell.Candidate);

            if (fold != cell.Fold)
            {
                wrong.Add(
                    $"折叠尺：{cell.First} + {cell.Candidate}（{cell.Shape}）"
                    + $"应当 {cell.Fold}，实际 {fold}");
            }

            if (moveDelete != cell.MoveDelete)
            {
                wrong.Add(
                    $"搬运删除尺：{cell.First} + {cell.Candidate}（{cell.Shape}）"
                    + $"应当 {cell.MoveDelete}，实际 {moveDelete}");
            }
        }

        Assert.True(
            wrong.Count == 0,
            "分卷名判据变了：这两把尺子的答案被改动了。改完请回来确认这个差异是不是有意的 ——"
            + "搬运删除那一侧是不可逆的（删除）。逐格差异：" + Environment.NewLine
            + string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void 允许的差异只有那四格_多一格少一格都算漂移()
    {
        List<string> actual = Corpus
            .Where(cell => FoldRuler(cell.First, cell.Candidate) != MoveDeleteRuler(cell.First, cell.Candidate))
            .Select(cell => $"{cell.First} + {cell.Candidate}")
            .ToList();

        Assert.Equal(AllowedDifferences, actual);
    }

    /// <summary>
    /// 折叠尺的**调用点**也要钉（⛔ 不然上面两条只是"对着一份没人用的判据断言"）：
    /// <see cref="RecursiveExtractor.CollapseSameGroupVolumes"/> 是纯函数，直接摆布它。
    /// 三格正常组卷必须折叠成 1 个内层归档；跨段伪装那一格今天**不折叠**（见类注释：
    /// 那一格盘上出现不了，钉的是判据本身）。
    /// </summary>
    [Fact]
    public void 折叠尺的调用点_一组卷折叠成一个内层归档()
    {
        Assert.Single(RecursiveExtractor.CollapseSameGroupVolumes(
            new List<string> { Full("x.7z.001"), Full("x.7z.002"), Full("x.7z.003") }));

        Assert.Single(RecursiveExtractor.CollapseSameGroupVolumes(
            new List<string> { Full("444.pa8rt1.rar"), Full("444.part2.rar") }));

        Assert.Single(RecursiveExtractor.CollapseSameGroupVolumes(
            new List<string> { Full("x.rar"), Full("x.r00"), Full("x.r01") }));

        // 不同的包一个都不许被折叠掉（对照）。
        Assert.Equal(2, RecursiveExtractor.CollapseSameGroupVolumes(
            new List<string> { Full("x.7z.001"), Full("y.7z.001") }).Count);

        // 今天的已知差异：跨段伪装那一族不折叠（⛔ 想"顺手统一"它，先回来看类注释与 AGENTS §11.4）。
        Assert.Equal(2, RecursiveExtractor.CollapseSameGroupVolumes(
            new List<string> { Full("x.7z.001.txt"), Full("x.7z.002.txt") }).Count);
    }
}

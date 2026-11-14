using static ArchiveFixer.Helpers.ExtensionNameRules;
using static ArchiveFixer.Helpers.ExtensionSkeletonMatcher;
using static ArchiveFixer.Helpers.VolumeMarkerRules;
using static ArchiveFixer.Helpers.VolumeMarkerSkeletonMatcher;
using static ArchiveFixer.Helpers.VolumeSegmentSplitter;

namespace ArchiveFixer.Helpers;

/// <summary>
/// **"这个名字的某一格是靠猜才读出来的"** 的三个谓词（唯一的猜测判定出口）：
/// 卷标记段（<see cref="IsVolumeMarkByDisguise"/>）、归档后缀段
/// （<see cref="IsArchiveSegmentByDisguise"/>）、partN 族的卷标记段
/// （<see cref="IsPartNumberedMarkBySkeleton"/>）。
///
/// <para><b>为什么单独立这一类</b>：它们回答的是**同一件事**——"这一格要不要重建"，
/// 而"敢不敢真改名"是另一件事（要再过整组自洽、兄弟卷佐证、尺寸规律、试开）。
/// 上层（<c>VolumeNameRepair</c> / <c>VolumeNamePlanner</c>）只认这三个入口，
/// ⛔ 不许各处自己写"像不像猜的"判据。</para>
///
/// <para>⛔ 逐字就合法的那几档一律 false：<c>x.7z.001</c> / <c>x.7z.001.txt</c> 不是猜的；
/// <c>001删除</c> / <c>001(1)</c>（逐字标记 + 粘着垃圾）也不算"要重建"——
/// 改名只删尾巴、卷号一个字符都不动，那是既有老口径。</para>
/// </summary>
internal static class VolumeNameRebuildGate
{
    /// <summary>
    /// 这个 <c>partN</c> 名字的**卷标记段**是不是"靠**骨架档**才认出来"的（末尾那段本身脏，例 <c>444.pa8rt1.rar</c>）。
    ///
    /// <para>判据两道（都转调既有出口，⛔ 不新造尺子）：① 倒数第二段**逐字不是**合法卷标记
    /// （<see cref="VolumeMarkerRules.IsVolumePartExtension"/>）；② 它按 <see cref="VolumeMarkerSkeletonMatcher.TryMatchPartNumberedSkeleton"/> 命中骨架。
    /// 这一位**只回答"卷标记这一格"** —— 尾巴那一段是不是 <c>rar</c>、要不要改名，由调用方自己判。</para>
    ///
    /// <para>⚠ 为什么要单独立这一位（用户 2026-10-04 第二轮口径）：这一段既可能是**真脏的卷标记**
    /// （<c>444.pa8rt1.rar</c> ⇒ 该认成 <c>part1</c>），也可能是**基名自己的一段**
    /// （<c>444.p1art2.part2.rar</c> 里那个 <c>p1art2</c>）—— 形状分不开，只能靠"它是不是这一组
    /// **最右**那个卷标记 + 同目录能不能配出**一组自洽的兄弟卷**"来分（「整组自洽」那道判据
    /// 落在 <c>VolumeNameRepair</c> 的改名链上）。</para>
    /// </summary>
    /// <param name="fileName">文件名（可含路径，内部只取文件名）。</param>
    /// <param name="canonicalMark">骨架命中的规范卷标记（<c>pa8rt1</c> ⇒ <c>part1</c>）。</param>
    public static bool IsPartNumberedMarkBySkeleton(string? fileName, out string canonicalMark)
    {
        canonicalMark = string.Empty;

        string name = string.IsNullOrWhiteSpace(fileName)
            ? string.Empty
            : System.IO.Path.GetFileName(fileName);

        if (name.Length == 0)
        {
            return false;
        }

        string[] parts = name.Split('.');

        if (parts.Length < 3)
        {
            return false;
        }

        string mark = parts[^2];

        // 逐字就合法 ⇒ 老口径那两档管它，⛔ 这里不抢（`444.p1art2.part2.rar` 的 `part2` 走这条）。
        if (IsVolumePartExtension("." + mark))
        {
            return false;
        }

        return TryMatchPartNumberedSkeleton(mark, out canonicalMark, out _);
    }

    /// <summary>
    /// 这个名字的**卷标记段本身**是不是"要**重建**才读得出来"的
    /// （<c>333.7z.0a0b1</c> ⇒ <c>001</c>、<c>222.z0删1</c> ⇒ <c>z01</c>、<c>444.pa8rt1.rar</c> ⇒ <c>part1</c>）。
    ///
    /// <para>判据（全部转调既有出口）：① 那一段**逐字不是**合法卷标记
    /// （<see cref="VolumeMarkerRules.IsVolumePartExtension"/>）；② **也不是**"逐字标记 + 粘着的垃圾"
    /// （<see cref="VolumeSegmentSplitter.TrySplitVolumeSegment"/>：<c>001删除</c> / <c>001(1)</c>）；③ 容错档
    /// <see cref="VolumeSegmentSplitter.TrySplitVolumeSegmentTolerant"/> 命中。partN 族由
    /// <see cref="IsPartNumberedMarkBySkeleton"/> 先回答（本方法转调它）。</para>
    ///
    /// <para>⛔ 它**只回答"卷标记这一格要不要重建"** —— 敢不敢改名由调用方那几道闸门回答
    /// （用户 2026-10-04 第六轮：「孤立一个 <c>set.7z.0a0b1</c> ⇒ 不改」：要重建的名字必须再过
    /// 「整组自洽」，判据在 <c>VolumeNameRepair.TryConfirmSelfConsistentVolumeGroup</c>）。</para>
    ///
    /// <para>⚠ 末段逐字就合法（<c>x.7z.001</c> / <c>x.7z.001.txt</c>）一律 false ——
    /// 那一档不是猜的，⛔ 不许顺手把闸门加到它头上（`x.7z.001.txt` 那一组照旧按老口径改）。</para>
    ///
    /// <para>⛔ **「逐字标记 + 粘着的垃圾」也不算要重建**（<c>001删除</c> / <c>001(1)</c>）：
    /// 那一段的**开头就是一个逐字合法的卷标记**、尾巴是垃圾 ⇒ 改名只删尾巴、**卷号一个字符都不动**
    /// —— 这是 2026-09-28（网盘缀「删除」）起的**老口径**，与 partN 那一档**同一条纪律**
    /// （<c>X.part1.rar删除</c> 的标记 <c>part1</c> 逐字干净 ⇒ <see cref="IsPartNumberedMarkBySkeleton"/>
    /// 同样是 false）。⚠ 实测记帐（2026-10-04 第六轮）：把这一档也收进闸门 ⇒
    /// <c>VolumeNameRepairGroupTests</c> / <c>RealMachine20261002FixesTests</c> /
    /// <c>RealMachineDefectFixesTests</c> **3 条既有用例变红**（夹具是"网盘缀删除 + 尺寸不规律 /
    /// 散在两个目录"）⇒ 闸门只管**要重建卷标记**的那一档，这一档照旧只按名字改。</para>
    /// </summary>
    public static bool IsVolumeMarkByDisguise(string? fileName, out string canonicalMark)
    {
        canonicalMark = string.Empty;

        string name = string.IsNullOrWhiteSpace(fileName)
            ? string.Empty
            : System.IO.Path.GetFileName(fileName);

        if (name.Length == 0)
        {
            return false;
        }

        // partN 族：卷标记 = 倒数第二段（尾巴是 rar 那一族），判据已在既有出口里。
        if (IsPartNumberedMarkBySkeleton(name, out canonicalMark))
        {
            return true;
        }

        string[] parts = name.Split('.');

        if (parts.Length < 2)
        {
            return false;
        }

        string last = parts[^1];

        // 末段逐字就是合法卷标记 ⇒ 这一格不是猜出来的（`x.7z.001` / `x.7z.001.txt` 走这条）。
        if (IsVolumePartExtension("." + last))
        {
            return false;
        }

        // 「逐字标记 + 粘着的垃圾」（`001删除` / `001(1)`）⇒ 老口径，不算"要重建"（见上面那段说明）。
        if (TrySplitVolumeSegment(last, out _, out _))
        {
            return false;
        }

        return TrySplitVolumeSegmentTolerant(last, out canonicalMark, out _);
    }

    /// <summary>
    /// 这个名字的**归档后缀段**是不是"靠容错/骨架档才读出来"的
    /// （<c>set.7aaaaz.002</c> ⇒ <c>7z</c>、<c>333.78a8fuaz.003</c> ⇒ <c>7z</c>、<c>set.7_______z.002</c> ⇒ <c>7z</c>）。
    ///
    /// <para>形状两道：① 末段**逐字就是**合法卷标记（<c>.002</c>）；② 它前面那一段逐字**不是**
    /// 已知归档后缀、却归一得出一个（<see cref="ExtensionSkeletonMatcher.TryRecoverDisguisedArchiveBody"/>，
    /// 三档 + 首尾对齐 + 结果唯一）。⛔ <c>set.7z.002</c>（后缀段本来就干净）一律 false。</para>
    ///
    /// <para>⛔ 同 <see cref="IsVolumeMarkByDisguise"/>：只回答"这一格是不是猜出来的"，
    /// 敢不敢改名由调用方那几道闸门回答（用户 2026-10-04 第六轮：这一档也要过「整组自洽」）。</para>
    /// </summary>
    public static bool IsArchiveSegmentByDisguise(string? fileName, out string canonicalExtension)
    {
        canonicalExtension = string.Empty;

        string name = string.IsNullOrWhiteSpace(fileName)
            ? string.Empty
            : System.IO.Path.GetFileName(fileName);

        if (name.Length == 0)
        {
            return false;
        }

        string[] parts = name.Split('.');

        if (parts.Length < 3 || !IsVolumePartExtension("." + parts[^1]))
        {
            return false;
        }

        string segment = parts[^2];

        // 本来就干净 ⇒ 不是猜的。
        if (IsKnownArchiveExtension("." + segment))
        {
            return false;
        }

        return TryRecoverDisguisedArchiveBody(segment, out canonicalExtension, out _);
    }
}

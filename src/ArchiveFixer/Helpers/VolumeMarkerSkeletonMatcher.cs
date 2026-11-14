using System;
using System.Collections.Generic;
using static ArchiveFixer.Helpers.ExtensionSkeletonMatcher;
using static ArchiveFixer.Helpers.VolumeMarkerRules;

namespace ArchiveFixer.Helpers;

/// <summary>
/// **卷标记的骨架命中**：<c>z01</c> / <c>r00</c> / 三位数字（<c>001</c>）/ <c>partN</c> 四族，
/// 以及"卷号被拉长 + 夹非 ASCII"那一档（<c>z11111111110删除3</c> ⇒ <c>z03</c>）。
///
/// <para>纪律与归档后缀骨架同一套（<see cref="ExtensionSkeletonMatcher"/>）：
/// **首尾对齐**（杂质只夹在中间）、**结果唯一**（两个骨架都能命中 ⇒ 判不出 ⇒ 不认）。</para>
///
/// <para>⚠ <c>partN</c> 骨架**默认不吃**：只有调用方已站定"这一段就是最右那个卷标记"
/// （<see cref="VolumeNameRebuilder.TrySplitPartNumberedVolume"/> 的尾巴逐字是 <c>rar</c> 那一档）
/// 才允许 —— <c>p1art2</c>（基名段）与 <c>pa8rt1</c>（脏卷标记）同形，任何按形状的判据都分不开
/// 它们（<c>444.p1art2.part2.rar</c> 的基名不许被再剥一层）。</para>
///
/// <para>⛔ 这里只回答"这一段去掉杂质之后是哪个卷标记"，⛔ 不回答"该不该改名"，
/// 也不判"哪个标记才是这一组最右的那个"（那是调用方的事）。</para>
/// </summary>
internal static class VolumeMarkerSkeletonMatcher
{
    /// <summary>
    /// 卷标记骨架命中（<see cref="VolumeSegmentSplitter.TrySplitVolumeSegmentTolerant"/> 的第三档）：
    /// <c>0a0b1</c> → <c>001</c>、<c>z0a1</c> → <c>z01</c>、<c>r0a1</c> → <c>r01</c>；
    /// <paramref name="allowPartNumberedSkeleton"/> = true 时连 <c>pa8rt1</c> → <c>part1</c> 也认。
    /// 另外还认"**卷号被拉长 + 夹非 ASCII**"那一种（<c>z11111111110删除3</c> → <c>z03</c>，
    /// 见 <see cref="TryMatchStretchedVolumeOrdinal"/>）。
    /// 与归档后缀那一档**同一条纪律**：**首尾对齐**（杂质只夹在中间）、多个候选 ⇒ 判不出。
    ///
    /// <para>⛔ <b>partN 骨架按"取最右"规矩放开</b>（用户 2026-10-04 拍板：「放开，但规定取最右边那个
    /// 能自洽的卷标记 —— 可以」）：只有调用方**已经站定"这一段就是最右那个卷标记"**时才传 true
    /// （眼下只有 <see cref="VolumeNameRebuilder.TrySplitPartNumberedVolume"/> 那一处：<c>&lt;基名&gt;.&lt;卷标记&gt;.rar</c>，
    /// 尾巴**逐字**是 <c>rar</c>）。理由是 `p1art2`（RAR 真夹具 `444.p1art2.part2.rar` 的**基名段**）
    /// 与 `pa8rt1` 是**同一个形状**（字母序列都是 <c>part</c>、都夹一个数字），任何按形状的判据都分不开
    /// 它们 —— 裸的末尾段一旦也吃骨架档，`444.p1art2.part2.rar` 剥完 `part2.rar` 之后剩下的
    /// `444.p1art2` 会被再剥一次 ⇒ 基名从 `444.p1art2` 变成 `444`（RAR 族"基名 = partN 段**之前**的
    /// 所有点段"这条不变量当场破）。⇒ "取最右"就是这条：**左边剩下来的段不许再吃骨架档**。</para>
    /// </summary>
    internal static bool TryMatchVolumeMarkerSkeleton(
        string s,
        bool allowPartNumberedSkeleton,
        out string canonicalSegment,
        out string junk)
    {
        canonicalSegment = string.Empty;
        junk = string.Empty;

        string? unique = null;
        string uniqueJunk = string.Empty;

        if (TryMatchVolumeMarkerCandidate(s, 'z', out string byZip, out string zipJunk, out int zipCount) && zipCount > 0)
        {
            unique = byZip;
            uniqueJunk = zipJunk;
        }

        if (TryMatchVolumeMarkerCandidate(s, 'r', out string byRar, out string rarJunk, out int rarCount) && rarCount > 0)
        {
            if (unique != null)
            {
                return false;
            }

            unique = byRar;
            uniqueJunk = rarJunk;
        }

        /*
         * 「卷号被拉长 + 夹着非 ASCII 杂质」那一档（真机 EEEE：`111.z11111111110删除3`）。
         *
         * 上面那两条候选只取"字母后面**头两个**数字、而且第二个必须落在段末"（首尾对齐）——
         * 这个名字的第二个数字位离段末还有 12 个字符 ⇒ 一条都不命中 ⇒ 整段判不出是卷标记。
         * ⛔ 位置必须在"三位数字骨架"之前：那一条会把这个段里的数字凑成 `001` 之类，
         * 与"末两位才是卷号"的口径撞车（本档一旦命中就 `return false`，绝不会两条一起用）。
         */
        if (TryMatchStretchedVolumeOrdinal(s, 'z', out string byStretchedZip, out string stretchedZipJunk))
        {
            if (unique != null)
            {
                return false;
            }

            unique = byStretchedZip;
            uniqueJunk = stretchedZipJunk;
        }

        if (TryMatchStretchedVolumeOrdinal(s, 'r', out string byStretchedRar, out string stretchedRarJunk))
        {
            if (unique != null)
            {
                return false;
            }

            unique = byStretchedRar;
            uniqueJunk = stretchedRarJunk;
        }

        /*
         * partN 骨架（"取最右"那一档）：`part` 四个字母按顺序**从段首**开始，后面**一路到段末全是数字**
         * （`pa8rt1` → `part1`、`0a0b1` 那种由上面的三位数字骨架管）。
         */
        if (allowPartNumberedSkeleton
            && TryMatchPartNumberedSkeleton(s, out string byPart, out string partJunk))
        {
            if (unique != null)
            {
                return false;
            }

            unique = byPart;
            uniqueJunk = partJunk;
        }

        // 三位数字骨架：段里按顺序出现 3 个 ASCII 数字（`0a0b1` → `001`）。
        var digits = new System.Text.StringBuilder(3);
        var digitPositions = new List<int>(3);

        for (int i = 0; i < s.Length && digits.Length < 3; i++)
        {
            if (char.IsAsciiDigit(s[i]))
            {
                digits.Append(s[i]);
                digitPositions.Add(i);
            }
        }

        // ⛔ 首尾对齐（与 <see cref="ExtensionSkeletonMatcher.TryMatchKnownSkeleton"/> 同一条）：杂质只能夹在中间。
        if (digits.Length == 3 && digitPositions[0] == 0 && digitPositions[2] == s.Length - 1)
        {
            if (unique != null)
            {
                return false;
            }

            unique = digits.ToString();
            uniqueJunk = BuildSkeletonJunk(s, digitPositions.ToArray());
        }

        if (unique == null)
        {
            return false;
        }

        canonicalSegment = unique;
        junk = uniqueJunk;
        return true;
    }

    /// <summary>
    /// <c>partN</c> 骨架：<c>part</c> 四个字母按顺序**从段首**开始、后面**一路到段末全是 ASCII 数字**
    /// （<c>pa8rt1</c> → <c>part1</c>、<c>paart02</c> → <c>part02</c>）。
    ///
    /// <para>⛔ 与另外几档同一条纪律：**首尾对齐**（字母在段首、数字在段末）、没有数字 ⇒ 不认
    /// （<c>partN</c> 这种"字母尾巴"不是卷标记）；⛔ 只由 <see cref="VolumeNameRebuilder.TrySplitPartNumberedVolume"/>
    /// 在"尾巴逐字是 <c>rar</c>"那一档转调（"取最右"，见 <see cref="TryMatchVolumeMarkerSkeleton"/>）。</para>
    /// </summary>
    public static bool TryMatchPartNumberedSkeleton(string s, out string canonicalSegment, out string junk)
    {
        canonicalSegment = string.Empty;
        junk = string.Empty;

        int[] positions = new int[4];
        int cursor = 0;

        for (int k = 0; k < 4; k++)
        {
            char letter = "part"[k];
            int found = -1;

            for (int i = cursor; i < s.Length; i++)
            {
                if (char.ToLowerInvariant(s[i]) == letter)
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
            {
                return false;
            }

            positions[k] = found;
            cursor = found + 1;
        }

        // 首尾对齐：字母序列从段首开始、数字一路到段末（中间夹什么都算杂质）。
        if (positions[0] != 0 || cursor >= s.Length)
        {
            return false;
        }

        for (int i = cursor; i < s.Length; i++)
        {
            if (!char.IsAsciiDigit(s[i]))
            {
                return false;
            }
        }

        string canonical = "part" + s[cursor..];

        if (!IsVolumePartExtension("." + canonical))
        {
            return false;
        }

        var all = new int[positions.Length + (s.Length - cursor)];

        Array.Copy(positions, all, positions.Length);

        for (int i = cursor; i < s.Length; i++)
        {
            all[positions.Length + i - cursor] = i;
        }

        canonicalSegment = canonical;
        junk = BuildSkeletonJunk(s, all);
        return true;
    }

    /// <summary>
    /// <c>zNN</c> / <c>rNN</c> 骨架：该字母之后**按顺序**再出现两个 ASCII 数字（<c>z0a1</c> → <c>z01</c>）。
    /// 返回的 <paramref name="hitCount"/> 是"这个字母后面凑得出两数位"的候选个数（0 = 没这一档）。
    /// </summary>
    private static bool TryMatchVolumeMarkerCandidate(
        string s,
        char letter,
        out string canonicalSegment,
        out string junk,
        out int hitCount)
    {
        canonicalSegment = string.Empty;
        junk = string.Empty;
        hitCount = 0;

        int start = s.IndexOf(letter, StringComparison.OrdinalIgnoreCase);

        if (start < 0)
        {
            return false;
        }

        List<int> positions = new List<int>(3) { start };

        for (int i = start + 1; i < s.Length && positions.Count < 3; i++)
        {
            if (char.IsAsciiDigit(s[i]))
            {
                positions.Add(i);
            }
        }

        if (positions.Count < 3)
        {
            return false;
        }

        // ⛔ 首尾对齐：字母在最前、第二个数位在段末（杂质只夹在中间）。
        if (positions[0] != 0 || positions[2] != s.Length - 1)
        {
            return false;
        }

        canonicalSegment = new string(new[] { letter, s[positions[1]], s[positions[2]] });
        junk = BuildSkeletonJunk(s, positions.ToArray());
        hitCount = 1;
        return true;
    }

    /// <summary>
    /// <c>zNN</c> / <c>rNN</c> **卷号被拉长**档：字母在最前，**剔掉非 ASCII 杂质之后**剩下的全是
    /// ASCII 数字、而且位数 &gt; 2 ⇒ **末两位就是卷号**（真机 EEEE 2026-10-10：
    /// <c>z11111111110删除3</c> → 骨架 <c>z111111111103</c> → <c>z03</c>，即 `111` 这一组的第 3 片）。
    ///
    /// <para><b>为什么别的档都认不出它</b>：两位卷号那条唯一尺子
    /// （<see cref="VolumeMarkerRules.IsVolumePartExtension"/>）只认 <c>z</c> + **两位**数字；骨架档那两条候选
    /// （<see cref="TryMatchVolumeMarkerCandidate"/>）走的是"首尾对齐 + 只取字母后面**头两个**数字"，
    /// 而这个名字的第二个数字位离段末还有 12 个字符 ⇒ 一条都不命中。</para>
    ///
    /// <para><b>认不出的后果（真机实测数字，不是推测）</b>：这一片算不算 <c>111</c> 这一组的成员、
    /// 基名算不算 <c>111</c>，全部由 <see cref="VolumeSegmentSplitter.TrySplitVolumeSegmentTolerant"/> 转调出来的这一档回答
    /// ⇒ 认不出就得到基名 `111.z11111111110删除3`（不是 `111`）⇒ 接片那一档拿到空的目标层、
    /// **静默什么都不做**（连一行日志都没有，`ExtractionCoordinator.TryAdoptUnresolvedVolumePiece`
    /// 里 `targetDir.Length == 0` 那一支）⇒ 那一组永远停在"缺 `111.z03`"，一次引擎调用都不做。
    /// 而那一片是真能用的：四片凑齐后 7-Zip 26.03 认这一组（`Volumes = 4`）并解出 679844929 字节。</para>
    ///
    /// <para><b>判据只做项目已有规则要求的那一步</b>（「后缀/卷名判定的第一步 = 先剥掉非 ASCII
    /// 杂质（含中文）再判」）：先剥非 ASCII，再看剩下的形状。⛔ 只认"字母 +（可夹非 ASCII）+
    /// 一路到底全是数字、且数字位数 &gt; 2"这一种形状；⛔ 一个非 ASCII 杂质都没有的不认
    /// （那是另一套位宽，不猜）；⛔ 剩下的部分只要出现一个 ASCII 非数字字符就立刻不认
    /// （`z0a1` 那种由上面那条候选管，⛔ 不许在这一档里再猜一次）。</para>
    /// </summary>
    /// <param name="s">待判的那一段（不含点）。</param>
    /// <param name="letter">本族的卷标记字母（<c>z</c> / <c>r</c>，小写）。</param>
    /// <param name="canonicalSegment">命中的规范卷标记（<c>z11111111110删除3</c> ⇒ <c>z03</c>）。</param>
    /// <param name="junk">被剔掉的那些非 ASCII 杂质（按原顺序拼回）。</param>
    private static bool TryMatchStretchedVolumeOrdinal(
        string s,
        char letter,
        out string canonicalSegment,
        out string junk)
    {
        canonicalSegment = string.Empty;
        junk = string.Empty;

        // 最短的形状 = 1 个字母 + 3 位数字 + 1 个杂质字符。
        if (s.Length < 5 || char.ToLowerInvariant(s[0]) != char.ToLowerInvariant(letter))
        {
            return false;
        }

        var digits = new System.Text.StringBuilder(s.Length);
        var removed = new System.Text.StringBuilder(s.Length);

        for (int i = 1; i < s.Length; i++)
        {
            char ch = s[i];

            if (char.IsAsciiDigit(ch))
            {
                digits.Append(ch);
                continue;
            }

            if (char.IsAscii(ch))
            {
                return false;
            }

            removed.Append(ch);
        }

        if (digits.Length <= 2 || removed.Length == 0)
        {
            return false;
        }

        canonicalSegment = new string(new[] { letter, digits[digits.Length - 2], digits[digits.Length - 1] });
        junk = removed.ToString();
        return true;
    }
}

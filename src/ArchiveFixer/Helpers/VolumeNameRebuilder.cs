using System;
using System.Linq;
using static ArchiveFixer.Helpers.ExtensionNameRules;
using static ArchiveFixer.Helpers.ExtensionSkeletonMatcher;
using static ArchiveFixer.Helpers.VolumeMarkerRules;
using static ArchiveFixer.Helpers.VolumeSegmentSplitter;

namespace ArchiveFixer.Helpers;

/// <summary>
/// **卷名重建**：从"名字最标准的那一卷"推名字前缀/名字干（<c>x.7z.001</c> ⇒ <c>x.7z</c> / <c>x</c>），
/// 把族后缀段归一（<c>7sz</c> / <c>7删z</c> ⇒ <c>7z</c>），并解析
/// <c>&lt;基名&gt;.&lt;卷标记&gt;.rar</c> 这一族的名字。
///
/// <para>⚠ <b>这不是基名算法</b>：基名只有两份（<c>FileNameHelper.FirstStepBaseName</c> /
/// <c>SecondStepBaseName</c>，第一个点之前）。本类回答的是"**写出来的名字该长什么样**"——
/// <c>VolumeGroupDetector</c> 的规范卷名与 <c>VolumeNameRepair</c> 的改名目标都用它；
/// 拿基名去拼规范名会把 <c>x.ra31415926535r</c> 改名成 <c>x.rar</c>（改错名字比不改更糟）。</para>
///
/// <para>⛔ 剥不动 / 判不出就**原样返回**（判不出 ⇒ 不动）：本类不碰盘、不改名、
/// 不做"要不要改名"的裁决（那在 <c>VolumeNameRepair</c> 那几道闸门上）。</para>
/// </summary>
internal static class VolumeNameRebuilder
{
    /// <summary>
    /// 名字里**卷标记之前**的那一段（保留族后缀段）：<c>x.7z.001</c> → <c>x.7z</c>、
    /// <c>x.7z.001.txt</c> → <c>x.7z</c>、<c>444.part1.rar</c> → <c>444</c>；
    /// 名字里没有卷标记 ⇒ 原名字本身（<c>x.zip</c> / <c>x.rar</c>）。
    ///
    /// <para>⚠ 这是**同族比较 / 名字重建**用的前缀，<b>不是基名</b>
    /// （基名 = 第一个点之前，只有 <c>FileNameHelper.FirstStepBaseName</c> /
    /// <c>SecondStepBaseName</c> 两份）。</para>
    /// </summary>
    public static string ResolveNamePrefix(string? fileName)
    {
        string name = FileNameHelper.GetFileName(fileName);

        if (name.Length == 0)
        {
            return string.Empty;
        }

        if (TrySplitPartNumberedVolume(name, out string partBase, out _, out _, out _, out _))
        {
            return partBase;
        }

        string[] parts = name.Split('.');

        for (int i = parts.Length - 1; i >= 1; i--)
        {
            if (TrySplitVolumeSegmentTolerant(parts[i], out _, out _))
            {
                return string.Join('.', parts, 0, i);
            }
        }

        return name;
    }

    /// <summary>
    /// 从"名字最标准的那一卷"推**名字前缀**（写出来的规范卷名里卷标记之前的那一段）：
    /// 从右往左一段一段剥 —— 卷号段（<c>01</c> / <c>001</c>）、卷标记段（<c>part1</c> / <c>z01</c> / <c>r00</c>）、
    /// 以及"本来就该是**该族**归档后缀"的那一段（<c>7z</c> / 只差一个字符的 <c>7</c>）。
    ///
    /// <para>⚠ 这是**名字重建**，不是基名算法：基名只有两份
    /// （<c>FileNameHelper.FirstStepBaseName</c> / <c>SecondStepBaseName</c>，第一个点之前）。
    /// <c>x.ra31415926535r</c> 的基名是 <c>x</c>（身份），但它**不是**这一族的第一卷 ——
    /// 拿基名去拼规范名会把它改名成 <c>x.rar</c>（改错名字比不改更糟）。</para>
    ///
    /// <para>⛔ 剥不动就停手（判不出就别拼名字）；调用方另行校验非法字符。</para>
    /// </summary>
    public static string ResolveNameStem(string? fileName, string? expectedArchiveExtension)
    {
        string name = FileNameHelper.GetFileName(fileName);
        string extension = (expectedArchiveExtension ?? string.Empty).Trim().TrimStart('.');

        if (name.Length == 0 || extension.Length == 0)
        {
            return string.Empty;
        }

        string[] parts = name.Split('.');
        int keep = parts.Length;

        while (keep >= 2)
        {
            string last = parts[keep - 1];

            if (IsOrdinalSegment(last)
                || TrySplitVolumeSegmentTolerant(last, out _, out _)
                || string.Equals(last, extension, StringComparison.OrdinalIgnoreCase)
                || IsUniqueOneEditAway(last, extension)
                || (TryRecoverDisguisedArchiveBody(last, out string recovered, out _)
                    && string.Equals(recovered, extension, StringComparison.OrdinalIgnoreCase)))
            {
                keep--;
                continue;
            }

            break;
        }

        return keep > 0 ? string.Join('.', parts, 0, keep) : name;
    }

    /// <summary>1~4 位纯数字段 = 像是被改烂的卷号（名字重建用，⛔ 不是基名判据）。</summary>
    private static bool IsOrdinalSegment(string segment)
    {
        if (segment.Length == 0 || segment.Length > 4)
        {
            return false;
        }

        foreach (char ch in segment)
        {
            if (ch < '0' || ch > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>这一段是不是"只差一个字符"就能变成**本族**后缀，而且**只有这一种变法**。</summary>
    private static bool IsUniqueOneEditAway(string segment, string extension)
    {
        string? unique = null;

        foreach (string known in KnownArchiveExtensions
            .Select(e => e.TrimStart('.'))
            .Where(name => name.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsOneEditAway(segment, known))
            {
                continue;
            }

            if (unique != null)
            {
                return false;
            }

            unique = known;
        }

        return unique != null && string.Equals(unique, extension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>两段之间差**恰好一个**字符（插入或删除 1 个字符）。</summary>
    private static bool IsOneEditAway(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) != 1)
        {
            return false;
        }

        string longer = a.Length > b.Length ? a : b;
        string shorter = a.Length > b.Length ? b : a;
        int i = 0;

        while (i < shorter.Length && char.ToLowerInvariant(longer[i]) == char.ToLowerInvariant(shorter[i]))
        {
            i++;
        }

        for (int j = i; j < shorter.Length; j++)
        {
            if (char.ToLowerInvariant(longer[j + 1]) != char.ToLowerInvariant(shorter[j]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 归一"名字前缀的**最后一段**"（族后缀段）：<c>x.7sz</c> → <c>x.7z</c>、<c>x.7删z</c> → <c>x.7z</c>、
    /// <c>333.78a8fuaz</c> → <c>333.7z</c>。
    ///
    /// <para>⚠ <b>这不是基名算法</b>：基名只有两份（<c>FileNameHelper.FirstStepBaseName</c> /
    /// <c>SecondStepBaseName</c>，第一个点之前）。它回答的是"**写出来的名字**该用哪个族后缀段" ——
    /// 名字重建（<c>VolumeGroupDetector</c> 的规范卷名 / <c>VolumeNameRepair</c> 的改名目标）需要它。
    /// 两份用途分开，⛔ 别把它当基名用、也别在别处再抄一份。</para>
    ///
    /// <para>两档判据（都只认**唯一**结论）：① 删掉 1 个字符后是不是已知归档后缀；
    /// ② 通用骨架命中（<see cref="ExtensionSkeletonMatcher.TryMatchKnownSkeleton"/>）。有歧义 ⇒ 原样返回。</para>
    /// </summary>
    public static string NormalizeFamilySegment(string prefix)
    {
        int lastDot = prefix.LastIndexOf('.');

        if (lastDot <= 0 || lastDot == prefix.Length - 1)
        {
            return prefix;
        }

        string segment = prefix[(lastDot + 1)..];

        if (IsKnownArchiveExtension("." + segment))
        {
            return prefix;
        }

        string? unique = null;

        for (int i = 0; i < segment.Length; i++)
        {
            string candidate = segment.Remove(i, 1);

            if (!IsKnownArchiveExtension("." + candidate))
            {
                continue;
            }

            if (unique != null)
            {
                return prefix; // 有歧义：宁可不归一
            }

            unique = candidate;
        }

        if (unique != null)
        {
            return prefix[..(lastDot + 1)] + unique;
        }

        if (TryMatchKnownSkeleton(
                segment,
                KnownArchiveExtensions.Select(e => e.TrimStart('.')),
                out string bySkeleton,
                out _))
        {
            return prefix[..(lastDot + 1)] + bySkeleton;
        }

        return prefix;
    }

    /// <summary>
    /// 拆开 <c>&lt;基名&gt;.&lt;卷标记&gt;.rar</c> 这一族 —— **卷标记与 rar 尾巴都可以粘少量垃圾**。
    ///
    /// <code>
    /// X.part1.rar      → 基名 X、卷标记 part1、尾巴 rar        （干净）
    /// X.part1.rar删除   → 基名 X、卷标记 part1、尾巴 rar删除     （垃圾粘在 .rar 上）
    /// 111.parts1.racr  → 基名 111、卷标记 parts1、尾巴 racr     （两边都夹垃圾）
    /// X.001.rar        → 基名 X、卷标记 001、尾巴 rar          （数字族同理）
    /// </code>
    ///
    /// <para><b>为什么必须只有这一份实现</b>（2026-10-03 真机）：这个形状原先在**三个地方各写过一遍**，
    /// 三处都硬编码了 <c>尾巴 == "rar"</c> ——
    /// <c>VolumeGroupDetector.Analyze</c>（卷序 / 归组）、
    /// 旧的剥卷标记副本（包基名 ⇒ 同组判定与落点）、
    /// <c>LegacyVolumeNames.IsVolumePart</c>（拦住会破坏分卷链的改名）。
    /// 尾巴一粘垃圾（网盘给每卷缀「删除」⇒ <c>X.part1.rar删除</c>），**三处同时失效**：
    /// 一组 4 卷被判成"四个基名互不相同的第 1 卷本体" —— 列表里四行、递归里四个分支、
    /// 引擎那边因为名字对不上而拼不起整组（现场见 <c>docs/真机事故复盘.md</c> §51）。</para>
    ///
    /// <para>判据（全部要成立，⛔ 不给"看着像"开口子）：① 末尾那段要么逐字是 <c>rar</c>、
    /// 要么按 <see cref="ExtensionSkeletonMatcher.TryRecoverDisguisedArchiveBody"/> **唯一地**还原成 <c>rar</c>；
    /// ② 倒数第二段要么逐字是合法卷标记、要么按 <see cref="VolumeSegmentSplitter.TrySplitVolumeSegmentTolerant"/>
    /// 唯一地还原成合法卷标记（<c>partN</c> / 三位数字）；③ 两段之前必须还有基名（⛔ 不许把名字吃光）。
    /// 两处"去杂质"都沿用既有那两把尺子（≤2 个多余字符、必须唯一），⛔ 这里不另立第三把。</para>
    /// </summary>
    /// <param name="baseName">去掉卷标记与 rar 尾巴的基名（磁盘上的写法，不含尾部的点）。</param>
    /// <param name="canonicalMark">还原后的规范卷标记（<c>part1</c> / <c>001</c>）。</param>
    /// <param name="index">卷序（1 起）。</param>
    /// <param name="tailSegment">磁盘上原样的尾巴段（<c>rar</c> 或 <c>rar删除</c>）—— 补缺失卷名时要用它。</param>
    /// <param name="disguised">卷标记或尾巴是不是"去杂质"才认出来的（名字不标准 ⇒ 不许当可删的本体名）。</param>
    public static bool TrySplitPartNumberedVolume(
        string? fileName,
        out string baseName,
        out string canonicalMark,
        out int index,
        out string tailSegment,
        out bool disguised)
    {
        baseName = string.Empty;
        canonicalMark = string.Empty;
        index = 0;
        tailSegment = string.Empty;
        disguised = false;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        string[] parts = fileName.Split('.');

        if (parts.Length < 3)
        {
            return false;
        }

        string tail = parts[^1];
        string mark = parts[^2];
        bool tailDisguised = false;

        if (!tail.Equals("rar", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryRecoverDisguisedArchiveBody(tail, out string recoveredTail, out _) ||
                !recoveredTail.Equals("rar", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            tailDisguised = true;
        }

        string canonical = mark;

        if (!IsVolumePartExtension("." + mark))
        {
            /*
             * ⛔ **"取最右"那一格**（用户 2026-10-04 拍板：「放开，但规定取最右边那个能自洽的卷标记」）：
             * 卷标记段走**通用骨架命中**（`pa8rt1` → `part1`），但**只对"尾巴逐字是 `rar`"这一档生效**。
             *
             * 为什么尾巴一脏就不吃骨架档：`pa8rt1`（该认成 `part1`）与 `p1art2`
             * （AAA 真夹具 `444.p1art2.ra3r` 的**基名段**，⛔ 不许认成卷标记）是**同一个形状** ——
             * 字母序列都是 `part`、都夹一个数字，任何按形状的判据都分不开。
             * 尾巴干净（`444.pa8rt1.rar`）时它是这一组**最右**的那个卷标记位置，
             * 认下来不会动到别人；尾巴是伪装的（`444.p1art2.ra3r`）时同一格会给出
             * "基名 `444`、卷标记 `part2`"—— 那一格是基名表第 64 行与 AAA 夹具的命根，照旧不认。
             * 尾巴伪装的**干净卷标记**（`444.pa删rt2.r除ar`）走的是上面那两档，一个字没动。
             */
            if (!TrySplitVolumeSegmentTolerant(
                    mark,
                    out canonical,
                    out _,
                    allowPartNumberedSkeleton: !tailDisguised) ||
                !IsVolumePartExtension("." + canonical))
            {
                return false;
            }
        }

        /*
         * 2026-10-25：`弦音sic NO.001 Alter EGO egoist [9P-92MB].rar` 这类名字里
         * `.001` 只是标题的一部分，不是卷标记。容错档认出 `001` 后，如果同一段
         * 还剩下 ASCII 字母或空格，就说明后面接着的是正文，不是「删除」这类后缀附加。
         * ⇒ 不认成 partN 分卷（标准名 `x.001删除.rar` 仍然只余中文，照旧认）。
         */
        string markLeftover = mark.StartsWith(canonical, StringComparison.OrdinalIgnoreCase)
            ? mark[canonical.Length..]
            : string.Empty;

        if (markLeftover.Any(char.IsWhiteSpace)
            || (markLeftover.Length > 2 && markLeftover.Any(char.IsAsciiLetter)))
        {
            return false;
        }

        /*
         * 卷号：partN 取 part 后面那串数字，数字族整段就是号码。
         * ⛔ 不用 int.Parse/LINQ：这里手算并卡上限，免得为一行逻辑多引一个 using。
         */
        string digits = canonical.StartsWith("part", StringComparison.OrdinalIgnoreCase)
            ? canonical[4..]
            : canonical;

        if (digits.Length == 0)
        {
            return false;
        }

        int parsed = 0;

        foreach (char c in digits)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }

            parsed = (parsed * 10) + (c - '0');

            if (parsed > 9999)
            {
                return false;
            }
        }

        if (parsed < 1)
        {
            return false;
        }

        string stem = string.Join('.', parts, 0, parts.Length - 2);

        if (stem.Length == 0)
        {
            return false;
        }

        baseName = stem;
        canonicalMark = canonical;
        index = parsed;
        tailSegment = tail;
        disguised = tailDisguised
                    || !string.Equals(canonical, mark, StringComparison.OrdinalIgnoreCase);
        return true;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using static ArchiveFixer.Helpers.ExtensionNameRules;

namespace ArchiveFixer.Helpers;

/// <summary>
/// **通用骨架命中**：已知后缀（或任意骨架词）的字符按顺序出现在某一段里、中间夹什么都当杂质，
/// 但**首尾必须对齐**、结果必须**唯一**。另含"归档本体后缀去杂质还原"
/// （<see cref="TryRecoverDisguisedArchiveBody"/>）。
///
/// <para><b>这里的"为什么"</b>：网盘会把中文塞进后缀内部（<c>222.zip</c> ⇒ <c>222.zi删除p</c>），
/// 只按"删 2 个字符"穷举会把 <c>7zip</c> 误读成 <c>7z</c>、把 <c>00c1x9</c> 读成卷标记 ⇒
/// 本类只认**顺序出现 + 首尾对齐 + 结果唯一**，判不出就不认（宁可不动）。</para>
///
/// <para>⛔ 判据只有这一份实现：归档后缀那一档、卷标记骨架那一档
/// （<see cref="VolumeMarkerSkeletonMatcher"/>）、归组键的后缀段归一
/// （<c>FileNameHelper.NormalizeArchiveExtensionSegment</c>）三处转调它。</para>
///
/// <para>⚠ 只回答"这一段去掉杂质之后是什么"，⛔ **不回答"该不该改名"** ——
/// 改名还要过同组自洽 / 尺寸规律 / 用户授权那几道闸门。</para>
/// </summary>
internal static class ExtensionSkeletonMatcher
{
    // ══════════════ 「通用骨架命中」——第三档（用户 2026-10-04 定稿）══════════════
    //
    // 用户原话：「我想要的是**你能够识别到伪装的后缀里面有 `7_______z.003` 的内容**，不是仅仅让你改
    // 这么简单的一个档」「出现其他的问题你也要弄啊，比如 `333.78a8fuaz.003`，这种情况下难道你就瘫痪了吗」。
    //
    // 判据（与既有两档**同一套纪律**：有界 + 结果唯一 + 判不出就不认）：
    //   · 已知骨架的字符序列在该段里**按顺序**出现就算命中，中间夹的任何字符一律当杂质剔除；
    //   · **首尾必须对齐**（骨架的第一个字符在段首、最后一个在段末）—— 杂质只能夹在**中间**，
    //     尾巴那一档照旧不猜（`0012` 是另一套位宽，`rarity` / `00c1x9` 的骨架没走到段末）；
    //   · 一段能命中**多个**不同骨架 ⇒ 判不出（`7zip` 同时命中 `7z` 与 `zip`）⇒ 不认；
    //   · 一次只看**一个**骨架的一次命中（⛔ 不做"删多处"的组合爆炸）—— 这就是"有界"。
    //
    // ⛔ 判据只有这一份实现：归档后缀那一档（<see cref="TryRecoverDisguisedArchiveBody"/>）、
    //    卷标记那一档（<see cref="TrySplitVolumeSegmentTolerant"/>）、归组键的后缀段归一
    //    （`FileNameHelper.NormalizeArchiveExtensionSegment`）**三处转调它**，⛔ 不许各写一份。

    /// <summary>
    /// 这一段是**归档本体后缀**、而且只有"多了几个字符"的差别时，把那个规范后缀还原出来。
    ///
    /// <code>
    /// zi删除p → (zip, "删除")     zscip → (zip, "sc")     z删除ip → (zip, "删除")
    /// ziɾ��p → (zip, ...)         7删除z → (7z, "删除")     zip → false（本来就是干净的）
    /// </code>
    ///
    /// <para><b>为什么必须有这一条</b>（2026-09-27 真机 `222.zscip` / 内层 `222.zi删除p`）：
    /// 网盘把中文塞进**后缀内部**是常规操作（`222.zip` → `222.zi删除p`），而底下那条
    /// <see cref="VolumeSegmentSplitter.TrySplitVolumeSegmentLoose"/> 的骨架档只把还原结果拿去比**分卷标记**
    /// （`.001`/`.z01`/`.r00`/`.partN`）—— `zip` 是**归档后缀**，于是当场被扔，
    /// 这个名字就退化成"一个没有卷标记的普通文件"，**基名被算成 `222.zscip`**，
    /// 整组改名之后产出的 `222.zscip.zip` 与归档内部记的 `222.zip` 对不上，
    /// 定稿闸门当场判"缺 `222.zip`"，整层作废（源包一个字节都没动，但用户的产物也没落地）。</para>
    ///
    /// <para>判据与容错档**同一套口径**：允许删掉**最多 2 个**"多余字符"，被删掉的字符
    /// **不全都是数字**（否则 `0012` 会被读成 `001`+`2`），而且**只有一种删法**时才返回
    /// —— 有歧义一律不认（宁可不动，AGENTS §9.5）。</para>
    ///
    /// <para>⛔ 这一步只回答"这个名字去掉杂质之后**是什么**"，**不回答"该不该改"** ——
    /// 真要改名字，调用方还得有兄弟卷佐证 / 尺寸规律 / 用户授权那几道。</para>
    /// </summary>
    public static bool TryRecoverDisguisedArchiveBody(
        string? segment,
        out string canonicalSegment,
        out string removedCharacters)
    {
        canonicalSegment = string.Empty;
        removedCharacters = string.Empty;

        if (string.IsNullOrWhiteSpace(segment))
        {
            return false;
        }

        string s = segment.Trim().TrimStart('.');

        if (s.Length == 0)
        {
            return false;
        }

        // 本来就是干净的归档后缀（`zip` / `rar`）：**不认**——这一档由调用方按原样处理。
        if (IsKnownArchiveExtension("." + s))
        {
            return false;
        }

        List<string> known = KnownArchiveExtensions
            .Select(e => e.TrimStart('.'))
            .Where(name => name.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        /*
         * ⛔ 约束①「**唯一**」（用户 2026-10-04 定稿）：一段能命中**多个**已知骨架 ⇒ **判不出 ⇒ 整段不认**
         * —— 连下面那两档老结论也不给。
         *
         * 反面例子就是用户点名的 `7zip`：它同时含 `7z` 与 `zip`。老的两档会靠"删掉两个字符"把它读成
         * `7z`，那正是"猜"而不是"判"（`7zip` 本来就是个常见后缀名）。有歧义 ⇒ 什么都不做。
         */
        if (CountSkeletonHits(s, known) > 1)
        {
            return false;
        }

        string? unique = null;
        string uniqueRemoved = string.Empty;
        bool ambiguous = false;

        // 删一个字符
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsAsciiDigit(s[i]))
            {
                continue; // 删掉的是数字 → 更像另一套位宽，不猜（与容错档同一条）
            }

            Consider(s.Remove(i, 1), s[i].ToString());

            if (ambiguous)
            {
                return false;
            }
        }

        // 删两个字符（`zi删除p` / `zscip` 都落在这里）
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsAsciiDigit(s[i]))
            {
                continue;
            }

            for (int j = i + 1; j < s.Length; j++)
            {
                if (char.IsAsciiDigit(s[j]))
                {
                    continue;
                }

                string removed = string.Concat(s[i], s[j]);

                if (removed.All(char.IsAsciiDigit))
                {
                    continue;
                }

                Consider(s.Remove(i, 1).Remove(j - 1, 1), removed);

                if (ambiguous)
                {
                    return false;
                }
            }
        }

        /*
         * 第三档（2026-10-04，用户拍板）：**通用骨架命中** —— `7_______z` → `7z`、`78a8fuaz` → `7z`、
         * `ra31415926535r` → `rar`。
         *
         * ⛔ 顺序不变：上面那两档（剔非字母数字 / 删 ≤2 个任意字符）是**更快更保守的前置档**，
         * 只有它们都判不出来，才轮到这一档（用户原话："保留原有两档……作为更快更保守的前置档，
         * 判断顺序不变"）。
         */
        if (unique == null &&
            TryMatchKnownSkeleton(s, known, out string bySkeleton, out string skeletonJunk))
        {
            unique = bySkeleton;
            uniqueRemoved = skeletonJunk;
        }

        if (unique == null)
        {
            return false;
        }

        canonicalSegment = unique;
        removedCharacters = uniqueRemoved;
        return true;

        void Consider(string candidate, string removed)
        {
            if (!known.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            if (unique != null)
            {
                /*
                 * 能删出两种规范后缀 ⇒ 有歧义 ⇒ 一律不认。
                 * 与容错档的不同：这里**必须是同一个后缀**才算歧义（`zi删除p` 删「删除」或「除p」
                 * 都得到 `zip`，那是同一种变法，不算歧义）。
                 */
                if (!string.Equals(unique, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    ambiguous = true;
                }

                return;
            }

            unique = candidate;
            uniqueRemoved = removed;
        }
    }

    /// <summary>
    /// **通用骨架命中**：<paramref name="knownSkeletons"/> 里的某一个，其字符在
    /// <paramref name="segment"/> 里**按顺序**出现（中间夹什么都算杂质）。
    ///
    /// <code>
    /// 7_______z → (7z, "_______")      78a8fuaz → (7z, "8a8fua")     ra31415926535r → (rar, "31415926535")
    /// 7zip      → false（同时命中 7z 与 zip ⇒ 判不出）        0012 → false（纯数字尾 ⇒ 不猜）
    /// rarity    → false（骨架没走到段末 ⇒ 不算命中）
    /// </code>
    ///
    /// <para>⛔ **命中只回答"这一段去掉杂质之后是什么"** —— 改不改名仍由调用方那几道闸门回答
    /// （同组形状自洽 / 尺寸规律 / 目标名没被占 / 硬链接试开 / 用户授权），本方法一个字都不改盘。</para>
    /// </summary>
    /// <param name="segment">待判的那一段（不含点）。</param>
    /// <param name="knownSkeletons">已知骨架（归档后缀名或卷标记名，带不带前导点都行）。</param>
    /// <param name="canonicalSegment">命中的规范骨架。</param>
    /// <param name="junk">被当成杂质剔掉的字符（按原顺序拼回）。</param>
    public static bool TryMatchKnownSkeleton(
        string? segment,
        IEnumerable<string>? knownSkeletons,
        out string canonicalSegment,
        out string junk)
    {
        canonicalSegment = string.Empty;
        junk = string.Empty;

        if (string.IsNullOrWhiteSpace(segment) || knownSkeletons == null)
        {
            return false;
        }

        string s = segment.Trim();

        if (s.Length == 0)
        {
            return false;
        }

        string? unique = null;
        string uniqueJunk = string.Empty;

        foreach (string? knownSegment in knownSkeletons)
        {
            string known = (knownSegment ?? string.Empty).Trim().TrimStart('.');

            // 单字符骨架（`.z`）不当骨架：一个字母到处都能命中，那是噪声不是证据。
            if (known.Length < 2 || known.Length > s.Length)
            {
                continue;
            }

            if (!TryMatchSubsequence(s, known, out int[] positions))
            {
                continue;
            }

            /*
             * ⛔ **首尾对齐**（用户口径是"**中间**夹的任何字符都当杂质剔除"）：骨架的第一个字符要在段首、
             * 最后一个字符要在段末 —— 杂质只可能是**中间**那些。
             *
             * 这一条同时替掉两条老红线的位置：尾巴那一档本来就不许猜（`0012` 更像另一套位宽），
             * 而"骨架没走到段末"的形状（`00c1x9` / `rarity` / `zipper`）也不再算命中 ——
             * 否则随便一个含 `rar` 三个字母的英文词都会被读成 RAR 本体。
             * 代价是"前缀夹垃圾"那一档不放开（`删除001` 那种由既有的一档管着，不受影响）。
             */
            if (positions[0] != 0 || positions[positions.Length - 1] != s.Length - 1)
            {
                continue;
            }

            if (unique != null)
            {
                // 两个不同骨架都能命中 ⇒ 判不出（`7zip`）⇒ 什么都不认。
                return false;
            }

            unique = known;
            uniqueJunk = BuildSkeletonJunk(s, positions);
        }

        if (unique == null)
        {
            return false;
        }

        canonicalSegment = unique;
        junk = uniqueJunk;
        return true;
    }

    /// <summary>贪心最左匹配：<paramref name="known"/> 的每个字符按顺序在 <paramref name="s"/> 里找第一个出现的位置。</summary>
    internal static bool TryMatchSubsequence(string s, string known, out int[] positions)
    {
        positions = new int[known.Length];
        int cursor = 0;

        for (int k = 0; k < known.Length; k++)
        {
            int found = -1;

            for (int i = cursor; i < s.Length; i++)
            {
                if (char.ToLowerInvariant(s[i]) == char.ToLowerInvariant(known[k]))
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

        return true;
    }

    /// <summary>把没被骨架用到的那些字符按原顺序拼回来（就是"杂质"）。</summary>
    internal static string BuildSkeletonJunk(string s, int[] positions)
    {
        var junk = new System.Text.StringBuilder(s.Length - positions.Length);
        int cursor = 0;

        foreach (int position in positions)
        {
            for (int i = cursor; i < position; i++)
            {
                junk.Append(s[i]);
            }

            cursor = position + 1;
        }

        for (int i = cursor; i < s.Length; i++)
        {
            junk.Append(s[i]);
        }

        return junk.ToString();
    }

    /// <summary>
    /// 这一段能命中**几个**不同的已知骨架（0 / 1 / ≥2）—— 约束①「唯一」那条的判据。
    /// 只要发现第二个就立刻返回（不需要精确计数）。
    /// </summary>
    private static int CountSkeletonHits(string s, List<string> known)
    {
        int hits = 0;

        foreach (string skeleton in known)
        {
            if (skeleton.Length < 2 || skeleton.Length > s.Length)
            {
                continue;
            }

            if (!TryMatchSubsequence(s, skeleton, out _))
            {
                continue;
            }

            hits++;

            if (hits > 1)
            {
                return hits;
            }
        }

        return hits;
    }
}

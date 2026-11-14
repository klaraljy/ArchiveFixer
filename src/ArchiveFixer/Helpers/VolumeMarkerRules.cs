using System;
using System.Linq;
using static ArchiveFixer.Helpers.ExtensionNameRules;

namespace ArchiveFixer.Helpers;

/// <summary>
/// **卷标记与杂质的逐字判据**（两把基础尺子，各只有一份）：
/// <see cref="IsVolumePartExtension"/>（什么算合法卷标记）与
/// <see cref="LooksLikeInsertedJunk"/>（什么算"塞进去的垃圾"）。
///
/// <para><b>为什么单独一层</b>：卷段拆分（<see cref="VolumeSegmentSplitter"/>）与卷标记骨架
/// （<see cref="VolumeMarkerSkeletonMatcher"/>）都要问"这算不算卷标记"，而卷名重建在更上层
/// —— 把尺子放在任何一边都会让另一边反向依赖。这里只放**逐字形状**判据，⛔ 不含
/// "删几个字符""骨架命中""拉长卷号"那些容错档（它们在拆分器与骨架类里）。</para>
///
/// <para>⛔ 判据只有这一份：拆分 / 骨架 / 卷名重建三层都转调这里，不许各写一份。</para>
/// </summary>
internal static class VolumeMarkerRules
{
    /// <summary>
    /// 判断一个扩展名是否是"分卷标记"。
    ///
    /// 覆盖：
    ///   .001 ~ .999  —— 7z / zip / rar 的 .001 分卷
    ///   .z01 ~ .z99  —— zip 分卷
    ///   .r00 ~ .r99  —— rar 老式分卷
    ///   .part1 / .part01 / .part001 —— rar 新式分卷的一段（如 xxx.part1.rar）
    ///
    /// 注意：这里只回答"这一段是不是分卷标记"，不做分卷组识别。
    /// 完整的分卷组识别（谁和谁是一组、缺哪一卷）见 AGENTS.md §9.3，M2 落地。
    /// </summary>
    public static bool IsVolumePartExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        string ext = extension.Trim().TrimStart('.');

        if (ext.Length == 0)
        {
            return false;
        }

        // .001 ~ .999
        if (ext.Length == 3 && ext.All(char.IsDigit))
        {
            return true;
        }

        // .z01 / .r00
        if (ext.Length == 3 &&
            (ext[0] == 'z' || ext[0] == 'Z' || ext[0] == 'r' || ext[0] == 'R') &&
            char.IsDigit(ext[1]) &&
            char.IsDigit(ext[2]))
        {
            return true;
        }

        // .part1 / .part01 / .part001
        if (ext.Length >= 5 &&
            ext.StartsWith("part", StringComparison.OrdinalIgnoreCase) &&
            ext.Skip(4).All(char.IsDigit))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 这一段是不是"**塞进去的垃圾**"（而不是一个正常的词）？
    ///
    /// <para>判据两条，都是**结构**判据，⛔ 不设"最多几个字符"这种上限：</para>
    /// <list type="number">
    /// <item><description>至少含一个**非字母非数字**的字符（符号 / 中文 / 其它）——
    /// 纯 ASCII 字母/数字凑出来的前缀更像正常的名字；</description></item>
    /// <item><description>**不许出现 ≥3 个连续 ASCII 字母** —— `photo-123` / `backup-002`
    /// 这种真名字不能被吃进来（真机语料里这种名字很常见）。</description></item>
    /// </list>
    ///
    /// <para>空串 = 不是"垃圾前缀"（那是前三档的地盘）。</para>
    /// </summary>
    internal static bool LooksLikeInsertedJunk(string junk)
    {
        if (junk.Length == 0)
        {
            return false;
        }

        /*
         * ⛔ 2026-10-13（困难批 `1111` 取证）：**"垃圾"整个就是归档本体后缀**时，它不是塞进去的垃圾，
         * 而是这一份自己的后缀段 —— 把它吃掉等于**把这一份的后缀丢掉**。
         *
         * <para>现场逐字（盘上事实）：`111-L2.7z-924`（造包器撞名时缀的随机数）被读成
         * "基名 `111-L2` 的第 **924** 片" ⇒ 还原工序照那个读法把盘上那份改名成 `111-L2.924`
         * —— `7z` 那一段没了 ⇒ 那一组的三片再也对不上号。`X.zip-002` / `X.rar-1` 同理。</para>
         *
         * <para>⛔ 只挡**归档本体后缀**（`zip` / `rar` / `7z` / …）；⛔ 不挡卷标记形状的 `.001` / `.z`
         * —— 那两种既可能是垃圾、也可能是卷标记本身，老口径照旧。</para>
         */
        if (IsArchiveBodyExtensionToken(junk))
        {
            return false;
        }

        bool hasNonAlphaNumeric = false;
        int consecutiveLetters = 0;

        foreach (char c in junk)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                hasNonAlphaNumeric = true;
            }

            if (char.IsAsciiLetter(c))
            {
                consecutiveLetters++;

                if (consecutiveLetters >= 3)
                {
                    return false;
                }
            }
            else
            {
                consecutiveLetters = 0;
            }
        }

        return hasNonAlphaNumeric;
    }

    /// <summary>
    /// 这一段（**剔掉尾部非字母数字之后**）是不是**归档本体后缀**（`zip` / `rar` / `7z` / …）？
    ///
    /// <para>判据只有这一处，消费方只有 <see cref="LooksLikeInsertedJunk"/> 开头那一条
    /// （"垃圾整个就是归档后缀 ⇒ 它不是垃圾"）。⛔ 卷标记形状的 `.001` / `.z` **不算**
    /// —— 那两种既可能是塞进去的垃圾、也可能是卷标记本身，老口径照旧。</para>
    /// </summary>
    private static bool IsArchiveBodyExtensionToken(string junk)
    {
        int end = junk.Length;

        while (end > 0 && !char.IsAsciiLetterOrDigit(junk[end - 1]))
        {
            end--;
        }

        if (end < 2)
        {
            return false;
        }

        string token = "." + junk[..end];

        return !string.Equals(token, ".001", StringComparison.OrdinalIgnoreCase)
            && KnownArchiveExtensions.Contains(token);
    }
}

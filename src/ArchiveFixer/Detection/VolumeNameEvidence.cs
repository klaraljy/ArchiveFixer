using ArchiveFixer.Helpers;
using System;
using System.Globalization;
using System.Linq;

namespace ArchiveFixer.Detection;

/// <summary>
/// 名字证据 —— 从一个文件名读出"这是哪一族、第几卷、基名是什么"（分卷组识别的唯一解析入口）。
///
/// 认的命名（左列是第 1 卷，右列是后续卷）：
///
/// <code>
/// volume.7z.001   →  .002 / .003 …        基名 volume.7z
/// archive.001     →  .002 / .003 …        基名 archive
/// x.zip           →  x.z01 / x.z02 …      基名 x（本体就是第 1 卷）
/// x.rar           →  x.r00 / x.r01 …      基名 x（本体就是第 1 卷）
/// x.part1.rar     →  x.part2.rar …        基名 x
/// </code>
///
/// <para><b>卷序是"绝对卷序"，不是"后缀里的数字"</b>。<c>x.r00</c> 解析成 2 而不是 1：
/// rar 老式分卷里 <c>x.rar</c> 才是第 1 卷，<c>r00</c> 只是"第 1 个后续卷"，
/// 所以 <c>rNN</c> 的卷序是 NN + 2，<c>zNN</c> 的卷序是 NN + 1（zip 分卷的本体是 <c>x.zip</c>）。
/// 这个偏移写错，后果是永远报"缺第 1 卷"或把两卷当成一卷。</para>
///
/// <para><b>为什么独立成层</b>：解析是"证据收集"的第一站 —— 归组、缺卷清单、名字重建三条路
/// 都只许从这里拿族/卷序/基名。<b>判据一个字都没改</b>：这里只是把原来内嵌在
/// <see cref="VolumeGroupDetector"/> 里的解析原样搬出来，分支次序、容错档、闸门全部逐行保留。</para>
///
/// <para><b>失败模型</b>：认不出 / 末段为空 / 没有点段 / 容错档也认不出 ⇒ <c>null</c>
/// （判不出就判不出，⛔ 不猜）。粘在卷号后面的"垃圾"只认后缀附加，正文标题一律不认
/// （见 <see cref="IsSuffixOnlyJunk"/>）。</para>
///
/// <para>生命周期：纯静态、无状态；只读名字字符串，不查存在性、不读内容、不碰文件系统。</para>
/// </summary>
internal static class VolumeNameEvidence
{
    /// <summary>解析一个文件名（可以传完整路径，内部只取文件名部分）。不是分卷/本体返回 null。</summary>
    public static VolumeNameInfo? Analyze(string? fileNameOrPath)
    {
        string fileName = FileNameHelper.GetFileName(fileNameOrPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        string[] parts = fileName.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        string last = parts[^1];
        if (last.Length == 0)
        {
            return null;
        }

        // ① <基名>.<分卷段>.rar：分卷段后面还挂着一个 .rar 尾巴（x.part1.rar）。
        //    这里只认 partN 与三位数字两种分卷段 —— x.z01.rar / x.r00.rar 不是真实存在的写法，
        //    与其猜，不如让它落到 ③ 的"普通 rar 本体"分支（那样不会成组）。
        //
        //    ⚠ 2026-10-03（真机 §51）：**尾巴上粘垃圾的也算**（`X.part1.rar删除` —— 网盘给每卷缀「删除」）。
        //    老写法把 `last == "rar"` 写死在这一行，于是这种名字掉到 ③b 被当成"本体后缀被塞了垃圾"，
        //    基名算成 `X.part1`、卷序 1 ⇒ 一组 4 卷各自成一组（列表四行、递归四个分支）。
        //    判据转调**唯一出口** `ExtensionHelper.TrySplitPartNumberedVolume` —— 与
        //    `LegacyVolumeNames.IsVolumePart` 那处**同一份**。
        if (ExtensionHelper.TrySplitPartNumberedVolume(
                fileName,
                out string partBase,
                out string partMark,
                out int partIndex,
                out string partTail,
                out bool partDisguised))
        {
            bool markIsPartNumbered = partMark.StartsWith("part", StringComparison.OrdinalIgnoreCase);

            return new VolumeNameInfo
            {
                BaseName = partBase,
                Family = markIsPartNumbered ? VolumeNameFamily.PartNumbered : VolumeNameFamily.Numeric,
                Index = partIndex,
                DigitWidth = markIsPartNumbered ? partMark.Length - 4 : partMark.Length,
                Tail = "." + partTail,
                IsDisguised = partDisguised
            };
        }

        // ② 末尾一段自己就是分卷标记：volume.7z.001 / archive.001 / x.z01 / x.r00 / x.part1。
        //    注意 volume.7z.001.txt 走不到这里（它末段是 txt），这正是要的效果：
        //    改坏后缀的文件不再被当成同一组的分卷。
        //    ⚠ 但"分卷标记后面**粘着**垃圾"的（volume.7z.001删除，百度网盘给每卷缀「删除」）算分卷：
        //    不算的话整组会散成几个独立压缩包，一键处理改名时会把 .001 这一段吃掉（2026-09-28 事故）。
        //    垃圾尾巴记进 Tail，小组时用它补出来的"缺失卷名"才跟磁盘上的名字对得上。
        // 末尾一段是分卷标记：先按已有两档解析；解析不出来再试**容错**档
        // （用户 2026-09-28 第三次真机：`amb909.7sz.00c1` —— 干扰字符塞进卷号内部、而且是字母）。
        // ⛔ 容错档只多认"删少量非纯数字字符后是合法卷标记"，认不出就照旧 null（宁可不动）。
        bool isVolumeSegment = TryParseVolumeSegment(last, out VolumeNameFamily family, out int index, out int digitWidth, out string junkTail);

        /*
         * 这一段是**靠容错档**（删掉 1~2 个字符）才认出来的（`z0sc1` → `z01`）——
         * 它算分卷成员，但**名字不是规范名**：尺寸规律那一道不许拿它当"满卷"基准
         * （真机 222 那一组里 `222.z0sc1` 与本体等大，拿它当满卷会让整桶被尺寸规律判死）。
         */
        bool volumeSegmentByTolerance = false;

        if (!isVolumeSegment &&
            ExtensionHelper.TrySplitVolumeSegmentTolerant(last, out string tolerantMark, out string tolerantJunk) &&
            TryParseVolumeSegment(tolerantMark, out family, out index, out digitWidth, out _))
        {
            // 容错档的垃圾是**夹在卷号内部**的（`00c1` 的 `c`）—— 补卷名时要用**干净的标准名**
            // （`amb909.7z.001`），所以这里不把垃圾当尾巴传下去（尾巴是给"粘在末尾"那种用的）。
            _ = tolerantJunk;
            junkTail = string.Empty;
            isVolumeSegment = true;
            volumeSegmentByTolerance = true;
        }

        if (isVolumeSegment)
        {
            if (!IsSuffixOnlyJunk(junkTail))
            {
                isVolumeSegment = false;
            }
        }

        if (isVolumeSegment)
        {
            string baseName = JoinBaseName(parts, 1);
            if (baseName.Length > 0)
            {
                return new VolumeNameInfo
                {
                    BaseName = baseName,
                    Family = family,
                    Index = index,
                    DigitWidth = digitWidth,
                    Tail = junkTail,
                    IsDisguised = junkTail.Length > 0 || volumeSegmentByTolerance,
                    HasCanonicalName = junkTail.Length == 0 && !volumeSegmentByTolerance
                };
            }
        }

        // ②b 卷标记后面还挂着别的点段：volume.7z.001.txt / archive.rar.001.bak
        //     （用户 2026-09-28 追加：不能只认"标记必须是最后一段"）。
        //     尾巴原样记进 Tail，补缺失卷名时才能拼回 `…001.txt` 这个名字。
        if (parts.Length >= 3 && !ExtensionHelper.IsKnownArchiveExtension("." + last))
        {
            for (int i = parts.Length - 2; i >= 1; i--)
            {
                if (!TryParseVolumeSegment(parts[i], out VolumeNameFamily tailFamily2, out int tailIndex2, out int tailWidth2, out string tailJunk2))
                {
                    continue;
                }

                if (!IsSuffixOnlyJunk(tailJunk2))
                {
                    continue;
                }

                /*
                 * ⚠ 与 FileNameHelper 里那条**同一道闸门**（2026-09-28）：只有"卷标记**紧跟在已知压缩后缀后面**"
                 * 才算跨段伪装（`x.7z.001.txt` / `x.rar.001.bak`）。不加这条，`rar-android-722.132.apk`
                 * 会被算成"卷 132 + .apk 尾巴"（`TryGetFirstVolumeName` 推出 `rar-android-722.001.apk`）——
                 * 落点与改名那两条路另有闸门不受影响，但**探测器口径必须一致**，否则迟早再冒出一条边角。
                 */
                if (i < 1 || !ExtensionHelper.IsKnownArchiveExtension("." + parts[i - 1]))
                {
                    continue;
                }
                /*
                 * ⚠ 只检查**卷标记右边**的那几段，别去查卷标记自己 —— `.001` 本身就在
                 * KnownArchiveExtensions 里（老代码把它当"压缩包后缀"收进去了），
                 * 拿它当判据会把 `.001.txt` 这种刚刚要支持的名字全挡掉（实测踩到过）。
                 */
                bool tailsArePlain = true;

                for (int j = i + 1; j < parts.Length; j++)
                {
                    if (ExtensionHelper.IsKnownArchiveExtension("." + parts[j]))
                    {
                        tailsArePlain = false;
                        break;
                    }
                }

                if (!tailsArePlain)
                {
                    continue;
                }

                string tailBase = JoinBaseName(parts, parts.Length - i);
                if (tailBase.Length == 0)
                {
                    continue;
                }

                return new VolumeNameInfo
                {
                    BaseName = tailBase,
                    Family = tailFamily2,
                    Index = tailIndex2,
                    DigitWidth = tailWidth2,
                    Tail = "." + string.Join('.', parts, i + 1, parts.Length - i - 1),
                    IsDisguised = true
                };
            }
        }

        // ③ x.zip / x.rar 本体：单看名字就是本族的第 1 卷（所以 TryGetVolumeIndex 返回 1），
        //    但"算不算分卷组"由 Group() 决定：要有同族的 .z01 / .r00 才算。
        if (last.Equals("zip", StringComparison.OrdinalIgnoreCase) || last.Equals("rar", StringComparison.OrdinalIgnoreCase))
        {
            string baseName = JoinBaseName(parts, 1);
            if (baseName.Length > 0)
            {
                return new VolumeNameInfo
                {
                    BaseName = baseName,
                    Family = last.Equals("zip", StringComparison.OrdinalIgnoreCase)
                        ? VolumeNameFamily.ZipSpanned
                        : VolumeNameFamily.RarOld,
                    Index = 1,
                    IsBody = true,
                    HasCanonicalName = true
                };
            }
        }

        /*
         * ③b **本体后缀被塞了垃圾**（`222.zi删除p` / `222.zscip`）—— 用户 2026-09-27 真机。
         *
         * 现场：外层跨盘 zip 解出来的两个条目叫 `222.zi删除p` 与 `222.z0sc1`（网盘把中文塞进后缀），
         * 这一支**以前根本不存在** ⇒ `222.zi删除p` 被判成"一个没有卷标记的普通文件"，
         * 归组时基名算成 `222.zscip`（去掉最后一段），整组改名产出 `222.zscip.zip`，
         * 与归档内部记的 `222.zip` 对不上 ⇒ 定稿闸门判"缺 `222.zip`"、整层作废。
         *
         * 判据只有一条（唯一出口 <see cref="ExtensionHelper.TryRecoverDisguisedArchiveBody"/>）：
         * 去掉最多 2 个非数字字符后**唯一地**变成本族的规范后缀。还原出来的名字照旧算
         * "第 1 卷本体"，但 <see cref="VolumeNameInfo.HasCanonicalName"/> = false ——
         * 它够资格**参与归组**，不够资格被当成"名字标准、可以删"（那一条只由
         * <c>VolumeGroupResolver</c> 按磁盘上的真名字回答）。
         */
        if (!ExtensionHelper.IsVolumeSegment(last) &&
            ExtensionHelper.TryRecoverDisguisedArchiveBody(last, out string bodySuffix, out _) &&
            (bodySuffix.Equals("zip", StringComparison.OrdinalIgnoreCase)
             || bodySuffix.Equals("rar", StringComparison.OrdinalIgnoreCase)))
        {
            string bodyBaseName = JoinBaseName(parts, 1);

            if (bodyBaseName.Length > 0)
            {
                return new VolumeNameInfo
                {
                    BaseName = bodyBaseName,
                    Family = bodySuffix.Equals("zip", StringComparison.OrdinalIgnoreCase)
                        ? VolumeNameFamily.ZipSpanned
                        : VolumeNameFamily.RarOld,
                    Index = 1,
                    IsBody = true,
                    IsDisguised = true,
                    IsDisguisedBody = true,
                    HasCanonicalName = false
                };
            }
        }

        return null;
    }

    /// <summary>
    /// 卷标记后面粘的"垃圾"只能是后缀附加（删除 / 括号 / 数字等），不能是 ASCII 正文。
    /// 2026-10-25：`弦音sic NO.001 Alter EGO egoist [9P-92MB].rar` 的 ` Alter EGO...`
    /// 是标题正文，不是「删除」；这条闸门把它挡在分卷组之外。
    /// </summary>
    private static bool IsSuffixOnlyJunk(string? junk)
    {
        if (string.IsNullOrEmpty(junk))
        {
            return true;
        }

        foreach (char ch in junk)
        {
            if (char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        return junk.Length <= 2 || !junk.Any(char.IsAsciiLetter);
    }

    /// <summary>
    /// 解析"末尾那一段"属于哪一族、是第几卷，以及**粘在卷号后面的垃圾尾巴**。
    ///
    /// 先用 <see cref="ExtensionHelper.TrySplitVolumeSegment"/> 把段拆成「分卷标记 + 垃圾」：
    /// "什么算分卷标记"只留一份定义，将来它扩了（比如补上新的记法），这里不会漏；
    /// 但它只回答"是不是、垃圾是什么"，"是哪一族、第几卷"还得自己拆 ——
    /// 这才是卷序偏移最容易写错的地方。
    /// </summary>
    private static bool TryParseVolumeSegment(
        string segment,
        out VolumeNameFamily family,
        out int index,
        out int digitWidth,
        out string junkTail)
    {
        family = VolumeNameFamily.Numeric;
        index = 0;
        digitWidth = 1;
        junkTail = string.Empty;

        if (!ExtensionHelper.TrySplitVolumeSegmentLoose(segment, out string mark, out string junk))
        {
            return false;
        }

        junkTail = junk;

        // .z01 ~ .z99：zip 分卷的**后续**卷。z01 前面还有本体 x.zip，所以卷序 = NN + 1。
        if (mark.Length == 3
            && (mark[0] == 'z' || mark[0] == 'Z')
            && TryParseTwoDigits(mark.AsSpan(1), out int zipOrdinal)
            && zipOrdinal >= 1)
        {
            family = VolumeNameFamily.ZipSpanned;
            index = zipOrdinal + 1;
            return true;
        }

        // .r00 ~ .r99：rar 老式分卷的后续卷。r00 是"第 1 个后续卷"、本体 x.rar 才是第 1 卷，
        // 所以卷序 = NN + 2（这里 0 是合法序号，不能像 zip 那样拒掉）。
        if (mark.Length == 3
            && (mark[0] == 'r' || mark[0] == 'R')
            && TryParseTwoDigits(mark.AsSpan(1), out int rarOrdinal))
        {
            family = VolumeNameFamily.RarOld;
            index = rarOrdinal + 2;
            return true;
        }

        // .001 ~ .999：数字本身就是卷序。
        if (mark.Length == 3 && IsAsciiDigits(mark.AsSpan()))
        {
            if (!TryParseVolumeNumber(mark, out index))
            {
                return false;
            }

            family = VolumeNameFamily.Numeric;
            digitWidth = 3;
            return true;
        }

        // .partN / .partNN：卷序就是 N（part1 本身就是第一卷）。
        if (mark.Length >= 5
            && mark.StartsWith("part", StringComparison.OrdinalIgnoreCase)
            && IsAsciiDigits(mark.AsSpan(4)))
        {
            if (!TryParseVolumeNumber(mark[4..], out index))
            {
                return false;
            }

            family = VolumeNameFamily.PartNumbered;
            digitWidth = mark.Length - 4;
            return true;
        }

        junkTail = string.Empty;
        return false;
    }

    /// <summary>
    /// 解析卷号。用 <see cref="NumberStyles.None"/> 且要求 ≥ 1：
    /// 卷号只能是纯数字（挡掉 "+1"、"1 "、"١" 这类写法），
    /// 而且 <c>x.000</c> / <c>x.part0</c> 这种没有意义的"第 0 卷"一律不算分卷
    /// —— 认了它们，缺卷检测就会开始报不存在的名字。
    /// </summary>
    private static bool TryParseVolumeNumber(string text, out int index)
    {
        index = 0;

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index >= 1;
    }

    /// <summary>解析恰好两位的十进制序号（<c>z01</c>/<c>r00</c> 的数字部分），允许 0。</summary>
    private static bool TryParseTwoDigits(ReadOnlySpan<char> span, out int value)
    {
        value = 0;

        if (span.Length != 2 || !IsAsciiDigits(span))
        {
            return false;
        }

        return int.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// 只认 ASCII 数字。<see cref="char.IsDigit(char)"/> 会放过阿拉伯-印度数字之类的字符，
    /// 那些字符经 int.Parse 又解析不出来，用它当条件会得到"看着像分卷但卷号是 0"的怪结果。
    /// </summary>
    private static bool IsAsciiDigits(ReadOnlySpan<char> span)
    {
        if (span.Length == 0)
        {
            return false;
        }

        foreach (char ch in span)
        {
            if (ch < '0' || ch > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 拼出**名字前缀**（写出来的规范卷名里"卷标记之前"的那一段）：把末尾 <paramref name="trailingCount"/> 段
    /// 之外的片段拼起来，再归一**最后一段的族后缀**（<c>x.7sz</c> → <c>x.7z</c>）。
    ///
    /// <para>⚠ <b>这不是基名</b>：基名只有两份（<c>FileNameHelper.FirstStepBaseName</c> /
    /// <c>SecondStepBaseName</c>，第一个点之前），用于归组/落点/匹配；
    /// 这里回答的是"<see cref="VolumeNameInfo.TryFormat"/> 该往哪个前缀后面接卷标记" ——
    /// <c>set.7z(删掉.001</c> 的前缀必须是 <c>set.7z</c>（否则写出来是 <c>set.001</c>，7-Zip 打不开）。
    /// 归一化只有一份实现（<see cref="ExtensionHelper.NormalizeFamilySegment"/>）。</para>
    /// </summary>
    private static string JoinBaseName(string[] parts, int trailingCount)
    {
        int count = parts.Length - trailingCount;
        if (count <= 0)
        {
            return string.Empty;
        }

        string joined = string.Join(".", parts, 0, count);

        /*
         * 前缀不 Trim：文件名里的首尾空格是真的会改变归组的字符。
         * 但"全是空白"的前缀（例如文件名叫 ".zip"）没有意义，直接不认。
         */
        string prefix = ExtensionHelper.NormalizeFamilySegment(joined);

        return string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix;
    }
}

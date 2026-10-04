using System;
using System.IO;
using System.Linq;
using System.Text;

namespace ArchiveFixer.Helpers
{
    /// <summary>
    /// 基名的**档位** —— <see cref="FileNameHelper.TryResolveVolumeBaseName"/> 的入参。
    ///
    /// <para><b>为什么要有档位</b>（2026-10-03 阶段 A 收口）：基名原先散在 **5 处各写一份** ——
    /// <c>VolumeGroupDetector.JoinBaseName</c>（:956）、<c>FileNameHelper.StripVolumeMarkers</c>（:270）、
    /// <c>OutputPlacement.ResolveArchiveBaseName</c>（:627）、<c>VolumeNameRepair.TrySplitDisguised</c>（:1618）、
    /// <c>VolumeNumberFromContent.TryDeriveStem</c>（:402）。这 5 处问的**不是同一个问题**，
    /// 但"哪一段是基名的末尾"这套规则只准有一份 —— 于是把规则收进
    /// <see cref="FileNameHelper.TryResolveVolumeBaseName"/>，每处按自己的问题选一个档。</para>
    ///
    /// <para>⛔ 收口这一轮**只搬家、不改结论**：每一档的行为与它原来那一处逐字相同
    /// （对照表见 <c>ArchiveBaseNameTests</c>）。要合并这些档 = 改变既有结论，得先拍板。</para>
    /// </summary>
    public enum VolumeBaseNameLevel
    {
        /// <summary>
        /// **归组键的归一化**（<c>VolumeGroupDetector.JoinBaseName</c>，归组用）。
        /// 输入是"调用方**已经剥掉卷标记段**的那段正文"（哪个点段是卷标记段，由归组那边自己的分支回答）。
        /// 只做两件事：剥掉粘在归档后缀段上的垃圾（<c>amb909.7z删除</c> → <c>amb909.7z</c>）、
        /// 把后缀段里夹的杂质归一（<c>x.7sz</c> → <c>x.7z</c>）—— 同一组各卷的基名必须逐字相等，
        /// 否则同一组会散成两组（2026-09-28 真机）。
        /// </summary>
        VolumeGroupKey,

        /// <summary>
        /// **剥法出口**（<c>FileNameHelper.StripVolumeMarkers</c>）：剥掉末尾的卷标记段、**保留**归档后缀段
        /// （<c>222.7z.001</c> → <c>222.7z</c>）。
        /// ⚠ 跨段伪装那一档（<c>x.7z.001.txt</c>）**连归档后缀段一起剥**（→ <c>x</c>）—— 调用方随后还要自己剥一层后缀；
        /// ⚠ 刻意**不归一化杂质**：<c>VolumeNameRepair</c> 拿它比"兄弟卷基名是否逐字相等"，多归一一步会放宽那道闸门。
        /// </summary>
        StripMarkers,

        /// <summary>
        /// **包基名**（<c>OutputPlacement.ResolveArchiveBaseName</c>，落点 / 包名用）：
        /// 在 <see cref="StripMarkers"/> 之上再剥掉归档后缀段（<c>222.7z.001</c> → <c>222</c>、
        /// <c>222.rar.jpg</c> → <c>222</c>）。
        /// </summary>
        PackageName,

        /// <summary>
        /// **锚点基名**（<c>VolumeNumberFromContent.TryDeriveStem</c>）：从"名字最标准的那一卷"
        /// （7z / RAR 取第 1 卷、跨盘 zip 取末片）推 —— 除卷标记段与**该族**归档后缀段之外，
        /// 还剥 1~4 位纯数字段（<c>amb909.7.01</c> → <c>amb909</c>）。要传 <c>expectedArchiveExtension</c>。
        /// </summary>
        AnchorStem,

        /// <summary>
        /// **伪装卷名拆解**（<c>VolumeNameRepair.TrySplitDisguised</c>）：拆出（基名、规范卷标记段），
        /// 改名目标名要用它拼（<c>giu910.7z.001删除</c> → 基名 <c>giu910.7z</c> + 卷段 <c>001</c>）。
        /// ⚠ 与 <see cref="VolumeGroupKey"/> 只差一道闸门：这里"标记后面还挂着点段"那一档**不要求**
        /// 标记紧跟在已知归档后缀后面（那道闸门是 2026-09-28 真机事故后**只加在归组那一处**的）——
        /// ⛔ 本轮照原样保留，顺手加会改变结论。
        /// </summary>
        DisguisedVolume,

        /// <summary>
        /// **归档基名**（<c>FileNameHelper.GetArchiveBaseName</c> / <c>GetSafeArchiveBaseName</c>）：
        /// 在 <see cref="StripMarkers"/> 之上**天真剥一层后缀**（<c>Path.GetFileNameWithoutExtension</c>
        /// 那一刀；<c>.tar.gz</c> 一族按已知组合多剥几段）。
        ///
        /// <para><b>用途</b>：给"同一个包的不同成员"当**匹配键** —— <c>EngineRouter</c> 找"抠出来的内嵌归档
        /// 与源包同名"、<c>RecursiveExtractor</c> 判"这一份是不是内组那一组的后续卷"、
        /// <c>RestVolumeCompletenessGate</c> 判"其余物里的片与成品里的片是不是同一组"。</para>
        ///
        /// <para>⚠ <b>它与 <see cref="PackageName"/>（包基名，只剥**已知归档后缀**）故意不是一回事</b>
        /// （2026-10-04 探针实测 + <c>VolumeBaseNameTests</c> 逐格钉住）：
        /// <c>movie.2024</c> → <c>movie</c>（本档）vs <c>movie.2024</c>（包基名）；
        /// <c>222.rar.jpg</c> → <c>222.rar</c>（本档）vs <c>222</c>（包基名）；
        /// <c>444.p1art2.part2.rar</c> → <c>444</c>（本档）vs <c>444.p1art2</c>（包基名）。
        /// ⛔ **合并成一个值会改结论**：最要命的一格是脏本体名（<c>222.zscip</c> / <c>一只顶美.z删除ip</c>）——
        /// 本档把它与 <c>222.z01</c> 归一成同一个基名（那道"半套不删"的防数据丢失闸门就靠这一格），
        /// 包基名那一档不归一 ⇒ 闸门当场放行。合并要用户拍板，⛔ 不许顺手统一。</para>
        /// </summary>
        ArchiveBaseName
    }

    /// <summary>
    /// 文件名处理工具。
    /// </summary>
    public static class FileNameHelper
    {
        /// <summary>
        /// 获取文件名。
        /// </summary>
        public static string GetFileName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFileName(path);
            }
            catch
            {
                return path;
            }
        }

        /// <summary>
        /// 获取不带最后一个后缀的文件名。
        /// </summary>
        public static string GetFileNameWithoutExtension(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFileNameWithoutExtension(path);
            }
            catch
            {
                return GetFileName(path);
            }
        }

        /// <summary>
        /// 获取目录路径。
        /// </summary>
        public static string GetDirectoryName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetDirectoryName(path) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 清理非法文件名字符。
        /// </summary>
        public static string SanitizeFileName(string? fileName, string replacement = "_")
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "未命名";
            }

            string safeReplacement = replacement ?? "_";
            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(fileName.Length);

            foreach (char ch in fileName)
            {
                if (Array.IndexOf(invalidChars, ch) >= 0)
                {
                    builder.Append(safeReplacement);
                }
                else
                {
                    builder.Append(ch);
                }
            }

            string result = builder.ToString().Trim();

            if (string.IsNullOrWhiteSpace(result))
            {
                result = "未命名";
            }

            result = TrimEndingDotsAndSpaces(result);

            if (IsReservedDeviceName(result))
            {
                result = "_" + result;
            }

            return result;
        }

        /// <summary>
        /// 清理非法路径片段。
        /// </summary>
        public static string SanitizePathPart(string? name)
        {
            return SanitizeFileName(name);
        }

        /// <summary>
        /// 去掉 Windows 不允许的结尾点和空格。
        /// </summary>
        public static string TrimEndingDotsAndSpaces(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "未命名";
            }

            string result = name.TrimEnd(' ', '.');

            if (string.IsNullOrWhiteSpace(result))
            {
                return "未命名";
            }

            return result;
        }

        /// <summary>
        /// 判断是否是 Windows 保留设备名。
        /// </summary>
        public static bool IsReservedDeviceName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            string fileName = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();

            return fileName is
                "CON" or
                "PRN" or
                "AUX" or
                "NUL" or
                "COM1" or
                "COM2" or
                "COM3" or
                "COM4" or
                "COM5" or
                "COM6" or
                "COM7" or
                "COM8" or
                "COM9" or
                "LPT1" or
                "LPT2" or
                "LPT3" or
                "LPT4" or
                "LPT5" or
                "LPT6" or
                "LPT7" or
                "LPT8" or
                "LPT9";
        }

        /// <summary>
        /// 生成压缩包基础名。
        /// 会处理 .tar.gz / .tar.bz2 / .tar.xz，以及**分卷标记**
        /// （<c>222.7z.001</c> / <c>222.zip.001</c> / <c>222.part1.rar</c> / <c>222.z01</c> / <c>222.r00</c>
        /// 一律得到 <c>222</c>）。
        ///
        /// <para>
        /// ⚠️ 分卷这一步是**必修**（2026-09-21 用户在真实产物里看到的目录就是 <c>17274362.7z\</c>）。
        /// 旧实现只剥**一层**后缀：<c>222.7z.001</c> → <c>222.7z</c>、<c>222.part1.rar</c> → <c>222.part1</c>，
        /// 于是分卷包的落点变成 <c>111\222.7z\</c>，分卷组里每一卷还会各建一个目录 ——
        /// 用户的原话是"你给我多弄了四个文件夹、文件一多根本就分不清"。
        /// </para>
        ///
        /// <para>
        /// "什么算分卷标记"只有一份定义（<see cref="ExtensionHelper.IsVolumePartExtension"/>），
        /// 剥法也只有一份实现（<see cref="StripVolumeMarkers"/>），本方法只负责在剥完之后照旧剥一层普通后缀。
        /// （<c>Extraction.OutputPlacement.ResolveArchiveBaseName</c> 有一份更严格的同类实现：
        /// 它只剥**已知归档后缀**，用于落点公式；它现在也转调 <see cref="StripVolumeMarkers"/>，
        /// 两者对分卷的剥法**是同一段代码**。）
        /// </para>
        /// </summary>
        public static string GetArchiveBaseName(string filePath)
        {
            string fileName = GetFileName(filePath);

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "未命名";
            }

            /*
             * ⚠ 2026-10-04 收口：本方法原先**自己**跑一遍"剥分卷标记 + 天真剥一层后缀"（`StripVolumeMarkers`
             * 之后再来一刀 `Path.GetFileNameWithoutExtension`）—— 与 `OutputPlacement.ResolveArchiveBaseName`
             * 那一档并排放在仓库里，两个都叫"包基名"却对同一串名字给出不同的值（探针实测 3 组不等）。
             * 现在整条规则搬进**唯一基名出口**的 `ArchiveBaseName` 档，本方法只转调（判据一个字没改，
             * `ArchiveBaseNameTests` 53 行逐字钉住）。
             *
             * ⛔ 这里**不是**把两档合成一个：`ArchiveBaseName`（本方法，匹配键）与 `PackageName`
             * （包基名，落点用）是两个用途，合并会改结论（含一道防数据丢失闸门）—— 见枚举上那两条注释。
             */
            return TryResolveVolumeBaseName(fileName, VolumeBaseNameLevel.ArchiveBaseName, out string baseName, out _)
                   && baseName.Length > 0
                ? baseName
                : fileName;
        }

        // ══════════════════ 唯一基名出口（阶段 A 收口，2026-10-03）══════════════════
        //
        // 三族的基名规则（方案《分卷族系统化识别》§1 ②），**全项目只在这里实现一次**：
        //   · RAR（partN 族）：基名 = **partN 段之前的所有点段**（444.p1art2.part2.rar → 444.p1art2）。
        //     ⛔ 不是".rar 前面那段"、⛔ 不是"第一个点之前那段"。
        //   · 7z / ZIP：锚点名字剥掉**卷标记段 + 归档后缀段**（111.7z.001 → 111）。
        //   · 跨盘 ZIP：末片名字剥掉 .zip（那一档走 StripMarkers / PackageName，与 7z 同一条路）。
        //
        // ⛔ 判据只转调既有那几把尺子（TrySplitPartNumberedVolume / TrySplitVolumeSegmentTolerant /
        //    TryRecoverDisguisedArchiveBody），这里**不新造第三把**（方案 §3 第 5 条）。
        // ⛔ 5 个消费点全部转调本方法，⛔ 不许在任何别处再算一遍基名。

        /// <summary>
        /// **唯一基名出口**：给一个文件名（或路径、或一段已经剥掉卷标记段的正文），按
        /// <paramref name="level"/> 算出基名。
        ///
        /// <para>档位为什么不止一个、每一档与别档差在哪，逐条写在
        /// <see cref="VolumeBaseNameLevel"/> 上（⛔ 那几条差异都是**今天就有**的，本轮一个字没改）。</para>
        ///
        /// <para>各档与旧实现的对应关系（对照用例 <c>ArchiveBaseNameTests</c> 逐个钉住）：
        /// <list type="bullet">
        /// <item><description><see cref="VolumeBaseNameLevel.VolumeGroupKey"/> ← <c>VolumeGroupDetector.JoinBaseName</c>（:956）</description></item>
        /// <item><description><see cref="VolumeBaseNameLevel.StripMarkers"/> ← <c>FileNameHelper.StripVolumeMarkers</c>（:270）</description></item>
        /// <item><description><see cref="VolumeBaseNameLevel.PackageName"/> ← <c>OutputPlacement.ResolveArchiveBaseName</c>（:627）</description></item>
        /// <item><description><see cref="VolumeBaseNameLevel.AnchorStem"/> ← <c>VolumeNumberFromContent.TryDeriveStem</c>（:402）</description></item>
        /// <item><description><see cref="VolumeBaseNameLevel.DisguisedVolume"/> ← <c>VolumeNameRepair.TrySplitDisguised</c>（:1618）</description></item>
        /// </list></para>
        /// </summary>
        /// <param name="fileName">文件名或路径（内部只取文件名）。</param>
        /// <param name="level">问的是哪个问题。</param>
        /// <param name="baseName">算出来的基名（判不出时是空串）。</param>
        /// <param name="canonicalSegment">规范化后的卷标记段 —— 只有 <see cref="VolumeBaseNameLevel.DisguisedVolume"/> 档会填，其余档一律空串。</param>
        /// <param name="expectedArchiveExtension">该族的规范归档后缀（<c>7z</c> / <c>zip</c> / <c>rar</c>）；<see cref="VolumeBaseNameLevel.AnchorStem"/> 档必填。</param>
        public static bool TryResolveVolumeBaseName(
            string? fileName,
            VolumeBaseNameLevel level,
            out string baseName,
            out string canonicalSegment,
            string? expectedArchiveExtension = null)
        {
            baseName = string.Empty;
            canonicalSegment = string.Empty;

            string name = GetFileName(fileName);

            if (name.Length == 0)
            {
                return false;
            }

            switch (level)
            {
                case VolumeBaseNameLevel.VolumeGroupKey:
                    baseName = NormalizeVolumeGroupKey(name);
                    return baseName.Length > 0;

                case VolumeBaseNameLevel.StripMarkers:
                    baseName = StripVolumeMarkersCore(name);
                    return baseName.Length > 0;

                case VolumeBaseNameLevel.PackageName:
                    baseName = StripArchiveSuffixSegments(StripVolumeMarkersCore(name));
                    return baseName.Length > 0;

                case VolumeBaseNameLevel.AnchorStem:
                    return TryResolveAnchorStem(name, expectedArchiveExtension, out baseName);

                case VolumeBaseNameLevel.DisguisedVolume:
                    return TryResolveDisguisedVolume(name, out baseName, out canonicalSegment);

                case VolumeBaseNameLevel.ArchiveBaseName:
                    return TryResolveArchiveBaseName(name, out baseName);

                default:
                    return false;
            }
        }

        /// <summary>
        /// 归档基名（<see cref="VolumeBaseNameLevel.ArchiveBaseName"/> 的档体）：原先整段写在
        /// <see cref="GetArchiveBaseName"/> 里（2026-10-04 按 §9.5 搬进唯一出口，**判据一个字没改**）。
        /// </summary>
        private static bool TryResolveArchiveBaseName(string fileName, out string baseName)
        {
            baseName = string.Empty;

            string stripped = StripVolumeMarkersCore(fileName);
            string lower = stripped.ToLowerInvariant();

            if (lower.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                baseName = stripped[..^7];
                return baseName.Length > 0;
            }

            if (lower.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase))
            {
                baseName = stripped[..^8];
                return baseName.Length > 0;
            }

            if (lower.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase))
            {
                baseName = stripped[..^7];
                return baseName.Length > 0;
            }

            if (lower.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                baseName = stripped[..^4];
                return baseName.Length > 0;
            }

            if (lower.EndsWith(".tbz2", StringComparison.OrdinalIgnoreCase))
            {
                baseName = stripped[..^5];
                return baseName.Length > 0;
            }

            if (lower.EndsWith(".txz", StringComparison.OrdinalIgnoreCase))
            {
                baseName = stripped[..^4];
                return baseName.Length > 0;
            }

            string withoutExt = Path.GetFileNameWithoutExtension(stripped);

            baseName = string.IsNullOrWhiteSpace(withoutExt) ? stripped : withoutExt;

            return baseName.Length > 0;
        }

        /// <summary>
        /// 归组键的归一化（<see cref="VolumeBaseNameLevel.VolumeGroupKey"/> 的档体）。
        /// 顺序要紧：先剥"粘在后缀段上的垃圾"，再把后缀段内部夹的杂质归一 ——
        /// 两条都与旧实现 <c>VolumeGroupDetector.JoinBaseName</c> 逐字相同。
        /// </summary>
        private static string NormalizeVolumeGroupKey(string rawBaseName) =>
            NormalizeArchiveExtensionSegment(StripJunkAfterArchiveExtension(rawBaseName));

        /// <summary>
        /// 把"压缩后缀后面粘着垃圾"的最后一段清干净：<c>x.7z删除</c> → <c>x.7z</c>；<c>y.rar副本</c> → <c>y.rar</c>。
        /// 段里没有已知压缩后缀、或后缀后面还跟着字母数字的，一律原样返回（宁可不动，也不乱剪）。
        ///
        /// <para>原在 <c>VolumeGroupDetector</c>（:1033），2026-10-03 收口时搬进唯一出口；判据一个字没改。</para>
        /// </summary>
        private static string StripJunkAfterArchiveExtension(string baseName)
        {
            int lastDot = baseName.LastIndexOf('.');

            if (lastDot <= 0 || lastDot == baseName.Length - 1)
            {
                return baseName;
            }

            string segment = baseName[(lastDot + 1)..];
            int letterCount = 0;

            while (letterCount < segment.Length && char.IsAsciiLetterOrDigit(segment[letterCount]))
            {
                letterCount++;
            }

            if (letterCount == 0 || letterCount == segment.Length)
            {
                // 整段都是字母数字（`7z` / `132`）：要么本来就是干净后缀，要么根本不是"后缀+垃圾"
                return baseName;
            }

            string extension = "." + segment[..letterCount];

            if (!ExtensionHelper.IsKnownArchiveExtension(extension) &&
                !ExtensionHelper.IsVolumePartExtension(extension))
            {
                return baseName;
            }

            return baseName[..(lastDot + 1 + letterCount)];
        }

        /// <summary>
        /// 把基名最后一段里"夹在压缩后缀**内部**的垃圾"归一：<c>x.7sz</c> → <c>x.7z</c>、<c>x.7删z</c> → <c>x.7z</c>。
        ///
        /// <para>为什么需要（用户 2026-09-28 第三次真机）：两卷分别被伪装成 <c>amb909.7sz.00c1</c> 与
        /// <c>amb909.7删z.00除2</c>，后缀段里各塞了一个字符 → 基名成了 <c>amb909.7sz</c> / <c>amb909.7删z</c>，
        /// **两个基名不同 → 同一组两卷散成两组**。这里只做"删 1 个字符后是不是已知压缩后缀"，
        /// 候选**唯一**才认（有歧义就不动）。</para>
        ///
        /// <para>原在 <c>VolumeGroupDetector</c>（:991），2026-10-03 收口时搬进唯一出口；判据一个字没改。</para>
        /// </summary>
        private static string NormalizeArchiveExtensionSegment(string baseName)
        {
            int lastDot = baseName.LastIndexOf('.');

            if (lastDot <= 0 || lastDot == baseName.Length - 1)
            {
                return baseName;
            }

            string segment = baseName[(lastDot + 1)..];

            if (ExtensionHelper.IsKnownArchiveExtension("." + segment))
            {
                return baseName;
            }

            string? unique = null;

            for (int i = 0; i < segment.Length; i++)
            {
                string candidate = segment.Remove(i, 1);

                if (!ExtensionHelper.IsKnownArchiveExtension("." + candidate))
                {
                    continue;
                }

                if (unique != null)
                {
                    return baseName; // 有歧义：宁可不归一
                }

                unique = candidate;
            }

            if (unique != null)
            {
                return baseName[..(lastDot + 1)] + unique;
            }

            /*
             * 第三档（2026-10-04，用户拍板）：**通用骨架命中** —— `333.78a8fuaz` → `333.7z`、
             * `x.7_______z` → `x.7z`（用户原话要的就是"识别到伪装的后缀里面有 `7_______z` 的内容"）。
             *
             * ⛔ 判据转调**唯一出口** <see cref="ExtensionHelper.TryMatchKnownSkeleton"/>：
             * 骨架规则、唯一性、纯数字尾那三条红线都只有那一份，这里不再写第二份。
             * ⛔ 顺序：排在既有那道"删 1 个字符"**之后**（前两档更快更保守，用户口径：判断顺序不变）。
             */
            if (ExtensionHelper.TryMatchKnownSkeleton(
                    segment,
                    ExtensionHelper.KnownArchiveExtensions.Select(e => e.TrimStart('.')),
                    out string bySkeleton,
                    out _))
            {
                return baseName[..(lastDot + 1)] + bySkeleton;
            }

            return baseName;
        }

        /// <summary>
        /// 剥掉末尾的归档后缀（<c>.rar</c> / <c>.7z</c> / <c>.zip</c> / <c>.tar.gz</c> …），最多 3 段。
        ///
        /// <para>两条经验规则：
        /// ① 只剥**已知归档后缀**。用 <c>Path.GetFileNameWithoutExtension</c> 无脑剥会把
        ///    <c>movie.2024</c> 变成 <c>movie</c> —— 数字结尾的名字太常见了；
        /// ② 若第一段剥掉的是**伪装后缀**（<c>222.rar.jpg</c>），允许继续剥下一段，
        ///    但只在"还没剥到归档后缀"时允许一次，避免把 <c>movie.mkv.rar</c> 的名字特征也啃掉。</para>
        ///
        /// <para>原在 <c>OutputPlacement</c>（私有 <c>StripArchiveExtensions</c>，:688），
        /// 2026-10-03 收口时搬进唯一出口（<see cref="VolumeBaseNameLevel.PackageName"/> 档的第二刀）；判据一个字没改。</para>
        /// </summary>
        private static string StripArchiveSuffixSegments(string name)
        {
            string current = name;
            bool strippedArchiveExtension = false;

            for (int guard = 0; guard < 3; guard++)
            {
                int lastDot = current.LastIndexOf('.');

                if (lastDot <= 0)
                {
                    break;
                }

                string extension = "." + current[(lastDot + 1)..];

                if (ExtensionHelper.IsKnownArchiveExtension(extension))
                {
                    current = current[..lastDot];
                    strippedArchiveExtension = true;
                    continue;
                }

                if (!strippedArchiveExtension && ExtensionHelper.IsSuspiciousFakeExtension(extension))
                {
                    current = current[..lastDot];
                    continue;
                }

                break;
            }

            return current;
        }

        /// <summary>
        /// 锚点基名（<see cref="VolumeBaseNameLevel.AnchorStem"/> 的档体）：从右往左一段一段剥 ——
        /// 卷号段（<c>01</c> / <c>001</c>）、卷标记段（<c>part1</c> / <c>z01</c> / <c>r00</c>）、
        /// 以及"本来就该是**该族**归档后缀"的那一段（<c>7z</c> / 只差一个字符的 <c>7</c>）。
        ///
        /// <para>⛔ 剥不动就停手 —— 基名只影响"改完的名字像不像人写的"，正确性由卷号与"绝不覆盖"两条钉着。</para>
        ///
        /// <para>原在 <c>VolumeNumberFromContent.TryDeriveStem</c>（:402），2026-10-03 收口时搬进唯一出口；
        /// 判据一个字没改（含 2026-09-27 真机补的 <c>222.zscip</c> 那一条）。</para>
        /// </summary>
        private static bool TryResolveAnchorStem(string fileName, string? expectedArchiveExtension, out string stem)
        {
            stem = string.Empty;

            string extension = (expectedArchiveExtension ?? string.Empty).Trim().TrimStart('.');

            if (fileName.Length == 0 || extension.Length == 0)
            {
                return false;
            }

            string[] parts = fileName.Split('.');
            int keep = parts.Length;

            while (keep >= 2)
            {
                string last = parts[keep - 1];

                if (IsOrdinalSegment(last)
                    || ExtensionHelper.TrySplitVolumeSegmentTolerant(last, out _, out _)
                    || string.Equals(last, extension, StringComparison.OrdinalIgnoreCase)
                    || IsUniqueOneEditAway(last, extension)
                    || (ExtensionHelper.TryRecoverDisguisedArchiveBody(last, out string recovered, out _)
                        && string.Equals(recovered, extension, StringComparison.OrdinalIgnoreCase)))
                {
                    keep--;
                    continue;
                }

                break;
            }

            stem = keep > 0 ? string.Join('.', parts, 0, keep) : fileName;

            return stem.Length > 0 && stem.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        /// <summary>
        /// 伪装卷名拆解（<see cref="VolumeBaseNameLevel.DisguisedVolume"/> 的档体）：
        /// <c>giu910.7z.001删除</c> → 基名 <c>giu910.7z</c>、标准卷段 <c>001</c>；
        /// <c>x.7z.001.txt</c> → 基名 <c>x.7z</c>、卷段 <c>001</c>；<c>y.z0删除3</c> → 基名 <c>y</c>、<c>z03</c>。
        ///
        /// <para>原在 <c>VolumeNameRepair.TrySplitDisguised</c>（:1618），2026-10-03 收口时搬进唯一出口；
        /// 判据与那三道闸门（⓪ 只接尾巴真脏 / ① 末段自己带垃圾 / ② 标记后面还挂着点段）一个字没改。</para>
        /// </summary>
        private static bool TryResolveDisguisedVolume(string fileName, out string baseName, out string canonicalSegment)
        {
            baseName = string.Empty;
            canonicalSegment = string.Empty;

            string[] parts = fileName.Split('.');

            if (parts.Length < 2)
            {
                return false;
            }

            /*
             * ⓪ <基名>.partN.rar（**卷标记与 rar 尾巴都可以粘垃圾**）：目标名必须**连 `.rar` 一起**保留。
             *
             * ⛔ 老写法把它当"标记后面挂着别的点段"处理，只把卷标记接回去 ⇒ 目标名算成 `X.part1`，
             * 等于把 RAR 的族后缀吃掉：改完 7-Zip / UnRAR 更打不开这一组（改名是**不可逆**动作，
             * 比"一个都不改"更糟）。判据与归组 / 包基名转调**同一份**
             * <see cref="ExtensionHelper.TrySplitPartNumberedVolume"/>。
             */
            if (ExtensionHelper.TrySplitPartNumberedVolume(
                    fileName,
                    out string partBase,
                    out string partMark,
                    out _,
                    out string partTail,
                    out _)
                && !partTail.Equals("rar", StringComparison.OrdinalIgnoreCase))
            {
                /*
                 * ⚠ 只接"**尾巴确实粘着垃圾**"那一档（`X.part1.rar删除`，网盘缀的「删除」）。
                 *
                 * 尾巴干净的（`X.part1.rar` / `444.pa删rt2.rar`）**必须留给下面既有那两条路**：
                 * 真机夹具（AAA）里 `444.p1art2.ra3r` 那一组，卷标记前面还有一段属于基名的
                 * `p1art2` —— 这里若抢着按"倒数第二段就是卷标记"去拆，基名会被算成 `444`、
                 * 改名产出 `444.part1.rar`（错），而正解是 `444.p1art2.part1.rar`。
                 * 实测：抢这一档 ⇒ `AaaReplayPipelineTests.夹具验收_一组分卷只解一次…` 当场变红
                 * （全量回归逮到，已收窄）。
                 */
                baseName = partBase;
                canonicalSegment = partMark + ".rar";
                return true;
            }

            // ① 最后一段自己就是"带垃圾的卷标记"（001删除 / 删除001 / z0删除3）
            if (!ExtensionHelper.IsVolumePartExtension("." + parts[^1]) &&
                ExtensionHelper.TrySplitVolumeSegmentTolerant(parts[^1], out string mark, out _))
            {
                baseName = NormalizeArchiveExtensionSegment(string.Join('.', parts, 0, parts.Length - 1));
                canonicalSegment = mark;
                return baseName.Length > 0;
            }

            // ② 卷标记后面还挂着别的点段（x.7z.001.txt）
            //    ⚠ 别去查卷标记自己是不是"已知压缩后缀" —— `.001` 本身就在那份名单里，
            //      拿它当闸门会把 `.001.txt` 全挡掉（实测踩到过）。
            for (int i = parts.Length - 2; i >= 1; i--)
            {
                if (!ExtensionHelper.TrySplitVolumeSegmentTolerant(parts[i], out string innerMark, out _))
                {
                    continue;
                }

                bool onlyPlainTails = true;

                for (int j = i + 1; j < parts.Length; j++)
                {
                    if (ExtensionHelper.IsKnownArchiveExtension("." + parts[j]))
                    {
                        onlyPlainTails = false;
                        break;
                    }
                }

                if (!onlyPlainTails)
                {
                    continue;
                }

                baseName = NormalizeArchiveExtensionSegment(string.Join('.', parts, 0, i));
                canonicalSegment = innerMark;
                return baseName.Length > 0;
            }

            return false;
        }

        /// <summary>1~4 位纯数字段 = 像是被改烂的卷号（原 <c>VolumeNumberFromContent</c> :1226）。</summary>
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

        /// <summary>这一段是不是"只差一个字符"就能变成**本族**后缀，而且**只有这一种变法**（原 <c>VolumeNumberFromContent</c> :1249）。</summary>
        private static bool IsUniqueOneEditAway(string segment, string extension)
        {
            string? unique = null;

            foreach (string known in ExtensionHelper.KnownArchiveExtensions
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

        /// <summary>两段之间差**恰好一个**字符（插入或删除 1 个字符）（原 <c>VolumeNumberFromContent</c> :1275）。</summary>
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

        // ══════════════════ 唯一出口的转调点（⛔ 不许在这里再算一遍基名）══════════════════

        /// <summary>
        /// 剥掉文件名末尾的**分卷标记**：<c>222.7z.001</c> → <c>222.7z</c>、
        /// <c>222.part1.rar</c> → <c>222</c>、<c>222.z01</c> → <c>222</c>、<c>222.r00</c> → <c>222</c>。
        ///
        /// <para>判据与实现都在唯一出口（<see cref="VolumeBaseNameLevel.StripMarkers"/> 档，档体
        /// <see cref="StripVolumeMarkersCore"/>），本方法只负责"判不出就原样返回"。
        /// ⚠ <b>剥法只有这一处</b>（体检报告 §2 第 2 条）：<c>Extraction.OutputPlacement</c> 的包基名
        /// 也走同一个出口（<see cref="VolumeBaseNameLevel.PackageName"/> 档）——
        /// 归档基名与包基名对同一个包算出不同的名字，落点公式就会指向两个不同的目录。</para>
        /// </summary>
        public static string StripVolumeMarkers(string fileName) =>
            TryResolveVolumeBaseName(fileName, VolumeBaseNameLevel.StripMarkers, out string baseName, out _)
                ? baseName
                : fileName;

        /// <summary>
        /// <see cref="VolumeBaseNameLevel.StripMarkers"/> 档的档体（原 <c>StripVolumeMarkers</c> 的循环，
        /// 2026-10-03 收口时原样搬进来）。
        ///
        /// 判据只有一份：<see cref="ExtensionHelper.IsVolumePartExtension"/>（.001~.999 / .z01 / .r00 / .partN）。
        /// 循环有守卫（最多 3 轮）：<c>.part1.rar</c> 这种"分卷段后面还挂着 .rar"的名字要连剥两次，
        /// 同时保证不会在 <c>222.rar</c> 上把 <c>222</c> 当成三位数字分卷段吃光整个名字。
        /// </summary>
        private static string StripVolumeMarkersCore(string fileName)
        {
            string current = fileName;

            for (int guard = 0; guard < 3; guard++)
            {
                int lastDot = current.LastIndexOf('.');

                if (lastDot <= 0)
                {
                    break;
                }

                string tail = current[(lastDot + 1)..];

                // ⚠ 用宽松判据（TrySplitVolumeSegmentLoose）而不是老的前缀版：卷标记里/前后夹垃圾的
                // （百度网盘那种 `222.7z.001删除`、`222.7z.删除001`、`222.z0删除3`）也算分卷标记，
                // **连同垃圾一起剥掉** —— 不剥的话 GetArchiveBaseName 会把 `001删除` 当"普通后缀"剥，
                // 算出来的基名对着整个包，改名时就会把 `.001` 这一段吃掉（2026-09-28 真机事故的根因）。
                if (ExtensionHelper.TrySplitVolumeSegmentTolerant(tail, out _, out _))
                {
                    current = current[..lastDot];
                    continue;
                }

                // xxx.part1.rar：分卷段在倒数第二段上，光看最后一段（.rar）看不出来。
                // ⚠ 必须要求分卷段**前面还有内容**（previousDot > 0），否则 "222.rar" 里的 "222"
                // 会被当成三位数字分卷段，整个名字被吃光。
                //
                // ⚠ 2026-10-03（真机 §51）：**尾巴粘垃圾的也要剥**（`X.part1.rar删除`，网盘给每卷缀「删除」），
                // 标记里夹垃圾的（`111.parts1.rar`）同理。判据转调唯一出口
                // `TrySplitPartNumberedVolume`（"前面还有内容"那一条它内含），
                // ⛔ 这里不再自己写一遍"最后一段是不是 rar"——那正是同一个形状被写了三遍、
                // 三处一起失效的那一格。
                //
                // ⛔ 位置必须在下面"跨段伪装"那段搜索**之前**：那段搜索会一边找一边改 `lastDot`，
                // 等它跑完，`lastDot` 已经不是"最后一个点"了 —— 实测踩到过：挪到 `stripped:` 之后，
                // `previousDot` 算成 -1 ⇒ 一个名字都剥不掉（名字不变）。
                if (ExtensionHelper.TrySplitPartNumberedVolume(current, out _, out _, out _, out _, out _))
                {
                    int previousDot = current.LastIndexOf('.', lastDot - 1);

                    if (previousDot > 0)
                    {
                        current = current[..previousDot];
                        continue;
                    }
                }

                // 卷标记后面还挂着别的点段（`x.7z.001.txt`）：从右往左找到"紧跟在压缩后缀后的卷标记"，
                // 把标记及其右边全剥掉。判据与 IsVolumePartFileName 里那条**同一份**。
                if (tail.Length > 0 && !ExtensionHelper.IsKnownArchiveExtension("." + tail))
                {
                    int markDot = current.LastIndexOf('.', Math.Max(0, lastDot - 1));

                    while (markDot > 0)
                    {
                        int beforeDot = current.LastIndexOf('.', Math.Max(0, markDot - 1));

                        if (beforeDot > 0 &&
                            ExtensionHelper.IsKnownArchiveExtension(current[beforeDot..markDot]) &&
                            ExtensionHelper.TrySplitVolumeSegmentTolerant(current[(markDot + 1)..lastDot], out _, out _))
                        {
                            current = current[..beforeDot];
                            goto stripped;
                        }

                        lastDot = markDot;
                        markDot = beforeDot;
                    }
                }

            stripped:

                break;
            }

            return current;
        }

        /// <summary>
        /// 判断是否为多重伪装文件名。
        /// </summary>
        public static bool IsMultiExtensionSuspicious(string fileName, string suggestedExtension)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            // 分卷不是伪装。
            // 例：volume.7z.001 的最后一段是分卷标记，不是"伪装上去的图片后缀"，
            // 不能报成"多重后缀疑似伪装"（设计.md §七 要求分卷单独归类，M2 补"分卷后缀"状态）。
            if (IsVolumePartFileName(fileName))
            {
                return false;
            }

            suggestedExtension = ExtensionHelper.NormalizeExtension(suggestedExtension);

            if (string.IsNullOrWhiteSpace(suggestedExtension))
            {
                return false;
            }

            if (!ExtensionHelper.HasMultipleExtensions(fileName))
            {
                return false;
            }

            string lastExt = ExtensionHelper.GetLastExtension(fileName);

            if (string.Equals(lastExt, suggestedExtension, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return ExtensionHelper.ContainsExtension(fileName, suggestedExtension);
        }

        /// <summary>
        /// 判断文件名是否是分卷的一部分。
        ///
        /// 例：
        ///   volume.7z.001 / data.zip.002   —— .001 分卷
        ///   movie.part1.rar                —— rar 新式分卷
        ///   old.rar / old.r00              —— rar 老式分卷
        ///   x.z01                          —— zip 分卷
        ///
        /// 用途：**拦住会破坏分卷链的改名**。7z 只认 ".001" 这一套命名，
        /// 一旦把 volume.7z.001 改成 volume.7z，整个分卷组就解不开了。
        /// </summary>
        public static bool IsVolumePartFileName(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            string[] parts = fileName.Split('.');

            if (parts.Length < 2)
            {
                return false;
            }

            // xxx.001 / xxx.z01 / xxx.part1
            // ⚠ 两处都放宽（用户 2026-09-28 第二轮）：
            //   ① 卷标记里/前后夹垃圾的（`xxx.7z.001删除`、`xxx.7z.删除001`、`xxx.z0删除3`）也算；
            //   ② **卷标记后面还挂着别的点段**的（`xxx.7z.001.txt`、`xxx.rar.001.bak`）也算 ——
            //      只要那些尾段**不是已知压缩后缀**。
            //   不算的话，这类名字会被当成"普通包"，改名时把卷号吃掉、整条分卷链断掉。
            if (ExtensionHelper.TrySplitVolumeSegmentTolerant(parts[^1], out _, out _))
            {
                return true;
            }

            if (IsVolumeMarkFollowedByNonArchiveTail(parts))
            {
                return true;
            }

            // xxx.part1.rar
            // ⚠ 2026-10-03（真机 §51）：尾巴粘垃圾的（`xxx.part1.rar删除`）与标记里夹垃圾的
            // （`111.parts1.rar`）同属这一族 —— 判据同样转调唯一出口，⛔ 不再自己写一遍
            // "最后一段是不是 rar"（三处一起失效的那一格）。
            if (ExtensionHelper.TrySplitPartNumberedVolume(fileName, out _, out _, out _, out _, out _))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// "卷标记后面还挂着别的点段"（<c>x.7z.001.txt</c> / <c>x.rar.001.bak</c>）算不算分卷：
        /// **从右往左**找第一个能当卷标记的段，它右边的所有段都不许是已知压缩后缀。
        ///
        /// 为什么这么定：<c>x.7z.001.rar</c> 是"分卷段后挂 .rar"的既有写法（另一条分支管它），
        /// <c>x.7z.001.zip</c> 更像"一个真 zip 被改了名" —— 这两种都不算跨段伪装。
        /// </summary>
        private static bool IsVolumeMarkFollowedByNonArchiveTail(string[] parts)
        {
            if (parts.Length < 3)
            {
                return false;
            }

            for (int i = parts.Length - 2; i >= 1; i--)
            {
                if (!ExtensionHelper.TrySplitVolumeSegmentTolerant(parts[i], out _, out _))
                {
                    continue;
                }

                /*
                 * ⚠ 收紧的一道闸门（2026-09-28 全量回归逮到的真回归）：
                 * 只有"卷标记**紧跟在已知压缩后缀后面**"才算跨段伪装（`x.7z.001.txt`、`x.rar.001.bak`）。
                 * 不加这条，`rar-android-722.132.apk` 会被当成"卷 132 + .apk 尾巴"，
                 * 基名被剥成 `rar-android-722` —— 落点目录跟着变（`OutputPlacementTests` 当场红）。
                 * 宁可漏认（用户还能靠改名解决），也不能把正常文件名剥错。
                 */
                if (i < 1 || !ExtensionHelper.IsKnownArchiveExtension("." + parts[i - 1]))
                {
                    continue;
                }

                // 卷标记左边得有基名（`001.txt` 这种连基名都没有的不算分卷）
                if (string.Join('.', parts, 0, i - 1).Length == 0)
                {
                    return false;
                }

                for (int j = i + 1; j < parts.Length; j++)
                {
                    if (ExtensionHelper.IsKnownArchiveExtension("." + parts[j]))
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// 获取安全的压缩包基础名。
        /// </summary>
        public static string GetSafeArchiveBaseName(string filePath)
        {
            return SanitizeFileName(GetArchiveBaseName(filePath));
        }

        /// <summary>
        /// 这个文件是不是**分卷的后续卷**（组的起点不是它）：<c>.002</c> 及更大的三位数字卷、
        /// <c>.z01</c>/<c>.r00</c>、<c>.part2</c> 及更大的分卷段都算；<c>.001</c> / <c>.zip</c> 本体 /
        /// <c>.part1</c> / <c>.rar</c>（起点）不算。
        ///
        /// <para><b>为什么判据只能有一份</b>（2026-09-28 审计 + 2026-10-02 真机）：全项目曾经三处各写一遍
        /// "分卷名判断"，两处不同步就会出"同一份包，扫描说它是后续卷、续解说它是内层包"这种自相矛盾。
        /// 现在的真实判据 = <see cref="ExtensionHelper.TrySplitVolumeSegmentTolerant"/> + "001 才是起点"，
        /// 由本方法一处给出：续解扫描（<c>OneClickCoordinator</c>）与递归的**单链判定**
        /// （<c>RecursiveExtractor.HasOnlyInformationalSiblings</c>）都读它。</para>
        ///
        /// <para><b>为什么递归的"单链判定"也需要它</b>（用户 2026-10-02 真机 `1-6 电磁感应定律（1）`）：
        /// 那一层里是 <c>51658213.7z.001</c>（**唯一**的内层归档）+ <c>51658213.7z.002</c>（**同一组**的后续卷），
        /// 老判据只跳过"内层归档那一个文件"，于是 <c>.002</c> 被算成"别的文件"⇒ 单链不成立 ⇒
        /// 报「这一层里有 1 个内层归档（多分支）」并**保守停在那一层**：一键档白白多跑一轮，
        /// 手动档还会拿"要不要展开多分支"去问用户 —— 而他看到的只有一个包。</para>
        /// </summary>
        public static bool IsVolumeContinuationPart(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            string fileName = GetFileName(filePath);
            string extension = Path.GetExtension(fileName);

            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            /*
             * ⚠ 判据用**容差档**（2026-09-28 审计：这是全项目最后一处还在用"前缀档"的分卷名判断）。
             * 老写法 `IsVolumePartExtension(extension)` 只认干净的 `.001`/`.z01`，于是名字被伪装过的后续卷
             * （`amb909.7删z.00除2`、`amb909.7z.002sc`）在这里判成"不是后续卷" —— 与探测器、改名闸门
             * 的口径不一致（同一判据第三次翻车就是这种"两处不同步"）。判据只留 `ExtensionHelper` 一份。
             */
            if (ExtensionHelper.TrySplitVolumeSegmentTolerant(extension.TrimStart('.'), out string volumeMark, out _))
            {
                // 三位数字分卷里只有 001 是起点；.z01 / .r00 这类也不是组的开头（老口径不变）。
                return !string.Equals(volumeMark, "001", StringComparison.OrdinalIgnoreCase);
            }

            // xxx.part2.rar 的最后后缀是 .rar，编号在倒数第二个后缀上。
            return GetPartSegmentNumber(fileName) > 1;
        }

        /// <summary>取 <c>xxx.part01.rar</c> 里的 1；不是 part 命名返回 0。</summary>
        private static int GetPartSegmentNumber(string fileName)
        {
            string partSegment = Path.GetExtension(Path.GetFileNameWithoutExtension(fileName));

            if (string.IsNullOrWhiteSpace(partSegment))
            {
                return 0;
            }

            string digits = partSegment.TrimStart('.');

            if (digits.Length < 5 ||
                !digits.StartsWith("part", StringComparison.OrdinalIgnoreCase) ||
                !digits.Skip(4).All(char.IsDigit))
            {
                return 0;
            }

            return int.TryParse(digits.Substring(4), out int number) ? number : 0;
        }
    }
}

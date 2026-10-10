using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Helpers
{
    /// <summary>
    /// 文件后缀相关工具。
    /// </summary>
    public static class ExtensionHelper
    {
        /// <summary>
        /// 常见压缩包后缀。
        /// </summary>
        public static readonly HashSet<string> KnownArchiveExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".zip",
                ".rar",
                ".7z",
                ".gz",
                ".gzip",
                ".bz2",
                ".xz",
                ".tar",
                ".tgz",
                ".tbz2",
                ".txz",
                ".001",
                ".z",
                ".cab",
                ".iso",
                ".arj",
                ".lzh",
                ".zst",
                ".lz4",
                ".rpm"
            };

        /// <summary>
        /// 常见伪装后缀。
        /// </summary>
        public static readonly HashSet<string> SuspiciousFakeExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".jpg",
                ".jpeg",
                ".png",
                ".gif",
                ".bmp",
                ".webp",
                ".pdf",
                ".mp4",
                ".mkv",
                ".avi",
                ".mp3",
                ".wav",
                ".txt",
                ".doc",
                // .docx / .xlsx / .pptx 已从伪装表移除：它们本身就是 ZIP 容器（Office Open XML），
                // 把"文件是 ZIP"当成伪装属于误报 —— 一份正常 Word 文档会被判成"疑似伪装"。
                ".xls",
                ".ppt",
                ".html",
                ".htm"
            };

        /// <summary>
        /// "本身就是 ZIP 容器"的合法扩展名。
        ///
        /// 为什么必须单独列一张表：
        /// 这些格式（APK / JAR / Office Open XML / EPUB / VSIX / NuGet 包…）的内容就是 ZIP，
        /// 但它们的后缀**本来就是对的**。如果按"检测到 ZIP 但后缀不是 .zip"判成"后缀不匹配"，
        /// 「智能修正后缀」就会把 `xxx.apk` 改成 `xxx.zip` —— 把安装包变成一个打不开的压缩包。
        /// 所以它们一律视为"后缀正常"。
        ///
        /// 真实踩到：`rar-android-722.132.apk`（Android 版 RAR）被判"后缀不匹配"、建议改成 .zip。
        /// </summary>
        public static readonly HashSet<string> ZipContainerExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".apk",     // Android 应用包
                ".aab",     // Android App Bundle
                ".jar",     // Java 归档
                ".war",
                ".ear",
                ".docx",    // Office Open XML
                ".docm",
                ".xlsx",
                ".xlsm",
                ".pptx",
                ".pptm",
                ".odt",     // OpenDocument
                ".ods",
                ".odp",
                ".epub",    // 电子书
                ".vsix",    // VS 扩展
                ".nupkg",   // NuGet 包
                ".ipa",     // iOS 应用包
                ".xpi",     // Firefox 扩展
                ".kmz",     // Google Earth
                ".cbz"      // 漫画包
            };

        /// <summary>是不是"本身就是 ZIP 容器"的合法扩展名（后缀不该被改）。</summary>
        public static bool IsZipContainerExtension(string? extension)
        {
            extension = NormalizeExtension(extension);

            return !string.IsNullOrWhiteSpace(extension)
                   && ZipContainerExtensions.Contains(extension);
        }

        /// <summary>
        /// 判断是否是常见压缩包后缀。
        /// </summary>
        public static bool IsKnownArchiveExtension(string? extension)
        {
            extension = NormalizeExtension(extension);
            return !string.IsNullOrWhiteSpace(extension)
                   && KnownArchiveExtensions.Contains(extension);
        }

        /// <summary>
        /// 判断是否是常见伪装后缀。
        /// </summary>
        public static bool IsSuspiciousFakeExtension(string? extension)
        {
            extension = NormalizeExtension(extension);
            return !string.IsNullOrWhiteSpace(extension)
                   && SuspiciousFakeExtensions.Contains(extension);
        }

        /// <summary>
        /// 判断文件名是否无后缀。
        /// </summary>
        public static bool HasNoExtension(string? fileNameOrPath)
        {
            if (string.IsNullOrWhiteSpace(fileNameOrPath))
            {
                return true;
            }

            return string.IsNullOrWhiteSpace(Path.GetExtension(fileNameOrPath));
        }

        /// <summary>
        /// 获取最后一个后缀。
        /// 没有后缀返回空字符串。
        /// </summary>
        public static string GetLastExtension(string? fileNameOrPath)
        {
            if (string.IsNullOrWhiteSpace(fileNameOrPath))
            {
                return string.Empty;
            }

            return Path.GetExtension(fileNameOrPath) ?? string.Empty;
        }

        /// <summary>
        /// 获取最后一个后缀。
        /// 没有后缀返回“无”。
        /// </summary>
        public static string GetLastExtensionDisplay(string? fileNameOrPath)
        {
            string ext = GetLastExtension(fileNameOrPath);
            return string.IsNullOrWhiteSpace(ext) ? "无" : ext;
        }

        /// <summary>
        /// 获取文件名中所有后缀。
        /// 例如：
        /// test.rar.pdf.jpg -> .rar .pdf .jpg
        /// </summary>
        public static List<string> GetAllExtensions(string? fileNameOrPath)
        {
            var result = new List<string>();

            if (string.IsNullOrWhiteSpace(fileNameOrPath))
            {
                return result;
            }

            string fileName = Path.GetFileName(fileNameOrPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return result;
            }

            string[] parts = fileName.Split('.', StringSplitOptions.None);

            if (parts.Length <= 1)
            {
                return result;
            }

            for (int i = 1; i < parts.Length; i++)
            {
                if (!string.IsNullOrEmpty(parts[i]))
                {
                    result.Add("." + parts[i]);
                }
            }

            return result;
        }

        /// <summary>
        /// 判断是否有多个后缀。
        /// </summary>
        public static bool HasMultipleExtensions(string? fileNameOrPath)
        {
            return GetAllExtensions(fileNameOrPath).Count >= 2;
        }

        /// <summary>
        /// 判断文件名中是否包含指定后缀。
        /// </summary>
        public static bool ContainsExtension(string? fileNameOrPath, string? extension)
        {
            extension = NormalizeExtension(extension);

            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            return GetAllExtensions(fileNameOrPath)
                .Any(x => string.Equals(x, extension, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 标准化后缀。
        /// 输入 7z 返回 .7z。
        /// 输入 .7z 返回 .7z。
        /// </summary>
        public static string NormalizeExtension(string? extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return string.Empty;
            }

            string value = extension.Trim();

            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            if (!value.StartsWith(".", StringComparison.Ordinal))
            {
                value = "." + value;
            }

            return value;
        }

        /// <summary>
        /// 替换最后一个后缀。
        /// 无后缀时直接添加。
        /// </summary>
        public static string ReplaceLastExtension(string filePath, string targetExtension)
        {
            targetExtension = NormalizeExtension(targetExtension);

            if (string.IsNullOrWhiteSpace(filePath))
            {
                return filePath;
            }

            if (string.IsNullOrWhiteSpace(targetExtension))
            {
                return filePath;
            }

            string? dir = Path.GetDirectoryName(filePath);
            string fileName = Path.GetFileName(filePath);

            string newFileName;

            if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
            {
                newFileName = fileName + targetExtension;
            }
            else
            {
                newFileName = Path.GetFileNameWithoutExtension(fileName) + targetExtension;
            }

            return string.IsNullOrWhiteSpace(dir)
                ? newFileName
                : Path.Combine(dir, newFileName);
        }

        /// <summary>
        /// 添加后缀。
        /// </summary>
        public static string AddExtension(string filePath, string targetExtension)
        {
            targetExtension = NormalizeExtension(targetExtension);

            if (string.IsNullOrWhiteSpace(filePath))
            {
                return filePath;
            }

            if (string.IsNullOrWhiteSpace(targetExtension))
            {
                return filePath;
            }

            return filePath + targetExtension;
        }

        /// <summary>
        /// 删除最后一个后缀。
        /// </summary>
        public static string DeleteLastExtension(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return filePath;
            }

            string? dir = Path.GetDirectoryName(filePath);
            string fileName = Path.GetFileName(filePath);

            if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
            {
                return filePath;
            }

            string newFileName = Path.GetFileNameWithoutExtension(fileName);

            return string.IsNullOrWhiteSpace(dir)
                ? newFileName
                : Path.Combine(dir, newFileName);
        }

        /// <summary>
        /// 删除多个后缀。
        /// 例如：
        /// test.rar.pdf.jpg 删除 2 个 -> test.rar
        /// </summary>
        public static string DeleteMultipleExtensions(string filePath, int count)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return filePath;
            }

            if (count <= 0)
            {
                return filePath;
            }

            string result = filePath;

            for (int i = 0; i < count; i++)
            {
                string next = DeleteLastExtension(result);

                if (string.Equals(next, result, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                result = next;
            }

            return result;
        }

        /// <summary>
        /// 根据真实格式获取建议后缀。
        /// </summary>
        public static string GetSuggestedExtensionByFormat(string? format)
        {
            if (string.IsNullOrWhiteSpace(format))
            {
                return string.Empty;
            }

            return format.ToUpperInvariant() switch
            {
                "ZIP" => ".zip",
                "ZIP_EMPTY" => ".zip",
                "ZIP_SPANNED" => ".zip",
                "7Z" => ".7z",
                "RAR4" => ".rar",
                "RAR5" => ".rar",
                "RAR" => ".rar",
                "GZIP" => ".gz",
                "BZIP2" => ".bz2",
                "XZ" => ".xz",
                "TAR" => ".tar",
                "CAB" => ".cab",
                "ARJ" => ".arj",
                "LZH" => ".lzh",
                "Z" => ".z",
                "ZSTD" => ".zst",
                "LZ4" => ".lz4",
                "RPM" => ".rpm",
                "ISO" => ".iso",
                _ => string.Empty
            };
        }

        /// <summary>
        /// 判断某个文件是否是扫描模式中的疑似文件。
        /// </summary>
        public static bool IsSuspiciousFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            string ext = GetLastExtension(filePath);

            if (string.IsNullOrWhiteSpace(ext))
            {
                return true;
            }

            if (HasMultipleExtensions(filePath))
            {
                return true;
            }

            if (IsKnownArchiveExtension(ext))
            {
                return true;
            }

            if (IsSuspiciousFakeExtension(ext))
            {
                return true;
            }

            return false;
        }

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
        /// 把"可能是分卷标记"的**一整段**拆成「分卷标记 + 粘在后面的垃圾」。
        ///
        /// <code>
        /// 001        → (001, "")        z01 → (z01, "")      part1 → (part1, "")
        /// 001删除    → (001, "删除")    001(1) → (001, "(1)") part01副本 → (part01, "副本")
        /// </code>
        ///
        /// <para>
        /// 为什么要有这一条（2026-09-28 真机事故）：百度网盘会把每个分卷名缀上「删除」这类尾巴，
        /// 变成 <c>giu910.7z.001删除</c>。老判据要求"分卷标记必须是最后一段"，于是整组被当成
        /// **几个各自独立的 .7z**，一键处理再把它们改成 <c>giu910.7z</c> / <c>giu910(1).7z</c> ——
        /// 分卷链当场被切断（介质没坏，但名字再也对不上了）。
        /// </para>
        ///
        /// <para>
        /// 判据只此一处：<see cref="FileNameHelper"/> 与 <c>VolumeGroupDetector</c> 都转调本方法。
        /// 尾巴**必须粘在号码后面、里面不含点**（<c>.001.txt</c> 这种另起一段的**不认** —— 那是
        /// "后缀被改坏"的老口径，保持不变）；尾巴是纯数字的也不认（那更像另一套位宽，不许乱猜）。
        /// </para>
        /// </summary>
        public static bool TrySplitVolumeSegment(string? segment, out string volumeSegment, out string junkTail)
        {
            volumeSegment = string.Empty;
            junkTail = string.Empty;

            if (string.IsNullOrWhiteSpace(segment))
            {
                return false;
            }

            string s = segment.Trim();

            // ① 整段就是分卷标记（老口径，最常见）
            if (IsVolumePartExtension("." + s))
            {
                volumeSegment = s;
                return true;
            }

            // ② 标记 + 粘着的垃圾。先按 3 个字符试（001 / z01 / r00），再按 part+数字试。
            int markLength = 0;

            if (s.Length > 3 && IsVolumePartExtension("." + s[..3]))
            {
                markLength = 3;
            }
            else if (s.StartsWith("part", StringComparison.OrdinalIgnoreCase))
            {
                int i = 4;

                while (i < s.Length && char.IsAsciiDigit(s[i]))
                {
                    i++;
                }

                if (i > 4)
                {
                    markLength = i;
                }
            }

            if (markLength <= 0 || markLength >= s.Length)
            {
                return false;
            }

            string mark = s[..markLength];
            string tail = s[markLength..];

            // 尾巴别是纯数字（0023 这种更像卷号本身），也别长到不像垃圾
            if (tail.Length > 32 || tail.All(char.IsAsciiDigit))
            {
                return false;
            }

            /*
             * ⛔ **标记的数字段后面不许紧跟数字**（2026-10-07 真机 `111.z11111111110删除3`）：
             * 老写法取 `s[..3]` = `z11` 就定了，尾巴 `111111110删除3` 只要"不是纯数字、≤32 字符"就放行
             * ⇒ 那 8 个数字被当垃圾丢掉，凭空得出"这是第 11 片"。数字与标记的数字**连在一起**
             * ⇒ 卷号本身有歧义 ⇒ 这一档判不出（交给骨架档与组内事实，证不出第几片就不改名）。
             * ⛔ `001(1)` 这一档不受影响：尾巴第一个字符是 `(`，不是数字（老口径照旧放行）。
             */
            if (char.IsAsciiDigit(tail[0]))
            {
                return false;
            }

            volumeSegment = mark;
            junkTail = tail;
            return true;
        }

        /// <summary>
        /// **宽松**版：段里任何位置夹了垃圾都能认出卷标记（用户 2026-09-28 第二轮追加）。
        ///
        /// <code>
        /// 001      → (001, "")        z01 → (z01, "")        part1 → (part1, "")
        /// 001删除  → (001, "删除")     删除001 → (001, "删除")   z0删除3 → (z03, "删除")
        /// 001(1)   → (001, "(1)")     part01副本 → (part01, "副本")
        /// 0012     → false（纯数字尾巴更像另一套位宽，不猜）
        /// </code>
        ///
        /// <para>
        /// 判据顺序（**顺序本身就是判据**，调换会让 <c>001(1)</c> 之类漏掉）：
        /// ① 整段就是标记 → ② 前缀标记 + 尾巴（老口径，<c>001(1)</c> 走这条）→ ③ **骨架**：
        /// 剔掉段内所有非字母数字字符，剩下的若整体是合法标记就算（<c>z0删除3</c> → <c>z03</c>、
        /// <c>删除001</c> → <c>001</c>）。
        /// </para>
        ///
        /// <para>
        /// 为什么敢宽松：认出卷标记只用来判"这几个文件是不是一组分卷"和"该改成什么名字"；
        /// 真要不要改，后面还有**尺寸规律**与**用户授权**两道。收益是实打实的 ——
        /// 网盘那个「删除」既能缀在后面（<c>.001删除</c>）也能缀在前面（<c>.删除001</c>），
        /// 还可能夹在中间（<c>.z0删除3</c>）。
        /// </para>
        /// </summary>
        public static bool TrySplitVolumeSegmentLoose(string? segment, out string canonicalSegment, out string junk)
        {
            canonicalSegment = string.Empty;
            junk = string.Empty;

            if (string.IsNullOrWhiteSpace(segment))
            {
                return false;
            }

            string s = segment.Trim();

            // ① 整段就是标记
            if (IsVolumePartExtension("." + s))
            {
                canonicalSegment = s;
                return true;
            }

            // ② 前缀标记 + 尾巴（老口径优先：001(1) / 001删除 走这条）
            if (TrySplitVolumeSegment(s, out string prefixMark, out string prefixTail))
            {
                canonicalSegment = prefixMark;
                junk = prefixTail;
                return true;
            }

            // ③ 骨架：剔掉所有非字母数字，剩下的整体是合法标记才算
            var skeleton = new System.Text.StringBuilder(s.Length);

            foreach (char c in s)
            {
                if (char.IsAsciiLetterOrDigit(c))
                {
                    skeleton.Append(c);
                }
            }

            string bones = skeleton.ToString();

            if (bones.Length == 0 || bones.Length == s.Length)
            {
                return false;
            }

            if (!IsVolumePartExtension("." + bones))
            {
                return false;
            }

            canonicalSegment = bones;

            /*
             * ⛔ 骨架档的垃圾**可能夹在号码中间**（`00除2` 的 `除`、`z0删除3` 的 `删除`），
             * 它**不是**"卷号后面的尾巴" —— 老写法 `s.Replace(bones, "")` 还会在"骨架不是原串子串"时
             * 原样吐回整段（`00除2` → 垃圾算成 `00除2`），补缺失卷名就拼出
             * `amb909.7z.00100除2` 这种磁盘上不存在的名字（用户 2026-09-28 真机）。
             * 尾巴只给"真·后缀"那一档用（`001删除` 走前缀规则，照旧带尾巴）。
             */
            junk = string.Empty;
            return true;
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
        /// <c>FileNameHelper.StripVolumeMarkers</c>（包基名 ⇒ 同组判定与落点）、
        /// <c>FileNameHelper.IsVolumePartFileName</c>（拦住会破坏分卷链的改名）。
        /// 尾巴一粘垃圾（网盘给每卷缀「删除」⇒ <c>X.part1.rar删除</c>），**三处同时失效**：
        /// 一组 4 卷被判成"四个基名互不相同的第 1 卷本体" —— 列表里四行、递归里四个分支、
        /// 引擎那边因为名字对不上而拼不起整组（现场见 <c>docs/真机事故复盘.md</c> §51）。</para>
        ///
        /// <para>判据（全部要成立，⛔ 不给"看着像"开口子）：① 末尾那段要么逐字是 <c>rar</c>、
        /// 要么按 <see cref="TryRecoverDisguisedArchiveBody"/> **唯一地**还原成 <c>rar</c>；
        /// ② 倒数第二段要么逐字是合法卷标记、要么按 <see cref="TrySplitVolumeSegmentTolerant"/>
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
        /// <see cref="TrySplitVolumeSegmentLoose"/> 的骨架档只把还原结果拿去比**分卷标记**
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

        /// <summary>这一段是不是"去掉杂质后是合法分卷标记"（底层判据 = <see cref="TrySplitVolumeSegmentLoose"/>）。</summary>
        public static bool IsVolumeSegment(string? segment) =>
            TrySplitVolumeSegmentLoose(segment, out _, out _);

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

        /// <summary>
        /// 卷标记骨架命中（<see cref="TrySplitVolumeSegmentTolerant"/> 的第三档）：
        /// <c>0a0b1</c> → <c>001</c>、<c>z0a1</c> → <c>z01</c>、<c>r0a1</c> → <c>r01</c>；
        /// <paramref name="allowPartNumberedSkeleton"/> = true 时连 <c>pa8rt1</c> → <c>part1</c> 也认。
        /// 另外还认"**卷号被拉长 + 夹非 ASCII**"那一种（<c>z11111111110删除3</c> → <c>z03</c>，
        /// 见 <see cref="TryMatchStretchedVolumeOrdinal"/>）。
        /// 与归档后缀那一档**同一条纪律**：**首尾对齐**（杂质只夹在中间）、多个候选 ⇒ 判不出。
        ///
        /// <para>⛔ <b>partN 骨架按"取最右"规矩放开</b>（用户 2026-10-04 拍板：「放开，但规定取最右边那个
        /// 能自洽的卷标记 —— 可以」）：只有调用方**已经站定"这一段就是最右那个卷标记"**时才传 true
        /// （眼下只有 <see cref="TrySplitPartNumberedVolume"/> 那一处：<c>&lt;基名&gt;.&lt;卷标记&gt;.rar</c>，
        /// 尾巴**逐字**是 <c>rar</c>）。理由是 `p1art2`（RAR 真夹具 `444.p1art2.part2.rar` 的**基名段**）
        /// 与 `pa8rt1` 是**同一个形状**（字母序列都是 <c>part</c>、都夹一个数字），任何按形状的判据都分不开
        /// 它们 —— 裸的末尾段一旦也吃骨架档，`444.p1art2.part2.rar` 剥完 `part2.rar` 之后剩下的
        /// `444.p1art2` 会被再剥一次 ⇒ 基名从 `444.p1art2` 变成 `444`（RAR 族"基名 = partN 段**之前**的
        /// 所有点段"这条不变量当场破）。⇒ "取最右"就是这条：**左边剩下来的段不许再吃骨架档**。</para>
        /// </summary>
        private static bool TryMatchVolumeMarkerSkeleton(
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

            // ⛔ 首尾对齐（与 <see cref="TryMatchKnownSkeleton"/> 同一条）：杂质只能夹在中间。
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
        /// 这个 <c>partN</c> 名字的**卷标记段**是不是"靠**骨架档**才认出来"的（末尾那段本身脏，例 <c>444.pa8rt1.rar</c>）。
        ///
        /// <para>判据两道（都转调既有出口，⛔ 不新造尺子）：① 倒数第二段**逐字不是**合法卷标记
        /// （<see cref="IsVolumePartExtension"/>）；② 它按 <see cref="TryMatchPartNumberedSkeleton"/> 命中骨架。
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
        /// （<see cref="IsVolumePartExtension"/>）；② **也不是**"逐字标记 + 粘着的垃圾"
        /// （<see cref="TrySplitVolumeSegment"/>：<c>001删除</c> / <c>001(1)</c>）；③ 容错档
        /// <see cref="TrySplitVolumeSegmentTolerant"/> 命中。partN 族由
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
        /// 已知归档后缀、却归一得出一个（<see cref="TryRecoverDisguisedArchiveBody"/>，
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

        /// <summary>
        /// <c>partN</c> 骨架：<c>part</c> 四个字母按顺序**从段首**开始、后面**一路到段末全是 ASCII 数字**
        /// （<c>pa8rt1</c> → <c>part1</c>、<c>paart02</c> → <c>part02</c>）。
        ///
        /// <para>⛔ 与另外几档同一条纪律：**首尾对齐**（字母在段首、数字在段末）、没有数字 ⇒ 不认
        /// （<c>partN</c> 这种"字母尾巴"不是卷标记）；⛔ 只由 <see cref="TrySplitPartNumberedVolume"/>
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
        /// （<see cref="IsVolumePartExtension"/>）只认 <c>z</c> + **两位**数字；骨架档那两条候选
        /// （<see cref="TryMatchVolumeMarkerCandidate"/>）走的是"首尾对齐 + 只取字母后面**头两个**数字"，
        /// 而这个名字的第二个数字位离段末还有 12 个字符 ⇒ 一条都不命中。</para>
        ///
        /// <para><b>认不出的后果（真机实测数字，不是推测）</b>：这一片算不算 <c>111</c> 这一组的成员、
        /// 基名算不算 <c>111</c>，全部由 <see cref="TrySplitVolumeSegmentTolerant"/> 转调出来的这一档回答
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

        /// <summary>贪心最左匹配：<paramref name="known"/> 的每个字符按顺序在 <paramref name="s"/> 里找第一个出现的位置。</summary>
        private static bool TryMatchSubsequence(string s, string known, out int[] positions)
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
        private static string BuildSkeletonJunk(string s, int[] positions)
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

        /// <summary>
        /// **容错**版（用户 2026-09-28 第三次真机：`amb909.7sz.00c1` / `amb909.7删z.00除2`）：
        /// 干扰字符被**塞进卷号内部**、甚至是**字母**（`001`→`00c1`）时也要认出来。
        ///
        /// <para>判据：允许删掉**最多 2 个**"多余字符"后剩下的必须是合法卷标记；
        /// 被删掉的字符必须**不全都是数字**（否则 `0012` 会被误读成 `001`+`2`）；
        /// 候选唯一时才返回 —— 有歧义（能删出两种合法标记）一律**不认**（宁可不动）。</para>
        ///
        /// <code>
        /// 00c1 → (001, "c")     0删0除1 → (001, "删除")     7sz（后缀段）→ 不认（段本身不是卷标记）
        /// 0012 → false（删掉的是纯数字）      001 → (001, "")
        /// </code>
        ///
        /// <para>⛔ **只回答"像不像卷标记"，不负责敢不敢用** —— 真要据此改名字，调用方还必须拿到
        /// "兄弟卷佐证 + 尺寸规律"两条证据（子卷归组那三层判据里另外两层）。</para>
        /// </summary>
        /// <param name="segment">待判的那一段（不含点）。</param>
        /// <param name="canonicalSegment">还原出来的规范卷标记。</param>
        /// <param name="junk">被当成杂质剔掉的字符（转调 <see cref="TrySplitVolumeSegmentLoose"/> 那颗时按它的口径，可为空串）。</param>
        /// <param name="allowPartNumberedSkeleton">
        /// 允不允许吃 **<c>partN</c> 骨架**（<c>pa8rt1</c> → <c>part1</c>）。
        /// ⛔ 默认 false = 老口径；**只有"取最右"那一处**（<see cref="TrySplitPartNumberedVolume"/>：
        /// 尾巴逐字是 <c>rar</c> 的 <c>&lt;基名&gt;.&lt;卷标记&gt;.rar</c>）才传 true ——
        /// 理由见 <see cref="TryMatchVolumeMarkerSkeleton"/>（`p1art2` 与 `pa8rt1` 同形，
        /// 裸的末尾段吃了骨架档，`444.p1art2.part2.rar` 的基名就会变成 `444`）。
        /// </param>
        public static bool TrySplitVolumeSegmentTolerant(
            string? segment,
            out string canonicalSegment,
            out string junk,
            bool allowPartNumberedSkeleton = false)
        {
            canonicalSegment = string.Empty;
            junk = string.Empty;

            if (string.IsNullOrWhiteSpace(segment))
            {
                return false;
            }

            string s = segment.Trim();

            // 先走已有的两档（整段就是标记 / 前缀标记+尾巴 / 非字母数字骨架）
            if (TrySplitVolumeSegmentLoose(s, out canonicalSegment, out junk))
            {
                return true;
            }

            // 再试"删最多 2 个字符"：把删掉的字符当垃圾
            string? uniqueMark = null;
            string uniqueJunk = string.Empty;
            bool ambiguous = false;

            int length = s.Length;

            // 删 1 个
            for (int i = 0; i < length; i++)
            {
                string candidate = s.Remove(i, 1);

                if (!IsVolumePartExtension("." + candidate))
                {
                    continue;
                }

                if (char.IsAsciiDigit(s[i]))
                {
                    continue; // 删掉的是数字 → 更像另一套位宽，不猜
                }

                if (uniqueMark != null)
                {
                    ambiguous = true;
                    break;
                }

                uniqueMark = candidate;
                uniqueJunk = s[i].ToString();
            }

            // 删 2 个
            if (!ambiguous && uniqueMark == null && length >= 5)
            {
                for (int i = 0; i < length - 1; i++)
                {
                    for (int j = i + 1; j < length; j++)
                    {
                        string candidate = s.Remove(j, 1).Remove(i, 1);
                        string removed = string.Concat(s[i], s[j]);

                        if (!IsVolumePartExtension("." + candidate))
                        {
                            continue;
                        }

                        if (removed.All(char.IsAsciiDigit))
                        {
                            continue;
                        }

                        if (uniqueMark != null)
                        {
                            ambiguous = true;
                            break;
                        }

                        uniqueMark = candidate;
                        uniqueJunk = removed;
                    }

                    if (ambiguous || uniqueMark != null)
                    {
                        break;
                    }
                }
            }

            /*
             * 第三档（2026-10-04，用户拍板）：**通用骨架命中** —— `0a0b1` → `001`、`z0a1` → `z01`。
             *
             * ⛔ 顺序不变（用户口径）：前面那两档是**更快更保守的前置档**，它们判得出来就不用这一档。
             * ⛔ partN 骨架（`pa8rt1` → `part1`）**默认不吃**，只有 <paramref name="allowPartNumberedSkeleton"/>
             *    为 true 时才吃 —— 那一位只由"取最右"那一处（`TrySplitPartNumberedVolume`）传，
             *    理由写在 `TryMatchVolumeMarkerSkeleton` 上（`p1art2` 与 `pa8rt1` 同形）。
             */
            if (!ambiguous &&
                uniqueMark == null &&
                TryMatchVolumeMarkerSkeleton(s, allowPartNumberedSkeleton, out string skeletonMark, out string skeletonJunk))
            {
                uniqueMark = skeletonMark;
                uniqueJunk = skeletonJunk;
            }

            if (ambiguous || uniqueMark == null)
            {
                return false;
            }

            canonicalSegment = uniqueMark;
            junk = uniqueJunk;
            return true;
        }
    }
}

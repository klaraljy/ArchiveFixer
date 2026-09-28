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

            volumeSegment = mark;
            junkTail = tail;
            return true;
        }
    }
}

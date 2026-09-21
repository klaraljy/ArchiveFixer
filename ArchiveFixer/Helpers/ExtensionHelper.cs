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
                ".docx",
                ".xls",
                ".xlsx",
                ".ppt",
                ".pptx",
                ".html",
                ".htm"
            };

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
    }
}

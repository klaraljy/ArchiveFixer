using System;
using System.IO;
using System.Text;

namespace ArchiveFixer.Helpers
{
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
        /// 会处理 .tar.gz / .tar.bz2 / .tar.xz。
        /// </summary>
        public static string GetArchiveBaseName(string filePath)
        {
            string fileName = GetFileName(filePath);

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "未命名";
            }

            string lower = fileName.ToLowerInvariant();

            if (lower.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                return fileName[..^7];
            }

            if (lower.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase))
            {
                return fileName[..^8];
            }

            if (lower.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase))
            {
                return fileName[..^7];
            }

            if (lower.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                return fileName[..^4];
            }

            if (lower.EndsWith(".tbz2", StringComparison.OrdinalIgnoreCase))
            {
                return fileName[..^5];
            }

            if (lower.EndsWith(".txz", StringComparison.OrdinalIgnoreCase))
            {
                return fileName[..^4];
            }

            string withoutExt = Path.GetFileNameWithoutExtension(fileName);

            if (string.IsNullOrWhiteSpace(withoutExt))
            {
                return fileName;
            }

            return withoutExt;
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
        /// 获取安全的压缩包基础名。
        /// </summary>
        public static string GetSafeArchiveBaseName(string filePath)
        {
            return SanitizeFileName(GetArchiveBaseName(filePath));
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Security
{
    /// <summary>
    /// “看起来是 ZIP、但用户要的是文件本身”的内容容器默认保护名单。
    ///
    /// <para>这不是按归档魔数判类型，而是按**用户意图**：这些后缀即使内部是 ZIP，
    /// 也默认当内容物保留，不在递归/续解/其余物阶段拆开。用户真要拆时，单独选中该文件
    /// 用「只解压」即可（源包入口不套用本保护）。</para>
    ///
    /// <para>⛔ 只作用于解压链内部；用户直接导入的源包不在这里拦截。</para>
    /// </summary>
    internal static class ContentContainerProtection
    {
        internal static readonly HashSet<string> DefaultExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // 文档 / 办公 / 电子书
                ".docx", ".docm", ".dotx",
                ".xlsx", ".xlsm", ".xltx",
                ".pptx", ".pptm", ".potx",
                ".odt", ".ods", ".odp",
                ".epub", ".cbz", ".kmz",
                ".pages", ".numbers", ".key",

                // 安装包 / 开发包
                ".apk", ".aab", ".ipa",
                ".jar", ".war", ".ear",
                ".vsix", ".nupkg", ".xpi"
            };

        internal static bool ShouldProtect(string? fileNameOrPath)
        {
            if (string.IsNullOrWhiteSpace(fileNameOrPath))
            {
                return false;
            }

            string extension;

            try
            {
                extension = Path.GetExtension(fileNameOrPath);
            }
            catch
            {
                return false;
            }

            return !string.IsNullOrEmpty(extension) && DefaultExtensions.Contains(extension);
        }

        internal static string Describe(string? fileNameOrPath)
        {
            string extension = string.Empty;

            try
            {
                extension = Path.GetExtension(fileNameOrPath) ?? string.Empty;
            }
            catch
            {
            }

            return string.IsNullOrEmpty(extension)
                ? "内容容器"
                : $"内容容器 {extension}";
        }
    }
}

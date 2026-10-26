using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 特定解压的输出文件夹命名（用户 2026-10-25 口径，独立于第一/第二大步）：
    ///
    /// 1. 最后一层解压后，根上只有一个文件夹且没有其他文件 ⇒ 用这个文件夹名；
    /// 2. 其余情况（直接是散文件、多个文件夹、文件夹+散文件）⇒ 用最后一层压缩包的基名。
    /// </summary>
    public static class SpecificOutputNaming
    {
        /// <summary>
        /// 特定解压的输出根：用户选的是文件夹时，输出直接落在**选中的那个合集根**下，
        /// 不跟着源包所在的深层子目录走（用户 2026-10-25 口径：
        /// <c>合集\333\3333.rar</c> → <c>合集\3333\内容物</c>，不是 <c>合集\333\3333\内容物</c>）。
        /// 选的是单个文件时仍落在文件自己那一层。
        /// </summary>
        public static string ResolveOutputRoot(
            string sourceArchive,
            SourceSelectionKind selectionKind,
            string? selectionRoot)
        {
            if (selectionKind == SourceSelectionKind.Folder
                && !string.IsNullOrWhiteSpace(selectionRoot)
                && Directory.Exists(selectionRoot))
            {
                return selectionRoot;
            }

            return Path.GetDirectoryName(sourceArchive) ?? string.Empty;
        }

        public static string Resolve(string lastArchivePath, string contentRoot)
        {
            string archiveName = Path.GetFileName(lastArchivePath);

            if (!string.IsNullOrWhiteSpace(contentRoot) && Directory.Exists(contentRoot))
            {
                List<string> entries = Directory
                    .GetFileSystemEntries(contentRoot)
                    .Where(path => !string.Equals(
                        Path.GetFileName(path),
                        ".ArchiveFixer.work",
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();

                List<string> directories = entries.Where(Directory.Exists).ToList();
                List<string> files = entries.Where(File.Exists).ToList();

                if (directories.Count == 1 && files.Count == 0)
                {
                    return Path.GetFileName(directories[0]);
                }

                /*
                 * 递归核心的 OutputPath 有时就是 RAR 自带的唯一内容文件夹本身
                 * （不是它的父目录）。这时 basename 就是用户要的名字；
                 * 内部层目录（output / layer-N）不算。
                 */
                string rootName = Path.GetFileName(
                    contentRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                if (directories.Count == 0
                    && files.Count > 0
                    && !string.Equals(rootName, "output", StringComparison.OrdinalIgnoreCase)
                    && !rootName.StartsWith("layer-", StringComparison.OrdinalIgnoreCase))
                {
                    return rootName;
                }
            }

            return StripVolumeSuffix(archiveName);
        }

        /// <summary>
        /// 标准分卷的第一卷名（<c>111.7z.001</c> / <c>111.part1.rar</c>）取回 <c>111</c>；
        /// 普通名字只去掉归档后缀。
        /// </summary>
        internal static string StripVolumeSuffix(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            string[] originalParts = fileName.Split('.');
            string name = fileName;

            if (originalParts.Length >= 2
                && IsVolumeMarker(originalParts[^1]))
            {
                name = string.Join('.', originalParts, 0, originalParts.Length - 1);
            }

            if (ExtensionHelper.IsKnownArchiveExtension(Path.GetExtension(name)))
            {
                name = name[..^Path.GetExtension(name).Length];
            }

            string[] parts = name.Split('.');

            if (parts.Length >= 2 && IsVolumeMarker(parts[^1]))
            {
                return string.Join('.', parts, 0, parts.Length - 1);
            }

            return name;
        }

        private static bool IsVolumeMarker(string segment)
        {
            if (segment.Length == 3 && segment.All(char.IsAsciiDigit))
            {
                return true;
            }

            if (segment.StartsWith("part", StringComparison.OrdinalIgnoreCase)
                && segment.Length > 4
                && segment[4..].All(char.IsAsciiDigit))
            {
                return true;
            }

            return segment.Length == 3
                   && (segment[0] is 'z' or 'Z' or 'r' or 'R')
                   && segment[1..].All(char.IsAsciiDigit);
        }

        private static string StripArchiveExtension(string name)
        {
            int dot = name.LastIndexOf('.');

            if (dot > 0 && ExtensionHelper.IsKnownArchiveExtension(name[dot..]))
            {
                return name[..dot];
            }

            return name;
        }
    }
}

using ArchiveFixer.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ArchiveFixer.Engines.WinRar
{
    /// <summary>
    /// 解析 <c>unrar lt -scf</c> 的输出（"技术列表"：每条目一段 <c>Key: Value</c>）。
    ///
    /// <para>
    /// 为什么用 <c>lt</c> 而不是人读的那张表（<c>l</c>）：
    /// <c>l</c> 是固定列宽的对齐文本，列宽、语言（本机那份是中文版 WinRAR！）都会变；
    /// <c>lt</c> 是稳定的键值对，能安全地取大小、目录标志、加密标志。
    /// </para>
    ///
    /// <para>
    /// ⚠ <c>-scf</c>（UTF-8 输出）是**必须**的：不加这个开关，中文条目名会按系统 OEM 代码页
    /// （中文系统 = 936/GBK）输出，而运行器按 UTF-8 解码 —— 名字会全变成 <c>������.txt</c>，
    /// 路径预检摘要、失败清单都会跟着不可核对（7.23 实测：加 <c>-scf</c> 即正常，加 <c>-scu</c> 是 UTF-16LE、更不能用）。
    /// </para>
    ///
    /// 实测的 <c>lt</c> 片段（<c>unrar lt -scf tree.rar</c>，注意条目之间**没有空行**）：
    /// <code>
    ///         Name: DeepSeekProjects\...\src\中文名.txt
    ///         Type: File
    ///         Size: 20
    ///  Packed size: 20
    ///        Ratio: 100%
    ///     Modified: 2026-09-22 19:37:15,431578900
    ///   Attributes: ..A....
    ///        CRC32: 5CDF18D4
    ///      Host OS: Windows
    ///  Compression: RAR 5.0(v50) -m0 -md=128k
    ///
    ///         Name: DeepSeekProjects\...\src\sub
    ///         Type: Directory
    ///     Modified: 2026-09-22 19:37:15,432914700
    ///   Attributes: ...D...
    /// </code>
    /// 加密条目会多一行 <c>Flags: encrypted</c>（实测 <c>-p</c> 数据加密与 <c>-hp</c> 加密头两种情况都有）。
    /// 归档级信息在文件头的 <c>Archive:</c> / <c>Details:</c> 两行里，
    /// 例如 <c>Details: RAR 5, encrypted headers</c>、<c>Details: RAR 5, volume 1</c>。
    /// </summary>
    internal static class UnRarListParser
    {
        /// <summary>
        /// <c>lt</c> 的键行。匹配"行首少量空格 + 已知键 + 冒号"，
        /// 值部分允许包含冒号（条目名里可能有 <c>C:\</c> 这种），所以只按**第一个**冒号切。
        /// </summary>
        private static readonly Regex KeyLine = new(
            @"^\s*(?<key>[A-Za-z][A-Za-z0-9 ]*?)\s*:\s?(?<value>.*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>解析结果：条目 + 归档级标志（是否分卷、是否加密头、卷号）。</summary>
        public sealed class ParsedListing
        {
            public List<ArchiveEntry> Entries { get; } = new();

            public long TotalUncompressedSize { get; set; }

            public int FileCount { get; set; }

            public int DirectoryCount { get; set; }

            /// <summary>归档头被加密（<c>Details: … encrypted headers</c>）。</summary>
            public bool HeaderEncrypted { get; set; }

            /// <summary>至少有一个条目是加密的。</summary>
            public bool AnyEntryEncrypted { get; set; }

            /// <summary>是不是分卷（<c>Details: … volume N</c>）。</summary>
            public bool IsMultiVolume { get; set; }

            /// <summary>当前这一卷的卷号（<c>Details: … volume N</c>）；取不到是 null。</summary>
            public int? VolumeIndex { get; set; }

            /// <summary>归档格式描述原文（<c>RAR 5</c> / <c>RAR 1.5</c> …），只用于日志与消息。</summary>
            public string Details { get; set; } = string.Empty;
        }

        public static ParsedListing Parse(string? output, string archivePath)
        {
            var parsed = new ParsedListing();

            var block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string rawLine in (output ?? string.Empty).Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                Match match = KeyLine.Match(line);

                if (!match.Success)
                {
                    continue;
                }

                string key = match.Groups["key"].Value.Trim();
                string value = match.Groups["value"].Value.Trim();

                if (key.Length == 0)
                {
                    continue;
                }

                // 归档级信息（只在第一个条目之前出现）。
                if (string.Equals(key, "Archive", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(key, "Details", StringComparison.OrdinalIgnoreCase))
                {
                    parsed.Details = value;
                    ApplyDetails(parsed, value);
                    continue;
                }

                /*
                 * "Name:" 是条目之间的分隔符（实测条目之间没有空行）。
                 * 见到它就把上一段结算掉，再开新的一段。
                 */
                if (string.Equals(key, "Name", StringComparison.OrdinalIgnoreCase))
                {
                    Flush(block, parsed);
                    block.Clear();
                    block["Name"] = value;
                    continue;
                }

                if (block.Count > 0)
                {
                    // 同一段里重复出现的键（例如两个 Flags）不覆盖第一个 —— 与 7-Zip 解析口径一致。
                    if (!block.ContainsKey(key))
                    {
                        block[key] = value;
                    }
                }
            }

            Flush(block, parsed);

            // 名字本身就是分卷（xxx.part1.rar / xxx.rar.001）时，即使没写出 volume N 也算分卷。
            if (!parsed.IsMultiVolume)
            {
                parsed.IsMultiVolume = FileNameHelper.IsVolumePartFileName(Path.GetFileName(archivePath));
            }

            return parsed;
        }

        private static void ApplyDetails(ParsedListing parsed, string details)
        {
            if (details.IndexOf("encrypted headers", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                parsed.HeaderEncrypted = true;
            }

            int volumeIndex = TryParseVolumeIndex(details);

            if (volumeIndex > 0)
            {
                parsed.IsMultiVolume = true;
                parsed.VolumeIndex = volumeIndex;
            }
        }

        /// <summary>
        /// 从 <c>RAR 5, volume 1</c> 里取卷号。
        /// 分卷在 RAR 里是 1 基（volume 1 = 第一卷），与 <c>VolumeGroupDetector.TryGetVolumeIndex</c>
        /// 的 0 基口径**不同**，所以这里返回的是**原始卷号**，调用方要比较时用 <c>&gt; 1</c> 判"不是首卷"。
        /// </summary>
        internal static int TryParseVolumeIndex(string? details)
        {
            if (string.IsNullOrWhiteSpace(details))
            {
                return -1;
            }

            Match match = Regex.Match(
                details,
                @"volume\s+(?<index>\d+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (!match.Success)
            {
                return -1;
            }

            return int.TryParse(match.Groups["index"].Value, out int index) ? index : -1;
        }

        private static void Flush(Dictionary<string, string> block, ParsedListing parsed)
        {
            if (block.Count == 0 || !block.TryGetValue("Name", out string? name) || string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            bool isDirectory = IsDirectoryBlock(block);
            bool encrypted = IsEncryptedBlock(block);

            if (encrypted)
            {
                parsed.AnyEntryEncrypted = true;
            }

            long size = 0;

            if (!isDirectory && block.TryGetValue("Size", out string? sizeText))
            {
                long.TryParse(sizeText, out size);
                parsed.TotalUncompressedSize += size;
            }

            if (isDirectory)
            {
                parsed.DirectoryCount++;
            }
            else
            {
                parsed.FileCount++;
            }

            parsed.Entries.Add(new ArchiveEntry
            {
                Path = name,
                Size = size,
                IsDirectory = isDirectory,
                IsEncrypted = encrypted
            });
        }

        /// <summary>
        /// 目录条目：<c>Type: Directory</c>（实测）。
        /// 另外认 <c>Attributes: ...D...</c> 作为兜底 —— RAR 的属性串里**第 4 位**是 D 才表示目录
        /// （<c>...D...</c>；普通文件是 <c>..A....</c>，只读/隐藏/系统是 R/H/S，都不含 D），
        /// 所以只查那一位，避免将来某个属性字母里混进 D 时把文件误判成目录。
        /// </summary>
        private static bool IsDirectoryBlock(Dictionary<string, string> block)
        {
            if (block.TryGetValue("Type", out string? type)
                && !string.IsNullOrWhiteSpace(type)
                && type.TrimStart().StartsWith("Dir", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return block.TryGetValue("Attributes", out string? attributes)
                && attributes != null
                && attributes.Length > 3
                && (attributes[3] == 'D' || attributes[3] == 'd');
        }

        private static bool IsEncryptedBlock(Dictionary<string, string> block)
        {
            return block.TryGetValue("Flags", out string? flags)
                && !string.IsNullOrWhiteSpace(flags)
                && flags.IndexOf("encrypted", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}

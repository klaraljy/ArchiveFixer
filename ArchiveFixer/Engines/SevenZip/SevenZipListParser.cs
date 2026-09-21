using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Engines.SevenZip
{
    /// <summary>
    /// 解析 <c>7z l -slt</c> 的输出（"技术列表"格式：一段一段的 <c>Key = Value</c>）。
    ///
    /// 为什么用 -slt 而不是人读的那份列表：
    /// 人读格式是给人看的，列宽、语言、省略号都会变；-slt 是稳定键值，能安全地取大小、条目数、加密标志。
    ///
    /// 注意：**不许**在这里判断"解压成功还是失败" —— 那是 SevenZipOutputParser 的事。
    /// 这里只回答"里面有什么"。
    /// </summary>
    internal static class SevenZipListParser
    {
        private const string SeparatorLine = "----------";

        public static ArchiveListResult Parse(
            string? output,
            string archivePath,
            string engineId,
            string engineVersion)
        {
            var entries = new List<ArchiveEntry>();
            long totalSize = 0;
            int fileCount = 0;
            int directoryCount = 0;
            bool anyEncrypted = false;
            bool headerEncrypted = false;
            bool multiVolume = false;
            bool seenSeparator = false;
            var block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string[] lines = (output ?? string.Empty).Split('\n');

            foreach (string rawLine in lines)
            {
                string line = rawLine.TrimEnd('\r');

                if (line.StartsWith(SeparatorLine, StringComparison.Ordinal))
                {
                    // 分隔线之前是归档自身的信息块
                    headerEncrypted = IsEncryptedBlock(block) || IsAesMethod(block);
                    multiVolume = block.ContainsKey("Volume Index") || block.ContainsKey("Multivolume");
                    block.Clear();
                    seenSeparator = true;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    if (block.Count > 0 && seenSeparator)
                    {
                        AppendEntry(block, entries, ref totalSize, ref fileCount, ref directoryCount, ref anyEncrypted);
                        block.Clear();
                    }

                    continue;
                }

                int eq = line.IndexOf(" = ", StringComparison.Ordinal);

                if (eq <= 0)
                {
                    continue;
                }

                string key = line[..eq].Trim();
                string value = line[(eq + 3)..].Trim();

                if (key.Length > 0)
                {
                    block[key] = value;
                }
            }

            if (block.Count > 0 && seenSeparator)
            {
                AppendEntry(block, entries, ref totalSize, ref fileCount, ref directoryCount, ref anyEncrypted);
            }

            // 名字本身就是分卷（xxx.7z.001）时，即使 7z 没报 Volume Index 也算分卷
            if (!multiVolume)
            {
                multiVolume = FileNameHelper.IsVolumePartFileName(Path.GetFileName(archivePath));
            }

            return new ArchiveListResult
            {
                Success = true,
                Entries = entries,
                TotalUncompressedSize = totalSize,
                FileCount = fileCount,
                DirectoryCount = directoryCount,
                IsEncrypted = headerEncrypted || anyEncrypted,
                IsMultiVolume = multiVolume,
                EngineId = engineId,
                EngineVersion = engineVersion
            };
        }

        private static void AppendEntry(
            Dictionary<string, string> block,
            List<ArchiveEntry> entries,
            ref long totalSize,
            ref int fileCount,
            ref int directoryCount,
            ref bool anyEncrypted)
        {
            if (!block.TryGetValue("Path", out string? path) || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            bool isDirectory = block.TryGetValue("Folder", out string? folder)
                && string.Equals(folder, "+", StringComparison.Ordinal);

            bool encrypted = IsEncryptedBlock(block);

            if (encrypted)
            {
                anyEncrypted = true;
            }

            long size = 0;

            if (!isDirectory && block.TryGetValue("Size", out string? sizeText))
            {
                long.TryParse(sizeText, out size);
                totalSize += size;
            }

            if (isDirectory)
            {
                directoryCount++;
            }
            else
            {
                fileCount++;
            }

            entries.Add(new ArchiveEntry
            {
                Path = path,
                Size = size,
                IsDirectory = isDirectory,
                IsEncrypted = encrypted
            });
        }

        private static bool IsEncryptedBlock(Dictionary<string, string> block)
        {
            return block.TryGetValue("Encrypted", out string? value)
                && string.Equals(value, "+", StringComparison.Ordinal);
        }

        private static bool IsAesMethod(Dictionary<string, string> block)
        {
            return block.TryGetValue("Method", out string? method)
                && method != null
                && method.IndexOf("AES", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}

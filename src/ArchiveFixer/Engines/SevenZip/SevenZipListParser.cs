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
            bool rawSplitStream = false;
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

                    /*
                     * Type = Split（用户 2026-09-25 第 36 条追加）：7-Zip 说"这不是一个归档，是一段通用分片"。
                     * 它出现的情形就是"分卷的第一卷名字被改坏、后续卷按名字找不到"（真机取证见
                     * ArchiveListResult.IsRawSplitStream 的注释）。管线据此在写盘之前判「分卷缺失」。
                     */
                    rawSplitStream = block.TryGetValue("Type", out string? headerType)
                        && string.Equals(headerType?.Trim(), "Split", StringComparison.OrdinalIgnoreCase);

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
                IsRawSplitStream = rawSplitStream,
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

            bool isDirectory = IsDirectoryBlock(block);

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

        /// <summary>
        /// 判断一个条目是不是目录。
        ///
        /// 为什么要看两处：<c>7z l -slt</c> 的目录条目在不同格式下长得不一样 ——
        /// - ZIP / RAR 会给 <c>Folder = +</c>
        /// - **7z 自己的格式没有 Folder 键**，只有 <c>Attributes = D</c>
        /// 早先只认 <c>Folder = +</c>，结果 7z 包里的目录被当成文件计数，
        /// 导致"解压后校验"的预期文件数比实际多一个 —— 而那是**清理源包的前置条件**，
        /// 数错的直接后果是该删的不删（或反过来误判通过）。
        /// </summary>
        private static bool IsDirectoryBlock(Dictionary<string, string> block)
        {
            if (block.TryGetValue("Folder", out string? folder)
                && string.Equals(folder, "+", StringComparison.Ordinal))
            {
                return true;
            }

            if (block.TryGetValue("Attributes", out string? attributes)
                && !string.IsNullOrEmpty(attributes)
                && (attributes[0] == 'D' || attributes[0] == 'd'))
            {
                return true;
            }

            return false;
        }

        private static bool IsAesMethod(Dictionary<string, string> block)
        {
            return block.TryGetValue("Method", out string? method)
                && method != null
                && method.IndexOf("AES", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}

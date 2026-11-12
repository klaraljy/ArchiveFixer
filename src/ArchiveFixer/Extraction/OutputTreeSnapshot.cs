using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 一次只读遍历得到的产物树快照：相对路径 → 字节数。
    /// 校验、清单核对、额外文件说明共用这一份，不再各自枚举整棵树。
    /// 口径与 OutputVerifier 原实现一致：不跟随符号链接/联接点；读不到大小按 0；
    /// 路径统一为正斜杠、去掉首尾分隔符。
    /// </summary>
    public sealed class OutputTreeSnapshot
    {
        private OutputTreeSnapshot(
            IReadOnlyDictionary<string, long> filesByRelativePath,
            int fileCount,
            long totalSize)
        {
            FilesByRelativePath = filesByRelativePath;
            FileCount = fileCount;
            TotalSize = totalSize;
        }

        public IReadOnlyDictionary<string, long> FilesByRelativePath { get; }

        public int FileCount { get; }

        public long TotalSize { get; }

        public static OutputTreeSnapshot Capture(string? directory)
        {
            var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(directory) || !SafeDirectoryExists(directory))
            {
                return new OutputTreeSnapshot(files, 0, 0);
            }

            if (!TryGetAttributes(directory, out FileAttributes rootAttributes) ||
                (rootAttributes & FileAttributes.ReparsePoint) != 0)
            {
                return new OutputTreeSnapshot(files, 0, 0);
            }

            int fileCount = 0;
            long totalSize = 0;
            var pending = new Stack<string>();
            pending.Push(directory);

            while (pending.Count > 0)
            {
                string currentDirectory = pending.Pop();
                string[] entries;

                try
                {
                    entries = Directory.GetFileSystemEntries(currentDirectory);
                }
                catch
                {
                    continue;
                }

                foreach (string entry in entries)
                {
                    if (!TryGetAttributes(entry, out FileAttributes attributes) ||
                        (attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    fileCount++;
                    long size = 0;

                    try
                    {
                        size = new FileInfo(entry).Length;
                    }
                    catch
                    {
                    }

                    totalSize += size;
                    files[NormalizeRelativePath(Path.GetRelativePath(directory, entry))] = size;
                }
            }

            return new OutputTreeSnapshot(files, fileCount, totalSize);
        }

        public static string NormalizeRelativePath(string path) =>
            (path ?? string.Empty).Replace('\\', '/').Trim('/');

        private static bool SafeDirectoryExists(string directory)
        {
            try
            {
                return Directory.Exists(directory);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch
            {
                attributes = default;
                return false;
            }
        }
    }
}

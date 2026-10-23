using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 用户导入源文件账：在导入/扫描那一刻记录绝对路径，之后改名、搬移都不改这本账。
    /// </summary>
    public static class UserSourceLedger
    {
        private static readonly object Gate = new();
        private static readonly HashSet<string> Paths = new(StringComparer.OrdinalIgnoreCase);

        public static void Record(IEnumerable<string> inputs)
        {
            if (inputs == null)
            {
                return;
            }

            lock (Gate)
            {
                foreach (string input in inputs)
                {
                    if (string.IsNullOrWhiteSpace(input))
                    {
                        continue;
                    }

                    try
                    {
                        if (File.Exists(input))
                        {
                            string full = Path.GetFullPath(input);
                            Paths.Add(full);

                            string? parent = Path.GetDirectoryName(full);
                            string? sourceRoot = parent == null ? null : Path.GetDirectoryName(parent);

                            if (!string.IsNullOrWhiteSpace(sourceRoot) && Directory.Exists(sourceRoot))
                            {
                                foreach (string sibling in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
                                {
                                    Paths.Add(Path.GetFullPath(sibling));

                                    if (Paths.Count >= 20000)
                                    {
                                        return;
                                    }
                                }
                            }
                        }
                        else if (Directory.Exists(input))
                        {
                            foreach (string file in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories))
                            {
                                Paths.Add(Path.GetFullPath(file));

                                if (Paths.Count >= 20000)
                                {
                                    return;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // 单个导入路径读不了就跳过。
                    }
                }
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                Paths.Clear();
            }
        }

        public static IReadOnlyList<string> Snapshot()
        {
            lock (Gate)
            {
                return new List<string>(Paths);
            }
        }
    }
}

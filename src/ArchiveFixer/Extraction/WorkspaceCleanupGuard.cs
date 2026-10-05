using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 清工作区之前的**第二道容器内校验**：这个目录里只许有我们自己造的子目录。
    ///
    /// <para><b>为什么要第二道</b>（AGENTS.md §6 不变量 12）：第一道只回答"它在不在工作区根之下"，
    /// 而删目录是**递归**的 —— 一个名字撞上工作区、或者谁往里放过东西的目录，一旦通过第一道就会被整份端掉。
    /// 用户 2026-09-30 那条红线（"目录里只许有我们自己造的子目录名，越界只写 WARN、一个字节都不删"）
    /// 说的就是这件事。</para>
    ///
    /// <para><b>为什么抽成公共纯函数</b>（2026-10-05 只读审计）：单层路径（<c>ExtractionCoordinator</c>）
    /// 一直有这一道，而递归路径（<c>RecursiveExtractor</c>）的两次删工作区**只有第一道** ——
    /// 同一个安全判据在两处各写一遍，迟早只剩一处是对的。这里只放**纯函数**（判名字、找外来的那一个），
    /// 不碰盘、不删东西：两边的"白名单"刻意**不同**（各自只认自己造的形状），
    /// ⛔ 不许互相套用 —— 拿单层路径的 <c>stage</c>/<c>volumes</c> 去套递归工作区，
    /// 会把每一次清理都拦下（那等于把清理整块关掉）。</para>
    /// </summary>
    public static class WorkspaceCleanupGuard
    {
        /// <summary>
        /// 递归工作区里"我们自己造的子目录名"：<c>layer-NNN</c>（每一层的目录）、
        /// <c>carved</c>（双面文件抠出来的副本）、<c>_密码预检</c>（密码探针）。
        ///
        /// <para>后两个平时都建在某一层的目录**里面**（<c>layer-NNN\carved</c>），列在这里是为了
        /// "以后谁把它们挪到上一层"时不至于被误判成外来目录 —— 判据放宽的只是这三个名字，
        /// 别的任何名字（用户的、别的任务的）照旧一律拦下。</para>
        /// </summary>
        public static bool IsOwnedRecursiveWorkspaceDirectory(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            if (string.Equals(name, RecursiveExtractor.CarveDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, RecursiveExtractor.ProbeDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string prefix = ExtractionWorkspace.LayerDirectoryPrefix;

            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            /*
             * 前缀之后必须是**纯数字层号**（我们自己造的是 `layer-000`、`layer-001`…）。
             * 只判前缀会把 `layer-backup` 这种"看着像我们的"也放进来，而那正是这道校验要拦的东西。
             */
            string digits = name[prefix.Length..];

            return digits.Length > 0 && digits.All(char.IsAsciiDigit);
        }

        /// <summary>
        /// 子目录列表里**第一个不是我们自己造的**（完整路径，直接可以写进日志）；
        /// 全是自己的 / 没有子目录 ⇒ null。
        ///
        /// <para>⚠ 这里**只找、不删**：调用方拿到非 null 之后一律"只写 WARN、一个字节都不删"
        /// （两条路径的兜底都落在"什么都不做"那一档）。</para>
        /// </summary>
        /// <param name="subdirectoryPaths"><c>Directory.GetDirectories</c> 的返回值（完整路径）。</param>
        /// <param name="isOwned">"这个名字是不是我们自己造的"——两条路径各给各的白名单。</param>
        public static string? FindForeignSubdirectory(
            IEnumerable<string>? subdirectoryPaths,
            Func<string, bool> isOwned)
        {
            if (subdirectoryPaths == null || isOwned == null)
            {
                return null;
            }

            foreach (string path in subdirectoryPaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                string? name = Path.GetFileName(
                    path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (!isOwned(name))
                {
                    return path;
                }
            }

            return null;
        }
    }
}

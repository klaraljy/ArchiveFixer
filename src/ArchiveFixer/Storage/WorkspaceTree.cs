using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// **工作区那棵树**的唯一判据出口（用户 2026-09-30 明确指示）。
    ///
    /// <para><b>为什么必须有它</b>：工作区默认位置从 <c>&lt;输出盘&gt;\.ArchiveFixer.work</c> 改成
    /// <b><c>&lt;目标目录&gt;\.ArchiveFixer.work</c></b> 之后，它**落在了成品目录里面**。
    /// 于是所有"扫产物 / 数内容物 / 找内层包 / 校验落点"的判据都会看见它 ——
    /// 不排除的后果是程序把工作区里的中间产物（内层包、抠出来的副本、暂存的一半文件）
    /// 当成"用户的内容物"搬出去、或者当成"下一层要解的包"接着解，属于**不可逆**的事故。</para>
    ///
    /// <para><b>判据只有两条，都在这里</b>（⛔ 别处不许自己拼 <c>.ArchiveFixer.work</c>，
    /// 也不许自己写"是不是在工作区里"的第二套比较）：</para>
    /// <list type="number">
    /// <item><description><b>名字</b>：最后一段等于 <see cref="WorkspaceRootResolver.DefaultWorkspaceDirectoryName"/>
    /// —— 覆盖"目标目录"这一档（不需要知道当前生效的根是哪一个就能排除）。</description></item>
    /// <item><description><b>位置</b>：在当前生效的工作区根**之内或就是它** —— 覆盖用户显式设过
    /// <c>CacheRootDirectory</c> 那一档（那时目录名叫 <c>work</c>，光看名字认不出来）。</description></item>
    /// </list>
    ///
    /// <para>两条是**或**的关系：任一成立就算工作区自己那棵树。宁可多排除一个同名目录
    /// （点开头的 <c>.ArchiveFixer.work</c> 本来就是我们的约定名），也绝不把中间产物搬出去。</para>
    ///
    /// <para><b>不引用 WPF</b>（分层铁律）：纯 <c>System.IO</c> 逻辑。</para>
    /// </summary>
    public static class WorkspaceTree
    {
        /// <summary>是不是工作区目录名（<c>.ArchiveFixer.work</c>，忽略大小写；传名字或完整路径都行）。</summary>
        public static bool IsWorkspaceDirectoryName(string? nameOrPath)
        {
            string name = LastSegment(nameOrPath);

            return name.Length > 0 &&
                   string.Equals(
                       name,
                       WorkspaceRootResolver.DefaultWorkspaceDirectoryName,
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 路径里**任意一段**是不是那个工作区目录名 —— 工作区**里面**的东西靠它认出来。
        ///
        /// <para>为什么要单独一条（而不是只比最后一段）：扫产物时手上往往是**文件的完整路径**
        /// （<c>…\.ArchiveFixer.work\&lt;taskId&gt;\stage\half.bin</c>），最后一段根本不是那个名字。
        /// 只比最后一段会让工作区里的文件"看起来像用户的产物" —— 那正是要防住的那件事。</para>
        /// </summary>
        public static bool HasWorkspaceSegment(string? path)
        {
            string value = (path ?? string.Empty).Trim();

            if (value.Length == 0)
            {
                return false;
            }

            string[] segments = value.Split(
                new[] { '\\', '/' },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (string segment in segments)
            {
                if (string.Equals(
                        segment,
                        WorkspaceRootResolver.DefaultWorkspaceDirectoryName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 某个路径是不是**当前生效的工作区根**本身、或位于它之下（<paramref name="workRoot"/>
        /// 为空/空白时一律 false：没有根就没有"之内"这回事）。
        /// </summary>
        public static bool IsInsideWorkspace(string? path, string? workRoot)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(workRoot))
            {
                return false;
            }

            string fullPath = NormalizeForCompare(path);
            string fullRoot = NormalizeForCompare(workRoot);

            if (fullPath.Length == 0 || fullRoot.Length == 0)
            {
                return false;
            }

            if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 扫目录时**要不要跳过这个条目**（两条判据的"或"，见类注释）。
        ///
        /// <para>这是"扫产物时排除工作区"的**唯一出口**：凡是会枚举成品目录的地方都调它，
        /// 而不是自己写 <c>!name.Equals(".ArchiveFixer.work")</c>。</para>
        ///
        /// <para>⚠ 名字那一条查的是**任意一段**（<see cref="HasWorkspaceSegment"/>），不是最后一段 ——
        /// 传进来的常常是工作区**里面**那个文件的完整路径。</para>
        /// </summary>
        public static bool ShouldSkipEntry(string? entryPath, string? workRoot)
            => HasWorkspaceSegment(entryPath) || IsInsideWorkspace(entryPath, workRoot);

        /// <summary>
        /// 递归枚举目录下的文件，**跳过工作区自己那棵树**（可选的递归）。
        ///
        /// <para>为什么不用 <c>Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)</c>：
        /// 那个重载**看不到**该跳哪一棵子树（它只能按属性跳 Hidden/System，而 Hidden 只是我们尽力设的一个属性，
        /// 设不上 / 被用户清掉 / 换台机器复制过都可能没了），也**不认**用户显式设过 <c>CacheRootDirectory</c>
        /// 那一档的 <c>work</c> 目录名。这里显式按目录剪枝，与属性无关。</para>
        ///
        /// <para>读不了的目录跳过（与既有各处"扫不动就少看一层、绝不让扫描把流程带崩"同一口径）；
        /// 符号链接 / 目录联接点不跟随（跟着走既可能绕圈，也会把别处的文件算成这次扫到的）。</para>
        /// </summary>
        public static IReadOnlyList<string> EnumerateFiles(string? directory, string? workRoot, bool recursive = true)
        {
            var files = new List<string>();

            if (string.IsNullOrWhiteSpace(directory) || !SafeDirectoryExists(directory))
            {
                return files;
            }

            var pending = new Stack<string>();
            pending.Push(directory);

            while (pending.Count > 0)
            {
                string current = pending.Pop();
                string[] entries;

                try
                {
                    entries = Directory.GetFileSystemEntries(current);
                }
                catch
                {
                    continue;
                }

                foreach (string entry in entries)
                {
                    if (ShouldSkipEntry(entry, workRoot))
                    {
                        continue;
                    }

                    if (!TryGetAttributes(entry, out FileAttributes attributes))
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (recursive)
                        {
                            pending.Push(entry);
                        }

                        continue;
                    }

                    files.Add(entry);
                }
            }

            return files;
        }

        /// <summary>
        /// 把目录标成**隐藏**（<see cref="FileAttributes.Hidden"/>）—— 用户翻自己目录时看不见它。
        ///
        /// <para>尽力而为：设不上（权限 / 路径刚好被删 / 非 Windows 语义）只当没发生，
        /// **绝不让它影响任何流程** —— 隐藏属性只是"看起来干净"，工作区本身的正确性不依赖它。</para>
        /// </summary>
        public static void MarkHidden(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                FileAttributes attributes = File.GetAttributes(path);

                if ((attributes & FileAttributes.Hidden) != 0)
                {
                    return;
                }

                File.SetAttributes(path, attributes | FileAttributes.Hidden);
            }
            catch
            {
                // 见方法注释：隐藏是观感，不是正确性。
            }
        }

        /// <summary>按需建目录 + **立刻标隐藏**（工作区那一棵树的目录一律走它）。</summary>
        public static bool EnsureHiddenDirectory(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !SafePathHelper.EnsureDirectoryExists(path))
            {
                return false;
            }

            MarkHidden(path);
            return true;
        }

        private static string LastSegment(string? nameOrPath)
        {
            string value = (nameOrPath ?? string.Empty).Trim().TrimEnd('\\', '/');

            if (value.Length == 0)
            {
                return string.Empty;
            }

            int separator = value.LastIndexOfAny(new[] { '\\', '/' });

            return separator >= 0 ? value[(separator + 1)..] : value;
        }

        private static string NormalizeForCompare(string? path)
        {
            return SafePathHelper.GetFullPathSafe(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool SafeDirectoryExists(string? path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
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

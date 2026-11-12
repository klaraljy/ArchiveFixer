using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using Microsoft.Win32;

namespace ArchiveFixer.Storage
{    /// <summary>
     /// 删除的安全前置（AGENTS.md §6 第 13 条、docs/输出与整理模型.md §3.2 / §5）。
     ///
     /// 五条缺一不可，全部在这里，且**默认拒绝**：
     /// ① 目标必须在允许的根之内（复用 <see cref="ArchivePathGuard.IsInsideRoot"/>，不另写一套）；
     /// ② 目标不得是驱动器根、不得是允许根本身；
     /// ③ 目标到根的路径上、以及目标的子树里，都不得有符号链接 / 联接点；
     /// ④ 调用方必须显式传"已获用户确认"；
     /// ⑤ 每个动作由 <see cref="RecycleBinService"/> 写日志（路径 + 理由 + 条目数 + 总大小）。
     ///
     /// 拆成两层是为了可测：<see cref="EvaluatePath"/> 是**纯函数**（只碰字符串），
     /// <see cref="Evaluate"/> 在它之上再做需要查文件系统的第 ③ 条（探针可注入）。
     ///
     /// ⚠ 这里是**尽力而为**的检查，不是原子保证：检查通过到真正删除之间，路径仍可能被换掉
     /// （TOCTOU）。能做到的是"检查到可疑就拒绝"，做不到"检查过就一定安全"。
     /// </summary>
    public static class DeleteSafetyGuard
    {
        /// <summary>
        /// 子树安全检查的条目上限。超过就**拒绝**而不是"扫一半算了"：
        /// 没扫完就没法保证里面没有联接点，而递归删除跟着联接点跑出去的后果是不可逆的。
        /// </summary>
        public const int MaxSubtreeScanEntryCount = 200_000;

        /// <summary>上级目录链的最大长度（正常路径不可能有这么多级，纯属防死循环）。</summary>
        private const int MaxAncestorDepth = 512;

        /// <summary>
        /// 纯判定（不碰文件系统）：确认标志、根目录、越界、根本身、驱动器根。
        /// </summary>
        public static DeleteSafetyDecision EvaluatePath(string? allowedRoot, string? targetPath, bool userConfirmed)
        {
            // ① 用户确认放在最前面：没有确认时，"路径合不合法"根本不该被讨论（默认拒绝最安全）。
            if (!userConfirmed)
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.NoConfirmation,
                    "调用方没有传入\"已获用户确认\"标志，一律拒绝删除（删除不可逆，必须由界面层显式确认）");
            }

            if (string.IsNullOrWhiteSpace(allowedRoot))
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.RootNotSpecified,
                    "没有指定允许的根目录，无法校验目标是否在其中，拒绝删除");
            }

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return DeleteSafetyDecision.Block(DeleteBlockReason.EmptyTarget, "目标路径为空，拒绝删除");
            }

            if (!TryNormalizeForCompare(allowedRoot, out string rootFull) ||
                !TryNormalizeForCompare(targetPath, out string targetFull))
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.UnresolvablePath,
                    $"路径无法规范化（可能含非法字符或超长），无法确认落点，拒绝删除：{targetPath}");
            }

            // ② 允许根本身：IsInsideRoot 会把"等于根"判成在根之内，这里必须单独拦一次 ——
            // 否则一次"看起来在根内"的删除会把整个根目录端掉。
            if (string.Equals(rootFull, targetFull, StringComparison.OrdinalIgnoreCase))
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.IsAllowedRootItself,
                    $"目标是允许的根目录本身（{targetFull}），拒绝删除");
            }

            // ② 驱动器根：C:\ 这种目标是"整盘删除"的量级，任何情况下都不允许。
            // 放在包含判定之前，是为了让 C:\ 这种目标报出"驱动器根"而不是笼统的"不在根之内"。
            if (IsDriveRoot(targetFull))
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.IsDriveRoot,
                    $"目标是驱动器根目录（{targetFull}），拒绝删除");
            }

            // ① 容器内校验：复用既有的 IsInsideRoot，不自己写一套前缀比较。
            if (!ArchivePathGuard.IsInsideRoot(rootFull, targetFull, out string insideReason))
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.OutsideAllowedRoot,
                    $"目标不在允许的根目录之内，拒绝删除（{insideReason}）");
            }

            return DeleteSafetyDecision.Allow();
        }

        /// <summary>
        /// 完整判定：在 <see cref="EvaluatePath"/> 之上，再做"路径上 / 子树里有没有符号链接、联接点"的检查。
        /// </summary>
        public static DeleteSafetyDecision Evaluate(
            string? allowedRoot,
            string? targetPath,
            bool userConfirmed,
            IDeleteFileSystemProbe? probe = null)
        {
            DeleteSafetyDecision pathDecision = EvaluatePath(allowedRoot, targetPath, userConfirmed);

            if (!pathDecision.IsAllowed)
            {
                return pathDecision;
            }

            probe ??= WindowsDeleteFileSystemProbe.Instance;

            if (!TryNormalizeForCompare(allowedRoot, out string rootFull) ||
                !TryNormalizeForCompare(targetPath, out string targetFull))
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.UnresolvablePath,
                    $"路径无法规范化，无法确认落点，拒绝删除：{targetPath}");
            }

            FileAttributes? targetAttributes = probe.TryGetAttributes(targetFull);

            if (targetAttributes == null)
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.TargetNotFound,
                    $"目标不存在或读不到属性（{targetFull}），本次未执行删除");
            }

            // ③-a 目标自己是链接 / 联接点：删它等于"删了不在授权范围内的东西"（链接指向的内容在别处）。
            if ((targetAttributes.Value & FileAttributes.ReparsePoint) != 0)
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.TargetIsReparsePoint,
                    $"目标是符号链接 / 联接点（{targetFull}），删除它会影响链接指向的其它位置，拒绝删除");
            }

            // ③-b 目标到根之间的每一级：中间夹着链接时，IsInsideRoot 的字符串判定会被绕过去
            //（root\link\file 文本上在根内，物理上可能在另一块盘）。
            DeleteSafetyDecision ancestorDecision = CheckAncestors(targetFull, rootFull, probe);

            if (!ancestorDecision.IsAllowed)
            {
                return ancestorDecision;
            }

            // ③-c 目标目录的子树：递归删除跟着联接点跑出去过，就不能让它开始。
            if ((targetAttributes.Value & FileAttributes.Directory) != 0)
            {
                DeleteSafetyDecision subtreeDecision = CheckSubtree(targetFull, probe);

                if (!subtreeDecision.IsAllowed)
                {
                    return subtreeDecision;
                }
            }

            return DeleteSafetyDecision.Allow();
        }

        /// <summary>目标（含）以上、直到允许根（不含）的每一级目录都不能是符号链接 / 联接点。</summary>
        private static DeleteSafetyDecision CheckAncestors(
            string targetFull,
            string rootFull,
            IDeleteFileSystemProbe probe)
        {
            string? current = Path.GetDirectoryName(targetFull);

            for (int depth = 0; depth < MaxAncestorDepth && !string.IsNullOrEmpty(current); depth++)
            {
                if (string.Equals(current, rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    // 走到根了，链上全是普通目录。
                    return DeleteSafetyDecision.Allow();
                }

                FileAttributes? attributes = probe.TryGetAttributes(current);

                if (attributes == null)
                {
                    return DeleteSafetyDecision.Block(
                        DeleteBlockReason.SafetyProbeUnavailable,
                        $"读不到上级目录的属性（{current}），无法确认路径上没有联接点，拒绝删除");
                }

                if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
                {
                    return DeleteSafetyDecision.Block(
                        DeleteBlockReason.ReparsePointInPath,
                        $"上级目录 {current} 是符号链接 / 联接点，目标的真实位置可能不在允许的根之内，拒绝删除");
                }

                string? parent = Path.GetDirectoryName(current);

                // 到盘根了还没碰到允许根 —— 说明这条路径与根的包含关系不可信，保守拒绝。
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = parent;
            }

            return DeleteSafetyDecision.Block(
                DeleteBlockReason.SafetyProbeUnavailable,
                $"无法确认目标位于允许的根（{rootFull}）之下，拒绝删除");
        }

        /// <summary>
        /// 子树里不得出现符号链接 / 联接点。扫不完（读不到 / 条目过多）也拒绝 ——
        /// 这一条是"宁可少删，不可误删"的直接体现。
        /// </summary>
        private static DeleteSafetyDecision CheckSubtree(string directory, IDeleteFileSystemProbe probe)
        {
            var pending = new Stack<string>();
            pending.Push(directory);

            int scanned = 0;

            while (pending.Count > 0)
            {
                string current = pending.Pop();

                if (!probe.TryListChildren(current, out IReadOnlyList<string> children))
                {
                    return DeleteSafetyDecision.Block(
                        DeleteBlockReason.SafetyProbeUnavailable,
                        $"无法读取目录内容（{current}），不能确认里面没有联接点，拒绝删除");
                }

                foreach (string child in children)
                {
                    scanned++;

                    if (scanned > MaxSubtreeScanEntryCount)
                    {
                        return DeleteSafetyDecision.Block(
                            DeleteBlockReason.SubtreeTooLarge,
                            $"待删除目录的条目数超过安全检查上限（{MaxSubtreeScanEntryCount}），" +
                            "没有扫完就不能保证里面没有联接点，拒绝删除");
                    }

                    FileAttributes? attributes = probe.TryGetAttributes(child);

                    if (attributes == null)
                    {
                        return DeleteSafetyDecision.Block(
                            DeleteBlockReason.SafetyProbeUnavailable,
                            $"读不到条目属性（{child}），不能确认它不是联接点，拒绝删除");
                    }

                    if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
                    {
                        return DeleteSafetyDecision.Block(
                            DeleteBlockReason.ReparsePointInSubtree,
                            $"待删除目录里含符号链接 / 联接点（{child}），递归删除可能跟着它跑出允许的根，拒绝删除");
                    }

                    if ((attributes.Value & FileAttributes.Directory) != 0)
                    {
                        pending.Push(child);
                    }
                }
            }

            return DeleteSafetyDecision.Allow();
        }

        /// <summary>是否是驱动器根（<c>C:\</c>）。</summary>
        private static bool IsDriveRoot(string fullPath)
        {
            try
            {
                string normalized = Path.TrimEndingDirectorySeparator(fullPath);
                string? root = Path.GetPathRoot(normalized);

                if (string.IsNullOrWhiteSpace(root))
                {
                    return false;
                }

                return string.Equals(
                    normalized,
                    Path.TrimEndingDirectorySeparator(root),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // 取不到盘根时按"不是驱动器根"处理：越界与自身判定已经在前面拦过一遍了。
                return false;
            }
        }

        /// <summary>规范化成可比较的完整路径（展开相对路径、消掉 ..、去掉结尾分隔符）。</summary>
        private static bool TryNormalizeForCompare(string? path, out string fullPath)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                fullPath = string.Empty;
                return false;
            }

            try
            {
                fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                return !string.IsNullOrWhiteSpace(fullPath);
            }
            catch
            {
                fullPath = string.Empty;
                return false;
            }
        }
    }

}

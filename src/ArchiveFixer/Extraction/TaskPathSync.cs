using System;
using System.Collections.Generic;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 「某个归档文件被搬走了（改名 / 搬进其余物）」之后，把任务账上**指向它的每一处路径**都改过来
    /// —— **唯一出口**（纯函数式的小工具，自己不碰盘）。
    ///
    /// <para><b>为什么必须是一个出口</b>（AGENTS.md §38 那条真机缺陷）：任务上指向同一个文件的地方有
    /// <b>两处</b> —— <see cref="ArchiveTask.CurrentPath"/> 与 <see cref="ArchiveTask.VolumePaths"/>
    /// （分卷清单）。只改一处，账上就会留下一个"盘上已不存在"的旧名字，而
    /// <c>ProcessArtifactLayout.SourcePackageMover.ResolveSourceGroup</c> 的判据是
    /// "清单非空、却不含自己 ⇒ **整组一份都不搬**"（用户 2026-10-02 收口后的保守档）——
    /// 用户看到的现象是"源包该搬没搬"，日志还得解释一遍。已经踩过两次：改名那一处（§38）
    /// 与内层包搬进其余物那一处（2026-10-02 补上）。</para>
    ///
    /// <para>⛔ 它**不改** <c>FileName</c> / 快照：改名与"搬进其余物"按既有口径都不算"源文件已变化"
    /// （不变量 11 的边界写在那里）。</para>
    /// </summary>
    public static class TaskPathSync
    {
        /// <summary>
        /// 按"旧路径 → 新路径"改一个任务上的两处路径（两个字段**一起**改）。
        /// </summary>
        /// <param name="task">目标任务（null 直接返回）。</param>
        /// <param name="moves">旧路径 → 新路径（键按 <see cref="StringComparer.OrdinalIgnoreCase"/> 比）。</param>
        public static void ApplyMove(ArchiveTask? task, IReadOnlyDictionary<string, string>? moves)
        {
            if (task == null || moves == null || moves.Count == 0)
            {
                return;
            }

            if (task.VolumePaths.Count > 0)
            {
                for (int i = 0; i < task.VolumePaths.Count; i++)
                {
                    if (TryResolve(moves, task.VolumePaths[i], out string? movedVolume))
                    {
                        task.VolumePaths[i] = movedVolume;
                    }
                }
            }

            if (TryResolve(moves, task.CurrentPath, out string? movedCurrent))
            {
                task.CurrentPath = movedCurrent;
            }
        }

        /// <summary>一次改一批任务（内层包搬运那条路是一批兄弟任务共用一个改名表）。</summary>
        public static void ApplyMoves(
            IEnumerable<ArchiveTask?>? tasks,
            IReadOnlyDictionary<string, string>? moves)
        {
            if (tasks == null || moves == null || moves.Count == 0)
            {
                return;
            }

            foreach (ArchiveTask? task in tasks)
            {
                ApplyMove(task, moves);
            }
        }

        /// <summary>一次"单文件搬家"的便捷写法（内层包那一处就是一个文件一个文件搬的）。</summary>
        public static void ApplySingleMove(ArchiveTask? task, string? from, string? to)
        {
            if (task == null || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            {
                return;
            }

            ApplyMove(
                task,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [from] = to });
        }

        private static bool TryResolve(
            IReadOnlyDictionary<string, string> moves,
            string? path,
            out string moved)
        {
            moved = string.Empty;

            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (!moves.TryGetValue(path, out string? target) || string.IsNullOrWhiteSpace(target))
            {
                return false;
            }

            moved = target;
            return true;
        }
    }
}

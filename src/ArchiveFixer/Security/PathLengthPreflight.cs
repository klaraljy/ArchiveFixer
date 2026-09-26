using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;

namespace ArchiveFixer.Security
{
    /// <summary>一次"解压前路径长度预检"的结论。全部字段都是"算出来的事实"，不预测成败。</summary>
    public sealed class PathLengthPreflightResult
    {
        /// <summary>是否存在"可能因为路径过长而失败"的风险。</summary>
        public bool HasWarning { get; init; }

        /// <summary>中文提示，可直接进日志 / 任务详情（没有风险时为空字符串）。</summary>
        public string Warning { get; init; } = string.Empty;

        /// <summary>最长的那一条归档内相对路径（原样，便于用户回包里核对）。</summary>
        public string LongestRelativePath { get; init; } = string.Empty;

        /// <summary>最长相对路径的字符数。</summary>
        public int LongestRelativePathLength { get; init; }

        /// <summary>目标根目录的字符数（没给目标根时为 0）。</summary>
        public int TargetRootLength { get; init; }

        /// <summary>最长条目拼上目标根之后的完整路径字符数。</summary>
        public int LongestFullPathLength { get; init; }

        /// <summary>判定为"过长"的条目数（目录条目也算：目录建不出来时它下面的文件全落不了盘）。</summary>
        public int OverLimitEntryCount { get; init; }
    }

    /// <summary>
    /// 路径长度预检 —— 在**解压前那一遍 list 里**顺带算出"这个包解到那个目录会不会因为路径太长而失败"。
    ///
    /// 为什么要提前说：7-Zip 报的是英文错（<c>The filename or extension is too long</c>），
    /// 而且往往**解到一半才报**（前面的条目已经落盘）。提前一句中文提示，用户能少走一大圈。
    ///
    /// 纪律（规格 §3 第 4 条）：它只吃**调用方已经拿到手的那一份 list**，绝不为了它再跑一次 7z ——
    /// 加密包每多列一次目录就多一次失败机会，重复 list 是把失败概率成倍放大。
    ///
    /// ⚠ 边界：判的是"**交给引擎的那个目标根** + 条目相对路径"（实际写盘的地方），
    /// 不是最终落点目录（定稿那一步可能还会挪一次、换一个更长的根）。
    /// 所以它是**预警**，不是拒绝理由：超了照样解，解出来多少算多少（状态由解压结果决定）。
    /// </summary>
    public static class PathLengthPreflight
    {
        /// <summary>
        /// 算一次。<paramref name="entries"/> 为 null（引擎给不出清单）时返回"没有结论"，
        /// **不假装安全**（<see cref="PathLengthPreflightResult.HasWarning"/> 为 false 但也没有任何数字）。
        /// </summary>
        public static PathLengthPreflightResult Check(IEnumerable<ArchiveEntry>? entries, string? targetRoot)
        {
            if (entries == null)
            {
                return new PathLengthPreflightResult();
            }

            string root = (targetRoot ?? string.Empty).Trim();

            /*
             * 去掉结尾分隔符只为"长度算一次、别因为多一个 \ 就变一个数"。
             * 用 Path.TrimEndingDirectorySeparator 而不是 TrimEnd('\\')：
             * 后者会把盘根 "C:\" 削成 "C:"，于是 Path.Combine 拼出的是**盘符相对路径** "C:x"
             * ——长度还照样算得出来，但那条路径跟用户的目标根本不是一回事。
             */
            root = Path.TrimEndingDirectorySeparator(root);

            string longestRelative = string.Empty;
            int longestRelativeLength = 0;
            int longestFullLength = 0;
            int overLimitCount = 0;
            bool any = false;

            foreach (ArchiveEntry? entry in entries)
            {
                string relative = entry?.Path ?? string.Empty;

                if (string.IsNullOrWhiteSpace(relative))
                {
                    continue;
                }

                any = true;

                string full = root.Length == 0 ? relative : SafePathHelper.Combine(root, relative);

                // 阈值只有一处定义：SafePathHelper.IsPathTooLong（≥240）。这里不另写一遍数字。
                if (SafePathHelper.IsPathTooLong(full))
                {
                    overLimitCount++;
                }

                if (full.Length > longestFullLength)
                {
                    longestFullLength = full.Length;
                    longestRelative = relative;
                    longestRelativeLength = relative.Length;
                }
            }

            if (!any)
            {
                return new PathLengthPreflightResult
                {
                    TargetRootLength = root.Length
                };
            }

            bool hasWarning = overLimitCount > 0;

            return new PathLengthPreflightResult
            {
                HasWarning = hasWarning,
                Warning = hasWarning
                    ? BuildWarning(longestRelative, longestRelativeLength, longestFullLength, root.Length, overLimitCount)
                    : string.Empty,
                LongestRelativePath = longestRelative,
                LongestRelativePathLength = longestRelativeLength,
                TargetRootLength = root.Length,
                LongestFullPathLength = longestFullLength,
                OverLimitEntryCount = overLimitCount
            };
        }

        /// <summary>
        /// 提示文案里必须带**那个最长的条目**：只说"路径过长"，用户既不知道是哪一条、也不知道该改什么。
        /// 前缀用 <see cref="StatusText.PathTooLong"/> 常量（AGENTS.md §7：状态字面量不许散落）。
        /// </summary>
        private static string BuildWarning(
            string longestRelative,
            int longestRelativeLength,
            int longestFullLength,
            int rootLength,
            int overLimitCount)
        {
            string composition = rootLength > 0
                ? $"目标根 {rootLength} 字符 + 最长条目 {longestRelativeLength} 字符 = {longestFullLength} 字符"
                : $"最长条目 {longestFullLength} 字符（未提供目标根，只按条目名算）";

            return $"{StatusText.PathTooLong}，可能失败：{overLimitCount} 个条目的完整路径已进入 Windows 路径上限的危险区间" +
                   $"（{composition}），最长的是「{longestRelative}」。" +
                   "建议缩短输出目录，或先解到更浅的目录再整理。";
        }
    }
}

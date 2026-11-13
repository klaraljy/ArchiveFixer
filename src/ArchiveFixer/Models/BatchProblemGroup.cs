using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// 诊断清单里的一组：组名 + 个数 + 点名（最多 <see cref="BatchSummaryDiagnosticsRules.MaxNamesPerGroup"/> 个，
    /// 其余折成"还有 K 个"）。
    /// </summary>
    public sealed class BatchProblemGroup
    {
        /// <summary>这一组是哪一类问题（判据与顺序都看它）。</summary>
        public BatchProblemKind Kind { get; init; }

        /// <summary>组名（能复用既有状态常量的就复用，见 <see cref="BatchSummaryDiagnosticsRules.DescribeKind"/>）。</summary>
        public string Title { get; init; } = string.Empty;

        /// <summary>被点名的那些包（已经按上限截断）。</summary>
        public IReadOnlyList<BatchProblemItem> Items { get; init; } = Array.Empty<BatchProblemItem>();

        /// <summary>这一组**实际**有几个（含没列出来的）。</summary>
        public int TotalCount { get; init; }

        /// <summary>组级的那一句注脚（目前只有密码类那一组有："可能是没有密码、或者密码不对"）。</summary>
        public string Note { get; init; } = string.Empty;

        /// <summary>没列出来的个数（0 = 全列了）。</summary>
        public int HiddenCount => Math.Max(0, TotalCount - Items.Count);

        /// <summary>
        /// 这一组里**定稿搬运失败**的有几个（事实位 <see cref="ArchiveTask.CommitMoveFailed"/>，⛔ 不比中文）。
        ///
        /// <para>为什么要它在组上（用户 2026-10-04 真机）：这一类失败**没有引擎原话**（是程序自己搬不动），
        /// 而「其他失败」那一组的指路是"去对照引擎原话逐条看" —— 组里全是它时那句话就是错的指路。
        /// 组注脚与「下一步」都读这一个数（同一份事实，⛔ 不各推一遍）。</para>
        /// </summary>
        public int CommitMoveFailedCount { get; init; }

        /// <summary>
        /// 渲染成一行，形状与用户在需求里给的一致：
        /// <c>密码问题（可能是没有密码、或者密码不对）：3 个 —— a.rar、b.rar（还有 1 个）</c>。
        /// </summary>
        public string ToLine()
        {
            string names = string.Join(
                "、",
                Items.Select(item => string.IsNullOrWhiteSpace(item.Detail)
                    ? item.Name
                    : $"{item.Name}（{item.Detail}）"));

            if (HiddenCount > 0)
            {
                names += string.Format(CultureInfo.CurrentCulture, StatusText.BatchDiagnosticsMoreFormat, HiddenCount);
            }

            string title = string.IsNullOrWhiteSpace(Note)
                ? Title
                : $"{Title}（{Note}）";

            return StatusText.BatchDiagnosticsBullet + string.Format(
                CultureInfo.CurrentCulture,
                StatusText.BatchDiagnosticsLineFormat,
                title,
                TotalCount,
                names);
        }
    }
}

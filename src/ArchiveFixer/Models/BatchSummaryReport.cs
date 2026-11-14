using System;
using System.Collections.Generic;

namespace ArchiveFixer.Models;

/// <summary>
/// 批末那一次汇报的**一份**结论：颜色（<see cref="Severity"/>）与文字（<see cref="Groups"/> /
/// <see cref="Text"/>）。
///
/// <para>为什么要合成一个对象（用户 2026-09-30 第 3 条："严重度管颜色、诊断管文字，
/// 两者都从同一份任务终态算出来，⛔ 不许各算一遍"）：这里就是那"一份" ——
/// 调用方只调 <see cref="BatchSummaryDiagnosticsRules.Build"/> 一次，
/// 颜色与文字都不可能来自两份不同的快照。</para>
/// </summary>
public sealed class BatchSummaryReport
{
    /// <summary>这一批的汇总严重度（判据仍是 <see cref="BatchSummarySeverityRules"/> 那一个出口）。</summary>
    public BatchSummarySeverity Severity { get; init; } = BatchSummarySeverity.Success;

    /// <summary>按组归好的问题清单（空 = 这一批没有任何"没做成"的事）。</summary>
    public IReadOnlyList<BatchProblemGroup> Groups { get; init; } = Array.Empty<BatchProblemGroup>();

    /// <summary>逐组那些行 + 「下一步」那一行（**不含**标题，日志逐行写它）。</summary>
    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 可以直接摆进弹窗正文的清单（标题 + <see cref="Lines"/>）；
    /// **空字符串 = 一个字都不写**（全成功那一批不许出现空标题）。
    /// </summary>
    public string Text => Lines.Count == 0
        ? string.Empty
        : StatusText.BatchDiagnosticsTitle + Environment.NewLine + string.Join(Environment.NewLine, Lines);

    /// <summary>这一批有没有要说的（= <see cref="Text"/> 非空）。</summary>
    public bool HasProblems => Groups.Count > 0;
}

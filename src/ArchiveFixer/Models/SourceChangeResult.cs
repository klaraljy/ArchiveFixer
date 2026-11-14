using System;
using System.Collections.Generic;

namespace ArchiveFixer.Models;

/// <summary>
/// 一次比对的结论。**Changed = false 时其余字段一律为空**（调用方只需要看这一个布尔）。
/// </summary>
public sealed class SourceChangeResult
{
    /// <summary>源文件（分卷则整组里任意一个）发生了变化。</summary>
    public bool Changed { get; init; }

    /// <summary>逐个文件的差异（按记录顺序；"文件不见了"是那个文件上唯一的一项）。</summary>
    public IReadOnlyList<SourceFileChange> Changes { get; init; } = Array.Empty<SourceFileChange>();

    /// <summary>
    /// 有没有"快照压根不存在"这回事（老任务 / 测试直接 new 出来的任务）。
    ///
    /// <para>它**不是**"变化了"：没有基准就无从比对，正确的做法是**在开工前补拍一次**
    /// 而不是拦下任务（拦下等于把"我们没记录"说成"你的文件变了"）。</para>
    /// </summary>
    public bool SnapshotMissing { get; init; }

    /// <summary>没有变化的那一份（省得每处都 new 一个）。</summary>
    public static SourceChangeResult Unchanged { get; } = new();
}

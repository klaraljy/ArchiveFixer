using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Threading;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 一层执行（候选循环）的输入：层准备已经确定的事实 + 调用方给的口子。
///
/// <para>⛔ 只装"跨阶段要用的输入"：循环里那几笔账（尝试 ledger、候选清单、失败结论…）
/// 仍留在 <see cref="LayerExecutor.ExecuteAsync"/> 的局部变量里 —— 它们只在这一层内活，
/// 不需要成为可共享状态，⛔ 也不许借这个对象把状态外泄给别的阶段。</para>
/// </summary>
internal sealed class LayerExecutionState
{
    internal WorkItem Item { get; init; } = null!;

    internal string ArchivePath { get; init; } = string.Empty;

    internal ExtractOptions Options { get; init; } = null!;

    internal IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();

    internal string LayerLabel { get; init; } = string.Empty;

    /// <summary>非 null = 这一层已经在解抠出来的副本（⛔ 不许再抠一次 / 再判双面文件）。</summary>
    internal string? CarvedSource { get; init; }

    internal CancellationToken CancellationToken { get; init; }

    internal IProgress<ArchiveProgress>? Progress { get; init; }

    internal Action<ArchiveStallNotice>? Stalled { get; init; }

    /// <summary>
    /// 停因观察口。**必须由调用方（<c>ExtractLayerAsync</c>）传进来**：它带
    /// <c>[CallerFilePath]</c>，观测到的源码位置要仍落在 <c>RecursiveExtractor.cs</c>（诊断 source_file 不变）。
    /// </summary>
    internal Action<RecursionStopReason, string> ReportStopReason { get; init; } = (_, _) => { };
}

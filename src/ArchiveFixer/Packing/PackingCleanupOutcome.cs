using System;
using System.Collections.Generic;

namespace ArchiveFixer.Packing;

/// <summary>打包收尾（原包 / 其余物）的结果 —— 只用来记账与写日志，**不参与成败判定**。</summary>
public sealed class PackingCleanupOutcome
{
    /// <summary>这次收尾有没有真的做事（没成功 / 不做外层时为 false）。</summary>
    public bool Ran { get; init; }

    /// <summary>原包有没有被搬进其余物。</summary>
    public bool SourceMoved { get; init; }

    /// <summary>原包搬进去之后的位置。</summary>
    public string SourceMoveTarget { get; init; } = string.Empty;

    /// <summary>其余物处理的结果（"保留" / "已移入回收站" / "已彻底删除" / "没做成：…"）。</summary>
    public string RestSummary { get; init; } = string.Empty;

    /// <summary>释放的字节数（保留那一档是 0）。</summary>
    public long FreedBytes { get; init; }

    /// <summary>要写进日志的行（调用方原样写出去；口径只有这一处）。</summary>
    public IReadOnlyList<string> LogLines { get; init; } = Array.Empty<string>();
}

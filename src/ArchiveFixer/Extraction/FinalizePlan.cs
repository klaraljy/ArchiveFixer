using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 定稿布局计划。**纯规划**：只有路径字符串，没有一次磁盘操作。
/// </summary>
public sealed class FinalizePlan
{
    /// <summary>判定表结论。</summary>
    public FinalizeLayoutKind Layout { get; init; } = FinalizeLayoutKind.Empty;

    /// <summary>失败原因（仅 <see cref="FinalizeLayoutKind.Failed"/> 时非空）。</summary>
    public string FailureReason { get; init; } = string.Empty;

    /// <summary>任务目标目录（规格 §1 算出来的 <c>destDir</c>）。</summary>
    public string DestinationDirectory { get; init; } = string.Empty;

    /// <summary>内容物最终所在的目录：<c>destDir</c> 或 <c>destDir\&lt;套的那一层&gt;</c>。</summary>
    public string ContentParentDirectory { get; init; } = string.Empty;

    /// <summary>套出来的那一层文件夹名；没有套层时为空。</summary>
    public string ContentDirectoryName { get; init; } = string.Empty;

    /// <summary>
    /// 全部移动计划（**内容物在前、其余物在后**）。
    ///
    /// 只有"顶层项"：绝不会同时出现某个目录和它里面的东西 —— 那样执行时会先搬父再搬子，子项必然失败。
    /// </summary>
    public IReadOnlyList<PlannedMove> Moves { get; init; } = Array.Empty<PlannedMove>();

    /// <summary>只属于内容物的移动。</summary>
    public IReadOnlyList<PlannedMove> ContentMoves { get; init; } = Array.Empty<PlannedMove>();

    /// <summary>只属于其余物的移动（都已归到 <see cref="ProcessArtifactDirectory"/> 下）。</summary>
    public IReadOnlyList<PlannedMove> ProcessArtifactMoves { get; init; } = Array.Empty<PlannedMove>();

    /// <summary>其余物集中目录（<c>destDir\其余物</c>）。契约 §3.2：其余物只允许出现在这一处。</summary>
    public string ProcessArtifactDirectory { get; init; } = string.Empty;

    /// <summary>其余物总字节数（契约 §3.2 要求详情里报出来）。</summary>
    public long ProcessArtifactTotalSize { get; init; }

    /// <summary>内容物文件数。</summary>
    public int ContentFileCount { get; init; }

    /// <summary>内容物总字节数。</summary>
    public long ContentTotalSize { get; init; }

    /// <summary>给用户看的一句话。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>规划过程中的提醒（忽略的非法条目、退而求其次的取名等），不改变结论。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>整份计划作废（不搬、不删）。</summary>
    public bool Failed => Layout == FinalizeLayoutKind.Failed;

    /// <summary>因为分卷组证明不了完整而整份作废（不是失败，调用方按跳过处理）。</summary>
    public bool BlockedByIncompleteVolumeGroup { get; init; }

    /// <summary>暂存区里只有 0 字节产物（没有可定稿内容）。</summary>
    public bool ZeroByteProductsOnly { get; init; }

    /// <summary>计划里的内容物条数。</summary>
    public int PlannedContentCount => ContentMoves.Count;

    /// <summary>哪几条是其余物（按 From 查，执行时用）。</summary>
    public IReadOnlyCollection<string> ProcessArtifactSources { get; init; } = Array.Empty<string>();

    internal static FinalizePlan Failure(string reason)
    {
        return new FinalizePlan
        {
            Layout = FinalizeLayoutKind.Failed,
            FailureReason = reason,
            Summary = reason,
            Warnings = new[] { reason }
        };
    }
}


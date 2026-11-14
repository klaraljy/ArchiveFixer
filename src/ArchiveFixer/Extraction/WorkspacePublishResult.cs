using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 发布（把产物移出工作区）的结果。
/// </summary>
public sealed class WorkspacePublishResult
{
    public bool Success { get; init; }

    public string DestinationPath { get; init; } = string.Empty;

    public int MovedFileCount { get; init; }

    /// <summary>本次发布尝试的条目数（搬成 + 明确失败）。</summary>
    public int ExpectedFileCount { get; init; }

    /// <summary>发布时明确失败的条目数；任何一条失败都不算整体成功。</summary>
    public int FailedFileCount { get; init; }

    /// <summary>给人看的一句话；**重命名过的文件也写在这里**，让用户知道产物不是原样落进去的。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// **判不出就没动**的那几件事，一条一件事（2026-10-03：就地替换的"半套分卷"兜底）。
    ///
    /// <para>为什么要单独一格、而不是并进 <see cref="Message"/>：这些不是"文件没搬成"的失败，
    /// 而是**刻意的"什么都不做"** —— 用户必须能一眼看到"有一组卷本该被替换掉、我们没敢动，
    /// 因为它有一片不在我们能证明的范围里"。并进 Message 会被淹没在搬运计数里
    /// （AGENTS.md §9.5：要删的动作判不出就什么都不做，而且要留证据）。</para>
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

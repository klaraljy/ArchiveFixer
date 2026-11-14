using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System.Collections.Generic;

namespace ArchiveFixer.ViewModels;

internal sealed class CleanupTaskPlan
{
    public ArchiveTask? Task { get; init; }

    public CleanupPreview? Preview { get; init; }

    /// <summary>共享输出目录的作用域提醒（单任务那份，合并时汇总成一句）。</summary>
    public string ScopeNote { get; init; } = string.Empty;

    /// <summary>没得删的原因（为空表示这是可执行的目标）。</summary>
    public string SkipReason { get; init; } = string.Empty;

    public bool IsActionable => Preview is { HasTarget: true };

    /// <summary>这个任务那一份的作用范围（给确认框里逐个列出用）。</summary>
    public string DescribeTarget()
    {
        string name = Task?.FileName ?? "（未知任务）";

        return Preview == null
            ? $"· {name}：{SkipReason}"
            : $"· {name} → {Preview.ScopePath}（顶层 {Preview.ItemCount} 项 / {Preview.EntryCount} 个条目 / {Preview.TotalBytes} 字节）";
    }
}

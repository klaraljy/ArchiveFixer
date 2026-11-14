using ArchiveFixer.Models;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.ViewModels;

internal sealed class CleanupTargetSelection
{
    /// <summary>本次要处理的任务（按列表顺序；只看勾选）。</summary>
    public IReadOnlyList<ArchiveTask> Tasks { get; init; } = Array.Empty<ArchiveTask>();

    /// <summary>作用来源：勾选 / 一个都没勾。</summary>
    public CleanupTargetSource Source { get; init; }

    /// <summary>有没有可处理的目标。</summary>
    public bool HasTarget => Tasks.Count > 0;
}

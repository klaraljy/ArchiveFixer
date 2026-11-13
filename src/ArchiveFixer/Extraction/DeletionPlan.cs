using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 一次删除的完整计划。手动清理与自动清理都先产出它，再把同一个对象交给执行阶段；
    /// 执行阶段不再自己算允许根或目标集合。
    /// </summary>
    public sealed class DeletionPlan
    {
        public IReadOnlyList<string> Targets { get; init; } = Array.Empty<string>();
        public string AllowedRoot { get; init; } = string.Empty;
        public bool DeletesEverything { get; init; }
        public bool Cancelled { get; init; }
        public string Reason { get; init; } = string.Empty;
    }
}


using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **规范卷名计划**（第二大步族算法的输出，交界处不许产出）。
    /// 只描述“哪一片该叫什么名、谁已经在用这个名字”，⛔ 不落盘、不建链接。
    /// </summary>
    public sealed class CanonicalNamePlan
    {
        public IReadOnlyList<CanonicalNamePlanItem> Items { get; init; } = Array.Empty<CanonicalNamePlanItem>();

        public string Evidence { get; init; } = string.Empty;

        public bool IsEmpty => Items.Count == 0;

        public bool HasConflict => Items.Any(item => item.Conflict.Length > 0);

        public string Describe() =>
            IsEmpty
                ? "规范名计划：空"
                : "规范名计划：" + string.Join(
                    "；",
                    Items.Select(item =>
                        item.TargetName + (item.IsEntry ? "（入口）" : string.Empty)
                        + (item.Conflict.Length > 0 ? $"［冲突：{item.Conflict}］" : string.Empty)));
    }

    /// <summary>一条“片 → 规范名”。<see cref="Conflict"/> 非空 = 目标名被占/不可用（只跳过，不覆盖）。</summary>
    public sealed record CanonicalNamePlanItem(
        string PiecePath,
        string TargetName,
        bool IsEntry,
        string Conflict = "");
}

using ArchiveFixer.Models;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

public static partial class VolumeGatherPlanner
{        /// <summary>
         /// **跨层收卷**的结论（纯事实 + 一张计划；<see cref="ResolveCrossLayerVolumeGather"/> 一个字节都不动）。
         /// </summary>
    internal sealed class CrossLayerVolumeGather
    {
        /// <summary>
        /// 这一档适不适用：手上这一份是不是某组的成员之一（⛔ 不再要求它是**第 1 卷**，
        /// 用户 2026-10-17：「这一组该拼到哪一层」与"手上拿着哪一片"无关）。
        /// 不适用 ⇒ 调用方照旧走今天那条路（⛔ 一个字节都不动、也不免试）。
        /// </summary>
        public bool Applicable { get; init; }

        /// <summary>
        /// 这一组**收拢的目标那一份** = 第 1 卷（跨盘 ZIP 那一族是末片 <c>X.zip</c>）的完整路径。
        /// 空 = 还没判到那一步。调用方**必须**把开工起点换成它 —— 手上有可能是后续卷，
        /// 那一片会被搬进第 1 卷所在层（不改名的话原地不动）。
        /// </summary>
        public string? EntryPath { get; init; }

        /// <summary>要把这几卷**搬进入口那一层**（<c>CanRepair = true</c> 才有值）；执行体仍是
        /// <see cref="TryApply"/>（全成或全不成、绝不覆盖、失败倒序回滚）。</summary>
        public VolumeNameRepairPlan? Plan { get; init; }

        /// <summary>这一趟真要动的卷数（搬过来的 + 原地改名的；入口那一卷不算）。</summary>
        public int MovedCount { get; init; }

        /// <summary>入口那一层**已经自证完整** ⇒ 引擎直接解得开（既不用搬、也不该免试）。</summary>
        public bool CompleteBesideEntry { get; init; }

        /// <summary>
        /// **不许再拿单独一片去试**（用户 2026-10-05 拍板）：有硬证据说这一组凑不齐、
        /// 或者凑得齐却收不到入口这一层。判据只有 7z 起始头那条字节数证据（见方法注释的"免试"一节）。
        /// </summary>
        public bool ShouldSkipTrial { get; init; }

        /// <summary>能点名的缺卷（规范卷名；空 = "缺的是末卷之后的卷"，名字里看不出来）。</summary>
        public IReadOnlyList<string> MissingNames { get; init; } = Array.Empty<string>();

        /// <summary>
        /// 这一趟**找卷用的基准**（用户 2026-10-05 口径：手上最浅的那一卷，⛔ 绝不是 001）。
        /// 空 = 这一档没跑到判基准那一步（不适用 / 判不出）。
        /// </summary>
        public string? AnchorPath { get; init; }

        /// <summary>给人看的一句话（收了哪几卷 / 为什么没收 / 缺哪几片）。</summary>
        public string Detail { get; init; } = string.Empty;
    }
}

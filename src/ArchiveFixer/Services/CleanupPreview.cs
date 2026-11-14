using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 清理**预览**：动手之前给用户看的清单（规格 §3.2 要求"列出路径数量与总大小"）。
    ///
    /// <para>
    /// 预览**不删除任何东西**：<see cref="HasTarget"/> 为 false 时连目标都没有，
    /// 调用方应当直接告诉用户"没有可清理的"，而不是弹一个"确定要删 0 项吗"。
    /// </para>
    /// </summary>
    public sealed class CleanupPreview
    {
        public CleanupScope Scope { get; init; }

        /// <summary>被清理的作用域路径（其余物目录 / 输出根）。</summary>
        public string ScopePath { get; init; } = string.Empty;

        /// <summary>允许删除的根（预览与执行必须一致；空 = 没算出来，拒绝执行）。</summary>
        public string AllowedRoot { get; init; } = string.Empty;

        /// <summary>
        /// 这次预览算出来的**完整作用域判定**。执行阶段直接复用它，
        /// 不再自己算一遍 —— 预览与执行分叉是这类删除功能最容易出事的地方。
        /// </summary>
        public ArtifactCleanupScope? ResolvedScope { get; init; }

        /// <summary>预览与执行共用的删除计划；执行阶段直接用它，不再重算。</summary>
        public DeletionPlan? Plan { get; init; }

        /// <summary>有没有可清理的东西（false 时不要进入确认流程）。</summary>
        public bool HasTarget { get; init; }

        /// <summary>将删除的**顶层条目数**（其余物目录算 1 项；空文件夹清理是候选目录数）。</summary>
        public int ItemCount { get; init; }

        /// <summary>条目总数（含子项；空文件夹清理里没有文件所以恒为 0）。</summary>
        public int EntryCount { get; init; }

        /// <summary>总字节数。</summary>
        public long TotalBytes { get; init; }

        /// <summary>能不能量准（读不到时为 false —— 数字宁可没有，也不能是编的）。</summary>
        public bool Determined { get; init; }

        /// <summary>将删除的顶层条目（最多列 <see cref="MaxListedItems"/> 个，其余用"等 N 项"收尾）。</summary>
        public IReadOnlyList<string> Items { get; init; } = Array.Empty<string>();

        /// <summary>
        /// 被删条目里的**源包文件**（压缩包本身）。大于 0 时确认框必须显著提示 ——
        /// 删掉源包意味着要重新下载，这是用户最容易忽略的后果。
        /// </summary>
        public IReadOnlyList<string> SourcePackageNames { get; init; } = Array.Empty<string>();

        /// <summary>源包文件个数（<see cref="SourcePackageNames"/> 可能被截断，计数以它为准）。</summary>
        public int SourcePackageCount { get; init; }

        /// <summary>
        /// 「删除本目录全部其余物」会影响到**几个包**（其余物根下按包基名分的子目录数）。
        ///
        /// <para>
        /// 为什么要有它：这个入口的作用域是**整个共享输出目录**，用户点之前必须知道
        /// "会有几个包一起被清掉"。以前确认框只说"会影响同目录里的所有包" —— 是一句形容词，
        /// 用户没法判断这次到底动了 2 个还是 20 个（2026-09-22 验收的口径：作用域越大越要点名）。
        /// 非整目录档恒为 0（此时作用域只有一个包，用不着点名单）。
        /// </para>
        /// </summary>
        public int AffectedPackageCount { get; init; }

        /// <summary>受影响的包名清单（最多列 <see cref="MaxListedItems"/> 个；计数以 <see cref="AffectedPackageCount"/> 为准）。</summary>
        public IReadOnlyList<string> AffectedPackageNames { get; init; } = Array.Empty<string>();

        /// <summary>一句话结论，可直接进日志 / 确认框。</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>确认框里最多列几项 —— 列太多会把对话框撑满，用户反而看不清主要信息。</summary>
        public const int MaxListedItems = 10;
    }
}

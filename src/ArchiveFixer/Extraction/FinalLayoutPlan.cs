using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 定稿布局计划：直接包住 <see cref="ResultFinalizer"/> 的结论，只加三样执行阶段要用的东西 ——
    /// 其余物源路径的集合（执行时判断一条移动属于哪一类）与"这是不是一次失败的规划"。
    ///
    /// 为什么不再自己算一套相对路径：判定表（契约 §3.1：终端单文件直接放 / 多文件套一层 /
    /// 多重空目录提上来 / 单链塌缩）**只有一份实现**，就在 <see cref="ResultFinalizer"/> 里。
    /// 本类只负责"把暂存树描述给它、把它的计划执行掉"。
    /// </summary>
    internal sealed class FinalLayoutPlan
    {
        public bool Failed { get; init; }

        public string FailureReason { get; init; } = string.Empty;

        public FinalizeLayoutKind Layout { get; init; } = FinalizeLayoutKind.Empty;

        /// <summary>全部移动（**内容物在前、其余物在后**，按这个顺序执行）。</summary>
        public IReadOnlyList<PlannedMove> Moves { get; init; } = Array.Empty<PlannedMove>();

        /// <summary>计划里的内容物条数。</summary>
        public int PlannedContentCount { get; init; }

        /// <summary>哪几条是其余物（按 From 查，执行时用）。</summary>
        public HashSet<string> ProcessArtifactSources { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public string ProcessArtifactDirectory { get; init; } = string.Empty;

        public long ProcessArtifactTotalSize { get; init; }

        public int ContentFileCount { get; init; }

        public string Summary { get; init; } = string.Empty;

        /// <summary>
        /// 这一单真的按**特定解压**跑了（判定表里"要套的那一层"没有套，规格 §3.5）。
        /// 日志与任务详情据此说清"这次为什么少了一层"（用户最恨"我以为它按默认跑的"）。
        /// </summary>
        public bool SpecialExtractionApplied { get; init; }

        /// <summary>
        /// 暂存区里**只有 0 字节产物**（用户 2026-09-24 铁证：密码不对时 7z 会写出 0 字节的桩文件）。
        ///
        /// <para>
        /// 这一档下 <see cref="Moves"/> 一定是空的（0 字节的东西不算产物，见
        /// <see cref="PlanFinalLayout"/>），所以"不定稿 / 不生成其余物 / 不搬源包"由控制流保证；
        /// 这个标志只用来把日志与结论说得更准（别让人读成"这个包本来就是空的"）。
        /// </para>
        /// </summary>
        public bool ZeroByteProductsOnly { get; init; }

        /// <summary>
        /// 这一层的计划作废，是因为**有一组分卷证明不了完整**（缺卷 / 疑缺卷 / 名字不标准）。
        ///
        /// <para>⚠ 2026-10-09：这一档**不是"失败"** —— 用户口径「连完整的都没有，这就不是程序的错误」、
        /// 「在最后一步之前碰到分卷缺失的就先跳过」⇒ 调用方据此把日志落成 **WARN + 先跳过**
        /// （原来一律写 ERROR「定稿布局规划失败」，用户点名那是红字误报）。
        /// ⛔ "什么都不动"这条判据一个字没放松：它照旧不定稿、不搬、不删。</para>
        /// </summary>
        public bool BlockedByIncompleteVolumeGroup { get; init; }

        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }
}

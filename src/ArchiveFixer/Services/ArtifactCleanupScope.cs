using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 其余物清理的作用域解析结果。
    ///
    /// <para>
    /// 这个类型存在的唯一理由是**把"删哪一份"这件事变成一次可检查、可断言、可复用的计算**：
    /// 预览与执行必须拿到同一个答案，绝不允许出现"预览按窄的算、执行按宽的删"。
    /// </para>
    /// </summary>
    public sealed class ArtifactCleanupScope
    {
        /// <summary>作用域种类：只有本任务那一份 / 整个共享目录。</summary>
        public CleanupScope Scope { get; init; } = CleanupScope.Artifacts;

        /// <summary>
        /// 允许删除的根（交给 <see cref="DeleteSafetyGuard"/> 做容器内校验的那个 <c>AllowedRoot</c>）。
        /// 为空 = 没有可信的边界 → 调用方必须拒绝删除。
        /// </summary>
        public string AllowedRoot { get; init; } = string.Empty;

        /// <summary>
        /// 真正会被删除的第一个目录（其余物目录）。为空 = 没有可删的落点。
        ///
        /// ⚠ 完整清单看 <see cref="ArtifactDirectories"/>：老版本留下的 <c>过程物</c> 与新版
        /// <c>其余物</c> 可能**同时存在**，两个都是本任务那一份，要一起删掉。
        /// </summary>
        public string ArtifactDirectory => ArtifactDirectories.Count > 0 ? ArtifactDirectories[0] : string.Empty;

        /// <summary>本次要删掉的**全部**其余物目录（同一个任务的历史名 + 新版名）。</summary>
        public IReadOnlyList<string> ArtifactDirectories { get; init; } = Array.Empty<string>();

        /// <summary>这次删除的作用域是不是"整个共享目录下的全部其余物"。</summary>
        public bool DeletesEverythingInDirectory { get; init; }

        /// <summary>是不是共享输出目录（模式 B / 指定位置直接放：多个包共用一个输出目录）。</summary>
        public bool OutputDirectoryIsShared { get; init; }

        /// <summary>这个作用域为什么是这个样子（进日志与确认框，说清判据）。</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>没算出来的原因（为空 = 算出来了）。</summary>
        public string BlockReason { get; init; } = string.Empty;

        /// <summary>
        /// 没算出来是因为**确实没有**（正常状态，告诉用户"没有可删的"就行），
        /// 还是因为**拿不准所以不敢删**（必须把拒绝理由说清楚）。
        /// 这两件事对用户的意义完全不同：前者无感，后者要让用户知道程序为什么不动手。
        /// </summary>
        public bool RefusedUnsafe { get; init; }

        /// <summary>能不能删：算出来了、有边界、有落点。</summary>
        public bool IsResolved => ArtifactDirectories.Count > 0 && !string.IsNullOrWhiteSpace(AllowedRoot);

        public DeletionPlan ToDeletionPlan(string reason) => new()
        {
            Targets = ArtifactDirectories,
            AllowedRoot = AllowedRoot,
            DeletesEverything = DeletesEverythingInDirectory,
            Reason = reason
        };
    }
}

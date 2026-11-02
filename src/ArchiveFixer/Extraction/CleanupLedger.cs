using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction
{
    /// <summary>待清理对象的身份。身份在产生时确定，⛔ 不在删除现场靠后缀/名字猜。</summary>
    public enum CleanupItemKind
    {
        UserSource,
        LayerInput,
        RestArtifact,
        Workspace
    }

    /// <summary>一条清理对象事实。</summary>
    public sealed class CleanupItem
    {
        public string Path { get; init; } = string.Empty;
        public CleanupItemKind Kind { get; init; }
        public string ChainId { get; init; } = string.Empty;
        public string OwnerTaskId { get; init; } = string.Empty;
        public int LayerIndex { get; init; } = -1;
        public long Size { get; init; }
    }

    /// <summary>每条链一份的清理账本；只记事实，不执行删除。</summary>
    public sealed class CleanupLedger
    {
        private readonly List<CleanupItem> _items = new();

        public IReadOnlyList<CleanupItem> Items => _items;

        public void Clear() => _items.Clear();

        public void Add(CleanupItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path))
            {
                return;
            }

            _items.Add(item);
        }

        public bool Has(CleanupItemKind kind)
        {
            foreach (CleanupItem item in _items)
            {
                if (item.Kind == kind)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// 一次删除的完整计划。手动清理与自动清理都先产出它，再把同一个对象交给执行阶段；
    /// 执行阶段不再自己算允许根或目标集合。
    /// </summary>
    public sealed class DeletionPlan
    {
        public IReadOnlyList<string> Targets { get; init; } = Array.Empty<string>();
        public string AllowedRoot { get; init; } = string.Empty;
        public bool DeletesEverything { get; init; }
        public string Reason { get; init; } = string.Empty;
    }

    /// <summary>链尾清理步骤；顺序就是唯一删除顺序。</summary>
    public enum ChainCleanupStep
    {
        CollectProcessArtifacts,
        ApplyRestHandling,
        SweepPassThrough,
        ProcessUserSource
    }

    /// <summary>一条链的清理计划。AllowedRoot 由计划给出，执行器不再自己解析。</summary>
    public sealed class ChainCleanupPlan
    {
        public IReadOnlyList<ChainCleanupStep> Steps { get; init; } = Array.Empty<ChainCleanupStep>();
        public string AllowedRoot { get; init; } = string.Empty;
    }

    /// <summary>链尾清理规划：成功/失败共用一套顺序，只有“是否处理源包”由事实决定。</summary>
    public static class CleanupPlanner
    {
        public static ChainCleanupPlan BuildChainPlan(string? allowedRoot, bool sourcePending)
        {
            var steps = new List<ChainCleanupStep>
            {
                ChainCleanupStep.CollectProcessArtifacts,
                ChainCleanupStep.ApplyRestHandling,
                ChainCleanupStep.SweepPassThrough
            };

            if (sourcePending)
            {
                steps.Add(ChainCleanupStep.ProcessUserSource);
            }

            return new ChainCleanupPlan
            {
                Steps = steps,
                AllowedRoot = allowedRoot ?? string.Empty
            };
        }

        /// <summary>内容物文件夹的父目录；其余物同级，删除边界就在父目录。</summary>
        public static string ResolveAllowedRoot(string? contentRoot)
        {
            if (string.IsNullOrWhiteSpace(contentRoot))
            {
                return string.Empty;
            }

            try
            {
                string full = System.IO.Path.GetFullPath(contentRoot);
                return System.IO.Path.GetDirectoryName(full) ?? full;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>唯一清理执行顺序入口；具体动作由调用方按既有执行体接线。</summary>
    public static class CleanupExecutor
    {
        public static async Task ExecuteAsync(
            ChainCleanupPlan plan,
            Func<ChainCleanupStep, CancellationToken, Task> execute,
            CancellationToken cancellationToken)
        {
            if (plan == null || execute == null)
            {
                return;
            }

            foreach (ChainCleanupStep step in plan.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await execute(step, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

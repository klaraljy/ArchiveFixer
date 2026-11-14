using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

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


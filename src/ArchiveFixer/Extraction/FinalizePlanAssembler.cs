namespace ArchiveFixer.Extraction;

/// <summary>
/// 把各步的规划结果汇成 <see cref="FinalizePlan"/>：合并移动清单、统计内容物文件数/字节数、
/// 收口"没有内容物"的两档，并写一句给用户看的汇总（日志口径的唯一出口）。
///
/// <para>只拼对象，⛔ 不执行任何移动。</para>
/// </summary>
internal static class FinalizePlanAssembler
{
    public static FinalizePlan Build(
        FinalizeLayoutDecision layout,
        string destinationDirectory,
        FinalizeContentMovePlan content,
        FinalizeArtifactMovePlan artifacts,
        IReadOnlyList<string> warnings)
    {
        FinalizeLayoutKind kind = layout.Kind;

        var moves = new List<PlannedMove>(content.Moves.Count + artifacts.Moves.Count);
        moves.AddRange(content.Moves);
        moves.AddRange(artifacts.Moves);

        string contentParent = content.ParentDirectory;

        int contentFileCount = 0;
        long contentTotalSize = 0;

        foreach (StagedTreeNode node in content.MovedNodes)
        {
            contentFileCount += StagedTreeFacts.CountFiles(node);
            contentTotalSize += StagedTreeFacts.SubtreeFileSize(node);
        }

        if (moves.Count == 0)
        {
            kind = FinalizeLayoutKind.Empty;
        }

        if (kind is FinalizeLayoutKind.Empty or FinalizeLayoutKind.ProcessArtifactsOnly)
        {
            // 没有内容物就无所谓"内容物落在哪"，留空比留个 destDir 更不容易被误读。
            contentParent = string.Empty;
        }

        return new FinalizePlan
        {
            Layout = kind,
            DestinationDirectory = destinationDirectory,
            ContentParentDirectory = contentParent,
            ContentDirectoryName = kind == FinalizeLayoutKind.SingleFileToDestination
                ? string.Empty
                : layout.WrapperName ?? string.Empty,
            Moves = moves,
            ContentMoves = content.Moves,
            ProcessArtifactMoves = artifacts.Moves,
            ProcessArtifactDirectory = artifacts.Directory,
            ProcessArtifactTotalSize = artifacts.TotalSize,
            ContentFileCount = contentFileCount,
            ContentTotalSize = contentTotalSize,
            Summary = BuildSummary(kind, contentParent, moves.Count, artifacts.Moves.Count, warnings.Count),
            Warnings = warnings
        };
    }

    private static string BuildSummary(
        FinalizeLayoutKind kind,
        string contentParent,
        int moveCount,
        int artifactMoveCount,
        int warningCount)
    {
        string layout = kind switch
        {
            FinalizeLayoutKind.SingleFileToDestination => "判定表 1：终端是单个文件，直接放进目标目录",
            FinalizeLayoutKind.WrapInFolder => "判定表 2：在目标目录下套一层",
            FinalizeLayoutKind.PromoteInnermostFolder => "判定表 3：把最里面那层文件夹提上来",
            FinalizeLayoutKind.ProcessArtifactsOnly => "只有其余物，没有内容物",
            FinalizeLayoutKind.Empty => "暂存区里没有可定稿的东西",
            _ => "定稿布局规划失败"
        };

        string summary = $"{layout}；共 {moveCount} 项移动（其中其余物 {artifactMoveCount} 项）";

        if (!string.IsNullOrWhiteSpace(contentParent))
        {
            summary += "；内容物落在 " + contentParent;
        }

        if (warningCount > 0)
        {
            summary += $"；{warningCount} 条提醒";
        }

        return summary;
    }
}

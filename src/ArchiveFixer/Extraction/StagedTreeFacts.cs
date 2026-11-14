namespace ArchiveFixer.Extraction;

/// <summary>
/// 暂存树的只读事实查询（有没有文件、是不是后代、文件数/字节数、显式其余物标记）。
///
/// <para>全部是纯函数；<see cref="HasAnyFile"/> 的 <c>memo</c> 由调用方持有，
/// 保证同一次规划里每个子树只算一次。</para>
/// </summary>
internal static class StagedTreeFacts
{
    /// <summary>
    /// 自己或祖先里有没有"显式标的其余物"。
    ///
    /// <para>
    /// 走到内容物根就停（<paramref name="contentScope"/> 这一层的标记仍然算数）：
    /// 内容物根**之上**的标记不外溢到内容物里 —— 否则"把暂存区整个标成其余物、再把内容物根指到它里面"
    /// 这种写法会把用户的内容物一起扫进 <c>其余物</c>。
    /// </para>
    /// </summary>
    public static bool HasMarkedArtifact(StagedTreeNode node, StagedTreeNode? contentScope)
    {
        for (StagedTreeNode? current = node; current != null; current = current.Parent)
        {
            if (current.MarkedArtifact)
            {
                return true;
            }

            if (contentScope != null && current == contentScope)
            {
                return false;
            }
        }

        return false;
    }

    public static bool IsDescendantOf(StagedTreeNode node, StagedTreeNode ancestor)
    {
        for (StagedTreeNode? current = node.Parent; current != null; current = current.Parent)
        {
            if (current == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasAnyFile(StagedTreeNode node, Dictionary<StagedTreeNode, bool> memo)
    {
        if (memo.TryGetValue(node, out bool cached))
        {
            return cached;
        }

        bool result = false;

        foreach (StagedTreeNode child in node.Children)
        {
            if (!child.IsDirectory || HasAnyFile(child, memo))
            {
                result = true;
                break;
            }
        }

        memo[node] = result;
        return result;
    }

    public static int CountFiles(StagedTreeNode node)
    {
        if (!node.IsDirectory)
        {
            return 1;
        }

        int count = 0;

        foreach (StagedTreeNode child in node.Children)
        {
            count += CountFiles(child);
        }

        return count;
    }

    public static long SubtreeFileSize(StagedTreeNode node)
    {
        if (!node.IsDirectory)
        {
            return node.Size;
        }

        long total = 0;

        foreach (StagedTreeNode child in node.Children)
        {
            total += SubtreeFileSize(child);
        }

        return total;
    }

    public static int Depth(StagedTreeNode node)
    {
        int depth = 0;

        for (StagedTreeNode? current = node; current != null; current = current.Parent)
        {
            depth++;
        }

        return depth;
    }
}

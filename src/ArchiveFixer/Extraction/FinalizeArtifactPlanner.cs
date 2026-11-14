namespace ArchiveFixer.Extraction;

/// <summary>
/// 其余物归置：认出哪些是其余物，并把它们全部归到 <c>destDir\其余物\</c> 下、保持相对结构。
///
/// <para>
/// 其余物目录名与落点**只认** <see cref="ProcessArtifactLayout"/>（决策 D-8/D-10）：
/// 名字默认「其余物」，内容物那一层正好重名时才让位成「其余物(1)」；
/// 多个包共用根时再按包基名分一层，包本来就有自己目录时不额外多套一层。
/// </para>
/// <para>纯规划：⛔ 不建目录、不移动、不删除。</para>
/// </summary>
internal static class FinalizeArtifactPlanner
{
    /// <summary>收集"最上层"的其余物节点：它自己是其余物、而它父亲不是。</summary>
    public static List<StagedTreeNode> CollectRoots(
        StagedTreeNode root,
        Func<StagedTreeNode, bool> isArtifact)
    {
        var result = new List<StagedTreeNode>();

        void Walk(StagedTreeNode node)
        {
            foreach (StagedTreeNode child in node.Children)
            {
                if (isArtifact(child))
                {
                    result.Add(child);
                    continue;
                }

                if (child.IsDirectory)
                {
                    Walk(child);
                }
            }
        }

        Walk(root);
        return result;
    }

    /// <summary>
    /// 这个节点算不算其余物（内容物 / 其余物的**唯一**分类判据）。
    ///
    /// <para>
    /// 判据只看三件只读事实：① 虚拟根只是容器，永远不算条目；② 内容物根的**祖先**只是路径上的容器
    /// （暂存区常见 <c>&lt;stage&gt;\out</c> 这种两级结构），把它当其余物搬走等于连内容物一起搬走 ——
    /// 这一条优先于"显式标了其余物"；③ 其余看它是否在内容物范围之外。
    /// </para>
    /// </summary>
    /// <param name="contentScope">内容物根节点；null 表示没指定内容物根。</param>
    /// <param name="contentRootSpecified">
    /// 调用方有没有指定内容物根。没指定时整棵树都是内容物，其余物只能靠显式标记。
    /// </param>
    public static bool IsArtifact(
        StagedTreeNode node,
        StagedTreeNode? contentScope,
        bool contentRootSpecified)
    {
        // 虚拟根只是容器，永远不算条目。
        if (node.Parent == null)
        {
            return false;
        }

        /*
         * 内容物根的**祖先**只是路径上的容器（暂存区常见 <stage>\out 这种两级结构）。
         * 把它当其余物搬走，等于连内容物一起搬走 —— 所以这一条优先于"显式标了其余物"。
         */
        if (contentScope != null && StagedTreeFacts.IsDescendantOf(contentScope, node))
        {
            return false;
        }

        if (StagedTreeFacts.HasMarkedArtifact(node, contentScope))
        {
            return true;
        }

        return !InContentScope(node, contentScope, contentRootSpecified);
    }

    private static bool InContentScope(
        StagedTreeNode node,
        StagedTreeNode? contentScope,
        bool contentRootSpecified)
    {
        if (contentScope == null)
        {
            return !contentRootSpecified;
        }

        return node == contentScope || StagedTreeFacts.IsDescendantOf(node, contentScope);
    }

    /// <param name="artifactRoots">最上层的其余物节点（调用方可能追加内容物根）。</param>
    /// <param name="shells">沿路跳过的纯空壳目录（没有内容物时它们就是其余物）。</param>
    /// <param name="contentMoves">内容物的移动清单：已经整棵搬走的子树不能再单独规划。</param>
    /// <param name="contentParentDirectory">
    /// 内容物最终所在目录 —— 其余物与它**同级**（决策 D-10 的 <c>ResolveArtifactDirectoryWithName</c>）。
    /// </param>
    /// <param name="wrapperName">内容物那一层真正叫什么（重名判断用它，不是 destDir 自己的名字）。</param>
    /// <param name="archiveBaseName">归档基名（共用根时其余物按它再分一层）。</param>
    /// <param name="sharedOutputRoot">这个落点是不是同一次导入里多个包共用的包（决策 D-10）。</param>
    /// <param name="warnings">规划过程中的提醒（本方法只追加）。</param>
    /// <param name="stagingRoot">暂存根绝对路径；空则 <c>From</c> 只有相对路径。</param>
    /// <param name="used">本批已占用的落点（内容物先占、其余物随后，共用同一份）。</param>
    public static FinalizeArtifactMovePlan Plan(
        IReadOnlyList<StagedTreeNode> artifactRoots,
        IReadOnlyList<StagedTreeNode> shells,
        IReadOnlyList<PlannedMove> contentMoves,
        string contentParentDirectory,
        string? wrapperName,
        string? archiveBaseName,
        bool sharedOutputRoot,
        List<string> warnings,
        string stagingRoot,
        HashSet<string> used)
    {
        var artifactNodes = new List<StagedTreeNode>(artifactRoots);
        var seenArtifacts = new HashSet<StagedTreeNode>(artifactNodes);

        foreach (StagedTreeNode shell in shells)
        {
            if (seenArtifacts.Add(shell))
            {
                artifactNodes.Add(shell);
            }
        }

        artifactNodes.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        artifactNodes = PruneNestedArtifacts(artifactNodes);

        // 已经被内容物整棵搬走的子树里的东西不能再单独规划一次（执行时那个源路径已经不在暂存区了）。
        var contentSources = contentMoves.Select(m => m.From).ToList();

        /*
         * 其余物目录名与落点（决策 D-8/D-10，唯一实现在 ProcessArtifactLayout）：
         * 名字默认「其余物」，只有内容物那一层正好也叫这个名时才让位成「其余物(1)」；
         * 落点分两档 —— 多个包共用根的两种模式（B/D）按包基名再分一层，
         * 包本来就有自己目录的模式直接 <c>destDir\其余物\</c>，不再多套一层。
         *
         * 重名判断用的是 **wrapperName**（内容物那一层真正叫什么），不是 destDir 自己的名字：
         * 会和 <c>destDir\其余物</c> 撞的正是前者。
         */
        string artifactDirectoryName = ProcessArtifactLayout.ResolveArtifactDirectoryName(wrapperName);

        if (ProcessArtifactLayout.IsArtifactDirectoryName(wrapperName))
        {
            warnings.Add($"内容物那一层与其余物目录重名，其余物目录改用 “{artifactDirectoryName}”");
        }

        bool sharedRoot = sharedOutputRoot;
        bool hasArchiveName = !string.IsNullOrWhiteSpace(archiveBaseName);

        if (sharedRoot && !hasArchiveName)
        {
            warnings.Add("多个包共用同一个输出根（同一个文件夹里的包都落进这一层）时没有提供归档基名，"
                         + $"其余物无法按包名隔离，同一个目录里的多个包会共用一个 {ProcessArtifactLayout.ArtifactDirectoryName} 目录");
        }

        string artifactRootDirectory = ProcessArtifactLayout.ResolveArtifactDirectoryWithName(
            contentParentDirectory,
            artifactDirectoryName,
            hasArchiveName ? archiveBaseName : null,
            sharedRoot);

        var plan = new FinalizeArtifactMovePlan();

        if (string.IsNullOrWhiteSpace(artifactRootDirectory))
        {
            plan.FailureReason = "其余物目录算不出来（目标目录无法规范化）";
            return plan;
        }

        plan.Directory = artifactRootDirectory;

        foreach (StagedTreeNode node in artifactNodes)
        {
            string from = FinalizePathSupport.FromPath(stagingRoot, node);

            if (contentSources.Any(source => FinalizePathSupport.IsSameOrChildPath(from, source)))
            {
                continue;
            }

            string relative = node.RelativePath;
            string stripped = FinalizePathSupport.StripLeadingArtifactSegment(relative);

            if (stripped.Length > 0)
            {
                relative = stripped;
            }

            // 落点名字必须清洗：其余物里也会有 Windows 非法名（解压出来的东西什么都可能有），
            // 不清洗的话执行阶段会直接在 File.Move 上炸掉。
            relative = FinalizePathSupport.SanitizeRelativePath(relative);

            plan.Moves.Add(new PlannedMove(
                from,
                FinalizePathSupport.Unique(
                    FinalizePathSupport.SafeCombine(artifactRootDirectory, relative),
                    used)));

            plan.TotalSize += StagedTreeFacts.SubtreeFileSize(node);
        }

        return plan;
    }

    /// <summary>
    /// 去掉"父亲已经在列表里"的其余物节点：只要顶层项，否则计划里会同时出现目录和它里面的东西。
    /// 按深度从浅到深处理，浅的自然先被保留；返回时**恢复调用方给的顺序**（按暂存路径），
    /// 保证计划顺序稳定、可预期。
    /// </summary>
    private static List<StagedTreeNode> PruneNestedArtifacts(List<StagedTreeNode> nodes)
    {
        var kept = new List<StagedTreeNode>();

        foreach (StagedTreeNode node in nodes.OrderBy(StagedTreeFacts.Depth).ToList())
        {
            bool nested = false;

            foreach (StagedTreeNode parent in kept)
            {
                if (StagedTreeFacts.IsDescendantOf(node, parent))
                {
                    nested = true;
                    break;
                }
            }

            if (!nested)
            {
                kept.Add(node);
            }
        }

        return nodes.Where(kept.Contains).ToList();
    }
}

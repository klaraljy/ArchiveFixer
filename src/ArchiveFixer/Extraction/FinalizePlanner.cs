namespace ArchiveFixer.Extraction;

/// <summary>
/// 定稿布局规划的编排：验证输入 → 建暂存树 → 定位内容物根/其余物 → 分析内容物形态 →
/// 定判定表与那一层名字 → 规划内容物与其余物移动 → 汇成 <see cref="FinalizePlan"/>。
///
/// <para>
/// 各步的规则在协作类里（<see cref="FinalizeStagedTree"/> / <see cref="FinalizeContentAnalyzer"/> /
/// <see cref="FinalizeLayoutDecider"/> / <see cref="FinalizeContentMover"/> /
/// <see cref="FinalizeArtifactPlanner"/> / <see cref="FinalizePlanAssembler"/>）；本类只负责
/// **顺序与共享事实**（例如重名占位集合要贯穿内容物与其余物、其余物目录与内容物同级）。
/// </para>
/// <para>纯规划：⛔ 不建目录、不移动、不删除。</para>
/// </summary>
internal static class FinalizePlanner
{
    public static FinalizePlan Plan(
        IReadOnlyList<StagedEntry>? stagedEntries,
        string? destinationDirectory,
        TerminalLayoutMode terminalLayout = TerminalLayoutMode.KeepLastFolder,
        string? archiveBaseName = null,
        string? contentRoot = null,
        string? stagingRoot = null,
        bool sharedOutputRoot = false,
        bool suppressPackageFolderLayer = false,
        string? innermostPackageBaseName = null)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return FinalizePlan.Failure("没有目标目录（destDir 为空），无法规划定稿布局");
        }

        var warnings = new List<string>();
        string destDir = destinationDirectory!.Trim().TrimEnd('\\', '/');
        string staging = (stagingRoot ?? string.Empty).Trim();

        if (destDir.Length == 0)
        {
            return FinalizePlan.Failure("目标目录只有一个路径分隔符，无法规划定稿布局");
        }

        StagedTreeNode root = FinalizeStagedTree.Build(stagedEntries, warnings);

        bool contentRootSpecified = !string.IsNullOrWhiteSpace(contentRoot);
        StagedTreeNode? contentScope = null;

        if (contentRootSpecified)
        {
            string[]? segments = FinalizeStagedTree.SplitRelativePath(contentRoot!);
            contentScope = segments == null ? null : FinalizeStagedTree.FindNode(root, segments);

            if (contentScope == null)
            {
                // 内容物根找不到就**不能**退化成"整棵树都是内容物"：那会把暂存区里的过程物
                // 当成用户的东西搬出去。宁可报"没有内容物"也不搬错。
                warnings.Add($"暂存树里找不到内容物根 \"{contentRoot}\"，按没有内容物处理");
            }
        }

        var fileMemo = new Dictionary<StagedTreeNode, bool>();
        bool HasAnyFileCached(StagedTreeNode node) => StagedTreeFacts.HasAnyFile(node, fileMemo);

        bool IsArtifact(StagedTreeNode node) =>
            FinalizeArtifactPlanner.IsArtifact(node, contentScope, contentRootSpecified);

        List<StagedTreeNode> artifactRoots = FinalizeArtifactPlanner.CollectRoots(root, IsArtifact);

        // ── 判定表：从尾巴倒着看 ────────────────────────────────────────────
        StagedTreeNode? startScope = contentScope ?? (contentRootSpecified ? null : root);
        FinalizeContentShape shape = FinalizeContentAnalyzer.Analyze(startScope, IsArtifact, HasAnyFileCached);

        if (!shape.HasContent && contentScope != null && contentScope.Parent != null)
        {
            /*
             * 内容物根里一个文件都没有（解压出来全是空壳/杂物）：整棵都当其余物，一次搬走。
             * 比"逐个空壳搬"完整得多 —— 那样会把内容物根里的非空杂物落在暂存区没人管。
             * 虚拟根（没指定内容物根时）不能这么干，所以上面要求 contentScope.Parent != null。
             */
            artifactRoots.Add(contentScope);
            shape.Shells.Clear();
        }

        bool hasArtifacts = artifactRoots.Count > 0 || shape.Shells.Count > 0;

        FinalizeLayoutDecision layout = FinalizeLayoutDecider.Resolve(
            shape,
            hasArtifacts,
            destDir,
            terminalLayout,
            archiveBaseName,
            warnings,
            suppressPackageFolderLayer,
            innermostPackageBaseName);

        // ── 内容物落法 ──────────────────────────────────────────────────────
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        FinalizeContentMovePlan content = FinalizeContentMover.Plan(
            shape,
            layout.Kind,
            layout.WrapperName,
            layout.WrapperChainNode,
            destDir,
            staging,
            used);

        // ── 其余物归置：全部进 destDir\其余物\，保持相对结构 ────────────────
        FinalizeArtifactMovePlan artifacts = FinalizeArtifactPlanner.Plan(
            artifactRoots,
            shape.Shells,
            content.Moves,
            content.ParentDirectory,
            layout.WrapperName,
            archiveBaseName,
            sharedOutputRoot,
            warnings,
            staging,
            used);

        if (artifacts.Failed)
        {
            return FinalizePlan.Failure(artifacts.FailureReason);
        }

        return FinalizePlanAssembler.Build(layout, destDir, content, artifacts, warnings);
    }
}

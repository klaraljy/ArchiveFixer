namespace ArchiveFixer.Extraction;

/// <summary>
/// 内容物落法：把"东西真正待着的那一层"按已定好的那一层名字搬进目标目录。
///
/// <para>
/// 只出移动计划，⛔ 不执行移动、不建目录。重名一律让位（<c>名字(1)</c>），绝不覆盖。
/// </para>
/// </summary>
internal static class FinalizeContentMover
{
    /// <param name="shape">内容物形态（那一层的活条目与链）。</param>
    /// <param name="kind">判定表档位（决定单文件直放这一支）。</param>
    /// <param name="wrapperName">套出来的那一层名字；null = 不套层。</param>
    /// <param name="wrapperChainNode">
    /// 名字来源的那个链上节点：非空时**整棵子树一次搬走**（比逐个子项搬少一堆操作，也不会中途留半个内容物）。
    /// </param>
    /// <param name="destinationDirectory">任务目标目录。</param>
    /// <param name="stagingRoot">暂存根绝对路径；空则 <c>From</c> 只有相对路径。</param>
    /// <param name="used">本批已占用的落点（内容物先占，其余物随后；两侧共用同一份）。</param>
    public static FinalizeContentMovePlan Plan(
        FinalizeContentShape shape,
        FinalizeLayoutKind kind,
        string? wrapperName,
        StagedTreeNode? wrapperChainNode,
        string destinationDirectory,
        string stagingRoot,
        HashSet<string> used)
    {
        var plan = new FinalizeContentMovePlan
        {
            ParentDirectory = destinationDirectory
        };

        /*
         * ⚠ 这里多一个 `wrapperName == null` 不是多余的（接手代理 2026-09-30）：
         *
         * 判定表 ①（单个文件直放 destDir）**不读 wrapperName** —— 上面最后那道闸门即便把
         * "最里层"补了回来，这一支也会把内容物直接放进 destDir 里（闸门形同不存在）。
         * 今天 `DecideLayout` 里那条 `hasInnermostPackage → WrapInFolder` 提前返回挡住了这种组合，
         * 可"最里层不许被吃掉"这条红线**不该只挂在另一个方法的返回顺序上**：
         * 一旦有人重排判定表，闸门就该自己生效。展开过内层包时 wrapperName 必然非空
         * （闸门保证），于是这一支不再命中，落到下面按 wrapperName 套层的分支。
         * 只解了一层（没有内层包）时 wrapperName 本来就是 null ⇒ 行为与加这条之前**逐字相同**。
         */
        if (kind == FinalizeLayoutKind.SingleFileToDestination && wrapperName == null)
        {
            StagedTreeNode file = shape.Items[0];
            plan.Moves.Add(new PlannedMove(
                FinalizePathSupport.FromPath(stagingRoot, file),
                FinalizePathSupport.Unique(
                    FinalizePathSupport.SafeCombine(destinationDirectory, FinalizePathSupport.SafeName(file.Name)),
                    used)));

            plan.MovedNodes.Add(file);
            plan.ParentDirectory = destinationDirectory;
        }
        else if (shape.HasContent)
        {
            if (wrapperName != null)
            {
                string wrapperTarget = FinalizePathSupport.Unique(
                    FinalizePathSupport.SafeCombine(destinationDirectory, wrapperName),
                    used);

                plan.ParentDirectory = wrapperTarget;

                if (wrapperChainNode != null)
                {
                    // 整棵子树一次搬走：比逐个子项搬少一堆操作，也不会中途留半个内容物。
                    // 搬的必须是**名字来源那一个**节点（wrapperChainNode），否则会把子层改名搬走。
                    plan.Moves.Add(new PlannedMove(
                        FinalizePathSupport.FromPath(stagingRoot, wrapperChainNode),
                        wrapperTarget));

                    plan.MovedNodes.Add(wrapperChainNode);
                }
                else
                {
                    foreach (StagedTreeNode item in shape.Items)
                    {
                        plan.Moves.Add(new PlannedMove(
                            FinalizePathSupport.FromPath(stagingRoot, item),
                            FinalizePathSupport.Unique(
                                FinalizePathSupport.SafeCombine(wrapperTarget, FinalizePathSupport.SafeName(item.Name)),
                                used)));

                        plan.MovedNodes.Add(item);
                    }
                }
            }
            else
            {
                plan.ParentDirectory = destinationDirectory;

                foreach (StagedTreeNode item in shape.Items)
                {
                    plan.Moves.Add(new PlannedMove(
                        FinalizePathSupport.FromPath(stagingRoot, item),
                        FinalizePathSupport.Unique(
                            FinalizePathSupport.SafeCombine(destinationDirectory, FinalizePathSupport.SafeName(item.Name)),
                            used)));

                    plan.MovedNodes.Add(item);
                }
            }
        }

        return plan;
    }
}

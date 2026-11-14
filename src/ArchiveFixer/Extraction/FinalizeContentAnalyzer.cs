namespace ArchiveFixer.Extraction;

/// <summary>
/// 内容物形态分析：从内容物根往下走：跳过纯空壳目录，沿着"这一层没有文件、只有一个子目录"的链一路到底，
/// 停在"东西真正待着的那一层"。
///
/// <para>
/// 空壳什么时候算"被跳过、要单独规划"，取决于内容物是怎么搬的：
/// 走链（<see cref="FinalizeContentShape.Chain"/> 非空）时最里面那一层是**整棵搬走**的，
/// 它里面的空壳跟着一起走（不能再单独规划一次，否则执行时源路径已经不在暂存区了）；
/// 没走链时内容物是逐项搬的，同层的空壳是它们的兄弟，必须单独规划。
/// </para>
/// </summary>
internal static class FinalizeContentAnalyzer
{
    public static FinalizeContentShape Analyze(
        StagedTreeNode? startScope,
        Func<StagedTreeNode, bool> isArtifact,
        Func<StagedTreeNode, bool> hasAnyFile)
    {
        var shape = new FinalizeContentShape();

        if (startScope == null)
        {
            return shape;
        }

        if (!startScope.IsDirectory)
        {
            // 内容物根指向一个**文件**（调用方直接点名了产物文件）：它本身就是终端内容物。
            // 不这么判的话它的 Children 为空，会被当成"没有内容物"，然后连人带文件被扫进其余物。
            shape.HasContent = true;
            shape.Items.Add(startScope);
            return shape;
        }

        StagedTreeNode current = startScope;

        while (true)
        {
            var liveFiles = new List<StagedTreeNode>();
            var liveDirectories = new List<StagedTreeNode>();
            var shellsHere = new List<StagedTreeNode>();

            foreach (StagedTreeNode child in current.Children)
            {
                if (isArtifact(child))
                {
                    continue;
                }

                if (child.IsDirectory && !hasAnyFile(child))
                {
                    // 纯空壳：子树里一个文件都没有。它不是内容物，也不该挡住"最后那个有意义的文件夹"。
                    shellsHere.Add(child);
                    continue;
                }

                if (child.IsDirectory)
                {
                    liveDirectories.Add(child);
                }
                else
                {
                    liveFiles.Add(child);
                }
            }

            if (liveDirectories.Count == 0 && liveFiles.Count == 0)
            {
                shape.HasContent = false;
                shape.Shells.AddRange(shellsHere);
                return shape;
            }

            if (liveDirectories.Count == 1 && liveFiles.Count == 0)
            {
                shape.Shells.AddRange(shellsHere);
                shape.Chain.Add(liveDirectories[0]);
                current = liveDirectories[0];
                continue;
            }

            shape.HasContent = true;
            shape.Items.AddRange(liveFiles);
            shape.Items.AddRange(liveDirectories);

            if (shape.Chain.Count == 0)
            {
                shape.Shells.AddRange(shellsHere);
            }

            return shape;
        }
    }
}

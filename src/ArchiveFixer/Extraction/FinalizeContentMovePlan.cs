namespace ArchiveFixer.Extraction;

/// <summary>内容物落法的规划结果：移动清单 + 参与搬运的节点 + 内容物最终所在目录。</summary>
internal sealed class FinalizeContentMovePlan
{
    /// <summary>内容物的移动（<c>From</c> 已按暂存根拼好）。</summary>
    public List<PlannedMove> Moves { get; } = new();

    /// <summary>被整棵或逐项搬走的内容物节点，用来统计文件数与字节数。</summary>
    public List<StagedTreeNode> MovedNodes { get; } = new();

    /// <summary>内容物最终所在的目录：<c>destDir</c> 或 <c>destDir\&lt;套的那一层&gt;</c>。</summary>
    public string ParentDirectory { get; set; } = string.Empty;
}

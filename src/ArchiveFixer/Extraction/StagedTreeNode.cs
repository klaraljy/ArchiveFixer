namespace ArchiveFixer.Extraction;

/// <summary>
/// 暂存树的一个节点（定稿规划唯一的输入形状）。
///
/// <para>
/// 由 <see cref="FinalizeStagedTree.Build"/> 从调用方给的 <see cref="StagedEntry"/> 列表拼出来：
/// 规划是纯函数，⛔ 不遍历磁盘 —— 节点只承载"相对路径 / 文件还是目录 / 字节数 /
/// 调用方显式标的其余物"这几件只读事实。
/// </para>
/// </summary>
internal sealed class StagedTreeNode
{
    public StagedTreeNode(string name, string relativePath, StagedTreeNode? parent, bool isDirectory)
    {
        Name = name;
        RelativePath = relativePath;
        Parent = parent;
        IsDirectory = isDirectory;
    }

    public string Name { get; }

    /// <summary>相对暂存根的路径（<c>\</c> 分隔），可直接拼 <c>From</c>。</summary>
    public string RelativePath { get; }

    public StagedTreeNode? Parent { get; }

    public bool IsDirectory { get; set; }

    /// <summary>文件字节数（目录恒为 0）。</summary>
    public long Size { get; set; }

    /// <summary>调用方显式标的其余物。</summary>
    public bool MarkedArtifact { get; set; }

    public List<StagedTreeNode> Children { get; } = new();

    public StagedTreeNode? FindChild(string name)
    {
        foreach (StagedTreeNode child in Children)
        {
            if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }
}

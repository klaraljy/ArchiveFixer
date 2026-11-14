namespace ArchiveFixer.Extraction;

/// <summary>内容物形态：链、跳过的空壳、以及"东西真正待着的那一层"。</summary>
internal sealed class FinalizeContentShape
{
    /// <summary>沿路经过的"只有一个子目录、没有别的文件"的层（从外到内）。</summary>
    public List<StagedTreeNode> Chain { get; } = new();

    /// <summary>沿路跳过的纯空壳目录（子树里一个文件都没有）。</summary>
    public List<StagedTreeNode> Shells { get; } = new();

    /// <summary>那一层的活条目（文件 + 目录，已排除其余物与纯空壳）。</summary>
    public List<StagedTreeNode> Items { get; } = new();

    public bool HasContent { get; set; }

    /// <summary>判定表 1：那一层只有一个文件、没有目录、也没有走过链。</summary>
    public bool IsSingleFile => HasContent && Chain.Count == 0 && Items.Count == 1 && !Items[0].IsDirectory;
}

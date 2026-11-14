namespace ArchiveFixer.Extraction;

/// <summary>
/// 一次定稿布局判定的结论：判定表档位 + "套出来的那一层"叫什么（null = 不套层）
/// + 取名/搬移共同依据的链上节点。
///
/// <para>
/// ⚠ <see cref="WrapperChainNode"/> 与 <see cref="WrapperName"/> **必须成对**使用：名字与
/// "搬哪一棵子树"取自同一个节点，否则会把 <c>P</c> 改名成 <c>T …</c> 搬走。
/// </para>
/// </summary>
internal sealed class FinalizeLayoutDecision
{
    public FinalizeLayoutKind Kind { get; init; } = FinalizeLayoutKind.Empty;

    /// <summary>套出来的那一层文件夹名；null 表示**不套层**（内容直接落 <c>destDir</c>）。</summary>
    public string? WrapperName { get; init; }

    /// <summary>链上<strong>最外层</strong>（展开过内层包）或最深（只解一层）的那个节点；没有链时为 null。</summary>
    public StagedTreeNode? WrapperChainNode { get; init; }
}

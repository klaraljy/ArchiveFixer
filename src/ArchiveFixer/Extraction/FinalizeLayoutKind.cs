namespace ArchiveFixer.Extraction;

/// <summary>
/// 定稿布局的结论（规格 §3.1 判定表，按顺序判、先命中先返回）。**机器可判**。
/// </summary>
public enum FinalizeLayoutKind
{
    /// <summary>输入不合法（没有目标目录等），没有任何计划。</summary>
    Failed = 0,

    /// <summary>暂存区里既没有内容物、也没有其余物：没什么可定稿的。</summary>
    Empty,

    /// <summary>判定表 1：终端只有一个文件 → 直接放 <c>destDir\</c> 下。</summary>
    SingleFileToDestination,

    /// <summary>判定表 2：多个文件 / 多个文件夹 / 自带一层文件夹 → 在 <c>destDir\</c> 下套一层。</summary>
    WrapInFolder,

    /// <summary>判定表 3：被多重空目录嵌套包裹 → 把最后那个有意义的文件夹提上来。</summary>
    PromoteInnermostFolder,

    /// <summary>只剩其余物（没有任何内容物）：只做其余物集中，目标目录下不会有内容物。</summary>
    ProcessArtifactsOnly
}


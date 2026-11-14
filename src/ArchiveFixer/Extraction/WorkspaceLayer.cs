namespace ArchiveFixer.Extraction;

/// <summary>
/// 工作区里的一层。
///
/// 目录布局（设计.md §十三：中间产物不许写进源目录、也不许直接写最终目录）：
/// <code>
/// C:\t\ws\&lt;taskId&gt;\layer-000\output\...   第 0 层（最外层归档）的产物
/// C:\t\ws\&lt;taskId&gt;\layer-001\output\...   第 1 层的产物（由 layer-000 里的内层归档解出）
/// </code>
/// 每层独立一个 output 目录，是为了让"失败定位到层"成为可能：
/// 停在某一层时，前面每一层的产物都还在原地，用户能直接看、也能从那一层重试。
/// </summary>
public sealed class WorkspaceLayer
{
    /// <summary>层号，从 0 开始（0 = 最外层归档）。</summary>
    public int Depth { get; init; }

    /// <summary>本层的目录（<c>layer-000</c>）。</summary>
    public string DirectoryPath { get; init; } = string.Empty;

    /// <summary>本层的产物目录（<c>layer-000\output</c>）。</summary>
    public string OutputPath { get; init; } = string.Empty;

    /// <summary>本层要解的那个归档（第 0 层是任务的 CurrentPath，之后是上一层产物里的内层归档）。</summary>
    public string InputPath { get; init; } = string.Empty;

    /// <summary>
    /// 这一层的产物目录里**解完那一刻**有几个条目（<c>null</c> = 没记过）。
    ///
    /// <para><b>为什么要记</b>（2026-10-05，递归路逐层回收）：发布侧要回答"这一层除了交给下一层的
    /// 内层包，还有没有别的东西"（判据 <see cref="PackageLayerRules.ProducedOwnContent"/>，
    /// 决定该不该给这一层留一个包名目录），而那条链的过程物现在会被**当场删掉** ——
    /// 发布时再读盘，中间层的目录已经空了 ⇒ 判成"没出内容物" ⇒ 该留的层目录被摊掉
    /// （实测：两遍对照的产物路径从 <c>…\level2\level3\…</c> 变成 <c>…\level3\…</c>）。
    /// 所以递归核心在**动手回收之前**把那一刻的条目数记在这里，判据本身一个字没改。</para>
    /// </summary>
    public int? EntryCountBeforeReclaim { get; set; }

    /// <summary>
    /// 这一层到底解开了没有。**就地替换发布**只搬"真的解开了"的层
    /// （见 <see cref="Publish"/> 的三条边界）：失败的那一层没产出任何东西，
    /// 把它的包删掉、或者把它的空目录搬出去，都只会让用户唯一的那一份线索消失。
    ///
    /// <para>
    /// 默认 **true**：建出来的层按"会有产物"算，只有递归核心收到引擎的失败结论时才置 false
    /// （它只在**一处**标记，见 RecursiveExtractor 主循环）。
    /// 默认 true 也让"手工搭一个工作区直接发布"的调用方（诊断、单测）不必先记得设它 ——
    /// 那种场合本来就没有"失败层"这回事。
    /// </para>
    /// </summary>
    public bool Successful { get; set; } = true;
}

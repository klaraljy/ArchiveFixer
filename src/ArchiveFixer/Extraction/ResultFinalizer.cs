using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 定稿布局规划（规格 §3"从尾巴倒着判定落点"）。
///
/// <para>
/// 输入：暂存产物树的抽象描述 + <c>destDir</c> + 终端落法 +（可选）归档基名。
/// 输出：移动计划 + 其余物清单 + 一个结论枚举。**不执行移动、不删任何东西**——执行由调用方接线。
/// </para>
/// <para>
/// 判定表（规格 §3.1，**按顺序判、先命中先返回**）：
/// ① 终端单个文件 → 直接放 <c>destDir</c>；
/// ② 多个文件/多个文件夹/自带一层文件夹 → 在 <c>destDir</c> 下套一层（名字取最外层文件夹名，没有就取归档基名）；
/// ③ 被多重空目录嵌套包裹 → 把最后那个有意义的文件夹提上来；
/// ④ 单链（每层只有一个文件夹、没有别的文件）→ 塌缩为最深层那个文件夹名（等价原 <c>移动文件夹2.bat</c>）。
/// </para>
/// <para>
/// ③ 与 ④ 的**落法**是同一个动作（沿着"没有文件、只有一个子目录"的链一路下去，拿最深处那个名字），
/// 区别只在触发条件：③ 路上跳过了纯空壳目录，④ 纯属一层套一层的单链。分开报是为了让用户看得懂
/// "为什么这一层没了"。判定表 4 种形态的**结果路径**在测试里逐条钉死。
/// </para>
/// <para>
/// ⚠ **源包不在这里规划**：它既不是内容物、也不是"解压产生的东西"，而是用户给的输入。
/// 把它搬进其余物是**定稿 + 校验通过 + 未取消**之后由 <c>ExtractionCoordinator</c> 单独做的事
/// （决策 D-9/D-11/D-12，搬运本体见 <see cref="SourcePackageMover"/>）。
/// 规划器只需保证一件事：**别把源包当成内容物**（它本来就不在暂存树里，天然满足）。
/// </para>
/// <para>
/// 职责分工：本类只留公开 API（薄转发），编排在 <see cref="FinalizePlanner"/>；
/// 暂存树、内容物形态、判定表与取名、内容物落法、其余物归置、汇总各有协作类。
/// </para>
/// </summary>
public static class ResultFinalizer
{
    /// <summary>
    /// 其余物集中目录名（契约 §3.2：固定叫"其余物"，不许散落字面量）。
    ///
    /// 2026-09-22 用户把这一层由「过程物」改名「其余物」（源包也移进来，决策 D-8/D-9）。
    /// 名字的**唯一来源**是 <see cref="ProcessArtifactLayout.ArtifactDirectoryName"/>，
    /// 这里只是保留一个旧名字的转发常量，免得外面还有引用它的人各写一份字面量。
    /// </summary>
    public const string ProcessArtifactDirectoryName = ProcessArtifactLayout.ArtifactDirectoryName;

    /// <summary>规划一次定稿布局。</summary>
    /// <param name="stagedEntries">暂存树的全部条目（相对 <paramref name="stagingRoot"/>）。</param>
    /// <param name="destinationDirectory">规格 §1 算出来的 <c>destDir</c>。</param>
    /// <param name="terminalLayout">终端落法（规格 §3.1 的可选项）。</param>
    /// <param name="archiveBaseName">
    /// 终端归档（**最内层那个**）的基名，用于"没有最外层文件夹名"时给那一层取名。
    /// 传空则在需要时退回 <c>destDir</c> 自己的末段名并给出提醒。
    /// </param>
    /// <param name="contentRoot">
    /// 内容物在暂存树里的相对根（例：<c>out</c>）。给了它，根之外的一切都算其余物；
    /// 不给则整棵树都是内容物，其余物只能靠 <see cref="StagedEntry.IsProcessArtifact"/> 显式标。
    /// </param>
    /// <param name="stagingRoot">
    /// 暂存根绝对路径。给了，计划里的 <c>From</c> 就是可直接执行的绝对路径；不给就只有相对路径。
    /// </param>
    /// <param name="sharedOutputRoot">
    /// 这个落点目录是不是**同一次导入里的多个包共用**的（可选，默认 false）。
    /// 只影响**其余物**放在哪（决策 D-10）：
    /// 共用根时其余物再套一层包基名（<c>&lt;共用根&gt;\其余物\&lt;包基名&gt;\</c>），
    /// 否则几十上百个包的分卷和过程物会在 <c>其余物\</c> 里互相撞名、也分不清是谁的。
    /// 每包一个目录的落点直接用 <c>destDir\其余物\</c>，**不再多套一层** ——
    /// 用户最反感"凭空多弄一个文件夹"。
    ///
    /// <para>
    /// ⚠ 这个事实**只由落点解析给出**（<see cref="OutputPlacementResult.SharesDestinationWithOtherPackages"/>）：
    /// 用户 2026-09-24 第 13 条之后唯一会共用根的是"添加文件夹 + 指定位置"那一档
    /// （<c>BBB\222\</c>，文件夹里每个包都落进去）。调用方**不许**按模式自己再推一遍。
    /// </para>
    /// </param>
    /// <param name="suppressPackageFolderLayer">
    /// 这一次**不套"包名那一层"**。三种成因（判据在调用方，这里只认这一个布尔）：
    ///
    /// <list type="bullet">
    /// <item><description><b>手动档「解压到当前文件夹」</b>（用户 2026-09-27，语义 = WinRAR 右键那一句）：
    /// 落点**就是源包所在的那一层**，内容物按包内原样解开（<c>111\222.rar</c> → <c>111\内容物</c>）；
    /// 一键处理 / 批量**永远不传它**（用户红线：批量一律套包名那一层，不摊平）。</description></item>
    /// <item><description><b>续解的那一层已经在落点路径里</b>（忠实档 / 分支退化档，见
    /// <c>ExtractionCoordinator.IsContinuationLayerAlreadyInPath</c>）：再套就是 <c>666\666</c>。</description></item>
    /// <item><description><b>过程物名不成层</b>：续解的这一层本身就是分卷组的一卷（<c>59768866.001</c>），
    /// 它的基名不是用户认得的包名 —— 真机上那个 <c>…\包名\59768866\真内容</c> 就是它造的。</description></item>
    /// </list>
    ///
    /// <para>
    /// ⚠ 它**只免掉"包名那一层"**：归档**自带的**那一层文件夹（内容物本身是一条单链文件夹）
    /// 照旧保留 —— 手动档要的就是"按包内原样解开"（<c>111\666\a.mp4</c>），
    /// 而不是把归档自己的目录结构也拆掉。
    /// </para>
    /// </param>
    /// <param name="innermostPackageBaseName">
    /// **最后一个被展开的内层包**的包基名（递归链里最后一个真的解开了的内层包；
    /// 没有内层包 —— 也就是只解了一层 —— 时传空）。唯一来源见
    /// <see cref="PackageLayerRules.ResolveBaseName"/>。
    ///
    /// <para>
    /// ⛔ 用户 2026-09-30 真机红线：**落点最少两层文件夹**。最外层 = 以源包（任务）包名命名的
    /// <c>destDir</c>（由上面的落点规则给，本规划器一个字都不动）；最里层 = 以最后一个压缩包
    /// 那一层命名的目录，**必须存在，任何分支都不许省**。所以这个参数非空时，判定表里那些
    /// "塌缩 / 不套层 / 提上来"的分支、以及特定解压例外档，**都不许把最里层吃掉**。
    /// </para>
    /// <para>
    /// 那一层叫什么（判据只有这一处）：内容物根下面**只有一个文件夹**（<c>shape.Chain</c>）
    /// 时就是**最外层那一个** —— 它是内层包自己产出的文件夹，**成为**最里层，更深的链原样待在它里面
    /// （真机现场 <c>T 小小绘 推特大合集 330P+454V-9.31G\P|V</c> 正是这一档）；
    /// 内容物直接摊在内层包根上（连一个文件夹都没有）时用这个包基名建一层
    /// （与 <c>destDir</c> 最后一段同名也**照建**：用户宁可多一层，也不要内容物摊平）。
    /// </para>
    /// <para>
    /// 传空 = 与加这条红线之前**逐字相同**（既有用例钉着那一份口径，不许弄红）。
    /// </para>
    /// </param>
    public static FinalizePlan Plan(
        OutputPlan outputPlan,
        IReadOnlyList<StagedEntry>? stagedEntries,
        string? contentRoot = null,
        string? stagingRoot = null,
        bool sharedOutputRoot = false,
        bool suppressPackageFolderLayer = false,
        string? innermostPackageBaseName = null)
    {
        if (outputPlan == null || !outputPlan.Success)
        {
            return FinalizePlan.Failure(
                outputPlan?.Message ?? "没有落点计划（OutputPlan 为空），无法规划定稿布局");
        }

        return FinalizePlanner.Plan(
            stagedEntries,
            outputPlan.DestinationDirectory,
            archiveBaseName: outputPlan.PackageBaseName,
            contentRoot: contentRoot,
            stagingRoot: stagingRoot,
            sharedOutputRoot: sharedOutputRoot,
            suppressPackageFolderLayer: suppressPackageFolderLayer,
            innermostPackageBaseName: innermostPackageBaseName);
    }

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
        return FinalizePlanner.Plan(
            stagedEntries,
            destinationDirectory,
            terminalLayout,
            archiveBaseName,
            contentRoot,
            stagingRoot,
            sharedOutputRoot,
            suppressPackageFolderLayer,
            innermostPackageBaseName);
    }
}

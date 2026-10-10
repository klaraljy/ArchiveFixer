using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 一条**计划中**的移动（<c>From</c> → <c>To</c>）。纯数据，本类只规划、绝不执行。
    ///
    /// <para>
    /// 用 <c>readonly record struct</c> 而不是匿名元组：测试可以直接比值（值语义），
    /// 调用方也能解构成 <c>(From, To)</c>，还能直接喂给"逐个搬"的执行器。
    /// </para>
    /// </summary>
    public readonly record struct PlannedMove(string From, string To);

    /// <summary>
    /// 暂存区里的一个条目（文件或目录），路径**相对暂存根**。
    ///
    /// <para>
    /// 这是定稿规划唯一的输入形状：规划是纯函数，**不遍历磁盘** —— 暂存树由刚跑完解压的
    /// 调用方描述出来，规划只做结构与字符串推理。这样它才能在真搬家之前就把"最终长什么样"算清楚
    /// （用户要的"从尾巴倒着想"）。
    /// </para>
    /// </summary>
    public sealed class StagedEntry
    {
        /// <summary>相对暂存根的路径，<c>\</c> 或 <c>/</c> 都认（例：<c>out\666\a.mp4</c>）。</summary>
        public string RelativePath { get; init; } = string.Empty;

        /// <summary>文件字节数；目录填 0（只用来统计"其余物总大小"，契约 §3.2）。</summary>
        public long Size { get; init; }

        /// <summary>是不是目录。</summary>
        public bool IsDirectory { get; init; }

        /// <summary>
        /// 是不是"其余物"（内层归档、分卷、抠出来的过程物……）。
        ///
        /// <para>
        /// **必须由调用方显式标**：只有跑过暂存阶段的人知道哪些是中间产物。
        /// 规划器按后缀猜是不行的 —— 内容物里本来就可能有一个用户要的 <c>.zip</c>，
        /// 猜错就把用户的东西扔进了 <c>其余物</c>。
        /// </para>
        /// <para>
        /// 另一种等价办法是不标、改用 <c>Plan(..., contentRoot: "out")</c>：
        /// 内容物根之外的一切都算其余物。
        /// </para>
        /// </summary>
        public bool IsProcessArtifact { get; init; }
    }

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

        /// <summary>判定表 4：单链（每层只有一个文件夹、没有别的文件）→ 塌缩为最深层那个文件夹名。</summary>
        CollapseSingleChain,

        /// <summary>只剩其余物（没有任何内容物）：只做其余物集中，目标目录下不会有内容物。</summary>
        ProcessArtifactsOnly
    }

    /// <summary>
    /// 定稿布局计划。**纯规划**：只有路径字符串，没有一次磁盘操作。
    /// </summary>
    public sealed class FinalizePlan
    {
        /// <summary>判定表结论。</summary>
        public FinalizeLayoutKind Layout { get; init; } = FinalizeLayoutKind.Empty;

        /// <summary>失败原因（仅 <see cref="FinalizeLayoutKind.Failed"/> 时非空）。</summary>
        public string FailureReason { get; init; } = string.Empty;

        /// <summary>任务目标目录（规格 §1 算出来的 <c>destDir</c>）。</summary>
        public string DestinationDirectory { get; init; } = string.Empty;

        /// <summary>内容物最终所在的目录：<c>destDir</c> 或 <c>destDir\&lt;套的那一层&gt;</c>。</summary>
        public string ContentParentDirectory { get; init; } = string.Empty;

        /// <summary>套出来的那一层文件夹名；没有套层时为空。</summary>
        public string ContentDirectoryName { get; init; } = string.Empty;

        /// <summary>
        /// 全部移动计划（**内容物在前、其余物在后**）。
        ///
        /// 只有"顶层项"：绝不会同时出现某个目录和它里面的东西 —— 那样执行时会先搬父再搬子，子项必然失败。
        /// </summary>
        public IReadOnlyList<PlannedMove> Moves { get; init; } = Array.Empty<PlannedMove>();

        /// <summary>只属于内容物的移动。</summary>
        public IReadOnlyList<PlannedMove> ContentMoves { get; init; } = Array.Empty<PlannedMove>();

        /// <summary>只属于其余物的移动（都已归到 <see cref="ProcessArtifactDirectory"/> 下）。</summary>
        public IReadOnlyList<PlannedMove> ProcessArtifactMoves { get; init; } = Array.Empty<PlannedMove>();

        /// <summary>其余物集中目录（<c>destDir\其余物</c>）。契约 §3.2：其余物只允许出现在这一处。</summary>
        public string ProcessArtifactDirectory { get; init; } = string.Empty;

        /// <summary>其余物总字节数（契约 §3.2 要求详情里报出来）。</summary>
        public long ProcessArtifactTotalSize { get; init; }

        /// <summary>内容物文件数。</summary>
        public int ContentFileCount { get; init; }

        /// <summary>内容物总字节数。</summary>
        public long ContentTotalSize { get; init; }

        /// <summary>给用户看的一句话。</summary>
        public string Summary { get; init; } = string.Empty;

        /// <summary>规划过程中的提醒（忽略的非法条目、退而求其次的取名等），不改变结论。</summary>
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

        /// <summary>
        /// 这一单是不是真的按**特定解压**跑了（判定表里"要套的那一层"没有套）。
        ///
        /// <para>规划器只报事实，日志与任务详情据此说清"这次为什么少了一层"。</para>
        /// </summary>
        public bool SpecialExtractionApplied { get; init; }

        internal static FinalizePlan Failure(string reason)
        {
            return new FinalizePlan
            {
                Layout = FinalizeLayoutKind.Failed,
                FailureReason = reason,
                Summary = reason,
                Warnings = new[] { reason }
            };
        }
    }

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
        /// <param name="specialExtraction">
        /// 这一批生效的**特定解压**（规格 §3.5；默认 <see cref="SpecialExtractionPlan.Off"/> =
        /// 与加这条功能之前逐字相同）。
        ///
        /// <para>
        /// 它只做一件事：<see cref="SpecialExtractionEffect.SingleContentLayer"/> 生效、且包内确实是
        /// **一条单链**时，<b>判定表里"要套的那一层"不再套</b> —— 内容物直接落在成品目录里
        /// （<c>222\1111\内容物</c>，包名那一层一个字都不动）。出现并列的多个文件夹、或几个包共用
        /// 同一个成品目录时**不塌**，按原判定表套一层并写一条 WARN 说明原因（**绝不静默**）。
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
            IReadOnlyList<StagedEntry>? stagedEntries,
            string? destinationDirectory,
            TerminalLayoutMode terminalLayout = TerminalLayoutMode.KeepLastFolder,
            string? archiveBaseName = null,
            string? contentRoot = null,
            string? stagingRoot = null,
            bool sharedOutputRoot = false,
            SpecialExtractionPlan? specialExtraction = null,
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

            /*
             * ⛔ 落点最少两层（用户 2026-09-30 真机红线）的总开关：这一次递归**展开过内层包**没有。
             *
             * 它非空 ⇒ destDir 里面必须还有一层"以最后一个压缩包那一层命名"的目录，判定表里
             * 任何分支（①单文件直放 / ②不套包名层 / ③提上来 / ④单链塌缩）都不许吃掉它。
             * 空 ⇒ 只解了一层（没有内层包）⇒ 一切照旧，与加这条红线之前逐字相同。
             *
             * 判据本身就是"最后一个内层包的包基名"这一个字符串（唯一来源见 PackageLayerRules），
             * 不另开第二个布尔 —— 两处各存一份必然漂移。
             */
            bool hasInnermostPackage = !string.IsNullOrWhiteSpace(innermostPackageBaseName);

            if (destDir.Length == 0)
            {
                return FinalizePlan.Failure("目标目录只有一个路径分隔符，无法规划定稿布局");
            }

            Node root = BuildTree(stagedEntries, warnings);

            bool contentRootSpecified = !string.IsNullOrWhiteSpace(contentRoot);
            Node? contentScope = null;

            if (contentRootSpecified)
            {
                string[]? segments = SplitRelativePath(contentRoot!);
                contentScope = segments == null ? null : FindNode(root, segments);

                if (contentScope == null)
                {
                    // 内容物根找不到就**不能**退化成"整棵树都是内容物"：那会把暂存区里的过程物
                    // 当成用户的东西搬出去。宁可报"没有内容物"也不搬错。
                    warnings.Add($"暂存树里找不到内容物根 \"{contentRoot}\"，按没有内容物处理");
                }
            }

            var fileMemo = new Dictionary<Node, bool>();
            bool HasAnyFileCached(Node node) => HasAnyFile(node, fileMemo);

            bool InContentScope(Node node)
            {
                if (contentScope == null)
                {
                    return !contentRootSpecified;
                }

                return node == contentScope || IsDescendantOf(node, contentScope);
            }

            bool IsArtifact(Node node)
            {
                // 虚拟根只是容器，永远不算条目。
                if (node.Parent == null)
                {
                    return false;
                }

                /*
                 * 内容物根的**祖先**只是路径上的容器（暂存区常见 <stage>\out 这种两级结构）。
                 * 把它当其余物搬走，等于连内容物一起搬走 —— 所以这一条优先于"显式标了其余物"。
                 */
                if (contentScope != null && IsDescendantOf(contentScope, node))
                {
                    return false;
                }

                if (HasMarkedArtifact(node, contentScope))
                {
                    return true;
                }

                return !InContentScope(node);
            }

            List<Node> artifactRoots = CollectArtifactRoots(root, IsArtifact);

            // ── 判定表：从尾巴倒着看 ────────────────────────────────────────────
            Node? startScope = contentScope ?? (contentRootSpecified ? null : root);
            ContentShape shape = AnalyzeContent(startScope, IsArtifact, HasAnyFileCached);

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

            FinalizeLayoutKind kind = DecideLayout(
                shape,
                artifactRoots.Count > 0 || shape.Shells.Count > 0,
                hasInnermostPackage: hasInnermostPackage);

            /*
             * "要套的那一层"对应链上**哪个节点** —— 名字与"搬哪一棵子树"必须取自同一个节点，
             * 否则会把 `P` 改名成 `T …` 搬走（两个判据分家就是这个后果）。
             *
             * · 展开了内层包 → 链上**最外层**那一个：它是内层包自己产出的那层文件夹，**就是**最里层
             *   （更深的链原样待在它里面，见 ResolveInnermostLayerName 的说明）；
             * · 没有内层包 → 沿用判定表 ④ 的既有口径：链上最深那一个。
             */
            Node? wrapperChainNode = shape.Chain.Count == 0
                ? null
                : hasInnermostPackage
                    ? shape.Chain[0]
                    : shape.Chain[^1];

            /*
             * ⛔ **就地替换那一层已经在暂存树里了**（用户 2026-09-30 中午）：
             * 工作区发布已经把每个被解开的内层包就地换成了 `<去掉假后缀的基名>\`，
             * 于是暂存树的内容物根上**一定至少有一个目录**（那一个就是末层）。
             *
             * 这时定稿**不许再套一层**：内容物根底下那些条目（真文件 + 各个内层包的目录）
             * 原样搬进 destDir 就是用户要的形状（极端例子 `AAA\{1.mp4, DDDD\, EEEE\, BBBB\CCCCC\, …}`）。
             * 老口径拿"最后一个内层包的包基名"再套一层，会把 `DDDD\`、`BBBB\` 这些**整个塞进**
             * 一个凭空多出来的目录里 —— 那不是"省层"，而是"多一层"。
             *
             * 只有内容物根上**一个目录都没有**（调用方给的暂存树里根本没有就地替换那一层：
             * 单层路径、直读路径、以及手工喂树的单元测试）时才退回"按内层包包基名建一层"。
             */
            bool inPlaceLayerAlreadyInTree = hasInnermostPackage
                && (shape.Chain.Count > 0 || shape.Items.Any(node => node.IsDirectory));

            string safeArchiveBaseName = FileNameHelper.SanitizeFileName(archiveBaseName ?? string.Empty);
            bool hasArchiveName = !string.IsNullOrWhiteSpace(archiveBaseName);
            string safeInnermostPackageBaseName = FileNameHelper.SanitizeFileName(innermostPackageBaseName ?? string.Empty);

            /*
             * ⛔ **内容物根上只有那个内层包自己 —— 它一次都没被打开过**（真机 EEEE 2026-10-10）。
             *
             * <para>形状：跨盘 ZIP 的入口 `111.zip` 是上一层解出来的产物，可整组还缺
             * `.z01/.z02/.z03` ⇒ 引擎**一次都没调**、`111.zip` 原样躺在暂存区里（它被抢救/搬进暂存区，
             * 正是为了让结果校验看得见它、好让它落进落点那一层 —— 见
             * `ExtractionCoordinator.PublishUnresolvedVolumePieces`）。</para>
             *
             * <para>这一刻**不许按它的包基名再套一层**：套出来的是 `<基名>\<基名>.zip`
             * —— 入口包被埋深一层，还把那一位占住。真机后果（当场复现，不是推测）：下一个来回解这一组时，
             * 落点 `…\111\111\111` 已存在且非空 ⇒ 自动改名成 `…\111\111\111(1)`，
             * 5 个 mp4 埋在 `…\111\111\111(1)\111\111\111\` 里，而 `…\111\111\111\111.zip`
             * 永远留在盘上 —— 正是用户点名的那两处残留（落点层脚手架 + 多套一层）。</para>
             *
             * <para>判据只有一条**只读树上的事实**：内容物根上恰好一个**文件**、没有目录，
             * 而且那个文件的**归档基名就是内层包的包基名**（它就是那个包本身，不是它的内容物）。
             * ⛔ 与 `inPlaceLayerAlreadyInTree` **同一档口径**（都在问"那一层到底该不该凭空造"），
             * 只是形状不同：那一档是"就地替换那一层已经在树里"，这一档是"那一个包根本没被打开"。</para>
             */
            bool contentRootIsUnopenedInnerPackage = hasInnermostPackage
                && shape.HasContent
                && shape.Chain.Count == 0
                && shape.Items.Count == 1
                && !shape.Items[0].IsDirectory
                && string.Equals(
                    FileNameHelper.GetArchiveBaseName(shape.Items[0].Name),
                    safeInnermostPackageBaseName,
                    StringComparison.OrdinalIgnoreCase);

            string? wrapperName = ResolveWrapperName(
                kind,
                terminalLayout,
                wrapperChainNode,
                destDir,
                hasArchiveName,
                safeArchiveBaseName,
                warnings,
                suppressPackageFolderLayer,
                hasInnermostPackage,
                safeInnermostPackageBaseName,
                inPlaceLayerAlreadyInTree,
                contentRootIsUnopenedInnerPackage);

            /*
             * ── 特定解压例外档（规格 §3.5，用户 2026-09-24 拍板）────────────────────────
             *
             * 用户原话："假如 222\ 里面有 100 个 .rar 压缩包，而且每个压缩包里面又压缩了两次……
             * 按照之前的方式是这样的 222\1111\内容物最近的一层文件夹\内容物，
             * **我想要的是这种 222\1111\内容物**"。
             *
             * 落法就是"把刚算出来的那一层去掉"（wrapperName = null）：下面的内容物搬运会退化成
             * "把最里面那一层的活条目逐个搬进 destDir"，于是内容物直接落在成品目录里。
             * ⚠ **包名那一层一个字都不动** —— destDir 是上面落点解析算出来的（222\1111\），
             * 本例外档绝不允许把内容物搬到 222\ 去（用户 2026-09-24 第 22 条的红线：
             * "为了防止弄混乱文件夹，内容物外面必须套一层文件夹"）。
             *
             * 三条边界，任何一条不成立都**不塌**，并且必须写一条 WARN 说清为什么（绝不静默）：
             * ① 总开关关着 / 这条规则没开 → 根本不会走到这里（specialExtraction.IsActive 为 false）；
             * ② 几个包共用同一个成品目录 → 那一层就是"包名那一层"，去掉它会把几个包的内容物混在一起；
             * ③ 包内有**多个并列的文件夹** → 去掉一层就分不清哪个才是内容物。
             *
             * ⚠ 2026-09-27 落点模型 v2 之后与"不套包名层"（`suppressPackageFolderLayer`）**互不干扰**：
             * 手动档「解压到当前文件夹」下规则照旧生效（开了规则就是"内容物直接落进源包那一层"，
             * 正是这条规则的本意）；续解的那一层即便已经在落点路径里，规则也照旧把它里面多套的那一层去掉。
             * ⛔ 别在这里加"摊平了就跳过规则"那种判断 —— 那等于替用户把开着的开关关掉。
             */
            bool specialCollapse = false;

            if (specialExtraction is { IsActive: true }
                && specialExtraction.Effect == SpecialExtractionEffect.SingleContentLayer
                && kind is not (FinalizeLayoutKind.SingleFileToDestination
                    or FinalizeLayoutKind.Empty
                    or FinalizeLayoutKind.ProcessArtifactsOnly
                    or FinalizeLayoutKind.Failed))
            {
                if (sharedOutputRoot)
                {
                    warnings.Add(specialExtraction.DescribeSkipSharedRoot(destDir));
                }
                else if (!IsSingleContentChain(shape))
                {
                    warnings.Add(specialExtraction.DescribeSkipBranch(
                        CountBranches(shape),
                        DescribeBranches(shape)));
                }
                else
                {
                    wrapperName = null;
                    specialCollapse = true;
                }
            }

            /*
             * ⛔ 最后一道闸门（用户 2026-09-30 红线）：**最里层不许被任何分支吃掉**。
             *
             * 上面每一条（判定表 ①/②/③/④、不套包名层、同名不套层、特定解压例外档）都可能把
             * wrapperName 弄成 null，也就是"内容物直接摊在 destDir 下"。这一次**展开过内层包**时
             * 那样落就是真机现场那个形状（`…\26081118\P`、`…\26081118\V` 直接躺在包名目录下），
             * 用户原话："最外一层和最里面一层的文件夹都不能省"。所以在这里统一补回来 ——
             * 判据只有这一处，⛔ 不在每个分支里各补一遍（那样迟早漏一个）。
             *
             * ⚠ 例外只有两条：**就地替换那一层已经在暂存树里、而且内容物根上不是一条链**
             * （多分支 / 真文件与包目录并排）时，"不套层"本身就是正确落法 ——
             * 那一层就在要搬的那些条目里面，不属于"被吃掉"；以及**内容物根上只有那个内层包自己、
             * 它一次都没被打开过**时（真机 `…\111\111\111\111.zip` 那一格），那一层根本还不存在，
             * 凭空造一个就是把入口包埋深一层。判据与 ResolveWrapperName 同一处口径。
             *
             * 特定解压例外档被这一条盖住时**绝不静默**：如实降成"这一次没生效"并写清原因，
             * 日志与结论也不会再报"规则已生效"（SpecialExtractionApplied 保持 false）。
             *
             * ⚠ 这一条要真的生效，还要求下面"内容物落法"那一支认 <c>wrapperName</c>
             * （判定表 ① 原本不读它）—— 见那里 `wrapperName == null` 那段说明。
             */
            if (hasInnermostPackage
                && !contentRootIsUnopenedInnerPackage
                && wrapperName == null
                && kind is not (FinalizeLayoutKind.Empty
                    or FinalizeLayoutKind.ProcessArtifactsOnly
                    or FinalizeLayoutKind.Failed))
            {
                string? restored = ResolveInnermostLayerName(
                    wrapperChainNode,
                    safeInnermostPackageBaseName,
                    inPlaceLayerAlreadyInTree);

                if (restored != null)
                {
                    wrapperName = restored;
                }

                if (specialCollapse)
                {
                    specialCollapse = false;

                    warnings.Add(
                        "特定解压规则这一次没有生效：包里还有内层包（" + safeInnermostPackageBaseName
                        + "），落点最少两层 —— 最里层是「最后一个压缩包」那一层，不许被吃掉。"
                        + "内容物落进 " + (wrapperName == null
                            ? destDir
                            : SafePathHelper.Combine(destDir, wrapperName)));
                }
            }

            // ── 内容物落法 ──────────────────────────────────────────────────────
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var contentMoves = new List<PlannedMove>();
            var movedContentNodes = new List<Node>();
            string contentParent = destDir;

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
                Node file = shape.Items[0];
                contentMoves.Add(new PlannedMove(FromPath(staging, file), Unique(SafeCombine(destDir, SafeName(file.Name)), used)));
                movedContentNodes.Add(file);
                contentParent = destDir;
            }
            else if (shape.HasContent)
            {
                if (wrapperName != null)
                {
                    string wrapperTarget = Unique(SafeCombine(destDir, wrapperName), used);
                    contentParent = wrapperTarget;

                    if (wrapperChainNode != null)
                    {
                        // 整棵子树一次搬走：比逐个子项搬少一堆操作，也不会中途留半个内容物。
                        // 搬的必须是**名字来源那一个**节点（wrapperChainNode），否则会把子层改名搬走。
                        contentMoves.Add(new PlannedMove(FromPath(staging, wrapperChainNode), wrapperTarget));
                        movedContentNodes.Add(wrapperChainNode);
                    }
                    else
                    {
                        foreach (Node item in shape.Items)
                        {
                            contentMoves.Add(new PlannedMove(
                                FromPath(staging, item),
                                Unique(SafeCombine(wrapperTarget, SafeName(item.Name)), used)));

                            movedContentNodes.Add(item);
                        }
                    }
                }
                else
                {
                    contentParent = destDir;

                    foreach (Node item in shape.Items)
                    {
                        contentMoves.Add(new PlannedMove(
                            FromPath(staging, item),
                            Unique(SafeCombine(destDir, SafeName(item.Name)), used)));

                        movedContentNodes.Add(item);
                    }
                }
            }

            // ── 其余物归置：全部进 destDir\其余物\，保持相对结构 ────────────────
            var artifactNodes = new List<Node>(artifactRoots);
            var seenArtifacts = new HashSet<Node>(artifactNodes);

            foreach (Node shell in shape.Shells)
            {
                if (seenArtifacts.Add(shell))
                {
                    artifactNodes.Add(shell);
                }
            }

            artifactNodes.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
            artifactNodes = PruneNestedArtifacts(artifactNodes);

            // 已经被内容物整棵搬走的子树里的东西不能再单独规划一次（执行时那个源路径已经不在暂存区了）。
            var contentSources = contentMoves.Select(m => m.From).ToList();

            /*
             * 其余物目录名与落点（决策 D-8/D-10，唯一实现在 ProcessArtifactLayout）：
             * 名字默认「其余物」，只有内容物那一层正好也叫这个名时才让位成「其余物(1)」；
             * 落点分两档 —— 多个包共用根的两种模式（B/D）按包基名再分一层，
             * 包本来就有自己目录的模式直接 <c>destDir\其余物\</c>，不再多套一层。
             *
             * 重名判断用的是 **wrapperName**（内容物那一层真正叫什么），不是 destDir 自己的名字：
             * 会和 <c>destDir\其余物</c> 撞的正是前者。
             */
            string artifactDirectoryName = ProcessArtifactLayout.ResolveArtifactDirectoryName(wrapperName);

            if (ProcessArtifactLayout.IsArtifactDirectoryName(wrapperName))
            {
                warnings.Add($"内容物那一层与其余物目录重名，其余物目录改用 “{artifactDirectoryName}”");
            }

            bool sharedRoot = sharedOutputRoot;

            if (sharedRoot && !hasArchiveName)
            {
                warnings.Add("多个包共用同一个输出根（同一个文件夹里的包都落进这一层）时没有提供归档基名，"
                             + $"其余物无法按包名隔离，同一个目录里的多个包会共用一个 {ProcessArtifactLayout.ArtifactDirectoryName} 目录");
            }

            string artifactRootDirectory = ProcessArtifactLayout.ResolveArtifactDirectoryWithName(
                destDir,
                artifactDirectoryName,
                hasArchiveName ? archiveBaseName : null,
                sharedRoot);

            if (string.IsNullOrWhiteSpace(artifactRootDirectory))
            {
                return FinalizePlan.Failure("其余物目录算不出来（目标目录无法规范化）");
            }

            var artifactMoves = new List<PlannedMove>();
            long artifactTotalSize = 0;

            foreach (Node node in artifactNodes)
            {
                string from = FromPath(staging, node);

                if (contentSources.Any(source => IsSameOrChildPath(from, source)))
                {
                    continue;
                }

                string relative = node.RelativePath;
                string stripped = StripLeadingArtifactSegment(relative);

                if (stripped.Length > 0)
                {
                    relative = stripped;
                }

                // 落点名字必须清洗：其余物里也会有 Windows 非法名（解压出来的东西什么都可能有），
                // 不清洗的话执行阶段会直接在 File.Move 上炸掉。
                relative = SanitizeRelativePath(relative);

                artifactMoves.Add(new PlannedMove(from, Unique(SafeCombine(artifactRootDirectory, relative), used)));
                artifactTotalSize += SubtreeFileSize(node);
            }

            var moves = new List<PlannedMove>(contentMoves.Count + artifactMoves.Count);
            moves.AddRange(contentMoves);
            moves.AddRange(artifactMoves);

            int contentFileCount = 0;
            long contentTotalSize = 0;

            foreach (Node node in movedContentNodes)
            {
                contentFileCount += CountFiles(node);
                contentTotalSize += SubtreeFileSize(node);
            }

            if (moves.Count == 0)
            {
                kind = FinalizeLayoutKind.Empty;
            }

            if (kind is FinalizeLayoutKind.Empty or FinalizeLayoutKind.ProcessArtifactsOnly)
            {
                // 没有内容物就无所谓"内容物落在哪"，留空比留个 destDir 更不容易被误读。
                contentParent = string.Empty;
            }

            return new FinalizePlan
            {
                Layout = kind,
                DestinationDirectory = destDir,
                ContentParentDirectory = contentParent,
                ContentDirectoryName = kind == FinalizeLayoutKind.SingleFileToDestination ? string.Empty : wrapperName ?? string.Empty,
                Moves = moves,
                ContentMoves = contentMoves,
                ProcessArtifactMoves = artifactMoves,
                ProcessArtifactDirectory = artifactRootDirectory,
                ProcessArtifactTotalSize = artifactTotalSize,
                ContentFileCount = contentFileCount,
                ContentTotalSize = contentTotalSize,
                SpecialExtractionApplied = specialCollapse,
                Summary = BuildSummary(
                    kind, contentParent, moves.Count, artifactMoves.Count, warnings.Count,
                    specialCollapse ? specialExtraction!.RuleNames : null),
                Warnings = warnings
            };
        }

        /// <summary>
        /// 特定解压「每个包只留一层内容」的判据：包内是不是**一条单链**（规格 §3.5）。
        ///
        /// <para>
        /// 用户原话："我想要的是这种 <c>222\1111\内容物</c>"。判据必须精确到"确实只有一条路可走"，
        /// 否则会把并列的东西压到一起：
        /// </para>
        /// <list type="bullet">
        /// <item><description><b>可以塌</b>：最里面那一层**一个文件夹都没有**（只有内容文件），
        /// 或者只有**一个**文件夹（那就是唯一的内容物，连同它里面的东西一起落进成品目录）；</description></item>
        /// <item><description><b>不塌</b>：两个及以上并列的文件夹 —— 去掉外面那层之后，
        /// 用户分不清哪个才是内容物，按原判定表保守套一层（宁可多一层，绝不弄混）。</description></item>
        /// </list>
        ///
        /// <para>
        /// ⚠ "只有一个文件夹"在 <see cref="AnalyzeContent"/> 里已经被它自己的循环吃进链
        /// （每层只有一个子目录、没有文件就继续往下走），所以这里看到的最内层必然是
        /// "有文件"或"有两个以上文件夹"。判据写成"文件夹个数 ≤ 1"是为了把两种形状
        /// 一起说清楚，而不是靠上面那条不变式。
        /// </para>
        /// </summary>
        private static bool IsSingleContentChain(ContentShape shape)
        {
            return shape.HasContent && CountBranches(shape) <= 1;
        }

        /// <summary>最里面那一层有几个**并列的文件夹**（纯空壳目录已经被 AnalyzeContent 摘掉了）。</summary>
        private static int CountBranches(ContentShape shape)
        {
            return shape.Items.Count(item => item.IsDirectory);
        }

        /// <summary>WARN 里举几个并列文件夹的名字（最多 3 个，超了写"…"）。</summary>
        private static string DescribeBranches(ContentShape shape)
        {
            List<string> names = shape.Items
                .Where(item => item.IsDirectory)
                .Select(item => item.Name)
                .Take(3)
                .ToList();

            if (names.Count == 0)
            {
                return StatusText.SpecialExtractionSkippedNoContentReason;
            }

            string joined = string.Join("、", names);

            return CountBranches(shape) > names.Count ? joined + "…" : joined;
        }

        /// <summary>
        /// 判定表：按顺序判、先命中先返回（规格 §3.1）。
        /// </summary>
        /// <param name="hasInnermostPackage">
        /// 这一次递归展开过内层包（⇒ 落点最少两层：destDir 里面必须还有"最后一个压缩包"那一层）。
        /// </param>
        private static FinalizeLayoutKind DecideLayout(ContentShape shape, bool hasArtifacts, bool hasInnermostPackage)
        {
            if (!shape.HasContent)
            {
                return hasArtifacts ? FinalizeLayoutKind.ProcessArtifactsOnly : FinalizeLayoutKind.Empty;
            }

            /*
             * ⛔ **普通文件夹不许摊平**（用户 2026-09-30 中午，红检点之一）。
             *
             * 用户原话："如果是一个文件夹 1111 里面包裹真正的内容物，这个时候你就会把 1111 省略，
             * 这是非常大忌。1111 只是一个文件夹名字，我们不能去假设原打包人的逻辑。"
             *
             * 判据是**事实**：这一次递归展开了内层包（就地替换那一层已经在暂存树里）⇒ 树上的每一层
             * 都有出处，"这一层只有一个子文件夹"**不再是**去掉它的理由 ⇒ 判定表 ③（提上来）
             * 与 ④（单链塌缩）两条**一个都不参与**，整棵结构原样保留。
             *
             * 只解了一层（没有内层包）时照旧走 ③/④：那一份口径有很多既有用例钉着，一个字都不改。
             */
            if (hasInnermostPackage)
            {
                return FinalizeLayoutKind.WrapInFolder;
            }

            /*
             * ① 终端只有单个文件 → 直接放 destDir 下。
             */
            if (shape.IsSingleFile)
            {
                return FinalizeLayoutKind.SingleFileToDestination;
            }

            // ③ 路上跳过了纯空壳目录 → 提上来的是"最后那个有意义的文件夹"。
            if (shape.Shells.Count > 0)
            {
                return FinalizeLayoutKind.PromoteInnermostFolder;
            }

            // ④ 一路单链（每层只有一个文件夹、没有别的文件）→ 塌缩到最深处那个名字。
            if (shape.Chain.Count >= 2)
            {
                return FinalizeLayoutKind.CollapseSingleChain;
            }

            // ② 其余：多个文件 / 多个文件夹 / 自带一层文件夹 → 在 destDir 下套一层。
            return FinalizeLayoutKind.WrapInFolder;
        }

        /// <summary>
        /// "以最后一个压缩包那一层命名的目录"叫什么（用户 2026-09-30 红线的**唯一**取名字处）。
        ///
        /// <list type="bullet">
        /// <item><description>链上有一个节点可用（<paramref name="wrapperChainNode"/> 非空）→ 用它的名字。
        /// 展开了内层包时它是链上**最外层**那一个（= 内层包自己产出的那层文件夹）：它**成为**最里层，
        /// 更深的链原样待在它里面。
        /// ⛔ 刻意**不是**"最深的那一个"（判定表 ④ 那一支）：真机现场
        /// <c>T 小小绘 推特大合集 330P+454V-9.31G\P|V</c> 里两者正好同一个，看不出区别；
        /// 可包内如果是 <c>T …\P\a.jpg</c> 这种单文件夹链，取最深那个就会把 <c>P</c>
        /// （**归档内部子文件夹**）直接放到包名目录下 —— 那正是用户点名不许出现的形状。</description></item>
        /// <item><description>连一个文件夹都没有、而**就地替换那一层已经在暂存树里**时 → 返回 null
        /// （不套层）：内容物根底下那些条目（真文件 + 各个内层包的目录）原样搬进 <c>destDir</c>
        /// 就是用户要的形状；再套一层反而会把它们整个塞进一个凭空多出来的目录里。</description></item>
        /// <item><description>其余（暂存树里根本没有就地替换那一层：单层路径 / 直读路径 / 手工喂树的用例）
        /// → 用内层包自己的包基名建一层。
        /// ⚠ 它与 <c>destDir</c> 最后一段同名（内层包与源包同名）时**照建**：
        /// 用户宁可多一层，也不要内容物摊平。</description></item>
        /// </list>
        /// </summary>
        private static string? ResolveInnermostLayerName(
            Node? wrapperChainNode,
            string safeInnermostPackageBaseName,
            bool inPlaceLayerAlreadyInTree)
        {
            if (wrapperChainNode != null)
            {
                return SafeName(wrapperChainNode.Name);
            }

            return inPlaceLayerAlreadyInTree ? null : safeInnermostPackageBaseName;
        }

        /// <summary>
        /// 决定"套出来的那一层"叫什么；返回 null 表示**不套层**（内容直接落 <c>destDir</c>）。
        ///
        /// <para>
        /// ⚠ "不套层"只剩两种情形：**没有内层包**（只解了一层），或者**就地替换那一层已经在暂存树里、
        /// 而且内容物根上不是一条链**（多分支 / 真文件与包目录并排）。
        /// </para>
        /// </summary>
        /// <param name="suppressPackageFolderLayer">
        /// 免掉"包名那一层"（三种成因见 <see cref="Plan"/> 的参数说明）。⛔ 它**免不掉**最里层。
        /// </param>
        /// <param name="hasInnermostPackage">
        /// 这一次递归展开过内层包 ⇒ 最里层必须存在。**刻意不给默认值**：这是一条红线开关，
        /// 漏传就等于把红线关掉，⛔ 不许靠"忘了传"来绕过它。
        /// </param>
        /// <param name="safeInnermostPackageBaseName">内层包包基名（已清洗）；<paramref name="hasInnermostPackage"/> 为真时的取名依据。</param>
        /// <param name="inPlaceLayerAlreadyInTree">
        /// 暂存树的内容物根上**已经**有就地替换留下的那个 <c>&lt;包基名&gt;\</c>（见 <see cref="Plan"/>）。
        /// </param>
        /// <param name="contentRootIsUnopenedInnerPackage">
        /// **内容物根上只有那个内层包自己、而它一次都没被打开过**（见 <see cref="Plan"/>）——
        /// 这一档同样**不套层**：套出来就是 <c>&lt;基名&gt;\&lt;基名&gt;.zip</c>。
        /// </param>
        private static string? ResolveWrapperName(
            FinalizeLayoutKind kind,
            TerminalLayoutMode terminalLayout,
            Node? wrapperChainNode,
            string destDir,
            bool hasArchiveName,
            string safeArchiveBaseName,
            List<string> warnings,
            bool suppressPackageFolderLayer,
            bool hasInnermostPackage,
            string safeInnermostPackageBaseName,
            bool inPlaceLayerAlreadyInTree,
            bool contentRootIsUnopenedInnerPackage)
        {
            if (kind is FinalizeLayoutKind.SingleFileToDestination
                or FinalizeLayoutKind.Empty
                or FinalizeLayoutKind.ProcessArtifactsOnly
                or FinalizeLayoutKind.Failed)
            {
                return null;
            }

            /*
             * ⛔ 展开了内层包（用户 2026-09-30 红线）：这一支直接给出那一层，下面那两条"省一层"的老分支
             * （不套包名层 / 套层名与落点末段同名）一个都不参与 —— 用户宁可多一层，也不要内容物摊平在
             * 包名目录下。链上有节点就用它；就地替换那一层已经在树里、又不是链时**不套层**
             * （那些条目本身就是"真文件 + 各自包名的目录"，正是用户要的形状）。
             *
             * ⚠ 2026-10-10 补第二条"不套层"：内容物根上**只有那个内层包自己**（它一次都没被打开过，
             * 见 <paramref name="contentRootIsUnopenedInnerPackage"/>）—— 那一刻根本没有"内层包产出的那一层"，
             * 按包基名造一个只会把入口包埋深一层（真机 `…\111\111\111\111.zip`）。
             */
            if (hasInnermostPackage)
            {
                if (contentRootIsUnopenedInnerPackage)
                {
                    return null;
                }

                return ResolveInnermostLayerName(
                    wrapperChainNode,
                    safeInnermostPackageBaseName,
                    inPlaceLayerAlreadyInTree);
            }

            /*
             * 不套"包名那一层"（三种成因见 Plan 的参数说明）：
             * 只保留"归档自带的那个文件夹那一层"—— 内容物本来就是一条单链文件夹时那一层是归档自己的结构，
             * ⛔ 不再拿内层卷基名 / 包名当上一层套出来（真机上的
             * `…\P55-8.7、8.8 磁场中的磁介质（1）\59768866\真内容` 就是这么来的）。
             * 内容物直接摊在归档根上（没有自带文件夹）时**一层都不套**。
             */
            if (suppressPackageFolderLayer)
            {
                return wrapperChainNode != null ? SafeName(wrapperChainNode.Name) : null;
            }

            string? name;

            if (terminalLayout == TerminalLayoutMode.UseArchiveName && hasArchiveName)
            {
                name = safeArchiveBaseName;
            }
            else if (wrapperChainNode != null)
            {
                name = SafeName(wrapperChainNode.Name);
            }
            else if (hasArchiveName)
            {
                name = safeArchiveBaseName;
            }
            else
            {
                /*
                 * 内容物根部没有文件夹名可用（东西直接摊在归档根上），调用方又没给归档基名。
                 * 这时**不另套一层**：目标目录本身就是那一层（同名子文件夹模式下它就叫包名）。
                 * 硬造一个"目标目录名"当那一层只会得到 111\222\222 这种重复层，用户已经明确抱怨过。
                 */
                warnings.Add("内容物直接摊在归档根上，且调用方没有提供归档基名：不另套一层，内容将直接落到 " + destDir);
                return null;
            }

            // 目标目录本身就叫这个名字 → 不再套一层。"111\222\222\内容物" 是用户明确抱怨过的重复层，
            // 也正是规格 §3.1 里 UseArchiveName 想省的"那一层点击"。
            if (string.Equals(name, FileNameHelper.GetFileName(destDir), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return name;
        }

        /// <summary>
        /// 从内容物根往下走：跳过纯空壳目录，沿着"这一层没有文件、只有一个子目录"的链一路到底，
        /// 停在"东西真正待着的那一层"。
        ///
        /// <para>
        /// 空壳什么时候算"被跳过、要单独规划"，取决于内容物是怎么搬的：
        /// 走链（<see cref="ContentShape.Chain"/> 非空）时最里面那一层是**整棵搬走**的，
        /// 它里面的空壳跟着一起走（不能再单独规划一次，否则执行时源路径已经不在暂存区了）；
        /// 没走链时内容物是逐项搬的，同层的空壳是它们的兄弟，必须单独规划。
        /// </para>
        /// </summary>
        private static ContentShape AnalyzeContent(
            Node? startScope,
            Func<Node, bool> isArtifact,
            Func<Node, bool> hasAnyFile)
        {
            var shape = new ContentShape();

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

            Node current = startScope;

            while (true)
            {
                var liveFiles = new List<Node>();
                var liveDirectories = new List<Node>();
                var shellsHere = new List<Node>();

                foreach (Node child in current.Children)
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

        /// <summary>
        /// 去掉"父亲已经在列表里"的其余物节点：只要顶层项，否则计划里会同时出现目录和它里面的东西。
        /// 按深度从浅到深处理，浅的自然先被保留；返回时**恢复调用方给的顺序**（按暂存路径），
        /// 保证计划顺序稳定、可预期。
        /// </summary>
        private static List<Node> PruneNestedArtifacts(List<Node> nodes)
        {
            var kept = new List<Node>();

            foreach (Node node in nodes.OrderBy(Depth).ToList())
            {
                bool nested = false;

                foreach (Node parent in kept)
                {
                    if (IsDescendantOf(node, parent))
                    {
                        nested = true;
                        break;
                    }
                }

                if (!nested)
                {
                    kept.Add(node);
                }
            }

            return nodes.Where(kept.Contains).ToList();
        }

        private static int Depth(Node node)
        {
            int depth = 0;

            for (Node? current = node; current != null; current = current.Parent)
            {
                depth++;
            }

            return depth;
        }

        /// <summary>收集"最上层"的其余物节点：它自己是其余物、而它父亲不是。</summary>
        private static List<Node> CollectArtifactRoots(Node root, Func<Node, bool> isArtifact)
        {
            var result = new List<Node>();

            void Walk(Node node)
            {
                foreach (Node child in node.Children)
                {
                    if (isArtifact(child))
                    {
                        result.Add(child);
                        continue;
                    }

                    if (child.IsDirectory)
                    {
                        Walk(child);
                    }
                }
            }

            Walk(root);
            return result;
        }

        private static Node BuildTree(IReadOnlyList<StagedEntry>? entries, List<string> warnings)
        {
            var root = new Node(string.Empty, string.Empty, null, true);

            if (entries == null)
            {
                return root;
            }

            foreach (StagedEntry? entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.RelativePath))
                {
                    warnings.Add("忽略了一个没有相对路径的暂存条目");
                    continue;
                }

                string[]? segments = SplitRelativePath(entry.RelativePath);

                if (segments == null)
                {
                    // 越出暂存根的条目（.. / 绝对路径）一律不规划：宁可少搬，不可搬错地方。
                    warnings.Add("忽略了越出暂存根的条目：" + entry.RelativePath);
                    continue;
                }

                Node cursor = root;

                for (int i = 0; i < segments.Length; i++)
                {
                    bool isLast = i == segments.Length - 1;
                    Node? child = cursor.FindChild(segments[i]);

                    if (child == null)
                    {
                        child = new Node(
                            segments[i],
                            cursor.RelativePath.Length == 0 ? segments[i] : cursor.RelativePath + "\\" + segments[i],
                            cursor,
                            !isLast || entry.IsDirectory);

                        cursor.Children.Add(child);
                    }
                    else if (isLast && entry.IsDirectory && !child.IsDirectory)
                    {
                        // 同一路径既是文件又是目录：目录优先（磁盘上不可能两者并存，说明上游描述有误）。
                        child.IsDirectory = true;
                        warnings.Add("同一个路径既被描述成文件又被描述成目录，按目录处理：" + child.RelativePath);
                    }

                    if (isLast)
                    {
                        child.MarkedArtifact |= entry.IsProcessArtifact;

                        if (!entry.IsDirectory && entry.Size > child.Size)
                        {
                            child.Size = entry.Size;
                        }
                    }

                    cursor = child;
                }
            }

            return root;
        }

        private static string BuildSummary(
            FinalizeLayoutKind kind,
            string contentParent,
            int moveCount,
            int artifactMoveCount,
            int warningCount,
            string? specialRuleNames)
        {
            /*
             * 特定解压生效时**不能**再报"判定表 4：单链塌缩到最深层那个文件夹名" ——
             * 那一档说的正是"保留最深层那个名字"，而规则做的恰好相反（连那一层也不套）。
             * 结论行必须与真实落法一致，否则用户读日志会得出相反的结论。
             */
            string layout = !string.IsNullOrWhiteSpace(specialRuleNames)
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.SpecialExtractionAppliedSummaryFormat,
                    specialRuleNames,
                    string.IsNullOrWhiteSpace(contentParent) ? "成品目录" : contentParent)
                : kind switch
                {
                    FinalizeLayoutKind.SingleFileToDestination => "判定表 1：终端是单个文件，直接放进目标目录",
                    FinalizeLayoutKind.WrapInFolder => "判定表 2：在目标目录下套一层",
                    FinalizeLayoutKind.PromoteInnermostFolder => "判定表 3：提上来的是最后那个有意义的文件夹（路上有纯空壳目录）",
                    FinalizeLayoutKind.CollapseSingleChain => "判定表 4：单链塌缩到最深层那个文件夹名",
                    FinalizeLayoutKind.ProcessArtifactsOnly => "只有其余物，没有内容物",
                    FinalizeLayoutKind.Empty => "暂存区里没有可定稿的东西",
                    _ => "定稿布局规划失败"
                };

            string summary = $"{layout}；共 {moveCount} 项移动（其中其余物 {artifactMoveCount} 项）";

            // 特定解压那一档已经把落点写进结论句里了，不再重复一次"内容物落在 …"。
            if (!string.IsNullOrWhiteSpace(contentParent) && string.IsNullOrWhiteSpace(specialRuleNames))
            {
                summary += "；内容物落在 " + contentParent;
            }

            if (warningCount > 0)
            {
                summary += $"；{warningCount} 条提醒";
            }

            return summary;
        }

        /// <summary>
        /// 把 <c>其余物\xxx</c> 的开头那段摘掉：已经在其余物目录里的东西不要再套一层。
        /// 旧名 <c>过程物</c> 同样认（决策 D-8：老版本留下的目录还在用户的盘上）。
        /// </summary>
        private static string StripLeadingArtifactSegment(string relativePath)
        {
            int separator = relativePath.IndexOf('\\');

            if (separator <= 0)
            {
                return string.Empty;
            }

            string first = relativePath[..separator];

            return ProcessArtifactLayout.IsArtifactDirectoryName(first)
                ? relativePath[(separator + 1)..]
                : string.Empty;
        }

        /// <summary>逐段清洗相对路径（Windows 保留名、结尾空格/点、非法字符），保留目录层级。</summary>
        private static string SanitizeRelativePath(string relativePath)
        {
            string[] segments = relativePath.Split('\\');

            for (int i = 0; i < segments.Length; i++)
            {
                segments[i] = SafeName(segments[i]);
            }

            return string.Join("\\", segments);
        }

        /// <summary>重名不覆盖：撞了就加 <c>(1)</c>、<c>(2)</c>…（加在扩展名之前）。</summary>
        private static string Unique(string target, HashSet<string> used)
        {
            if (used.Add(target))
            {
                return target;
            }

            string directory = FileNameHelper.GetDirectoryName(target);
            string name = FileNameHelper.GetFileName(target);
            string extension = Path.GetExtension(name);
            string stem = extension.Length > 0 && extension.Length < name.Length
                ? name[..^extension.Length]
                : name;

            for (int i = 1; i <= 100000; i++)
            {
                string candidate = SafeCombine(directory, $"{stem}({i}){extension}");

                if (used.Add(candidate))
                {
                    return candidate;
                }
            }

            return target;
        }

        private static string SafeName(string? name)
        {
            return FileNameHelper.SanitizeFileName(name);
        }

        private static string SafeCombine(string directory, string name)
        {
            return SafePathHelper.Combine(directory, name);
        }

        /// <summary><c>From</c> 路径：给了暂存根就拼成可执行的绝对路径，否则就是相对路径。</summary>
        private static string FromPath(string stagingRoot, Node node)
        {
            return stagingRoot.Length == 0
                ? node.RelativePath
                : SafeCombine(stagingRoot, node.RelativePath);
        }

        /// <summary>
        /// 自己或祖先里有没有"显式标的其余物"。
        ///
        /// <para>
        /// 走到内容物根就停（<paramref name="contentScope"/> 这一层的标记仍然算数）：
        /// 内容物根**之上**的标记不外溢到内容物里 —— 否则"把暂存区整个标成其余物、再把内容物根指到它里面"
        /// 这种写法会把用户的内容物一起扫进 <c>其余物</c>。
        /// </para>
        /// </summary>
        private static bool HasMarkedArtifact(Node node, Node? contentScope)
        {
            for (Node? current = node; current != null; current = current.Parent)
            {
                if (current.MarkedArtifact)
                {
                    return true;
                }

                if (contentScope != null && current == contentScope)
                {
                    return false;
                }
            }

            return false;
        }

        private static bool IsDescendantOf(Node node, Node ancestor)
        {
            for (Node? current = node.Parent; current != null; current = current.Parent)
            {
                if (current == ancestor)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasAnyFile(Node node, Dictionary<Node, bool> memo)
        {
            if (memo.TryGetValue(node, out bool cached))
            {
                return cached;
            }

            bool result = false;

            foreach (Node child in node.Children)
            {
                if (!child.IsDirectory || HasAnyFile(child, memo))
                {
                    result = true;
                    break;
                }
            }

            memo[node] = result;
            return result;
        }

        private static int CountFiles(Node node)
        {
            if (!node.IsDirectory)
            {
                return 1;
            }

            int count = 0;

            foreach (Node child in node.Children)
            {
                count += CountFiles(child);
            }

            return count;
        }

        private static long SubtreeFileSize(Node node)
        {
            if (!node.IsDirectory)
            {
                return node.Size;
            }

            long total = 0;

            foreach (Node child in node.Children)
            {
                total += SubtreeFileSize(child);
            }

            return total;
        }

        /// <summary>
        /// 拆相对路径。返回 null 表示"不能用"：空、绝对路径、盘符、或含 <c>..</c>（会越出暂存根）。
        /// </summary>
        private static string[]? SplitRelativePath(string relativePath)
        {
            string trimmed = relativePath.Trim();

            if (trimmed.Length == 0)
            {
                return null;
            }

            if (trimmed[0] == '\\' || trimmed[0] == '/')
            {
                return null;
            }

            if (trimmed.Length >= 2 && trimmed[1] == ':')
            {
                return null;
            }

            string[] parts = trimmed.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            var segments = new List<string>(parts.Length);

            foreach (string part in parts)
            {
                if (part == ".")
                {
                    continue;
                }

                if (part == ".." || part.Trim().Length == 0)
                {
                    return null;
                }

                segments.Add(part);
            }

            return segments.Count == 0 ? null : segments.ToArray();
        }

        private static Node? FindNode(Node root, string[] segments)
        {
            Node cursor = root;

            foreach (string segment in segments)
            {
                Node? child = cursor.FindChild(segment);

                if (child == null)
                {
                    return null;
                }

                cursor = child;
            }

            return cursor;
        }

        /// <summary>candidate 与 parent 相同、或位于 parent 之下（Windows 下忽略大小写）。</summary>
        private static bool IsSameOrChildPath(string candidate, string parent)
        {
            string a = candidate.TrimEnd('\\', '/');
            string b = parent.TrimEnd('\\', '/');

            if (a.Length == 0 || b.Length == 0)
            {
                return false;
            }

            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase)
                   || a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>内容物形态：链、跳过的空壳、以及"东西真正待着的那一层"。</summary>
        private sealed class ContentShape
        {
            /// <summary>沿路经过的"只有一个子目录、没有别的文件"的层（从外到内）。</summary>
            public List<Node> Chain { get; } = new();

            /// <summary>沿路跳过的纯空壳目录（子树里一个文件都没有）。</summary>
            public List<Node> Shells { get; } = new();

            /// <summary>那一层的活条目（文件 + 目录，已排除其余物与纯空壳）。</summary>
            public List<Node> Items { get; } = new();

            public bool HasContent { get; set; }

            /// <summary>判定表 1：那一层只有一个文件、没有目录、也没有走过链。</summary>
            public bool IsSingleFile => HasContent && Chain.Count == 0 && Items.Count == 1 && !Items[0].IsDirectory;
        }

        /// <summary>暂存树的一个节点。</summary>
        private sealed class Node
        {
            public Node(string name, string relativePath, Node? parent, bool isDirectory)
            {
                Name = name;
                RelativePath = relativePath;
                Parent = parent;
                IsDirectory = isDirectory;
            }

            public string Name { get; }

            /// <summary>相对暂存根的路径（<c>\</c> 分隔），可直接拼 <c>From</c>。</summary>
            public string RelativePath { get; }

            public Node? Parent { get; }

            public bool IsDirectory { get; set; }

            /// <summary>文件字节数（目录恒为 0）。</summary>
            public long Size { get; set; }

            /// <summary>调用方显式标的其余物。</summary>
            public bool MarkedArtifact { get; set; }

            public List<Node> Children { get; } = new();

            public Node? FindChild(string name)
            {
                foreach (Node child in Children)
                {
                    if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return child;
                    }
                }

                return null;
            }
        }
    }
}

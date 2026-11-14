using ArchiveFixer.Helpers;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 判定表与"最里层那一层叫什么"的**唯一**实现处（规格 §3.1、用户 2026-09-30 红线）。
///
/// <para>
/// 输入只有只读事实（内容物形态 + 这一次展开过内层包没有 + 落点参数），输出
/// <see cref="FinalizeLayoutDecision"/>：判定表档位、那一层叫什么、以及名字与"搬哪一棵子树"
/// 共同依据的链上节点。**不执行移动、不删任何东西**。
/// </para>
/// </summary>
internal static class FinalizeLayoutDecider
{
    /// <summary>
    /// 按判定表定档，并定下"套出来的那一层"叫什么。
    /// </summary>
    /// <param name="shape">内容物形态（<see cref="FinalizeContentAnalyzer"/> 的结论）。</param>
    /// <param name="hasArtifacts">除内容物之外还有没有要归置的其余物（空壳也算）。</param>
    /// <param name="destinationDirectory">规格 §1 算出来的 <c>destDir</c>（已去尾分隔符）。</param>
    /// <param name="terminalLayout">终端落法（规格 §3.1 的可选项）。</param>
    /// <param name="archiveBaseName">终端归档（**最内层那个**）的基名；没有时那一层退回别的取名依据。</param>
    /// <param name="warnings">规划过程中的提醒（本方法只追加，不改变结论）。</param>
    /// <param name="suppressPackageFolderLayer">这一次免掉"包名那一层"（三种成因见 <see cref="ResultFinalizer.Plan"/>）。</param>
    /// <param name="innermostPackageBaseName">
    /// **最后一个被展开的内层包**的包基名；空表示只解了一层。非空 ⇒ 落点最少两层，任何分支都不许吃掉最里层。
    /// </param>
    public static FinalizeLayoutDecision Resolve(
        FinalizeContentShape shape,
        bool hasArtifacts,
        string destinationDirectory,
        TerminalLayoutMode terminalLayout,
        string? archiveBaseName,
        List<string> warnings,
        bool suppressPackageFolderLayer,
        string? innermostPackageBaseName)
    {
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

        FinalizeLayoutKind kind = DecideLayout(shape, hasArtifacts, hasInnermostPackage);

        /*
         * "要套的那一层"对应链上**哪个节点** —— 名字与"搬哪一棵子树"必须取自同一个节点，
         * 否则会把 `P` 改名成 `T …` 搬走（两个判据分家就是这个后果）。
         *
         * · 展开了内层包 → 链上**最外层**那一个：它是内层包自己产出的那层文件夹，**就是**最里层
         *   （更深的链原样待在它里面，见 ResolveInnermostLayerName 的说明）；
         * · 没有内层包 → 沿用判定表 ④ 的既有口径：链上最深那一个。
         */
        StagedTreeNode? wrapperChainNode = shape.Chain.Count == 0
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
            destinationDirectory,
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
        /*
         * 用户 2026-11-02：旧「特定解压只留一层内容」的 specialCollapse 分支整段删除。
         * 落点永远按判定表建层，不再因为某条规则把中间那层去掉。
         */

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
         * ⚠ 这一条要真的生效，还要求"内容物落法"那一支认 <c>wrapperName</c>
         * （判定表 ① 原本不读它）—— 见 <see cref="FinalizeContentMover"/> 里 `wrapperName == null` 那段说明。
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
        }

        return new FinalizeLayoutDecision
        {
            Kind = kind,
            WrapperName = wrapperName,
            WrapperChainNode = wrapperChainNode
        };
    }

    /// <summary>
    /// 判定表：按顺序判、先命中先返回（规格 §3.1）。
    /// </summary>
    /// <param name="hasInnermostPackage">
    /// 这一次递归展开过内层包（⇒ 落点最少两层：destDir 里面必须还有"最后一个压缩包"那一层）。
    /// </param>
    private static FinalizeLayoutKind DecideLayout(
        FinalizeContentShape shape,
        bool hasArtifacts,
        bool hasInnermostPackage)
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

        // ④ 旧「单链塌缩」已删除（用户 2026-11-02）：不再按最深层文件夹名合并路径。
        // 其余：多个文件 / 多个文件夹 / 自带一层文件夹 → 在 destDir 下套一层。
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
        StagedTreeNode? wrapperChainNode,
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
    /// 免掉"包名那一层"（三种成因见 <c>ResultFinalizer.Plan</c> 的参数说明）。⛔ 它**免不掉**最里层。
    /// </param>
    /// <param name="hasInnermostPackage">
    /// 这一次递归展开过内层包 ⇒ 最里层必须存在。**刻意不给默认值**：这是一条红线开关，
    /// 漏传就等于把红线关掉，⛔ 不许靠"忘了传"来绕过它。
    /// </param>
    /// <param name="safeInnermostPackageBaseName">内层包包基名（已清洗）；<paramref name="hasInnermostPackage"/> 为真时的取名依据。</param>
    /// <param name="inPlaceLayerAlreadyInTree">
    /// 暂存树的内容物根上**已经**有就地替换留下的那个 <c>&lt;包基名&gt;\</c>（见 <c>ResultFinalizer.Plan</c>）。
    /// </param>
    /// <param name="contentRootIsUnopenedInnerPackage">
    /// **内容物根上只有那个内层包自己、而它一次都没被打开过**（见 <c>ResultFinalizer.Plan</c>）——
    /// 这一档同样**不套层**：套出来就是 <c>&lt;基名&gt;\&lt;基名&gt;.zip</c>。
    /// </param>
    private static string? ResolveWrapperName(
        FinalizeLayoutKind kind,
        TerminalLayoutMode terminalLayout,
        StagedTreeNode? wrapperChainNode,
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

    private static string SafeName(string? name)
    {
        return FileNameHelper.SanitizeFileName(name);
    }
}

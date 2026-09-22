using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Helpers;

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

        /// <summary>文件字节数；目录填 0（只用来统计"过程物总大小"，契约 §3.2）。</summary>
        public long Size { get; init; }

        /// <summary>是不是目录。</summary>
        public bool IsDirectory { get; init; }

        /// <summary>
        /// 是不是"过程物"（内层归档、分卷、抠出来的中间件……）。
        ///
        /// <para>
        /// **必须由调用方显式标**：只有跑过暂存阶段的人知道哪些是中间产物。
        /// 规划器按后缀猜是不行的 —— 内容物里本来就可能有一个用户要的 <c>.zip</c>，
        /// 猜错就把用户的东西扔进了 <c>过程物</c>。
        /// </para>
        /// <para>
        /// 另一种等价办法是不标、改用 <c>Plan(..., contentRoot: "out")</c>：
        /// 内容物根之外的一切都算过程物。
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

        /// <summary>暂存区里既没有内容物、也没有过程物：没什么可定稿的。</summary>
        Empty,

        /// <summary>判定表 1：终端只有一个文件 → 直接放 <c>destDir\</c> 下。</summary>
        SingleFileToDestination,

        /// <summary>判定表 2：多个文件 / 多个文件夹 / 自带一层文件夹 → 在 <c>destDir\</c> 下套一层。</summary>
        WrapInFolder,

        /// <summary>判定表 3：被多重空目录嵌套包裹 → 把最后那个有意义的文件夹提上来。</summary>
        PromoteInnermostFolder,

        /// <summary>判定表 4：单链（每层只有一个文件夹、没有别的文件）→ 塌缩为最深层那个文件夹名。</summary>
        CollapseSingleChain,

        /// <summary>只剩过程物（没有任何内容物）：只做过程物集中，目标目录下不会有内容物。</summary>
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
        /// 全部移动计划（**内容物在前、过程物在后**）。
        ///
        /// 只有"顶层项"：绝不会同时出现某个目录和它里面的东西 —— 那样执行时会先搬父再搬子，子项必然失败。
        /// </summary>
        public IReadOnlyList<PlannedMove> Moves { get; init; } = Array.Empty<PlannedMove>();

        /// <summary>只属于内容物的移动。</summary>
        public IReadOnlyList<PlannedMove> ContentMoves { get; init; } = Array.Empty<PlannedMove>();

        /// <summary>只属于过程物的移动（都已归到 <see cref="ProcessArtifactDirectory"/> 下）。</summary>
        public IReadOnlyList<PlannedMove> ProcessArtifactMoves { get; init; } = Array.Empty<PlannedMove>();

        /// <summary>过程物集中目录（<c>destDir\过程物</c>）。契约 §3.2：过程物只允许出现在这一处。</summary>
        public string ProcessArtifactDirectory { get; init; } = string.Empty;

        /// <summary>过程物总字节数（契约 §3.2 要求详情里报出来）。</summary>
        public long ProcessArtifactTotalSize { get; init; }

        /// <summary>内容物文件数。</summary>
        public int ContentFileCount { get; init; }

        /// <summary>内容物总字节数。</summary>
        public long ContentTotalSize { get; init; }

        /// <summary>给用户看的一句话。</summary>
        public string Summary { get; init; } = string.Empty;

        /// <summary>规划过程中的提醒（忽略的非法条目、退而求其次的取名等），不改变结论。</summary>
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

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
    /// 输出：移动计划 + 过程物清单 + 一个结论枚举。**不执行移动、不删任何东西**——执行由调用方接线。
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
    /// </summary>
    public static class ResultFinalizer
    {
        /// <summary>
        /// 过程物集中目录名（契约 §3.2：固定叫"过程物"，不许散落字面量）。
        ///
        /// <para>
        /// ⚠️ **改名时同步**：另一个代理正在新建 <c>Extraction.ProcessArtifactLayout</c>，
        /// 它落地后这里应改成引用它（保持"目录名只有一处定义"）。在它出现之前，这个常量就是唯一来源。
        /// </para>
        /// </summary>
        public const string ProcessArtifactDirectoryName = "过程物";

        /// <summary>规划一次定稿布局。</summary>
        /// <param name="stagedEntries">暂存树的全部条目（相对 <paramref name="stagingRoot"/>）。</param>
        /// <param name="destinationDirectory">规格 §1 算出来的 <c>destDir</c>。</param>
        /// <param name="terminalLayout">终端落法（规格 §3.1 的可选项）。</param>
        /// <param name="archiveBaseName">
        /// 终端归档（**最内层那个**）的基名，用于"没有最外层文件夹名"时给那一层取名。
        /// 传空则在需要时退回 <c>destDir</c> 自己的末段名并给出提醒。
        /// </param>
        /// <param name="contentRoot">
        /// 内容物在暂存树里的相对根（例：<c>out</c>）。给了它，根之外的一切都算过程物；
        /// 不给则整棵树都是内容物，过程物只能靠 <see cref="StagedEntry.IsProcessArtifact"/> 显式标。
        /// </param>
        /// <param name="stagingRoot">
        /// 暂存根绝对路径。给了，计划里的 <c>From</c> 就是可直接执行的绝对路径；不给就只有相对路径。
        /// </param>
        /// <param name="placementMode">
        /// 落点模式（可选）。只影响**过程物**放在哪：模式 B（<see cref="OutputPlacementMode.SourceDirectoryFlat"/>）
        /// 的目标目录就是源目录本身，一个目录里几十上百个包会共用它，
        /// 所以过程物再套一层包基名（<c>&lt;源目录&gt;\过程物\&lt;包基名&gt;\</c>，决策 D-2），
        /// 否则多个包的分卷和中间件会在 <c>过程物\</c> 里互相撞名。
        /// 其余模式的目标目录本来就是"一个包一个目录"，直接用 <c>destDir\过程物\</c>。
        /// </param>
        public static FinalizePlan Plan(
            IReadOnlyList<StagedEntry>? stagedEntries,
            string? destinationDirectory,
            TerminalLayoutMode terminalLayout = TerminalLayoutMode.KeepLastFolder,
            string? archiveBaseName = null,
            string? contentRoot = null,
            string? stagingRoot = null,
            OutputPlacementMode? placementMode = null)
        {
            if (string.IsNullOrWhiteSpace(destinationDirectory))
            {
                return FinalizePlan.Failure("没有目标目录（destDir 为空），无法规划定稿布局");
            }

            var warnings = new List<string>();
            string destDir = destinationDirectory!.Trim().TrimEnd('\\', '/');
            string staging = (stagingRoot ?? string.Empty).Trim();

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
                    // 内容物根找不到就**不能**退化成"整棵树都是内容物"：那会把暂存区里的中间件
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
                 * 把它当过程物搬走，等于连内容物一起搬走 —— 所以这一条优先于"显式标了过程物"。
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
                 * 内容物根里一个文件都没有（解压出来全是空壳/杂物）：整棵都当过程物，一次搬走。
                 * 比"逐个空壳搬"完整得多 —— 那样会把内容物根里的非空杂物落在暂存区没人管。
                 * 虚拟根（没指定内容物根时）不能这么干，所以上面要求 contentScope.Parent != null。
                 */
                artifactRoots.Add(contentScope);
                shape.Shells.Clear();
            }

            FinalizeLayoutKind kind = DecideLayout(shape, artifactRoots.Count > 0 || shape.Shells.Count > 0);

            string safeArchiveBaseName = FileNameHelper.SanitizeFileName(archiveBaseName ?? string.Empty);
            bool hasArchiveName = !string.IsNullOrWhiteSpace(archiveBaseName);

            string? wrapperName = ResolveWrapperName(
                kind, terminalLayout, shape, destDir, hasArchiveName, safeArchiveBaseName, warnings);

            // ── 内容物落法 ──────────────────────────────────────────────────────
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var contentMoves = new List<PlannedMove>();
            var movedContentNodes = new List<Node>();
            string contentParent = destDir;

            if (kind == FinalizeLayoutKind.SingleFileToDestination)
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

                    if (shape.Chain.Count > 0)
                    {
                        // 整棵子树一次搬走：比逐个子项搬少一堆操作，也不会中途留半个内容物。
                        Node wrapperNode = shape.Chain[^1];
                        contentMoves.Add(new PlannedMove(FromPath(staging, wrapperNode), wrapperTarget));
                        movedContentNodes.Add(wrapperNode);
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

            // ── 过程物归置：全部进 destDir\过程物\，保持相对结构 ────────────────
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

            string artifactDirectoryName = ResolveArtifactDirectoryName(
                contentMoves, destDir, artifactNodes.Count > 0, warnings);
            string artifactRootDirectory = SafeCombine(destDir, artifactDirectoryName);

            /*
             * 决策 D-2：模式 B（解压到当前目录）的目标目录是**源目录本身**，几十上百个包共用它。
             * 过程物只放在 <源目录>\过程物\ 会让这些包的中间件、分卷互相撞名（还分不清是谁的），
             * 所以再套一层包基名。其余模式 destDir 本来就是"一个包一个目录"，不需要这层。
             */
            if (placementMode == OutputPlacementMode.SourceDirectoryFlat)
            {
                if (hasArchiveName)
                {
                    artifactRootDirectory = SafeCombine(artifactRootDirectory, safeArchiveBaseName);
                }
                else
                {
                    warnings.Add("模式 B（解压到当前目录）下没有提供归档基名，过程物无法按包名隔离，"
                                 + "同一个目录里的多个包会共用一个 过程物 目录");
                }
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

                // 落点名字必须清洗：过程物里也会有 Windows 非法名（解压出来的东西什么都可能有），
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
                Summary = BuildSummary(kind, contentParent, moves.Count, artifactMoves.Count, warnings.Count),
                Warnings = warnings
            };
        }

        /// <summary>
        /// 判定表：按顺序判、先命中先返回（规格 §3.1）。
        /// </summary>
        private static FinalizeLayoutKind DecideLayout(ContentShape shape, bool hasArtifacts)
        {
            if (!shape.HasContent)
            {
                return hasArtifacts ? FinalizeLayoutKind.ProcessArtifactsOnly : FinalizeLayoutKind.Empty;
            }

            // ① 终端只有单个文件 → 直接放 destDir 下。
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
        /// 决定"套出来的那一层"叫什么；返回 null 表示**不套层**（内容直接落 <c>destDir</c>）。
        /// </summary>
        private static string? ResolveWrapperName(
            FinalizeLayoutKind kind,
            TerminalLayoutMode terminalLayout,
            ContentShape shape,
            string destDir,
            bool hasArchiveName,
            string safeArchiveBaseName,
            List<string> warnings)
        {
            if (kind is FinalizeLayoutKind.SingleFileToDestination
                or FinalizeLayoutKind.Empty
                or FinalizeLayoutKind.ProcessArtifactsOnly
                or FinalizeLayoutKind.Failed)
            {
                return null;
            }

            string? name;

            if (terminalLayout == TerminalLayoutMode.UseArchiveName && hasArchiveName)
            {
                name = safeArchiveBaseName;
            }
            else if (shape.Chain.Count > 0)
            {
                name = SafeName(shape.Chain[^1].Name);
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
        /// 过程物目录名：正常情况下就是 <see cref="ProcessArtifactDirectoryName"/>；
        /// 只有真要搬过程物、且内容物那一层正好也叫这个名字时才让位
        /// （否则内容物和过程物会叠在同一个目录里）。
        /// </summary>
        private static string ResolveArtifactDirectoryName(
            IReadOnlyList<PlannedMove> contentMoves,
            string destDir,
            bool hasArtifacts,
            List<string> warnings)
        {
            if (!hasArtifacts)
            {
                return ProcessArtifactDirectoryName;
            }

            foreach (PlannedMove move in contentMoves)
            {
                if (IsSameOrChildPath(move.To, destDir)
                    && string.Equals(
                        FileNameHelper.GetFileName(move.To),
                        ProcessArtifactDirectoryName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    string alternative = ProcessArtifactDirectoryName + "(1)";
                    warnings.Add($"内容物那一层与过程物目录重名，过程物目录改用 \"{alternative}\"");
                    return alternative;
                }
            }

            return ProcessArtifactDirectoryName;
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
                // 不这么判的话它的 Children 为空，会被当成"没有内容物"，然后连人带文件被扫进过程物。
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
        /// 去掉"父亲已经在列表里"的过程物节点：只要顶层项，否则计划里会同时出现目录和它里面的东西。
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

        /// <summary>收集"最上层"的过程物节点：它自己是过程物、而它父亲不是。</summary>
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
            int warningCount)
        {
            string layout = kind switch
            {
                FinalizeLayoutKind.SingleFileToDestination => "判定表 1：终端是单个文件，直接放进目标目录",
                FinalizeLayoutKind.WrapInFolder => "判定表 2：在目标目录下套一层",
                FinalizeLayoutKind.PromoteInnermostFolder => "判定表 3：提上来的是最后那个有意义的文件夹（路上有纯空壳目录）",
                FinalizeLayoutKind.CollapseSingleChain => "判定表 4：单链塌缩到最深层那个文件夹名",
                FinalizeLayoutKind.ProcessArtifactsOnly => "只有过程物，没有内容物",
                FinalizeLayoutKind.Empty => "暂存区里没有可定稿的东西",
                _ => "定稿布局规划失败"
            };

            string summary = $"{layout}；共 {moveCount} 项移动（其中过程物 {artifactMoveCount} 项）";

            if (!string.IsNullOrWhiteSpace(contentParent))
            {
                summary += "；内容物落在 " + contentParent;
            }

            if (warningCount > 0)
            {
                summary += $"；{warningCount} 条提醒";
            }

            return summary;
        }

        /// <summary>把 <c>过程物\xxx</c> 的开头那段摘掉：已经在过程物目录里的东西不要再套一层。</summary>
        private static string StripLeadingArtifactSegment(string relativePath)
        {
            int separator = relativePath.IndexOf('\\');

            if (separator <= 0)
            {
                return string.Empty;
            }

            string first = relativePath[..separator];

            return string.Equals(first, ProcessArtifactDirectoryName, StringComparison.OrdinalIgnoreCase)
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
        /// 自己或祖先里有没有"显式标的过程物"。
        ///
        /// <para>
        /// 走到内容物根就停（<paramref name="contentScope"/> 这一层的标记仍然算数）：
        /// 内容物根**之上**的标记不外溢到内容物里 —— 否则"把暂存区整个标成过程物、再把内容物根指到它里面"
        /// 这种写法会把用户的内容物一起扫进 <c>过程物</c>。
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

            /// <summary>那一层的活条目（文件 + 目录，已排除过程物与纯空壳）。</summary>
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

            /// <summary>调用方显式标的过程物。</summary>
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

using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArchiveFixer.Extraction
{
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

    /// <summary>
    /// 发布（把产物移出工作区）的结果。
    /// </summary>
    public sealed class WorkspacePublishResult
    {
        public bool Success { get; init; }

        public string DestinationPath { get; init; } = string.Empty;

        public int MovedFileCount { get; init; }

        /// <summary>给人看的一句话；**重命名过的文件也写在这里**，让用户知道产物不是原样落进去的。</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>
        /// **判不出就没动**的那几件事，一条一件事（2026-10-03：就地替换的"半套分卷"兜底）。
        ///
        /// <para>为什么要单独一格、而不是并进 <see cref="Message"/>：这些不是"文件没搬成"的失败，
        /// 而是**刻意的"什么都不做"** —— 用户必须能一眼看到"有一组卷本该被替换掉、我们没敢动，
        /// 因为它有一片不在我们能证明的范围里"。并进 Message 会被淹没在搬运计数里
        /// （AGENTS.md §9.5：要删的动作判不出就什么都不做，而且要留证据）。</para>
        /// </summary>
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// 清理工作区（<see cref="ExtractionWorkspace.Cleanup"/>）的结果。
    ///
    /// 为什么不返回 <c>void</c>：调用方必须能在日志里区分"真的删掉了"和"因为安全校验 / 占用而没删"。
    /// 后者用户需要知道 —— 数据还在盘上、为什么还在（AGENTS.md §9.5 对不可逆操作留证据的同一要求）。
    /// 把结论丢掉等于让日志只剩一半信息，而删目录是不可逆的。
    /// </summary>
    public sealed class WorkspaceCleanupResult
    {
        /// <summary>被判定为"本任务工作区"的那个目录（即使没删也带回来，方便日志与用户指认）。</summary>
        public string TaskDirectory { get; init; } = string.Empty;

        /// <summary>
        /// 动完之后工作区是不是**真的不在了**：本次删掉、或本来就已经不存在都算 true。
        /// false = 还留在盘上（路径越界没动手 / 删除失败），调用方应当按 WARN 留证据。
        /// </summary>
        public bool Cleaned { get; init; }

        /// <summary>可以直接写进日志的一句话（含路径与原因，不含任务名）。</summary>
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// 递归解压的工作区：所有中间产物先落在这里，确认无误后再"发布"到最终目录。
    ///
    /// 为什么要这么一层（设计.md §十三/§十四、AGENTS.md §6 第 12 条）：
    /// ① 递归展开会解出**很多**中间文件，直接往最终目录写会让用户在失败时收到一堆半成品；
    /// ② 每层都在自己的目录里，失败能定位到层、能从那一层重试；
    /// ③ 只有"全部完成"才发布 —— 部分完成时产物留在工作区，用户能看、能接着处理，不会被半成品污染目标目录。
    ///
    /// 安全边界（本类会删目录，所以每一条都写死在这里）：
    /// 1. <c>RootDirectory</c> 为空直接抛 —— 那会把工作区建在进程当前目录，属于调用方的编程错误；
    /// 2. <c>taskId</c> 必须清洗成**单个目录名**，否则 <c>".."</c> / <c>"a\..\..\b"</c> 这类值
    ///    能让"工作区"落到 root 之外，后续 Cleanup 就会删掉用户的东西；
    /// 3. <c>Cleanup</c> 删之前再规范化比较一次，确认确实在 root 之下，不在就**什么都不删**；
    /// 4. <c>Publish</c> 移动时同名绝不覆盖（AutoRename），移动用的 File.Move 两参数版本
    ///    在目标存在时也会抛，是最后一道保险。
    /// </summary>
    public sealed class ExtractionWorkspace
    {
        /// <summary>
        /// 工作区目录名前缀。带序号且补零，是为了在资源管理器里天然按层序排好。
        ///
        /// <para><c>internal</c>：清工作区前的第二道容器内校验要认它（<see cref="WorkspaceCleanupGuard"/>），
        /// ⛔ 前缀只能在这里写一次。</para>
        /// </summary>
        internal const string LayerDirectoryPrefix = "layer-";

        /// <summary>每层的产物子目录名。固定叫 output，方便"某一层的产物"被直接指认。</summary>
        private const string OutputDirectoryName = "output";

        /// <summary>report.json 的文件名（崩溃后恢复时按这个约定去找）。</summary>
        private const string ReportFileName = "report.json";

        private readonly List<WorkspaceLayer> _layers = new();

        /// <summary>创建并准备一个工作区（<c>&lt;rootDirectory&gt;\&lt;清洗后的 taskId&gt;</c> 会被建出来）。</summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="rootDirectory"/> 为空。这是**调用方的编程错误**，不是用户输入错误 ——
        /// 用户输入的空目录应该在更上层就被拦住并给出可读提示，走到这里说明上层漏了校验，
        /// 此时静默继续只会把产物写到一个没人知道的地方，所以必须当场炸出来。
        /// </exception>
        public ExtractionWorkspace(string rootDirectory, string taskId)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                throw new ArgumentException("工作区根目录不能为空", nameof(rootDirectory));
            }

            RootDirectory = SafePathHelper.GetFullPathSafe(rootDirectory);

            // 只取文件名部分、并清洗非法字符与保留名，保证 TaskDirectory 一定是 root 的**直接子目录**。
            string safeTaskId = FileNameHelper.SanitizeFileName(taskId);

            TaskDirectory = Path.Combine(RootDirectory, safeTaskId);

            /*
             * 工作区那棵树一律"建完立刻标隐藏"（用户 2026-09-30 诉求③："要创的话就隐秘文件，
             * 就是单看着看不出来的，用户也察觉不到"）—— 父目录、任务目录、每一层目录都标。
             */
            if (!WorkspaceTree.EnsureHiddenDirectory(TaskDirectory))
            {
                // 建不出来说明这个位置根本不可写（磁盘满 / 权限 / 路径非法）。
                // 与其让后面每一层都失败一遍，不如在这里一次性说清。
                throw new IOException($"无法创建工作区目录：{TaskDirectory}");
            }
        }

        /// <summary>工作区根目录（由调用方指定，通常是某个临时目录）。</summary>
        public string RootDirectory { get; }

        /// <summary>本任务的工作区目录：<c>&lt;root&gt;\&lt;清洗后的 taskId&gt;</c>。</summary>
        public string TaskDirectory { get; }

        /// <summary>已经创建的层，按层号升序。</summary>
        public IReadOnlyList<WorkspaceLayer> Layers => _layers;

        /// <summary>
        /// 创建下一层目录（layer-000、layer-001…）并返回。
        /// 层号取 <see cref="Layers"/>.Count，所以"层号连续、不跳号"是结构上保证的。
        /// </summary>
        public WorkspaceLayer CreateNextLayer(string inputArchivePath)
        {
            int depth = _layers.Count;

            string layerDirectory = Path.Combine(
                TaskDirectory,
                LayerDirectoryPrefix + depth.ToString("D3"));

            string outputDirectory = Path.Combine(layerDirectory, OutputDirectoryName);

            if (!WorkspaceTree.EnsureHiddenDirectory(outputDirectory))
            {
                throw new IOException($"无法创建工作区层目录：{outputDirectory}");
            }

            var layer = new WorkspaceLayer
            {
                Depth = depth,
                DirectoryPath = layerDirectory,
                OutputPath = outputDirectory,
                InputPath = inputArchivePath ?? string.Empty
            };

            _layers.Add(layer);

            return layer;
        }

        /// <summary>
        /// 把最终产物"发布"到 <paramref name="targetDirectory"/>（**移动**，绝不覆盖）。
        ///
        /// 发布内容 = **所有叶子层**的产物，即"没有别的层是从它里面解出来的"那些层：
        /// 单链包只有最深那一层是叶子（所以中间层的 inner.7z 不会被搬出去），多分支包每个分支
        /// 各是一层叶子（所以 a.txt 与 b.txt 会一起落到目标目录）。
        /// 只发布叶子、不发布全部层，是为了不让"已经被展开掉的中间归档"混进最终产物 ——
        /// 用户要的是解到底的东西，不是半路的压缩包。
        ///
        /// ⛔ <b>发布这一步**一个外壳都不摊**</b>（2026-10-02 真机修）：这一层的产物**原样**搬进
        /// <paramref name="targetDirectory"/>。理由有两条，缺一不可：
        /// ① "哪一层留、那一层叫什么"的**唯一出口**是 <see cref="ResultFinalizer"/>（判定表 ①③④ 与
        /// "同名不套层"都在那里；套娃目录 <c>pack\pack\</c> 由它自己的同名那一档收掉）；
        /// 两处各摊一次 = 同一个事实两个判据，真机上表现为"用户的文件夹被吃过一层"，而且没有日志。
        /// ② 用户 2026-09-30 的红线原话："如果是一个文件夹 <c>1111</c> 里面包裹真正的内容物，
        /// 这个时候你就会把 <c>1111</c> 省略，这是非常大忌" —— 现场就是
        /// <c>风景01.7z.001</c>（只解一层、内容全在一个 <c>风景\</c> 文件夹里）被摊成了
        /// <c>&lt;包名&gt;\1.mp4</c>。
        ///
        /// <para>
        /// ⛔ <b>这一次递归展开了内层包时走"就地替换"那一档</b>（<paramref name="inPlaceInnerPackages"/>
        /// 传 true，用户 2026-09-30 中午的规则）：被解开的内层包在**它原来的位置**留下一个
        /// **以它命名的文件夹**（去掉假后缀的基名）放内容物，真文件原地不动 ——
        /// 形状 <c>AAA\DDDD\内容物</c>、<c>AAA\BBBB\CCCCC\内容物</c>。
        /// ⛔ 老口径"把所有叶子层产物摊到发布目标根上"（等于把它们都搬到顶层）**只有在这一档之外**
        /// 才是对的；两档现在都不摊"外壳" —— 那些文件夹是打包人自己的结构，见 <see cref="PackageLayerRules"/>。
        /// </para>
        ///
        /// <paramref name="targetDirectory"/> 必须由调用方给出**完整目标位置**
        /// （本方法不会替它再拼一层包名）：递归模式下"层产物"与"统一目标目录"的关系
        /// 由调用方决定，工作区不替用户拍板。
        /// </summary>
        /// <param name="inPlaceInnerPackages">
        /// 这一次递归展开了内层包没有（默认 false = 与加"最少两层 / 就地替换"那两条规则之前**逐字相同**）。
        /// 判据**不在本类**：由调用方从 <see cref="PackageLayerRules"/> 那**一个**出口读，本类只认这一个布尔。
        /// </param>
        /// <param name="omitMiddlePackageLayers">
        /// 中间层省不省（②页「续解时省略中间层」这一档）。**首层与末层永不受它影响**：
        /// 首层是发布目标本身，末层由 <see cref="PackageLayerRules.ShouldKeepLayerFolder"/> 无条件保留。
        /// </param>
        public WorkspacePublishResult Publish(
            string targetDirectory,
            bool inPlaceInnerPackages = false,
            bool omitMiddlePackageLayers = false)
        {
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return PublishFailure(string.Empty, "未指定发布目标目录");
            }

            if (_layers.Count == 0)
            {
                return PublishFailure(string.Empty, "工作区里还没有任何一层，没有可发布的产物");
            }

            string destination = SafePathHelper.GetFullPathSafe(targetDirectory);
            IReadOnlyList<WorkspaceLayer> leaves = ResolveLeafLayers();

            /*
             * ⛔ 发布目标**落在工作区自己那棵树里面**要立刻停手（用户 2026-09-30）。
             *
             * 工作区默认就建在目标目录里面（<目标目录>\.ArchiveFixer.work），于是"发布目标"与
             * "工作区"第一次成了**父子关系** —— 万一有人把工作区根本身（或它的子目录）当成发布目标，
             * 产物就会被搬进工作区，紧接着收尾清理会把工作区整份删掉：用户的成品**不可逆地消失**。
             * 下面那条"目标不能落在某一层产物目录内部"只挡住了反方向（往自己里面塞），挡不住这一边。
             */
            if (IsSameOrChildPath(destination, RootDirectory))
            {
                return PublishFailure(destination, "发布目标目录就是工作区（或在工作区里面），已停止发布");
            }

            /*
             * 目标落在产物目录内部要立刻停手，而且要在**建目录之前**判断（任意一层命中就整体停）：
             * 那样会一边搬一边往自己里面塞，轻则无限套娃、重则丢文件；
             * 提前判断还能保证"目标不合法"时连空目录都不会留下。
             */
            foreach (WorkspaceLayer leaf in leaves)
            {
                if (IsSameOrChildPath(destination, leaf.OutputPath))
                {
                    return PublishFailure(destination, "发布目标目录不能位于工作区产物目录内部，已停止发布");
                }
            }

            try
            {
                // 目标不存在就建；建不出来就没必要继续，否则每个文件都要失败一遍。
                if (!SafePathHelper.EnsureDirectoryExists(destination))
                {
                    return PublishFailure(destination, $"发布目标目录不存在且创建失败：{destination}");
                }

                var renamed = new List<string>();
                var errors = new List<string>();
                var warnings = new List<string>();

                /*
                 * 这一步**真的搬进来了哪些文件**：`来源全路径 -> 落点全路径`（落点是撞名改名之后的那个）。
                 *
                 * 为什么必须记：就地替换会把"已经被解开的那一份内层包"从产物里拿掉，
                 * 而它的落点是**算出来的**（父层落点 + 相对路径）—— 只问 `File.Exists` 就删，
                 * 删掉的可能是**本来就在那里的同名残留**（我们这一步真搬的那一份因为撞名被改成了
                 * `名字(1).ext`）。判据只认事实：不在这一份表里的，一个字节都不删。
                 */
                var movedFromTo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                int movedCount = 0;

                if (inPlaceInnerPackages)
                {
                    movedCount += PublishInPlace(
                        destination,
                        movedFromTo,
                        omitMiddlePackageLayers,
                        renamed,
                        errors,
                        warnings);
                }
                else
                {
                    // 按层号升序搬：同名的"谁先落位"因此是稳定的（先解出来的先落位，后来的改名）。
                    foreach (WorkspaceLayer leaf in leaves)
                    {
                        if (!SafeDirectoryExists(leaf.OutputPath))
                        {
                            errors.Add($"第 {leaf.Depth} 层的产物目录不存在（{leaf.OutputPath}）");
                            continue;
                        }

                        /*
                         * ⛔ **发布这一步一个外壳都不摊**（2026-10-02 真机修，用户原话见
                         * PackageLayerRules 第 1 条："如果是一个文件夹 1111 里面包裹真正的内容物，
                         * 这个时候你就会把 1111 省略，这是非常大忌"）。
                         *
                         * 现场：`风景01.7z.001`（只解一层、内容全在一个 `风景\` 文件夹里）——
                         * 这里以前传 `stripWrapper: true`，把 `风景\` 当"无意义外壳"摊掉，
                         * 落地成 `<包名>\1.mp4`；而"哪一层留、那一层叫什么"的**唯一出口**是
                         * <see cref="ResultFinalizer"/>（它本来就会正确处理：同名套娃 `pack\pack\`
                         * 由它自己的"同名不套层"那条收掉，普通文件夹则原样保留）。
                         * 两处各摊一次 ⇒ 用户的文件夹被吃过一层，而且**没有任何日志**。
                         */
                        movedCount += MoveContent(
                            leaf.OutputPath,
                            destination,
                            renamed,
                            errors,
                            movedFromTo);
                    }
                }

                return new WorkspacePublishResult
                {
                    Success = movedCount > 0,
                    DestinationPath = destination,
                    MovedFileCount = movedCount,
                    Message = BuildPublishMessage(
                        destination,
                        movedCount,
                        renamed,
                        errors,
                        countsMovedNotLanded: inPlaceInnerPackages),
                    Warnings = warnings
                };
            }
            catch (Exception ex)
            {
                // 发布跑在任务收尾阶段，任何意外都不能把已经解压好的产物带崩（AGENTS.md §6 第 9 条）。
                return PublishFailure(destination, $"发布过程中出现意外错误：{ex.Message}");
            }
        }

        /// <summary>
        /// **就地替换**那一档的发布（用户 2026-09-30 中午：递归 = 就地替换）。
        ///
        /// <para>算法（层号升序，父层一定排在子层前面）：</para>
        /// <list type="number">
        /// <item><description>第 0 层：产物**原样**搬进发布目标（不做任何"外壳摊平"——
        /// 那些文件夹是打包人自己的结构）；</description></item>
        /// <item><description>之后每一层：它要解的那个包在父层产物里的相对路径是 <c>X\Y\DDDD.mp4</c>，
        /// 那一层就落在 <c>&lt;父层落点&gt;\X\Y\DDDD\</c> —— **原位置 + 去掉假后缀的基名**；</description></item>
        /// <item><description>落位之后把那个包文件**从产物里拿掉**：位置让给同名目录
        /// （用户给的形状 <c>AAA\DDDD\内容物</c> 里没有 <c>DDDD.mp4</c>）。真文件原地不动。</description></item>
        /// </list>
        ///
        /// <para>
        /// 三条边界（都是"宁可少动"的那一侧）：
        /// ① <b>失败的层不参与</b> —— 它没产出任何东西，把它的包删掉等于把用户唯一的那一份弄丢；
        /// ② 父子关系不额外记一份，仍从 <see cref="WorkspaceLayer.InputPath"/> 反推
        /// （与 <see cref="ResolveLeafLayers"/> 同一个事实来源）；
        /// ③ "中间层省不省 / 末层永不可省"全部读 <see cref="PackageLayerRules"/> 那一个出口，
        /// 本方法不自己判。
        /// </para>
        /// </summary>
        private int PublishInPlace(
            string destination,
            Dictionary<string, string> movedFromTo,
            bool omitMiddlePackageLayers,
            List<string> renamed,
            List<string> errors,
            List<string> warnings)
        {
            List<WorkspaceLayer> successful = _layers
                .Where(layer => layer.Successful)
                .OrderBy(layer => layer.Depth)
                .ToList();

            // 每一层下面挂着哪几个**真的解开了**的内层包（兄弟数 / "还有没有下一层"都从这里来）。
            var childrenOf = new Dictionary<WorkspaceLayer, List<WorkspaceLayer>>();
            var parentOf = new Dictionary<WorkspaceLayer, WorkspaceLayer?>();

            /*
             * ⛔ "该不该给这一层留一个包名目录"必须**在任何一次移动之前**全部算完。
             *
             * 判据里有一条是"父层除了交给下一层的那个包，还有没有别的东西"
             * （<see cref="PackageLayerRules.ProducedOwnContent"/>），读的是**父层产物目录**；
             * 而移动是**按层号升序**做的 —— 等到处理第 N 层时，第 N-1 层的产物目录已经被搬空、
             * 甚至被顺手删掉了（MoveContent 末尾那条 TryDeleteDirectoryIfEmpty）。
             * 那时再去读只会读到"目录不存在"（保守口径按"出了内容物"算），
             * 于是每一层都被判成"不该留目录" —— 五层链会被压成一层。
             */
            var keepFolderOf = new Dictionary<WorkspaceLayer, bool>();

            foreach (WorkspaceLayer layer in successful)
            {
                WorkspaceLayer? parent = layer.Depth == 0 ? null : ResolveParentLayer(layer, successful);

                parentOf[layer] = parent;

                if (parent == null)
                {
                    continue;
                }

                if (!childrenOf.TryGetValue(parent, out List<WorkspaceLayer>? children))
                {
                    children = new List<WorkspaceLayer>();
                    childrenOf[parent] = children;
                }

                children.Add(layer);
            }

            foreach (WorkspaceLayer layer in successful)
            {
                WorkspaceLayer? parent = parentOf[layer];

                if (parent == null)
                {
                    continue;
                }

                int siblingCount = childrenOf.TryGetValue(parent, out List<WorkspaceLayer>? siblings)
                    ? siblings.Count
                    : 0;

                bool parentProducedContent = PackageLayerRules.ProducedOwnContent(parent.OutputPath, siblingCount);

                keepFolderOf[layer] = PackageLayerRules.ShouldKeepLayerFolder(
                    omitMiddlePackageLayers,
                    parentProducedContent,
                    siblingCount,
                    hasChildLayer: childrenOf.ContainsKey(layer));
            }

            var destinationOf = new Dictionary<WorkspaceLayer, string>();
            int movedCount = 0;

            foreach (WorkspaceLayer layer in successful)
            {
                if (!SafeDirectoryExists(layer.OutputPath))
                {
                    errors.Add($"第 {layer.Depth} 层的产物目录不存在（{layer.OutputPath}）");
                    continue;
                }

                WorkspaceLayer? parent = parentOf[layer];

                if (parent == null)
                {
                    if (layer.Depth != 0)
                    {
                        // 找不到父层说明这一层的输入不在任何一层的产物里（不该发生）：不猜落点。
                        errors.Add($"第 {layer.Depth} 层的输入（{layer.InputPath}）不在任何一层的产物里，已跳过");
                        continue;
                    }

                    destinationOf[layer] = destination;

                    movedCount += MoveContent(
                        layer.OutputPath,
                        destination,
                        renamed,
                        errors,
                        movedFromTo);

                    continue;
                }

                string relArchive = RelativeUnder(parent.OutputPath, layer.InputPath);
                string relDirectory = Path.GetDirectoryName(relArchive) ?? string.Empty;
                string baseDirectory = relDirectory.Length == 0
                    ? destinationOf[parent]
                    : SafePathHelper.Combine(destinationOf[parent], relDirectory);

                string layerName = keepFolderOf[layer]
                    ? PackageLayerRules.ResolveInPlaceLayerName(layer.InputPath)
                    : string.Empty;

                string layerDirectory = layerName.Length == 0
                    ? baseDirectory
                    : SafePathHelper.Combine(baseDirectory, layerName);

                /*
                 * 先把包文件从产物里拿掉，再把这一层的内容搬进同名目录 ——
                 * 反过来的话同名目录会和那个文件抢同一个路径（Windows 上必然失败）。
                 *
                 * ⚠ 路径从**父层的落点**拼（`destinationOf[parent] + relArchive`），
                 * 不是从 `baseDirectory`（那个已经把 `relDirectory` 算进去了，再拼一次 relArchive
                 * 会得到 `…\BBBB\BBBB\CCCCC.mp4` 这种不存在的路径，于是包永远删不掉）。
                 *
                 * ⛔ 2026-10-03：「算出来的那个位置上站的到底是不是我们这一步搬进来的那一份」由**事实**回答
                 * （`movedFromTo`：来源全路径 → 落点全路径），⛔ 不再只问 `File.Exists`
                 * —— 撞名时我们真搬的那一份会被改名，算出来的位置上可能是**别处留下的同名残留**。
                 */
                TryDeleteConsumedPackage(
                    SafePathHelper.Combine(destinationOf[parent], relArchive),
                    layer.InputPath,
                    movedFromTo,
                    destination,
                    errors,
                    warnings);

                destinationOf[layer] = layerDirectory;

                movedCount += MoveContent(
                    layer.OutputPath,
                    layerDirectory,
                    renamed,
                    errors,
                    movedFromTo);
            }

            return movedCount;
        }

        /// <summary>
        /// 这一层的输入归档是从哪一层的产物里解出来的：取"产物目录是它的祖先"的那一层里**层号最大**的
        /// （层号升序建层，所以直接倒着找第一个命中的就是父层）。
        /// 找不到返回 null（第 0 层、或路径关系不成立）。
        /// </summary>
        private static WorkspaceLayer? ResolveParentLayer(
            WorkspaceLayer layer,
            IReadOnlyList<WorkspaceLayer> candidates)
        {
            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                WorkspaceLayer candidate = candidates[i];

                if (candidate.Depth >= layer.Depth)
                {
                    continue;
                }

                if (IsSameOrChildPath(layer.InputPath, candidate.OutputPath))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary><paramref name="childPath"/> 在 <paramref name="parentDirectory"/> 之下的相对路径。</summary>
        private static string RelativeUnder(string? parentDirectory, string? childPath)
        {
            string fullParent = NormalizeForCompare(parentDirectory);
            string fullChild = NormalizeForCompare(childPath);

            if (fullParent.Length == 0
                || fullChild.Length == 0
                || !fullChild.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return fullChild[(fullParent.Length + 1)..];
        }

        /// <summary>
        /// 把**已经被解开**的那个包从产物里删掉（它的位置让给同名目录）；
        /// 它要是分卷组的一卷，**同一组的其余卷一起删**（见 <see cref="TryDeleteConsumedVolumeGroup"/>）。
        ///
        /// <para>
        /// 只在它确实位于发布目标之下时才删（越界一律不碰，只记一条）—— 这是不可逆操作，
        /// 兜底必须落在"什么都不做"那一档。删不掉也不影响已经落位的内容物，只记进 Message。
        /// </para>
        ///
        /// <para><b>2026-10-03 加的那道闸门</b>：删之前先按事实回答"**这一份确实是我们这一步搬进来的那一份**吗"
        /// （<paramref name="movedFromTo"/>：来源全路径 → 落点全路径）。
        /// 老写法只问 <c>File.Exists</c> —— 而落点是**算出来的**（父层落点 + 相对路径），
        /// 撞名时我们真搬的那一份会被改名成 `名字(1).ext`，算出来的那个位置上站的可能是
        /// **本来就在那里的同名残留**（上一次留下的、或别的工序放进去的）⇒ 老写法会把**不是我们的东西**删掉。
        /// 现在：算出来的那个位置上的同名残留**一个字节都不动**（只写一条 WARN 说清），
        /// 真正被消费的那一份按事实在**它自己的落点**上清掉。</para>
        ///
        /// <para>它要是分卷组的一卷，整组一起判、一起删（见
        /// <see cref="TryDeleteConsumedVolumeGroup"/>）—— 判不出就**连被消费的那一份都不删**：
        /// 只删得掉一部分，留下的就是**半套卷**，而定稿侧的分卷完整性闸门会因此把整份计划作废。</para>
        /// </summary>
        private static void TryDeleteConsumedPackage(
            string publishedArchivePath,
            string consumedSourcePath,
            Dictionary<string, string> movedFromTo,
            string artifactRoot,
            List<string> errors,
            List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(publishedArchivePath) ||
                string.IsNullOrWhiteSpace(consumedSourcePath))
            {
                return;
            }

            try
            {
                string computed = SafePathHelper.GetFullPathSafe(publishedArchivePath);

                if (Directory.Exists(computed))
                {
                    // 已经被同名目录占着：说明这一层的东西早就落在那儿了，什么都不用做。
                    return;
                }

                /*
                 * 事实：这一份到底搬到哪去了？查不到 = 这一步没把它搬进产物（移动失败 / 不在本步范围内）
                 * ⇒ 判不出哪一份是我们自己的 ⇒ **什么都不做**（连同这一组的分卷一起放弃）。
                 */
                if (!movedFromTo.TryGetValue(
                        SafePathHelper.GetFullPathSafe(consumedSourcePath),
                        out string? actual))
                {
                    warnings.Add(
                        $"{Path.GetFileName(consumedSourcePath)}（它已经被解开，但这一步没能把它搬进产物"
                        + "（撞名、占用或移动失败）—— 判不出哪一份才是我们自己搬进来的那一份，"
                        + "所以这一份连同同组的分卷一个字节都不删；删除不可恢复）");

                    return;
                }

                actual = SafePathHelper.GetFullPathSafe(actual);

                if (!string.Equals(actual, computed, StringComparison.OrdinalIgnoreCase))
                {
                    /*
                     * ⛔ 算出来的那个位置上站的是**别人的同名文件**（我们那一份因为撞名被改成了别的名字）。
                     * 一个字节都不动它 —— 老写法在这里会把用户/别的工序留下的那份删掉（不可逆）。
                     */
                    warnings.Add(
                        $"{Path.GetFileName(computed)}（这个位置上的同名文件「不是我们这一步搬进来的那一份」"
                        + $"（我们那一份落在「{Path.GetFileName(actual)}」）—— 它一个字节都不动；"
                        + "这一档只清我们自己搬进来的那一份）");
                }

                if (!TryDeleteConsumedVolumeGroup(
                        actual,
                        consumedSourcePath,
                        computed,
                        movedFromTo,
                        artifactRoot,
                        errors,
                        warnings))
                {
                    return;   // 闸门拦下了：整组一个都不删（含被消费的那一份）
                }

                if (File.Exists(actual))
                {
                    File.Delete(actual);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(publishedArchivePath)}（已解开，但原文件没能删掉：{ex.Message}）");
            }
        }

        /// <summary>
        /// 被解开的分卷组是**整组**一起被消费掉的：只删掉"这一层要解的那一卷"，同组的后续卷会缺了首卷
        /// 留在产物里 —— 而定稿那一侧的分卷完整性闸门
        /// （<c>ExtractionCoordinator.TryConfirmVolumeGroupComplete</c>：<c>.002</c> 必须能找到同目录的
        /// <c>.001</c>）会判定"整组不完整" ⇒ **整份计划作废、任务报失败**（2026-09-30 那 25 GB 踩的
        /// 正是同一道闸门）。这一组卷刚刚被我们成功解开、内容物也已经落位，所以它们**整个**是过程物，
        /// 一起清掉才不会留下半套。
        ///
        /// <para>
        /// 判据全部只读盘上的事实：组的成员从**父层产物那一层**（<paramref name="consumedSourcePath"/> 所在目录）
        /// 按"同目录 + 同族 + 同基名"收（判据唯一出口 <see cref="VolumeGroupDetector.BelongsToSameGroup"/>，
        /// ⛔ 不另写一套名字规则）；别的文件（真内容物、别的组）一个都不碰。
        /// </para>
        ///
        /// <para><b>2026-10-03 加的两道闸门（"判不出就不删"，整组一起判）</b>：老写法只看"同目录 + 同族 + 同基名"
        /// 就无条件连坐，于是同组还有一片**在别处**（别的目录）或**名字认不出**（`mid.z删除ip` 那种被改坏的）
        /// 时，删掉手上这几片就留下**半套**（§44.2 那 25 GB 正是这个形状）。现在：</para>
        /// <list type="number">
        /// <item><description><b>闸门 A</b>：整组的每一份都必须能查到**我们这一步把它搬到了哪**
        /// （<paramref name="movedFromTo"/>）—— 查不到就说明它不在我们能证明的范围里；</description></item>
        /// <item><description><b>闸门 B</b>：这一棵树里**不许还有同基名的归档件留在别处** ——
        /// 判据转调唯一出口 <see cref="RestVolumeCompletenessGate.DescribeBlockerForCandidates"/>
        /// （⛔ 不另写一套分类；它连"名字被改坏的那一片"也认得出同基名）。</description></item>
        /// </list>
        /// <para>两道闸门任意一道不过 ⇒ **这一组一个字节都不删**（**含被消费的那一份** —— 只删一半就是半套）
        /// + 一条 WARN 点名是哪一片不在我们能证明的范围里（宁可留一整套，也不许留半套）。</para>
        /// </summary>
        /// <param name="consumedPublishedPath">我们这一步**真搬进来的**那一份被消费的包的落点（事实）。</param>
        /// <param name="consumedSourcePath">它在父层产物里的位置（组就是从这一层收出来的）。</param>
        /// <param name="computedPublishedPath">
        /// 按相对路径**算出来的**落点。它只用于一件事：撞名时那份**不是我们的**同名残留要被闸门 B 排除 ——
        /// 它站在"我们这一份本该落的位置"上，是撞名的产物，不是"同组另一片落在别处"
        /// （用户 2026-10-03 批准的用例 ①：那份残留不动，我们自己的那一份照旧清掉）。
        /// </param>
        /// <returns>true = 闸门全过（调用方可以删整组）；false = 拦下了（一个都不许删）。</returns>
        private static bool TryDeleteConsumedVolumeGroup(
            string consumedPublishedPath,
            string consumedSourcePath,
            string computedPublishedPath,
            Dictionary<string, string> movedFromTo,
            string artifactRoot,
            List<string> errors,
            List<string> warnings)
        {
            string name = Path.GetFileName(consumedPublishedPath);

            if (name.Length == 0 || !FileNameHelper.IsVolumePartFileName(name))
            {
                // 不是分卷组：它自己删掉就是整组删掉（⛔ 绝不按"名字像"去连坐别的文件）。
                return true;
            }

            string sourceName = Path.GetFileName(consumedSourcePath);
            string consumedSourceFull = SafePathHelper.GetFullPathSafe(consumedSourcePath);
            string sourceDirectory = Path.GetDirectoryName(consumedSourceFull) ?? string.Empty;
            string publishedDirectory = Path.GetDirectoryName(consumedPublishedPath) ?? string.Empty;

            if (sourceDirectory.Length == 0 || publishedDirectory.Length == 0)
            {
                warnings.Add(
                    $"{name}（它已经被解开，但拿不到这一组所在的目录 —— 判不出整组是不是都在我们自己搬进来的范围里，"
                    + "所以这一组一个字节都不删；删除不可恢复）");

                return false;
            }

            /*
             * 组的成员从**事实**里收，不从目录里猜：
             * ① 我们这一步真搬过的那些（`movedFromTo` 的键）里，跟被消费的那一份**同目录 + 同族 + 同基名**的；
             * ② 父层产物那一层**还在盘上**的话，把它里面同组的也收进来 —— 那些"没跟着一起搬过来"的
             *    就会在下面那道闸门里被点名（这正是"同组有一片不在我们能证明的范围里"那一档）。
             *    ⚠ 多数情况下那一层已经被父层的搬运清空并删掉了（`MoveContent` 末尾那一步），
             *    所以这里必须容忍"目录不在了"——那不是异常，是正常收尾。
             */
            var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { consumedSourceFull };

            foreach (string source in movedFromTo.Keys)
            {
                if (IsSameGroupMemberInSameDirectory(source, consumedSourceFull, sourceName))
                {
                    sources.Add(source);
                }
            }

            if (SafeDirectoryExists(sourceDirectory))
            {
                string[] sourceFiles;

                try
                {
                    sourceFiles = Directory.GetFiles(sourceDirectory);
                }
                catch (Exception ex)
                {
                    errors.Add($"{name}（同组分卷没能一起清掉：读不动父层产物那一层 {ex.Message}）");
                    return false;
                }

                foreach (string file in sourceFiles)
                {
                    if (IsSameGroupMemberInSameDirectory(file, consumedSourceFull, sourceName))
                    {
                        sources.Add(SafePathHelper.GetFullPathSafe(file));
                    }
                }
            }

            // 闸门 A：整组的每一份都要查得到"我们把它搬到哪了"。
            var actualPaths = new List<string>();
            var computedPaths = new List<string>();

            foreach (string source in sources)
            {
                computedPaths.Add(SafePathHelper.GetFullPathSafe(
                    SafePathHelper.Combine(publishedDirectory, Path.GetFileName(source))));

                if (!movedFromTo.TryGetValue(source, out string? actual))
                {
                    warnings.Add(
                        $"{name}（它已经被解开，但同组的「{Path.GetFileName(source)}」不在我们这一步搬进来的范围里"
                        + "—— 它可能在别的目录里、也可能根本没跟着这一份一起搬过来，"
                        + "所以这一组一个字节都不删；删除不可恢复，宁可留一整套也不许留半套）");

                    return false;
                }

                actualPaths.Add(SafePathHelper.GetFullPathSafe(actual));
            }

            // 闸门 B：这一棵树里不许还有同基名的归档件留在别处（含名字被改坏的那一片）。
            string? splitBlocker = RestVolumeCompletenessGate.DescribeBlockerForCandidates(
                actualPaths,
                artifactRoot,
                candidatePrefix: "就地替换要删的",
                blockerTail: "同组还有一片留在这一棵树里（判不出它是不是我们这一步搬进来的那一份），"
                    + "所以这一组一个字节都不删（删除不可恢复；宁可留下整套，也不许留半套）",
                requireRecognizedCandidates: true,
                excludedPaths: computedPaths);

            if (splitBlocker != null)
            {
                warnings.Add(splitBlocker);
                return false;
            }

            // 闸门全过：整组一起清（被消费的那一份由调用方删，这里删其余几卷）。
            foreach (string actual in actualPaths)
            {
                if (string.Equals(actual, consumedPublishedPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;   // 被消费的那一份由调用方删（它才是"位置让给同名目录"的那一份）
                }

                try
                {
                    if (File.Exists(actual))
                    {
                        File.Delete(actual);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(actual)}（同一分卷组，已解开，但没能删掉：{ex.Message}）");
                }
            }

            return true;
        }

        /// <summary>
        /// 这一份是不是**跟被消费的那一份同目录、同族、同基名**的分卷（组的成员判据）。
        ///
        /// <para>判据只有 <see cref="FileNameHelper.IsVolumePartFileName"/> +
        /// <see cref="VolumeGroupDetector.BelongsToSameGroup"/> 两个既有出口
        /// （⛔ 不在这里另写一套名字/分族规则）；目录用规范化全路径比。</para>
        /// </summary>
        private static bool IsSameGroupMemberInSameDirectory(
            string candidatePath,
            string consumedSourceFullPath,
            string consumedSourceName)
        {
            string full = SafePathHelper.GetFullPathSafe(candidatePath);

            if (full.Length == 0 || string.Equals(full, consumedSourceFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(
                    Path.GetDirectoryName(full),
                    Path.GetDirectoryName(consumedSourceFullPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string fileName = Path.GetFileName(full);

            return FileNameHelper.IsVolumePartFileName(fileName)
                   && VolumeGroupDetector.BelongsToSameGroup(consumedSourceName, fileName);
        }

        /// <summary>
        /// 删除工作区，并如实回答"删了没有、为什么"。
        ///
        /// 只在调用方**确认后**调用（AGENTS.md §6 第 13 条：清工作区必须先经用户确认）。
        /// 对"本次任务自己造出来的中间产物"来说，那个确认点就是"任务已成功、产物已经发布出去"；
        /// 失败 / 取消 / 部分完成时**不许**调用（此时工作区里的东西是用户唯一的线索）。
        ///
        /// 三道防线：
        /// ① 删之前用规范化的完整路径确认 <see cref="TaskDirectory"/> 确实位于 <see cref="RootDirectory"/> 之下
        ///    （相等也不行 —— 那等于把整个 root 端掉），不满足就**什么都不删**；
        /// ② 目录不存在时直接返回，不去"顺手"删父目录；
        /// ③ 删非空目录用递归删除（工作区本来就是自己造的，里面全是本任务的中间产物）；
        ///    删不掉（占用 / 权限）只如实报告，**不抛异常** —— 清工作区失败不该让已经成功的任务变成失败。
        ///
        /// 删除失败时**不清空** <see cref="Layers"/>：那些层目录还在盘上，层清单必须继续如实描述现状。
        /// </summary>
        public WorkspaceCleanupResult Cleanup()
        {
            if (!IsInsideRoot(TaskDirectory))
            {
                // 注意：这里**不抛异常**。路径越界意味着工作区的身份本身可疑，
                // 此时最有价值的动作就是"一个字节都不动"，让用户自己去看。
                return new WorkspaceCleanupResult
                {
                    TaskDirectory = TaskDirectory,
                    Cleaned = false,
                    Message = $"工作区目录不在工作区根目录之下，未删除任何东西：{TaskDirectory}"
                };
            }

            if (!SafeDirectoryExists(TaskDirectory))
            {
                _layers.Clear();

                return new WorkspaceCleanupResult
                {
                    TaskDirectory = TaskDirectory,
                    Cleaned = true,
                    Message = $"工作区目录已不存在（可能已经被清理过）：{TaskDirectory}"
                };
            }

            try
            {
                Directory.Delete(TaskDirectory, recursive: true);

                _layers.Clear();

                return new WorkspaceCleanupResult
                {
                    TaskDirectory = TaskDirectory,
                    Cleaned = true,
                    Message = $"工作区已清理：{TaskDirectory}"
                };
            }
            catch (Exception ex)
            {
                // 目录里有文件被占用、权限不足等等：留着就是了，不影响任务结论。
                return new WorkspaceCleanupResult
                {
                    TaskDirectory = TaskDirectory,
                    Cleaned = false,
                    Message = $"清理工作区失败（{ex.Message}），目录保留：{TaskDirectory}"
                };
            }
        }

        /// <summary>
        /// 写 report.json（给"崩溃后可恢复"用，字段自己定，但必须是可读的 JSON）。
        ///
        /// 两条硬约束：
        /// ① **绝不写密码**（AGENTS.md §6 第 5 条）—— 报告只记结构和数量，调用方传进来的对象里
        ///    也不该有明文密码，本方法不做任何"帮你去掉密码"的补救；
        /// ② 写不进去（磁盘满 / 权限不足 / 序列化失败）**吞掉异常**：报告是给恢复用的辅助品，
        ///    写不出来不该让整个已经跑完的任务失败。
        /// </summary>
        public void WriteReport(object report)
        {
            if (report == null)
            {
                return;
            }

            try
            {
                if (!SafePathHelper.EnsureDirectoryExists(TaskDirectory))
                {
                    return;
                }

                string json = JsonSerializer.Serialize(report, ReportSerializerOptions);

                File.WriteAllText(
                    Path.Combine(TaskDirectory, ReportFileName),
                    json,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch
            {
                // 见方法注释：报告写不出来是"少一条恢复线索"，不是"任务失败"。
            }
        }

        private static readonly JsonSerializerOptions ReportSerializerOptions = new()
        {
            WriteIndented = true,

            // 中文状态字符串、路径都不转成 \uXXXX：报告是给人看和给恢复逻辑读的，可读性优先。
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        /// <summary>规范化后判断 candidate 是否等于 root、或位于 root 之下（Windows 下忽略大小写）。</summary>
        private bool IsInsideRoot(string? candidate)
        {
            string fullCandidate = NormalizeForCompare(candidate);
            string fullRoot = NormalizeForCompare(RootDirectory);

            if (fullCandidate.Length == 0 || fullRoot.Length == 0)
            {
                return false;
            }

            // 与 root 相同也算"不在其下"：那等于删掉整个根目录（别的任务的工作区可能就在旁边）。
            if (string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>candidate 与 parent 相同、或位于 parent 之下时返回 true（Windows 下忽略大小写）。</summary>
        private static bool IsSameOrChildPath(string? candidate, string? parent)
        {
            string fullCandidate = NormalizeForCompare(candidate);
            string fullParent = NormalizeForCompare(parent);

            if (fullCandidate.Length == 0 || fullParent.Length == 0)
            {
                return false;
            }

            if (string.Equals(fullCandidate, fullParent, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return fullCandidate.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 要发布的层 = **叶子层**：没有任何别的层是从它的产物里解出来的。
        ///
        /// 父子关系不额外记一份，而是从 <see cref="WorkspaceLayer.InputPath"/> 反推：
        /// 每一层解的那个归档一定位于"解出它的那一层"的产物目录里。这样"谁是叶子"就只有一个
        /// 事实来源（层自己记的输入路径），不会出现"递归那边记一棵树、发布这边又算一遍"的漂移。
        ///
        /// 返回值按层号升序（<see cref="_layers"/> 本来就是按创建顺序排的），
        /// 让同名文件的落位顺序稳定、可复现。
        /// </summary>
        private IReadOnlyList<WorkspaceLayer> ResolveLeafLayers()
        {
            var leaves = new List<WorkspaceLayer>();

            foreach (WorkspaceLayer layer in _layers)
            {
                bool hasChild = _layers.Any(other =>
                    !ReferenceEquals(other, layer) &&
                    IsSameOrChildPath(other.InputPath, layer.OutputPath));

                if (!hasChild)
                {
                    leaves.Add(layer);
                }
            }

            return leaves;
        }

        /// <summary>把内容根目录下的条目搬进目标目录，返回真正移动成功的文件数。</summary>
        /// <param name="movedFromTo">
        /// **真的搬成了的那些文件的 `来源全路径 → 落点全路径`**（落点是撞名改名之后的那个名字）——
        /// 调用方要靠它回答"这一份到底搬到哪去了"（见 <see cref="TryDeleteConsumedPackage"/>）。
        /// </param>
        private static int MoveContent(
            string sourceDirectory,
            string destinationDirectory,
            List<string> renamed,
            List<string> errors,
            Dictionary<string, string> movedFromTo)
        {
            string[] entries;

            try
            {
                entries = Directory.GetFileSystemEntries(sourceDirectory);
            }
            catch (Exception ex)
            {
                errors.Add($"无法读取产物目录（{ex.Message}）");
                return 0;
            }

            int movedCount = 0;

            foreach (string entry in entries)
            {
                if (!TryGetAttributes(entry, out FileAttributes attributes))
                {
                    errors.Add($"{Path.GetFileName(entry)}（读不到文件属性）");
                    continue;
                }

                // 不跟随符号链接 / 联接点：跟着走会把别处的文件挪走，还可能绕圈。
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    errors.Add($"{Path.GetFileName(entry)}（是符号链接/联接点，未移动）");
                    continue;
                }

                string entryName = Path.GetFileName(entry);

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    movedCount += MoveContent(
                        entry,
                        Path.Combine(destinationDirectory, entryName),
                        renamed,
                        errors,
                        movedFromTo);

                    continue;
                }

                if (TryMoveFile(entry, destinationDirectory, renamed, errors, movedFromTo))
                {
                    movedCount++;
                }
            }

            // 这一层已经搬空了就顺手删掉（只用非递归重载：目录里还有东西就必然抛，删不掉非空目录）。
            TryDeleteDirectoryIfEmpty(sourceDirectory);

            return movedCount;
        }

        private static bool TryMoveFile(
            string sourceFile,
            string destinationDirectory,
            List<string> renamed,
            List<string> errors,
            Dictionary<string, string> movedFromTo)
        {
            string fileName = Path.GetFileName(sourceFile);
            string targetPath = Path.Combine(destinationDirectory, fileName);

            try
            {
                if (!SafePathHelper.EnsureDirectoryExists(destinationDirectory))
                {
                    errors.Add($"{fileName}（无法创建目标目录）");
                    return false;
                }

                if (File.Exists(targetPath) || Directory.Exists(targetPath))
                {
                    // 目标同名：换成 名字(1).ext 并记下来，**绝不覆盖**（AGENTS.md §6 第 3 条）。
                    string renamedPath = SafePathHelper.AutoRenameFilePath(targetPath);

                    if (!string.Equals(renamedPath, targetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        renamed.Add($"{fileName} → {Path.GetFileName(renamedPath)}");
                    }

                    targetPath = renamedPath;
                }

                /*
                 * 两参数 File.Move 在目标已存在时直接抛 IOException，**不会覆盖**。
                 * 这是最后一道保险：即使 AutoRename 与 Move 之间有人往目标目录里塞了同名文件，
                 * 也只会少移动一个文件并记进 Message，不会把别人的文件冲掉。
                 */
                File.Move(sourceFile, targetPath);

                movedFromTo[SafePathHelper.GetFullPathSafe(sourceFile)] = SafePathHelper.GetFullPathSafe(targetPath);

                return true;
            }
            catch (Exception ex)
            {
                errors.Add($"{fileName}（{ex.Message}）");
                return false;
            }
        }

        /// <summary>发布结果那句话（数字口径与实际搬运一致，见下面那个开关）。</summary>
        /// <param name="countsMovedNotLanded">
        /// 就地替换那一档（<c>inPlaceInnerPackages</c>）传 true：搬动的文件里**包含**那些
        /// "落位之后位置让给同名目录、随后被拿掉"的内层包，所以那个数**大于**落点里最终的文件数。
        ///
        /// <para>2026-10-02 真机现场：日志写「已发布 146 个文件到 …\stage」，紧接着下一行就是
        /// 「结果校验 —— 校验通过：预期 145 个文件 / 实际 145 个」—— 两行互相打脸。
        /// 数字本身没错（146 = 145 个内容物 + 1 个被替换掉的内层包），错的是那句话让人读成
        /// "落点里有 146 个"。</para>
        /// </param>
        private static string BuildPublishMessage(
            string destination,
            int movedCount,
            List<string> renamed,
            List<string> errors,
            bool countsMovedNotLanded = false)
        {
            var parts = new List<string>();

            if (movedCount <= 0)
            {
                parts.Add("没有可发布的产物文件");
            }
            else if (countsMovedNotLanded)
            {
                parts.Add(
                    $"搬运 {movedCount} 个文件到 {destination}"
                    + "（含已被同名目录替换掉的内层包，所以比落点里最终的文件数多 —— 落点里几个看校验那一行）");
            }
            else
            {
                parts.Add($"已发布 {movedCount} 个文件到 {destination}");
            }

            if (renamed.Count > 0)
            {
                parts.Add($"其中 {renamed.Count} 个同名文件已改名，未覆盖原有文件（{string.Join("、", renamed)}）");
            }

            if (errors.Count > 0)
            {
                parts.Add($"{errors.Count} 个文件没能移动（{string.Join("；", errors)}）");
            }

            return string.Join("；", parts);
        }

        private static WorkspacePublishResult PublishFailure(string destination, string message)
        {
            return new WorkspacePublishResult
            {
                Success = false,
                DestinationPath = destination,
                MovedFileCount = 0,
                Message = message
            };
        }

        /// <summary>尽力删除已经空掉的目录：删不掉（非空 / 被占用 / 没权限）一律当没发生。</summary>
        private static void TryDeleteDirectoryIfEmpty(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                if (Directory.GetFileSystemEntries(directory).Length > 0)
                {
                    return;
                }

                Directory.Delete(directory, recursive: false);
            }
            catch
            {
                // 空目录删不掉不影响发布结果，留给用户自己收拾。
            }
        }

        private static string NormalizeForCompare(string? path)
        {
            return SafePathHelper.GetFullPathSafe(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool SafeDirectoryExists(string? path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch
            {
                attributes = default;
                return false;
            }
        }
    }
}

using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;




namespace ArchiveFixer.ViewModels
{
    /// <summary>落点/层计算编排（第 4 块，2026-10-23 从主分片搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// 定稿布局规划的统一入口：算"终端归档基名"这件事只在这里做一次，
        /// 预检与真正的定稿走**同一份输入**（否则预检说有冲突、定稿却按另一套计划落位）。
        ///
        /// <para>
        /// ⚠ 它刻意**不是 static**：本批是不是「解压到当前文件夹」只读
        /// <c>_extractIntoSourceFolderThisRun</c> 这一个字段 —— 让两处调用各传一个参数，
        /// 迟早会漂移成"预检按平铺算、定稿按套层算"（预检说有冲突、实际没有，或者反过来）。
        /// </para>
        /// </summary>
        internal FinalLayoutPlan PlanFinalLayoutForTask(
            ArchiveTask task,
            string stageDirectory,
            string destinationDirectory,
            bool sharedOutputRoot,
            TerminalLayoutMode terminalLayout,
            SpecialExtractionPlan? specialExtraction = null,
            RecursionResult? recursion = null)
        {
            string archiveBaseName = OutputPlacement.ResolveArchiveBaseName(task.CurrentPath);

            /*
             * ⛔ 落点最少两层（用户 2026-09-30 真机红线）：**最里层 = 最后一个压缩包那一层**。
             *
             * 它的名字来源只有一处（`PackageLayerRules.ResolveBaseName`，读的就是这一次递归的结果）：
             * 递归展开了内层包时非空 ⇒ 定稿那一侧任何分支都不许把最里层吃掉
             * （真机现场 `26081118.7z` → 内层包解出 `T 小小绘 推特大合集 330P+454V-9.31G\P|V`，
             * 定稿却只留 `P`、`V` 直接躺在 `…\26081118\` 下）。
             * 只解了一层时是空串 ⇒ 一路照旧，与加这条红线之前逐字相同。
             *
             * ⚠ 它与 `suppressPackageFolderLayer` 是**两件事**：后者只免掉"包名那一层"，
             * 最里层由这里另外钉住 —— 预检与真正的定稿走的是**同一个**方法，不会各判一套。
             */
            string innermostPackageBaseName = PackageLayerRules.ResolveBaseName(recursion?.Layers);

            /*
             * "要不要套包名那一层"只有这一个出口（`suppressPackageFolderLayer`），三种成因在这里合并：
             *   ① 手动档「解压到当前文件夹」：落点就是源包所在那一层，按包内原样解开；
             *   ② 续解的这一层已经在落点路径里（忠实档 / 分支退化档）：再套就是 `666\666`；
             *   ③ 这一层本身就是分卷组的一卷（过程物名不成层）。
             * 合成一个布尔往下传 —— 定稿那一侧只认一个判据，不会再出现"落点摊平了、定稿又套一层"。
             *
             * ⚠ ②这条判据**不能简化成"是不是续解任务"**：简洁档下续解的**末层**恰恰需要那一层
             * （`111\222\666\内容物` 里的 `666`），而"自己那一层在不在落点路径里"是**路径本身**
             * 说得清的事实 —— 不需要预知"这是不是最后一层"。
             */
            string parentDirectory = task.ParentOutputDirectory;
            bool continuation = !string.IsNullOrWhiteSpace(parentDirectory);

            bool layerAlreadyInPath = continuation
                && IsContinuationLayerAlreadyInPath(parentDirectory, archiveBaseName);

            /*
             * 过程物名不成层（用户 2026-09-27 红线）：续解的这一层如果**本身就是分卷组的一卷**
             * （`59768866.001`），它的包基名是过程物的名字、不是用户认得的包名 ——
             * 拿它当一层就是真机上那个 `…\包名\59768866\真内容`。
             */
            bool processArtifactName = continuation
                && FileNameHelper.IsVolumePartFileName(FileNameHelper.GetFileName(task.CurrentPath));

            /*
             * ⚠ 2026-10-09 第 ④ 种成因（**定稿侧与落点侧判据分家**，缺陷取证定位到行）：
             * 这一层要建的位置**被内层包自己那个文件占着**（Windows 同一条路径上不许既有文件又有目录）。
             *
             * 现场：包名被改坏、剥不出后缀（`user.jp删除g`）⇒ 包基名就等于文件名本身 ⇒
             * `…\out\polyglot\user.jp删除g` 既是那个文件、又要当这一层的目录。
             * **落点侧早就挡住了**（`OneClickCoordinator.ResolveContinuationOutputDirectory` 里那一句
             * `IsOwnLayerOccupiedByPackageFile` ⇒ 这一层不另建、内容落进父层），
             * **定稿侧一次都没调** ⇒ 两侧判据分家 ⇒ 定稿把每条内容物都往
             * `…\user.jp删除g\inner.7z.001` 搬 ⇒ `EnsureDirectoryExists` 撞同名文件必然失败
             * ⇒ 整单报「定稿搬运失败 ……（无法创建目标目录）」（真机形状见
             * `InnerLayerContinuationTests.续解_下一层名字和文件头都是假的` 那次排障原文）。
             *
             * ⛔ 判据只转调**既有那一个出口**（`OneClickCoordinator.IsOwnLayerOccupiedByPackageFile`），
             * ⛔ 不在这里另写一份"名字被占"的判断。物理上唯一可行的落法就是这一层不另建。
             */
            bool ownLayerOccupied = OneClickCoordinator.IsOwnLayerOccupiedByPackageFile(
                destinationDirectory,
                archiveBaseName,
                task.CurrentPath);

            return PlanFinalLayout(
                stageDirectory,
                destinationDirectory,
                sharedOutputRoot,
                archiveBaseName,
                terminalLayout,
                specialExtraction,
                suppressPackageFolderLayer: _extractIntoSourceFolderThisRun
                    || layerAlreadyInPath
                    || processArtifactName
                    || ownLayerOccupied,
                innermostPackageBaseName: innermostPackageBaseName,
                /*
                 * ⛔ 引擎自己写出来的东西**不是过程物**（用户 2026-09-27 真机 222 那一单）。
                 *
                 * 现场：外层跨盘 zip 解出来的两个条目叫 `222.zip` 与 `222.zi删除p`（名字里嵌了中文），
                 * 它们**本身就是一份完整的两卷 split zip**。老判据只看后缀像不像归档 ⇒ `222.zip`
                 * 被当成"待续解的过程物"，而旁边那一卷名字不标准 ⇒ 整层定稿作废，
                 * 用户的 678 MB 产物一个字节都没落地。判据见
                 * <see cref="IsEngineOutputFile"/>：这一单引擎这一趟写出来的，就不是过程物。
                 */
                engineOutput: recursion,
                /*
                 * ⛔ 「内容物保留关键词」的那一份判据（用户 2026-10-04 的新功能「内容物压缩文件不解压」）：
                 * 命中关键词的内容文件**不算过程物** ⇒ 它跟着内容物一起定稿落盘、一个字节都不动
                 * （不进其余物、也不参与后面的删除）。判据唯一出口仍是 <see cref="ContentKeepRules"/>。
                 */
                contentKeepRules: ContentKeepRules.FromSettings(Settings),
                /*
                 * ⛔ **正在收的那几组不许被"分卷组不完整"那道闸门拦死**（用户 2026-10-06 真机：
                 * `111(2)_.zip` 的定稿被判「这一层里有分卷组不完整：111」⇒ 整层不定稿 ⇒
                 * 其余物与源包全留着不动）。判据只读既有账：这一批已经登记过"谁吐出了哪一组的片"
                 * （`_groupPieceProducers`）⇒ 那些组基名的片一律放行，交给收卷那一档。
                 */
                assembledVolumeBaseNames: SnapshotGroupPieceProducerBaseNames());
        }

        /// <summary>
        /// 定稿布局规划：把暂存产物树描述成 <see cref="StagedEntry"/>，交给
        /// <see cref="ResultFinalizer.Plan"/>（契约 §3.1 判定表的**唯一实现处**），
        /// 再把它的话翻译成本类执行阶段用的形状。
        ///
        /// 分工要说清楚：
        /// · 本方法负责"哪些是其余物" —— 这一步只有跑过暂存阶段的人知道。
        ///   判据用后缀（分卷段 + 归档本体）：解压出来的归档就是**待续解的内层包**，
        ///   按流水线语义它属于其余物（一键处理下一轮会去解它，成功的话它的内容物自会落进内容物一层）；
        ///   判定失败/到轮数上限时它留在 <c>其余物</c> 里可回收，比混在内容物里强。
        ///   （ResultFinalizer 的注释建议"由调用方显式标"，这里就是那个调用方。）
        /// · <see cref="ResultFinalizer"/> 负责"摆成什么样" —— 终端单文件直接放、多文件套一层、
        ///   多重空目录提上来、单链塌缩，以及 D-2 的 <c>&lt;源目录&gt;\其余物\&lt;包基名&gt;\</c>。
        ///
        /// 纯函数：只读目录、只返回计划，一个字节都不动，所以能脱离管线被测。
        /// </summary>
        /// <param name="stageDirectory">暂存目录（入仓阶段的产物树）。</param>
        /// <param name="destinationDirectory">最终目录（落点由 <see cref="PathService.BuildOutputPath"/> 算）。</param>
        /// <param name="sharedOutputRoot">
        /// 这个落点目录是不是同一次导入里多个包共用的（落点解析给出的唯一事实，
        /// 见 <see cref="OutputPlacementResult.SharesDestinationWithOtherPackages"/>）；
        /// 只影响其余物集中到哪（决策 D-10 依赖它）。
        /// </param>
        /// <param name="archiveBaseName">
        /// 终端归档基名（"没有最外层文件夹名"时给那一层取名用）。传空则退回 destDir 自己的末段名。
        /// </param>
        /// <param name="terminalLayout">
        /// 终端落法。⚠ 用户 2026-09-27 把这档选择**从界面上删掉了**（落点固定，不再让用户选），
        /// 所以生产路径现在一律传 <see cref="TerminalLayoutMode.KeepLastFolder"/>：
        /// 保留压缩包自带的那一层内层文件夹，外面再套一层以包名命名的文件夹。
        /// 参数本身留着 —— 它是 <see cref="ResultFinalizer.Plan"/> 的既有契约（其余物布局等路径也在用），
        /// 删掉只会白白动一大片与本次需求无关的代码。
        /// </param>
        /// <param name="specialExtraction">
        /// 本批生效的特定解压（规格 §3.5；默认 <see cref="SpecialExtractionPlan.Off"/> = 与以前逐字相同）。
        /// 规则生效时判定表里"要套的那一层"不再套（<c>222\1111\内容物</c>）；包内有并列的多个文件夹、
        /// 或几个包共用同一个成品目录时不塌，按原判定表套一层并写一条 WARN（见 <see cref="ResultFinalizer.Plan"/>）。
        /// </param>
        /// <param name="innermostPackageBaseName">
        /// **最后一个被展开的内层包**的包基名（没有内层包时传空）。
        /// 唯一来源 = <see cref="PackageLayerRules.ResolveBaseName"/>（读递归结果，不自己数层数）。
        /// 它非空 ⇒ 落点最少两层（用户 2026-09-30 红线）：destDir 里面必须还有"最后一个压缩包"那一层。
        /// </param>
        /// <param name="engineOutput">
        /// 这一单的递归结果。**只用来回答一件事**：暂存目录里某个文件是不是"引擎这一趟自己写出来的"
        /// —— 是 ⇒ 它是内容物，⛔ 不许当成待续解的过程物（用户 2026-09-27 真机：外层跨盘 zip 解出来的
        /// <c>222.zip</c> + <c>222.zi删除p</c> 本身就是一份完整的两卷 split zip，老判据把
        /// <c>222.zip</c> 当过程物、把另一卷当"名字不标准的残组"⇒ 整层定稿作废，678 MB 产物没落地）。
        /// 传 null（预检那一档）⇒ 退回老判据，一个字不变。
        /// </param>
        internal static FinalLayoutPlan PlanFinalLayout(
            string stageDirectory,
            string destinationDirectory,
            bool sharedOutputRoot,
            string? archiveBaseName,
            TerminalLayoutMode terminalLayout = TerminalLayoutMode.KeepLastFolder,
            SpecialExtractionPlan? specialExtraction = null,
            bool suppressPackageFolderLayer = false,
            string? innermostPackageBaseName = null,
            RecursionResult? engineOutput = null,
            ContentKeepRules? contentKeepRules = null,
            IReadOnlyCollection<string>? assembledVolumeBaseNames = null)
        {
            if (string.IsNullOrWhiteSpace(stageDirectory) ||
                string.IsNullOrWhiteSpace(destinationDirectory) ||
                !Directory.Exists(stageDirectory))
            {
                return new FinalLayoutPlan();
            }

            string stageRoot = SafePathHelper.GetFullPathSafe(stageDirectory);

            /*
             * 「内容物保留关键词」判据（用户 2026-10-04 的新功能）：整个循环只算一次；
             * 没传（null）/ 空列表 ⇒ 一个都不拦（行为与加这条功能之前逐字相同）。
             */
            ContentKeepRules keepRules = contentKeepRules ?? ContentKeepRules.Empty;

            /*
             * ===== 整组改名也要覆盖"引擎刚解出来的那一层"（用户 2026-10-01 真机）=====
             *
             * 现场：外层跨盘 zip 解出来的两个条目本身**又是一份完整的两卷 split zip**，可它们的名字
             * 是归档内部记的原名（`222.zi删除p` + `222.z0sc1`，网盘把中文/缩写塞进了后缀）。
             * 整组改名的两个调用点（批首 / 手动修复）**都只对"已经在任务表里"的文件动手**，
             * 这一对是递归产物、从没进过任务表 ⇒ 名字原样落地 ⇒ 7-Zip 按标准卷名找不到兄弟 ⇒ 打不开。
             *
             * 判据一个字不放宽（唯一出口仍是 VolumeGroupResolver + IsCanonicalVolumeName）：
             *   · 这一组必须**结论完整**、而且**每一卷都能唯一推出规范名**（判不出 / 缺卷 / 推不出 ⇒ 一个字都不改）；
             *   · 只改名字、内容一个字节不动；目标名被占 ⇒ 这一卷跳过（绝不覆盖）；
             *   · 全部改完再复查一遍（中途任何一卷不到位 ⇒ 倒序改回原名，⛔ 绝不留下半改状态）；
             *   · ⛔ **刚改过名的那一组不许进"可删的其余物"**（见下面 `renamedVolumeMembers`）——
             *     它是**内容物**（用户要拿去打开的那一份），不是待清理的过程物。
             */
            var volumeRenameChanges = new List<(string Level, string Message)>();
            var renamedVolumeMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            VolumeNameOrchestration.NormalizeEngineOutputVolumeNames(stageRoot, volumeRenameChanges, renamedVolumeMembers);

            var staged = new List<StagedEntry>();
            var warnings = new List<string>();
            long totalStageBytes = 0;
            int zeroByteArtifacts = 0;

            /* 分卷组不完整的现场（见下面那道闸门）：非空 ⇒ 整份计划作废，一个字节都不动。 */
            var incompleteVolumeGroups = new List<string>();

            /*
             * ===== 分卷组装判定（唯一出口 VolumeGroupResolver）=====
             *
             * 暂存目录里出现的每一组分卷**先判一次**，再决定谁能进"可删的其余物"。
             * 判据不再看"扩展名像不像分卷"，而是走同一份证据模型（基名 / 卷号 / 体积 / 位置 / 物理同一性；
             * 试开那一档在定稿这台同步机器上不做 —— 它在解前预检那边做，结果一样收进同一份结论里）。
             *
             * ⛔ 凡是被判定"属于某个分卷组"的文件，**永远不许进可删残留名单**：
             * 只有"整组完整 + 每一卷名字都标准"这一种情形才放行；其余一律让整份计划作废。
             */
            StageVolumeScan stageVolumeScan = ResolveStageVolumeGroups(stageRoot);

            foreach (string file in Directory.EnumerateFiles(stageRoot, "*", SearchOption.AllDirectories))
            {
                long size = 0;

                try
                {
                    size = new FileInfo(file).Length;
                }
                catch
                {
                    // 量不出大小只影响"其余物总共多大"这个数字，不影响布局。
                }

                totalStageBytes += size;

                /*
                 * ===== ⛔ 引擎这一趟自己写出来的文件**不是过程物**（用户 2026-09-27 真机：222 那一单）=====
                 *
                 * 现场：外层跨盘 zip（`222.zscip.zip` + `222.zscip.z01`）解出来的两个条目叫
                 * `222.zip` 与 `222.zi删除p`（网盘把中文塞进了内层后缀）。这两个**本身就是一份完整的
                 * 两卷 split zip**（7-Zip 自己认：「Multivolume = + / Volumes = 2」）。
                 *
                 * 老判据只看"后缀像不像归档" ⇒ `222.zip` 被算成"待续解的过程物"，随后下面那道
                 * 分卷组闸门看见同目录还有名字不标准的一卷 ⇒ 判"不完整" ⇒ **整份计划作废**：
                 * 内容物一个字节没搬、源包没进其余物、链尾的其余物一个都删不掉（日志还写着
                 * "解压成功 ｜ 校验通过"，用户却什么也没拿到）。
                 *
                 * 判据只有一条：**这个文件在不在这一趟引擎的产物清单里**（唯一事实来源 = 递归结果
                 * 指向的那一层产物目录）。在 ⇒ 它是内容物：既不是过程物，**也不进分卷组那两道闸门**
                 * —— 那两道问的是"这一堆是不是待续解的分卷"，引擎自己刚写出来的东西根本不是。
                 */
                /*
                 * ⚠ 2026-10-09 真机 `EEEE`（14:11 那一趟）**实测**：原来的三条判据
                 * （本层产物清单① / 每一层的产物目录② / 每一层解压前的逐条清单③）在
                 * **"链中途停在缺卷"**这一档**全都不成立**（排障行原文：
                 * `在产物清单里=False ｜ 在某层清单里=False ｜ 最终算过程物=True`）：
                 * ① `FinalOutputPath` 指向停住的那一层、文件却在 stage；② 层目录里那份已被发布**搬走**；
                 * ③ 那一层**列不出目录**（缺分卷 ⇒ 清单失败，真机行 217/219「没能列出归档内容」）
                 * ⇒ `ManifestEntries` **空表**。
                 * ⇒ 于是 `111.rar` 自己解出来的内容物 `111.zip` 又一次进了其余物 ⇒ 用户看到的"两个 `111.zip`"。
                 *
                 * ⇒ 判据不再"逐层找证据"，改问一个**当场成立**的事实：**这一趟到底跑过递归没有**。
                 * 跑过（`engineOutput != null`）⇒ 这个暂存区里每一个字节都出自**我们这条链**
                 * （本层引擎写出的 + 上一层产物发布进来的），而链内中间物早由"逐层回收"删掉了
                 * ⇒ **一律是内容物、要落地**（用户原话：「他就是最初 111.rar 经过两轮解压产出的内容物而已，
                 * 为什么会进其余物里面」）。
                 * ⛔ **没跑过递归**（`engineOutput == null`：预检那一档，以及 `PlanFinalLayout` 的直调用例）
                 * ⇒ 来路**判不出** ⇒ 退回原来的保守判据 + 下面那两道分卷组完整性闸门
                 * （`VolumeGroupDeletionSafetyTests` 那一族钉的就是这条兜底 —— 实测 136 条全绿）。
                 */
                bool isProcessArtifact = engineOutput == null
                    && (IsProcessArtifactFile(file) || CardSaysItIsOneOfTheGroup(file))
                    /*
                     * ⛔ 命中「内容物保留关键词」的**不是过程物**（用户 2026-10-04 的新功能
                     * 「内容物压缩文件不解压」）：它是用户点名"碰都不碰"的内容物 ⇒
                     * 跟着内容物一起定稿落盘，**不进其余物**（进了就会被链尾那一档按设置删掉）。
                     *
                     * 现场形状：外层包里就装着一个 `小明.zip`（用户要留的那一份）—— 老判据只看
                     * "像不像归档"，把它当"待续解的过程物"收进其余物 ⇒ 选了「彻底删除」就没了。
                     * 判据唯一出口 <see cref="ContentKeepRules"/>（包含即命中 / 大小写不敏感 / 不通配正则）。
                     */
                    && !keepRules.ShouldKeep(file);

                /*
                 * ===== ⛔ 分卷组的完整性闸门（用户 2026-09-30 真机：25 GB 被当"其余物"永久删除）=====
                 *
                 * 现场：外层 RAR 解出来的是一组 **PKZIP 跨盘**（`一只顶美.z01…z05` + 末卷 `一只顶美.zip`），
                 * 而末卷的名字被改坏成 `一只顶美.z删除ip` ⇒ 认不出是归档。
                 * 老判据只看**扩展名像不像分卷**，于是剩下 5 卷"每一卷单看都像待续解的过程物"，
                 * 全被收进可删的其余物；再叠加「空间不足」模式的永久删除 ⇒ **25 GB 当场没了**，
                 * 日志还写着"解压成功 ｜ 校验通过"。
                 *
                 * 现在：**分卷要进"可删的其余物"，必须先由判定器证明整组完整且名字标准**
                 * （判据见 <see cref="VolumeGroupResolver"/>，只认盘上的事实）。证明不了 ⇒
                 * 这一份既不进其余物、也不当内容物，**整份计划作废**（下面的 Failed 分支），
                 * 于是源包与其余物的删除一个都不会发生（它们都挂在"定稿 + 校验通过"之后）。
                 */
                StageVolumeGroupView? stageGroup = FindStageVolumeGroup(stageVolumeScan.Groups, file);

                /*
                 * ⛔ 这一道同样只问**过程物**（引擎这一趟自己写出来的东西已经在上面的 isProcessArtifact
                 * 里被摘掉了）：那两道闸门要回答的是"这一堆是不是待续解的分卷"，引擎刚解出来的产物
                 * 根本不是 —— 真机 222 那一单就是被这里拦下的（`222\222.zip` 与 `222\222.zi删除p`
                 * 是一份完整的两卷 split zip，判定器说"名字不标准"⇒ 整层作废、产物没落地）。
                 */
                if (isProcessArtifact && stageGroup != null)
                {
                    if (!stageGroup.Resolution.CanEnterDeletableRestItems)
                    {
                        incompleteVolumeGroups.Add($"{stageGroup.Resolution.BaseName}：{stageGroup.Resolution.Reason}");
                        continue;
                    }

                    /*
                     * 整组完整、名字也标准 ⇒ 照老路算"其余物"（源包走完可以进其余物再删）。
                     *
                     * ⚠ **但引擎刚解出来的那一组不算**（用户 2026-10-01 真机）：它是**内容物**
                     * —— 真机里 `222\222.zip` + `222\222.z01` 就是用户要拿去打开的那一份，
                     * 被算进"可删的其余物"就是把它删掉。判据只有一条事实：
                     * 这一组的成员**都是刚刚由本方法改回标准名的那一批**（`renamedVolumeMembers`）
                     * —— "刚改过名"正是"这是我们自己解出来的、名字不标准的那一组"的直接证据。
                     */
                    if (stageGroup.FilePaths.All(
                            member => renamedVolumeMembers.Contains(SafePathHelper.GetFullPathSafe(member))))
                    {
                        isProcessArtifact = false;
                    }
                    else
                    {
                        isProcessArtifact = true;
                    }
                }

                /*
                 * 0 字节的**归档 / 分卷**不是有效的其余物（用户 2026-09-24 要求）。
                 *
                 * 依据：任何真实归档都不可能只有 0 字节（连文件头都放不下）——
                 * 这种文件只有一个来源：7z 用错密码 / 数据不全时写出的**桩文件**
                 * （真机现场就是 `2部轻熟1.7z.001/.002` 两个 0 字节）。
                 * 把它搬进 `其余物` 等于把垃圾当成"用户以后可能还要的东西"收起来，
                 * 而且下一轮还会被当成内层包去解 —— 用户明确要求"不得搬进其余物"。
                 *
                 * ⚠ 只对**归档 / 分卷后缀**生效：内容物里本来就有 0 字节文件是正常情形
                 * （说明文件、占位文件），那些照常定稿（见下面 totalStageBytes 那条的说明）。
                 */
                if (isProcessArtifact && size <= 0)
                {
                    zeroByteArtifacts++;
                    continue;
                }

                /*
                 * ===== ⛔ 刚改过名的那一组是**内容物**，不是过程物（用户 2026-10-01 真机）=====
                 *
                 * 它们在上一段（`NormalizeEngineOutputVolumeNames`）里被改成标准卷名，
                 * 于是下面那道"整组完整 + 名字标准"的闸门会顺理成章地把它们算成"可删的其余物"
                 * —— 而用户要的恰恰是**拿到这一组去打开**（它是内容物）。
                 * 判据只有一条事实：这个文件是不是**刚刚由本方法改名的那一批**。
                 */
                if (isProcessArtifact
                    && renamedVolumeMembers.Contains(SafePathHelper.GetFullPathSafe(file)))
                {
                    isProcessArtifact = false;
                }

                /*
                 * ===== ⛔ 孤儿卷的兜底闸门（带卷标记、却没落进任何一组）=====
                 *
                 * 上面那道闸门只覆盖"归组认得出来的组"。可 <see cref="VolumeGroupDetector"/> 对
                 * **名字被伪装过**的桶还要过一道尺寸规律，过不了就把整桶丢掉（2026-09-28 三层方案，
                 * 为了"认错组比不认糟得多"）。丢掉之后，`x.7z.001` 这种文件在老写法里只剩
                 * "扩展名像分卷 ⇒ 可删的其余物"这一条路 —— 而它旁边那个 `x.7z.002删除`
                 * 恰恰可能是它的兄弟。**一卷被删掉就再也解不开整组**。
                 *
                 * 所以这一档回头**逐文件问判定器**（复用同一份目录事实，不重新列目录）：
                 *   · 判定器证明"整组完整且名字标准" ⇒ 照老路当其余物；
                 *   · 其余一切结论（判不出 / 缺卷 / 疑缺 / 名字不标准）⇒ **整份计划作废**，
                 *     一个字节都不搬、不删（兜底落在"什么都不做"那一档，AGENTS §9.5）。
                 *
                 * ⛔ 只对**卷标记后缀**（`.001` / `.z01` / `.r00` / `.partN`）做这一步：
                 * `.zip` / `.7z` 这种归档本体自己就是完整包，不走分卷这条判据。
                 */
                if (isProcessArtifact && stageGroup == null && IsVolumePartFile(file))
                {
                    /*
                     * ⚠ 例外：这一片正是**我们已经在收的那一组**的片（用户 2026-10-06 真机：
                     * `111(2)_.zip` 解出来的 `111.z01` 就是那一组跨盘 ZIP 的第 1 片，收卷那一档
                     * 已经把它接到入口包那一层、整组由批末那一站解开）。
                     * ⇒ 那不叫"分卷组不完整"，叫"这一组正在收" ⇒ ⛔ 不许拿这道闸门把整层定稿拦死
                     * （真机症状：`定稿布局规划失败：这一层里有分卷组不完整：111` ⇒ 整层不定稿 ⇒
                     * 其余物与源包全留着不动）。
                     * ⛔ 判据只读**既有账**（`_groupPieceProducers` 的组基名），⛔ 不放宽"证明完整"那一步。
                     */
                    string thisBaseName = FileNameHelper.GetArchiveBaseName(Path.GetFileName(file));

                    if (assembledVolumeBaseNames != null
                        && thisBaseName.Length > 0
                        && assembledVolumeBaseNames.Contains(thisBaseName))
                    {
                        continue;
                    }

                    VolumeGroupResolution orphan = new VolumeGroupResolver().Resolve(new VolumeGroupQuery
                    {
                        AnchorPath = file,
                        DirectoryEntries = stageVolumeScan.EntriesFor(file),
                        AllowTrialOpen = false
                    });

                    if (!orphan.CanEnterDeletableRestItems)
                    {
                        incompleteVolumeGroups.Add($"{Path.GetFileName(file)}：{orphan.Reason}");
                        continue;
                    }
                }

                staged.Add(new StagedEntry
                {
                    RelativePath = Path.GetRelativePath(stageRoot, file),
                    Size = size,
                    IsProcessArtifact = isProcessArtifact
                });
            }

            /*
             * ⛔ 只要发现**任何一组分卷证明不了完整**，这一层的计划就整份作废（不定稿、不建其余物、
             * 不搬任何文件）—— 调用方拿到 Failed 后按失败收场：源包与其余物的删除全部不发生。
             * 理由见上面那道闸门（用户 2026-09-30：25 GB 就是这么没的）。
             */
            if (incompleteVolumeGroups.Count > 0)
            {
                string detail = string.Join("；", incompleteVolumeGroups.Distinct(StringComparer.Ordinal).Take(3));

                return new FinalLayoutPlan
                {
                    Failed = true,
                    BlockedByIncompleteVolumeGroup = true,
                    FailureReason = $"这一层里有分卷组不完整：{detail}。已按「什么都不动」处理 —— "
                        + "产物与源包一个字节都不搬、不删（补齐全套分卷后再来）",
                    Summary = "分卷组不完整，未定稿"
                };
            }

            if (zeroByteArtifacts > 0)
            {
                warnings.Add(
                    $"{zeroByteArtifacts} 个 0 字节的归档 / 分卷文件不算其余物（它们不是压缩包，留在暂存目录里）");
            }

            /*
             * 暂存区里**全是 0 字节产物** → 一律不定稿（用户 2026-09-24 铁证，不变量 6 的同一口径）。
             *
             * 现场：外层 ZIP 里有加密条目，7z 用错密码时"半成功"地写出两个 **0 字节**的
             * `*.7z.001/.002`。这些桩文件既不是内容物、也不是"内层包"（0 字节的 `*.7z.001` 不是包），
             * 更不该被搬进 `其余物` —— 用户明确要求的三条，全都在这里落地：
             * 计划为空 ⇒ 不定稿、不建最终目录、不建 `其余物`、源包一个字节都不动。
             *
             * 为什么以"整棵树的总字节"为判据（而不是逐个文件丢掉 0 字节的那些）：
             * 归档里**本来就有空文件**是正常情形（说明文件、占位文件），那种包必须照常定稿；
             * 0 字节桩的问题只出现在"整个产物全是 0 字节"这种"什么都没解出来"的形状上。
             * 这个取舍写在 docs/需求变更.md 的边界里，不假装覆盖了第一种情形。
             */
            if (totalStageBytes <= 0 && staged.Count > 0)
            {
                return new FinalLayoutPlan
                {
                    ZeroByteProductsOnly = true,
                    Summary = "暂存区里只有 0 字节产物（没有解出任何内容）"
                };
            }

            // 目录条目也要给：判定表要认"自带一层文件夹""多重空目录嵌套""单链"这些形态，
            // 光有文件路径推不出"这一层是不是空的"。
            foreach (string directory in Directory.EnumerateDirectories(stageRoot, "*", SearchOption.AllDirectories))
            {
                staged.Add(new StagedEntry
                {
                    RelativePath = Path.GetRelativePath(stageRoot, directory),
                    IsDirectory = true
                });
            }

            if (staged.Count == 0)
            {
                return new FinalLayoutPlan();
            }

            FinalizePlan finalize = ResultFinalizer.Plan(
                staged,
                destinationDirectory,
                terminalLayout,
                archiveBaseName,
                contentRoot: null,
                stagingRoot: stageRoot,
                sharedOutputRoot: sharedOutputRoot,
                specialExtraction: specialExtraction,
                suppressPackageFolderLayer: suppressPackageFolderLayer,
                innermostPackageBaseName: innermostPackageBaseName);

            /*
             * 整组改名那一档也进 warnings（调用方按既有口径逐条写日志：
             * 成功那一条是 INFO、没改成那一条是 WARN，见 ApplyStageCommitAsync 里 plan.Warnings 的消费）。
             */
            warnings.AddRange(volumeRenameChanges.Select(change => change.Message));

            var processSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (PlannedMove move in finalize.ProcessArtifactMoves)
            {
                processSources.Add(move.From);
            }

            return new FinalLayoutPlan
            {
                Failed = finalize.Layout == FinalizeLayoutKind.Failed,
                FailureReason = finalize.FailureReason,
                Layout = finalize.Layout,
                Moves = finalize.Moves,
                PlannedContentCount = finalize.ContentMoves.Count,
                ProcessArtifactSources = processSources,
                ProcessArtifactDirectory = finalize.ProcessArtifactDirectory,
                ProcessArtifactTotalSize = finalize.ProcessArtifactTotalSize,
                ContentFileCount = finalize.ContentFileCount,
                Summary = finalize.Summary,
                SpecialExtractionApplied = finalize.SpecialExtractionApplied,
                Warnings = finalize.Warnings.Concat(warnings).ToList()
            };
        }

        /// <summary>
        /// 本任务的**最终输出目录**：归集开启时产物整棵被搬到归集目录，权威落点就是它。
        ///
        /// 转发到 <see cref="OneClickCoordinator.ResolveContinuationOutputDirectory"/>，
        /// 不在这里重写一遍 —— 续解、归集、源包补搬必须用同一个"内容物最终在哪"的口径。
        /// </summary>
        private static string ResolveContinuationOutputDirectory(ArchiveTask task) =>
            OneClickCoordinator.ResolveContinuationOutputDirectory(task);

        /// <summary>
        /// 内容物**实际所在的那一层目录**（第 35 条）。
        ///
        /// <para>定稿计划的 <c>DestinationDirectory</c> 是"落点目录"，但内容物往往被套了一层
        /// （判定表：多文件套一层 / 包里自带一个同名文件夹）—— 真机现场就是这样：
        /// 共用输出根是 <c>BBB\111</c>，而定稿把那**一个**文件夹 <c>2222</c> 搬了进去，
        /// 于是内容物真正所在的那一层是 <c>BBB\111\2222</c>。</para>
        ///
        /// <para>判据：落点目录里**除了「其余物」和工作区自己那棵树之外**只剩一个条目、而且它是目录
        /// → 那一层就是内容物层；否则就是落点目录本身（内容物是散文件，或本来就有多项）。
        /// 续解产物按它落位，才能做到"一个源包 = 一个目录"（用户第 35 条原话：
        /// "你应该是要将 `BBB\111\222` 文件夹放在 `BBB\111\2222` 这个里面"）。</para>
        ///
        /// <para>⛔ <b>必须排除工作区那棵树</b>（用户 2026-09-30）：工作区默认就建在目标目录里面
        /// （<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>），不排除的话它会被当成"唯一的那一个子目录"，
        /// 于是"内容物层"被解析成工作区 —— 续解产物会直接落进工作区，然后被收尾清理一起删掉。</para>
        /// </summary>
        private string ResolveContentLayerDirectory(string destinationDirectory, string processArtifactDirectory)
        {
            if (string.IsNullOrWhiteSpace(destinationDirectory))
            {
                return string.Empty;
            }

            try
            {
                if (!Directory.Exists(destinationDirectory))
                {
                    return destinationDirectory;
                }

                string workRoot = _pathService.WorkDirectory;

                List<string> entries = Directory
                    .GetFileSystemEntries(destinationDirectory)
                    .Where(path => !ProcessArtifactLayout.IsArtifactDirectoryName(path))
                    .Where(path => !WorkspaceTree.ShouldSkipEntry(path, workRoot))
                    .ToList();

                if (entries.Count == 1 && Directory.Exists(entries[0]))
                {
                    return entries[0];
                }
            }
            catch
            {
                // 读不了目录（权限 / 刚好被删）：按落点目录回落，绝不因此让定稿结论出问题。
            }

            return destinationDirectory;
        }

        private static string ResolveContentDirectoryAfterCollect(
            StageCommitResult commit,
            CollectResult? collected)
        {
            string planned = commit.ContentDirectory;

            if (string.IsNullOrWhiteSpace(planned))
            {
                return string.Empty;
            }

            if (collected is not { Success: true } || string.IsNullOrWhiteSpace(collected.DestinationPath))
            {
                return planned;
            }

            try
            {
                // 内容目录相对**输出目录**的那一段在归集之后原样保留（归集是整棵搬走）。
                string outputDirectory = commit.ContentDirectory;

                if (!string.IsNullOrWhiteSpace(commit.ProcessArtifactDirectory))
                {
                    outputDirectory = Path.GetDirectoryName(commit.ProcessArtifactDirectory) ?? outputDirectory;
                }

                string moved = Path.Combine(
                    collected.DestinationPath,
                    Path.GetRelativePath(outputDirectory, planned));

                if (SafePathHelper.DirectoryExists(moved))
                {
                    return moved;
                }

                return SafePathHelper.DirectoryExists(planned) ? planned : moved;
            }
            catch
            {
                return planned;
            }
        }

        /// <summary>
        /// 定稿冲突预检：**只读**地算出"计划里哪些落点已经被占着"。
        ///
        /// 为什么要先算一遍：Ask 档必须**在动最终目录之前**拿到答案（也就是"暂停该任务"），
        /// 而落位那一刻已经在后台搬运的中途，边搬边问会让最终目录进退两难
        /// （一部分已经落位、剩下的在等一个还没出现的答案）。
        /// 本方法只读目录、只判存在，一个字节都不写；**只允许在后台线程上跑**。
        /// </summary>
        /// <param name="recursion">
        /// 这一次递归的结果（与真正的定稿读**同一个**对象）：落点最少两层那条红线的判据
        /// （"最后一个压缩包那一层"）由它算出来 —— 预检按平铺算、定稿按套层算就会得到
        /// "预检说有冲突、实际没有"这种自相矛盾的结论。
        /// </param>
        private ConflictPrecheck PrecheckFinalLayoutConflicts(
            ArchiveTask task,
            string stageDirectory,
            string destinationDirectory,
            bool sharedOutputRoot,
            TerminalLayoutMode terminalLayout,
            SpecialExtractionPlan? specialExtraction = null,
            RecursionResult? recursion = null)
        {
            var precheck = new ConflictPrecheck();

            if (string.IsNullOrWhiteSpace(stageDirectory) ||
                string.IsNullOrWhiteSpace(destinationDirectory) ||
                !Directory.Exists(stageDirectory))
            {
                return precheck;
            }

            FinalLayoutPlan plan;

            try
            {
                plan = PlanFinalLayoutForTask(task, stageDirectory, destinationDirectory, sharedOutputRoot, terminalLayout, specialExtraction, recursion);
            }
            catch
            {
                // 预检失败不改变任何结论：真正的失败会在定稿那一步如实报出来（这里只当"没有冲突"）。
                return precheck;
            }

            if (plan.Failed || plan.Moves.Count == 0)
            {
                return precheck;
            }

            foreach (PlannedMove move in plan.Moves)
            {
                if (!File.Exists(move.To) && !Directory.Exists(move.To))
                {
                    continue;
                }

                ConflictTargetKind kind = Directory.Exists(move.To) && !File.Exists(move.To)
                    ? ConflictTargetKind.Directory
                    : ConflictTargetKind.File;

                precheck.TotalCount++;

                if (!plan.ProcessArtifactSources.Contains(move.From))
                {
                    precheck.ContentCount++;
                }

                if (precheck.Samples.Count < MaxConflictSamplesInDialog)
                {
                    precheck.Samples.Add((move.To, kind));
                }
            }

            return precheck;
        }

        /// <summary>
        /// 执行定稿：按 <see cref="PlanFinalLayout"/> 的计划把暂存产物搬进最终目录。
        ///
        /// **只允许在后台线程上跑**（几千个 File.Move、跨盘还是拷贝）。
        /// 四条硬要求：
        /// ① 取消时把已经搬过去的**搬回来**（最终目录不许留半成品，契约 §6 第 2 条）；
        /// ② 同名绝不**默认**覆盖（AutoRename 改名，记进 <see cref="StageCommitResult.RenamedCount"/>）；
        /// ③ 单个文件搬不动只记一笔，不拖垮其余文件（与 <see cref="ExtractionWorkspace"/> 的搬运口径一致）；
        /// ④ 用户明确选了「覆盖」时才覆盖，而且必须走 <see cref="PathService.TryOverwriteLanding"/>
        ///    的两阶段（先挪到临时名 → 落位 → 再删），并逐个落点写日志留痕。
        /// </summary>
        /// <param name="conflictAction">同名冲突档（<see cref="ConflictActions"/> 的值）。</param>
        /// <param name="conflictDecision">
        /// 用户对同名冲突的答案（Ask 档；非 Ask 档为 null）。为 null 且档位是 Ask 时，
        /// <see cref="PathService.ResolveConflict"/> 会退回保守档（跳过）—— 绝不替用户决定成覆盖。
        /// </param>
        private StageCommitResult ExecuteFinalLayout(
            ArchiveTask task,
            string stageDirectory,
            string destinationDirectory,
            bool sharedOutputRoot,
            TerminalLayoutMode terminalLayout,
            string conflictAction,
            ConflictDecision? conflictDecision,
            CancellationToken cancellationToken,
            bool verificationPassed = true,
            SpecialExtractionPlan? specialExtraction = null,
            RecursionResult? recursion = null)
        {
            var logEntries = new List<(string Level, string Message)>();

            if (string.IsNullOrWhiteSpace(stageDirectory) ||
                string.IsNullOrWhiteSpace(destinationDirectory) ||
                !Directory.Exists(stageDirectory))
            {
                return new StageCommitResult { Attempted = false };
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new StageCommitResult
                {
                    Attempted = true,
                    Cancelled = true,
                    Message = "定稿开始前已取消"
                };
            }

            FinalLayoutPlan plan;

            try
            {
                plan = PlanFinalLayoutForTask(
                    task,
                    stageDirectory,
                    destinationDirectory,
                    sharedOutputRoot,
                    terminalLayout,
                    specialExtraction,
                    recursion);
            }
            catch (Exception ex)
            {
                string readFailure = $"读取暂存目录失败：{ex.Message}";

                logEntries.Add(("ERROR", $"{task.LogName}：{readFailure}"));

                return new StageCommitResult
                {
                    Attempted = true,
                    FailedCount = 1,
                    Failures = new[] { readFailure },
                    Message = readFailure,
                    LogEntries = logEntries
                };
            }

            if (plan.Failed)
            {
                /*
                 * ⚠ 2026-10-09（用户点名第二条红字）：**"分卷组不完整"这一档不是失败、也不写 ERROR**。
                 * 用户口径：「连完整的都没有，这就不是程序的错误」＋「在最后一步之前碰到分卷缺失的就先跳过」。
                 * 判据读**结构化标志**（`plan.BlockedByIncompleteVolumeGroup`），⛔ 不比中文文案；
                 * ⛔ "什么都不动"一个字没放松（照旧不定稿、不搬、不删）。
                 */
                string planFailure = plan.BlockedByIncompleteVolumeGroup
                    ? "这一层里有分卷组还没凑齐（先跳过，等最后那一轮再判）：" + plan.FailureReason
                    : "定稿布局规划失败：" + plan.FailureReason;

                logEntries.Add((
                    plan.BlockedByIncompleteVolumeGroup ? "WARN" : "ERROR",
                    $"{task.LogName}：{planFailure}"));

                return new StageCommitResult
                {
                    Attempted = true,
                    FailedCount = 1,
                    Failures = new[] { planFailure },
                    Message = planFailure,
                    LogEntries = logEntries
                };
            }

            foreach (string warning in plan.Warnings)
            {
                logEntries.Add(("WARN", $"{task.LogName}：定稿提醒 —— {warning}"));
            }

            if (plan.Moves.Count == 0)
            {
                /*
                 * 引擎什么都没写出来（空包）：没有可定稿的东西，也不该凭空造一个空目录给用户。
                 *
                 * ⚠ "只有 0 字节产物"要单独说清楚（用户 2026-09-24 要求）：它看起来也是"没有产物"，
                 * 但成因完全不同（密码不对 / 数据不全时 7z 会写 0 字节的桩文件），
                 * 写成"这个包本来就是空的"会把人引到错的方向 —— 那句话必须带上"校验未通过，产物视为无效"。
                 */
                string emptyMessage = plan.ZeroByteProductsOnly
                    ? "暂存目录里只有 0 字节产物 —— 校验未通过，产物视为无效：不定稿、不生成其余物、源包留在原地。"
                    : "暂存目录里没有产物，没有需要定稿的内容。";

                logEntries.Add(("WARN", $"{task.LogName}：{emptyMessage}"));

                return new StageCommitResult
                {
                    Attempted = true,
                    Message = emptyMessage,
                    LogEntries = logEntries
                };
            }

            bool destinationExisted = Directory.Exists(destinationDirectory);

            if (!SafePathHelper.EnsureDirectoryExists(destinationDirectory))
            {
                string createFailure = $"最终目录不存在且创建失败：{destinationDirectory}";

                logEntries.Add(("ERROR", $"{task.LogName}：{createFailure}"));

                return new StageCommitResult
                {
                    Attempted = true,
                    FailedCount = plan.Moves.Count,
                    Failures = new[] { createFailure },
                    Message = createFailure,
                    LogEntries = logEntries
                };
            }

            var moved = new List<(string From, string To)>();
            var failures = new List<string>();
            var overwritten = new List<string>();

            int movedContent = 0;
            int movedProcess = 0;
            int renamed = 0;
            int skippedContent = 0;
            int skippedProcess = 0;

            // 计划里 **内容物在前、其余物在后**：先让用户要的东西落位，再收拾过程物。
            foreach (PlannedMove move in plan.Moves)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    RollBackFinalLayout(moved, destinationDirectory, destinationExisted);

                    logEntries.Add(("WARN", $"{task.LogName}：定稿被取消，已把 {moved.Count} 项退回暂存目录。"));

                    return new StageCommitResult
                    {
                        Attempted = true,
                        Cancelled = true,
                        Message = "定稿被取消，产物已退回暂存目录",
                        LogEntries = logEntries
                    };
                }

                bool isProcessArtifact = plan.ProcessArtifactSources.Contains(move.From);

                try
                {
                    // 计划里的一条可能是**文件**，也可能是**整个目录**（判定表 2/3/4 会整棵搬）。
                    bool isDirectory = Directory.Exists(move.From) && !File.Exists(move.From);

                    /*
                     * ⚠ 2026-10-09 真机 `EEEE`（用户最在意的那条「为什么有两个 `111.zip`」）：
                     * **目标位上已经是同一份物理文件 ⇒ 不必再搬**（⛔ 不删、⛔ 不改名，直接跳过这一条）。
                     *
                     * 现场（这一条**两个改动缺一不可**：上面把"本链产出"判成内容物之后，
                     * 它就会来定稿落地，于是撞上这个形状）：接片给"**本单自己暂存目录**"里那一份
                     * **建的是硬链接**（`ShouldMovePieceIntoGroup` 那一档只建链接、绝不搬走，
                     * 真机行 146 的 `moveIntoGroup=False` 就是它）⇒ `111\111\111.zip` 与暂存里的
                     * `111.zip` **是同一个 FileId**。定稿再搬一次，只会在同一个目录里多出一个
                     * `111(1).zip` —— 用户看到的"两个"就从"其余物里一个"变成"`(1)` 一个，症状根本不消失。
                     *
                     * 判据只读事实（唯一出口 <see cref="FileIdentity.IsSamePhysicalFile"/>：卷序列号 + 文件索引），
                     * ⛔ 不比名字、⛔ 不看大小。**一个字节都不动**：数据本来就已经在目标位上（同一条记录）。
                     */
                    if (!isDirectory && FileIdentity.IsSamePhysicalFile(move.From, move.To))
                    {
                        logEntries.Add((
                            "INFO",
                            $"{task.LogName}：{Path.GetFileName(move.To)} 已经在目标位上、而且是「同一份文件」"
                            + "（接片时建的硬链接）—— 不必再搬一次，也不留多余的名字。"));

                        continue;
                    }

                    /*
                     * ⚠ 2026-10-09 真机 `EEEE` 的另一半（同一个"两个 `111.zip`"的另一形状）：
                     * **这一片已经由接片搬进"这一组的落点层"了 ⇒ 产出方自己落点里这一份是多余名**。
                     *
                     * 与上面那一条的区别（真机行 161 实测）：那一档 `moveIntoGroup=True` ⇒ 接片对
                     * 层产物用的是**移动**（不是建链接），所以两边**不是同一个 FileId**，
                     * `IsSamePhysicalFile` 判不出来 —— 只能按"组层是不是已经有这个名字"判。
                     *
                     * 判据两条事实，缺一不可：① 这一份是**分卷片**（`IsVolumePartFile`）；
                     * ② 这一组的**落点层**（唯一出口 `ResolveBatchGroupDirectory`）里已经有同名的一份。
                     * ⇒ 那才是它的正位（整组在那一层拼、在那一层解），产出方落点里这一份就是
                     * 用户点名过的"没人认领的名字"（`SiblingFolderVolumeGatherTests` ③ 那条用例钉的就是它）。
                     * ⛔ 数据一个字节都不丢：正位那一份已经在了。
                     */
                    if (!isDirectory && IsVolumePartFile(move.From))
                    {
                        string pieceName = Path.GetFileName(move.From);

                        /*
                         * ⚠ **读"接片那一刻定下来的层"，⛔ 不在这里重算**（2026-10-11 实测）：
                         * 这一档原来转调 `ResolveBatchGroupDirectory` 的 1 参版，而它当年靠"先到先得"那条
                         * 兜底才答得出层；兜底按用户口径删掉之后它答不出 ⇒ 这道闸门**静默失效**
                         * ⇒ 本单的工作区那一份又被发布到产出方落点里，成了**没人认领的多余名**
                         * （实测残留逐字 `…\out-…\111(2)_\111.z01`；守门用例
                         * `SiblingFolderVolumeGatherTests.真机形状_过路层那一单的其余物按档删掉_48MB那处`
                         * 第 ③ 条断言「盘上收口：只剩成品本身」钉的就是它）。
                         * 层只有一个出口：接片那一档算过的那个（`_groupLayerByBaseName`）。
                         */
                        string groupLayer = _groupLayerByBaseName.TryGetValue(
                            FileNameHelper.GetArchiveBaseName(pieceName),
                            out string? rememberedLayer)
                            ? rememberedLayer
                            : string.Empty;

                        if (groupLayer.Length > 0 && File.Exists(Path.Combine(groupLayer, pieceName)))
                        {
                            logEntries.Add((
                                "INFO",
                                $"{task.LogName}：{pieceName} 已经由接片搬进「{Path.GetFileName(groupLayer)}」"
                                + "这一组的落点层了 —— 产出方这里这一份是多余的名字，不落地（一个字节都不丢）。"));

                            continue;
                        }
                    }

                    if (!SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(move.To)))
                    {
                        failures.Add($"{Path.GetFileName(move.To)}（无法创建目标目录）");
                        continue;
                    }

                    string target = move.To;

                    // 覆盖档由 TryOverwriteLanding 自己把新条目搬过去（它要先腾位），这里不再搬第二次。
                    bool landed = false;

                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        /*
                         * 目标是**最终目录自己**时不许覆盖：那等于把刚落位的产物连同目录一起顶掉，
                         * 而且是用户完全没预期的一次删除。规划器现在不会给出这种条目
                         * （计划里永远是 destDir\子项），这里是最后一道保险。
                         */
                        bool isDestinationRoot = SafePathHelper.PathEquals(target, destinationDirectory);

                        ConflictResolution resolution = isDestinationRoot
                            ? new ConflictResolution(ConflictChoice.AutoRename, target, true, false)
                            : _pathService.ResolveConflict(
                                target,
                                isDirectory ? ConflictTargetKind.Directory : ConflictTargetKind.File,
                                conflictAction,
                                conflictDecision);

                        if (resolution.Choice == ConflictChoice.Skip)
                        {
                            // 「跳过」：这一项不动，已有文件一个字节都不改（新产物留在暂存目录）。
                            if (isProcessArtifact)
                            {
                                skippedProcess++;
                            }
                            else
                            {
                                skippedContent++;
                            }

                            logEntries.Add(("WARN", $"{task.LogName}：同名冲突，按你的选择跳过：{target}"));
                            continue;
                        }

                        if (resolution.Choice == ConflictChoice.Overwrite && !isDestinationRoot)
                        {
                            /*
                             * 「覆盖」= 两阶段落位（AGENTS.md §6 第 3 条）：
                             * 先把占位者挪到临时名 → 新条目落位 → 落位成功后才删掉那个临时名。
                             * **禁止"先 File.Delete 再 Move"**：中间任何失败都会让用户想保留的那一份
                             * 永久消失，而新条目还没到位。
                             */
                            if (!_pathService.TryOverwriteLanding(
                                    move.From,
                                    target,
                                    isDirectory,
                                    out string overwriteError,
                                    out string overwriteNote))
                            {
                                failures.Add($"{Path.GetFileName(move.From)}（{overwriteError}）");
                                continue;
                            }

                            // 真正覆盖前要留痕：哪个落点被顶掉了必须写进日志（AGENTS.md §6 第 3 条 / 用户要求）。
                            overwritten.Add(target);
                            landed = true;

                            logEntries.Add((
                                "WARN",
                                $"{task.LogName}：同名冲突，按你的选择覆盖：{target}（{overwriteNote}）"));
                        }
                        else
                        {
                            // 自动重命名：换成 名字(1)，**绝不覆盖**（AGENTS.md §6 第 3 条）。
                            string renamedTarget = resolution.TargetPath;

                            if (!string.Equals(renamedTarget, target, StringComparison.OrdinalIgnoreCase))
                            {
                                renamed++;
                            }

                            logEntries.Add(("INFO", $"{task.LogName}：同名冲突，改名落位（绝不覆盖）：{renamedTarget}"));

                            target = renamedTarget;
                        }
                    }

                    if (!landed)
                    {
                        if (isDirectory)
                        {
                            Directory.Move(move.From, target);
                        }
                        else
                        {
                            File.Move(move.From, target);
                        }
                    }

                    moved.Add((move.From, target));

                    if (isProcessArtifact)
                    {
                        movedProcess++;
                    }
                    else
                    {
                        movedContent++;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(move.From)}（{ex.Message}）");
                }
            }

            /*
             * 抠出来的内嵌归档：它是从源文件按偏移再生出来的派生数据，契约 §3.2 把它列为其余物。
             * 它**不在暂存区里**（在任务工作区根下，是解压的输入），所以不在上面的计划里，单独搬一次。
             * 为什么默认不搬：见 MoveCarvedArtifactIntoProcessDirectory 的说明。
             */
            if (MoveCarvedArtifactIntoProcessDirectory && task.EmbeddedArchiveOffset > 0)
            {
                string carvedPath = BuildEmbeddedArchivePath(task);

                if (File.Exists(carvedPath))
                {
                    try
                    {
                        string processRoot = string.IsNullOrWhiteSpace(plan.ProcessArtifactDirectory)
                            ? Path.Combine(destinationDirectory, ProcessArtifactDirectoryName)
                            : plan.ProcessArtifactDirectory;

                        string carvedTarget = Path.Combine(processRoot, Path.GetFileName(carvedPath));

                        SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(carvedTarget));

                        if (File.Exists(carvedTarget))
                        {
                            carvedTarget = SafePathHelper.AutoRenameFilePath(carvedTarget);
                            renamed++;
                        }

                        File.Move(carvedPath, carvedTarget);

                        movedProcess++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{Path.GetFileName(carvedPath)}（抠出的中间归档：{ex.Message}）");
                    }
                }
            }

            string processDirectory = string.IsNullOrWhiteSpace(plan.ProcessArtifactDirectory)
                ? Path.Combine(destinationDirectory, ProcessArtifactDirectoryName)
                : plan.ProcessArtifactDirectory;

            /*
             * 定稿摘要 —— **"没能搬运"那几条必须带上明细**（用户 2026-10-04 真机）。
             *
             * 现场：`老王.apk` 那一单写着「定稿搬运失败，产物没能写进输出目录：…；10 项没能搬运」，
             * ①页「错误信息」列与日志里都只有这一个数字 —— 那 10 条 `文件名（原因原文）` 在收集处
             * （上面的 `failures`）就已经拿到了，却在 CommitFailed 的提前 return 那条路上被丢掉，
             * 用户手里没有任何可行动的线索（而批末诊断还指着他去看"引擎原话"，定稿类失败没有引擎原话）。
             *
             * 为什么补在摘要里而不是另写一行：摘要这一份字符串**同时**是日志那一行
             * （"定稿完成 —— …"）与①页「错误信息」列（`StageCommitResult.Message` →
             * `CommitFailureMessage`）的来源 ⇒ 一处出口、两个消费点，不会各写一份、也不会对不上。
             * ⛔ 判据一个都没动：这里只把**已经收集到的**事实说出来。
             */
            string summary =
                $"内容物 {plan.ContentFileCount} 个文件 → {destinationDirectory}" +
                (movedProcess > 0
                    ? $"；其余物 {movedProcess} 项（{plan.ProcessArtifactTotalSize} 字节）→ {processDirectory}"
                    : string.Empty) +
                (renamed > 0 ? $"；{renamed} 项同名，已改名未覆盖" : string.Empty) +
                (overwritten.Count > 0 ? $"；{overwritten.Count} 项同名，按你的选择覆盖（已写日志）" : string.Empty) +
                (skippedContent + skippedProcess > 0 ? $"；{skippedContent + skippedProcess} 项同名，按你的选择跳过" : string.Empty) +
                (failures.Count > 0 ? $"；{failures.Count} 项没能搬运" : string.Empty) +
                (failures.Count > 0
                    ? string.Format(
                        CultureInfo.CurrentCulture,
                        StatusText.FinalizeMoveFailureDetailFormat,
                        MaxFinalizeContentNameLines,
                        DescribeFinalizeMoveFailures(failures))
                    : string.Empty);

            /*
             * 「定稿完成」这句话**不许在"没解出东西"时读成正常完成**（用户 2026-09-24 要求）。
             *
             * 现场误读：日志写着"定稿完成 —— 内容物 0 个文件 → …；其余物 2 项（0 字节）→ …"，
             * 而实际上校验早已判否、那 2 项是 0 字节垃圾 —— 用户看到"定稿完成"就以为流程正常走完了。
             * 现在两种情况各挂一句明确的结论：
             * · 校验未通过 → "⚠ 校验未通过，产物视为无效"；
             * · 校验通过但一个内容物都没有 → 如实说"这次没有内容物"（那是合法的，例如只出过程物）。
             */
            string contentNote = plan.ContentFileCount > 0
                ? string.Empty
                : verificationPassed
                    ? "（⚠ 这次没有内容物，只有其余物）"
                    : "（⚠ 校验未通过，产物视为无效；不定稿有效内容、不搬源包）";

            logEntries.Add((failures.Count == 0 ? "INFO" : "WARN", $"{task.LogName}：定稿完成{contentNote} —— {summary}"));

            /*
             * 产物**点名**（用户 2026-09-25："出问题我看日志就能知道"）。
             *
             * 为什么必须列名字：续解那一步是按"这一轮新出现的文件"找下一层的，而名字被改坏的产物
             * （真机上是 `222.ra删除r`）在日志里如果只报个数，用户就只能自己去翻目录 —— 那次就是这么绕远的。
             * 上限 20 条，超出只报个数（几千个文件时不能让日志爆掉），但"还有多少没列"必须写出来。
             */
            if (plan.Moves.Count > 0)
            {
                List<PlannedMove> contentMoves = plan.Moves
                    .Where(move => !plan.ProcessArtifactSources.Contains(move.From))
                    .ToList();

                List<string> contentNames = contentMoves
                    .Select(move => Path.GetFileName(move.To))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Take(MaxFinalizeContentNameLines)
                    .ToList();

                if (contentNames.Count > 0)
                {
                    logEntries.Add((
                        "INFO",
                        $"{task.LogName}：本次内容物 —— {string.Join("、", contentNames)}"
                        + (contentMoves.Count > contentNames.Count
                            ? $"（…还有 {contentMoves.Count - contentNames.Count} 项没列出来）"
                            : string.Empty)));
                }
            }

            // 覆盖留痕：被顶掉的落点单独再写一条，用户事后追查"我原来那份去哪了"时有据可依。
            foreach (string overwrittenPath in overwritten)
            {
                logEntries.Add(("WARN", $"{task.LogName}：已覆盖（原文件按两阶段落位删除）：{overwrittenPath}"));
            }

            return new StageCommitResult
            {
                Attempted = true,
                PlannedContentCount = plan.PlannedContentCount,
                MovedContentCount = movedContent,
                MovedProcessCount = movedProcess,
                ContentFileCount = plan.ContentFileCount,
                ProcessArtifactDirectory = processDirectory,
                ContentDirectory = ResolveContentLayerDirectory(destinationDirectory, processDirectory),
                ProcessArtifactBytes = plan.ProcessArtifactTotalSize,
                RenamedCount = renamed,
                SkippedCount = skippedContent + skippedProcess,
                SkippedContentCount = skippedContent,
                OverwrittenCount = overwritten.Count,
                OverwrittenPaths = overwritten,
                FailedCount = failures.Count,
                Failures = failures,
                Message = summary,
                LogEntries = logEntries
            };
        }


        /// <summary>
        /// 「本次内容物 —— 名字、名字…」最多列几个（用户 2026-09-25 第 44 条）。
        ///
        /// <para>那一行原来是**整份日志里最占字节的一条**（56 行 / 3 万字符：每行二十个文件名的完整清单，
        /// 而他这批是 68 套同名照片）。名字仍然有用（续解找下一层靠它、名字被改坏时肉眼能看出来），
        /// 但**前 5 个足够定性**：多出来的只写个数。</para>
        /// </summary>
        private const int MaxFinalizeContentNameLines = 5;


        /// <summary>
        /// 定稿布局计划：直接包住 <see cref="ResultFinalizer"/> 的结论，只加三样执行阶段要用的东西 ——
        /// 其余物源路径的集合（执行时判断一条移动属于哪一类）与"这是不是一次失败的规划"。
        ///
        /// 为什么不再自己算一套相对路径：判定表（契约 §3.1：终端单文件直接放 / 多文件套一层 /
        /// 多重空目录提上来 / 单链塌缩）**只有一份实现**，就在 <see cref="ResultFinalizer"/> 里。
        /// 本类只负责"把暂存树描述给它、把它的计划执行掉"。
        /// </summary>
        internal sealed class FinalLayoutPlan
        {
            public bool Failed { get; init; }

            public string FailureReason { get; init; } = string.Empty;

            public FinalizeLayoutKind Layout { get; init; } = FinalizeLayoutKind.Empty;

            /// <summary>全部移动（**内容物在前、其余物在后**，按这个顺序执行）。</summary>
            public IReadOnlyList<PlannedMove> Moves { get; init; } = Array.Empty<PlannedMove>();

            /// <summary>计划里的内容物条数。</summary>
            public int PlannedContentCount { get; init; }

            /// <summary>哪几条是其余物（按 From 查，执行时用）。</summary>
            public HashSet<string> ProcessArtifactSources { get; init; } = new(StringComparer.OrdinalIgnoreCase);

            public string ProcessArtifactDirectory { get; init; } = string.Empty;

            public long ProcessArtifactTotalSize { get; init; }

            public int ContentFileCount { get; init; }

            public string Summary { get; init; } = string.Empty;

            /// <summary>
            /// 这一单真的按**特定解压**跑了（判定表里"要套的那一层"没有套，规格 §3.5）。
            /// 日志与任务详情据此说清"这次为什么少了一层"（用户最恨"我以为它按默认跑的"）。
            /// </summary>
            public bool SpecialExtractionApplied { get; init; }

            /// <summary>
            /// 暂存区里**只有 0 字节产物**（用户 2026-09-24 铁证：密码不对时 7z 会写出 0 字节的桩文件）。
            ///
            /// <para>
            /// 这一档下 <see cref="Moves"/> 一定是空的（0 字节的东西不算产物，见
            /// <see cref="PlanFinalLayout"/>），所以"不定稿 / 不生成其余物 / 不搬源包"由控制流保证；
            /// 这个标志只用来把日志与结论说得更准（别让人读成"这个包本来就是空的"）。
            /// </para>
            /// </summary>
            public bool ZeroByteProductsOnly { get; init; }

            /// <summary>
            /// 这一层的计划作废，是因为**有一组分卷证明不了完整**（缺卷 / 疑缺卷 / 名字不标准）。
            ///
            /// <para>⚠ 2026-10-09：这一档**不是"失败"** —— 用户口径「连完整的都没有，这就不是程序的错误」、
            /// 「在最后一步之前碰到分卷缺失的就先跳过」⇒ 调用方据此把日志落成 **WARN + 先跳过**
            /// （原来一律写 ERROR「定稿布局规划失败」，用户点名那是红字误报）。
            /// ⛔ "什么都不动"这条判据一个字没放松：它照旧不定稿、不搬、不删。</para>
            /// </summary>
            public bool BlockedByIncompleteVolumeGroup { get; init; }

            public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        }

    }
}
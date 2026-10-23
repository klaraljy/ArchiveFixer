using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Services
{
    /// <summary>其余物这一步要问宿主的事（跨步账本、设置档位、路径服务、运行期 CTS）。</summary>
    internal interface IRestHandlingStepHost
    {
        PathService Paths { get; }

        bool SpaceTightThisBatch { get; }

        bool SpaceTightKeepSourceThisBatch { get; }

        bool LayerReclaimThisBatch { get; set; }

        CancellationTokenSource? OperationCts { get; }

        List<string> ChainInnerLayerInputs { get; }

        HashSet<ArchiveTask> PassThroughRestHandled { get; }

        Dictionary<string, List<(ArchiveTask Task, string PieceName)>> GroupPieceProducers { get; }

        Dictionary<string, string> GroupConsumerByName { get; }

        Dictionary<string, string> ConsumedVolumeSources { get; }

        List<(ArchiveTask Producer, string GroupBaseName, string SourcePath)> ByteBudgetLinkedSources { get; }

        AppSettings? Settings { get; }

        OneClickRunOptions? RunOptions { get; }

        ObservableCollection<ArchiveTask> Tasks { get; }

        bool IsStopping { get; }

        SourceHandlingMode EffectiveSourceHandling { get; }

        IDeleteExecutor? RestDeleteExecutor { get; }

        void AppendLog(string level, string message);

        List<string> SnapshotGroupPieceProducerBaseNames();

        bool TrySnapshotGroupPieceProducers(string baseName, out List<(ArchiveTask Task, string PieceName)> producers);

        void PurgeGroupLandedPieces(ArchiveTask consumer, string groupBaseName);

        void CollectConsumedSourcePiecesIntoRest(ArchiveTask consumer);

        ScheduledTaskRuntime GetOrCreateRuntime(ArchiveTask task);

        string? PurgeLayerSourcePackage(
            ArchiveTask task,
            OutputVerificationResult? verification,
            StageCommitResult? commit,
            List<(string Level, string Message)> logEntries,
            LayerPurgeTrigger trigger,
            ResultCompletenessVerdict? reclaimVerdict = null,
            bool reclaimRootSource = true);

        string ResolveContinuationOutputDirectory(ArchiveTask task);

        bool AllRestFilesBelongToGroup(ArchiveTask? task, string groupBaseName, IEnumerable<string>? fileNames);

        int CountRestEntries(string? directory);

        string DescribeShortDestination(string? collectedPath, string? outputPath);

        string DescribeShortTail(string? path, int segments);

        void LogPassThroughRestCriterionInputs(
            ArchiveTask producer,
            ArchiveTask? consumer,
            string groupBaseName,
            string restDirectory,
            string why);

        void NoteBatchPurge(long freedBytes, bool permanent);

        ArchiveTask[] SnapshotTaskTable(ObservableCollection<ArchiveTask> tasks);

        /// <summary>链完整性缺口的说明（唯一实现留在协调器的静态转发里，用例也直接钉它）。</summary>
        string? DescribeChainVerificationGap(
            string destinationDirectory,
            ArchiveTask rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks);
    }

    /// <summary>
    /// 其余物（过程残留）的处理（第 23 轮③：从 <c>ExtractionCoordinator.RestHandlingStep.cs</c> 搬成独立类）。
    /// ⛔ 删除前的六道门槛一条不绕过；判不出 ⇒ 一个字节都不动。
    /// </summary>
    internal sealed class RestHandlingStep
    {
        private readonly IRestHandlingStepHost _host;

        internal RestHandlingStep(IRestHandlingStepHost host)
        {
            _host = host;
        }

        /// <summary>
        /// 把一条续解链里**被真正解过的内层包**搬进根任务的「其余物」（第 35 条）。
        ///
        /// <para>只搬"事实成立"的那些：属于这条链、是续解任务、任务成功 + 校验通过、
        /// 文件还在、且落在根任务的输出范围之内。搬完把其余物目录记到根任务上 ——
        /// 这样「删除操作」那一档才有对象可删（此前内层包不在其余物里，彻底删除等于空转）。</para>
        ///
        /// <para>任何一步失败只写 WARN（内层包留在原地，不影响任何结论）；已经被搬进其余物的跳过（幂等）。</para>
        /// </summary>
        internal async Task CollectChainInnerPackagesIntoRestAsync(
            ArchiveTask rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks,
            CancellationToken cancellationToken)
        {
            if (chainTasks == null || chainTasks.Count == 0)
            {
                return;
            }

            /*
             * 归到哪一份其余物：根任务的（内容物那一层目录下的「其余物」）。
             * 定稿时已经记下过就沿用那一条（⛔ 不许现场重算：那一层可能因为撞名变成了「其余物(1)」）。
             */
            string restDirectory = rootTask.RestDirectoryPath;

            if (string.IsNullOrWhiteSpace(restDirectory))
            {
                string baseDirectory = string.IsNullOrWhiteSpace(rootTask.ContentDirectoryPath)
                    ? rootTask.OutputPath
                    : rootTask.ContentDirectoryPath;

                if (string.IsNullOrWhiteSpace(baseDirectory))
                {
                    return;
                }

                restDirectory = Path.Combine(baseDirectory, ProcessArtifactLayout.ArtifactDirectoryName);
            }

            string outputRoot = string.IsNullOrWhiteSpace(rootTask.OutputPath)
                ? rootTask.ContentDirectoryPath
                : rootTask.OutputPath;

            var planningWarnings = new List<string>();

            List<(string From, string To)> moves = await Task.Run(
                () => PlanChainInnerPackageMoves(
                    rootTask,
                    chainTasks,
                    restDirectory,
                    outputRoot,
                    ContentKeepRules.FromSettings(_host.Settings),
                    out planningWarnings),
                cancellationToken);

            foreach (string warning in planningWarnings)
            {
                _host.AppendLog("WARN", warning);
            }

            /*
             * ⛔ 2026-10-15：「留着没跑的内层包 ⇒ 如实点名」那一段**搬走了**（见
             * <see cref="ReportLeftBehindInnerPackages"/>）—— 它必须**无条件**跑，而本方法是"链尾要不要搬"
             * 那一个判据下的空跑段（普通档恒不执行）⇒ 长在这里就会被静默掉
             * （实测：`ChainRestSweepTests` 三条守门因此变红）。
             */
            if (moves.Count == 0)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _host.AppendLog("WARN", $"{rootTask.LogName}：链尾收内层包之前被取消，内层包留在原地（其余物不生成）。");
                return;
            }

            var failures = new List<string>();

            // 这一趟真正落下去的目标名（让位时也要看它，见下）。
            var movedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach ((string from, string to) in moves)
            {
                try
                {
                    /*
                     * ⛔ 计划是几秒前算出来的：目标名可能在这期间被占掉（并发收同一份其余物 / 用户自己动过文件）。
                     * 搬之前**再看一眼**，占了就现场再让一次位 —— ⛔ 仍然绝不覆盖。
                     *
                     * ⚠ 这是兜底，不是根因：同一份计划里两个同名内层包互相撞名，已经在
                     * `PlanChainInnerPackageMoves` 里用 `reserved` 解决（真机 2026-10-01 那批就栽在那一处）。
                     *
                     * ⚠ 2026-10-12：这一段（让位 + 同卷改名/跨卷复制 + 绝不覆盖）**收成一个出口**
                     * <see cref="TryMoveIntoRest"/> —— 「其余物 = 不处理」档那一支（`ReclaimLayerFilesToRest`）
                     * 也要用同一套动作，⛔ 不许各写一份。
                     */
                    if (!TryMoveIntoRest(from, to, movedTargets, out string target, out string? moveFailure))
                    {
                        failures.Add($"{Path.GetFileName(from)}（{moveFailure}）");
                        _host.AppendLog("WARN", $"{rootTask.LogName}：内层包没能移入其余物：{from} —— {moveFailure}（它留在原地）");
                        continue;
                    }

                    /*
                     * 任务对象跟着改位置：续解扫描靠它把"刚搬走的内层包"排除掉。
                     *
                     * ⚠ 2026-10-02 补齐：这里原来**只改 CurrentPath**，而任务上指向同一个文件的地方有两处
                     * （另一处是分卷清单 `VolumePaths`）—— 只改一条就会在账上留下"盘上已不存在"的旧名字，
                     * 下一次按分卷组搬源包时 `SourcePackageMover.ResolveSourceGroup` 判"清单非空、却不含自己"
                     * ⇒ **整组一份都不搬**（用户 2026-10-02 收口后的保守档），源包留在原地还要解释一遍。
                     * 现在与"源包搬进其余物"（ApplySourceMoveResult）、"整组改名"（SyncTasksAfterVolumeRename）
                     * 走同一个出口：`TaskPathSync.ApplyMove`。
                     */
                    TaskPathSync.ApplyMoves(
                        chainTasks,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [from] = target });

                    /*
                     * 一行说完，**不写两条完整绝对路径**（用户 2026-09-26 第 45 条：
                     * 真机上这一行是 200 字符上下 × 38 个包，而两条路径对用户没有信息量 ——
                     * 他关心的是"哪个内层包进了其余物"，不是它待在 H:\ 的哪一层）。
                     */
                    _host.AppendLog(
                        "INFO",
                        $"{rootTask.LogName}：内层包已移入其余物 —— {Path.GetFileName(from)}"
                        + $"（{Path.GetFileName(Path.GetDirectoryName(target)) ?? string.Empty} 那一层）");
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(from)}（{ex.Message}）");
                    _host.AppendLog("WARN", $"{rootTask.LogName}：内层包没能移入其余物：{from} —— {ex.Message}（它留在原地）");
                }
            }

            if (moves.Count > failures.Count)
            {
                // 只有真的搬进去了才把其余物记到根任务上（否则删除档会去删一个不存在的目录）。
                rootTask.RestDirectoryPath = restDirectory;
            }

            /*
             * ===== **链内产出的片**也要集中进其余物（用户 2026-10-12 计划的第三个动作）=====
             *
             * 上面那一段只认**任务**（`candidate.IsContinuationTask`），而链内解出来的那一族片
             * **不是任务** ⇒ 「其余物 = 不处理」档下它们留在落地树里（基线 3 轮 ③✘：`run-4444` /
             * `run-11111111` / `run-19191919`），下一遍再导入就会被当成新原包。
             *
             * 名单来自**递归钩子每层报的** `GroupFiles`（`_host.ChainInnerLayerInputs`，只记账不动盘）。
             * ⛔ 搬的时机**只能在这里**（链尾、定稿之后）：实测（`run-4444`）挂在"逐层"那一刻去搬
             * 会让后续那一层的产物跟踪按路径找不到原件 ⇒ 内容物被重复解出来（`444(1)/(2).mp4`）。
             * ⛔ 只搬"确实还在盘上、且落在根任务输出范围之内"的；⛔ 判不出/搬不动 ⇒ 写一行、不动。
             */
            CollectChainInnerLayerInputsIntoRest(rootTask, restDirectory, outputRoot);
        }

        /// <summary>
        /// **把链内"用过的那几个文件"集中进其余物**（链尾那一刻；名单由递归钩子逐层记账）。
        ///
        /// <para>只搬三条件同时成立的那些：① 还在盘上；② 落在根任务的**输出范围之内**（⛔ 不碰别处）；
        /// ③ 不在其余物目录里（幂等）。执行体 = <see cref="TryMoveIntoRest"/>（与旁边那一段同一套让位/复制语义）。</para>
        /// </summary>
        internal void CollectChainInnerLayerInputsIntoRest(ArchiveTask rootTask, string restDirectory, string outputRoot)
        {
            if (rootTask == null || _host.ChainInnerLayerInputs.Count == 0 || string.IsNullOrWhiteSpace(restDirectory))
            {
                return;
            }

            var movedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int moved = 0;
            var failures = new List<string>();

            foreach (string from in _host.ChainInnerLayerInputs.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                if (!File.Exists(from))
                {
                    continue;
                }

                if (outputRoot.Length > 0
                    && !ArchivePathGuard.IsInsideRoot(outputRoot, from, out _))
                {
                    // 不在根任务的输出范围里 ⇒ 不是"这一条链的产物" ⇒ 一个字节都不动。
                    continue;
                }

                if (ProcessArtifactLayout.IsInsideArtifactDirectory(from, Path.GetDirectoryName(restDirectory) ?? string.Empty))
                {
                    continue;
                }

                if (TryMoveIntoRest(from, Path.Combine(restDirectory, Path.GetFileName(from)), movedTargets, out _, out string? moveFailure))
                {
                    moved++;
                    continue;
                }

                failures.Add($"{Path.GetFileName(from)}（{moveFailure}）");
            }

            if (moved > 0)
            {
                rootTask.RestDirectoryPath = restDirectory;

                _host.AppendLog(
                    "INFO",
                    $"{rootTask.LogName}：链内那一族的片已集中进其余物 {moved} 个 —— {ProcessArtifactLayout.ArtifactDirectoryName}"
                    + (failures.Count > 0 ? $"（{failures.Count} 个没搬成，仍在原处）" : string.Empty));
            }
        }

        /// <summary>
        /// **如实点名"成品目录里还留着的内层包"**（唯一出口；链尾那一站**无条件**调它）。
        ///
        /// <para><b>为什么不能当过程物清掉</b>（用户 2026-09-28 真机：`amb909\amb909\amb.7z.001..004`
        /// 一直留在成品目录里，他以为是"忘了搬进其余物"）：过程物（内层包）能清的前提是"它的内容物已经
        /// 解出来了" —— 这一单跑成功、内层包被判**已完成**才成立。而"这一轮根本没跑"的内层包，内容还在它
        /// 里面，清掉（其余物在删除档下是**彻底删除**）等于把唯一一份内容删了 ⇒ 只做一件事：**如实点名 +
        /// 告诉他怎么处理**（判据与搬运用同一套：分卷组取 `VolumePaths` 全卷，单文件取自己；只认在本单输出目录里的）。</para>
        ///
        /// <para>⚠ 2026-10-15：这一段原来长在 <see cref="CollectChainInnerPackagesIntoRestAsync"/> 里，
        /// 跟着"链尾要不要搬"那一个判据（`!_host.LayerReclaimThisBatch`）一起被跳过；而逐层回收现在**恒开**
        /// ⇒ 那一整段在普通档下永不执行 ⇒ 这句点名被**静默**掉（实测 `ChainRestSweepTests` 三条守门变红）。
        /// 判据与动作于是拆开：**诊断恒做**，搬照旧只在该搬的时候搬。</para>
        /// </summary>
        internal void ReportLeftBehindInnerPackages(ArchiveTask rootTask, IReadOnlyList<ArchiveTask>? chainTasks)
        {
            if (rootTask == null || chainTasks == null || chainTasks.Count == 0)
            {
                return;
            }

            string outputRoot = string.IsNullOrWhiteSpace(rootTask.OutputPath)
                ? rootTask.ContentDirectoryPath
                : rootTask.OutputPath;

            try
            {
                var leftBehind = new List<string>();

                /*
                 * ⛔ 判据与搬运那一边**同一套**（`PlanChainInnerPackageMoves`）：只有"自己这一单成功且校验通过"
                 * 的内层包才会被收进其余物 —— 在这里再点名它们就是自相矛盾的假警报。
                 *
                 * ⚠ 2026-10-04 起"根任务成功 ⇒ 链上内层包都会被收走"这半句**不再成立**：
                 * 未解开过的内层包一条都不搬（用户当场推翻旧口径，见 PlanChainInnerPackageMoves）。
                 * 两边必须同时改（§9.5：同一件事只能有一个判据），否则又会出现
                 * "警告说还留着、下一行就搬走删掉"那种自相矛盾。
                 *
                 * 真机 2026-10-01：这一行写着「成品目录里还留着 2 个内层包没被清理（它们的内容还没解出来，
                 * 清掉就等于删内容）」，紧接着两行就把那两个搬进其余物并彻底删掉了 ——
                 * 用户读到的是"程序自己跟自己打架"，而真正没被清掉的那两个（撞名搬失败的）它反倒没说是为什么。
                 */
                foreach (ArchiveTask? candidate in chainTasks)
                {
                    if (candidate == null ||
                        ReferenceEquals(candidate, rootTask) ||
                        !candidate.IsContinuationTask)
                    {
                        continue;
                    }

                    bool candidateSucceeded =
                        candidate.Outcome == TaskOutcome.Succeeded &&
                        candidate.OutputVerification == OutputVerificationOutcome.Passed;

                    // 会被收走的（**只有它自己成功那一种**）：不进"还留着"的名单。
                    if (candidateSucceeded)
                    {
                        continue;
                    }

                    IEnumerable<string> candidatePaths = candidate.IsVolumeGroup && candidate.VolumePaths.Count > 0
                        ? candidate.VolumePaths
                        : new[] { candidate.CurrentPath };

                    foreach (string candidatePath in candidatePaths)
                    {
                        if (string.IsNullOrWhiteSpace(candidatePath) || !File.Exists(candidatePath))
                        {
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(outputRoot) &&
                            ArchivePathGuard.IsInsideRoot(outputRoot, candidatePath, out _))
                        {
                            leftBehind.Add(Path.GetFileName(candidatePath));
                        }
                    }
                }

                if (leftBehind.Count > 0)
                {
                    _host.AppendLog(
                        "WARN",
                        $"{rootTask.LogName}：成品目录里还留着 {leftBehind.Count} 个内层包没被清理（它们的内容还没解出来，"
                        + $"清掉就等于删内容）：{string.Join("、", leftBehind.Take(5))}"
                        + $"{(leftBehind.Count > 5 ? " 等" : string.Empty)}"
                        + " —— 要解就把它们加进任务列表；删除档只清「内容物已经解出来」的过程物。");
                }
            }
            catch (Exception ex)
            {
                _host.AppendLog("WARN", $"{rootTask.LogName}：清点留在成品目录里的内层包失败（不影响这一单）：{ex.Message}");
            }
        }

        /// <summary>
        /// 一条续解链里"该收进其余物的内层包"的搬运计划（**纯计划：一个字节都不动**）。
        ///
        /// <para>抽成静态方法是为了能被用例直接钉住（真机 2026-10-01 那一类缺陷全在"名字怎么算"上，
        /// 而算名字这一步以前埋在 <c>Task.Run</c> 的匿名体里，谁都测不到）。</para>
        ///
        /// <para><b>⛔ 撞名让位必须把"本计划里前面那几条"也算进去</b>（<c>reserved</c>）：
        /// 只问文件系统的话，**两条同名内层包会算出同一个目标** —— 第一条搬成功、第二条撞上
        /// <c>already exists</c> 而**留在原地**。用户 2026-10-01 真机 BBBB / CCCC 两批的现场就是它：
        /// <c>…\111\111\111\111.part1.rar</c> 与 <c>…\222\222\222\222.zip</c> 卡在内容物里没被收走，
        /// 日志写着「内层包没能移入其余物：… The file '…\其余物\111.part1(1).rar' already exists.（它留在原地）」。
        /// 口径与源包搬运（<c>SourcePackageMover.Plan</c>）**逐字相同**：reserved + <c>MakeUniqueTarget</c>。</para>
        /// </summary>
        /// <param name="rootTask">这一条链的根任务（其余物挂在它身上）。</param>
        /// <param name="chainTasks">整条链的任务（含根任务自己，会被跳过）。</param>
        /// <param name="restDirectory">其余物目录（定稿时算好的那一个，⛔ 不现场重算）。</param>
        /// <param name="outputRoot">根任务的输出根（只搬这个范围里的内层包）。</param>
        /// <param name="keepRules">
        /// 「内容物保留关键词」判据（用户 2026-10-04 的新功能；唯一出口 <see cref="ContentKeepRules"/>）。
        /// 命中它的内层包**不搬进其余物**（"碰都不碰"），整组一起留；空判据 ⇒ 一个都不拦（老行为）。
        /// 传 <c>null</c> 等价于 <see cref="ContentKeepRules.Empty"/>。
        /// </param>
        internal static List<(string From, string To)> PlanChainInnerPackageMoves(
            ArchiveTask? rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks,
            string? restDirectory,
            string? outputRoot,
            ContentKeepRules? keepRules,
            out List<string> warnings)
        {
            var planned = new List<(string, string)>();
            warnings = new List<string>();

            ContentKeepRules rules = keepRules ?? ContentKeepRules.Empty;

            if (rootTask == null ||
                chainTasks == null ||
                chainTasks.Count == 0 ||
                string.IsNullOrWhiteSpace(restDirectory))
            {
                return planned;
            }

            string artifactRoot = restDirectory!;

            // 本计划**已经排出去的名字**（见方法注释：不问它就必然撞名）。
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /*
             * ⛔ 谁的"过程物"要收：**只有真的被解开过的**续解任务（内容已经落盘）。
             *
             * <para>用户 2026-10-04 当场推翻 2026-09-28 的旧口径 —— 原话：
             * 「链上那个从未解开过的内层包（它躺在其余物里）—— **不是没解开你为什么要放在其余物里面，啊**」。
             * 老写法是 `if (!candidateSucceeded && !rootSucceeded) continue;`：根任务一成功，
             * 链上**所有**续解任务（失败的 / 这一轮没跑的 / 跳过的）都被当过程物收走，
             * 而链尾「其余物按设置处理」那一档在「彻底删除」下是**永久删除**
             * ⇒ 还没解出来的内容连包一起没了（不可逆）。
             *
             * 新口径（与"内层包按有没有被解开过分类"同一句话）：
             * · **解开过的**（自己这一单成功 + 输出校验通过 ⇒ 内容已落盘）= 过程物 ⇒ 收进其余物、照旧按档处理；
             * · **从未解开过的**（停在半路留下的分支 / 命中「内容物保留关键词」的 / 到上限没展开的 /
             *   失败与没跑成的）= **还没处理的内容** ⇒ 原样留在成品目录里，⛔ 一条都不进其余物。
             *
             * 判据**不新造**：就是本方法一直在用的那条既有事实（`Succeeded` + `Passed`），
             * 以及上面「内容物保留关键词」那一档；⛔ 绝不按"名字像不像归档"去猜。
             * ⚠ 与 `CollectChainInnerPackagesIntoRestAsync` 里那份"还留着"名单**同一套判据**（§9.5）。
             */
            foreach (ArchiveTask? candidate in chainTasks)
            {
                if (candidate == null ||
                    ReferenceEquals(candidate, rootTask) ||
                    !candidate.IsContinuationTask)
                {
                    continue;
                }

                bool candidateSucceeded =
                    candidate.Outcome == TaskOutcome.Succeeded &&
                    candidate.OutputVerification == OutputVerificationOutcome.Passed;

                if (!candidateSucceeded)
                {
                    continue;
                }

                string path = candidate.CurrentPath;

                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    continue;
                }

                /*
                 * ⛔ 分卷组必须**整组一起搬**（2026-09-28 真机：内层包是 4 卷分卷组时只搬走了
                 * `amb.7z.001` 并把它彻底删掉，`.002/.003/.004` 留在成品目录里 —— 用户原话
                 * "同样犯了 winrar 会犯的问题：只删除 .001 为首的分卷头文件，其他分卷还残留着"）。
                 * 搬一半等于把一套完整的包拆成废件，比不搬糟得多。
                 *
                 * 判据与"源包整组一起移"**同一套**：分卷组取 VolumePaths 全卷，单文件取它自己。
                 */
                var groupPaths = new List<string>();

                if (candidate.IsVolumeGroup && candidate.VolumePaths.Count > 0)
                {
                    foreach (string volumePath in candidate.VolumePaths)
                    {
                        if (!string.IsNullOrWhiteSpace(volumePath) && File.Exists(volumePath))
                        {
                            groupPaths.Add(volumePath);
                        }
                    }
                }

                if (groupPaths.Count == 0)
                {
                    groupPaths.Add(path);
                }

                /*
                 * 兜底（**宁可不动，也不搬一半**）：分组信息没拿到（IsVolumeGroup=false），
                 * 但名字明显是分卷、同目录里还躺着同组的别的卷 —— 这一卷不搬，写清为什么。
                 * 留下的是一套完整的包，用户还能自己接着处理。
                 */
                if (!candidate.IsVolumeGroup && VolumeSiblingProbe.HasSiblingVolumeBeside(path))
                {
                    // 日志由调用方写（纯计划里不写盘、不记日志 —— 这样它才测得动）。
                    warnings.Add(
                        $"{rootTask.LogName}：{Path.GetFileName(path)} 看着是分卷组的一卷，"
                        + "但没拿到整组清单 —— 这次不搬它（搬一半会把一套包拆成废件）。");
                    continue;
                }

                /*
                 * ===== 「内容物保留关键词」：命中就**整组都不搬**（用户 2026-10-04 的新功能）=====
                 *
                 * 用户原话：「只要文件名里面包含着这个字符就不能动……这些压缩文件碰都不要碰」。
                 * 链尾把内层包收进其余物，是为了让「删除操作」那一档把它们清掉；命中关键词的既然
                 * "碰都不碰"，那就**一步都不动**：不搬、不改名、后面的删除也删不到它（它不在其余物里）。
                 *
                 * ⛔ 判据唯一出口 <see cref="ContentKeepRules"/>（包含即命中 / 大小写不敏感 / 不做通配正则）。
                 * ⛔ 分卷组**整组一起判**：一卷命中就整组留着，绝不只留一半。
                 * ⛔ 空关键词列表 ⇒ 这里一个都不拦（行为与以前逐字相同）。
                 */
                string? keepKeyword = groupPaths
                    .Select(groupPath => rules.FindMatch(groupPath))
                    .FirstOrDefault(keyword => keyword != null);

                if (keepKeyword != null)
                {
                    warnings.Add(
                        $"{rootTask.LogName}：{Path.GetFileName(path)} 命中「内容物保留关键词」（{keepKeyword}）"
                        + " —— 按设置碰都不碰，这次不搬进其余物（它留在原处，也不参与后面的删除）。");
                    continue;
                }

                foreach (string groupPath in groupPaths)
                {
                    // 已经在其余物里了（重复调用 / 上一轮搬过）：跳过。
                    if (SafePathHelper.GetFullPathSafe(Path.GetDirectoryName(groupPath) ?? string.Empty)
                            .EndsWith(ProcessArtifactLayout.ArtifactDirectoryName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(outputRoot) &&
                        !ArchivePathGuard.IsInsideRoot(outputRoot, groupPath, out _))
                    {
                        // 不在根任务的输出范围内（理论上不该发生）：不动它。
                        continue;
                    }

                    /*
                     * 撞名（同一条链里两个同名内层包）自动让位，绝不覆盖 ——
                     * **本计划里已经排出去的名字也算撞名**（reserved，见方法注释）。
                     */
                    string target = Path.Combine(
                        artifactRoot,
                        FileNameHelper.SanitizeFileName(Path.GetFileName(groupPath)));

                    if (reserved.Contains(target) || File.Exists(target) || Directory.Exists(target))
                    {
                        target = ProcessArtifactLayout.MakeUniqueTarget(
                            target,
                            isDirectory: false,
                            reserved,
                            FileSystemArtifactTargetProbe.Instance);
                    }

                    reserved.Add(target);
                    planned.Add((groupPath, target));
                }
            }

            return planned;
        }

        /// <summary>
        /// 把"这段活干了多快"说成人话（"21.0 MB/s"）——直读那一条日志用它（用户 2026-09-25 第 38 条）。
        /// 速度按**写出字节 ÷ 用时**算：直读的真实工作量就是把这些字节从源读到成品。
        /// </summary>
        internal static string DescribeThroughput(long writtenBytes, TimeSpan elapsed)
        {
            if (writtenBytes <= 0 || elapsed.TotalSeconds <= 0.05)
            {
                return "速度算不出来（太小或太快）";
            }

            double mibPerSecond = writtenBytes / 1024d / 1024d / elapsed.TotalSeconds;

            return $"{mibPerSecond:0.0} MB/s";
        }

        /// <summary>
        /// 「一组分卷的第一卷名字被改坏」时，把**我们自己产出的**那一卷改回标准命名（用户 2026-09-25 第 37 条）。
        ///
        /// <para>真机现场：他那个 12.22 GiB 的容器解出来的是三卷 7z，第一卷
        /// <c>Code Complete-BZ.7z(删掉.001</c>（打包者塞了字、吃掉右括号），后两卷名字正常。
        /// 7-Zip 按名字找不到后续卷 → 把它当"通用分片" → 解出来是一个等大的垃圾文件；
        /// 于是那 12 GiB 的内容永远出不来（用户看到的是"20 G 的素材只出来 8 G"）。</para>
        ///
        /// <para>⛔ 只对**续解任务**（文件是我们自己从上一层解出来的过程物）改名：
        /// 那是把我们的产物摆正，不是动用户的源文件（不变量 1）。
        /// 用户自己添加的坏名字分卷走不了这一条 —— 会由后面的「通用分片」闸门如实报「分卷缺失」并给出改名建议。</para>
        ///
        /// <para>改完之后**重拍源文件快照**（不变量 11）：名字换了、路径换了，旧快照对不上会让这一单
        /// 被"源文件已变化"拦下（与"分卷补齐"那条路同一处理）。</para>
        /// </summary>
        internal string TryRepairBrokenVolumeChainName(ArchiveTask task, string engineArchivePath)
        {
            if (task == null || string.IsNullOrWhiteSpace(task.CurrentPath))
            {
                return engineArchivePath;
            }

            // 只有"从别的包里解出来的"才允许改名（见方法注释）。
            if (!task.IsContinuationTask)
            {
                return engineArchivePath;
            }

            IReadOnlyList<string?> names = TryListFileNames(Path.GetDirectoryName(task.CurrentPath));

            if (names.Count == 0)
            {
                return engineArchivePath;
            }

            BrokenVolumeChainRepair.RenamePlan? plan = BrokenVolumeChainRepair.TryPlan(task.CurrentPath, names);

            // 名字本来就标准（零条改名）= 什么都不用做，也**不许**记那句"被改坏"的日志。
            if (plan == null || BrokenVolumeChainRepair.IsNoOp(plan))
            {
                return engineArchivePath;
            }

            if (!BrokenVolumeChainRepair.TryApply(plan, out string failure))
            {
                _host.AppendLog(
                    "WARN",
                    $"{task.LogName}：这一卷的名字看着被改坏了（{plan.Describe()}），但改名没成功：{failure}"
                    + " —— 保持原样继续，下面会如实报结论。");

                return engineArchivePath;
            }

            _host.AppendLog(
                "INFO",
                $"{task.LogName}：分卷名字被改坏，已在自己的产物里摆正 —— {plan.Describe()}"
                + $"（源包一个字节都没动；改的是上一层解出来的过程物）。原位置：{task.CurrentPath}");

            string previousPath = task.CurrentPath;
            task.CurrentPath = string.IsNullOrWhiteSpace(plan.FirstVolumePathAfterRename)
                ? previousPath
                : plan.FirstVolumePathAfterRename;

            // 路径换了就必须重拍快照（不变量 11：按"位"比对，旧路径会立刻判成"文件不见了"）。
            task.CaptureSourceSnapshot();

            return task.CurrentPath;
        }

        /// <summary>列一个目录里的**文件名**（读不了就返回空集合，调用方按"没有线索"处理）。</summary>
        internal static IReadOnlyList<string?> TryListFileNames(string? directory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return Array.Empty<string?>();
                }

                return Directory.EnumerateFiles(directory!, "*", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string?>();
            }
        }

        /// <summary>
        /// 链尾对**单个**成功任务补做「删除操作」那一档（一键处理专用；幂等：已处理过的直接跳过）。
        /// </summary>
        /// <param name="chainTasks">
        /// 整条链的全部任务（根 + 续解子任务）。**调用方照旧按位置传**（同一批的链信息在别处还要用），
        /// 但本档 2026-10-04 起**不再读它** —— 判据只问"这一单自己的根源包那一层"，
        /// 不再要求链上每个内层包都成功（用户原话：「你不会读设置吗，我勾选了保留吗，没勾选你留着干什么」，
        /// 见下面那一段长注释）。
        /// </param>
        /// <param name="cancellationToken">取消令牌（取消那一档由执行体 <c>RestItemPurger</c> 第 3 道门槛判）。</param>
        internal async Task ApplyRestHandlingAfterChainAsync(
            ArchiveTask task,
            IReadOnlyList<ArchiveTask>? chainTasks,
            CancellationToken cancellationToken)
        {
            _ = chainTasks;
            _ = cancellationToken;

            string mode = RestHandlingModes.Normalize(RestHandlingThisBatch);

            if (string.Equals(mode, RestHandlingModes.Keep, StringComparison.OrdinalIgnoreCase))
            {
                /*
                 * ⚠ 2026-10-13（困难批：6 份样本 ①✔ 而只差判据 ③，日志 + 运行期定案）：
                 * 「其余物 = 不动」这一档过去在这里**整档早退** ⇒ 「程序自己产出、已被整组接走」的那份副本
                 * （接片建的链接 / 产出方落点里那一份）**永远没人认领** ⇒ 判据 ③「盘上无残留」红。
                 *
                 * <para><b>用户口径（2026-10-13 拍板选 A + 当天再次点名）</b>：「不动其余物」的"不动"
                 * = **不删用户的东西**；而**程序自己产出、且已被整组接走**的那份多余副本**允许挪位置**
                 * （搬进同层的 `其余物\`，⛔ 一个字节都不删）；原话还有一句
                 * 「连"程序自己产的、已经没用的那份副本" 这个不就是**过程物**吗」。</para>
                 *
                 * <para>⇒ 这一档只补调**"产出方那一份片"那一个出口**（它自己按档分派：Keep ⇒ 搬进同层其余物、
                 * 一个字节都不删，见 <see cref="_host.PurgeGroupLandedPieces"/>）。⛔ **用户源片那一半照旧一步都不动**
                 * —— 下面那两段（`SettleOneConsumedPieceTask` 的搬运与"借来用过的用户源片"账）**一个都不调**，
                 * 所以"不动其余物"对用户自己的东西仍然是一个字节都不碰。</para>
                 */
                if (task.Outcome == TaskOutcome.Succeeded)
                {
                    /*
                     * ⛔ 组基名**不许拿这一单自己的包基名猜**（断点实测 `1111`：链尾那一单是 `111-W0-0.zip`，
                     * 它自己的基名与那一组 `111-L2` 毫无关系 ⇒ 拿它去问等于什么都没问），
                     * ⛔ 也**不许拿 `_adoptedLandedPieces` 当来源**（同一次断点实测：那一本账 **0 条** ——
                     * 这两片是"引擎报缺卷 ⇒ 发布"那条路留下了，根本没走"接片"那一档）。
                     * ⇒ 只认既有那本"**谁吐出过哪一组的片**"的账（<see cref="_host.GroupPieceProducers"/>，
                     * 写点 = `PublishUnresolvedVolumePieces` → `RememberGroupPieceProducer`），
                     * 问法 = **这一单在哪些组里当过我产出的那一份**，逐组按档处理它自己那一份。
                     */
                    foreach (string groupBaseName in _host.SnapshotGroupPieceProducerBaseNames())
                    {
                        if (_host.TrySnapshotGroupPieceProducers(
                                groupBaseName,
                                out List<(ArchiveTask Task, string PieceName)> producers)
                            && producers.Any(item => item.Task != null && ReferenceEquals(item.Task, task)))
                        {
                            _host.PurgeGroupLandedPieces(task, groupBaseName);
                        }
                    }

                    /*
                     * ⚠ 2026-10-13（困难批 `1111` 判据 ③，插桩实测那一轮定案）：**产出方自己那一档**。
                     *
                     * <para>实测事实（`[排障·Keep档]` / `[排障·收片]` 两组行，夹具 `dbg-b6a4ee4a`）：
                     * 这一批里走进本档的只有 `recipe.json`(Skipped)、`111-W2-0.rar`、`111-W1-0.zip`
                     * 三单，而它们**每一单都拿自己的包基名**去问（`111-W2-0` / `111-W1-0`）——
                     * 那一组真正叫 `111-L2` ⇒ `targets.Count` 恒 0 ⇒ 「产出方那一份已经落地的片」没人认领。</para>
                     *
                     * <para>为什么会这样：那一组是被**批末补判**交给"续解出来的那一片"那一单去解的
                     * （入口那一单 `111-W0-0.zip` 的源包处理档是"链尾补搬"⇒ 走的是 `DeferredToChainEnd`
                     * 那一支，**根本走不到本档**），所以"谁在解"那本账里记的是**别人**；
                     * 而**我亲手从自己这棵树里链接走了一片**这件事只有这本账知道
                     * （<see cref="_host.ByteBudgetLinkedSources"/>，写点 = <see cref="TryLinkMissingPiecesByByteBudget"/>）
                     * ⇒ 就按它问：这一单是不是**给这一组供过片**。</para>
                     *
                     * <para>⛔ 判据没放宽：下面那一档的每一条闸门（终态成功 / 可证完整 / 在盘上 / 在落点根内 /
                     * 不是任何一单的源 / 名字仍属于这一组）一条都不绕过；⛔ 用户自己的源片一个字节都不碰。</para>
                     */
                    foreach (string groupBaseName in _host.ByteBudgetLinkedSources
                                 .Where(item => ReferenceEquals(item.Producer, task))
                                 .Select(item => item.GroupBaseName)
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        _host.PurgeGroupLandedPieces(task, groupBaseName);
                    }

                    // 我亲手链接过来源的那几组 —— 只处理**这一单正是那一组的接手方**的那些
                    // （`_host.GroupConsumerByName` 是既有账：批末那一组齐了、交给哪一单去解时写的）。
                    foreach (string groupBaseName in _host.ByteBudgetLinkedSources
                                 .Select(item => item.GroupBaseName)
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (_host.GroupConsumerByName.TryGetValue(groupBaseName, out string? consumerName)
                            && string.Equals(consumerName, task.FileName, StringComparison.Ordinal))
                        {
                            _host.PurgeGroupLandedPieces(task, groupBaseName);
                        }
                    }

                    // 另外：这一单自己就是"入口包那一单"时（它 CurrentPath 已是第 1 卷）照旧问一次它自己的基名。
                    string ownBaseName = FileNameHelper.GetArchiveBaseName(task.CurrentPath ?? string.Empty);

                    if (ownBaseName.Length > 0)
                    {
                        _host.PurgeGroupLandedPieces(task, ownBaseName);
                    }
                }

                return;
            }

            /*
             * ===== 判据逐条查、**每一条拦下都要说清是哪一条**（用户 2026-09-30 真机报的 bug）=====
             *
             * 现场：用户设了"源包放入其余物 + 其余物彻底删除"，链跑完（日志"没有发现可继续解压的内层包"），
             * 然后**什么都没有**：源包原地不动、其余物没生成、日志里一个字都没说为什么 ——
             * 老写法在下面那一支直接 `return`（一句注释"什么都不做"），用户只能猜。
             *
             * 现在：判据一条一条查出来，拦下时写一行 WARN，点名**是哪一条不成立**；
             * 其中 L4 那一档由唯一出口 <see cref="ResultCompletenessClassifier"/> 给出
             * 「哪一层 + 什么原因」（`verdict.Blocker`）。
             */
            if (task.Outcome != TaskOutcome.Succeeded)
            {
                AppendRemovalBlocked(
                    task,
                    $"{StatusText.ChainRestBlockedTaskOutcomeFormat}（机器终态：{task.Outcome}）");

                return;
            }

            /*
             * ⚠ 2026-09-30（检验等级 L4）：第二条判据从"校验通过"**收紧**成"可证完整"——
             * 唯一出口 <see cref="ResultCompletenessClassifier"/>。老口径把"拿不到清单、
             * 只做了非空底线校验"也算成通过，这一档**没有任何证据**却能走到链尾删其余物。
             * （下游 <see cref="RestItemPurger"/> 还会再挡一次，但**同一件事只许有一个判据**：
             * 这里留一个更弱的版本，等于给下一个人两条互相矛盾的口径。）
             */
            ResultCompletenessVerdict completeness = ResultCompletenessClassifier.Classify(task);

            if (!completeness.AllowsSourceRemoval)
            {
                // 红线：判不出 / 可证不完整 ⇒ 什么都不做，但**必须写清是哪一条判据拦下的**。
                AppendRemovalBlocked(task, completeness.Blocker);

                return;
            }

            if (string.IsNullOrWhiteSpace(task.RestDirectoryPath))
            {
                AppendRemovalBlocked(task, StatusText.ChainRestBlockedNoRestDirectoryFormat);

                return;
            }

            if (!Directory.Exists(task.RestDirectoryPath))
            {
                AppendRemovalBlocked(
                    task,
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.ChainRestBlockedRestMissingFormat,
                        task.RestDirectoryPath));

                return;
            }

            /*
             * ===== 借来用过的用户源片**也算源包**：跟这一组一起进其余物、一起按删除档处理 =====
             * （用户 2026-10-05 拍板原话：「那肯定啊，这个算原包，最后放到其余物里面一同删除」）
             *
             * 现场：一组跨盘 ZIP 的末片压在包里、其余三片散在三个源目录里 —— 递归层把三片硬链接过来
             * 凑齐、整组解开、结果发布成功。那三片就是**这一组的源包**，不能因为"它们自己那一单是跟班卷"
             * 就永远留在用户目录里。
             *
             * 位置刻意排在上面那几道判据**之后**（消费方成功 + 可证完整 + 其余物目录在）：判据一条都不放宽；
             * 搬运本体仍是既有出口 `ExecuteSourcePackageMove`（整组一起移、绝不覆盖、跨盘先复制再删原件、
             * 单个失败不影响其余），⛔ 不另写一套搬运动作。
             */
            _host.CollectConsumedSourcePiecesIntoRest(task);

            /*
             * ===== 「整条链都得成功」那道门槛 2026-10-04 **被用户推翻**（原第 37 条）=====
             *
             * 老口径（2026-09-25 第 37 条那次 12.22 GiB 事故之后加的）：链上**每一个**续解任务都成功
             * 且可证完整，链尾才按「删除操作」处理其余物 —— 理由是"链没跑完时过程物是唯一的产物线索"。
             *
             * 用户 2026-10-04 真机（日志 271/272）否掉了它，原话：
             * 「你不会读设置吗，我勾选了保留吗，没勾选你留着干什么」——
             * 他选了「其余物 = 彻底删除」，只因为**内层包**失败（根源包自己那一层是成功的、
             * 输出校验也过了）就整份留着，在他眼里就是"设置没生效"。
             *
             * 现在的口径：**判据只问"这一单自己的根源包那一层"** ——
             * ① 根源包成功（<see cref="TaskOutcome.Succeeded"/>，上面已查）；
             * ② 输出校验通过且**完整性可证**（L4 唯一出口，上面已查）；
             * ③ 未取消（<c>RestItemPurger</c> 第 3 道门槛，执行体里查）；
             * ④ 其余物目录是定稿那一刻记下来的那一个（上面已查 + 执行体第 4/5 道门槛）。
             * ⛔ **不再要求链上每个内层包都成功**。
             *
             * 红线一个字没动（全部由下面这些闸门继续钉着，与哪一档无关）：
             * · 失败 / 部分完成 / 取消的**那一单** ⇒ 源包原地不动、其余物不生成（上面两条早退 + 执行体）；
             * · 其余物删除只有 <c>RestItemPurger</c> 一个执行体，六道门槛（含「半套分卷」
             *   <c>RestVolumeCompletenessGate</c>）照旧全过；
             * · 「空间不足」模式与其余物处理的口径不变；
             * · 判据只读事实（枚举终态 / 校验结果 / 取消令牌），⛔ 不比中文。
             *
             * 确实不满足上面四条而没处理其余物时，仍然**逐链点名**说清原因 + 路径
             * （<see cref="AppendRemovalBlocked"/> / <see cref="RecordRestKept"/>，三处消费）。
             */
            ScheduledTaskRuntime runtime = _host.GetOrCreateRuntime(task);

            if (runtime.PurgedBytes > 0 || runtime.RestPurged)
            {
                return;
            }

            await RunRestHandlingAsync(task, runtime, oneClickRun: false, force: true);
        }

        /// <summary>
        /// **其余物为什么原封不动地留着** —— 那一句话的唯一出口（用户 2026-10-04 真机）。
        ///
        /// <para>现场：日志 271/272 里写着「<c>Sociology.7z：链尾的其余物不处理（链上的「老王.apk」没有成功…）</c>」、
        /// 「<c>第6集.7z：链尾没有按「删除操作」处理其余物 / 源包 —— 任务没有成功…</c>」，
        /// 可①页、批末诊断、失败清单里**一个字都没有** —— 用户看到的是"解压成功、其余物还在"，
        /// 只能自己去猜。这一句把那条日志里的事实搬到他能看见的三个地方（同一份文字，⛔ 不各写一遍）。</para>
        /// </summary>
        internal static string DescribeRestKeptNote(ArchiveTask task, string reason)
        {
            if (task == null)
            {
                return string.Empty;
            }

            return string.IsNullOrWhiteSpace(task.RestDirectoryPath)
                ? string.Format(CultureInfo.CurrentCulture, StatusText.RestKeptNoteNoPathFormat, reason)
                : string.Format(CultureInfo.CurrentCulture, StatusText.RestKeptNoteFormat, task.RestDirectoryPath, reason);
        }

        /// <summary>
        /// 把"其余物没处理 + 为什么"记到任务上（①页「错误信息」列读 <c>ErrorMessage</c>，
        /// 批末诊断与失败清单读 <c>RestKeptReason</c> —— 三处同一份文字）。
        ///
        /// <para>⚠ 刻意**不改**任务的终态 / 状态：内容物是好的（这一单确实成功），
        /// 只是"其余物这一档没做" —— 改终态会连带改掉批末计数与四条删除裁决，那不是措辞的事。
        /// 幂等：同一句只写一次（链尾可能两条路都判到）。</para>
        /// </summary>
        internal static void RecordRestKept(ArchiveTask task, string reason)
        {
            if (task == null || string.IsNullOrWhiteSpace(reason))
            {
                return;
            }

            string note = DescribeRestKeptNote(task, reason);

            task.RestKeptReason = note;

            if (!task.ErrorMessage.Contains(note, StringComparison.Ordinal))
            {
                task.ErrorMessage = string.IsNullOrWhiteSpace(task.ErrorMessage)
                    ? note
                    : task.ErrorMessage + "；" + note;
            }
        }

        /// <summary>
        /// 链尾"按档处理其余物 / 源包"被某一条判据拦下时的**唯一**一行日志
        /// （用户 2026-09-30：源包被留下时必须说清是哪一条判据 —— ⛔ 不许静默 return）。
        ///
        /// <para>措辞与源包搬运动那几条 WARN 分开：这里说的是"链尾这一档没做"，
        /// 而 <paramref name="why"/> 里已经带了具体判据（含"哪一层 + 什么原因"）。</para>
        /// </summary>
        internal void AppendRemovalBlocked(ArchiveTask task, string why)
        {
            // 这条 WARN 就是"其余物为什么还在"的答案 ⇒ 同一句话也要落到①页与报告里（用户 2026-10-04 真机）。
            RecordRestKept(task, why);

            _host.AppendLog(
                "WARN",
                $"{task.LogName}：{StatusText.ChainRestBlockedPrefix} —— {why}；"
                + "一个字节都没动（失败 / 取消 / 判不出完整性一律不动）。");
        }

        /// <summary>
        /// L4（完整性分类）的**那一行证据** —— 全仓唯一一处写它。
        ///
        /// <para>为什么要写：这一条结论就是"敢不敢动用户源包"的唯一依据。
        /// 2026-09-30 的真机日志里，源包被留下了、可**为什么**只有一个笼统的"没有可用的归档清单可核对"，
        /// 用户根本不知道是哪一层、下一步该做什么。</para>
        ///
        /// <list type="bullet">
        /// <item><description><b>可证完整</b>：只在"这一次真的要按设置动源包"时才写（留在原地档没什么要交代的）。</description></item>
        /// <item><description><b>判不出 / 可证不完整</b>：**一律写**，而且写的是"为什么没删源包：判据 + 哪一层 + 原因"。</description></item>
        /// </list>
        /// </summary>
        internal void AppendCompletenessEvidence(
            ArchiveTask task,
            OutputVerificationResult verification,
            SourceHandlingMode sourceHandling)
        {
            ResultCompletenessVerdict verdict = ResultCompletenessClassifier.Classify(
                verification,
                task.ManifestExpectation);

            if (verdict.AllowsSourceRemoval)
            {
                if (sourceHandling == SourceHandlingMode.KeepInPlace)
                {
                    return;
                }

                _host.AppendLog(
                    "INFO",
                    $"{task.LogName}：{verdict.Message}（L3 核对依据：{DescribeManifestBasis(task.ManifestExpectation)}；"
                    + $"机器结论：{verdict.Evidence}）");

                return;
            }

            _host.AppendLog(
                "WARN",
                $"{task.LogName}：{verdict.Blocker}（机器结论：{verdict.Evidence}）");
        }

        /// <summary>L3 的预期清单是从哪一层来的（给人看的一句话；取不到时说清是哪一层、为什么）。</summary>
        internal static string DescribeManifestBasis(ManifestExpectation expectation)
        {
            return expectation.Source switch
            {
                ManifestExpectationSource.LeafLayer =>
                    $"{expectation.LayerLabel}（产出最终内容物的那一层）的清单 {expectation.Expected!.FileCount} 个文件 / "
                    + $"{expectation.Expected.TotalUncompressedSize} 字节",
                ManifestExpectationSource.OuterList =>
                    $"归档自己的清单 {expectation.Expected!.FileCount} 个文件 / {expectation.Expected.TotalUncompressedSize} 字节",
                ManifestExpectationSource.QueryArchive =>
                    $"引擎对这份归档现列出来的清单",
                _ => expectation.UnavailableReason
            };
        }

        /// <summary>
        /// 链结束后补搬的后台本体：判据 → 执行 → 结论。**只允许在后台线程上跑**。
        /// </summary>
        internal async Task<DeferredSourceMoveWork> RunDeferredSourceMoveWorkAsync(
            ArchiveTask rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks,
            SourceHandlingMode sourceHandling,
            CancellationToken cancellationToken)
        {
            var logEntries = new List<(string Level, string Message)>();

            // 幂等（硬要求）：已经搬成的一律跳过 —— 不靠"文件还在不在"猜。
            if (rootTask.SourcePackageMove == SourcePackageMoveState.Done)
            {
                return new DeferredSourceMoveWork(logEntries);
            }

            if (sourceHandling == SourceHandlingMode.KeepInPlace)
            {
                logEntries.Add((
                    "INFO",
                    $"{rootTask.LogName}：源包处理档是「留在原地」，链结束后的补做按该档不动源包。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            if (rootTask.IsContinuationTask)
            {
                // 只处理最外层源包：内层包的"源文件"是其余物里的过程物（与本轮直接搬同一口径）。
                return new DeferredSourceMoveWork(logEntries);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                logEntries.Add(("WARN", $"{rootTask.LogName}：链结束后的补搬被取消，源包留在原地（其余物不生成）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            /*
             * ===== 判据一律读**事实**，⛔ 不读状态字符串（用户 2026-09-24 明确要求）=====
             *
             * 老写法是 `OneClickCoordinator.IsSuccessStatus(rootTask)`，也就是拿
             * `task.Status == "解压成功"` 去决定"要不要动用户唯一无法再生的那份源包"。
             * 那个字符串**证明不了**产物是好的 —— 真机日志里恰好出现过"状态写着解压成功、校验却已判否"，
             * 万一那条路走到这儿，用户的源包就会被删掉。
             *
             * 现在读的是**校验那一刻的事实**，而且用的是 L4 的**唯一出口**
             * <see cref="ResultCompletenessClassifier"/>（= 可证完整）：它要求"产物非空 + 与引擎清单逐条对得上"
             * （`ManifestCrossChecked = true`）—— **拿不到清单那一档算"判不出"，同样不许动源包**
             * （2026-09-30 检验等级），加上"机器终态不是部分完成/失败/取消"（<see cref="TaskOutcome.Succeeded"/>）。
             * 这些字段都由管线在结论成立的那一刻写、之后没有任何地方会为了显示去改它们。
             */
            /*
             * ⚠ 2026-09-30（检验等级 L4）：从"校验通过"**收紧**成"可证完整"
             * （唯一出口 <see cref="ResultCompletenessClassifier"/>，与链尾其余物那两道门同一口径）。
             *
             * 漏掉这一处会怎样：链尾补搬是**移动用户唯一无法再生的源包**。老口径下"没拿到清单、
             * 只做了非空底线校验"也算校验通过 —— 链尾照样把源包搬进其余物，而接下来的
             * "删除操作"档一起来就把它删了。判不出就是判不出：一个字节都不许动。
             *
             * ⚠ 与下面 `DescribeChainVerificationGap` 那一处**判据相同、刻意查两遍**：根任务总会
             * 落进那个目录，所以严格说这一处是冗余的早退；留着的理由是它给出的是"根任务自己判不出"
             * 这句更准的话，而且它排在分卷 / 内容物那几道门之前（红检实测：把下面那一处退回旧判据，
             * 用例 `形状B_链上有一层判不出完整性_链尾不补搬源包` 变红；只退这一处则被下面那一处接住）。
             * ⛔ 别为了"去重"删掉任何一处 —— 它们挡的是同一个不可逆动作，而两处的调用场景并不完全相同。
             */
            ResultCompletenessVerdict rootCompleteness = ResultCompletenessClassifier.Classify(rootTask);

            if (!rootCompleteness.AllowsSourceRemoval)
            {
                logEntries.Add((
                    "WARN",
                    $"{rootTask.LogName}：{rootCompleteness.Blocker}" +
                    $"（机器结论：{rootCompleteness.Evidence}），链结束后不动源包（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            if (rootTask.Outcome != TaskOutcome.Succeeded)
            {
                logEntries.Add((
                    "WARN",
                    $"{rootTask.LogName}：根任务的机器终态不是「完成」（当前：{rootTask.Outcome}），" +
                    $"链结束后不动源包（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            if (rootTask.IsVolumeGroup && !rootTask.IsVolumeComplete)
            {
                logEntries.Add((
                    "WARN",
                    $"{rootTask.LogName}：分卷组不完整" +
                    (string.IsNullOrWhiteSpace(rootTask.VolumeInfoText) ? string.Empty : $"（{rootTask.VolumeInfoText}）") +
                    "，链结束后不动源包（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            string destinationDirectory = _host.ResolveContinuationOutputDirectory(rootTask);

            if (string.IsNullOrWhiteSpace(destinationDirectory))
            {
                logEntries.Add(("WARN", $"{rootTask.LogName}：算不出根任务的最终输出目录，链结束后不补搬源包。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            string? verificationGap = _host.DescribeChainVerificationGap(destinationDirectory, rootTask, chainTasks);

            if (verificationGap != null)
            {
                logEntries.Add((
                    "WARN",
                    $"{rootTask.LogName}：链里的输出校验没有全部通过（{verificationGap}），" +
                    $"链结束后不补搬源包（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            int contentFiles = CountContentFiles(destinationDirectory);

            if (contentFiles == 0)
            {
                // 内容物压根没出现（例如第二层解压失败 / 密码不对）：源包留在原地，其余物不为它生成。
                logEntries.Add((
                    "WARN",
                    $"{rootTask.LogName}：{destinationDirectory} 里没有内容物（只有过程物），" +
                    $"链结束后不补搬源包（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            /*
             * ===== 按档位收尾：删 or 搬（**这一支以前只实现了"搬"**）=====
             *
             * 用户 2026-09-24 的第 19.4 条：设置=「校验通过后删除」时，选中**文件夹**里那些包一个都没被删，
             * 而单个文件会被删。诊断结论就落在这里 ——
             * 文件夹里的包绝大多数是"第一层只出内层包"的形状（假 MP4 + 尾部 ZIP + 内层加密分卷），
             * 于是它们全被记账成"留到链结束后补做"；而链结束后的这一段老代码写着
             * `if (sourceHandling != MoveToRest) { 写一句"按该档不搬源包"; return; }` ——
             * **DeleteAfterVerify 走到这里等于什么都没做**，源包自然而然留在了原地。
             *
             * 现在两种档位共用上面那同一套判据（内容物已定稿 + 全链校验通过 + 那个目录里确实有内容物 +
             * 未取消 + 不是内层包），只是最后的动作不同：一个搬进其余物、一个按 §9.5 删掉。
             * 失败 / 取消 / 校验未通过 → 上面每一条都已经 return，一个字节都不会动（红线不变）。
             */
            string artifactRoot = rootTask.RestDirectoryPath;

            if (string.IsNullOrWhiteSpace(artifactRoot))
            {
                // 定稿计划没留下其余物目录：宁可不搬，也不要把用户的源包扔到一个猜出来的位置。
                logEntries.Add((
                    "WARN",
                    $"{rootTask.LogName}：这一轮的其余物目录没有被记下来，为避免把源包搬到别处，" +
                    $"链结束后不补搬（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                logEntries.Add(("WARN", $"{rootTask.LogName}：链结束后的补搬被取消，源包留在原地（其余物不生成）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            logEntries.Add((
                "INFO",
                $"{rootTask.LogName}：链结束后的补搬 —— 整条续解链已跑完，{destinationDirectory} 里有 " +
                $"{contentFiles} 个内容物且输出校验通过；本轮没有内容物可定稿、留到现在的源包按设置移入其余物。"));

            /*
             * ⛔ 2026-10-15（用户 2026-10-14 口径）：源包那一档只剩「留下 / 彻底删除」，而**两者都不搬** ——
             * 留下 = 一个字节都不动；彻底删除 = **就地删**（⛔ 不搬进其余物，用户逐字：
             * 「原包一直在原来的位置，选『彻底删除』也是就地删、不用移入其余物」）。
             * ⇒ 这里原来那次 `ExecuteSourcePackageMove(…, SourceMoveTrigger.AfterChain, …)`
             * （把源包搬进其余物 + 随后按「删除操作」清那一份）**整段删除**，
             * 改走与别处**同一条闸门链**（唯一出口 `_host.PurgeLayerSourcePackage`）。
             */
            SourceHandlingMode deferredSourceHandling = _host.RunOptions?.SourceHandling
                ?? AppSettings.ParseSourceHandling(_host.Settings?.SourceHandling);

            if (deferredSourceHandling != SourceHandlingMode.DeleteInPlace)
            {
                logEntries.Add((
                    "INFO",
                    $"{rootTask.LogName}：按「源包留下」这一档，链结束后源包一个字节都不动"
                    + $"（留在原处，随时可手工处理）。"));

                return new DeferredSourceMoveWork(logEntries);
            }

            /*
             * 「彻底删除」：走**同一条闸门链**（L4 完整性 + L5 收尾复核 + 未取消 + 属于本任务 + 半套分卷）。
             *
             * ⚠ 2026-10-15：这里**喂的是真证据**，⛔ 不是放宽闸门 —— 上面刚算出来的
             * <paramref name="rootCompleteness"/>（L4 唯一出口 <see cref="ResultCompletenessClassifier"/>）
             * 就是 `_host.PurgeLayerSourcePackage` 那一档要的那份事实；机器终态、分卷完整性、内容物存在与否
             * 也都在上面逐条查过（任一条不过就在那一条 return 了，走不到这里）。
             * ⇒ 传 `reclaimVerdict` 之后，执行体不再要求 `commit`/`verification` 那两份"本轮定稿"事实
             * （链尾本来就没有它们），而 半套分卷闸门 / 幂等 / 路径形状 那几道**照旧全过**。
             */
            _host.PurgeLayerSourcePackage(
                rootTask,
                verification: null,
                commit: null,
                logEntries,
                LayerPurgeTrigger.SourceHandlingDelete,
                reclaimVerdict: rootCompleteness);

            return new DeferredSourceMoveWork(logEntries);
        }

        /// <summary>
        /// 链上的任务集合（调用方传进来的续解子任务 + 根任务自己），去重后逐个产出。
        /// 根任务单独兜一层：调用方可能只传了子任务，而"根任务也是链上的一员"这件事不能漏。
        /// </summary>
        internal static IEnumerable<ArchiveTask> EnumerateChainTasks(
            ArchiveTask rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks)
        {
            var seen = new HashSet<ArchiveTask>();

            if (chainTasks != null)
            {
                foreach (ArchiveTask? candidate in chainTasks)
                {
                    if (candidate != null && seen.Add(candidate))
                    {
                        yield return candidate;
                    }
                }
            }

            if (seen.Add(rootTask))
            {
                yield return rootTask;
            }
        }

        /// <summary>
        /// 数一个最终目录里**真正的内容物文件**：排除 <c>其余物</c>（含旧名 <c>过程物</c>）里的东西，
        /// 排除归档与分卷这类"待续解的过程物"，**并排除工作区自己那棵树**（用户 2026-09-30）。
        ///
        /// <para>这是"内容物确实已定稿"的事实依据 —— 不用"本轮搬了几条"这种过程数字
        /// （用户明确要求：别拿 move 计数当唯一判据）。
        /// 读不了目录时返回 0：宁可判定"没有内容物"而不动源包。</para>
        ///
        /// <para>⛔ 为什么必须排除工作区：工作区默认就建在目标目录里面（<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>），
        /// 而这一支是**删/搬源包（不可逆）之前的闸门** —— 把工作区里的暂存产物数成"内容物"，
        /// 等于用一个假事实去放行不可逆动作。判据唯一出口 <see cref="WorkspaceTree"/>。</para>
        /// </summary>
        internal int CountContentFiles(string destinationDirectory)
        {
            if (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory))
            {
                return 0;
            }

            string workRoot = _host.Paths.WorkDirectory;

            try
            {
                int count = 0;

                // 「内容物保留关键词」判据：命中它的那些**就是内容物**（用户 2026-10-04 的新功能），
                // 所以"这一层有没有内容物"这件事要把它们数进去（判据唯一出口 ContentKeepRules）。
                ContentKeepRules keepRules = ContentKeepRules.FromSettings(_host.Settings);

                foreach (string file in Directory.EnumerateFiles(destinationDirectory, "*", SearchOption.AllDirectories))
                {
                    if (WorkspaceTree.ShouldSkipEntry(file, workRoot))
                    {
                        continue;
                    }

                    if (ProcessArtifactLayout.IsInsideArtifactDirectory(file, destinationDirectory))
                    {
                        continue;
                    }

                    if (StageFilePredicates.IsProcessArtifactFile(file) && !keepRules.ShouldKeep(file))
                    {
                        continue;
                    }

                    count++;
                }

                return count;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 归集之后其余物目录到底在哪。
        ///
        /// 归集把 <c>destDir</c> 整棵搬到 <c>归集目标\包名\</c>（**保留目录结构**），
        /// 所以"计划路径相对 destDir 的那一段"就是它在新位置下的相对路径。三种情况：
        /// ① 归集之后的位置已经存在（其余物真的被搬过去了）→ 用它；
        /// ② 它还不存在、而计划路径还在（归集只搬走了一部分）→ 用计划路径；
        /// ③ 两边都不存在（本次根本没有过程物）→ **跟着内容物走**，在归集目录里新建。
        ///
        /// ③ 是最容易写错的一档：没有过程物时 <c>destDir\其余物</c> 压根不存在，
        /// 只看"存在与否"就会退回那个**已经不存在**的旧位置，源包被扔进一个空目录（用户找不到）。
        /// </summary>
        internal static string ResolveRestDirectoryAfterCollect(
            StageCommitResult commit,
            CollectResult? collected)
        {
            string planned = commit.ProcessArtifactDirectory;

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
                // destDir 从计划里的路径反推：其余物目录永远在它下面（计划就是这么算的）。
                string destDir = Path.GetDirectoryName(planned) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(destDir))
                {
                    return planned;
                }

                string moved = Path.Combine(
                    collected.DestinationPath,
                    Path.GetRelativePath(destDir, planned));

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
        /// 搬运成功后把任务对象上的源路径改到新位置（CurrentPath 与分卷清单）。
        ///
        /// <c>task.FileName</c> 不变（只是换目录），所以界面上的名字照旧，路径列会显示新位置 ——
        /// 这是诚实的：源包确实已经在其余物里了。
        ///
        /// <para>⚠ 判据只有一处：<see cref="TaskPathSync.ApplyMove"/>（⛔ 别再自己写一遍"只改 CurrentPath"
        /// —— 那样账上会留下盘上已不存在的旧名字，见 §38 与内层包那一处 2026-10-02 的补齐）。</para>
        /// </summary>
        internal static void ApplySourceMoveResult(ArchiveTask task, SourcePackageMoveResult result)
        {
            var byOldPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (SourcePackageMove move in result.Moved)
            {
                byOldPath[move.SourcePath] = move.TargetPath;
            }

            TaskPathSync.ApplyMove(task, byOldPath);
        }

        /// <summary>
        /// **过路层那一单的其余物按档处理**（用户 2026-10-06 拍板：「其余物会在每层的过程中会删掉，
        /// 我无法理解为什么还会有其余物留着」；他两次追问的 `111\111\其余物\111.zip`（48.35 MB）就是这一格）。
        ///
        /// <para><b>形状</b>：某一单只出**过程物**（它解出来的入口包 / 分卷片就是下一层的输入），
        /// 自己因此停在中途；那一组后来由**另一个当场核过清单、可证完整**的消费方解开
        /// ⇒ 它自己那份其余物不再有用，按「删除操作」处理。</para>
        ///
        /// <para>⛔ **六道门槛一条不绕过**（机器终态 / 完整性证据 / 其余物目录 / 形状 / 输出范围 /
        /// 半套分卷），唯一差别是**证据来源**：这一单自己停在中途 ⇒ 它自己的完整性判不出来，
        /// 该按证据的是**接手它的那一单**（`completenessEvidence`）。⛔ 那条证据只用于这一次删除，
        /// 不写回它自己的账。</para>
        ///
        /// <para>⛔ 放行之前还要逐条确认：其余物里**每一个文件的名字都属于这一组**
        /// （= 都是被那次成功解压接手的过程物）；只要有一个不属于 ⇒ 什么都不做（判不出就不动）。</para>
        ///
        /// <para><b>⚠ 2026-10-11 环台实测（取证，⛔ 不是推断）</b>：把这一档与
        /// <see cref="SweepPassThroughRestDirectoriesAsync"/> **两条都整条关掉**
        /// （各加一句 early return），整个 <c>SiblingFolderVolumeGatherTests</c>（19 条）
        /// **照旧 18 通过 / 1 跳过 / 0 失败**，`彻底删除：111\其余物（8 项 / 7.0 MB）`照旧执行 ⇒
        /// 现在这几条用例真正覆盖的是**链尾那一遍**「这一单自己的其余物按档处理」
        /// （关掉它 ⇒ `真机形状_过路层那一单的其余物按档删掉_48MB那处` 当场红）。
        /// ⇒ 本档在现有夹具里**没有一次真的删成功**（19 次调用全部停在闸门上，逐条见日志里那句
        /// `过路层的其余物这次没删 —— …`），⛔ **不许把"用例绿"读成"这一档验过了"**；
        /// 哪一天真机日志里出现 `过路层的其余物已按「删除操作」处理`，那才是它第一次真干活。</para>
        /// </summary>
        internal void PurgePassThroughRestOfSettledProducer(
            ArchiveTask producer,
            ArchiveTask consumer,
            string groupBaseName)
        {
            if (producer == null || !_host.PassThroughRestHandled.Add(producer))
            {
                return;
            }

            string restDirectory = producer.RestDirectoryPath;

            if (string.IsNullOrWhiteSpace(restDirectory) || !Directory.Exists(restDirectory))
            {
                // 「判不出 ⇒ 什么都不做」：把判据输入写进日志（⛔ 判据本身一个字不动）。
                _host.LogPassThroughRestCriterionInputs(
                    producer,
                    consumer,
                    groupBaseName,
                    restDirectory,
                    "没记下这一单定稿实际用的其余物目录（或它已经不在盘上）");

                return;
            }

            string mode = RestHandlingModes.Normalize(RestHandlingThisBatch);

            if (string.Equals(mode, RestHandlingModes.Keep, StringComparison.OrdinalIgnoreCase))
            {
                // 「不动其余物」这一档：一个字节都不删（不变量 1 的默认档）。
                return;
            }

            try
            {
                /*
                 * ⛔ 前置闸门的引用名走**唯一出口**（`_host.AllRestFilesBelongToGroup` → `ResolveGroupReferenceName`）：
                 * 原来写死 `<组基名>.zip` ⇒ 非 ZIP 族这一层永远判"里面有别人的东西" ⇒ 永远不删
                 * （用户选了「彻底删除」也不动）；ZIP 族却删 —— 同一个设置两种结果（2026-10-11 统一）。
                 */
                if (!_host.AllRestFilesBelongToGroup(
                        consumer,
                        groupBaseName,
                        Directory.EnumerateFiles(restDirectory, "*", SearchOption.AllDirectories)))
                {
                    // 里面还有不属于这一组的东西 / 读不到 ⇒ 判不出 ⇒ 什么都不做（原样留着）。
                    _host.LogPassThroughRestCriterionInputs(
                        producer,
                        consumer,
                        groupBaseName,
                        restDirectory,
                        "其余物里有不属于这一组的文件（判不出它是不是被接手的过程物）");

                    return;
                }
            }
            catch
            {
                return;
            }

            ResultCompletenessVerdict evidence = ResultCompletenessClassifier.Classify(consumer);

            if (!evidence.AllowsSourceRemoval)
            {
                _host.LogPassThroughRestCriterionInputs(
                    producer,
                    consumer,
                    groupBaseName,
                    restDirectory,
                    "接手方那一单的完整性证据不允许删源（不是「可证完整 + 校验通过」）");

                return;
            }

            bool cancelled = _host.IsStopping || _host.OperationCts?.IsCancellationRequested == true;

            // ⚠ 2026-10-15：回收站档整条下线 ⇒ 这里（Keep 已在上面挡掉）只剩永久删。
            DeleteMode deleteMode = DeleteMode.Permanent;

            RestPurgeOutcome outcome;

            try
            {
                outcome = new RestItemPurger(
                        _host.RestDeleteExecutor,
                        null,
                        ContentKeepRules.FromSettings(_host.Settings))
                    .Purge(
                        producer,
                        cancelled,
                        deleteMode,
                        evidence,

                        /*
                         * 输出范围那一道门槛的锚点：这一单被收场时 `OutputPath` 已经被清空
                         * （跟班卷没有自己的成品目录）⇒ 用**唯一落点出口**现算一份它那条链的落点目录
                         * —— 其余物正是建在那一层底下（真机 `111\111\其余物`）。
                         */
                        /*
                         * ⛔ 锚点 = **其余物目录的父目录**（其余物就建在它自己那一层底下），
                         * ⛔ **绝不现算这一单的落点**：真机 2026-10-06 21:24 实测 —— 走到这里时
                         * 它的源包**已经被搬进组层其余物**，现算出来是 `…\其余物\111` 这种荒唐路径，
                         * 于是"不在本任务输出目录之内"把删除拒了 4 次，48.35 MB 一直留着。
                         * 其余五道门槛（终态 / 完整性证据 / 目录形状 / 组内文件 / 半套分卷）照旧一条不绕过。
                         */
                        Path.GetDirectoryName(restDirectory) ?? string.Empty);
            }
            catch (Exception ex)
            {
                // 删东西绝不许把已经成立的结论拖成异常：落成"没删成"，别的什么都不变。
                _host.AppendLog("WARN", $"{producer.LogName}：处理过路层的其余物时出现意外错误：{ex.Message}");
                return;
            }

            _host.AppendLog(
                outcome.Succeeded ? "INFO" : "WARN",
                outcome.Succeeded
                    ? $"{producer.LogName}：过路层的其余物已按「删除操作」处理（{outcome.EntryCount} 项 / {outcome.FreedBytes} 字节）"
                      + $"—— 这一组的内容已由「{consumer.FileName}」解开。"
                    : $"{producer.LogName}：过路层的其余物这次没删成 —— {outcome.Message}");
        }

        /// <summary>
        /// 「其余物」这一档会动到什么 —— **其余物 = 过程物 + 原包**（用户 2026-09-27 定死的口径）。
        ///
        /// <para>为什么必须由档位决定这句话：源包那一档选「留在原地」时，其余物里**根本没有源包**。
        /// 2026-09-27 真机上就是这里出的洋相 —— 同一份日志一边写"源包处理：留在原地（一个字节都不搬）"，
        /// 一边写"其余物：任务成功后会彻底删除（源包 + 过程物）"，用户直接问"我都设置了原包不动，
        /// 你怎么还说原包在其余物里面"。</para>
        /// </summary>
        internal static string DescribeRestScope(SourceHandlingMode sourceHandling)
            => sourceHandling == SourceHandlingMode.KeepInPlace
                ? "解压过程中产生的过程物（源包留在原地：一个字节都不搬、不删）"
                : "源包 + 过程物";

        internal static bool CanSweepPassThroughRest(
            ArchiveTask? task,
            IReadOnlyDictionary<string, string>? consumedVolumeSources)
        {
            if (task == null || string.IsNullOrWhiteSpace(task.RestDirectoryPath))
            {
                return false;
            }

            // ① 失败 / 取消 ⇒ 红线：其余物一个字节都不动（那是失败现场的凭证）。
            if (task.Outcome is TaskOutcome.Failed or TaskOutcome.Cancelled)
            {
                return false;
            }

            if (task.OutputVerification == OutputVerificationOutcome.Failed)
            {
                return false;
            }

            string restDirectory = task.RestDirectoryPath;

            if (!Directory.Exists(restDirectory))
            {
                return false;
            }

            if (consumedVolumeSources == null || consumedVolumeSources.Count == 0)
            {
                return false;
            }

            // ② 其余物里每一个文件都必须在"借来的源片"账里（= 已被下游接手）。
            bool any = false;

            foreach (string file in Directory.EnumerateFiles(restDirectory, "*", SearchOption.AllDirectories))
            {
                any = true;

                bool adopted = false;

                foreach (string key in consumedVolumeSources.Keys)
                {
                    if (SafePathHelper.PathEquals(key, file))
                    {
                        adopted = true;
                        break;
                    }
                }

                if (!adopted)
                {
                    // 判不出 / 还有一个没被接手的 ⇒ 什么都不做。
                    return false;
                }
            }

            return any;
        }

        /// <summary>
        /// 把**这一个根任务那条链里"过路层"的其余物**按档处理掉（含 `111\111\其余物` 这种中间层）。
        ///
        /// <para>只处理"落在根任务那棵树里、且放行判据成立"的那几单 —— 这是与"对全表扫一遍"的关键差别：
        /// 后者会碰到别的链、也会碰到链中途失败要留现场的那几单（实测把三条红线用例打红）。</para>
        /// </summary>
        internal async Task SweepPassThroughRestDirectoriesAsync(ArchiveTask rootTask, CancellationToken cancellationToken)
        {
            string? rootDirectory = Path.GetDirectoryName(rootTask.OutputPath);

            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                string contentRoot = rootTask.ContentDirectoryPath;

                rootDirectory = string.IsNullOrWhiteSpace(contentRoot)
                    ? null
                    : Path.GetDirectoryName(contentRoot);
            }

            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            {
                return;
            }

            foreach (ArchiveTask restOwner in _host.SnapshotTaskTable(_host.Tasks))
            {
                if (restOwner == null
                    || ReferenceEquals(restOwner, rootTask)
                    || string.IsNullOrWhiteSpace(restOwner.RestDirectoryPath))
                {
                    continue;
                }

                // ⛔ 只碰**这个根任务那棵树里**的其余物（别的链、别的包一律不动）。
                if (!IsRestInsideRootTree(rootDirectory, restOwner.RestDirectoryPath))
                {
                    continue;
                }

                if (!CanSweepPassThroughRest(restOwner, _host.ConsumedVolumeSources))
                {
                    continue;
                }

                await ApplyRestHandlingAfterChainAsync(restOwner, null, cancellationToken);
            }
        }

        /// <summary>
        /// **"过路层的其余物"该不该放行**（唯一判据，纯函数；真机 CCCC 2026-10-06）。
        ///
        /// <para><b>为什么不能只看终态"成功"</b>：真机那个现场的过路层是 `111.rar` —— 它只出过程物
        /// （解出来的入口包 `111.zip` 被**采纳进别的层**），于是它自己那条链停在中途、终态是
        /// **「部分完成」** ⇒ 按"只放行成功"那条判据它**永远被挡在外面**，那份其余物
        /// （`111\111\其余物\111.zip`，48.35 MB）就一直留在盘上（用户报的"其余物还有"）。</para>
        ///
        /// <para><b>但也不许改成"部分完成就删"</b> —— 那是失败现场的凭证（红线：
        /// 失败 / 部分完成 / 取消 ⇒ 其余物一个字节都不动）。⇒ 放行条件换成**可证"这份其余物里的东西
        /// 已经全被下游接手了"**（三条同时成立）：</para>
        /// <list type="number">
        /// <item><description>这一单**没失败**、也没被取消（`Failed` / `Cancelled` 一律不动）；</description></item>
        /// <item><description>其余物目录**在盘上**且**不是空的**（空壳由既有那一档处理，这里不抢它的活）；</description></item>
        /// <item><description>其余物里**每一个文件**都出现在那份"借来的源片"账
        /// <c>_host.ConsumedVolumeSources</c> 里（= 它已经被下游某一单接手用过了）——
        /// 只要有一个文件没进账 ⇒ 判不出 ⇒ **什么都不做**。</description></item>
        /// </list>
        /// </summary>
        /// <summary>
        /// **这一份其余物是不是落在"根任务那棵树"里**（⛔ 只碰这一棵树：别的链、别的包一律不动）。
        ///
        /// <para>⚠ 参数顺序按 <see cref="ArchivePathGuard.IsInsideRoot"/> 的定义来：**第一个是根、第二个是待查路径**。
        /// 真机 CCCC 2026-10-06 18:13 那一批：这里两个参数写反了 ⇒ 每一份过路层其余物都被判成"不在树里"
        /// ⇒ <see cref="SweepPassThroughRestDirectoriesAsync"/> 整段成了死代码 ⇒
        /// `111\111\其余物\111.zip`（48.35 MB）一直留到用户看见（他原话：「**还有 48MB 的问题**」）。</para>
        /// </summary>
        internal static bool IsRestInsideRootTree(string? rootDirectory, string? restDirectory)
            => !string.IsNullOrWhiteSpace(rootDirectory)
               && !string.IsNullOrWhiteSpace(restDirectory)
               && ArchivePathGuard.IsInsideRoot(rootDirectory, restDirectory, out _);

        /// <summary>
        /// **把一个过程物文件搬进其余物**（唯一出口：让位 + 同卷改名 / 跨卷复制 + 绝不覆盖）。
        ///
        /// <para>三处消费点共用它（⛔ 不许各写一份）：① <c>CollectChainInnerPackagesIntoRestAsync</c>
        /// （链尾把"内层包"集中进其余物）；② <c>CollectChainInnerLayerInputsIntoRest</c>
        /// （链内那一族的片在链尾集中）；③ <c>_host.PurgeGroupLandedPieces</c>
        /// （被整组接走的那份副本集中进其余物）。</para>
        ///
        /// <para>判据与源包搬运（<c>SourcePackageMover</c>）**同一口径**：目标名被占 ⇒
        /// <see cref="ProcessArtifactLayout.MakeUniqueTarget"/> 让位（连"本计划里前面那几条"一起算），
        /// 同卷直接改名、跨卷/被占用才回落到"先复制成功再删原件" —— 复制失败就一个字节都不动。</para>
        /// </summary>
        /// <param name="from">源（必须在盘上）。</param>
        /// <param name="to">目标（最终名可能因让位而变，见 <paramref name="target"/>）。</param>
        /// <param name="movedTargets">本趟已经落下去的目标名（让位时要看它）。</param>
        /// <param name="target">真的搬到的那个名字（失败时为空串）。</param>
        /// <param name="failure">失败原因（成功时为 null）。</param>
        internal static bool TryMoveIntoRest(
            string from,
            string to,
            HashSet<string> movedTargets,
            out string target,
            out string? failure)
        {
            target = to;
            failure = null;

            try
            {
                if (movedTargets.Contains(target) || File.Exists(target) || Directory.Exists(target))
                {
                    target = ProcessArtifactLayout.MakeUniqueTarget(
                        target,
                        isDirectory: false,
                        movedTargets,
                        FileSystemArtifactTargetProbe.Instance);
                }

                movedTargets.Add(target);

                SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(target) ?? string.Empty);

                try
                {
                    File.Move(from, target, overwrite: false);
                }
                catch (IOException)
                {
                    File.Copy(from, target, overwrite: false);
                    File.Delete(from);
                }
                catch (NotSupportedException)
                {
                    File.Copy(from, target, overwrite: false);
                    File.Delete(from);
                }

                return true;
            }
            catch (Exception ex)
            {
                failure = ex.Message;

                return false;
            }
        }

        /// <summary>
        /// 「删除操作」那一档的**唯一触发点**：任务收尾之后，按用户选的档处理它自己的其余物。
        ///
        /// <para>三档（用户 2026-09-25 第 32 条亲自定的）：不动（Keep）/ 移入回收站 / 彻底删除。
        /// 后两档的门槛全在 <see cref="RestItemPurger"/> 里（终态必须是「完成」+ 校验通过 + 未取消 +
        /// 路径是定稿那一刻记下来的那一条 + 落在自己的输出根之内）——这里只负责"什么时候试"和"怎么记账"。
        /// **失败 / 部分完成 / 取消的任务一个字节都不会被删**（不变量 1 的红线）。</para>
        /// </summary>
        internal async Task RunRestHandlingAsync(
            ArchiveTask task,
            ScheduledTaskRuntime runtime,
            bool oneClickRun,
            bool force = false)
        {
            string mode = RestHandlingModes.Normalize(RestHandlingThisBatch);

            /*
             * ⚠ 一键处理里**不能在这里动手**（2026-09-25 第 32 条实现删除操作时被测试逮住的顺序陷阱）：
             * 任务收尾这一刻，其余物里躺着的正是**下一层要解的内层包** —— 当场删掉，续解就没粮草了
             * （实测：选了「彻底删除」之后 ContinuationLayers 直接变成 0，第二层再也解不出来）。
             * 所以一键档统一记成"留到链尾"，由
             * CompleteRootSourcePackagesAfterChainAsync → ApplyRestHandlingAfterChainAsync 处理；
             * 手动「只解压」是单层路径，没有链可等，当场按档处理。
             */
            if (oneClickRun && !force)
            {
                if (!string.Equals(mode, RestHandlingModes.Keep, StringComparison.OrdinalIgnoreCase))
                {
                    /*
                     * ⚠ 2026-10-03（普通档「逐层回收」）：续解层的过程物**已经当场回收掉了**
                     * （`PostProcessSuccessAsync` 里那一支），链尾那一档从今天起只处理最外层源包 ——
                     * 老文案"整条续解链跑完后再按「删除操作」处理"在这一档下就是**假话**
                     * （用户照它去等，什么都不会再发生）。
                     * ⛔ 只改这一支的措辞；最外层那一单照旧（它的其余物确实要等链尾）。
                     *
                     * ⚠ 2026-10-04（同一天的真机）：这一句以前是**无条件**的 —— 续解层「定稿搬运失败」
                     * 时它照样写着"已当场回收"，而那一刻回收判据一条都没过（`_host.PurgeLayerSourcePackage`
                     * 要么被 `commit.FailedCount > 0` 挡下、要么根本走不到），过程物**原封不动躺在盘上**。
                     * 现在**按事实说**（用户要求：真删了才说已回收，被拒就如实说没回收 + 原因）。
                     *
                     * 事实位 = `task.SourcePackageMove == Done`：对续解任务它**只可能**由
                     * `_host.PurgeLayerSourcePackage` 写（`MoveSourcePackageIntoRest` 那一支对续解任务走不到，
                     * 上面那个 `else if (task.IsContinuationTask)` 先接住了），而它只在
                     * "真的删掉了至少一个文件、且一个都没失败"时才落 —— 正是"真删了"。
                     */
                    if (_host.LayerReclaimThisBatch && task.IsContinuationTask)
                    {
                        _host.AppendLog(
                            "INFO",
                            task.SourcePackageMove == SourcePackageMoveState.Done
                                ? string.Format(
                                    CultureInfo.CurrentCulture,
                                    StatusText.LayerProcessReclaimedFormat,
                                    task.FileName)
                                : string.Format(
                                    CultureInfo.CurrentCulture,
                                    StatusText.LayerProcessNotReclaimedFormat,
                                    task.FileName,
                                    DescribeLayerReclaimBlocker(task)));

                        return;
                    }

                    /*
                     * ⚠ 措辞要**如实**（第 35 条）：这一轮**没有**产生其余物时（内容物里就是那个内层包），
                     * 老文案"其余物先留着（里面还有要接着解的内层包）"会让人以为目录已经建好了 ——
                     * 用户按这句话去找其余物，只会更糊涂。有其余物才说"留着"，没有就说清"这一轮没有其余物"。
                     */
                    _host.AppendLog(
                        "INFO",
                        string.IsNullOrWhiteSpace(task.RestDirectoryPath)
                            ? $"{task.LogName}：这一轮没有产生其余物（过程物就是内容物里的那个内层包）；"
                              + "整条续解链跑完后按「删除操作」处理内层包。"
                            : $"{task.LogName}：其余物先留着（里面还有要接着解的内层包），"
                              + "整条续解链跑完后再按「删除操作」处理。");
                }

                return;
            }

            if (string.Equals(mode, RestHandlingModes.Keep, StringComparison.OrdinalIgnoreCase))
            {
                // 不动其余物：也要**说一句**（用户 2026-09-25 真机反馈："源包确实没有了，但是其余物还在"）——
                // 静默正是问题本身。只对真的产出了其余物的任务写，免得整批刷屏。
                if (!string.IsNullOrWhiteSpace(task.RestDirectoryPath))
                {
                    /*
                     * 一行说清（第 44 条：一次导出 713 KB，"每次不需要汇报得那么详细"）。
                     * "去哪儿改档位"这句话**批首已经说过一遍**（`其余物：本批保留（③页「删除操作」= 不动其余物）…`），
                     * 逐任务再念一遍 68 次只是把日志撑大。
                     */
                    _host.AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.RestKeptCompactFormat,
                            task.FileName,
                            LogLineText.DescribeShortDestination(task.RestDirectoryPath, task.RestDirectoryPath),
                            LogLineText.CountRestEntries(task.RestDirectoryPath)));
                }

                return;
            }

            bool cancelled = _host.IsStopping || _host.OperationCts?.IsCancellationRequested == true;

            // ⚠ 2026-10-15：回收站档整条下线 ⇒ 这里（Keep 已在上面挡掉）只剩永久删。
            DeleteMode deleteMode = DeleteMode.Permanent;

            RestPurgeOutcome outcome;

            try
            {
                // 磁盘活（量大小 + 删目录）→ 放后台，别占住 UI 线程。
                outcome = await Task.Run(
                    () => new RestItemPurger(
                            _host.RestDeleteExecutor,
                            null,
                            ContentKeepRules.FromSettings(_host.Settings))
                        .Purge(task, cancelled, deleteMode));
            }
            catch (Exception ex)
            {
                // 这一层不该抛（RestItemPurger 内部全部收敛成结论），但"删东西"这件事
                // 绝不允许把已经成功的解压拖成异常：结论落成"没删成"，内容物不受影响。
                outcome = new RestPurgeOutcome
                {
                    Message = $"{task.LogName}：处理其余物时出现意外错误：{ex.Message}"
                };
            }

            /*
             * 逐条 LogLines（`[彻底删除] 成功 路径=…；理由=…；条目数=…；总大小=…；说明=…`）
             * **只在没删成时写**（用户 2026-09-25 第 44 条：成功那一支一行就够，见下面）。
             */
            if (!outcome.Succeeded)
            {
                foreach (string line in outcome.LogLines)
                {
                    _host.AppendLog("INFO", line);
                }
            }

            if (outcome.Succeeded)
            {
                runtime.RestPurged = deleteMode == DeleteMode.Permanent;
                runtime.PurgedBytes = outcome.FreedBytes;

                /*
                 * 一行说清（用户 2026-09-25 第 44 条："最后删除文件你还弄得这么多的文字"）。
                 * 以前这里是两句长文案（含理由、条目数、总大小、说明、完整路径、不可逆提示），
                 * 68 个包就是 400 多行、7 万多字符。理由与"不可逆"已经在批首那条 + ③页常驻红提示里
                 * 说过一次了，逐任务再念一遍只是把日志撑大；**失败时仍然全量**（下面那一支）。
                 */
                _host.NoteBatchPurge(outcome.FreedBytes, deleteMode == DeleteMode.Permanent);

                _host.AppendLog("INFO", DescribeRestPurgedLine(outcome, deleteMode));

                return;
            }

            runtime.PurgeNote = outcome.Message;

            // 其余物没删成也是"其余物还在"的一个答案 ⇒ 同一句话落到①页 / 批末诊断 / 失败清单。
            RecordRestKept(task, outcome.Message);

            _host.AppendLog(outcome.Attempted ? "ERROR" : "INFO", outcome.Message);
        }

        /// <summary>
        /// 逐层回收那一句里"**为什么没回收**"（只有 <c>SourcePackageMove != Done</c> 时才会问到这里）。
        ///
        /// <para>三档都只读**任务上已有的**事实（状态常量 / 机器终态），⛔ 不重推判据、不重算原因：</para>
        /// <list type="number">
        /// <item><description><b>部分完成</b>（删了一部分没删完，占用 / 只读 / 权限）——
        /// 这时候那一层已经写过一行 ERROR，指过去。</description></item>
        /// <item><description><b>终态成功</b> ⇒ 这一层跑成了，是回收判据没放行（完整性判不出 / 半套分卷闸门 /
        /// 源包清单是空的）—— 那一层也各写了一行 WARN，指过去。</description></item>
        /// <item><description><b>其余</b> ⇒ 这一层自己没跑成（定稿搬运失败 / 校验没过 / 已取消），
        /// 回收那一步**根本没走到** ⇒ 原因在①页「错误信息」列（用户 2026-10-04 真机正是这一档）。</description></item>
        /// </list>
        /// </summary>
        internal static string DescribeLayerReclaimBlocker(ArchiveTask task)
        {
            if (string.Equals(task.Status, StatusText.PartiallyCompleted, StringComparison.Ordinal))
            {
                return StatusText.LayerReclaimPartialBlockedReason;
            }

            if (task.Outcome == TaskOutcome.Succeeded)
            {
                return StatusText.LayerReclaimGateBlockedReason;
            }

            return string.Format(
                CultureInfo.CurrentCulture,
                StatusText.LayerReclaimLayerFailedReasonFormat,
                task.Status);
        }

        /// <summary>
        /// 其余物处理成功后那**一行**（措辞唯一出口，单独可测）。
        ///
        /// <para><b>两档措辞</b>：</para>
        /// <list type="bullet">
        /// <item><description><b>空壳档</b>（<see cref="RestPurgeOutcome.RemovedAsEmptyShell"/>）：
        /// 其余物里递归地一个文件都没有 ⇒ 就地删掉空目录、**没有进回收站**
        /// （<see cref="StatusText.RestPurgedEmptyShellFormat"/>）。</description></item>
        /// <item><description><b>普通档</b>：<see cref="StatusText.RestPurgedCompactFormat"/>
        /// —— 档名 + 短路径 + 条目数 + 释放大小（用户 2026-09-25 第 44 条："一次导出 713KB，这个多吓人"）。</description></item>
        /// </list>
        ///
        /// <para>⚠ <b>为什么必须按事实位选、不能按数字猜</b>（用户 2026-10-03 真机）：空壳档在
        /// 「移入回收站」这一档下会写成「移入回收站：…\其余物（0 项 / 0 B）」，而空壳**一个字节都没进回收站**
        /// —— 那就是一句假话。判据只读 <see cref="RestPurgeOutcome.RemovedAsEmptyShell"/>（AGENTS.md §9.5：
        /// 要删 / 要写盘的动作判据只准读事实；"0 项 0 B"证明不了"没进回收站"）。</para>
        /// </summary>
        internal static string DescribeRestPurgedLine(RestPurgeOutcome outcome, DeleteMode deleteMode)
        {
            if (outcome.RemovedAsEmptyShell)
            {
                return string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.RestPurgedEmptyShellFormat,
                    LogLineText.DescribeShortTail(outcome.Directory, 2));
            }

            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.RestPurgedCompactFormat,
                deleteMode == DeleteMode.Permanent ? StatusText.RestActionDelete : StatusText.RestActionRecycleBin,
                LogLineText.DescribeShortTail(outcome.Directory, 2),
                outcome.EntryCount,
                WorkspaceCleanupService.FormatSize(outcome.FreedBytes));
        }

        /// <summary>
        /// 批首定一次"本批的删除操作按哪一档走"（用户中途改了设置也只影响下一批，同一批口径一致）。
        ///
        /// <para>⚠ 2026-09-25 第 33 条：**「本次选项」优先**（<see cref="_host.RunOptions"/> 里那一档）——
        /// 用户在确认框的折叠区里改了「删除操作」，这一次就必须按他选的跑；
        /// 没有本次选项（无界面宿主 / 勾了"以后不再询问"）时才读设置。</para>
        /// </summary>
        internal void PrepareRestHandlingForBatch()
        {
            /*
             * ⚠ 「空间不足」模式**覆盖**这一档（用户 2026-09-27 拍板的口径：
             * 源包 → 放入其余物 + 其余物 → 彻底删除，运行期自动覆盖、⛔ 不写设置）。
             *
             * 注意覆盖的**不是**"是否删源包"那件事 —— 源包在这个模式下的命运在
             * PostProcessSuccessAsync 里已经定了（定稿 + 校验通过 → 当场删）。
             * 这一档管的是**过程物**（内层包、暂存残渣）：它们仍然按链尾统一处理，
             * 所以这里给它落到"彻底删除"。
             */
            if (_host.SpaceTightThisBatch)
            {
                RestHandlingThisBatch = RestHandlingModes.Delete;

                /*
                 * 「空间不足」模式**不走逐层回收这一档**：它有自己的那一支（`PostProcessSuccessAsync`
                 * 的第一支，连**源包**都在每层当场删），措辞与门槛都是它自己那套。
                 * ⛔ 置 false 只是"别让普通档那一支抢它的活"，那个模式的判据一个字都没改。
                 */
                _host.LayerReclaimThisBatch = false;

                return;
            }

            RestHandlingThisBatch = _host.RunOptions != null
                ? RestHandlingModes.Normalize(_host.RunOptions.RestHandling)
                : RestHandlingModes.Normalize(_host.Settings.RestHandlingAfterVerify);

            /*
             * ⛔ **2026-10-15 口径变更：逐层回收与③页「删除操作」档位解绑**（用户 2026-10-14 口径逐字：
             * 「删除操作只针对**最后一层过程物**……**其他的过程物统一彻底删除**（因为他们都是成功的情况下），
             * 统一彻底删除」—— 见 `docs\大改动-2026-10-14-口径与冲突.md` §2.1 / §2.2 / §18.1）。
             *
             * 老口径（2026-10-03 的「B 档位分叉」）是"只有彻底删除档才逐层回收、不动其余物档集中进其余物、回收站档
             * 逐层进回收站"。回收站那一档已整条下线，而**"成功过的过程物"在哪个档下都不该留着**：
             * 用户 2026-10-14 的论证是「删掉的是**成功过的那几层**——它们可以从留下的那一层重做，所以不是证据」。
             * ⇒ 这一位恒为 true（普通档），动作恒为**当场永久删**。
             * ⛔ 那四道门槛（定稿 + 可证完整 + 未取消 + 「半套分卷」）一条都没放宽，改的只是"要不要做"。
             *
             * ⚠ **「最后一层过程物」不在这一位管**（用户口径：那一份默认**留下**，归③页「删除操作」那一档）：
             * 链停下来的那一层，它的输入包是"这一层要解的那个东西"，按红线**一个字节都不动** ——
             * 见 `_host.PurgeLayerSourcePackage` 的入口判据与 `PostProcessSuccessAsync` 里"只有跑成了才回收"。
             * ⇒ 「彻底删除」档目前对"最后一层过程物"还没有动作（**已知实现缺口**，下一刀）。
             */
            _host.LayerReclaimThisBatch = true;

            // 链内记账每批一清（它只服务"这一批这一条链"）。
            _host.ChainInnerLayerInputs.Clear();
        }

        /// <summary>
        /// 本批「其余物」的范围那一句话（口径的唯一来源）。
        ///
        /// <para>「空间不足」模式必须**另说一句**：那种模式下源包根本不进其余物（校验通过即删），
        /// 照普通档说"源包 + 过程物"会让用户以为源包还在其余物里躺着 ——
        /// 而他会去找一个根本不存在的东西（2026-09-27 真机上正是这类自相矛盾的日志挨了骂）。</para>
        /// </summary>
        internal string DescribeRestScopeForBatch()
        {
            if (!_host.SpaceTightThisBatch)
            {
                return DescribeRestScope(_host.EffectiveSourceHandling);
            }

            return _host.SpaceTightKeepSourceThisBatch
                ? "过程物（源包在本模式下「一个字节都不动」：不搬进其余物、也不删除）"
                : "过程物（源包在本模式下不进其余物：定稿 + 校验通过后立刻永久删除）";
        }

        /// <summary>
        /// 批首写一条"其余物这一批会怎么处理"的日志（用户 2026-09-25 第 32 条：
        /// 他要的是**当场看得懂**，而不是跑完发现"其余物还在"却不知道为什么）。
        /// </summary>
        internal void LogRestHandlingForBatch()
        {
            string mode = RestHandlingModes.Normalize(RestHandlingThisBatch);

            /*
             * 范围这句话**跟着源包那一档说**（2026-09-27 用户真机：他选了"源包留在原地"，
             * 这条却写"彻底删除（源包 + 过程物）"，两句自相矛盾）。判据来自 `_host.EffectiveSourceHandling`，
             * ⛔ 不许在这里另读一遍设置。
             */
            string scope = DescribeRestScopeForBatch();

            if (_host.SpaceTightThisBatch)
            {
                /*
                 * 这一批的"为什么这么办"写在最前面（用户 2026-09-26 教训：同一份日志里两句话自相矛盾
                 * 比少写一句更糟）。它必须同时说清三件事：覆盖了什么、**没写设置**、以及源包的下场。
                 *
                 * ⚠ 两档分开说（用户 2026-09-27 加了「不删原包」安全档）：会删的那一档说"立刻删除"，
                 * 安全档说"一个字节都不动" —— 一条文案盖两种行为就是撒谎。
                 */
                _host.AppendLog(
                    "WARN",
                    "「空间不足」模式（本次运行，不写设置）：本批忽略「全速」，并发由空间自己定"
                    + "（你要是把②页的「最大并发解压数」调得更低，就按更低的那个跑 —— 慢盘上少开几个反而更快）；"
                    + (_host.SpaceTightKeepSourceThisBatch
                        ? "「不删原包」也开着 —— 源包一个字节都不动（不搬、不删），"
                          + "只把过程物在任务成功后彻底删掉。⚠ 这一档不回收源包那份空间，需要的余量更大。"
                        : "每个包定稿 + 校验通过后立刻永久删除它的源包"
                          + "（不进回收站、不可恢复），收回的空间马上给后面的包用。")
                    + "失败 / 部分完成 / 取消的任务一个字节都不删。");
            }

            if (string.Equals(mode, RestHandlingModes.Keep, StringComparison.OrdinalIgnoreCase))
            {
                _host.AppendLog(
                    "INFO",
                    $"其余物：本批保留（③页「删除操作」= 不动其余物）。任务成功后其余物（{scope}）留在成品目录里，随时可手工删。");
                return;
            }

            _host.AppendLog(
                "WARN",
                $"其余物：本批任务成功后会彻底删除（{scope}，不进回收站、不可恢复）。"
                + "失败 / 部分完成 / 取消的任务一个字节都不动。");
        }

        /// <summary>
        /// 部分完成收尾：把其余物里**除这条链最外层源包以外**的项删掉（用户 2026-10-02 改的口径）。
        ///
        /// <para><b>为什么可以删</b>：其余物里装的都是过程物（内层包、抠出来的内嵌归档副本），
        /// 全都能从最外层源包重新解出来；用户原话是「我建议源包 + 已解出的内容物留着，其他的都删掉」——
        /// 这样盘上正好是"两份"，与③页小字那句承诺对得上。</para>
        ///
        /// <para><b>⛔ 安全闸门（这一条我来守）</b>：只有"这条链的最外层源包**此刻还在盘上**"时才动手；
        /// 它不在了（已被删 / 路径失效）⇒ **一个字节都不删**（判不出 ⇒ 什么都不做）——
        /// 源包是唯一能重建整条链的东西，它都没了还清过程物，等于把最后一份可恢复路径也删掉。</para>
        /// </summary>
        internal void PurgeRestItemsExceptChainRootSource(ArchiveTask task)
        {
            string rootSource = task.ChainRootIdentity;

            if (string.IsNullOrWhiteSpace(rootSource))
            {
                _host.AppendLog(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PartialPublishRestKeptFormat,
                        task.FileName,
                        "算不出这条链的最外层源包是谁"));

                return;
            }

            string rootSourceFull = SafePathHelper.GetFullPathSafe(rootSource);

            if (rootSourceFull.Length == 0 || !SafePathHelper.FileExists(rootSourceFull))
            {
                _host.AppendLog(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PartialPublishRestKeptFormat,
                        task.FileName,
                        $"这条链的最外层源包不在盘上了（{rootSource}）—— 它是唯一能把整条链重新解出来的东西，"
                        + "所以其余物里的过程物一个都不删"));

                return;
            }

            if (string.IsNullOrWhiteSpace(task.RestDirectoryPath))
            {
                // 这一单压根没建过其余物（部分完成这一档不生成其余物）⇒ 没事可做，不写日志。
                return;
            }

            RestPurgeOutcome purge = new RestItemPurger(
                    null,
                    null,
                    ContentKeepRules.FromSettings(_host.Settings))
                .PurgeExcept(
                task,
                new[] { rootSourceFull },
                DeleteMode.Permanent);

            _host.AppendLog(
                purge.Succeeded ? "INFO" : "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    purge.Succeeded
                        ? StatusText.PartialPublishRestPurgedFormat
                        : StatusText.PartialPublishRestKeptFormat,
                    task.FileName,
                    purge.EntryCount,
                    purge.Message));
        }

        /// <summary>
        /// 本批生效的「删除操作」档（<see cref="RestHandlingModes"/>；批首定一次）。
        ///
        /// <para>⚠ 批首定一次而不是每任务现读设置：同一批里不许"前半段按一个档、后半段按另一个"
        /// （用户可能中途去③页改）。<see cref="RestHandlingModes.Keep"/> 是默认档 ——
        /// 没经过批首的路径（例如单独解压一个包）永远按 Keep 走，不会顺手删东西。</para>
        /// </summary>
        internal string RestHandlingThisBatch = RestHandlingModes.Keep;
    }
}
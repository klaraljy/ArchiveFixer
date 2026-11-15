using ArchiveFixer.BindingModels;
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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>源包清理/逐层回收的删除判据与执行（从 ExtractionCoordinator.Source.cs 搬出）：触发点文案、六道门槛、半套分卷闸门、层清理分类。</summary>
internal sealed class ExtractionSourcePurge
{
    private readonly ExtractionCoordinator _host;

    internal ExtractionSourcePurge(ExtractionCoordinator host)
    {
        _host = host;
    }

    /// <summary>这次是哪个触发点（进日志；⛔ 措辞只在这里拼一次）。</summary>
    private static string DescribeLayerPurgeTrigger(LayerPurgeTrigger trigger) => trigger switch
    {
        LayerPurgeTrigger.LayerReclaim => "逐层回收（成功链的过程物统一彻底删除）",
        LayerPurgeTrigger.SourceHandlingDelete => "源包操作=彻底删除",
        _ => "空间不足模式"
    };

    /// <summary>
    /// **这一层跑完就删掉这一层的源**（两个触发点共用一个方法；调用方只喂事实）。
    ///
    /// <para><b>「空间不足」模式的那一支</b>：**每一层**定稿 + 校验通过之后立刻永久删除
    /// **这一层自己**那一组源包（最外层 = 用户给的包；续解层 = 上一层交出来的内层包）。</para>
    ///
    /// <para><b>为什么必须在"这一刻"删</b>：这个模式能成立的前提是"盘上的字节真的变少了"。
    /// 其余物式的搬运是同盘移动（净占用不变），链尾统一处理又要等到整条续解链跑完 ——
    /// 而链没跑完时后面的包已经因为空间不足被拦下了。所以这里选**最快回收**的那一刻：
    /// 这一层的内容物已定稿（<paramref name="verification"/> 通过）就删。</para>
    ///
    /// <para><b>为什么"每一层"是这条模式的初心</b>（用户 2026-09-29 第 1 条）：四层嵌套链下，
    /// 只有最外层当场回收、内层包却攒到链尾的话，峰值是"四倍单层"；每层各收各的，
    /// 峰值才是"当前层 + 下一层"（≈ 两倍单层）—— 与用户原话"每一层就删一遍，这样也还行，
    /// 只不过是两倍的情况"完全一致。</para>
    ///
    /// <para><b>2026-10-03 起多了一个触发点</b>（用户拍板的「普通档逐层回收」）：普通档里**续解层**也走
    /// 这一条 —— 每层当场回收这一层的过程物。⚠ **2026-10-15 起它与③页「删除操作」档位解绑**：
    /// 成功链上的过程物**一律当场永久删**（用户 2026-10-14 口径：「删除操作只针对**最后一层过程物**……
    /// 其他的过程物统一彻底删除」），只有「空间不足」模式那一档仍是"连源包一起、每层各删各的"。
    /// 判据只有 `_layerReclaimThisBatch` 一个出口（挂钩子那一行也读它）。
    /// 两个触发点的差别只有措辞，以及"最外层源包要不要一起删"这件事由**调用方**决定（这里不管）。</para>
    ///
    /// <para><b>与手动档"解压成功就直接删除解压包"的关系</b>（用户 2026-09-29 第 4 条）：
    /// 语义**完全一样**（成功 + 校验通过 + 未取消 ⇒ 这一层的源包消失），
    /// 只是由模式**强制开启**，不再依赖③页「源包操作 + 删除操作」那两档的组合。
    /// 执行体也仍然是项目里唯一那个"删任务自己的源包"的执行体（<see cref="SourceCleanupService"/>）——
    /// ⛔ 没有第三套删除逻辑。与手动档唯一的差别是**原地删、不先搬进其余物**：
    /// 其余物就在同一块盘上，搬过去是同盘移动、一个字节都不会回到可用空间里，
    /// 那就等于这个模式什么都没省（这一条从 2026-09-27 起就是这么定的，不改）。</para>
    ///
    /// <para><b>门槛比搬运那一档只多不少</b>（判据全在 <see cref="SourceCleanupService"/> 里，这里只喂事实）：</para>
    /// <list type="number">
    /// <item><description>内容物**已定稿**（<paramref name="commit"/>.Attempted 且没有搬运失败）；</description></item>
    /// <item><description>输出校验通过（<paramref name="verification"/>，本方法开头先查一次，没通过直接返回、一个字节都不动）；</description></item>
    /// <item><description>调用方已经查过"未取消"（<c>PostProcessSuccessAsync</c> 在动源包之前单独再查一次令牌）；</description></item>
    /// <item><description>要删的清单**只来自任务自身**（分卷组 = 整组各卷，单文件 = 它自己）；</description></item>
    /// <item><description>**不扫目录、不递归、不删目录**；</description></item>
    /// <item><description>单个文件删不掉（占用 / 只读 / 权限）只记失败并继续，绝不影响内容物的结论。</description></item>
    /// </list>
    ///
    /// <para>删成功（或本来就没什么可删的）之后把 <see cref="ArchiveTask.SourcePackageMove"/> 落成
    /// <see cref="SourcePackageMoveState.Done"/>：链尾那次"补搬"因此会跳过它 ——
    /// ⛔ 同一个源包绝不允许既被删又被搬（那会凭空报一堆"源包不存在"的假失败）。
    /// 对续解层的内层包来说，这一步同时让链尾的 `CollectChainInnerPackagesIntoRestAsync`
    /// 按"文件不在了"跳过它（那条路本来就只搬**还在盘上**的）；普通档逐层回收时链尾**整段都不再调它**。</para>
    ///
    /// <para>⚠ <b>2026-10-15：<c>toRest</c>（把这一层的过程物集中进其余物）整条删除</b> ——
    /// 它只服务③页「删除操作 = 不动其余物」那一档，而新口径下**成功链的过程物一律当场永久删**
    /// （见 <c>PrepareRestHandlingForBatch</c>）⇒ 那条支路与它的执行体 <c>ReclaimLayerFilesToRest</c>、
    /// 文案 <c>StatusText.RecursionLayerKeepTriggerLabel</c> 一并删除（用户："老代码就是要删掉的"）。</para>
    /// </summary>
    /// <returns>失败时返回一句"内容物已好、源包没删掉"的说明（调用方据此标「部分完成」）；正常返回 null。</returns>

    internal string? PurgeLayerSourcePackage(
        ArchiveTask task,
        OutputVerificationResult? verification,
        StageCommitResult? commit,
        List<(string Level, string Message)> logEntries,
        LayerPurgeTrigger trigger,
        ResultCompletenessVerdict? reclaimVerdict = null,
        bool reclaimRootSource = true,
        string? reclaimLabel = null,
        string? reclaimWorkspaceDirectory = null,
        SourcePackageDonePolicy donePolicy = SourcePackageDonePolicy.Respect)
    {
        if (task == null)
        {
            return null;
        }

        string triggerLabel = DescribeLayerPurgeTrigger(trigger);

        /*
         * 空间不足模式删源前先清掉这一组的 group-entry 硬链接别名：
         * 只删源路径、别名还在时数据不会释放，下一单会误判“空间没回来”。
         * 失败/缺卷不会走到这里；清理失败也由 SourceCleanupService 的别名检查兜底。
         */
        IReadOnlyList<string>? groupSourcePaths = null;

        /*
         * 第二大步的 owner 任务不一定带 IsVolumeGroup（真机形状：入口任务 .001
         * 也可能只是普通任务，后面两片才是 follower）⇒ 判据用“这一单有没有组收片账”，
         * 不能用 IsVolumeGroup 代替。
         */
        if (trigger == LayerPurgeTrigger.SpaceTight
            && _host.VolumeStepPreflight.GroupWork.TryGetValue(task, out VolumeStepPreflightHandler.VolumeStepGroupWork? groupWork))
        {
            /*
             * 第二大步新组的源片清单在 VolumeStepGroupWork.Pieces 里；
             * task.VolumePaths 可能只装入口那一份（旧归组服务才全量写）。
             * 在 CleanupVolumeStepGroupEntry 摘掉这份账之前先保住整组清单。
             */
            groupSourcePaths = groupWork.Pieces;
            _host.VolumeStepPreflight.CleanupVolumeStepGroupEntry(task);
        }

        /*
         * 日志措辞按"这一层的源包是什么"分开说：最外层是用户拖进来的那个包，
         * 续解层是上一层解出来的内层包（对用户来说是过程物，但对这一层就是它的源）。
         * 一句"源包"盖两种东西，真机上就会有人问"其余物里的内层包怎么也叫源包"。
         *
         * ⚠ 递归路（<paramref name="reclaimLabel"/> 非空）更要说清：那里的"这一层"是**工作区的一层**
         * （不是任务），说"源包"会让人以为在动用户拖进来的那个包。所以说法由调用方给（唯一出口 =
         * <c>RecursiveExtractor.DescribeProbeLabel</c>，如「第 1 层：level2.7z」）。
         */
        string subject = reclaimLabel != null
            ? "内层包（上一层的输入）"
            : task.IsContinuationTask ? "内层包（这一层的源）" : "源包";

        /*
         * ===== 递归路：门槛由调用方按**每一层**喂进来，本方法不再看"定稿 / 校验"这两件任务级事实 =====
         *
         * 递归路的层是**工作区目录、不是任务**：那条路上没有 `StageCommitResult`（整条链最后才定稿一次），
         * 而"这一层可证完整吗"是**按层**判的（拿这一层的清单逐条核对过，唯一出口
         * `ResultCompletenessClassifier`，见 `RecursiveExtractor.BuildLayerReclaim`）。
         * 所以这一档只认调用方给出的那份三态结论 —— ⛔ 这里不许再造一个"默认放行"的兜底：
         * 传进来的是 `Undeterminable`（默认值）就什么都不做。
         */
        if (reclaimVerdict != null)
        {
            if (!reclaimVerdict.AllowsSourceRemoval)
            {
                logEntries.Add((
                    "WARN",
                    $"{reclaimLabel ?? task.FileName}：{reclaimVerdict.Blocker}，{subject}留在原地（{triggerLabel}也不删）。"));

                return null;
            }
        }
        else
        {
            if (commit == null || !commit.Attempted || commit.FailedCount > 0)
            {
                // 定稿没发生 / 有搬运失败 → 与搬运那一档同一条红线：源包一个字节都不动。
                logEntries.Add(("WARN", $"{task.LogName}：内容物未全部定稿，{subject}留在原地（{triggerLabel}也不删）。"));
                return null;
            }

            /*
             * ⚠ 2026-09-30（检验等级 L4）：判据从"校验通过"**收紧**成"可证完整"（唯一出口 =
             * <see cref="ResultCompletenessClassifier"/>）：判不出完整性时**永久删源包**这一步不做 ——
             * "空间不足"是让用户难受，删错源包是不可逆。
             */
            ResultCompletenessVerdict completeness = ResultCompletenessClassifier.Classify(
                verification,
                task.ManifestExpectation);

            if (!completeness.AllowsSourceRemoval)
            {
                logEntries.Add(("WARN", $"{task.LogName}：{completeness.Blocker}，{subject}留在原地（{triggerLabel}也不删）。"));
                return null;
            }
        }

        if (task.SourcePackageMove == SourcePackageMoveState.Done
            && donePolicy == SourcePackageDonePolicy.Respect)
        {
            // 幂等：已经处理过了（删过 / 搬过），绝不第二次。
            logEntries.Add(("INFO", $"{task.LogName}：{subject}已经处理过（删过或搬过），不再动第二次。"));
            return null;
        }

        /*
         * ⛔ **最外层源包留到链尾**（用户 2026-10-03 的原话："原包还是比较重要的"）——
         * 递归路那一条只允许删**我们自己产出的过程物**（内层包就在递归工作区里），
         * 所以这里上一道显式的保险：调用方说"这一档不碰源包"时，
         * 候选里出现**任何一份不在递归工作区里**的文件就直接停手。
         *
         * 判据用**路径事实**（那一份在不在 `.ArchiveFixer.work` 之下）而不是"它是不是源包"：
         * 递归工作区之外的文件这一档本来就不该碰 —— 源包（用户拖进来的那个）在源目录里，
         * 它**永远不在**工作区之下；而这一档要删的内层包**永远在**工作区之下
         * （`<目标目录>\.ArchiveFixer.work\recursive\<id>\layer-NNN\output\…`）。
         * 判不出 / 拿不到路径 ⇒ 落在"什么都不做"那一档（删除不可恢复）。
         *
         * 为什么不是"没事，反正传进来的清单里没有它"：这是**不可逆**操作，
         * 多一道"值不值得信"的检查比事后解释便宜得多（AGENTS.md §9.5）。
         */
        if (!reclaimRootSource && !WorkspacePathRules.IsInsideRecursionWorkspace(task.CurrentPath))
        {
            logEntries.Add((
                "WARN",
                $"{reclaimLabel ?? task.FileName}：候选里的「{Path.GetFileName(task.CurrentPath)}」不在递归工作区里"
                + "（它可能是用户拖进来的源包）—— 判不出它是不是本层的过程物，所以这一个字节都不删"
                + "（最外层源包只留到链尾处理；删除不可恢复）。"));

            return null;
        }

        /*
         * ===== 闸门：「半套分卷」不许当场删（2026-10-03 独立复核查出的红线级缺口）=====
         *
         * 这一条原本只有其余物那一档（`RestItemPurger.Purge` 的第六道门槛）在过，而本方法走的是
         * `SourceCleanupService`（逐个 `File.Delete`）**不过那道闸门** —— 2026-10-03 把这条删法从
         * "只在空间不足档"扩到"普通档 + 彻底删除档（逐层回收）"之后，风险面跟着变大。
         *
         * §44.2 那次 25 GB 永久消失的形状正是"一组卷被拆在两边、剩下的被整份删掉"：
         * 同组的一片因为名字被改坏而没被认出来、被当**内容物**留在成品目录里，同组其余几片在其余物里
         * ⇒ 把手上这几片删掉，这一组就再也拼不起来。
         *
         * 判据**转调唯一出口** `RestVolumeCompletenessGate.DescribeLayerReclaimBlocker`
         * （⛔ 这里不另写一份判断），候选清单也**就是** `SourceCleanupService` 要删的那一份
         * （同一个出口 `BuildTargetList`）⇒ 判据与实际删除看的是同一份清单。
         *
         * 拦下就**一个字节都不删**（兜底落在"什么都不做"）：这一层的过程物照旧留到链尾按老口径处理，
         * 而链尾那一档本来就会再过一次同一道闸门（`RestItemPurger.Purge` 第六道门槛）。
         */
        /*
         * ⛔ 严格档只给「逐层回收」的内层包（2026-10-18 困难批 `11111111` 运行期定案）：
         * 链尾删**用户源包**那一档（trigger = SourceHandlingDelete 且不是续解任务）传 false ——
         * 源包名可能被改到没有后缀（`aaa-W0-1删除`），而"它是归档"已由**任务成功解出 5 层
         * + 内容物校验通过**证明，不该再要一次名字证明；同组邻居检查（基名 + 族）照旧跑。
         * 判据本体一字不改，默认值 true 保住了逐层回收/空间不足那两档。
         */
        /*
         * 本组源包：组可证完整 + 内容校验通过 ⇒ 半套闸门不再适用。
         */
        bool canSkipHalfVolumeGate = CanSkipHalfVolumeGate(task, reclaimVerdict);

        if (canSkipHalfVolumeGate && reclaimVerdict == null)
        {
            logEntries.Add((
                "INFO",
                $"{task.LogName}：这一组可证完整且内容物校验通过 ⇒ 组源包按「{triggerLabel}」处理时不再过半套闸门（整组已解开，源片不再需要拼组）。"));
        }

        CleanupCandidateKind candidateKind = ClassifyLayerCleanupCandidate(task, trigger);

        string? splitBlocker = canSkipHalfVolumeGate
            ? null
            : DescribeLayerSplitBlocker(
                task,
                reclaimWorkspaceDirectory,
                candidateKind);

        if (splitBlocker != null)
        {
            logEntries.Add(("WARN", $"{task.LogName}：{splitBlocker}"));
            return null;
        }

        _host.VolumeHandoff.RestoreGatheredUserSourcePieces(logEntries);

        /*
         * 删源证据必须是“本链自己”的完整性结论：
         * ① 递归层回收用本层的 ResultCompletenessVerdict；
         * ② 单层/任务收尾用这次 OutputVerifier 的清单逐条核对结果。
         * ⛔ 不能由调用方传 verified:true，也不能只读 Verified 而忽略 ManifestCrossChecked。
         */
        SourceCleanupEvidence evidence = reclaimVerdict?.AllowsSourceRemoval == true
            ? SourceCleanupEvidence.LayerCompleteness
            : (verification?.AllowsSourceRemoval == true
                ? SourceCleanupEvidence.ManifestCrossChecked
                : SourceCleanupEvidence.None);
        string verificationNoteForSourceRemoval = evidence != SourceCleanupEvidence.None
            ? $"{triggerLabel}：内容物可证完整（{evidence}）"
            : $"{triggerLabel}：判不出完整性（未拿清单逐条核对），保留源文件";

        var sourceTargets = SourceCleanupService.BuildTargetList(
            task,
            _host._spaceTightPolicy.Enabled,
            groupSourcePaths);

        var sourcePlan = new DeletionPlan
        {
            Targets = sourceTargets,
            AllowedRoot = Path.GetDirectoryName(task.CurrentPath) ?? string.Empty,
            DeletesEverything = true,
            Cancelled = _host.IsStopping || _host._operationCts?.IsCancellationRequested == true,
            Reason = $"{triggerLabel}：删除源包"
        };

        SourceCleanupResult cleanup = _host.Source.SourcePackageCleanup.Cleanup(
            task,
            new SourceCleanupDecision
            {
                Evidence = evidence,
                Note = verificationNoteForSourceRemoval,
                IncludeRestAliases = _host._spaceTightPolicy.Enabled,
                Plan = sourcePlan
            });

        if (cleanup.DeletedFiles.Count == 0 && cleanup.FailedFiles.Count == 0)
        {
            // 没有可删的路径（分卷清单不完整）→ 如实说，不假装删过了。
            logEntries.Add((
                "WARN",
                $"{task.LogName}：{triggerLabel}要删{subject}，但任务的源包清单是空的 —— {cleanup.Message}"
                + $"｜判据输入：CurrentPath=「{task.CurrentPath}」、OriginalPath=「{task.OriginalPath}」"
                + $"、VolumePaths={task.VolumePaths.Count} 条"));
            return null;
        }

        if (cleanup.FailedFiles.Count > 0)
        {
            logEntries.Add((
                "ERROR",
                $"{task.LogName}：{triggerLabel}删除{subject}有 {cleanup.FailedFiles.Count} 个失败"
                + $"（{string.Join("、", cleanup.FailedFiles.Take(3).Select(path => Path.GetFileName(path)))}）—— {subject}仍在原处。"));

            return $"{triggerLabel}：内容物已好，但{subject}没能全部删除（" + cleanup.Message + $"）；{subject}仍在原处，内容物不受影响";
        }

        foreach (string deletedPath in cleanup.DeletedFiles)
        {
            DiagnosticLog.WriteFileOperation(
                _host._vm.Logger,
                "delete",
                deletedPath,
                target: null,
                result: "deleted",
                detail: $"trigger={trigger}; subject=源包; fate=永久删除");
        }

        foreach (string failedPath in cleanup.FailedFiles)
        {
            DiagnosticLog.WriteFileOperation(
                _host._vm.Logger,
                "delete",
                failedPath,
                target: null,
                result: "failed",
                detail: $"trigger={trigger}; subject=源包; message={cleanup.Message}");
        }

        // 只删文件会把“其余物”留成空壳；这里只收拾已经被清空的那一层。
        foreach (string deletedPath in cleanup.DeletedFiles)
        {
            RemoveEmptyRestShell(deletedPath, logEntries);
        }

        task.SourcePackageMove = SourcePackageMoveState.Done;

        /*
         * 收尾那句按触发点分开说（用户 2026-10-03）：空间不足模式连最外层源包都删了，
         * 而普通档逐层回收**只删内层包**，最外层源包要留到链尾（用户原话："原包还是比较重要的"）。
         * "已立刻永久删除"这个短语两个触发点都保留：既有用例按它点名
         * （ChainSpaceReclaimTests / SpaceTightModeTests），而且它就是这一档最该一眼看到的事实。
         */
        string tail = trigger == LayerPurgeTrigger.LayerReclaim
            ? "（不进回收站、不可恢复；最外层源包留到链尾按「源包操作 + 删除操作」处理）。"
            : "（不进回收站、不可恢复；这份空间马上给后面的包用）。";

        /*
         * 两种说法的分叉只有一个理由：**递归路的"这一层"是工作区的一层、不是任务**
         * （`reclaimLabel` = 「第 1 层：level2.7z」），而且那条路上**没有"定稿"这一步**
         * —— 整条链最后才发布一次，所以那句"定稿 + 校验通过"在递归路上是一句**假话**
         * （用户 2026-10-04 真机为"说话不实"挨过骂）。递归路说的事实是
         * "这一层已经证明可证完整（拿这一层的清单逐条核对过）"。
         * ⛔ 续解那条路一个字都没改（既有用例按"定稿 + 校验通过"与"已立刻永久删除"点名）。
         */
        logEntries.Add((
            "INFO",
            reclaimLabel != null
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    StatusText.RecursionLayerReclaimedFormat,
                    reclaimLabel,
                    cleanup.DeletedFiles.Count,
                    WorkspaceCleanupService.FormatSize(cleanup.FreedBytes))
                : $"{task.LogName}：{triggerLabel} —— 定稿 + 校验通过，已立刻永久删除{subject} "
                  + $"{cleanup.DeletedFiles.Count} 个，收回 {WorkspaceCleanupService.FormatSize(cleanup.FreedBytes)}"
                  + tail));

        return null;
    }

    /// <summary>
    /// 删掉一个文件之后，如果它所在的“其余物”已经被彻底清空，就连空壳一起删。
    /// ⛔ 只删名字逐字是“其余物”的那一层；非空一律不动，不往上删父目录。
    /// </summary>
    private static void RemoveEmptyRestShell(
        string deletedPath,
        List<(string Level, string Message)> logEntries)
    {
        try
        {
            string? directory = Path.GetDirectoryName(deletedPath);

            for (int depth = 0; depth < 4 && !string.IsNullOrWhiteSpace(directory); depth++)
            {
                if (string.Equals(
                        Path.GetFileName(directory),
                        ProcessArtifactLayout.ArtifactDirectoryName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(directory) &&
                        !Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory, recursive: false);
                        logEntries.Add((
                            "INFO",
                            $"其余物已清空，删除空壳目录：{Path.GetFileName(directory)}"
                            + $"（{directory}）"));
                    }

                    break;
                }

                directory = Path.GetDirectoryName(directory);
            }
        }
        catch
        {
            // 空壳收拾失败不影响已完成的删除结论；下次运行还会再收拾。
        }
    }


    /// <summary>
    /// 这一份源包的内容证据是否**可证不是分卷成员**（只有这一档才让“半套分卷”闸门不适用）。
    /// 文件读不出 / 没有内容证据 ⇒ false（照旧过闸门，⛔ 不因判不出而放宽）。
    /// </summary>
    private static bool IsProvablyNotVolumeMemberSource(ArchiveTask task)
    {
        if (task == null)
        {
            return false;
        }

        foreach (string? path in new[] { task.CurrentPath, task.OriginalPath })
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            try
            {
                return !FileNature.Inspect(path).ParticipatesInVolumeHandling;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    /// 这一层准备删的那一份是不是**半套分卷**（同组还有一片留在成品目录树里）？
    /// 返回非 null = **拦下的理由**（调用方写一行 WARN、一个字节都不删）。
    ///
    /// <para>判据**转调唯一出口** <see cref="RestVolumeCompletenessGate.DescribeLayerReclaimBlocker"/>
    /// （⛔ 不在这里另写一份）；候选清单也转调删除侧那一个出口
    /// <see cref="SourceCleanupService.BuildTargetList"/>（判据与真删看同一份清单）。</para>
    /// </summary>
    /// <summary>
    /// 待删除对象的身份。身份决定“要不要证明它是归档/分卷”：
    /// 只有真正的分卷组才要求名字/内容能认出归档；独立续解内层包和用户源包按更直接的事实处理。
    /// </summary>
    private enum CleanupCandidateKind
    {
        UserSource,
        ProducedInnerPackage,
        VolumeGroup
    }

    private static CleanupCandidateKind ClassifyLayerCleanupCandidate(
        ArchiveTask task,
        LayerPurgeTrigger trigger)
    {
        if (task.IsVolumeGroup || task.IsVolumeGroupFollower)
        {
            return CleanupCandidateKind.VolumeGroup;
        }

        if (trigger == LayerPurgeTrigger.SourceHandlingDelete && !task.IsContinuationTask)
        {
            return CleanupCandidateKind.UserSource;
        }

        return CleanupCandidateKind.ProducedInnerPackage;
    }

    /// <summary>
    /// 这一份源包/过程物是否已经属于“组可证完整 + 内容校验通过”的组，
    /// 因而可以跳过“半套分卷”闸门。事实只来自任务终态账与回传 verdict。
    /// </summary>
    private static bool CanSkipHalfVolumeGate(
        ArchiveTask task,
        ResultCompletenessVerdict? reclaimVerdict)
    {
        if (task.IsVolumeGroup
            && task.IsVolumeComplete
            && ResultCompletenessClassifier.Classify(task).AllowsSourceRemoval)
        {
            return true;
        }

        /*
         * 链尾组源包结算：调用方已经把**组级结论**作为 reclaimVerdict 传进来
         * （组齐全 + owner 可证完整 + 内容校验通过）。这一份结论覆盖的不只是 owner 自己，
         * 还包括为这一组产出片的包装单（111-1/111-2）；它们不一定被标成
         * IsVolumeGroupFollower，但它们的片已经全部被这个已验证组消费，半套分卷闸门不再适用。
         * 只有组结论为允许删除时才走这里；其它没有组结论的路径仍按原判据。
         */
        return reclaimVerdict != null
               && reclaimVerdict.AllowsSourceRemoval;
    }

    private string? DescribeLayerSplitBlocker(
        ArchiveTask task,
        string? reclaimWorkspaceDirectory = null,
        CleanupCandidateKind candidateKind = CleanupCandidateKind.VolumeGroup)
    {
        /*
         * 内容卡已经**可证不是分卷成员**（SingleComplete / ProvablyNotVolumeMember）⇒ 它不可能是
         * 半套分卷的一半，这道闸门根本不适用。真机 960 批 `27272727`：源包是一份完整 ZIP，
         * 成品目录里恰好有同基名的内层分卷片 `aaa-L2.中文z02`，旧判据只按基名+族就把它拦下，
         * 导致 DeleteInPlace 该删的源包没删。判据只读内容卡，⛔ 不按名字猜。
         */
        if (IsProvablyNotVolumeMemberSource(task))
        {
            return null;
        }

        List<string> candidates = SourceCleanupService.BuildTargetList(task);

        if (candidates.Count == 0)
        {
            return null;   // 没有可删的路径（分卷清单不完整）⇒ 上游那条"清单是空的"会如实说
        }

        IReadOnlyList<string> artifactRoots = _host.Source.EnumerateSplitGateArtifactRoots(task, candidates);

        /*
         * ⛔ 2026-10-03 第二轮复核：**一棵能扫的树都取不到 = 判不出 ⇒ 拦下**
         * （兜底一律落在"什么都不做"；原来这里会直接放行）。
         */
        if (artifactRoots.Count == 0)
        {
            return "拿不到成品目录树（任务上没有记下可用的输出目录）—— 判不出同组还有没有别的片留在里面，"
                + "所以这一个字节都不删（删除不可恢复）";
        }

        /*
         * 递归路要排掉**本次递归的工作区**（见 DescribeLayerReclaimBlocker 的 excludedPaths 说明）：
         * 那一棵树是程序的暂存区、收尾整份删掉，里面的东西不是"成品目录里留下的另一片"。
         * ⛔ 只有递归路传得进来；续解那条路（默认 null）行为一个字没变。
         */
        IReadOnlyList<string>? excluded = string.IsNullOrWhiteSpace(reclaimWorkspaceDirectory)
            ? null
            : new[] { reclaimWorkspaceDirectory! };

        foreach (string artifactRoot in artifactRoots)
        {
            string? blocker = RestVolumeCompletenessGate.DescribeLayerReclaimBlocker(
                candidates,
                artifactRoot,
                excluded,
                candidateKind == CleanupCandidateKind.VolumeGroup);

            if (blocker != null)
            {
                return blocker;
            }
        }

        return null;
    }
}


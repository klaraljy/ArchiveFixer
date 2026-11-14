using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
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
using System.Windows;


namespace ArchiveFixer.ViewModels
{
    /// <summary>缺卷不在批首判死：跑完再补判一次 + 链尾收口（第 10 块，从 Password 大杂烩搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        internal async Task<IReadOnlyList<ArchiveTask>> FinalizeDeferredVolumeDeficitsAsync(CancellationToken cancellationToken = default)
        {

            /*
             * ⚠ 2026-10-08：**这一站才是"真缺卷"下结论的地方** ⇒ 先把进来时那份名单快照下来，
             * 补判跑完之后把已经离开名单的那些单的"待判"事实位清掉（它们要么被补判判出了真结论、
             * 要么已经被放行去跑）。仍在名单里的（例如"消费方还没跑完"那一支）保持"待判"。
             */
            ArchiveTask[] pending = _volumeDeficitDeferred.ToArray();

            /*
             * ⚠ 2026-10-09 **试做后已撤回**（如实记账）：本想在这里加一趟"最后一轮分卷操作"——
             * 对全表每一单再跑一次 `AdoptPublishedVolumePieces`，把"某一组还缺的那一片"接进那一层，
             * 以解掉真机 EEEE 的时序竞争（`111(3).rar` 收尾 22:44:30 早于入口 `111.zip` 落地 22:44:32
             * ⇒ 那一次接片是空操作，`111(3)\111(3)\111.z02` 一直没被接走）。
             *
             * ⛔ 实做第一步就打红：`SiblingFolderVolumeGatherTests.真机形状_过路层那一单的其余物按档删掉_48MB那处`
             * ③"盘上收口只留成品" ⇒ 留下孤儿 `111_outer\111.z01` —— 全表扫会把片接进**产出方自己的落点层**，
             * 而收尾的删除档不认那个新名字。⇒ 要做的不是"再扫一遍全表"，而是
             * **接片的目标层与收尾的清理口径必须一致**（唯一出口仍是 `ResolveBatchGroupDirectory` +
             * `PublishUnresolvedPieceLandingCopies`）。撤回后该用例恢复绿。
             */
            List<ArchiveTask> chainEndCompleted = await RecheckDeferredVolumeDeficitsAsync(
                finalPass: true,
                cancellationToken).ConfigureAwait(false);

            foreach (ArchiveTask task in chainEndCompleted)
            {
                AppendLog(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.VolumeDeficitChainEndCompleteLogFormat,
                        task.FileName));

                /*
                 * ⚠ 2026-10-09 **B1：链尾给它们一个落定结论**（原话：「链尾 toRun 落结论、Pending 收口」）。
                 *
                 * 改之前这里**只打一行日志**，那几单的机器终态一直停在 `Pending`、状态停在「等待解压」
                 * ⇒ 批末汇总按终态把它们算进「未处理」，而它其实"这一批没轮到"（原因很具体：
                 * 整组是在链尾这一趟才凑齐的，而这一批的轮次已经跑完了）。
                 *
                 * ⛔ **这不是"放宽批中间不落结论"那条判据**：判据管的是**批中间**，而走到这一行时
                 * 整条链已经跑完 —— 就地落结论正是「分卷和其他未识别的东西统统跳过，到了最后再重新
                 * 检验一步」那条口径的最后一格。
                 *
                 * ⛔ 结论**不许写成缺卷 / 跟班卷**：这一组的完整性刚在这里被判成"齐了"，
                 * 写"缺卷"是假话；内容也确实没有任何一单解过（解过的话上面
                 * `TryResolveConsumedByAnotherTask` 早就把它按跟班卷收场了）。⇒ 如实写
                 * 「这一批没轮到它」，并且**明确告诉用户再点一次就会解**。
                 */
                task.Outcome = TaskOutcome.Skipped;
                task.MarkSkipped(
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.VolumeDeficitChainEndNotRunText,
                        task.FileName));
            }

            foreach (ArchiveTask task in pending)
            {
                if (!_volumeDeficitDeferred.Contains(task))
                {
                    task.IsVolumeDeficitDeferred = false;
                }
            }

            /*
             * ⚠ 2026-10-08（EEEE 真机）：上面这一趟是**整批跑完之后**才改状态的那一处 ——
             * 导出的日志若在这里就结束，"①页每一行显示什么"那份快照会停在批首的中性档
             * （真机那两行写着「等待解压」，用户据此判定"显示不对"）。⇒ 补一份定稿后的快照。
             */
            AppendTaskRowSnapshot();

            /*
             * ⚠ 返回"链尾这一趟才凑齐、但这一批已经没有机会再跑"的那几单：调用方（一键档）拿它写汇总，
             * 测试拿它断言"一单都不许留在 `Pending`"。⛔ 它们的状态已经在上面的循环里落定。
             */
            return chainEndCompleted;
        }

        /// <summary>
        /// 这一单是不是"缺卷留到最后再判"名单里那一单（**事实位**，供一键档那道守卫用）。
        ///
        /// <para>⛔ 只读这份名单本身：不看状态、不看终态、不重推判据 —— 名单的写入点唯一
        /// （<see cref="RecordDeferredVolumeDeficit"/>）。</para>
        /// </summary>
        internal bool IsDeferredVolumeDeficit(ArchiveTask? task) =>
            task != null && _volumeDeficitDeferred.Contains(task);


        /// <summary>
        /// 最后一轮仍凑不齐：如实报缺卷（不变量 7：从头到尾没有"带着缺卷开始解"这回事），
        /// 再按盘上事实决定终态（不完整 ⇒ 部分完成，完整却解不出 ⇒ 由既有失败收口判失败）。
        /// </summary>
        internal void MarkStillMissingAtFinalPass(ArchiveTask task, string missing)
        {
            // ③ 到这一步才如实报缺卷（不变量 7：从头到尾没有"带着缺卷开始解"这回事）。
            string diagnosis = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.VolumeDeficitFinalDiagnosisFormat,
                task.FileName,
                missing);

            AppendLog(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.VolumeDeficitRecheckStillFormat,
                    task.FileName,
                    missing));

            /*
             * ⚠ 2026-10-06 试过一版"批末这一档也改成「部分完成」+ WARN"，**已撤回**：
             * 全量当场 6 条红线用例变红（`对照_一直补不上_批末才如实报缺卷`、
             * `PipelineWiringTests.缺卷_真案一的现场_批末补判才拦下并点名缺第2卷` 等）——
             * **"缺卷不开始解 + 如实报缺卷"是用户早先拍过并有用例钉住的口径**，
             * 真正该改的是"内容已被接手的那些单不该走到这一支"（见下面那行排障）。
             */
            MarkStoppedBeforeExtract(task, StatusText.VolumeMissing, diagnosis, TaskReason.VolumeMissing);
            AppendLog("ERROR", $"分卷缺失，未开始解压：{task.FileName}，{diagnosis}");

            /*
             * ===== 到最后一刻仍然不完整 ⇒ **部分完成**，⛔ 不是失败（用户 2026-10-06）=====
             *
             * 用户原话：「假如，1_最后剩一个 `111.7z.001` 你不能说是解压失败了，这应该是部分完成，
             * 因为这个分卷不完整，到了最后检测不到完整的；2_如果最后完整了，但是解压不出来，那才是失败了」。
             *
             * 判据只读**盘上事实**（`VolumeGroupDetector` 的结论）：这一组到底完不完整 ——
             * · 不完整（缺片）⇒ `PartiallyCompleted`：我们**根本没拿到全部数据**，不是"解压失败"；
             * · 完整了却解不出来 ⇒ 走既有的失败收口 ⇒ `Failed`（那才是真失败）。
             * ⛔ 「源包一个字节都不动」这条红线一个字不改（下面既有的收尾照旧）。
             */
            var leftoverCandidates = new List<VolumeCandidate>();

            foreach (string path in task.VolumePaths)
            {
                if (File.Exists(path))
                {
                    leftoverCandidates.Add(new VolumeCandidate { Path = path });
                }
            }

            foreach (string path in CollectVolumeFilesInDirectory(
                         Path.GetDirectoryName(task.CurrentPath ?? string.Empty) ?? string.Empty))
            {
                leftoverCandidates.Add(new VolumeCandidate { Path = path });
            }

            bool stillIncomplete = VolumeGroupDetector
                .Group(leftoverCandidates)
                .Any(candidateGroup => !candidateGroup.IsComplete);

            if (stillIncomplete)
            {
                task.Outcome = TaskOutcome.PartiallyCompleted;
                AppendLog(
                    "WARN",
                    $"{task.LogName}：这一组到最后一刻仍然不完整（缺 {missing}）—— 按「部分完成」记，⛔ 不是解压失败。");
            }

            // 机器终态收口：这一单是在**任务收尾之后**才落的状态，走不到单任务那个 finally ⇒ 这里补。
            FinalizeOutcomeIfPending(task);

            /*
             * ⚠ 2026-10-08：离开"缺卷待批末判"名单的**那一刻**就要把显示/统计事实位清掉
             * （手动档不走链尾 `FinalizeDeferredVolumeDeficits`，只靠那一处会漏 ⇒ 真机表现是
             * 诊断里仍按"跳过"分组、拿不到"缺哪几卷 + 下一步：补卷"）。
             */
            task.IsVolumeDeficitDeferred = false;

            _volumeDeficitDeferred.Remove(task);
            _volumeDeficitFinalPass.Add(task);
        }

        /// <summary>
        /// 这一组的内容已经由本批另一个任务解出来 ⇒ 按跟班卷收场（不算"没做成"）。
        /// 位置必须排在"仍缺 ⇒ 如实报缺卷"之前：那一单再报缺卷就是一句把人指错的话。
        /// </summary>
        internal bool TrySettleAsVolumeGroupFollower(ArchiveTask task)
        {
            if (!TryResolveConsumedByAnotherTask(task, out string consumerName))
            {
                return false;
            }

            string consumedNote = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.VolumeDeficitConsumedByOtherTaskFormat,
                task.FileName,
                consumerName);

            /*
             * 跟班卷 = **不算没做成**（唯一事实位 `IsVolumeGroupFollower` ⇒ `CountsTowardBatchOutcome`）：
             * 四处消费点（色带 / 批末诊断 / 一键汇总行 / 本批汇总）读的都是它。
             *
             * ⚠ **机器终态必须显式落 `Skipped`**：`MarkSkipped` 只写 `Status`、不写 `Outcome`
             * ⇒ 汇总会把这些单算成「未处理」。这里与既有那条跳过路逐行同一套：
             * 清校验 / 清落点 + 落终态 + 标跟班。
             */
            task.IsOutputVerified = false;
            task.OutputVerification = OutputVerificationOutcome.NotAttempted;
            task.OutputManifestCrossChecked = false;
            task.OutputOnlyZeroByteEntriesMissing = false;
            task.OutputPath = string.Empty;
            task.Outcome = TaskOutcome.Skipped;
            task.IsVolumeGroupFollower = true;
            task.MarkSkipped(consumedNote);

            AppendLog("INFO", consumedNote);

            // ⚠ 2026-10-08：离开名单 ⇒ 事实位一起清（理由见下面那一处）。
            task.IsVolumeDeficitDeferred = false;

            _volumeDeficitDeferred.Remove(task);
            _volumeDeficitFinalPass.Add(task);

            return true;
        }

        /// <summary>
        /// 族规则判不出"该拼到哪一层"时的两级兜底：① 底层识别卡自证首卷 ⇒ 它所在层就是入口层；
        /// ② 还判不出 ⇒ 读接片那一档真的落地时记下的层（唯一出口 `_groupLayerByBaseName`）。
        /// ⛔ 已经判出层的一个字都不改；两级都判不出 ⇒ 原样返回空串（照旧什么都不做）。
        /// </summary>
        internal string ResolveEntryLayerFallbacks(ArchiveTask task, string entryLayer)
        {
            /*
             * ===== 族规则判不出"该拼到哪一层"时，让**底层识别卡**补一次 =====
             * （用户 2026-10-17 第 4 条：「第一卷竟然还有弄不成的情况，就是识别出入口包的情况」）
             *
             * 只在 `entryLayer` **为空**时补：手上那一份如果自证是**首卷**（`VolumeFirstPiece`，
             * 名字被改到族规则认不出时它是唯一还认得出"我是入口"的证据），那它所在的层就是入口层 ——
             * 引擎就是拿它开始解的。⛔ 已经判出层的一个字都不改；⛔ 卡也判不出 ⇒ 照旧什么都不做。
             */
            if (entryLayer.Length == 0)
            {
                string ownMemberPath = ResolveDeferredGroupMemberPath(task);

                if (!string.IsNullOrWhiteSpace(ownMemberPath) && File.Exists(ownMemberPath))
                {
                    try
                    {
                        CardVolumeGroupReading ownCard =
                            VolumePiecesFromCards.Read(new[] { FileNature.Inspect(ownMemberPath) });

                        if (ownCard.EntryPath.Length > 0)
                        {
                            entryLayer = Path.GetDirectoryName(ownCard.EntryPath) ?? string.Empty;

                            if (entryLayer.Length > 0)
                            {
                                AppendLog(
                                    "INFO",
                                    $"{task.LogName}：这一组该拼到哪一层 —— 族规则判不出，改由底层识别卡指出入口"
                                    + $"（卡上自证首卷=「{FileNameHelper.GetFileName(ownCard.EntryPath)}」）");
                            }
                        }
                    }
                    catch
                    {
                        // 卡读不出来 ⇒ 照旧什么都不做。
                    }
                }
            }

            /*
             * ===== 层判不出 ⇒ 读**接片那一档算过的层**（唯一出口 `_groupLayerByBaseName`）=====
             *
             * <para>2026-10-18 困难批 `30303030` 现场：三片都已按内容规范名 `.001/.002/.003`
             * 接到 `333(2)` 那一层，可批末重判时这一单手上那一份在**已删掉的工作区**里 ⇒
             * 族规则与卡都判不出入口层 ⇒ 逐字 `该拼到哪一层=「」｜这一层不在盘上 ⇒ 判不出完整的一组`
             * ⇒ 整组明明齐了却不开始解压。</para>
             *
             * <para>判据不另造：`_groupLayerByBaseName` 就是接片真的落地那一刻记下的层
             * （`ExtractionCoordinator` 里"层只有一个出口"那条注释指的就是它）。⛔ 只在这一单判不出时用；
             * 目录不存在 ⇒ 照旧空（什么都不做）。</para>
             */
            if (entryLayer.Length == 0)
            {
                string ledgerBaseName = FileNameHelper.GetArchiveBaseName(
                    ResolveDeferredGroupMemberPath(task));

                if (ledgerBaseName.Length > 0
                    && _groupLayerByBaseName.TryGetValue(ledgerBaseName, out string? assembledLayer)
                    && !string.IsNullOrWhiteSpace(assembledLayer)
                    && Directory.Exists(assembledLayer))
                {
                    entryLayer = assembledLayer!;

                    AppendLog(
                        "INFO",
                        $"{task.LogName}：这一组该拼到哪一层 —— 族规则与卡都判不出，"
                        + $"改用接片那一档算过的层「{assembledLayer}」");
                }
            }

            return entryLayer;
        }


        /// <summary>试开那一段的结论（把 9 个被改写的局部量一次交回调用方）。</summary>
        // ===== 搬出后的入口薄转发（实现见独立类）=====
        private ExtractionDeferredVolumeRechecker? _deferredVolumeRechecker;
        private ExtractionDeferredVolumeRechecker DeferredVolumeRechecker =>
            _deferredVolumeRechecker ??= new ExtractionDeferredVolumeRechecker(this);

        private Task<List<ArchiveTask>> RecheckDeferredVolumeDeficitsAsync(bool finalPass, CancellationToken cancellationToken) =>
            DeferredVolumeRechecker.RecheckDeferredVolumeDeficitsAsync(finalPass, cancellationToken);

        private ExtractionOrderTrial? _orderTrial;
        private ExtractionOrderTrial OrderTrial => _orderTrial ??= new ExtractionOrderTrial(this);

        internal Task<OrderTrialOutcome> RunOrderTrialAsync(
            ArchiveTask task,
            string entryLayer,
            VolumeGatherPlan gatherPlan,
            List<string> gatherCandidates,
            string gatherReference,
            CardVolumeGroupReading cardReading,
            string groupResolutionLayer,
            string taskOutputPathBeforeRecheck,
            int gatherScannedInEntryLayer,
            CancellationToken cancellationToken) =>
            OrderTrial.RunOrderTrialAsync(
                task, entryLayer, gatherPlan, gatherCandidates, gatherReference, cardReading,
                groupResolutionLayer, taskOutputPathBeforeRecheck, gatherScannedInEntryLayer, cancellationToken);
    }
}


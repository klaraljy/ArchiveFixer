using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>硬链接试开/内容定序（从 ExtractionCoordinator.DeferredVolumeDeficitStep.cs 搬出）：只读试开、只交顺序；接片仍走 TryGatherGroupPiecesInto。</summary>
internal sealed class ExtractionOrderTrial
{
    private readonly ExtractionCoordinator _host;

    internal ExtractionOrderTrial(ExtractionCoordinator host)
    {
        _host = host;
    }

    /// <summary>
    /// (c) 硬链接试开 / 内容定序：通用分片先按相似度排顺序、真的解一遍验证；
    /// 其余族照旧只按内容/名字给出的那一种顺序列一次目录。
    /// ⛔ 试开只读、只交顺序；接片仍走既有出口 TryGatherGroupPiecesInto。
    /// </summary>
    internal async Task<OrderTrialOutcome> RunOrderTrialAsync(
        ArchiveTask task,
        string entryLayer,
        VolumeGatherPlan gatherPlan,
        List<string> gatherCandidates,
        string gatherReference,
        CardVolumeGroupReading cardReading,
        string groupResolutionLayer,
        string taskOutputPathBeforeRecheck,
        int gatherScannedInEntryLayer,
        CancellationToken cancellationToken)
    {
        ArchiveFixer.Detection.CardVolumeGroupReading? gatherCardEvidence = null;
        bool contentOrderUndetermined = false;
        bool stagedOrderGroup = false;
        IReadOnlyList<string>? confirmedOrderForGroup = null;
        VolumeNamingFamily? confirmedFamilyForGroup = null;
        bool confirmedGroupForTask = false;

        /*
         * ===== (c) 硬链接试开 / 内容定序（用户 2026-10-17 四步里的第三步 + 2026-10-18 顺序口径）=====
         *
         * ① **通用分片**（`X.zip.001..00N`）：中间片内容里没有卷号 ⇒ 先按相似度排高概率顺序、
         *    真的解一遍验证（硬链接试开）；全试不出 ⇒ 判不出（这一趟不按名字硬拼）。
         * ② 其余族（RAR / 7z / 跨盘 ZIP）：卷序由内容或名字明确给出，照旧只按那一种顺序列一次目录。
         *
         * ⛔ 试开是只读的；赢的那一版**只交顺序**，接片仍走既有出口 `TryGatherGroupPiecesInto`
         * （它会记"谁吐出的片、谁借的片"，批末收场才能把产出方那一份按档收干净）。
         */
        _host.AppendLog(
            "INFO",
            $"{task.LogName}：试开前决策 = {gatherPlan.Action}（{gatherPlan.Reason}）"
            + $"｜同族候选 {gatherCandidates.Count} 个");

        ExtractionCoordinator.GroupOrderTrialResult probe = await _host.ProbeGatherGroupAsync(
                gatherPlan.Pieces,
                gatherCandidates,
                entryLayer,
                cardReading.EntryPath,
                gatherReference,
                allowFallback: gatherPlan.Action == VolumeGatherAction.Gather,
                cancellationToken)
            .ConfigureAwait(false);

        if (probe.Failure != null)
        {
            /*
             * ⛔ 试开**只是"这一次先不搬"的闸门，不是对整组的判决**（用户 2026-10-17：
             * 「只是简单的判断一下，不能够形成判决情况，因为这个是第二大步才做的」）。
             * ⇒ 这里只写"这一趟先不收"，把结论留给第二大步；⛔ 不写"整组不动/判不出"。
             *
             * ⚠ 但**内容定序这一档适用却失败**（片数 ≤5、末片可定、引擎全试不出）⇒ 名字那把尺子
             * 也不能再拿来硬拼一个"完整的一组"（那正是 `30303030` 反复假完整的来源）：
             * 记下"判不出"，下面认组那一站据此跳过。
             */
            _host.AppendLog(
                "WARN",
                $"{task.LogName}：这一趟先不收（硬链接试开没通过，留到第二大步再判）—— {probe.Failure}");

            gatherPlan = VolumeGatherPlan.Leave("这一趟先不收（试开没通过，留到第二大步再判）");
            contentOrderUndetermined = probe.Applicable;
        }
        else if (probe.Confirmed)
        {
            confirmedOrderForGroup = probe.OrderedPieces;
            confirmedFamilyForGroup = probe.Family;
            confirmedGroupForTask = true;
            /*
             * 试开真的解开了这个顺序 ⇒ 先把它按规范卷名落进一个**全新的硬链接暂存目录**，
             * 用暂存目录当组入口（2026-10-18 新批 `24242424`）：原入口层里可能已经有早先建的
             * `.z01/.z02` 旧名字，与内容定出的新槽位撞名；而那些旧名字同时被组任务写进
             * `VolumePaths`，受"用户源"红线保护，原地删不得。暂存目录是全新的，不撞任何名字，
             * 也不碰用户/任务源；接片仍只建零字节硬链接。
             */
            ArchiveFixer.Detection.CardVolumeGroupReading? stagedReading =
                VolumeOrderMaterializer.TryStageConfirmedOrder(
                    entryLayer,
                    probe.OrderedPieces,
                    probe.Stem,
                    probe.Family,
                    out string stagingDirectory,
                    out string stageFailure);

            if (stagedReading != null)
            {
                cardReading = stagedReading;
                gatherCardEvidence = stagedReading;
                groupResolutionLayer = stagingDirectory;
                stagedOrderGroup = true;
                _host._confirmedOrderStagingDirectories.Add(stagingDirectory);
                _host._stagedGroupOutputPaths[task] = taskOutputPathBeforeRecheck;

                /*
                 * 这一组用的那几片里，来自**包装壳产出**的（`333-W1-0\333-L2.pdf`）
                 * 要记进"产出方那一份已经落地的片"那本账 —— 批末收场才会把包装壳的源包
                 * 与其产出片按档收干净（⛔ 用户源片由 `IsAnyTaskSourcePath` 自动排除）。
                 */
                foreach (string orderedPiece in probe.OrderedPieces)
                {
                    _host.RememberAdoptedLandedPiece(
                        orderedPiece,
                        task,
                        probe.Stem,
                        renamedInPlace: false);
                }

                gatherPlan = new ArchiveFixer.Extraction.VolumeGatherPlan(
                    VolumeGatherAction.Gather,
                    "内容试开定序成立 ⇒ 用硬链接暂存目录当组入口",
                    probe.OrderedPieces);

                _host.AppendLog(
                    "INFO",
                    $"{task.LogName}：{probe.Reason}；已按规范卷名接进硬链接暂存目录"
                    + $"「{Path.GetFileName(stagingDirectory)}」，原入口层一个名字都不动");
            }
            else
            {
                _host.AppendLog(
                    "WARN",
                    $"{task.LogName}：硬链接暂存目录没建成（{stageFailure}）⇒ 退回入口层接片老路");

                ArchiveFixer.Detection.CardVolumeGroupReading? confirmedReading =
                    VolumeOrderMaterializer.BuildConfirmedReading(
                        probe.OrderedPieces,
                        probe.Stem,
                        probe.Family);

                if (confirmedReading == null)
                {
                    _host.AppendLog(
                        "WARN",
                        $"{task.LogName}：这一趟先不收（内容试开定序成立了，但折不出可用的内容读取）—— 整组不动");

                    gatherPlan = VolumeGatherPlan.Leave("这一趟先不收（内容读取折不出来）");
                    contentOrderUndetermined = true;
                }
                else
                {
                    cardReading = confirmedReading;
                    gatherCardEvidence = confirmedReading;

                    gatherPlan = new ArchiveFixer.Extraction.VolumeGatherPlan(
                        VolumeGatherAction.Gather,
                        "内容试开定序成立 ⇒ 按赢的那一版接进入口层",
                        probe.OrderedPieces);

                    _host.AppendLog("INFO", $"{task.LogName}：{probe.Reason}");
                }
            }
        }

        _host.AppendLog(
            "INFO",
            $"{task.LogName}：收拢决策 = {gatherPlan.Action} —— {gatherPlan.Reason}"
            + $"（这一层同族候选 {gatherCandidates.Count} 个｜这一层文件 {gatherScannedInEntryLayer} 个"
            + $"｜参照名=「{(gatherReference.Length == 0 ? "判不出" : gatherReference)}」）");

        /*
         * ⛔ **名字归不出组、但底层识别卡可证完整 ⇒ 允许搬**（用户 2026-10-17 总则：底层优先）。
         * 现场形状：片名被改烂到 `VolumeGroupDetector.Group` 一个组都归不出来，
         * 而卡上"首卷自述 + 体积规律 + 合计对上"三条全成立 —— 这时名字只是线索坏了，
         * 事实（整组齐）是成立的。搬运本体仍走 `TryGatherGroupPiecesInto`（它自己的
         * 卡证据收源 + 那几道闸门一条不落），这里只负责**放行**。
         */
        bool allowMoveIntoGroup = gatherPlan.Action == VolumeGatherAction.Gather;

        if (!allowMoveIntoGroup
            && cardReading.Complete
            && cardReading.EntryPath.Length > 0
            && File.Exists(cardReading.EntryPath))
        {
            allowMoveIntoGroup = true;

            _host.AppendLog(
                "INFO",
                $"{task.LogName}：收拢决策 = Gather（名字归不出组，但底层识别卡可证完整）—— {cardReading.Reason}");
        }

        if (!stagedOrderGroup)
        {
            /*
             * ⚠ 2026-10-13（第 15 修之后）：收片只认名字 ⇒ 名字被伪装到"读不出卷号"的那几片收不进来。
             * 再走一步**字节数**那条硬证据（7z 起始头自述）把缺口按规范槽位名**硬链接**补上
             * （⛔ 原件一个字节都不动；凑不出正好相等就一份都不补）。
             */
            _host.TryLinkMissingPiecesByByteBudget(
                task,
                entryLayer,
                _host._deferredVolumeGroupMember.TryGetValue(task, out string? stalledForBudget) ? stalledForBudget : null,
                cardEntryPath: cardReading.EntryPath);
        }

        return new OrderTrialOutcome(
            gatherPlan,
            cardReading,
            groupResolutionLayer,
            gatherCardEvidence,
            contentOrderUndetermined,
            stagedOrderGroup,
            confirmedOrderForGroup,
            confirmedFamilyForGroup,
            confirmedGroupForTask);
    }
}


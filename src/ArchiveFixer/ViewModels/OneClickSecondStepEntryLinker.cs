using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 第二大步 B 段的**点名入口任务**（只解一层，⛔ 不搬片）：组 → 入口任务的注册/事实位，
    /// 以及由 C 段后续建链接。
    ///
    /// <para>为什么单独成类：这一段只读组事实与任务表、只写任务事实位；与"这一轮怎么解"无关，
    /// 且它是第二大步里唯一会创建/登记入口任务的地方，独立出来才好逐条对账。</para>
    /// </summary>
    internal sealed class OneClickSecondStepEntryLinker
    {
        private readonly MainViewModel _vm;
        private readonly ExtractionCoordinator _extractionCoordinator;
        private readonly OneClickBatchFinalizer _batchFinalizer;

        internal OneClickSecondStepEntryLinker(
            MainViewModel vm,
            ExtractionCoordinator extractionCoordinator,
            OneClickBatchFinalizer batchFinalizer)
        {
            _vm = vm;
            _extractionCoordinator = extractionCoordinator;
            _batchFinalizer = batchFinalizer;
        }

        private ObservableCollection<ArchiveTask> Tasks => _vm.Tasks;

        private void AppendLog(string level, string message) => _vm.AppendLog(level, message);

        /// <summary>给每个可证完整的交接组点名入口任务（只解一层，⛔ 不搬片）。</summary>
        internal async Task RunNamingAsync(
            OneClickRoundState roundState,
            IReadOnlyList<VolumeCandidateDiscovery.VolumeCandidate> handoffGroups)
        {
            /*
             * ===== 交界处消费端 · 第二段（B）：点名入口任务（只解一层，⛔ 不搬片）=====
             *
             * 可证完整的交接组 ⇒ 在任务表里找到入口那一份，清掉它上一轮「已跳过」的结论，
             * 登记第二大步事实位（`_volumeStepTasks` ⇒ 只解一层），并把它排进下一轮。
             * ⛔ 这一版不搬片、不改名、不动盘；片进不进入口层由 C 段再做。
             */
            List<ArchiveTask> volumeEntryTasks = roundState.VolumeEntryTasks;
            List<InnerArchiveCandidate> secondStepEntryCandidates = roundState.SecondStepEntryCandidates;
            var secondStepEntryGroups = roundState.SecondStepEntryGroups;

            /*
             * 标准名前置快通道的控制名单：只收“同目录全部标准名、且同基名没有
             * 标准片散在别处”的组。Incomplete 不在这里，交回正常第二大步。
             * ⛔ 只影响控制路径，不写内容卡事实。
             */
            var fastReadyEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fastReadyFamilies = new Dictionary<string, VolumeNamingFamily?>(StringComparer.OrdinalIgnoreCase);

            foreach (ArchiveFixer.Extraction.StandardNameFastPath.Decision decision
                     in ArchiveFixer.Extraction.StandardNameFastPath.Decide(
                         _extractionCoordinator.FirstStepLedger
                             .AllSecondStepCandidates()
                             .Select(entry => entry.Path)))
            {
                if (decision.Kind == ArchiveFixer.Extraction.StandardNameFastPath.DecisionKind.Ready)
                {
                    fastReadyEntries.Add(decision.Group.EntryPath);
                    fastReadyFamilies[decision.Group.EntryPath] =
                        ArchiveFixer.Extraction.StandardNameFastPath.ToNamingFamily(decision.Group.Family);
                    AppendLog(
                        "INFO",
                        $"[第二大步·标准名快通道] {decision.Group.BaseName}：{decision.Reason}"
                        + $"｜入口={FileNameHelper.GetFileName(decision.Group.EntryPath)}");
                }
                else
                {
                    AppendLog(
                        "INFO",
                        $"[第二大步·标准名快通道] {decision.Group.BaseName}：{decision.Kind}（{decision.Reason}）⇒ 交回正常第二大步");
                }
            }

            foreach (VolumeCandidateDiscovery.VolumeCandidate group in handoffGroups)
            {
                /*
                 * ⛔ 这里不再用候选 Verdict 当闸门（那只是交界处的“建议”）：
                 * 已接线的族由族算法判完整性；未接线的族仍要求候选 Complete（在下面兜住）。
                 */
                if (string.IsNullOrWhiteSpace(group.EntryPath))
                {
                    continue;
                }

                /*
                 * 族的**入口片**才是 owner。先按组事实建 Unit（owner 先空），
                 * 由族算法给出规范名计划里的 IsEntry 片（RAR=第 1 卷、跨盘 ZIP=末片 .zip、
                 * 通用分片/7z=第 1 片）；⛔ 不再“任务表里哪一片先出现就拿哪一片当 owner”
                 * （真机 6666：owner 被点成了第 2 片 `.=Z002`，引擎打不开）。
                 */
                string entryLayer = Path.GetDirectoryName(group.EntryPath) ?? string.Empty;

                if (entryLayer.Length == 0)
                {
                    continue;
                }

                /*
                 * 入口包已经不在盘上，而同层同基名有一个成功任务（2222 批的三个「风景01」
                 * 都是这种：任务名是 .mp4，组入口是已经清掉的 .7z.001）⇒ 这一组已收口，
                 * 不再进 TryCommit（否则会先打一条 prepared 组建组被拒的 WARN）。
                 * 只在“入口确实不存在”时走，入口还在就不许猜。
                 */
                string earlyEntryPath = group.PreparedGroup?.OriginalEntryPath
                    ?? group.OriginalEntryPath;

                if (!string.IsNullOrWhiteSpace(earlyEntryPath)
                    && !File.Exists(earlyEntryPath)
                    && _batchFinalizer.FindSucceededTaskInLayer(entryLayer, group.BaseName) != null)
                {
                    AppendLog(
                        "INFO",
                        $"[第二大步·接住] {group.BaseName}：入口包已不在盘上，但同层同基名任务已完成 ⇒ 已收口，不重复建组。");
                    continue;
                }

                bool standardNameFastPath = fastReadyEntries.Contains(group.EntryPath)
                    && ArchiveFixer.Extraction.StandardNameFastPath.Find(group.Pieces)
                        .Any(standard => standard.Pieces.Count == group.Pieces.Count);

                VolumeNamingFamily? fastFamily = standardNameFastPath
                    && fastReadyFamilies.TryGetValue(group.EntryPath, out VolumeNamingFamily? familyFromName)
                        ? familyFromName
                        : null;

                ArchiveFixer.Extraction.VolumeGroupUnit? preUnit =
                    _extractionCoordinator.TryCommitVolumeGroup(
                        group,
                        null,
                        entryLayer,
                        standardNameFastPath: standardNameFastPath,
                        standardFamily: fastFamily);

                if (preUnit != null
                    && preUnit.State == ArchiveFixer.Extraction.VolumeStep.VolumeGroupState.Missing)
                {
                    /*
                     * 组事实层落最终组失败：不点名、不链接、不解压、不删源包。
                     * 缺片不是第一大步的交接跳过，而是第二大步最终组失败。
                     */
                    _extractionCoordinator.RecordFinalGroupFailure(
                        preUnit,
                        FinalGroupFailureKind.Incomplete,
                        $"最终内容分卷组不完整：{preUnit.Verdict}",
                        Tasks);

                    AppendLog(
                        "WARN",
                        $"[第二大步·组] {group.BaseName}：Missing ⇒ Failed / FinalGroupIncomplete（不链接、不解压，源包不动）。");
                    continue;
                }

                if (preUnit != null
                    && (preUnit.State == ArchiveFixer.Extraction.VolumeStep.VolumeGroupState.Unknown
                        || preUnit.State == ArchiveFixer.Extraction.VolumeStep.VolumeGroupState.SizeAnomaly))
                {
                    if (preUnit.State == ArchiveFixer.Extraction.VolumeStep.VolumeGroupState.SizeAnomaly
                        && preUnit.MissingNumbers.Count > 0)
                    {
                        _extractionCoordinator.RecordFinalGroupFailure(
                            preUnit,
                            FinalGroupFailureKind.Incomplete,
                            $"最终内容分卷组结构异常且缺片：{preUnit.Verdict}",
                            Tasks);
                    }

                    AppendLog(
                        "INFO",
                        $"[第二大步·组] {group.BaseName}：{preUnit.State} ⇒ 不点名、不链接（{preUnit.Verdict}）。");
                    continue;
                }

                // 引擎读临时标准名；任务表/落点/source 处理只认原始入口。
                string entryPath = preUnit?.NamePlan?.Items.FirstOrDefault(item => item.IsEntry)?.PiecePath
                    ?? group.EntryPath;

                string originalEntryPath = group.PreparedGroup?.OriginalEntryPath
                    ?? group.OriginalEntryPath;

                if (string.IsNullOrWhiteSpace(originalEntryPath))
                {
                    originalEntryPath = entryPath;
                }

                ArchiveTask? entryTask = null;

                foreach (ArchiveTask candidate in Tasks)
                {
                    if (candidate != null
                        && SafePathHelper.PathEquals(candidate.CurrentPath, originalEntryPath))
                    {
                        entryTask = candidate;
                        break;
                    }
                }

                if (entryTask == null)
                {
                    /*
                     * 入口任务还没建（典型：这一组的三片是上一轮刚产出的，扫描只把它们当“分卷片”
                     * 记了交接事实，还没进任务表）。⇒ 用**组事实**把入口片按正常“加任务”那条路
                     * 补进表；下一轮由第二大步点名为 owner。
                     * ⛔ 不在这里解压、不搬片、不改名；⛔ owner 只补写一次。
                     */
                    if (preUnit != null
                        && (preUnit.State == ArchiveFixer.Extraction.VolumeStep.VolumeGroupState.Complete
                            || preUnit.State == ArchiveFixer.Extraction.VolumeStep.VolumeGroupState.OwnerAssigned)
                        && File.Exists(originalEntryPath))
                    {
                        /*
                         * ⛔ 父目录必须取**这一片自己所在的那一层**，不能取组分析入口那一层的目录：
                         * 同一基名可能同时有“原包树那份”和“产出层那份”，组分析的入口可能是原包树那份，
                         * 而这一片（如 `.0Bs401`/`111-L2.zip`）实际在产出层 —— 取错层就把这一单的
                         * OutputPath 写进原包树（真机 8888：内容全部落到原包树、判据树里没有）。
                         */
                        string entryTaskLayer =
                            Path.GetDirectoryName(originalEntryPath) ?? entryLayer;

                        secondStepEntryCandidates.Add(new InnerArchiveCandidate
                        {
                            Path = originalEntryPath,
                            ParentOutputDirectory = entryTaskLayer,
                            ParentTaskName = group.BaseName,
                            RootSourcePath = originalEntryPath
                        });
                        secondStepEntryGroups.Add((group, preUnit));

                        AppendLog(
                            "INFO",
                            $"[第二大步·组] {group.BaseName}：入口任务还没建 ⇒ 用组事实补建任务"
                            + "（下一轮点名，⛔ 不在边界解压）。");
                    }
                    else
                    {
                        /*
                         * 同层、同基名已经有一个成功任务（真机 2222：三个「风景01」各自
                         * 在不同层，入口是 `.7z.001`，而成功任务名是 `.mp4`）⇒ 这一组
                         * 实际上已经由那个任务收口，不能再报"找不到入口"的 WARN。
                         * 只认同一层 + 同基名 + 机器终态成功，禁跨层猜。
                         */
                        ArchiveTask? settledSameBase = _batchFinalizer.FindSucceededTaskInLayer(entryLayer, group.BaseName);

                        if (settledSameBase != null)
                        {
                            if (preUnit != null)
                            {
                                _extractionCoordinator.SettleExtractedVolumeGroup(preUnit);
                            }

                            AppendLog(
                                "INFO",
                                $"[第二大步·接住] {group.BaseName}：入口任务不在表里，但同层同基名任务"
                                + $"「{settledSameBase.LogName}」已完成 ⇒ 视为已收口，不再点名。");
                        }
                        else
                        {
                            AppendLog(
                                "WARN",
                                $"[第二大步·接住] {group.BaseName}：可证完整，但任务表里找不到入口那一份"
                                + $"（{FileNameHelper.GetFileName(entryPath)}）⇒ 这一组不动。");
                        }
                    }

                    continue;
                }

                if (entryTask.Outcome == TaskOutcome.Succeeded || entryTask.IsOutputVerified)
                {
                    /*
                     * 入口已经解成功过：这一组不会再点名，但它其余的片仍是本组的过程物
                     * ⇒ 按“已被消费”收掉（⛔ 用户原件不删；真机 8888 的 `.00!~2` 就留在这里）。
                     */
                    _extractionCoordinator.SettleExtractedVolumeGroup(preUnit);

                    AppendLog(
                        "INFO",
                        $"[第二大步·接住] {group.BaseName}：入口已经解成功过 ⇒ 不再点名（⛔ 不重跑）。");
                    continue;
                }

                /*
                 * ⛔ 上一轮已经拿到「失败 / 取消 / 部分完成」的终态 ⇒ **绝不重跑**：
                 * 重跑只会把终态重置成 Pending，一路撞轮数上限、最后连"失败"都没了
                 * （真机 V04：密码错 ⇒ 每次都被重新点名，7 轮后停在 Pending）。
                 * 片原样留着，结论由收片账那一行给。
                 *
                 * 例外（真机 21212121）：这一组的事实刚被**重算成新 Unit**（owner 还空着、
                 * 状态 Complete），说明上一轮的终态是“拿错片当入口”留下的 ⇒ 允许这一新版跑一次；
                 * 已经是 Failed/Linked/Succeeded 的 Unit 不走这里（⛔ 不形成重跑环）。
                 */
                bool freshUnitAfterFactsChanged =
                    preUnit != null
                    && preUnit.State == ArchiveFixer.Extraction.VolumeStep.VolumeGroupState.Complete
                    && string.IsNullOrWhiteSpace(preUnit.OwnerTaskFileName);

                if (!freshUnitAfterFactsChanged
                    && (entryTask.Outcome == TaskOutcome.Failed
                        || entryTask.Outcome == TaskOutcome.Cancelled
                        || entryTask.Outcome == TaskOutcome.PartiallyCompleted))
                {
                    AppendLog(
                        "INFO",
                        $"[第二大步·接住] {group.BaseName}：入口上一轮终态={entryTask.Outcome} ⇒ 不重跑，片原样留着。");
                    continue;
                }

                entryTask.Outcome = TaskOutcome.Pending;
                entryTask.IsOutputVerified = false;
                entryTask.EndTime = null;
                entryTask.ErrorMessage = string.Empty;
                entryTask.Status = StatusText.WaitingExtract;
                entryTask.Operation = StatusText.OpWaiting;
                entryTask.ProgressText = StatusText.ProgressWaiting;

                /*
                 * ===== P1：提交成 Unit（族算法在这里回答完整性/顺序/规范名计划）=====
                 * ⛔ Missing/Unknown ⇒ 不点名、不链接、不解压（判不出就不动）。
                 * RAR 已接线；其它族未接线 ⇒ 保持 P0 行为（不判、不命名）。
                 */
                ArchiveFixer.Extraction.VolumeGroupUnit? committedUnit =
                    await _extractionCoordinator.TryCommitVolumeGroupAsync(
                        group,
                        entryTask,
                        entryLayer,
                        standardNameFastPath,
                        standardFamily: fastFamily)
                        .ConfigureAwait(false);

                if (committedUnit != null
                    && (committedUnit.State == ArchiveFixer.Extraction.VolumeStep.VolumeGroupState.Missing
                        || committedUnit.State == ArchiveFixer.Extraction.VolumeStep.VolumeGroupState.Unknown
                        || committedUnit.State == ArchiveFixer.Extraction.VolumeStep.VolumeGroupState.SizeAnomaly))
                {
                    AppendLog(
                        "INFO",
                        $"[第二大步·组] {group.BaseName}：{committedUnit.State} ⇒ 不点名、不链接（{committedUnit.Verdict}）。");
                    continue;
                }

                /*
                 * 最终结论只认 VolumeGroupService + 族算法；VolumeCandidateDiscovery 的 Verdict
                 * 只是候选建议，不能拿来给未建组的候选兜底放行。
                 */
                if (committedUnit == null)
                {
                    AppendLog(
                        "WARN",
                        $"[第二大步·组] {group.BaseName}：族算法没有建组"
                        + $"（候选建议={group.SuggestedVerdict}）⇒ 不点名、不链接。");
                    continue;
                }

                _extractionCoordinator.AttachContentGroupOwner(
                    group.PreparedGroup,
                    entryTask.FileName,
                    entryTask.CurrentPath);

                _extractionCoordinator.RememberVolumeStepTaskForRound(entryTask);

                IReadOnlyList<string> consumedPaths = group.PreparedGroup?.Pieces
                    .Select(piece => piece.OriginalPath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToList()
                    ?? group.Pieces;

                _extractionCoordinator.MarkVolumeHandoffConsumed(consumedPaths, "第二大步·点名");

                /*
                 * ===== C 段：把片放进入口层，**一律只建硬链接** =====
                 * ⛔ 不搬、不复制、不删：用户原件与下一轮内层包都必须原地不动。
                 * ⛔ 规范卷名/改名是**第二大步自己的算法**，不在这里做（用户 2026-10-24 拍板：
                 * 交界处只负责把片送到入口层；"名字该怎么写"属第二大步）。
                 */
                if (group.PreparedGroup is not null)
                {
                    /*
                     * prepared 组的标准名已由边界 resolver 在临时入口层建好；
                     * 这里只把引擎入口指过去，⛔ 不再搬/链接一次、⛔ 不再造第二套名字。
                     */
                    entryTask.EngineEntryPathOverride = entryPath;

                    AppendLog(
                        "INFO",
                        $"[第二大步·标准名] {group.BaseName}：prepared 组入口 = "
                        + $"{FileNameHelper.GetFileName(entryPath)}（原始入口 {FileNameHelper.GetFileName(originalEntryPath)}；"
                        + "源文件未动）");

                    _extractionCoordinator.RememberVolumeStepGroupWork(
                        entryTask,
                        group.BaseName,
                        entryLayer,
                        group.PreparedGroup?.Pieces
                            .Select(piece => piece.OriginalPath)
                            .Where(path => !string.IsNullOrWhiteSpace(path))
                            .ToList()
                            ?? group.Pieces);
                }
                else if (entryLayer.Length > 0)
                {
                    if (committedUnit?.NamePlan is { IsEmpty: false } namePlan)
                    {
                        /*
                         * P1：族算法给了规范名计划 ⇒ 按计划建链接（只链接、不覆盖）。
                         * ⛔ 不再按原文件名另建一套，避免同一组两套名字。
                         */
                        ArchiveFixer.Extraction.VolumeGroupEntryLayer.PlanLinkResult linked =
                            ArchiveFixer.Extraction.VolumeGroupEntryLayer.LinkByPlan(
                                namePlan,
                                entryLayer,
                                (level, message) => AppendLog(level, $"[第二大步·搬进入口层] {message}"));

                        AppendLog("INFO", $"[第二大步·搬进入口层] {group.BaseName}：{linked.Describe()}（{namePlan.Evidence}）");
                        _extractionCoordinator.MarkVolumeGroupLinked(committedUnit, linked.LinkedNames);

                        DiagnosticLog.WriteGroup(
                            _vm.Logger,
                            "group.linked",
                            group.BaseName,
                            group.Format.ToString(),
                            committedUnit.State.ToString(),
                            committedUnit.Verdict,
                            group.EntryPath,
                            committedUnit.OrderedPieces,
                            committedUnit.MissingNumbers,
                            detail: namePlan.Evidence);

                        if (namePlan.Items.FirstOrDefault(item => item.IsEntry) is { } entryItem)
                        {
                            entryTask.EngineEntryPathOverride =
                                Path.Combine(entryLayer, entryItem.TargetName);
                        }
                    }
                    else
                    {
                        ArchiveFixer.Extraction.VolumeGroupEntryLayer.MoveResult moved =
                            ArchiveFixer.Extraction.VolumeGroupEntryLayer.MovePiecesIntoEntryLayer(
                                group.Pieces,
                                entryLayer,
                                _ => true,
                                (level, message) => AppendLog(level, $"[第二大步·搬进入口层] {message}"),
                                forceLinks: true);

                        AppendLog("INFO", $"[第二大步·搬进入口层] {group.BaseName}：{moved.Describe()}");
                    }

                    _extractionCoordinator.RememberVolumeStepGroupWork(
                        entryTask,
                        group.BaseName,
                        entryLayer,
                        group.PreparedGroup?.Pieces
                            .Select(piece => piece.OriginalPath)
                            .Where(path => !string.IsNullOrWhiteSpace(path))
                            .ToList()
                            ?? group.Pieces);
                }

                if (!volumeEntryTasks.Contains(entryTask))
                {
                    volumeEntryTasks.Add(entryTask);
                }

                AppendLog(
                    "INFO",
                    $"[第二大步·接住] {group.BaseName}：可证完整（{group.Pieces.Count} 片，{group.Evidence}）"
                    + $"—— 入口={FileNameHelper.GetFileName(group.EntryPath)}，这一单只解一层"
                    + "（B 段：只点名、不搬片）。");
            }
        }
    }
}


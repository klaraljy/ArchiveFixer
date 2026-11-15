using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 一个任务的生命周期执行：跑任务（解压 → 其余物 → 释放预留）与任务级处理
/// （第一/第二大步闸门 → 单层解压 → 产物登记 → 分卷收尾 → 任务收尾）。
///
/// <para>为什么单独成类：这两段只编排"一个任务从开工到收工"，与协调器的调度/批次无关；
/// ⛔ 判据、终态、工作区与源包处理全部仍走协调器既有出口（<see cref="_host"/>）。</para>
/// </summary>
internal sealed class ExtractionTaskRunner
{
    private readonly ExtractionCoordinator _host;
    private readonly MainViewModel _vm;

    internal ExtractionTaskRunner(ExtractionCoordinator host, MainViewModel vm)
    {
        _host = host;
        _vm = vm;
    }

    private void AppendLog(string level, string message) => _vm.AppendLog(level, message);

    internal async Task RunScheduledTaskAsync(ArchiveTask task, bool oneClickRun, ScheduledTaskRuntime runtime)
    {
        try
        {
            _host.Schedule.RecordSpaceTrend("任务开工");

            await ProcessExtractTaskAsync(task, oneClickRun);

            UiActivity.Mark("收尾", task.LogName, "其余物处理");
            var restWatch = System.Diagnostics.Stopwatch.StartNew();
            await _host.RunRestHandlingAsync(task, runtime, oneClickRun);
            restWatch.Stop();

            if (restWatch.ElapsedMilliseconds >= 1000)
            {
                _host.AppendLog("WARN", $"[性能] {task.LogName}：其余物处理耗时 {restWatch.ElapsedMilliseconds:N0} 毫秒");
            }

            UiActivity.Mark("收尾", task.LogName, "其余物处理完成");

            // 空间不足也不能拿“用户接受风险”替代本单成功/校验证据：
            // 失败/部分完成时源包保留，由链尾按用户档位处理。
        }
        finally
        {
            runtime.AvailableAfterFinish = _host.Schedule.ProbeAvailableSpace(_host.Schedule.ResolveSpaceProbePathOrBatch(task))
                                           ?? SpaceReservationLedger.UnknownAvailable;

            SpaceReservationLedger? ledger = _host.SpaceLedger;

            if (ledger != null)
            {
                ledger.Release(runtime.ReservedBytes);
                ledger.RefreshAvailable(runtime.AvailableAfterFinish >= 0 ? runtime.AvailableAfterFinish : null);
                runtime.ReservedBytes = 0;
            }

            _host.Schedule.RecordSpaceTrend("任务收尾");

            _host.UpdateSummary();
        }
    }

    internal async Task ProcessExtractTaskAsync(ArchiveTask task, bool oneClickRun)
    {
        var taskCts = new CancellationTokenSource();
        _host.TrackRunningTask(taskCts, task);

        _host.LogCapture.BeginTaskLogCapture(task);

        using IDisposable taskSpan = ArchiveFixer.Services.DiagnosticSpan.BeginTask(task.TaskId, task.LogName);

        try
        {
            /*
             * ⛔ 第一大步只做「单一完整包」：分卷成员只登记交接事实、原地不动，交给第二大步。
             * 判据只读内容卡（FileNature.Inspect），⛔ 不按名字猜；判不出（Unknown）照旧放行。
             */
            ArchiveFixer.Detection.FileNatureCard entryCard =
                ArchiveFixer.Detection.FileNature.Inspect(task.CurrentPath ?? string.Empty);

            /*
             * ⛔ 自动一键处理不碰非压缩文件：图片/视频/音频/文档/文本，以及 ZIP 家族的
             * docx/epub/apk/jar。手动「只解压」不走这里（oneClickRun=false），保留既有强制拆语义。
             */
            if (_host.Auxiliary.TrySkipKnownContent(task, oneClickRun, entryCard))
            {
                return;
            }

            /*
             * 共用骨架：第一大步**每一份闸门通过的文件**都进账本，包括内容 Unknown 的中间片。
             * 这一步只登记事实，⛔ 不判分卷、不碰盘、不改变任何既有分支。
             */

            /*
             * 第二大步入口（2026-10-23）：内容卡证明这一份就是分卷组的**入口卷** ⇒ 这一单就是
             * 第二大步的入口任务：登记任务级事实位（让递归只解一层），交给单层路解这一组。
             * ⛔ 仍只读内容卡，不按名字猜；非入口卷照旧跳过、原地不动。
             */

            /*
             * 第一大步**只登记、不操作**（用户 2026-10-24 拍板）：内容卡证明是分卷成员的，
             * 一律在这里跳过并登记交接事实；⛔ 不再放行、不再点名、不再搬片。
             *
             * 唯一例外（交界处重建 B 段，2026-10-24）：**已经被第二大步点名的入口任务**
             * （`_volumeLedgerState.VolumeStepTasks` 里有它）放行，只解这一层。⛔ 放行不放宽任何判据：
             * 它仍然是"内容卡证明的入口卷 + 第二大步点名"两个事实同时成立。
             */
            bool isSecondStepEntry = new ExtractionVolumeStepFacts(_host).IsSecondStepTask(task);

            string entryName = FileNameHelper.GetFileName(task.CurrentPath ?? string.Empty);
            /*
             * ⚠ 暂时仍只对 `.7z.NNN` 放行这一档：通用 NameSlot 闸门会把
             * `作用域.002` 这类普通文件也挡在第一大步外（WorkspaceRoot / SourceChange 用例当场红）。
             * 7z 中间片没有魔数，这一档是入口；等通用的"同组首卷已由内容证实"判据接上后再放宽。
             */
            int? nameSlotIndex =
                ArchiveFixer.Detection.LegacyVolumeNames.IsVolumePart(entryName)
                    ? LegacyVolumeNames.TryGetIndex(entryName)
                    : null;

            bool contentSingleComplete =
                entryCard.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.SingleComplete;

            bool nameSlotVolumeCandidate =
                !contentSingleComplete
                && nameSlotIndex > 1
                &&
                /*
                 * 7z 的中间片/末片没有卷号，内容卡常落 SingleComplete / Unknown；
                 * 名字槽 + 同批内容自证的首卷就是它们唯一的门票。⛔ 不能只收
                 * Unknown/Unknown，否则 `.003` 会被递归器当成独立包去解。
                 */
                (entryCard.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.Unknown
                 && entryCard.Format == ArchiveFixer.Detection.VolumeContentFormat.Unknown
                 || entryCard.Format == ArchiveFixer.Detection.VolumeContentFormat.SevenZip)
                && _host.VolumeHandoff.ProducedVolumeGate.HasContentProvenVolumeFirstSibling(task, entryName);

            /*
             * RAR 加密头卷号读不出（Membership=Unknown），但标准名仍是唯一的第二大步门票；
             * 多卷加密头按用户口径走标准名快速通道。⛔ 只收 RAR 标准名，不扩到所有 .002。
             */
            bool rarNameSlotCandidate =
                !contentSingleComplete
                && entryCard.Format == ArchiveFixer.Detection.VolumeContentFormat.Rar
                && entryCard.Size > 0
                && nameSlotIndex > 1;

            bool contentVolumeCandidate =
                !contentSingleComplete
                && (entryCard.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.VolumeFirstPiece
                    || entryCard.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.VolumePiece
                    /*
                     * RAR4 老编号的 `.r00` 卡上没有 BodyKind，但 Membership 可靠；
                     * ZIP 的 `Membership=VolumeMember` 只说明"开头像 zip 流"（普通单卷也成立），
                     * ⛔ 不能单凭它把 `pipe.zip` 这类单卷挡在第一大步外。ZIP 只认 split EOCD。
                     */
                    || (entryCard.Format == ArchiveFixer.Detection.VolumeContentFormat.Rar
                        && entryCard.Size > 0
                        && entryCard.Membership == ArchiveFixer.Detection.VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember)
                    || (entryCard.Format == ArchiveFixer.Detection.VolumeContentFormat.Zip
                        && entryCard.Size > 0
                        && entryCard.ZipEndRecordMeansSplit));

            string handoffEvidence = contentVolumeCandidate
                ? ArchiveFixer.Detection.VolumeHandoffEvidence.ContentProven.ToString()
                : nameSlotVolumeCandidate || rarNameSlotCandidate
                    ? ArchiveFixer.Detection.VolumeHandoffEvidence.NameSlot.ToString()
                    : ArchiveFixer.Detection.VolumeHandoffEvidence.Unknown.ToString();

            _host.AppendLog(
                "INFO",
                $"{task.LogName}：闸门判定 —— 第二大步任务={isSecondStepEntry}，内容身份={entryCard.BodyKind}，"
                + $"族={entryCard.Format}，交接证据={handoffEvidence}。");

            if (!isSecondStepEntry
                && (contentVolumeCandidate || nameSlotVolumeCandidate || rarNameSlotCandidate))
            {
                _host.VolumeHandoff.ProducedVolumeGate.RegisterFirstStepLedgerEntry(task, entryCard);
                ArchiveFixer.Detection.VolumeHandoffEntry handoff = _host.VolumeHandoff.Ledger.RegisterVolumeHandoff(task, entryCard);

                task.Status = StatusText.Skipped;
                task.Outcome = TaskOutcome.Skipped;
                task.ProgressText = StatusText.ProgressSkipped;
                task.EndTime = ArchiveFixer.Helpers.Clock.Now;
                task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;
                task.ElapsedText = task.StartTime.HasValue
                    ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                    : "-";

                /*
                 * 交接日志必须带**底层识别事实**（用户 2026-10-24：第二大步要用的判据要能看到）：
                 * 族 / 内容身份 / 成员三档判定 / 卷号（含原始字段与读不出的原因）/ 首卷标记 /
                 * 7z 自述的整包字节数与片数 / 所在层 / 来源 —— 全部由交接单 `Describe()` 一处给出。
                 */
                _host.AppendLog(
                    "WARN",
                    $"{task.LogName}：这一份是分卷成员 —— 第一大步不解它，只登记交接事实、原地不动，交给第二大步。"
                    + $"底层识别：{handoff.Describe()}");

                return;
            }

            if (isSecondStepEntry)
            {
                _host.AppendLog(
                    "INFO",
                    $"{task.LogName}：第二大步入口任务（只解一层）—— 闸门放行，不按分卷成员跳过"
                    + $"（族={entryCard.Format}，内容身份={entryCard.BodyKind}）。");
            }

            bool renameAfterGate = oneClickRun
                && (entryCard.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.SingleComplete
                    || (entryCard.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.Unknown
                        && task.IsArchive
                        && (task.ExtensionStatus == StatusText.ExtensionMismatch
                            || task.ExtensionStatus == StatusText.ExtensionMissing
                            || task.ExtensionStatus == StatusText.ExtensionMultiFake)));

            if (renameAfterGate)
            {
                await _vm.AutoFixSingleTaskAsync(task).ConfigureAwait(true);
                entryCard = ArchiveFixer.Detection.FileNature.Inspect(task.CurrentPath ?? string.Empty);
            }

            _host.VolumeHandoff.ProducedVolumeGate.RegisterFirstStepLedgerEntry(task, entryCard);

            /*
             * 放行的这一档也把内容卡事实写一行（用户 2026-10-24「文件的信息有没有记录」）：
             * 后面"解压 / 闸门"的判定都读这一组事实，事后只看日志就能复核。
             */
            _host.AppendLog(
                "INFO",
                $"{task.LogName}：内容卡 —— 格式={entryCard.Format}，内容身份={entryCard.BodyKind}，"
                + $"{entryCard.Size} 字节，成员判定={entryCard.Membership}，"
                + $"卷号={(entryCard.VolumeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "判不出")}"
                + $"（原始字段={(entryCard.RawVolumeField?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "判不出")}"
                + $"；读不出原因={entryCard.ContentFail}），"
                + $"首卷={(entryCard.FirstVolumeFlag ? "是" : "否")}，"
                + $"自述整包={(entryCard.DeclaredTotalBytes > 0 ? entryCard.DeclaredTotalBytes + " 字节" : "判不出")}，"
                + $"自述片数={(entryCard.DeclaredVolumeCount > 0 ? entryCard.DeclaredVolumeCount + " 片" : "判不出")}。");

            await new ExtractTaskMainHandler(_host).RunAsync(task, taskCts.Token, oneClickRun);

            /*
             * 第一大步产出的内层文件也必须进入唯一账本：
             * 包装解出来的 7z 中间片/Unknown 常常没有独立任务行，
             * 不在这里登记，SevenZipBoundaryResolver 就永远只看到一小部分片。
             */
            UiActivity.Mark("收尾", task.LogName, "登记第一大步产物");
            await _host.VolumeHandoff.ProducedVolumeGate.RegisterProducedOutputsToLedgerAsync(task);
            UiActivity.ClearBackground();

            /*
             * 收尾（P1）：owner 任务先按 VolumeGroupUnit 收片/留片/回写结论；
             * 不是某组 owner ⇒ 交回旧收片账（其它族/未接线路径行为不变）。
             * 成功 ⇒ 只删本组建出来的链接名（用户原件不删）；失败/密码错/缺卷 ⇒ 一个字节不动。
             */
            if (!_host.TrySettleVolumeGroup(task))
            {
                _host.VolumeStepPreflight.TryPurgeSolvedVolumeStepPieces(task);
            }
        }
        catch (OperationCanceledException)
        {
            task.Status = StatusText.Cancelled;
            task.Operation = StatusText.OpWaiting;
            task.ProgressText = StatusText.Cancelled;
            task.ErrorMessage = "任务已取消";
            task.EndTime = ArchiveFixer.Helpers.Clock.Now;
            task.ElapsedText = task.StartTime.HasValue
                ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                : "-";
            task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;

            _host.AppendLog("WARN", $"任务已取消：{task.FileName}");
        }
        catch (Exception ex)
        {
            task.Status = StatusText.UnknownError;
            task.ErrorMessage = ex.Message;
            task.Operation = StatusText.OpWaiting;
            task.ProgressText = StatusText.ProgressFailed;
            task.EndTime = ArchiveFixer.Helpers.Clock.Now;
            task.ElapsedText = task.StartTime.HasValue
                ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                : "-";
            task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;

            _host.AppendLog("ERROR", $"任务失败：{task.FileName}，{ex.Message}", DiagnosticCodes.TaskFailed, "ExtractionCoordinator.ExtractTask");
            _vm.Logger.Exception(
                "任务失败",
                ex,
                task.CurrentPath,
                errorCode: DiagnosticCodes.TaskFailed,
                component: "ExtractionCoordinator.ExtractTask");
        }
        finally
        {
            _host.VolumeStepPreflight.CleanupVolumeStepGroupEntry(task);
            _host.UntrackRunningTask(taskCts);

            try
            {
                taskCts.Dispose();
            }
            catch
            {
            }

            // 释放"最终目录占位"：这一刻目录要么已经带着产物存在（后来的任务走既有的冲突档），
            // 要么根本没建起来（后来的任务就该用它）。
            _host.Auxiliary.ReleaseOutputDirectoryClaim(task.OutputPath);

            /*
             * 没成功的工作区：失败 / 取消 / 部分完成时，默认把**这一单自己的**工作区整份删掉
             * （用户 2026-09-25 第 25 条："我不希望有这么多的失败残留"），
             * 打开「失败时保留中间产物」才留现场；零文件空壳无论如何都删。
             * 放在 finally 里是因为三条收尾路径（正常返回、OperationCanceledException、异常）
             * 都要走到它 —— 尤其是取消那一条：用户点停之后留下的残留正是他最不想看到的。
             * 成功路径不从这里走（那一支的清理在 PostProcessSuccessAsync 里按"校验通过"删）。
             */

            /*
             * 机器终态收口（第 44 条追加）：任务已经结束（`EndTime` 有值）而 `Outcome` 还停在 `Pending`，
             * 说明它走的是那些"只写了中文状态、没写终态"的失败分支（实测：识别不出格式的包
             * 状态是「解压失败」，`Outcome` 却是 `Pending`，于是批末汇总把它算进"未处理"）。
             *
             * 汇总 / 失败清单 / 四道删除门读的都是这一位，⛔ 一律不许靠比对中文。
             * 判据用"没通过输出校验"这条事实，只有真正的失败才会落进来。
             *
             * ⚠ 这一步**刻意排在下面两件事之前**（2026-10-02 部分完成发布）：那一档只认机器终态
             * （失败 / 部分完成），而这一位此刻还是 `Pending` 的话，一个字节都不会发布 ——
             * 顺序错了就等于"这一档在对绝大多数失败分支上不生效"。
             */
            /*
             * 「这一单整体有没有内容物解出」这条事实**必须在收口之前读**：
             * 候选表在下面那次 `TryPublishPartialProductsAsync` 里会被 `TryRemove` 掉。
             */
            bool chainProducedContent =
                _host.PartialPublishCandidates.TryGetValue(task, out PartialPublishStep.PartialPublishCandidate? registeredCandidate)
                && registeredCandidate.ChainProducedContent;

            ExtractionCoordinator.FinalizeOutcomeIfPending(task, chainProducedContent);
            /*
             * 部分完成发布（用户 2026-10-02 拍板的口径 A）：把已经解出来、逐条核对过的那部分
             * 放进 <c>&lt;目标&gt;\&lt;包名&gt;\部分完成\</c>。**必须排在清工作区之前** ——
             * 那些内容此刻还在这一单的暂存目录里，清掉就再也没有了（老口径正是这么丢的）。
             * 返回值 = "内容没全发布，工作区不能清"（见 PartialPublisher.Result.AllMoved）。
             */
            UiActivity.Mark("收尾", task.LogName, "部分完成发布");
            bool keepWorkspaceForUnpublished = await _host.TryPublishPartialProductsAsync(
                task,
                cancelled: taskCts.IsCancellationRequested);

            UiActivity.Mark("收尾", task.LogName, "失败工作区清理");
            var failedCleanupWatch = System.Diagnostics.Stopwatch.StartNew();
            await Task.Run(() => _host.CleanupFailedTaskWorkspace(task, keepWorkspaceForUnpublished));
            failedCleanupWatch.Stop();
            UiActivity.ClearBackground();

            if (failedCleanupWatch.ElapsedMilliseconds >= 1000)
            {
                _host.AppendLog(
                    "WARN",
                    $"[性能] {task.LogName}：失败工作区清理耗时 {failedCleanupWatch.ElapsedMilliseconds:N0} 毫秒");
            }

            /*
             * ⛔ **任务收尾：实时进度清零**（`ArchiveTask.ProgressPercent` 的契约逐字：「它是**瞬时状态**：
             * 任务一收尾就必须清掉，否则已完成/失败的任务还会挂着"解压中 45%"（不变量 6…）」）。
             *
             * 位置选在**所有路径都经过的那一个 finally**（⛔ 不是某条分支里）：
             * 实测（守门 `ProgressVisibilityTests.解压进度_落到任务上并且只在跨10个百分点时写日志`）
             * 递归那条路上收尾**没有清** ⇒ 跑完的任务 `ProgressPercent` 仍停在 100
             * （逐字 `Expected: -1 / Actual: 100`）。另外两处清**都不是统一出口**：开工前清掉上一轮残留那一处
             * （本方法中段建 `TaskProgressSink` 时）、递归路 `RecursionStopReason.SourceChanged` 那一支自己那一处。
             */
            task.ClearProgress();

            // 日志收尾：成功 → 只留一行摘要（细节全丢）；失败 / 取消 → 细节全吐出来再收摘要。
            UiActivity.Mark("收尾", task.LogName, "任务日志收尾");
            var logWatch = System.Diagnostics.Stopwatch.StartNew();
            _host.LogCapture.EndTaskLogCapture(task);
            logWatch.Stop();

            if (logWatch.ElapsedMilliseconds >= 1000)
            {
                _host.AppendLog("WARN", $"[性能] {task.LogName}：任务日志收尾耗时 {logWatch.ElapsedMilliseconds:N0} 毫秒");
            }

            UiActivity.Mark("收尾", task.LogName, "更新汇总");
            var summaryWatch = System.Diagnostics.Stopwatch.StartNew();
            _host.UpdateSummary();
            summaryWatch.Stop();

            if (summaryWatch.ElapsedMilliseconds >= 1000)
            {
                _host.AppendLog("WARN", $"[性能] {task.LogName}：更新汇总耗时 {summaryWatch.ElapsedMilliseconds:N0} 毫秒");
            }

            UiActivity.Clear();
        }
    }
}


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
    /// <summary>任务解压主流程（调度执行/单任务处理/单任务解压主体；第 12 块，同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// 跑一个任务：解压 → （空间不足模式下当场删源包）→ 其余物按档处理 → 释放预留 + 刷新可用空间。
        ///
        /// <para>⚠ 顺序是刻意的：删除发生在**任务收尾之后、释放预留之前**。
        /// 这样"删源包回收回来的空间"会出现在下一个任务的空间门判断里（这正是那个模式能解决空间不够的原因），
        /// 而账本上的预留仍然按"这些字节还在盘上"来算，绝不会提前把它许给别的任务。</para>
        /// </summary>
        private async Task RunScheduledTaskAsync(ArchiveTask task, bool oneClickRun, ScheduledTaskRuntime runtime)
        {
            try
            {
                RecordSpaceTrend("任务开工");

                await ProcessExtractTaskAsync(task, oneClickRun);

                await RunRestHandlingAsync(task, runtime, oneClickRun);

                if (_spaceTightThisBatch
                    && task.Outcome is TaskOutcome.Failed or TaskOutcome.PartiallyCompleted)
                {
                    SourceCleanupResult cleanup = new SourceCleanupService()
                        .CleanupAcceptedRisk(task, enabled: true);

                    AppendLog(
                        cleanup.FailedFiles.Count == 0 ? "INFO" : "WARN",
                        $"{task.LogName}：空间不足模式失败后删源包（用户已接受风险）—— {cleanup.Message}");
                }
            }
            finally
            {
                runtime.AvailableAfterFinish = ProbeAvailableSpace(ResolveSpaceProbePathOrBatch(task))
                                               ?? SpaceReservationLedger.UnknownAvailable;

                SpaceReservationLedger? ledger = _spaceLedger;

                if (ledger != null)
                {
                    ledger.Release(runtime.ReservedBytes);
                    ledger.RefreshAvailable(runtime.AvailableAfterFinish >= 0 ? runtime.AvailableAfterFinish : null);
                    runtime.ReservedBytes = 0;
                }

                RecordSpaceTrend("任务收尾");

                UpdateSummary();
            }
        }

        private async Task ProcessExtractTaskAsync(ArchiveTask task, bool oneClickRun)
        {
            var taskCts = new CancellationTokenSource();
            TrackRunningTask(taskCts);

            BeginTaskLogCapture(task);

            try
            {
                /*
                 * ⛔ 第一大步只做「单一完整包」：分卷成员只登记交接事实、原地不动，交给第二大步。
                 * 判据只读内容卡（FileNature.Inspect），⛔ 不按名字猜；判不出（Unknown）照旧放行。
                 */
                ArchiveFixer.Detection.FileNatureCard entryCard =
                    ArchiveFixer.Detection.FileNature.Inspect(task.CurrentPath ?? string.Empty);

                /*
                 * 共用骨架：第一大步**每一份闸门通过的文件**都进账本，包括内容 Unknown 的中间片。
                 * 这一步只登记事实，⛔ 不判分卷、不碰盘、不改变任何既有分支。
                 */
                RegisterFirstStepLedgerEntry(task, entryCard);

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
                 * （`_volumeStepTasks` 里有它）放行，只解这一层。⛔ 放行不放宽任何判据：
                 * 它仍然是"内容卡证明的入口卷 + 第二大步点名"两个事实同时成立。
                 */
                bool isSecondStepEntry = IsSecondStepTask(task);
                string entryName = FileNameHelper.GetFileName(task.CurrentPath ?? string.Empty);
                /*
                 * ⚠ 暂时仍只对 `.7z.NNN` 放行这一档：通用 NameSlot 闸门会把
                 * `作用域.002` 这类普通文件也挡在第一大步外（WorkspaceRoot / SourceChange 用例当场红）。
                 * 7z 中间片没有魔数，这一档是入口；等通用的"同组首卷已由内容证实"判据接上后再放宽。
                 */
                bool nameSlotVolumeCandidate =
                    entryCard.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.Unknown
                    && entryCard.Format == ArchiveFixer.Detection.VolumeContentFormat.Unknown
                    && ArchiveFixer.Helpers.FileNameHelper.IsVolumePartFileName(entryName)
                    && ArchiveFixer.Detection.VolumeGroupDetector.TryGetVolumeIndex(entryName) is int nameSlotIndex
                    && nameSlotIndex > 1
                    && System.Text.RegularExpressions.Regex.IsMatch(
                        entryName,
                        @"(?i)\.7z\.\d{3}$");

                bool contentVolumeCandidate =
                    entryCard.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.VolumeFirstPiece
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
                        && entryCard.ZipEndRecordMeansSplit);

                string handoffEvidence = contentVolumeCandidate
                    ? ArchiveFixer.Detection.VolumeHandoffEvidence.ContentProven.ToString()
                    : nameSlotVolumeCandidate
                        ? ArchiveFixer.Detection.VolumeHandoffEvidence.NameSlot.ToString()
                        : ArchiveFixer.Detection.VolumeHandoffEvidence.Unknown.ToString();

                AppendLog(
                    "INFO",
                    $"{task.LogName}：闸门判定 —— 第二大步任务={isSecondStepEntry}，内容身份={entryCard.BodyKind}，"
                    + $"族={entryCard.Format}，交接证据={handoffEvidence}。");

                if (!isSecondStepEntry && (contentVolumeCandidate || nameSlotVolumeCandidate))
                {
                    ArchiveFixer.Detection.VolumeHandoffEntry handoff = RegisterVolumeHandoff(task, entryCard);

                    task.Status = StatusText.Skipped;
                    task.Outcome = TaskOutcome.Skipped;
                    task.ProgressText = StatusText.ProgressSkipped;
                    task.EndTime = DateTime.Now;
                    task.LastUpdatedTime = DateTime.Now;
                    task.ElapsedText = task.StartTime.HasValue
                        ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                        : "-";

                    /*
                     * 交接日志必须带**底层识别事实**（用户 2026-10-24：第二大步要用的判据要能看到）：
                     * 族 / 内容身份 / 成员三档判定 / 卷号（含原始字段与读不出的原因）/ 首卷标记 /
                     * 7z 自述的整包字节数与片数 / 所在层 / 来源 —— 全部由交接单 `Describe()` 一处给出。
                     */
                    AppendLog(
                        "WARN",
                        $"{task.LogName}：这一份是分卷成员 —— 第一大步不解它，只登记交接事实、原地不动，交给第二大步。"
                        + $"底层识别：{handoff.Describe()}");

                    return;
                }

                if (isSecondStepEntry)
                {
                    AppendLog(
                        "INFO",
                        $"{task.LogName}：第二大步入口任务（只解一层）—— 闸门放行，不按分卷成员跳过"
                        + $"（族={entryCard.Format}，内容身份={entryCard.BodyKind}）。");
                }

                /*
                 * 放行的这一档也把内容卡事实写一行（用户 2026-10-24「文件的信息有没有记录」）：
                 * 后面"解压 / 闸门"的判定都读这一组事实，事后只看日志就能复核。
                 */
                AppendLog(
                    "INFO",
                    $"{task.LogName}：内容卡 —— 格式={entryCard.Format}，内容身份={entryCard.BodyKind}，"
                    + $"{entryCard.Size} 字节，成员判定={entryCard.Membership}，"
                    + $"卷号={(entryCard.VolumeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "判不出")}"
                    + $"（原始字段={(entryCard.RawVolumeField?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "判不出")}"
                    + $"；读不出原因={entryCard.ContentFail}），"
                    + $"首卷={(entryCard.FirstVolumeFlag ? "是" : "否")}，"
                    + $"自述整包={(entryCard.DeclaredTotalBytes > 0 ? entryCard.DeclaredTotalBytes + " 字节" : "判不出")}，"
                    + $"自述片数={(entryCard.DeclaredVolumeCount > 0 ? entryCard.DeclaredVolumeCount + " 片" : "判不出")}。");

                await ExtractSingleTaskAsync(task, taskCts.Token, oneClickRun);

                /*
                 * 收尾（P1）：owner 任务先按 VolumeGroupUnit 收片/留片/回写结论；
                 * 不是某组 owner ⇒ 交回旧收片账（其它族/未接线路径行为不变）。
                 * 成功 ⇒ 只删本组建出来的链接名（用户原件不删）；失败/密码错/缺卷 ⇒ 一个字节不动。
                 */
                if (!TrySettleVolumeGroup(task))
                {
                    TryPurgeSolvedVolumeStepPieces(task);
                }
            }
            catch (OperationCanceledException)
            {
                task.Status = StatusText.Cancelled;
                task.Operation = StatusText.OpWaiting;
                task.ProgressText = StatusText.Cancelled;
                task.ErrorMessage = "任务已取消";
                task.EndTime = DateTime.Now;
                task.ElapsedText = task.StartTime.HasValue
                    ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                    : "-";
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("WARN", $"任务已取消：{task.FileName}");
            }
            catch (Exception ex)
            {
                task.Status = StatusText.UnknownError;
                task.ErrorMessage = ex.Message;
                task.Operation = StatusText.OpWaiting;
                task.ProgressText = StatusText.ProgressFailed;
                task.EndTime = DateTime.Now;
                task.ElapsedText = task.StartTime.HasValue
                    ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                    : "-";
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("ERROR", $"任务失败：{task.FileName}，{ex.Message}");
            }
            finally
            {
                UntrackRunningTask(taskCts);

                try
                {
                    taskCts.Dispose();
                }
                catch
                {
                }

                // 释放"最终目录占位"：这一刻目录要么已经带着产物存在（后来的任务走既有的冲突档），
                // 要么根本没建起来（后来的任务就该用它）。
                ReleaseOutputDirectoryClaim(task.OutputPath);

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
                    PartialPublishCandidates.TryGetValue(task, out PartialPublishStep.PartialPublishCandidate? registeredCandidate)
                    && registeredCandidate.ChainProducedContent;

                FinalizeOutcomeIfPending(task, chainProducedContent);
                /*
                 * 部分完成发布（用户 2026-10-02 拍板的口径 A）：把已经解出来、逐条核对过的那部分
                 * 放进 <c>&lt;目标&gt;\&lt;包名&gt;\部分完成\</c>。**必须排在清工作区之前** ——
                 * 那些内容此刻还在这一单的暂存目录里，清掉就再也没有了（老口径正是这么丢的）。
                 * 返回值 = "内容没全发布，工作区不能清"（见 PartialPublisher.Result.AllMoved）。
                 */
                bool keepWorkspaceForUnpublished = await TryPublishPartialProductsAsync(
                    task,
                    cancelled: taskCts.IsCancellationRequested);

                await Task.Run(() => CleanupFailedTaskWorkspace(task, keepWorkspaceForUnpublished));

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
                EndTaskLogCapture(task);

                UpdateSummary();
            }
        }

        private async Task ExtractSingleTaskAsync(ArchiveTask task, CancellationToken cancellationToken, bool oneClickRun)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (task == null)
            {
                return;
            }

            DiagnosticLog.WriteTaskSnapshot(_vm.Logger, "task.begin", task);

            // 每个任务开始：清掉"这一个"的冲突记账（"全部 X"是本批级的，不清）。
            ResetTaskConflictState();

            /*
             * 上一轮的"提示类"结论先清掉。
             *
             * 为什么必须清：任务对象是**复用**的（同一个包重试、或用户再点一次），
             * 上一轮算出来的"本包含 N 个可执行文件""路径过长"如果不擦掉，
             * 这一轮换了密码/换了输出目录之后会继续挂着 —— 用户看到的是一句**已经不成立**的结论。
             * 它们是"提示"不是状态，所以只重置这两个字段，不碰 Status / ErrorMessage（那是下面按流程写的）。
             */
            task.DangerousEntriesWarning = string.Empty;
            task.PathLengthWarning = string.Empty;

            /*
             * 真正的 APK 不碰（判据与落点见 TrySkipAndroidPackage 的注释：内容证据、唯一出口）。
             */
            if (TrySkipAndroidPackage(task))
            {
                return;
            }

            /*
             * ===== 分卷名被伪装时，解压前先把整组改回标准名 =====
             *
             * 用户 2026-09-28 的口径：「一键处理的功能是啥，就是我按一下你全部搞定，这些必要的操作肯定是要的」。
             * 7-Zip **只认 `<基名>.001/.002`… 这套精确名字**（实测：只把第二卷改成 `.002删除`，
             * `7z x` 直接 `Unexpected end of archive`），名字不标准就必须先改，否则这一单必然失败。
             *
             * 位置在**任何引擎调用之前**，且早于源快照（改完名字再拍快照，后面各层比对的都是新名字）。
             * 只改名字、内容一个字节不动；凑不成组 / 目标名被占 → 什么都不做，并写清为什么。
             * 手动档「修复分卷名并重试」调的是**同一对**判据与执行体（VolumeNameRepair）。
             */
            // 第一大步不改分卷名（改名归第二大步）

            /*
             * ===== 不变量 11 的**第一道**（也是唯一收口的那一道）：源文件变化 = 立刻停下 =====
             *
             * 位置刻意放在本方法**最前面**，理由有三条，缺一条都会漏：
             * ① 这里是所有解压入口的唯一收口（一键处理 / 手动「只解压」/ 单包重试 / 危险模式自测）；
             * ② 必须在**任何引擎调用之前** —— 下面的"分卷缺失"里会请引擎帮忙列目录、
             *    "格式未知"那条路也会让引擎试列一次，一旦引擎碰过这个包，
             *    后面再拦就已经"用过旧识别结果"了（不变量 11 要防的正是这个）；
             * ③ 必须在**任何写盘之前**：此刻还没有暂存目录、没有输出目录、没有其余物，
             *    拦下来就是"什么都没发生"（不变量 1 的红线在这种情形下照旧成立）。
             *
             * 没有基准的老任务在这里补拍（见 EnsureSourceSnapshot），不会被误拦。
             */
            /*
             * 同一组的后续卷：入口已经定下来 ⇒ 这一单不再检查源文件、不再试密码、不再解压。
             * 它要报的是“随整组解开”，不是“源文件不见了”。
             */
            if (task.IsVolumeGroupFollower)
            {
                return;
            }

            EnsureSourceSnapshot(task);

            if (StopIfSourceChanged(task))
            {
                return;
            }

            /*
             * ===== 分卷开工前检查：已停用（用户 2026-10-24 拍板）=====
             * 原来这里对第二大步入口单调 `TryStopVolumeStepBeforeStartAsync`（缺卷不开工 / 现读盘 / 延期账）。
             * 第一大步不再碰分卷；实现仍在 `SecondStepResidue/ExtractionCoordinator.VolumeStepPreflightStep.cs`。
             */

            bool tryExtractUnknown = Settings.UnknownFormatAction == "TryExtract";

            if (!task.IsArchive || task.DetectedFormat == "Unknown")
            {
                if (tryExtractUnknown)
                {
                    AppendLog("WARN", $"格式未知：{task.FileName}，已开启“尝试解压未知格式”，继续尝试。");
                }
                else
                {
                    /*
                     * 魔数说不认识，**不等于"它不是压缩包"** —— 只说明文件头不在我的签名表里。
                     * 基线里早就写下了正确原则：最终以 7-Zip 的结论为准。
                     *
                     * 所以这里让引擎亲自试一次列目录（只对用户勾选的任务做，代价是一次进程调用）：
                     *   · 列得出来 → 就是能解的容器（自解压安装器、签名表里没有的格式都算），继续处理；
                     *   · 列不出来 → 才跳过，并把"7-Zip 也打不开"写进原因，而不是含糊的"格式未知"。
                     */
                    /*
                     * 第二大步已把这一组按规范名链接好时，交给引擎的入口是 `.zip`/`7z.001`
                     * 那个**链接名**，不是任务账上的入口片原路径（真机 960 的 8888：owner 是
                     * 内容卷号 3 的片，直接拿它去探测必然 Headers Error）。这里必须先认 override，
                     * 否则“未知格式先探一次”会在链接生效前把整组判死。
                     */
                    string unknownProbePath =
                        !string.IsNullOrWhiteSpace(task.EngineEntryPathOverride)
                        && File.Exists(task.EngineEntryPathOverride)
                            ? task.EngineEntryPathOverride
                            : task.CurrentPath;

                    ArchiveListResult probe = await _archiveEngine.ListAsync(
                        ArchiveRequest.For(unknownProbePath, string.Empty),
                        cancellationToken);

                    if (probe.Success && probe.FileCount > 0)
                    {
                        task.IsArchive = true;
                        task.EngineVerdict = $"{_archiveEngine.DisplayName} {_archiveEngine.Version} 能打开：{probe.FileCount} 个文件";

                        AppendLog("INFO", $"{task.LogName}：文件头不在签名表里，但 7-Zip 能打开（{probe.FileCount} 个文件），继续处理。");
                    }
                    else
                    {
                        /*
                         * 第一大步只做一件事：**7-Zip 打不开 ⇒ 这一单按「跳过」收场**（终态收口的理由见下）。
                         * "它是不是某个分卷组的一片"是**第二大步**的判定 —— 登记与批末升级都在分卷侧
                         * （`RegisterUnrecognizedSuspect` / `UpgradeUnrecognizedMaybePieceVerdicts`），
                         * 判据 = 同族第 1 卷 + 基名相同或互为前缀 + 体积 ≤ 满片。
                         */
                        RegisterUnrecognizedSuspect(task);

                        task.Status = StatusText.Skipped;
                        task.Operation = StatusText.OpSkip;
                        task.ProgressText = StatusText.ProgressCompleted;
                        task.ErrorMessage = string.IsNullOrWhiteSpace(probe.Message)
                            ? "不是压缩包：7-Zip 也打不开"
                            : $"不是压缩包：7-Zip 打不开（{probe.Message}）";

                        /*
                         * ⚠ 2026-10-09（B 前半「未识别的东西只跳过 + 记账」、用户原话：
                         * 「分卷和其他**未识别的东西统统跳过**到了最后再重新检验一步」）：
                         * **机器终态必须显式落 `Skipped`** —— 老写法这一支只写状态、不写终态，
                         * 而这一支走的是**正常 `return`**（只有取消 / 异常那条 `finally` 才补 `EndTime`，
                         * 见 `RunScheduledTaskAsync` 的 catch）⇒ 终态收口 `FinalizeOutcomeIfPending`
                         * 的判据（要求 `EndTime` 有值）不成立 ⇒ 这一位永远是 `Pending`
                         * ⇒ ①页那一行写着「已跳过」、批末汇总却把它算进**「未处理」**（同一件事两处口径打架）。
                         *
                         * ⛔ **不能只补 `EndTime`**：那样终态收口会接手，而它的 else 支按
                         * `Status != VolumeMissing` 一律落 `Failed` ⇒ 一个"跳过"当场变成**失败**。
                         * ⇒ 显式写 `Skipped` 之后收口那一处自然 no-op（判据第一句就是 `Outcome == Pending`）。
                         * ⚠ 与 `ArchiveTask.MarkSkipped` 的关系：这里保持既有的四处赋值**一个字节不改**
                         * （`ProgressText` 那一格是「完成」，改了会动显示口径），只补终态与结束时间。
                         */
                        task.Outcome = TaskOutcome.Skipped;
                        task.EndTime = DateTime.Now;
                        task.LastUpdatedTime = DateTime.Now;

                        AppendLog("WARN", $"跳过：{task.FileName} —— {task.ErrorMessage}");
                        return;
                    }
                }
            }

            if (!File.Exists(task.CurrentPath))
            {
                /*
                 * 与"缺卷预检 / 源文件已变化"同一个收口：这一单根本没开始解压，
                 * 所以状态、原因、**结束时间**一次落全（少了它，机器终态永远停在 Pending，
                 * 汇总把它算进「未处理」——与缺卷那一档同一个缺陷）。
                 */
                MarkStoppedBeforeExtract(task, StatusText.ExtractFailed, "文件不存在");

                AppendLog("ERROR", $"文件不存在：{task.CurrentPath}");
                return;
            }

            task.StartTime = DateTime.Now;
            task.EndTime = null;
            task.ElapsedText = "-";
            task.Operation = StatusText.OpPrepare;
            task.Status = StatusText.WaitingExtract;
            task.ProgressText = StatusText.ProgressProcessing;
            task.ErrorMessage = string.Empty;

            /*
             * 上一轮的**校验结论与机器终态**也必须清干净（任务对象是复用的：重试 / 再点一次）。
             *
             * 为什么不能只清 ErrorMessage：它们现在是"删源 / 搬源 / 续解 / 危险模式删除"的判据。
             * 一个上一轮成功过的任务，如果这一轮在开工前就被拦下（分卷缺失 / 源文件变化），
             * 留着上一轮的 `Passed` + `Succeeded` 会让那四条裁决读到一个**已经不成立的事实** ——
             * 那正是本次要根治的那类错误（拿旧结论决定不可逆动作）。
             */
            task.OutputVerification = OutputVerificationOutcome.NotAttempted;
            task.OutputManifestCrossChecked = false;
            task.Outcome = TaskOutcome.Pending;
            task.VerifyMessage = string.Empty;

            // 上一轮"定稿搬运失败"那个事实位也要清掉（批末诊断读它，留着会把这一轮指错方向）。
            task.CommitMoveFailed = false;

            // 同理：上一轮"其余物为什么留着"那句话也要清掉（这一轮它会重新写）。
            task.RestKeptReason = string.Empty;

            task.LastUpdatedTime = DateTime.Now;

            // 选项构造收口在 BuildExtractOptions：确认框的"内容物会生成在…"必须与这里用**同一份字段**算。
            ExtractOptions extractOptions = BuildExtractOptions(tryExtractUnknown);

            /*
             * 「本次选项」的运行期覆盖（规格 §9.2 硬要求①/⑥）。
             *
             * 位置刻意放在**这里**：ExtractOptions 是落点推导的入口，覆盖写在它上面，
             * 下面那条唯一的推导链（BuildOutputPath → OutputPlacement.FromLegacyFlags →
             * ResolveDestinationDirectory）一个字都不用改 —— 面板不新增第二条拼路径的实现。
             * 内层包（IsContinuationTask）的落点由父任务给定，覆盖对它没有影响。
             */
            if (RunOptions != null)
            {
                RunOptions.ApplyTo(extractOptions);
            }

            /*
             * 任务上留一份"为什么落这儿"（§9.2 硬要求⑥ / 特定解压的用户 2026-09-24 要求）：
             * 失败清单第二级与「复制任务信息」读它。
             *
             * ⚠ 特定解压那一句**不能只在有本次选项快照时才写**：手动「只解压」路径没有快照
             * （RunOptions 为 null），但用户开了特定解压时同样要能回答"这次用的是哪条特定规则"。
             * 两段都空时写空串（把上一轮留在同一个任务对象上的旧依据清掉 —— 任务对象是复用的，
             * 留着旧依据会让用户读到一条已经不成立的解释）。
             */
            string runOptionsNote = RunOptions?.Describe() ?? string.Empty;
            string specialExtractionNote = SpecialExtractionPlan.FromSettings(Settings).Describe();

            task.RunOptionsNote = specialExtractionNote.Length == 0
                ? runOptionsNote
                : runOptionsNote.Length == 0
                    ? specialExtractionNote
                    : runOptionsNote + "；" + specialExtractionNote;

            /*
             * 落点（唯一实现：PathService.ResolveOutputPlacement → OutputPlacement）。
             *
             * ⚠ 2026-09-27 起两件事变了：
             * ①**场景 B 塌缩退役**（用户："如果 111\222\ 那层里还有很多别的东西呢，不就混乱了吗"）→
             *   不再扫目录、不再塌缩，一律多一层包名目录
             *   （顺带：原来为它服务的那次"目录里还有没有别的包"的扫描与 `SourceFolderScanService`
             *   一起删掉了 —— 落点解析重新是纯函数，UI 线程上也没有磁盘活了）；
             * ②手动档「解压到当前文件夹」走 `extractOptions.ExtractIntoSourceFolder`（内容物直接落源包那一层）。
             */
            OutputPlacementResult placement = _pathService.ResolveOutputPlacement(
                task,
                extractOptions);

            string requestedOutputPath = placement.DestinationDirectory;
            string outputPath = requestedOutputPath;
            string outputRedirectNote = string.Empty;

            /*
             * 试开定序的组任务：`CurrentPath` 被改到硬链接暂存组入口，按路径算出来的落点会落进
             * `.ArchiveFixer.work`（产物还会被当成过程物）。⇒ 回**试开之前**那一份落点；⛔ 只对
             * 我们自己登记过的组任务生效，其它任务一个字不改。
             */
            if (_stagedGroupOutputPaths.TryGetValue(task, out string? stagedOutputPath)
                && !string.IsNullOrWhiteSpace(stagedOutputPath))
            {
                outputPath = stagedOutputPath;
                outputRedirectNote = "试开定序暂存组入口";

                AppendLog(
                    "INFO",
                    $"{task.LogName}：产物落点回到试开之前的原落点 —— {outputPath}");
            }

            /*
             * 落点为什么在这一层（用户 2026-10-05 口径："落在**第一卷的位置**"）：
             * 这行日志属于第二大步的解释，已停用（用户 2026-10-24）—— 实现随
             * `VolumeHandoffStep.cs` 一起搬到 `SecondStepResidue/`，重建第二大步时去那儿挑。
             */

            /*
             * 落点算不出来时必须**当场停下**（必修项，2026-09-21）。
             *
             * 旧实现会把空的"自定义输出目录"回落成程序安装目录，于是用户的文件被解进 exe 所在的那个目录 ——
             * 既找不到、又可能污染程序自己的 data。现在 BuildOutputPath 返回空串，
             * 这里给出明确状态与一句话原因，让用户去设置里选一个输出目录。
             */
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                // 同上面两处：这一单到这儿就结束了（引擎一次都没被调用），结束时间必须一起落。
                MarkStoppedBeforeExtract(
                    task,
                    StatusText.ExtractFailed,
                    "输出目录无效（未设置或不可用），请在设置里选择输出目录后重试");

                AppendLog("ERROR", $"{task.LogName}：{task.ErrorMessage}");
                return;
            }

            /*
             * 落点是不是"源包自己的目录"。
             * 现在只剩一种成因：手动档「解压到当前文件夹」（用户 2026-09-27）——
             * 场景 B 塌缩已经退役，别的落法都会多一层包名目录。
             * 这条事实决定"目录已存在且非空就改名 xxx(1)"要不要让开，
             * 理由见下面那个分支上的说明；判定本身只有一处实现（OutputPlacement）。
             */
            bool landsInSourceDirectory = OutputPlacement.LandsInSourceDirectory(task.CurrentPath, outputPath);

            // 共用落点（"添加文件夹 + 指定位置"）：别的包也会落进这一层，所以两处"改名让开"都要算上它。
            bool sharedOutputRoot = placement.SharesDestinationWithOtherPackages;

            try
            {
                /*
                 * 「输出目录已存在且非空 → 自动改名成 xxx(1)」只对**最外层源包**做。
                 *
                 * 为什么续解出来的内层包必须跳过这一步：它的落点是父任务**正在用**的那个目录
                 * （内容物 + 其余物都在里面），对它改名等于把第二层解到旁边的 xxx(1) 去 ——
                 * 那正是用户抱怨的"多弄了四个文件夹、文件一多根本分不清"。
                 * 同名冲突也不会因此丢东西：内层产物进目录时由定稿搬运的 AutoRename 兜住（绝不覆盖）。
                 */
                if (task.IsContinuationTask)
                {
                    AppendLog(
                        "INFO",
                        $"{task.LogName}：内层包，产物归入父任务输出目录 {outputPath}（不再另建目录）。");
                }
                else if (landsInSourceDirectory)
                {
                    /*
                     * 落点就是**源包所在目录**（手动档「解压到当前文件夹」）
                     * → 上面那条"已存在且非空就改名 xxx(1)"必须让开。
                     *
                     * 为什么：那个目录**必然非空** —— 源包自己就躺在里面。照旧规则一改名，
                     * 用户点的"解压到当前文件夹"当场被抵消，产物落到旁边的 名字(1)\，
                     * 正是这一轮要根治的"凭空多一层目录"。
                     *
                     * 让开之后会不会和既有文件混在一起：不会丢东西。定稿搬运对同名条目一律
                     * AutoRename（绝不覆盖），而且这条规则的本意是"别把产物倒进一个已有内容的目录"，
                     * 可这里那个目录**本来就是这次要整理的目标**。
                     */
                    AppendLog(
                        "INFO",
                        $"{task.LogName}：落点就是源包所在目录 {outputPath}（解压到当前文件夹 / 就地整理），不套用“目录已存在就改名”的规则。");
                }
                else if (sharedOutputRoot)
                {
                    /*
                     * 「添加文件夹 + 指定位置」那一档：同一个文件夹里的包都落进 BBB\222\（用户 2026-09-24 第 13 条）。
                     *
                     * 第一个包定稿之后这个目录就"存在且非空"了，照旧规则会把第二个包改名成 222(1)\ ——
                     * 用户要的"都放进 BBB\222\"当场被拆开。所以这里同样让开：
                     * 同名条目由定稿搬运的 AutoRename / 用户选的冲突档兜住，绝不覆盖（不变量 3）。
                     */
                    AppendLog(
                        "INFO",
                        $"{task.LogName}：落点是本次导入共用的目标目录 {outputPath}（添加文件夹 + 指定位置），" +
                        "不套用“目录已存在就改名”的规则。");
                }
                else if (!string.IsNullOrWhiteSpace(outputPath) &&
                    Directory.Exists(outputPath) &&
                    HasProductEntriesOutsideWorkspace(outputPath))
                {
                    /*
                     * 目标目录已存在且非空 = 第一次撞上"目标已存在"的冲突。
                     *
                     * ⚠ 这里以前**无条件**自动改名成 xxx(1) —— 于是设置里那一档「询问」在落点上
                     * 从来没有兑现过：用户选了询问，程序静默改名。现在一律走 ResolveConflictAsync：
                     * · 非 Ask 档：按档位直接算（默认 AutoRename = 老行为，绝不覆盖）；
                     * · Ask 档：第一次冲突时暂停该任务、聚合问一次（覆盖 / 跳过 / 自动重命名，可对整批生效）。
                     */
                    ConflictResolution resolution = await ResolveConflictAsync(
                        task,
                        ConflictTargetKind.Directory,
                        outputPath,
                        $"输出目录已存在且非空，{task.FileName} 的产物会落进一个已经有内容的目录里。" +
                        "请选择同名时怎么处理。",
                        "落点：" + outputPath,
                        cancellationToken);

                    if (resolution.Choice == ConflictChoice.Skip)
                    {
                        // 「跳过」= 这个包这次不处理：一个字节都不写，也不动目录里已有的东西。
                        task.Status = StatusText.Skipped;
                        task.Operation = StatusText.OpSkip;
                        task.ProgressText = StatusText.ProgressSkipped;
                        task.ErrorMessage = $"输出目录已存在且非空，按你的选择跳过：{outputPath}";
                        task.EndTime = DateTime.Now;
                        task.ElapsedText = task.StartTime.HasValue
                            ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                            : "-";
                        task.LastUpdatedTime = DateTime.Now;

                        /*
                         * ⚠ 2026-10-09：**用户自己选的"跳过"不许被算成失败**。
                         *
                         * 老写法这一支写了 `EndTime` 却**没写终态** ⇒ 终态收口 `FinalizeOutcomeIfPending`
                         * 的判据（`Outcome == Pending` + `EndTime` 有值 + 校验没通过）**三条全中**，
                         * 而它的 else 支按"不是缺卷 ⇒ `Failed`"落结论（`:457-459`）
                         * ⇒ 用户在冲突框里点的那一下「跳过」，批末汇总按**失败**数、批末色带变**红**
                         * （用户点名过「为什么跳过要标红，这是非常错误的行为」）。
                         *
                         * ⇒ 显式落 `Skipped`：收口那一处随即 no-op（它第一句就是 `Outcome == Pending`）。
                         * ⛔ 与上面"未识别"那一条同一个病、同一个修法 —— 两处都不是"补 EndTime 就行"。
                         */
                        task.Outcome = TaskOutcome.Skipped;

                        AppendLog("WARN", $"{task.LogName}：同名冲突按你的选择跳过，本任务不解压（输出目录：{outputPath}）。");
                        return;
                    }

                    if (resolution.Choice == ConflictChoice.Overwrite)
                    {
                        /*
                         * 「覆盖」= 沿用这个已有目录（把产物合并进去）。
                         *
                         * 目录本身不会被删：同名条目在**定稿搬运**那一步才真正相撞，
                         * 到那一刻仍然按同一个决定处理（覆盖走"先挪到临时名 → 落位 → 再删"两阶段）。
                         */
                        AppendLog(
                            "WARN",
                            $"{task.LogName}：输出目录已存在且非空，按你的选择「覆盖」沿用该目录：{outputPath}" +
                            "（目录里已有的同名条目会在定稿时按同一决定处理）。");

                        outputRedirectNote = $"原定输出目录 {outputPath} 已存在且非空，按你的选择覆盖进该目录";
                    }
                    else
                    {
                        string newOutputPath = resolution.TargetPath;

                        AppendLog("WARN", $"输出目录已存在且非空，为避免混入旧文件，自动改用新目录：{newOutputPath}");

                        /*
                         * 实际落点与"打算的落点"不一致这件事**不许静默**：
                         * ① 下面会把实际落点写回 task.OutputPath（界面"输出目录"列看得见）；
                         * ② 校验结论里也带一句（task.VerifyMessage）；
                         * ③ 日志里额外提醒续解的影响 —— 一键处理的续解是按**解压前**的目录快照找内层包的，
                         *    内层包落进新目录时它找不到，用户看到的现象就是"第二层没解"。
                         *    这里的提示是让人一眼知道该去哪找，而不是让程序假装没发生。
                         */
                        outputRedirectNote = $"原定输出目录 {requestedOutputPath} 已存在且非空，本次实际输出到 {newOutputPath}";

                        AppendLog("WARN", $"{task.LogName}：{outputRedirectNote}。自动续解按解压前的目录查找内层包，若内层包落在新目录里可能不会被继续解开。");

                        outputPath = newOutputPath;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                /*
                 * 取消必须原样抛上去（落成「已取消」，不变量 6）。
                 *
                 * 同名冲突询问里的「取消本批」也走这条路：下面的兜底 catch 会把任何异常变成
                 * 一句"检查输出目录失败，继续使用原输出目录"—— 那会让取消被吞掉、任务接着跑。
                 */
                throw;
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"检查输出目录失败，将继续使用原输出目录：{ex.Message}");
            }

            /*
             * ===== 并发下的"最终目录"占位（2026-09-22：默认并发提到 4 之后暴露的竞态）=====
             *
             * 场景：同一个目录里 `pipe.rar` 与 `pipe.zip` 的**包基名都是 `pipe`**，
             * 默认落点都是 `<out>\pipe`。串行时第二个会看到目录已存在、按冲突档落成 `pipe(1)`；
             * 并发时两个任务会在同一瞬间认定"这个目录还不存在"，于是双双往同一个新目录里定稿 ——
             * 后到的那个 `File.Move` 撞上已存在的目标，整条内容物都没搬成，任务报「解压失败」。
             * 用户什么错都没犯，却因为并发丢了一次解压。
             *
             * 修法：把"这个还不存在的最终目录"在**进程内先占下来**（只占不建，用户目录里不留空壳），
             * 占不到就按同一套 `名字(1)` 约定换一个。等这个任务收尾时释放（见 ProcessExtractTaskAsync）。
             *
             * 只对"目录还不存在"的情况占位：
             * · 目录已存在时，上面的冲突档已经基于一个**看得见的事实**做完了决定（沿用 / 改名 / 跳过），
             *   再插一手会把用户显式选的「覆盖」变成"改名"，那是另一回事；
             * · 落点就是源目录（**手动档「解压到当前文件夹」**；旧场景 B 塌缩已退役）时**绝不占位**：
             *   那个目录本来就是这次要整理的目标，给它改名等于把用户点的那次就地整理甩到旁边的 `名字(1)` 去；
             * · 落点是**本次导入共用的目标目录**时同样绝不占位（用户 2026-09-24 第 13 条）：
             *   旧版"添加文件夹 + 指定位置"下同一个文件夹里的包都落进 BBB\222\，第一个包占了位，
             *   后面那些包就会各自换成 222(1)、222(2)…（抢占只对"每包一个目录"的落点有意义）。
             *   ⚠ 2026-09-27 落点模型 v2 之后那一档已经**每包一个目录**（`sharedOutputRoot` 恒为 false），
             *   这条判据留着当兜底，不再有生产路径会命中它。
             */
            (outputPath, outputRedirectNote) = ClaimFinalOutputDirectoryIfNeeded(
                task, outputPath, landsInSourceDirectory, sharedOutputRoot, outputRedirectNote);

            /*
             * 回写**实际最终输出目录**（P1）。
             *
             * 这是"产物到底在哪"的唯一权威来源：上面可能刚把目录从 xxx 改成了 xxx(1)，
             * 解压后校验、结果归集、清理源包、界面"输出目录"列、以及一键处理的续解全都以它为准
             * （续解那边要读的就是这个值，见报告里的移交项）。
             * 绝不允许只把改名写进日志、却让 task.OutputPath 停在"打算输出到哪"。
             *
             * ⚠ 它**始终指向最终目录**，不指向暂存目录：暂存区只是过程，
             * 续解逻辑（OneClickCoordinator）读的必须是"内容物最终在哪"。
             */
            task.OutputPath = outputPath;

            // 每个任务都明确说一次实际落点：目录被改名时用户必须能立刻看出产物去了哪。
            // 有「本次选项」时带上依据（§9.2 硬要求⑥）：光有落点回答不了"这次为什么解到这里"。
            AppendLog(
                "INFO",
                RunOptions == null
                    ? $"{task.LogName}：本次实际输出目录 {outputPath}"
                    : $"{task.LogName}：本次实际输出目录 {outputPath}（依据 → {RunOptions.Describe()}）");

            (bool stagingOk, string stageDirectory, string engineOutputPath) staging =
                await TryPrepareStagingAsync(task, outputPath, cancellationToken);

            if (!staging.stagingOk)
            {
                return;
            }

            string stageDirectory = staging.stageDirectory;
            string engineOutputPath = staging.engineOutputPath;

            /*
             * 内嵌归档（双面文件）：先按偏移把尾部那段真正的 ZIP 抠出来，
             * 再拿它去列目录 / 预检 / 解压 / 解压后校验。
             *
             * 为什么不能直接把源文件交给 7z：这种文件的 ZIP 内部偏移是**相对 ZIP 自己**的。
             * 7-Zip 会尝试用"文件末尾 EOCD 反推的基准偏移"容忍这种错位，但只容忍 8 MiB 以内
             * （本机 26.01 实测：前置 8,388,608 字节可以，8,388,609 字节就报
             * "Cannot open the file as archive"）；用户那个文件垫了 17,031,321 字节，必然被拒。
             * 改后缀也没用（资源管理器能打开，只是因为它的 ZIP 读取器容忍这种整体错位）。
             * 把 [偏移, 归档终点) 原样复制出来是唯一可靠、且完全不改源文件的做法。
             * 终点是识别阶段算出来的 EOCD + 22 + 注释长度 —— 真实文件在 EOCD 之后还有十几 KB
             * 正常数据（实测 14,350–17,424 字节），那截不属于归档，抠取时按终点截断。
             *
             * 换成局部变量 engineArchivePath，而**不去改 task.CurrentPath**：
             * 后者是源文件路径，改名、清理源包、结果统计、报告全都依赖它 ——
             * 一旦被换成工作区里的临时文件，"清理源包"会去删我们自己的中间产物，
             * 而真正的源文件永远得不到处理，报告里指向的也不再是用户给的那个文件。
             */
            string engineArchivePath = task.CurrentPath;

            /*
             * ===== 一组分卷的第一卷名字被改坏 → 在我们自己的产物里把它摆正（用户 2026-09-25 第 37 条）=====
             *
             * 必须在**第一次 list / 解压之前**做完：改完名字引擎才认得出这一组，
             * 后面的清单、预算、路径预检、输出校验、定稿全都按"真正的归档内容"走。
             * 判据见 BrokenVolumeChainRepair：名字带卷号 1 + 后续卷 2、3… 连续 + 标准基名推得出来。
             *
             * ⛔ 只改**我们自己产出的**那一卷（续解任务：它本来就在 `其余物` 里）；
             * 用户自己添加的坏名字分卷一个字节都不动 —— 那种情况留给下面那道
             * 「通用分片」闸门如实报「分卷缺失」+ 给改名建议。
             */
            engineArchivePath = ResolveEngineArchivePath(task, engineArchivePath);

            /*
             * ⚠ 2026-10-16（困难批 `21212121`，运行期实测）：**交给引擎的那一份，名字得是它自己的规范名**。
             *
             * <para>位置说明（⚠ 用词：这一档**不是**用户可选的档位 —— 那三个"递归模式"档位
             * 用户 2026-10-14 已拍板删除，产品只有完全递归。这里是 <c>ExtractSingleTaskAsync</c>：
             * **把一份包交给引擎解**那一段；第一大步解完接着走递归核心（下面那两处
             * `IsSecondStepTask(task)` 闸门），而"第二大步"（分卷组只解一层）是**任务级事实位**
             * `_volumeStepTasks` 让它只解这一层。）</para>
             *
             * <para>现场逐字（那一批日志）：批末补判已经判出「这一组现在齐了（共 3 卷）」并把这一单重跑，
             * 可引擎拿到的仍是伪装名 ⇒ `7z x … aaa-L2.part0788839中文1.rar` ⇒ 引擎原话
             * `ERROR = Missing volume : aaa-L2.part0788839中文2.rar` —— 它是按**手上这个名字**推兄弟卷名的，
             * 而这一组的三卷在盘上叫 `aaa-L2.part1/2/3.rar`（同一层、三卷全在）。</para>
             *
             * <para>实测（`fsutil hardlink list`）：`aaa-L2.part0788839中文1.rar` 与 `aaa-L2.part1.rar`
             * 是**同一个 File ID**（`…014d85`）⇒ 换个名字交给引擎**一个字节都不换**，只是让它认得出这一组。</para>
             *
             * <para>⛔ 判据全在读事实（唯一出口 <see cref="RecursiveExtractor.PreferCanonicalSiblingName"/>）：
             * 卡上的规范名非空且与当前文件名不同 + 同目录里真有那个名字 + 磁盘身份证明是同一份；
             * 任何一条不成立 / 判不出 ⇒ 原样返回（老口径一个字节不改）。⛔ 只影响"交给引擎的那一份"：
             * `task.CurrentPath` 与源包那一档一个字都不动（改名、删源包、统计、报告读的都是它）。</para>
             */

            /*
             * 内嵌 ZIP 的**直读**结论（非 null = 这一次走直读，不产生那份等大的临时副本）。
             *
             * 用户 2026-09-24 拍板：这条路先试直读，直读说"不支持"就**原地回落到今天的"抠取 + 7z"**。
             * 两条路的结论口径完全一致 —— 用的都是既有那套 ArchivePathGuard / ResourceBudget /
             * OutputVerifier / 定稿 / 其余物 / 源包处理，只是"谁来把字节解出来"换了个人。
             */
            EmbeddedZipProbeResult? directZip = null;

            /*
             * 密码候选表：**必须在直读探查之前**算出来（2026-09-25 第 29 条）。
             *
             * 为什么顺序重要：内嵌归档可能是**加密的**（百度网盘那种分享包就是 AES-256），
             * 而 7-Zip 对 AES 包的密码只按 ANSI 字节派生 —— 它永远打不开 UTF-8 打包的中文密码。
             * 所以直读那一步要拿着候选密码自己去解（内置 ZIP-AES 读取器，两种字节编码都试），
             * 解开了就**不用**再抠一份等大的副本、也不用去问 7z。
             */
            List<PasswordItem> candidates = _passwordService.GetPasswordCandidates(
                task,
                Settings.UseGlobalPasswordForAllTasks ? GlobalPassword : string.Empty,
                _passwordService.Passwords,
                Settings.TryEmptyPasswordFirst,
                Settings.EnableSidecarPassword);

            if (candidates.Count == 0)
            {
                candidates.Add(new PasswordItem
                {
                    Value = string.Empty,
                    Source = "Empty",
                    IsEnabled = true,
                    Remark = "空密码"
                });
            }

            /*
             * 本批手动输入的密码（如果用户答过）插进候选里。
             *
             * 位置：空密码之后、密码本之前 —— 见 InsertManualPasswordCandidate 的说明。
             * 它天然参与下面的"每层尝试上限"，所以不会让加密分卷的候选循环变成无限循环（不变量 8）。
             */
            InsertManualPasswordCandidates(candidates, _manualBatchPasswords);

            /*
             * 直读/抠取两条路共用的候选值表。⛔ 只活在内存里：日志里只说"第 N 个候选 + 哪种字节编码"，
             * 绝不出密码明文（AGENTS.md §8）。
             */
            List<string> candidatePasswords = candidates.Select(c => c.Value ?? string.Empty).ToList();

            if (task.EmbeddedArchiveOffset > 0)
            {
                long carveBytes = 0;

                try
                {
                    /*
                     * 区间长度 = 终点 − 起点，**不是**"文件长度 − 起点"。
                     * 真实资源包在 EOCD 之后还有十几 KB 正常数据（实测 14,350–17,424 字节），
                     * 那截不属于归档；终点缺失（0 或 ≤ 起点）时退回文件末尾，与旧行为一致。
                     */
                    long sourceLength = new FileInfo(task.CurrentPath).Length;

                    carveBytes = task.EmbeddedArchiveEnd > task.EmbeddedArchiveOffset &&
                                 task.EmbeddedArchiveEnd <= sourceLength
                        ? task.EmbeddedArchiveEnd - task.EmbeddedArchiveOffset
                        : sourceLength - task.EmbeddedArchiveOffset;
                }
                catch
                {
                    // 量不出大小不影响能不能抠，只是日志与空间预检里少个数字。
                }

                if (IsSecondStepTask(task))
                {
                    /*
                     * 第二大步（分卷组）只解一层：直读是**第一大步**的动作，这里不做。
                     * 这一行如实说明为什么没走直读，免得用户看到"直读"两个字却发现空间没省下来。
                     */
                    AppendLog(
                        "INFO",
                        $"{task.LogName}：第二大步只解这一层，不在这里做 ZIP 直读；"
                        + $"仍按偏移取出内嵌归档（需要约 {carveBytes / 1024 / 1024} MB 临时空间）。");
                }
                else
                {
                    /*
                     * 只读探查（一个字节都不写）：结构、压缩方法、加密位、ZIP64 字段、条目名一次问清。
                     *
                     * 为什么在这里再探一次、而不是信识别阶段那个标记：中间隔着冲突询问、占位、暂存目录准备，
                     * 源文件完全可能已经换了一份（不变量 11 的快照只挡"大小/修改时间变了"）。
                     * 真正拍板必须用**此刻**这一份的结论。
                     */
                    directZip = await Task.Run(
                        () => EmbeddedZipStreamExtractor.Probe(
                            task.CurrentPath,
                            task.EmbeddedArchiveOffset,
                            task.EmbeddedArchiveEnd,
                            engineOutputPath,
                            candidatePasswords,
                            cancellationToken),
                        cancellationToken);

                    if (directZip.Supported)
                    {
                        // 上一次回落留下的抠取副本：本次直读用不到它，先清掉（见方法说明）。
                        DiscardStaleCarvedArtifact(task);

                        if (directZip.ResolvedPasswordIndex > 0)
                        {
                            /*
                             * 加密包（AES）解锁成功：这句话必须说清"是哪一档候选、按哪种字节解开的"，
                             * 因为**7-Zip 在同样一个包上只会报"密码错误"**（它只按 ANSI 字节派生密码）——
                             * 用户拿着正确的中文密码却反复失败，根因就在这里（2026-09-25 第 29 条）。
                             * ⛔ 只说序号与编码，不说密码（AGENTS.md §8）。
                             */
                            AppendLog(
                                "INFO",
                                $"{task.LogName}：内嵌归档是加密包（AES），直读已用第 {directZip.ResolvedPasswordIndex} 个候选密码" +
                                $"（按{ZipAesCrypto.DescribeEncoding(directZip.ResolvedPasswordEncoding)}）通过校验与认证码 —— " +
                                "7-Zip 对这类包只按 ANSI 字节派生密码，交给它会一律报密码错误，所以本次由内置读取器解。");
                        }

                        AppendLog(
                            "INFO",
                            $"{task.LogName}：内嵌归档可以 ZIP 直读（{directZip.List?.FileCount ?? 0} 个文件 / " +
                            $"{TaskSpaceEstimate.FormatSize(directZip.TotalBytes)}）—— 本次不需要那份等大的临时副本" +
                            $"（原本要抠 {TaskSpaceEstimate.FormatSize(directZip.ArchiveLength)}）。");

                        // 直读的"引擎"是内置读取器：结果可追溯（不变量 14）里不能写成 7-Zip。
                        if (_archiveEngine is EngineRouter directReadRouter)
                        {
                            directReadRouter.RememberEmbeddedZipDirectRead(task.CurrentPath);
                        }
                    }
                    else if (directZip.PathRejected)
                    {
                        /*
                         * 条目名越界：这是**硬失败**，不是回落信号。
                         * 抠出来交给 7z 会被同一套路径预检拒掉（AGENTS.md §6 第 4 条），
                         * 白白拷一份等大的副本没有任何意义。文案与既有那条逐字一致。
                         */
                        task.Status = StatusText.ExtractFailed;
                        task.ErrorMessage = directZip.Message;
                        task.LastUpdatedTime = DateTime.Now;

                        AppendLog("ERROR", $"{task.LogName}：{directZip.Message}");
                        return;
                    }
                    else if (directZip.PasswordRejected)
                    {
                        /*
                         * 加密包、候选密码一个都没通过：**硬失败，同样不回落**。
                         *
                         * 为什么这次连"抠出来交给 7z 试试"都不做：7-Zip 对 AES 包的密码只按 ANSI 字节派生
                         * （实测把归档的 UTF-8 声明手工清掉/置上都一样），百度网盘那种按 UTF-8 打包的包
                         * 在它那里**必然**再报一次密码错误 —— 回落只会白拷一份等大的副本（用户明说过
                         * "我不希望有这么多的失败残留"）。而内置读取器两种字节都试过了，试不出来就是真没有。
                         */
                        task.Status = StatusText.WrongPassword;
                        task.PasswordStatus = StatusText.WrongPassword;
                        task.ErrorMessage = directZip.Message;
                        task.LastUpdatedTime = DateTime.Now;

                        AppendLog("ERROR", $"{task.LogName}：{directZip.Message}");
                        return;
                    }
                    else
                    {
                        AppendLog("INFO", $"{task.LogName}：内嵌归档直读不支持（{directZip.Reason}），已回落到抠取。");
                        directZip = null;
                    }
                }

                /*
                 * 直读不通（不支持 / 递归模式）才走抠取。抠取那一整套原样保留 ——
                 * 它仍是所有"直读覆盖不到的形态"的唯一出路（加密条目、bzip2、分卷 ZIP…）。
                 */
                if (directZip == null)
                {
                    string carveTarget = BuildEmbeddedArchivePath(task);

                    /*
                     * 临时空间预检：抠出来的中间文件落在工作区（<c>&lt;程序目录&gt;\data\work</c>，即程序所在的那个盘）。
                     * 一个 780MB 的双面文件要在那里占掉 760MB —— 空间不够会写到一半失败，
                     * 还会把那个盘挤满。取不到空间就不拦（宁可试也不误拒），取到了才判。
                     *
                     * 这也是"空间核算里乐观地不记那笔副本"的兜底：真回落时由这一道把关，
                     * 所以账面省掉的那一笔不会变成"写到一半盘满"。
                     */
                    long? carveFree = SpaceChecker.GetAvailableFreeSpace(Path.GetDirectoryName(carveTarget));

                    if (carveFree.HasValue && carveBytes > 0 && carveFree.Value < carveBytes + (256L * 1024 * 1024))
                    {
                        task.Status = StatusText.ExtractFailed;
                        task.ErrorMessage =
                            $"取出内嵌归档需要约 {carveBytes / 1024 / 1024} MB 临时空间，" +
                            $"但系统盘只剩 {carveFree.Value / 1024 / 1024} MB。请先清理空间再试。";
                        task.LastUpdatedTime = DateTime.Now;

                        AppendLog("ERROR", $"{task.LogName}：{task.ErrorMessage}");
                        return;
                    }

                    AppendLog(
                        "INFO",
                        $"{task.LogName}：正在取出内嵌归档（约 {carveBytes / 1024 / 1024} MB），这一步在后台做，界面不会卡住。");

                    /*
                     * 关键修复：抠出是**同步磁盘拷贝**（几百 MB 量级），必须扔到后台线程。
                     * 之前直接在这里同步调用，拷贝期间整个界面线程被占住 ——
                     * 表现就是"点了一键处理之后程序卡死、窗口未响应"，只能强杀进程
                     * （实测：780MB 的双面文件拷 763MB，界面全程无响应）。
                     */
                    int lastPercent = -1;

                    // 每前进 20% 报一次：够密到能看出在动，又不至于把日志刷爆。
                    var carveProgress = new Progress<int>(percent =>
                    {
                        if (percent >= lastPercent + 20 || percent >= 100)
                        {
                            lastPercent = percent;
                            AppendLog("INFO", $"{task.LogName}：取出内嵌归档 {percent}%");
                        }
                    });

                    CarveResult carve = await Task.Run(
                        () => EmbeddedArchiveCarver.Carve(
                            task.CurrentPath,
                            task.EmbeddedArchiveOffset,
                            carveTarget,
                            task.EmbeddedArchiveEnd),
                        cancellationToken);

                    if (!carve.Success || !File.Exists(carve.OutputPath))
                    {
                        task.Status = StatusText.ExtractFailed;
                        task.Operation = StatusText.OpWaiting;
                        task.ProgressText = StatusText.ProgressFailed;
                        task.ErrorMessage = string.IsNullOrWhiteSpace(carve.Message)
                            ? "取出内嵌归档失败"
                            : "取出内嵌归档失败：" + carve.Message;
                        task.LastUpdatedTime = DateTime.Now;

                        AppendLog("ERROR", $"{task.LogName}：{task.ErrorMessage}");
                        return;
                    }

                    engineArchivePath = carve.OutputPath;

                    /*
                     * 抠出来那一份的名字按**内容**定（用户 2026-10-11 大测试 run-5555 实测的缺口）：
                     * 一整个 RAR5 尾巴被抠成 `X.zip` 时，`7z x` 按扩展名挑 zip 处理器直接拒绝
                     * （退出码 2、产物 0 个），上层还会把这次失败归到「分卷缺失」⇒ 整单跳过。
                     * 判据与唯一写点都在 AlignCarvedArchiveExtension 里（⛔ 不看后缀、⛔ 不猜）。
                     */
                    engineArchivePath = AlignCarvedArchiveExtension(task, engineArchivePath);

                    AppendLog(
                        "INFO",
                        $"检测到内嵌归档：已从偏移 {task.EmbeddedArchiveOffset} 处取出 {carve.BytesWritten} 字节" +
                        $"（区间 {task.EmbeddedArchiveOffset}–{task.EmbeddedArchiveOffset + carve.BytesWritten}），" +
                        $"实际使用 {engineArchivePath} 解压。");
                }
            }

            /*
             * 密码候选的**硬上限**（AGENTS.md §9.2：每层、每任务、每批次都要有尝试上限）。
             *
             * 解压模式下一个候选 = 一次完整解压：几百条密码本的包会被逐个候选整包重解一遍，
             * 用户看到的是"几十分钟不动、磁盘一直在写"。所以本层只试前 N 个，
             * 到上限时状态说"达到密码尝试上限"（**不是**"密码错误"）——
             * 包可能完全没问题，只是密码不在这批候选里。
             */
            // 上限读一次就定住：设置是用户随时可改的，同一单任务里不许"前半段按一个上限、后半段按另一个"。
            int attemptLimit = MaxPasswordAttemptsPerLayer;
            int maxPasswordAttempts = Math.Min(candidates.Count, attemptLimit);
            bool candidatesTruncated = candidates.Count > maxPasswordAttempts;

            if (candidatesTruncated)
            {
                AppendLog(
                    "WARN",
                    $"{task.LogName}：密码候选共 {candidates.Count} 个，超过每层上限 {attemptLimit} 个，" +
                    $"本层只试前 {maxPasswordAttempts} 个（到上限会明确报“{StatusText.PasswordAttemptLimitReached}”，不会报成密码错误）。");
            }

            /*
             * M5 解压前预检：路径安全 + 资源预算。
             *
             * 为什么必须先"列目录"再解压：真正写盘的是外部 7z.exe，进程外拦不住 Zip Slip。
             * 能做的只有两件事 —— ①解压前把危险条目挑出来，拒绝这一单；
             * ②解压后再校验落点（见 PostProcessSuccessAsync）。这里做的是 ①。
             *
             * 加密头（-mhe）的包不给密码是列不出目录的，所以先拿密码候选去试列表：
             * 试成功的那一个同时也是最可能的解压密码，后面解压循环还会再用一遍。
             */
            ArchiveListResult? preflightList = null;

            // 最后一次 list 失败的结构化原因（成功时用不到）。下面两个"只 list 失败的 WARN 分支"要靠它出结论：
            // 加密文件名与"文件损坏"的文本**分不开**，能分开的是错误类型（见 SevenZipOutputParser 的实测记录）。
            string lastListErrorType = string.Empty;
            string lastListMessage = string.Empty;

            // 预检给出的"文件名已加密"结论（含给用户的那句话）。null = 预检没下这个结论。
            // 它必须活到这一层结束，见下面 preflightSaidEncryptedHeaders 的恢复。
            string? encryptedHeadersMessage = null;

            if (directZip != null)
            {
                /*
                 * 直读路线：清单是**刚解析出来的**，不再问引擎 ——
                 * 源文件 7z 根本打不开（前缀远超 8 MiB 的容忍上限），问它只会得到一次失败。
                 * 更关键的是：这份清单必须与真正解出来的东西是同一份，否则后面的校验就是两套口径。
                 */
                preflightList = directZip.List;

                AppendLog(
                    "INFO",
                    $"{task.LogName}：直读清单 {preflightList?.FileCount ?? 0} 个文件 / " +
                    $"{TaskSpaceEstimate.FormatSize(preflightList?.TotalUncompressedSize ?? 0)}（未调用 7-Zip 列目录）。");
            }
            else
            {
                foreach (PasswordItem candidate in candidates.Take(MaxPreflightPasswordAttempts))
                {
                    ArchiveListResult attempt = await _archiveEngine.ListAsync(
                        ArchiveRequest.For(engineArchivePath, candidate.Value),
                        cancellationToken);

                    if (attempt.Success)
                    {
                        preflightList = attempt;
                        break;
                    }

                    lastListErrorType = attempt.ErrorType ?? string.Empty;
                    lastListMessage = attempt.Message ?? string.Empty;
                }
            }

            /*
             * ==================== 第 42 条：容器里装的是第 1 卷时，把外面的后续卷接上 ====================
             *
             * 用户 2026-09-25 选的方案 A：**做，但默认关**（②页那个开关打开才走这条路）。
             * 三种形状长得很像，判据必须是**引擎自己说的话**，不能靠猜：
             * · 容器里装的是**完整**归档 → 上面那次 list 就成功了（preflightList != null），走不到这里；
             * · 容器里装的是**分卷第 1 卷**、后续卷在外面 → list 报的正是「分卷缺失」这一类 ← 只有这一档才拼；
             * · 容器里装的是**后续卷**（真的缺首卷） → 外面那一组里**有**标准首卷名 → 不是"缺首卷的组"，
             *   SplitVolumeAssembler 的一组都挑不出来，一个字都不动。
             * 所以这里要求"引擎刚说过分卷缺失"这一条**硬证据**，拼完再用引擎验一次：验不过就退回原路。
             */
            if (preflightList == null
                && directZip == null
                && task.EmbeddedArchiveOffset > 0
                && Settings.AssembleSplitVolumesFromContainer
                && IsVolumeMissingErrorType(lastListErrorType))
            {
                (string? assembledFirstVolume, ArchiveListResult? assembledList) =
                    await TryAssembleSplitVolumesAsync(task, engineArchivePath, candidates, cancellationToken);

                if (assembledFirstVolume != null && assembledList != null)
                {
                    // 引擎自己认了这一套：换成它，后面预检 / 解压全都用拼好的那一套。
                    engineArchivePath = assembledFirstVolume;
                    preflightList = assembledList;
                }
            }

            if (preflightList == null)
            {
                /*
                 * 列不出内容：要么加密头（RAR -hp / 7z -mhe），要么归档真的坏了。
                 *
                 * ⚠ 这里**只认引擎给的错误类型**，绝不在这条路径上"猜"：
                 * 解压 / 测试路径上的 WrongPassword 是**密码候选循环的驱动信号**
                 * （循环见到非 WrongPassword 就 break），在那里把它改成"文件名已加密"
                 * 会让第一个候选（常是空密码）就打断循环 —— 我们自己 -mhe 的内层分卷会全部解不开。
                 * 判定本身只在 SevenZipOutputParser.LooksLikeEncryptedHeaders 里，而且只在列目录操作上成立
                 * （有反向回归测试钉着），这里只是把那个结论落到任务状态上。
                 */
                if (string.Equals(
                        lastListErrorType,
                        SevenZipOutputParser.EncryptedHeadersErrorType,
                        StringComparison.OrdinalIgnoreCase))
                {
                    task.Status = StatusText.EncryptedHeaders;
                    task.PasswordStatus = StatusText.PasswordNeed;

                    /*
                     * 这份文案要**留到这一层结束**（见下面 preflightSaidEncryptedHeaders 的恢复）：
                     * 密码候选循环会把 task.ErrorMessage 覆盖成泛泛的"密码错误或缺少正确密码"，
                     * 而用户真正需要知道的是"这个包连文件列表都读不出来，得先给对密码"。
                     */
                    encryptedHeadersMessage =
                        $"这个包可能加密了文件名（RAR -hp / 7z -mhe），所以连内容清单都读不出来，" +
                        $"需要正确密码才能列出内容。{lastListMessage}";

                    task.ErrorMessage = encryptedHeadersMessage;

                    AppendLog(
                        "WARN",
                        $"{task.LogName}：{StatusText.EncryptedHeaders} —— 连内容清单都读不出来（需要正确密码），" +
                        $"本次跳过路径预检与资源预算，解压后仍会校验落点。原因：{lastListMessage}");
                }
                else
                {
                    AppendLog("WARN", $"{task.LogName}：没能列出归档内容（可能是加密头或文件损坏），本次跳过路径预检与资源预算，解压后仍会校验落点。");
                }
            }
            else
            {
                /*
                 * 「一组分卷的第一卷名字被改坏」这一档（用户 2026-09-25 第 36 条追加，取证见 RawSplitStreamDetector）。
                 *
                 * 为什么必须在**写盘之前**拦：7-Zip 找不到同组的后续卷时会退化成"通用分片"，
                 * 把这一卷**照解不误**（退出码 0）—— 解出来是一个与它等大的垃圾文件，
                 * 而结果校验拿"清单那一条"比"盘上那一个文件"会判**通过**，
                 * 用户拿到 5 GB 垃圾却看到「解压成功」。这就是"成功"被用错，必须判「分卷缺失」。
                 *
                 * ⛔ 判据全在 RawSplitStreamDetector 里（纯函数、可测）：引擎说通用分片 + 清单只有"文件自己"一条
                 * + 魔数认得出是归档 + 这一组只有自己一卷。名字正常且卷齐的那一组**不拦**（拼起来正是用户要的）。
                 */
                if (RawSplitStreamDetector.IsBrokenVolumeChain(
                        preflightList,
                        task.CurrentPath,
                        task.IsArchive && !string.Equals(task.DetectedFormat, "Unknown", StringComparison.OrdinalIgnoreCase),
                        task.VolumeCount))
                {
                    string volumeDirectory = Path.GetDirectoryName(task.CurrentPath) ?? string.Empty;

                    /*
                     * 改名建议**只有一处推法**（Extraction/VolumeNameRepair）：
                     * 失败清单里写给用户的名字、与①页「按建议改名并重试」真正会改成的名字，
                     * 必须是同一个 —— 两边各推一次就会出现"按它说的改完还是解不开"。
                     */
                    VolumeNameRepairPlan repair = VolumeNameRepair.Plan(
                        task.CurrentPath,
                        CollectVolumeFilesInDirectory(volumeDirectory).Select(Path.GetFileName));

                    task.VolumeRenameSuggestion = repair.CanRepair ? repair.SuggestedFileName : string.Empty;

                    string siblingText = repair.Siblings.Count > 0
                        ? $"同目录里有像后续卷的文件：{string.Join("、", repair.Siblings)}"
                          + "（它们与这一卷的名字对不上，所以引擎找不到它们）。"
                        : "同目录里也没有找到像后续卷的文件。";

                    string advice = repair.CanRepair
                        ? $"把这一卷改回标准命名（{repair.SuggestedFileName}）就能解开 —— "
                          + "勾上它点①页「修复分卷名并重试」，程序只改名字（内容一个字节都不动）后立刻重试；"
                          + "也可以自己改完右键「重新扫描此文件」。"
                        : $"这一步没给出改名建议：{repair.Reason}。";

                    /*
                     * ⚠ 2026-10-24（第一大步造包器当场逮到）：这一支原来只写中文状态、不落机器终态 ⇒
                     * 批末汇总把它算进「未处理」。⇒ 走统一收口（`MarkStoppedBeforeExtract` +
                     * `FinalizeOutcomeIfPending`）：状态仍是「分卷缺失」，终态按既有口径（缺卷 ⇒ 部分完成）。
                     */
                    string volumeMissingMessage =
                        "分卷缺失：这一卷是「一组分卷的第一卷」，但它的名字被改坏了 —— "
                        + "引擎按名字找不到同组的后续卷，只会把它当成一段通用分片"
                        + "（那样「解出来」的是一个与它等大的垃圾文件，不是包里的内容），所以不开始。"
                        + siblingText + advice
                        + "程序不会自己改源文件（不变量 1）：只有你点那个按钮，它才会改这一个名字。";

                    MarkStoppedBeforeExtract(task, StatusText.VolumeMissing, volumeMissingMessage);
                    FinalizeOutcomeIfPending(task);

                    AppendLog("ERROR", $"{task.LogName}：{volumeMissingMessage}");
                    return;
                }

                PathSafetyReport pathReport = ArchivePathGuard.CheckEntries(preflightList.Entries);

                /*
                 * 命名类（保留名 / 结尾点或空格 / 备用数据流）：只记一条 WARN 照常解压 ——
                 * 引擎落盘时会把它们改成安全名（用户 2026-10-24 口径：清洗后再落盘，⛔ 不整包拒绝）。
                 */
                if (pathReport.NamingHazards.Count > 0)
                {
                    AppendLog("WARN", $"{task.LogName}：{pathReport.NamingSummary}");
                }

                if (!pathReport.IsSafe)
                {
                    /*
                     * ⚠ 2026-10-24（造包器 G01/G02 当场逮到）：同上 —— 只写状态不落终态会让这一单停在
                     * `Pending`（批末汇总报「未处理」）。⇒ 统一收口；状态是「解压失败」⇒ 终态 `Failed`。
                     */
                    string unsafeMessage = "归档里有不安全的条目，已拒绝解压：" + pathReport.Summary;

                    MarkStoppedBeforeExtract(task, StatusText.ExtractFailed, unsafeMessage);
                    FinalizeOutcomeIfPending(task);

                    AppendLog("ERROR", $"{task.LogName}：路径预检未通过 —— {pathReport.Summary}");
                    return;
                }

                long archiveSize = 0;

                try
                {
                    /*
                     * 量的是**真正交给引擎的那个文件**：内嵌归档抠出来之后它比源文件小得多，
                     * 拿源文件大小当分母会把展开比算小，压缩炸弹就更容易蒙混过关。
                     *
                     * 直读路线没有"那个文件"：归档就是虚拟区间 [Offset, ArchiveEnd)，
                     * 分母取它的长度（源文件含几百 MB 视频前缀，拿它当分母同样会把展开比算小）。
                     */
                    archiveSize = directZip != null
                        ? directZip.ArchiveLength
                        : new FileInfo(engineArchivePath).Length;
                }
                catch
                {
                    // 取不到大小就不做展开比判断，其余预算照常。
                }

                // 预算里的"落点"用暂存目录：引擎真正写盘的地方是它，最终目录此时还不存在。
                // 上限取用户设置（第 36 条）：四条上限只有 BudgetLimits 这一个出口。
                BudgetCheckResult budget = new ResourceBudget(BudgetLimits)
                    .CheckBeforeExtract(preflightList, archiveSize, engineOutputPath);

                /*
                 * 精确空间需求（用户 2026-09-22 需求第 1 条：核算必须含内容物 + 过程物 + 去重）。
                 *
                 * 用的是**手上这一份 list**（绝不为此再跑一次 7z：加密包每多列一次目录就多一次失败机会），
                 * 而流程预算只算了"内容物"那一项 —— 抠出来的内嵌过程物、内层包再展开的增量、
                 * 以及并发下别的任务已经占下的份额，都要在这一步一起算进来。
                 *
                 * ⚠ 源包**不算**进这个需求：它已经在盘上、本来就不在"可用空间"里
                 * （判据是 FreeSpaceDemandBytes = 内容物 + 过程物；见 TaskSpaceEstimate 里的说明）。
                 */
                TaskSpaceEstimate refined = SpaceEstimator.RefineWithListing(
                    SpaceEstimator.FromSourceFiles(task, DirectReadAppliesTo(task), PartialPublishActive),
                    preflightList,
                    directZip != null ? 0 : SpaceEstimator.EstimateCarvedBytes(task, archiveSize),
                    carvedBytesNotNeeded: directZip != null)
                    .WithVolumeLayout(ResolveWorkspaceVolumeShare(outputPath));

                if (!budget.Allowed)
                {
                    if (budget.IsSpaceShortage)
                    {
                        /*
                         * 空间不够（精确值算出来的）：**不启动**，报具体数字与建议动作。
                         * 顺手用 ReconcileReservation 把账本调准 —— 它给出的信息比预算那句话更全
                         * （含"差多少"与三条建议）；万一它反而放行（两边估算口径不同），
                         * 就退回预算那句话，绝不把"放行"当结论（宁可保守）。
                         */
                        SpaceGateDecision spaceGate = ReconcileReservation(task, refined);
                        string spaceReason = spaceGate.Allowed ? budget.Reason : spaceGate.ToLogLine();

                        MarkSpaceBlockedCore(
                            task,
                            spaceReason + $"（{refined.Describe()}；依据：{refined.Basis}）",
                            refined.FreeSpaceDemandBytes,
                            budget.FreeSpaceBytes ?? SpaceReservationLedger.UnknownAvailable,
                            spaceGate.Allowed ? 0L : spaceGate.ShortfallBytes);

                        return;
                    }

                    task.Status = StatusText.ExtractFailed;
                    task.ErrorMessage = "资源预算未通过：" + budget.Reason;

                    AppendLog("ERROR", $"{task.LogName}：资源预算未通过 —— {budget.Reason}");
                    return;
                }

                /*
                 * 空间门（精确值）：够了就放行、不够就**在这里停下**（引擎一个字节都还没写）。
                 * 它同时把本任务的预留从"粗估"调整成精确峰值 —— 并发下这一步很关键：
                 * 估小了会让几个任务一起把盘写满，估大了会白白拦下本来能跑的任务。
                 */
                SpaceGateDecision preciseGate = ReconcileReservation(task, refined);

                if (!preciseGate.Allowed)
                {
                    MarkSpaceBlockedCore(
                        task,
                        preciseGate.ToLogLine() + $"（{refined.Describe()}；依据：{refined.Basis}）",
                        refined.FreeSpaceDemandBytes,
                        preciseGate.AvailableBytes,
                        preciseGate.ShortfallBytes);

                    return;
                }

                AppendLog("INFO", $"{task.LogName}：空间门通过（精确） —— {preciseGate.Reason}；{refined.Basis}");

                if (!string.Equals(
                        RestHandlingModes.Normalize(RestHandlingThisBatch),
                        RestHandlingModes.Keep,
                        StringComparison.OrdinalIgnoreCase))
                {
                    /*
                     * "预计可回收多少"必须**跟着源包那一档算**（2026-09-27 用户真机）：
                     * `ReclaimableBytes` = 源包 + 过程物（那是"危险模式能收回多少"的口径），
                     * 可源包选的是「留在原地」时它一个字节都不会被碰 —— 报 6.8 MiB 就是在骗人
                     * （日志里那个 6.8 MiB 正是源包自己的大小）。
                     */
                    SourceHandlingMode sourceMode = EffectiveSourceHandling;

                    long reclaimable = sourceMode == SourceHandlingMode.KeepInPlace
                        ? refined.ProcessArtifactBytes
                        : refined.ReclaimableBytes;

                    string amount = reclaimable > 0
                        ? $"预计可回收 {TaskSpaceEstimate.FormatSize(reclaimable)}"
                        : "这次没有可回收的过程物";

                    AppendLog(
                        "INFO",
                        $"{task.LogName}：定稿 + 校验通过之后会按「删除操作」"
                        // ⚠ 2026-10-15：回收站档整条下线 ⇒ 走到这里（Keep 已挡掉）只剩彻底删除。
                        + "彻底删除其余物"
                        + $"（范围：{DescribeRestScope(sourceMode)}；{amount}）");
                }

                if (!string.IsNullOrWhiteSpace(budget.Reason))
                {
                    /*
                     * 这一条是**提示**（"预检通过：N 个文件 / 约 X MiB，展开比 N 倍"），不是警告 ——
                     * 以前记成 WARN，后果有两个：①日志里每个任务都顶着一条黄字，看着像出了事；
                     * ②第 44 条的日志瘦身策略里"出现 WARN 就把该任务的细节全部解锁"，
                     *    于是 68 个包每个都把十几行样板全吐了出来（实测就是被这一条挡住的）。
                     * 真正被预算挡下时走的是 ERROR（"安全上限"/"磁盘空间不足"那几支），一个字都不少。
                     */
                    AppendLog("INFO", $"{task.LogName}：资源预算提示 —— {budget.Reason}");
                }

                /*
                 * 长路径提示落地（WinRAR 参考 §2 I 组采纳项）。
                 *
                 * 以前它只在上面那行日志里（ResourceBudget 已经把它拼进 Reason），任务详情里看不到 ——
                 * 而"路径过长导致少了几个文件"恰恰是用户事后才来查的问题。落到任务字段之后，
                 * 失败清单逐归档的第二级（TaskSummaryService）也会带上这一行。
                 * 数字与文案都由 Security/PathLengthPreflight 备好，这里只搬运，不重算。
                 */
                if (!string.IsNullOrWhiteSpace(budget.PathLengthWarning))
                {
                    task.PathLengthWarning = budget.PathLengthWarning;
                }

                /*
                 * 危险条目统计（WinRAR 参考 §2 F 组 / §3 第 5 条采纳项，设置项 ReportDangerousEntries 默认开）。
                 *
                 * ⚠ 复用**手上这一份** list（preflightList），绝不为它再跑一次 7z：
                 * 加密包每多列一次目录就多一次失败机会，重复 list 是把失败概率成倍放大。
                 * **只提示不阻断**：安装器 / 补丁经常就是内容物，不做全局硬排除掩码。
                 */
                PublishDangerousEntriesHint(
                    task,
                    AnalyzeDangerousEntries(preflightList.Entries, Settings.ReportDangerousEntries),
                    "INFO");
            }

            /*
             * 预检给出的"文件名已加密"结论必须**留住**。
             *
             * 为什么不能只写在预检那一支里：后面的密码候选循环无论如何都会覆盖 task.Status
             * （解压失败 → WrongPassword）。于是预检算出来的那个**更具体**的结论会被一句泛泛的
             * "密码错误"吃掉 —— 用户拿到的还是错的那句话，这一处接线就等于白做。
             *
             * 只在"这一层最后仍然只是密码没通过"时把它恢复：
             * · 解压成功（有的包加密头也能解）→ 不动；
             * · 达到尝试上限 → 那是更具体的结论，不动；
             * · 文件损坏 / 权限不足这类别的失败 → 更具体的结论，不动。
             */
            bool preflightSaidEncryptedHeaders = task.Status == StatusText.EncryptedHeaders;

            /*
             * ===== 不变量 11 的**第二道**：就在引擎真正开工之前再比一次 =====
             *
             * 第一道在本方法开头（挡住"扫描之后就被改过"的包）。这一道挡的是**中间那段窗口**：
             * 从第一道到这里，程序已经做过冲突询问、目录占位、坍缩扫描、暂存目录准备
             * —— 弹窗可以停在屏幕上等用户点，一秒到几分钟都正常，
             * 而这期间用户完全可能自己把那个包换掉（"我先看看里面是什么"→ 解压 → 覆盖回去）。
             *
             * 两道之间只做 stat（不读内容、不写任何东西），所以代价可以忽略；
             * 而漏掉这一道的代价是**拿旧识别结果解新文件**，那正是不变量 11 要防的事。
             */
            if (StopIfSourceChanged(task))
            {
                return;
            }

            /*
             * 递归模式（M4）：不是"只解当前层"时，整条解压交给 RecursiveExtractor。
             * 它自己会解第 0 层、探测内层、按模式决定继续还是问用户，并受硬上限约束。
             * 放在预检之后：预检已经把暂存目录算好，而且非归档 / 格式未知在前面已经分流走了。
             *
             * 落点传的是**暂存目录**：递归核心本来就在自己的工作区里逐层解、最后才 Publish，
             * 让它直接 Publish 到最终目录等于跳过了"入仓 → 定稿"这道门（失败时还会在用户目录里留下半成品）。
             * 现在它发布进暂存区，再由下面的定稿一次性搬进最终目录 —— 与单层路径同一口径。
             *
             * ⚠ 这条分支里的第 0 层同样受不变量 11 保护：递归核心**每一层**开工之前都会回头问一次
             * "源文件还是原来那一份吗"（见 CheckRootSourceUnchangedAsync），不必在这里再写一遍。
             */
            /*
             * 手动「只解压」= 只解当前这一层（2026-10-25 用户拍板，WinRAR 语义）；
             * 一键处理 = 完全递归。两条路都交给同一个递归核心，仅层数上限不同
             * （手动传 1，一键传设置里的最大嵌套层数）。
             */
            if (!IsSecondStepTask(task))
            {

                RecursionResult? recursion;

                try
                {
                    recursion = await RunRecursiveAsync(
                        task, engineArchivePath, engineOutputPath, oneClickRun, cancellationToken,
                        maxDepth: oneClickRun ? null : 1);
                }
                catch (Exception ex)
                {
                    AppendLog("ERROR", $"{task.LogName}：递归解压异常 —— {ex.GetType().Name}：{ex.Message}");
                    throw;
                }

                // 这一趟有没有**可发布的产物**（用户 2026-10-06：「上一步得到的产物下一步就有可能成为
                // 所需要解压的文件」）—— ⛔ 发布与否只由产物事实回答，⛔ 不再由终态标签兼任开关：
                // `111.rar` 解出 `111.zip` 后因缺 `.z01` 停下时，旧写法把状态记成「部分完成」⇒
                // 校验 / 定稿 / 发布整条链一次都不跑 ⇒ 已经解出来的 `111.zip` 一个字节都不落地。
                bool hasPublishableProducts = recursion != null
                    && recursion.Layers.Any(layer => layer.Success)
                    && !string.IsNullOrWhiteSpace(recursion.FinalOutputPath);

                /*
                 * ⚠ **一拿到递归结果就搬**（位置三条硬要求，全是实测踩出来的）：
                 * ① **必须在工作区被清掉之前**：那一片（`111.zip`）就活在工作区的递归层目录里，
                 *    收尾一清它就没了 ⇒ 放到最后再搬会静默搬不到（守门用例当场逮到：
                 *    "这一步解出来的"那一行压根没出现、"结果校验 —— 输出目录是空目录"照旧）。
                 * ② **必须在结果校验之前**：校验读的是**暂存目录**，那一片不搬进去 ⇒ 校验看到空目录 ⇒
                 *    「校验未通过：输出目录是空目录，没有产物」⇒ 列表显示「解压失败」+「完整性：可证不完整」
                 *    ⇒ 源包一个字节都不处理（真机 2026-10-06 11:24 那两个 bug 就是这一处）。
                 * ③ **必须在"成功才进"那一支的外面**：`111.rar` 这类是「部分完成」⇒ 进不去那一支。
                 * ⇒ 三条一起满足的唯一位置就是这里（拿到结果之后、进任何收尾分支之前）。
                 */
                if (recursion != null && recursion.UnresolvedVolumePieces.Count > 0)
                {
                    List<string> carried = PublishUnresolvedVolumePieces(recursion, task);
                    AdoptUnresolvedVolumePieces(recursion, task, carried);

                    // 接片接不进去的那几份，才在产出它的那一单自己的落点里补一份（顺序不许反过来）。
                    PublishUnresolvedPieceLandingCopies(carried, task);
                }

                if (task.Status == StatusText.ExtractSuccess || hasPublishableProducts)
                {
                    // 递归产物同样要走"校验 → 定稿 → 归集 → 源包处理"，与单层路径一个字都不差。
                    bool recursionConclusionStands = await PostProcessSuccessAsync(
                        task,
                        engineArchivePath,
                        string.Empty,
                        engineOutputPath,
                        outputRedirectNote,
                        sharedOutputRoot,
                        oneClickRun,
                        cancellationToken,
                        knownList: null,
                        recursion: recursion);

                    if (recursionConclusionStands && task.Status == StatusText.ExtractSuccess)
                    {
                        AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
                    }
                }

                /*
                 * ⚠ 落地那一档**已经在上面搬过了**（拿到递归结果就搬 —— 必须在工作区被清掉之前）。
                 * ⛔ 这里不要再搬一遍。
                 */

                /*
                 * ===== 递归这条路的密码类失败也要登记到本批（2026-10-05 只读审计）=====
                 *
                 * 现场：<see cref="RecordPasswordFailure"/> 过去只有单层路径的两个调用点，而这一支解完就 return ——
                 * 于是出厂默认档（展开所有分支 ⇒ 内层包走递归）下，用户 2026-10-02 明确要的批末指路
                 * （「可能本来就没有密码 / 也可能对的密码排在候选更靠后 ⇒ 去④页调顺序或调大上限」）
                 * 与"本批 N 个包没能解开"整句**一次都不出现**。
                 *
                 * 判据**不在**这里重写：<see cref="RecordPasswordFailure"/> 自己就只认三个状态常量
                 * （密码错误 / 密码可能不对也可能数据坏了 / 达到密码尝试上限），不是那一档就什么都不做 ——
                 * 转调它 = 登记判据与批末分档仍然只有一处（§9.5）。
                 */
                RecordPasswordFailure(task);

                return;
            }

            ArchiveOperationResult? lastResult = null;
            string selectedPassword = string.Empty;
            bool passwordConfirmed = false;

            /*
             * 密码类结论上要补的两句话（**方法级**，因为要在"文件名已加密"那一支之后才落到 ErrorMessage 上）：
             * · 换引擎兜底跑过 / 没跑成（"已换 WinRAR 再试过一遍" / "没找到 WinRAR，所以没换引擎再试"）；
             * · 候选被每层上限截断时点名"哪几条没试到"。
             * 判据不成立时它**一直是空串** ⇒ 正常路径一个字都不改。
             */
            string passwordConclusionNote = string.Empty;

            /*
             * 两义那一档（引擎既说密码不对、又说数据坏了）出现过的那一次：
             * 收尾时优先用它，结论与日志同时保留两种可能（判据在引擎解析层那一处）。
             */
            ArchiveOperationResult? ambiguousTestResult = null;

            /*
             * 进度可见（D5）的接线点：**每个任务一个接收端**。
             *
             * 为什么一个任务只建一个：它记着"上一个写进日志的 10% 档位"，
             * 每换一次密码候选就新建一个的话，"达到 10% 就写一行"这条限制会失效
             * （每个候选都从头数一遍档位）。
             *
             * 清一次残留：同一个任务可能被重跑（换密码候选、用户再点一次），
             * 上一轮的百分比绝不能带到这一轮来。
             */
            ProgressHeartbeatStep.Sink progressSink = _progressHeartbeat.CreateSink(task);
            task.ClearProgress();

            /*
             * 关键修复：
             *
             * 旧逻辑：
             * candidates.Count > 1 就强制先执行 7z t。
             *
             * 问题：
             * 7z t 只是测试，不会产生解压文件。
             * 所以用户会看到 7-Zip Console 一直运行，但输出目录没有产物。
             *
             * 新逻辑：
             * 只有用户明确开启 TestBeforeExtract，或者任务明确标记 IsEncrypted，
             * 才先测试密码。
             */
            bool shouldTestPasswordBeforeExtract =
                Settings.TestBeforeExtract;

            if (directZip != null)
            {
                /*
                 * 直读路线：不走密码候选循环、不调引擎。
                 *
                 * 为什么可以完全跳过密码：能走到这里就说明探查已经确认"没有加密条目"
                 * （通用位标志 bit0 的检查在 EmbeddedZipStreamExtractor 里，一处定义）。
                 * 收尾仍然走**同一个** PostProcessSuccessAsync（校验 → 定稿 → 归集 → 其余物 → 源包处理），
                 * 绝不另起一条收尾路径 —— 两条路的结论口径必须一致。
                 */
                await RunEmbeddedZipDirectExtractionAsync(
                    task,
                    directZip,
                    candidatePasswords,
                    preflightList,
                    engineOutputPath,
                    outputRedirectNote,
                    sharedOutputRoot,
                    oneClickRun,
                    progressSink,
                    cancellationToken);
            }
            else if (shouldTestPasswordBeforeExtract)
            {
                task.Operation = StatusText.OpTest;
                task.Status = StatusText.Testing;
                task.ProgressText = StatusText.ProgressProcessing;
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("INFO", $"{task.LogName}：开始解压前测试。");

                for (int i = 0; i < maxPasswordAttempts; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    PasswordItem candidate = candidates[i];
                    string password = candidate.Value ?? string.Empty;

                    AppendLog("INFO", $"{task.LogName}：测试密码候选 {i + 1}/{maxPasswordAttempts}，{_passwordService.BuildTryPasswordLogText(candidate, i + 1)}");

                    ArchiveOperationResult testResult = await _archiveEngine.TestAsync(
                        _progressHeartbeat.BuildTrackedRequest(engineArchivePath, password, null, progressSink),
                        cancellationToken);

                    lastResult = testResult;

                    // 内部日志永远详细：普通档由任务缓冲只写文件、不上屏幕。
                    EngineOutputLog.LogVerbose(AppendLog, task.FileName, testResult);

                    if (testResult.DetectedErrorType == "Cancelled" ||
                        testResult.Status == StatusText.Cancelled ||
                        cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    if (testResult.Success)
                    {
                        selectedPassword = password;
                        passwordConfirmed = true;

                        task.Status = StatusText.TestPassed;
                        task.PasswordStatus = string.IsNullOrEmpty(password) ? StatusText.PasswordNotNeeded : StatusText.PasswordCorrect;
                        task.ErrorMessage = string.Empty;
                        task.LastUpdatedTime = DateTime.Now;

                        AppendLog("INFO", $"{task.LogName}：测试通过。");
                        break;
                    }

                    if (testResult.DetectedErrorType == "WrongPassword")
                    {
                        task.PasswordStatus = StatusText.WrongPassword;

                        /*
                         * ⛔ 这里**不是** WARN（用户 2026-09-26 第 45 条实测）：候选错一个是**预期之内**的事，
                         * 而 WARN 会（按第 44 条的规矩）把该任务攒着的细节**立刻全吐出来**并让后续 INFO 不再攒 ——
                         * 真机上就是这一行让"成功就丢"整条失效：38 个包每个都因此把入仓 / 空间门 / 定稿 /
                         * 本次内容物 / 结果校验几十行样板全写了出来（也是他看到的 246 KB 的主要来源）。
                         * 用 INFO 之后：这个候选错了就错了，任务最后**成功** → 细节照样丢；**失败** → 细节照样全留。
                         */
                        AppendLog("INFO", $"{task.LogName}：这个密码候选不对，继续试下一个。");
                        continue;
                    }

                    /*
                     * 两义那一档（引擎既说密码不对、又说数据坏了）在这里同样是**继续试下一个候选**，
                     * 不许 break —— 判据与处置只有引擎解析层那一处（见 UnRarOutputParser /
                     * EngineErrorTypes.PasswordOrCorrupted），三条候选循环口径一致。
                     */
                    if (testResult.DetectedErrorType == EngineErrorTypes.PasswordOrCorrupted)
                    {
                        ambiguousTestResult ??= testResult;
                        task.PasswordStatus = StatusText.PasswordNeed;

                        AppendLog(
                            "WARN",
                            string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                StatusText.CandidatePasswordOrCorruptedLogFormat,
                                task.FileName));

                        continue;
                    }

                    task.Status = testResult.Status;
                    task.ErrorMessage = testResult.Message;
                    task.Operation = StatusText.OpWaiting;
                    task.ProgressText = StatusText.ProgressCompleted;
                    task.LastUpdatedTime = DateTime.Now;

                    AppendLog("ERROR", $"{task.LogName}：测试失败，原因：{testResult.Message}");
                    break;
                }

                if (!passwordConfirmed)
                {
                    /*
                     * "试到上限停了"和"候选全都试过、都说密码错"是两件事（AGENTS.md §9.2）：
                     * 前者的状态是"达到密码尝试上限"，不能让用户以为密码本里就一定没有正确密码。
                     * lastResult 不是 WrongPassword 时说明停因另有其人（损坏/权限…），照旧原样上报。
                     */
                    bool stoppedByAttemptLimit = candidatesTruncated &&
                        (lastResult == null || lastResult.DetectedErrorType == "WrongPassword");

                    /*
                     * ⚠ 2026-10-13（困难批 `run-4444`，运行期定案）：**单层这条路也要问一次"这一份是什么"**
                     * —— 与递归路那三处**同一个唯一出口**（<see cref="RecursiveExtractor.IsPieceOfIncompleteGroup"/>）。
                     *
                     * <para>现场逐字：那一单（`bbb-L2.rar` = 程序从包装里救出来的那一片）在单层路上
                     * 10 条候选全试完、一条都没解开 ⇒ 落 `PasswordAttemptLimitReached` ⇒ 机器终态 `Failed`
                     * （日志逐字 `解压失败：bbb-L2.rar，原因：已达到密码尝试上限：本层试了 10 个候选（共 12 个），
                     * 未能确认密码。`）。而卡上逐字 `格式=Rar 分卷身份=VolumeMember 卷号=判不出 首卷标志=False`
                     * ⇒ 它**可证是某个还没凑齐的分卷组的一员**，同组入口不在旁边 ⇒ 结论应当是**缺卷**。</para>
                     *
                     * <para>⚠ `requireNothingElseBeside: true`：单层路这一档还要求**同层里没有别的名字也属于这一组的东西**
                     * ——`EncryptedVolumePipelineTests` 那两条守门（三片同层、名字都缀了「删除」）钉的就是这一条：
                     * 不加它会把"片都在、只是名字都坏了"错判成「缺卷」（实测逐字
                     * `Expected: "密码错误" Actual: "分卷缺失"`）。⛔ 递归路那三处**不用**这一条。</para>
                     */
                    if (RecursiveExtractor.IsPieceOfIncompleteGroup(task.CurrentPath, requireNothingElseBeside: true))
                    {
                        task.Status = StatusText.VolumeMissing;
                        task.ErrorMessage =
                            "分卷缺失：这一片属于某个分卷组，而同组的入口包不在这一层 —— "
                            + "按「缺卷」处理（⛔ 不是「密码不对」，也⛔ 不是「解压失败」）；"
                            + "片原样留着，交给批末/跨链那一套去凑。";

                        /*
                         * ⚠ 2026-10-14（走查 + 日志实测，`10101010` 那一组）：这一支原来**只写状态**、
                         * 不登记进"缺卷留到最后再判"那份名单 ⇒ 批末重判**一次都不跑**
                         * （日志里"批末补判 / 这一组现在齐了"整段是空的）⇒ 三片都在盘上也没人把它们收到一层
                         * ⇒ 判据 ① 红。⇒ 结论是缺卷就必须进名单（唯一出口 <see cref="RememberDeferredVolumeDeficit"/>，
                         * 它只管"进名单 + 点亮事实位"，⛔ 不碰 Status / ErrorMessage，⛔ 幂等）。
                         */
                        RememberDeferredVolumeDeficit(task, task.CurrentPath);

                        AppendLog("WARN", $"{task.LogName}：{task.ErrorMessage}");
                    }
                    else if (stoppedByAttemptLimit)
                    {
                        task.Status = StatusText.PasswordAttemptLimitReached;
                        task.PasswordStatus = StatusText.PasswordNeed;
                        task.ErrorMessage =
                            $"已达到密码尝试上限：本层试了 {maxPasswordAttempts} 个候选（共 {candidates.Count} 个），未能确认密码。";
                    }
                    else if (ambiguousTestResult != null)
                    {
                        // 同"普通模式"那一支：引擎自己说过两种可能，结论就必须把两种可能都留着。
                        task.Status = ambiguousTestResult.Status;
                        task.PasswordStatus = StatusText.PasswordNeed;
                        task.ErrorMessage = ambiguousTestResult.Message;
                    }
                    else if (lastResult != null && lastResult.DetectedErrorType == "WrongPassword")
                    {
                        task.Status = StatusText.WrongPassword;
                        task.PasswordStatus = StatusText.WrongPassword;
                        task.ErrorMessage = "密码错误或缺少正确密码";
                    }
                    else if (lastResult != null)
                    {
                        task.Status = lastResult.Status;
                        task.ErrorMessage = lastResult.Message;
                    }
                    else
                    {
                        task.Status = StatusText.TestFailed;
                        task.ErrorMessage = "没有可用密码或测试失败";
                    }

                    task.EndTime = DateTime.Now;
                    task.ElapsedText = task.StartTime.HasValue
                        ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                        : "-";

                    task.Operation = StatusText.OpWaiting;
                    task.ProgressText = StatusText.ProgressCompleted;
                    task.LastUpdatedTime = DateTime.Now;

                    AppendLog("ERROR", $"解压前测试失败：{task.FileName}，原因：{task.ErrorMessage}");

                    /*
                     * 密码类失败**登记到批次**，由 ShowPasswordFailuresSummaryAsync 合并成一次提示。
                     * 原来这里（以及下面的直接解压分支）各弹一次模态框、而且用的是同步 Dispatcher.Invoke：
                     * 一批几十个错包就要点几十次，还会阻塞调用线程。
                     */
                    RecordPasswordFailure(task);

                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                task.Operation = StatusText.OpExtract;
                task.Status = StatusText.Extracting;
                task.ProgressText = StatusText.ProgressProcessing;
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("INFO", $"{task.LogName}：测试通过，开始正式解压。");

                ArchiveOperationResult extractResult = await _archiveEngine.ExtractAsync(
     _progressHeartbeat.BuildTrackedRequest(engineArchivePath, selectedPassword, engineOutputPath, progressSink),
     extractOptions,
     cancellationToken);

                lastResult = extractResult;

                if (extractResult.DetectedErrorType == "Cancelled" ||
                    extractResult.Status == StatusText.Cancelled ||
                    cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (extractResult.Success)
                {
                    /*
                     * 引擎说成功 ≠ 产物是好的（同"普通模式"那条分支的真机铁证）：先校验。
                     *
                     * 这一支没有"下一个候选"可试 —— 密码是 7z t 亲自确认过的，
                     * 产物不完整只可能是盘满 / 权限 / 数据本身的问题。校验判否时**不落成功**，
                     * 但仍然走一次收尾：半截产物落在用户看得见的地方，0 字节产物什么都不会搬，
                     * 源包一个字节都不动（收尾里的校验闸门负责把结论落成失败）。
                     */
                    StageProductVerification stage = await VerifyStageProductsAsync(
                        task,
                        engineArchivePath,
                        selectedPassword,
                        engineOutputPath,
                        cancellationToken);

                    if (stage.Verification.Verified)
                    {
                        task.Status = StatusText.ExtractSuccess;
                        task.PasswordStatus = string.IsNullOrEmpty(selectedPassword) ? StatusText.PasswordNotNeeded : StatusText.PasswordCorrect;
                        task.ErrorMessage = string.Empty;

                        // 密码成功记录仍然挂在**源文件**上：下次用户再导入这个文件时要能直接命中，
                        // 而工作区里抠出来的临时文件活不过这次任务。
                        _passwordService.RecordPasswordSuccess(task.CurrentPath, selectedPassword);
                    }
                    else
                    {
                        AppendLog(
                            "WARN",
                            $"{task.LogName}：结果校验 —— {stage.Verification.Message}" +
                            "（密码是对的，但产物不完整，按失败收场）");
                    }

                    // 收尾可能把"解压成功"顶掉（产物越界 / 超预算 / 定稿失败 / 校验未通过）：只有结论仍然成立时才敢这么写日志。
                    bool conclusionStands = await PostProcessSuccessAsync(
                        task, engineArchivePath, selectedPassword, engineOutputPath, outputRedirectNote, sharedOutputRoot, oneClickRun, cancellationToken, stage.List);

                    if (conclusionStands)
                    {
                        AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
                    }
                }
                else
                {
                    task.Status = extractResult.Status;
                    task.ErrorMessage = extractResult.Message;

                    if (extractResult.DetectedErrorType == "WrongPassword")
                    {
                        task.Status = StatusText.WrongPassword;
                        task.PasswordStatus = StatusText.WrongPassword;
                        task.ErrorMessage = "密码错误或缺少正确密码";
                    }

                    // 「容器里装的是分卷第一卷」那一档：把引擎那句与事实相反的话补正（第 42 条）。
                    string volumeHint = BuildEmbeddedVolumeMissingHint(task, extractResult.DetectedErrorType);

                    if (!string.IsNullOrEmpty(volumeHint))
                    {
                        task.ErrorMessage = task.ErrorMessage + " " + volumeHint;
                    }

                    AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}");
                }
            }
            else
            {
                /*
                 * 普通模式：
                 * 不先测试，直接解压。
                 *
                 * 好处：
                 * 1. 普通无密码压缩包会立刻产生解压产物。
                 * 2. 不会让用户误以为卡住。
                 * 3. 如果遇到密码错误，再尝试下一个密码。
                 */
                bool extractSuccess = false;
                bool hasWrongPassword = false;

                /*
                 * 两义那一档（引擎既说密码不对、又说数据坏了）这一层出现过没有：
                 * 过它的时候**不停候选循环**，收尾时状态与原因取它（引擎原话里两种可能都写着），
                 * 只有它不存在时才退回"最后一次引擎结果"（老口径，行为不变）。
                 */
                ArchiveOperationResult? ambiguousResult = null;

                /*
                 * 「密码已经证实、坏的是数据」这一档（用户 2026-10-01 真机 `giu910`，17.7 GiB 解了 13 分钟）：
                 * true = 某一趟**真解出了内容**之后才失败的 ⇒ 密码不是问题，不再往下试候选。
                 * 判据见下面候选循环里那两处 `TryMeasureProducedContent`。
                 */
                bool passwordProvenDataFailed = false;
                (int Files, long Bytes) provenProducts = (0, 0);

                /*
                 * ===== 主引擎在这一层的候选上写出过字节没有（2026-10-05；换引擎兜底那条判据的输入）=====
                 *
                 * true = 某一趟失败（或"引擎说成功但校验不过"）在暂存目录里**留下了非零字节** ⇒
                 * 这个引擎**进得去这一份的数据流**。它与"换引擎兜底"的关系见
                 * `PasswordFallbackTriggerFacts.MainEngineWroteBytes`：WinRAR 用错密码时也会按原大小把
                 * 解密出来的乱码写进同一个目录（名字 / 大小 / 时间戳与真产物一样，实测）⇒ 主引擎写出过
                 * 字节的那些档，两个引擎给不出**可分辨**的证据 ⇒ 判不出 ⇒ 不换引擎、结论照旧。
                 *
                 * ⚠ 只数非零字节：0 字节桩文件不算"写出过字节"（错密码打 AES ZIP 的典型签名就是一堆 0 字节桩，
                 * 真机那一档照旧要允许换引擎）—— 口径与 `ProducedContentGate` / `ProducedBytesInLayerOutput` 一致。
                 */
                bool anyCandidateWroteBytes = false;

                /*
                 * ===== 先试密码，再解整包（用户 2026-09-25 第 37 条）=====
                 *
                 * 两条纪律，缺一条他那个 12 GiB 的包就又要白跑一整包：
                 * ① **加密包不试空密码**：7-Zip / WinRAR 都造不出"用空密码加密"的包（`-p""` 等于不加密），
                 *    拿空密码去打一个已加密的包必然白跑 —— 他真机那 10 分 18 秒就是这么来的；
                 * ② **候选先过"最小条目"这一关**：探针解开了才去解整包（见 Extraction/PasswordProbe）。
                 *
                 * 为什么以前躲不过：`-mhe=off` 的包**列目录不需要密码**（任何候选都列得出来），
                 * 而 `Copy` 方法 + AES-CBC 的数据要**整条读完**才能比对 CRC —— 错密码的代价 = 解一遍整包。
                 * ⚠ 空密码那一档只在"还有别的候选可试"时才跳过：一个候选都不剩会让下面的收场分支
                 * 落到"未知解压失败"，那比多跑一次更难懂（见 skippedOnlyEmptyPasswordBecauseEncrypted 那一支）。
                 */
                string? probeEntryPath = PasswordProbe.IsWorthProbing(
                        preflightList,
                        preflightList?.TotalUncompressedSize ?? 0)
                    ? PasswordProbe.ChooseProbeEntry(preflightList)
                    : null;
                long probeEntrySize = 0;
                bool skippedOnlyEmptyPasswordBecauseEncrypted = false;

                if (probeEntryPath != null)
                {
                    probeEntrySize = preflightList!.Entries
                        .FirstOrDefault(e => e != null && string.Equals(e.Path, probeEntryPath, StringComparison.Ordinal))?.Size ?? 0;
                }

                if (PasswordProbe.ShouldSkipEmptyPassword(preflightList))
                {
                    int removedEmpty = candidates.RemoveAll(c => string.IsNullOrEmpty(c.Value));
                    skippedOnlyEmptyPasswordBecauseEncrypted = removedEmpty > 0 && candidates.Count == 0;

                    if (removedEmpty > 0)
                    {
                        AppendLog(
                            "INFO",
                            $"{task.LogName}：整包已加密 —— 跳过「空密码」这一档（引擎造不出用空密码加密的包，"
                            + "试它必然白跑一整包）。"
                            + (candidates.Count == 0 ? "这次没有任何可用密码。" : $"还有 {Math.Min(candidates.Count, maxPasswordAttempts)} 个候选可试。"));
                    }
                }
                else if (preflightList == null
                         && string.Equals(
                             lastListErrorType,
                             SevenZipOutputParser.EncryptedHeadersErrorType,
                             StringComparison.OrdinalIgnoreCase))
                {
                    /*
                     * 文件名被加密（RAR `-hp` / 7z `-mhe=on`）时**清单根本读不出来**（preflightList == null），
                     * 上面那条判据看不到任何条目 —— 可"这一包要密码"是引擎**已经说过**的事实。
                     *
                     * 用户 2026-09-26 第 45 条真机（38 个同源包，内层是加密 RAR）里那行
                     * `开始解压，密码候选 1/10，尝试空密码` 就是从这条路来的：
                     * 空密码必然失败，白跑一轮，还顺手把"成功就丢"的日志瘦身整条打掉（那一行以前记 WARN）。
                     *
                     * ⚠ **只在还有别的候选可试时**才跳过（与上面那一支同一条边界）：一个候选都不剩会让
                     * 下面的收场分支落到"未知解压失败"，那比多跑一次更难懂 ——
                     * `PipelineWiringTests.列目录失败且判为加密头_任务状态是文件名已加密` 钉着这一点。
                     */
                    int removedEmpty = candidates.Count > 1
                        ? candidates.RemoveAll(c => string.IsNullOrEmpty(c.Value))
                        : 0;

                    if (removedEmpty > 0)
                    {
                        AppendLog(
                            "WARN",
                            $"{task.LogName}：这一包连文件名都加密（引擎说 {lastListErrorType}）—— 同样跳过「空密码」这一档。");
                    }
                }

                /*
                 * ===== 先试密码：只读「开头 64 字节」给候选排序（用户 2026-09-25 第 38 条）=====
                 *
                 * 这一档专门解决"包里没有小文件"（他这个 12 GiB 的包正是三个 5 GB 的大条目 ——
                 * 小样预检挑不出探针）。7z 的 AES **没有密码校验位**（不像 WinZip AES 有 2 字节校验值），
                 * 所以"只验密码、不读数据"在协议上做不到；但可以**只读开头几十字节**：
                 * 密码对，解出来的就是文件真正的开头（7z/zip/rar/mp4… 的魔数）；密码错，解出来是随机字节。
                 * 代价 = 7-Zip 的密钥派生（约 0.1–0.3 秒），**与包多大无关**、一个字节都不落盘。
                 *
                 * ⛔ 只用来**排序**、绝不丢候选：看不出文件开头**不等于**密码错
                 * （有些真实文件就是没有魔数的裸数据）—— 那些排到后面再试，一个都不会少。
                 */
                string? prefixEntryPath = PasswordProbe.IsWorthProbing(
                        preflightList,
                        preflightList?.TotalUncompressedSize ?? 0)
                    ? PasswordProbe.ChoosePrefixEntry(preflightList)
                    : null;

                if (prefixEntryPath != null &&
                    candidates.Count > 1 &&
                    _archiveEngine is SevenZipEngine prefixEngine)
                {
                    candidates = await RankCandidatesByDecryptedPrefixAsync(
                        task,
                        engineArchivePath,
                        prefixEngine,
                        prefixEntryPath,
                        candidates,
                        maxPasswordAttempts,
                        cancellationToken);
                }

                /*
                 * 本轮候选循环的两笔账（用户 2026-09-24 要求，缺一不可）：
                 * · lastVerification —— 最后一个"解出来了但产物没通过校验"的结论。
                 *   循环跑完还没成功时，它就是**最准确**的失败理由（比泛泛的"密码错误"多出三个数字）；
                 * · attemptedCandidates —— 到底试了几个候选，写进最终原因（"已试 N 个候选，产物始终不完整"）。
                 */
                OutputVerificationResult? lastVerification = null;
                int attemptedCandidates = 0;

                /*
                 * 这一层的**逐条清单**（部分完成发布的尺子）：优先取"带着密码列出来的那一份"
                 * （校验里那次 list），没有就退回解压前那一次预检清单 —— 未加密的包两者本来就一样。
                 * ⛔ 只留最近一次成功的：候选循环里每个候选都会重列一次，留错哪一份就对着错的尺子量。
                 */
                IReadOnlyList<(string Path, long Size)>? lastLayerManifestEntries =
                    PartialPublishPlanner.ToManifestEntries(preflightList);

                /*
                 * 2026-09-27 真机（`rar-android-722.132.apk`）加的两笔账：
                 *
                 * · previousVerification —— 上一个候选的校验数字。**连续两个候选结果一模一样**时，
                 *   换密码显然不会改变任何东西，再试下去只是白烧时间（真机白试了 9 次 / 5 分钟）；
                 * · passwordIrrelevant —— 结论是"产物不完整、与密码无关"（包没加密 / 结果不变 / 试到上限之外）。
                 *   收尾时据此**不许**再把它说成「密码错误」——那句话把用户指去核对密码本，方向全错。
                 *
                 * · stoppedByStopRequest —— 用户点了「停止后续」，候选循环在**候选之间**停下的那一档
                 *   （用户 2026-09-27："停止后续就是停止所有的东西"）。
                 */
                OutputVerificationResult? previousVerification = null;
                bool passwordIrrelevant = false;
                string? passwordIrrelevantReason = null;
                bool stoppedByStopRequest = false;

                /*
                 * ⛔ 上限必须在**动过候选表之后**重算（2026-09-26 真机逮到的缺陷）：
                 * 上面"跳过空密码"那一档会 `RemoveAll` 掉空密码候选，候选表因此少一个 ——
                 * 而 `maxPasswordAttempts` 是**列目录之前**算的（`Math.Min(候选数, 每层上限)`）。
                 * 候选数 ≤ 上限时两者相等，于是循环最后会多跑一次、`candidates[i]` 越界：
                 * 真机现场（加密包 + 候选 10 个）就是 `开始解压，密码候选 9/10` 之后直接
                 * `任务失败：… Index was out of range`，结论落成一句看不懂的"未知错误"。
                 *
                 * 顺带把"有没有被上限截断"一起重算：少了那一个之后可能已经"全都试过了"，
                 * 仍然按旧值判就会把"密码都不对"误报成「达到密码尝试上限」。
                 */
                maxPasswordAttempts = Math.Min(candidates.Count, attemptLimit);
                candidatesTruncated = candidates.Count > maxPasswordAttempts;

                for (int i = 0; i < maxPasswordAttempts; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    /*
                     * ===== 「停止后续」在候选之间生效（用户 2026-09-27 真机）=====
                     *
                     * 现场：08:25:32 他点了「停止后续」，08:25:33 程序照旧开始"密码候选 10/10"，
                     * 又跑了 32 秒才结束 —— 他原话："我都暂停了，你还在尝试新的密码"。
                     *
                     * 为什么判在这里：`StopAfterCurrent` 取消的是**批级** token（只阻止启动后续任务），
                     * 每个任务持有自己的取消源（不变量 9：停止后续 ≠ 强杀当前）。所以任务内部要自己看
                     * `IsStopping` 这个"用户想让一切都停下来"的信号 —— **候选之间**是天然的落点：
                     * 正在解的那一个不打断（不留半个产物），下一个密码一个都不再试。
                     */
                    if (IsStopping && attemptedCandidates > 0)
                    {
                        stoppedByStopRequest = true;

                        AppendLog(
                            "WARN",
                            $"{task.LogName}：已按「停止后续」中断 —— 剩余 "
                            + $"{maxPasswordAttempts - attemptedCandidates} 个候选密码不再尝试。");

                        break;
                    }

                    PasswordItem candidate = candidates[i];
                    string password = candidate.Value ?? string.Empty;

                    selectedPassword = password;
                    attemptedCandidates++;

                    task.Operation = StatusText.OpExtract;
                    task.Status = StatusText.Extracting;
                    task.ProgressText = StatusText.ProgressProcessing;
                    task.LastUpdatedTime = DateTime.Now;

                    /*
                     * ===== 候选先过"最小条目"这一关，通过了才去解整包（第 37 条）=====
                     *
                     * 探针只解清单里最小的那个文件（≤16 MiB，见 PasswordProbe）；解不开 = 这个候选不对，
                     * 而**整包一个字节都没动**。挑不出探针时 probeEntryPath 为 null，这里整段跳过（退回老路）。
                     */
                    if (probeEntryPath != null)
                    {
                        PasswordCandidatesStep.PasswordProbeOutcome probe = await ProbePasswordAsync(
                            engineArchivePath,
                            extractOptions,
                            engineOutputPath,
                            probeEntryPath,
                            selectedPassword,
                            progressSink,
                            cancellationToken);

                        if (probe == PasswordCandidatesStep.PasswordProbeOutcome.Rejected)
                        {
                            hasWrongPassword = true;
                            task.PasswordStatus = StatusText.WrongPassword;

                            AppendLog(
                                "WARN",
                                $"{task.LogName}：密码预检不通过（只解了 {PasswordProbe.Describe(probeEntryPath, probeEntrySize)}，"
                                + "整包一个字节都没动）—— 这个候选不对，试下一个。");

                            continue;
                        }

                        if (probe == PasswordCandidatesStep.PasswordProbeOutcome.Verified)
                        {
                            AppendLog(
                                "INFO",
                                $"{task.LogName}：密码预检通过（{PasswordProbe.Describe(probeEntryPath, probeEntrySize)} 解开了）"
                                + "—— 这个候选是对的，开始解整包。");
                        }
                    }

                    AppendLog("INFO", $"{task.LogName}：开始解压，密码候选 {i + 1}/{maxPasswordAttempts}，{_passwordService.BuildTryPasswordLogText(candidate, i + 1)}");

                    /*
                     * ===== 每换一个候选，先把暂存产物清空（用户 2026-09-25 真机铁证）=====
                     *
                     * 现场（日志 8:27:53–54 十条候选）：偶数候选全是"引擎说成功但产物 2 个 / 0 字节"，
                     * 奇数候选全是"密码错误" —— 奇偶交替。查下来是**上一次尝试的垃圾把下一次废掉**：
                     *
                     * ① 7z 用**错密码**解一个 AES ZIP 时，会先在输出目录里建出 0 字节的桩文件，**然后**才报
                     *    Wrong password（退出码 2）—— 实测如此（用故意错的密码跑，目录里照样留下 2 个 0 字节文件）；
                     * ② 旧代码只在"引擎说成功但校验不过"那一支清产物，"密码错误"这一支**直接 continue**，
                     *    于是桩文件留在了暂存目录里；
                     * ③ 下一次尝试带着 `-aos`（已存在就跳过）跑：7z 看到那两个文件已存在，**直接跳过、退出码 0**，
                     *    我们随后校验到"2 个文件 / 0 字节"，把这个候选当成废票扔掉并清掉产物；
                     * ④ 于是**真正正确的那个候选，只要排在"错密码候选"后面，就永远没机会真正解一次** ——
                     *    用户密码本里的第一条正是这样被废掉的。
                     *
                     * 修法：每个候选都从**空目录**开始。宁可每次多删一次（暂存目录本来就是我们的、内容都是垃圾），
                     * 也不能让上一次的桩文件把这一次的正确密码判成废票。
                     */
                    await DiscardStageProductsAsync(task, engineOutputPath, cancellationToken);

                    ArchiveOperationResult extractResult = await _archiveEngine.ExtractAsync(
     _progressHeartbeat.BuildTrackedRequest(engineArchivePath, selectedPassword, engineOutputPath, progressSink),
     extractOptions,
     cancellationToken);

                    lastResult = extractResult;

                    /*
                     * 详细档：这一次候选的命令行与原话（**无论成败**）。
                     *
                     * ⚠ 失败的"关键行"刻意**不在这里**写：候选循环每试一个错密码就要报一次错，
                     * 十个候选就是十条 ERROR —— 那是噪声，不是信号。失败原话在**结论那一处**
                     * 写一次（见下面 `conclusionResult` 那一段）。
                     */
                    // 内部日志永远详细：普通档由任务缓冲只写文件、不上屏幕。
                    EngineOutputLog.LogVerbose(AppendLog, task.FileName, extractResult);

                    _vm.Logger.Event(
                        "engine.extract",
                        $"{task.FileName}：引擎={extractResult.EngineId}；成功={extractResult.Success}；错误={extractResult.DetectedErrorType}",
                        detail: "command=" + extractResult.CommandSummary
                            + "; exit=" + extractResult.ExitCode
                            + "; message=" + extractResult.Message
                            + "; output=" + string.Join(" ｜ ", extractResult.KeyOutputLines()),
                        file: task.CurrentPath,
                        engine: extractResult.EngineId,
                        exitCode: extractResult.ExitCode,
                        result: extractResult.Success ? "success" : "failed");

                    if (extractResult.DetectedErrorType == "Cancelled" ||
                        extractResult.Status == StatusText.Cancelled ||
                        cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    if (extractResult.Success)
                    {
                        /*
                         * ===== 关键修复（用户 2026-09-24 真机铁证）======
                         *
                         * 引擎说"成功"**不等于**产物是好的：7z 会在密码不对 / 数据不全时写出 0 字节的桩文件，
                         * 而退出码仍然是 0。老逻辑拿到 Success 就直接落「解压成功」并 break ——
                         * 用户看到的是一句"解压成功"，目录里却什么都没有，而且剩下的候选密码再也没被试过。
                         *
                         * 现在：**先校验，再决定这个候选算不算数**。
                         * 判否 → 当成"这个候选不行"：清掉它的垃圾产物、continue 试下一个候选。
                         * 只有真的校验通过，才落成功；无论哪种情况，收尾都走同一个 PostProcessSuccessAsync
                         * （校验未通过时它会**顶掉成功结论**、不动源包、也不会把 0 字节产物搬出去）。
                         */
                        StageProductVerification stage = await VerifyStageProductsAsync(
                            task,
                            engineArchivePath,
                            selectedPassword,
                            engineOutputPath,
                            cancellationToken);

                        // 这一层的尺子换成"用这个候选的密码列出来的那一份"（部分完成发布要用它逐条对账）。
                        if (stage.List is { Success: true })
                        {
                            lastLayerManifestEntries = PartialPublishPlanner.ToManifestEntries(stage.List);
                        }

                        if (stage.Verification.Verified)
                        {
                            extractSuccess = true;

                            task.Status = StatusText.ExtractSuccess;
                            task.PasswordStatus = string.IsNullOrEmpty(selectedPassword) ? StatusText.PasswordNotNeeded : StatusText.PasswordCorrect;
                            task.ErrorMessage = string.Empty;

                            // 同"先测试再解压"那条分支：密码成功记录挂源文件，不挂工作区里的临时文件。
                            _passwordService.RecordPasswordSuccess(task.CurrentPath, selectedPassword);
                        }
                        else
                        {
                            lastVerification = stage.Verification;
                            task.PasswordStatus = StatusText.WrongPassword;

                            /*
                             * 还有候选可试吗？有 → 这个候选不算数，清掉它的产物接着试。
                             *
                             * 例外一（**产物越界**，不变量 4）：它比"候选不行"更具体，交给收尾去说。
                             * 越界的现场往往"暂存目录里什么都没有"（产物被写到了目标根之外），
                             * 在这里判成"这个候选不行"会把真正的原因换成一句泛泛的"没有产物"，
                             * 用户就看不到那条唯一能行动的信息。
                             *
                             * 例外二（**最后一个候选**）：没有下一个可试了，此时走收尾而不是把产物删掉 ——
                             * 半截产物（真的解出了一部分的那种）能落在用户看得见的地方，
                             * 而 0 字节产物在那一步天然什么都不会搬（PlanFinalLayout 的零字节规则）。
                             * 两种情况的结论都一样：**不许成功**（收尾里的校验闸门会落成失败）。
                             */
                            string? landingViolation = await Task.Run(
                                () => RecursiveExtractor.FindLandingViolation(engineOutputPath),
                                cancellationToken);

                            bool hasMoreCandidates = i < maxPasswordAttempts - 1;

                            /*
                             * ===== 这次失败到底是不是"密码不对"？（2026-09-27 真机铁证）=====
                             *
                             * 现场：`rar-android-722.132.apk` 用**空密码**解出 1238 个文件（校验说"少 33 个"），
                             * 程序把这句话当成"这个候选不行"，于是把 10 个候选密码全试了一遍（5 分钟），
                             * 最后报「密码错误」—— 而那个包**根本没有加密**。那次"少 33 个"的真因是
                             * **32 组仅大小写不同的同名条目**（Windows 物理上放不下），已在校验口径里修掉；
                             * 这里修的是**另一半**：结论不能想当然地归给密码。
                             *
                             * 三条判据全部来自事实（⛔ 不比对文案）：
                             *  ① 包**没有加密**（清单里一个加密条目都没有）→ 密码与产物无关；
                             *  ② 与**上一个候选的结果完全相同**（同文件数、同字节数）→ 换密码不会改变结果；
                             *  ③ 用户已经点了「停止后续」→ 一个都不再试。
                             * 命中任一条就不再烧候选，并把这个结论带进收尾（⛔ 不许说成「密码错误」）。
                             */
                            /*
                             * ⛔ 前置门槛：**必须真的解出了内容**（字节数 > 0）才谈得上"与密码无关"。
                             *
                             * 为什么这一条不能省（2026-09-25 那条修复正是它）：**错密码**的典型签名就是
                             * "引擎退出码 0、只写出一堆 0 字节桩文件"，而那种结果**必须继续试下一个候选** ——
                             * 密码本里正确的那个可能就排在后面。只有"解出了真东西、只是比清单少"这种形状，
                             * 换密码才不可能有帮助（真机那个 apk：1238 个真文件 / 13.5 MB）。
                             */
                            bool realContentProduced = stage.Verification.ActualTotalSize > 0;

                            // 同一件事的事实位（换引擎兜底那条判据要它）：引擎**说成功**但产物没通过校验，
                            // 而盘上确实留下了非零字节 ⇒ 这个引擎进得去这一份的数据流。
                            anyCandidateWroteBytes |= realContentProduced;

                            bool listSaysNoEncryption =
                                realContentProduced && stage.List is { Success: true, IsEncrypted: false };

                            bool sameAsPrevious =
                                realContentProduced &&
                                previousVerification != null &&
                                previousVerification.ActualFileCount == stage.Verification.ActualFileCount &&
                                previousVerification.ActualTotalSize == stage.Verification.ActualTotalSize;

                            previousVerification = stage.Verification;

                            string? stopReason = !hasMoreCandidates
                                ? "没有更多候选可试，按失败收场"
                                : stoppedByStopRequest
                                    ? "已按「停止后续」中断，剩余候选不再尝试"
                                    : listSaysNoEncryption
                                        ? $"这个包没有加密（清单里没有加密条目），换密码不会改变结果，"
                                          + $"剩余 {maxPasswordAttempts - attemptedCandidates} 个候选不再尝试"
                                        : sameAsPrevious
                                            ? $"与上一个候选的结果完全相同，换密码不会改变结果，"
                                              + $"剩余 {maxPasswordAttempts - attemptedCandidates} 个候选不再尝试"
                                            : null;

                            if (landingViolation == null && stopReason == null)
                            {
                                AppendLog(
                                    "WARN",
                                    $"{task.LogName}：结果校验 —— {stage.Verification.Message}" +
                                    "（这个候选不算数，继续尝试下一个候选密码）");

                                await DiscardStageProductsAsync(task, engineOutputPath, cancellationToken);
                                continue;
                            }

                            /* 结论是"与密码无关"时，收尾必须按这个走（否则会给用户指错方向）。 */
                            passwordIrrelevant = listSaysNoEncryption || sameAsPrevious;

                            /*
                             * 理由要**原样带进收尾**（2026-09-27 自查逮到的措辞 bug）：
                             * 两句结论对应的现场完全不同 —— 一个是"包根本没加密"，
                             * 一个是"加密了但换哪个候选结果都一样"。收尾那句如果一律写"没有加密"，
                             * 用户拿着一个明明加密的包会以为程序搞错了。
                             */
                            passwordIrrelevantReason = listSaysNoEncryption
                                ? "这个包没有加密（清单里没有加密条目）"
                                : sameAsPrevious
                                    ? "连续两个候选的结果完全相同"
                                    : null;

                            AppendLog(
                                "WARN",
                                $"{task.LogName}：结果校验 —— {stage.Verification.Message}" +
                                "（" + (landingViolation != null
                                    ? "产物越界，按越界收场，不再换密码"
                                    : stopReason ?? "按失败收场") + "）");
                        }

                        // 收尾否掉结论（越界 / 超预算 / 定稿失败 / 校验未通过 / 源包没有搬成）时不许写"解压成功"。
                        // knownList 传刚校验过的那份清单：定稿那一步用**同一份**再校验，两条路不许各列一次目录。
                        bool conclusionStands = await PostProcessSuccessAsync(
                            task, engineArchivePath, selectedPassword, engineOutputPath, outputRedirectNote, sharedOutputRoot, oneClickRun, cancellationToken, stage.List);

                        if (conclusionStands && task.Status == StatusText.ExtractSuccess)
                        {
                            AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
                        }

                        break;
                    }

                    if (extractResult.DetectedErrorType == "WrongPassword")
                    {
                        /*
                         * ===== 「密码其实已经对了，坏的是数据」这一档（用户 2026-10-01 真机 `giu910`）=====
                         *
                         * 现场：一组 17.7 GiB 的 7z `-mhe`（19 GB 源包）——
                         * 候选 1/2 秒级失败（头没打开），**候选 3 把整个包解到 98%**（13 分钟），
                         * 最后 `CRC Failed in encrypted file. Wrong password? : giu\V\fulibl.net (15).mp4`。
                         * 老口径把这一句当成"这个候选不对"，于是**解压完又回去试密码**（候选 4..10），
                         * 13 分钟的产物全部丢弃、最后报「密码错误」。用户原话：
                         * 「**为什么试了密码之后再去试一次，我说过要试密码的话要在最开始的时候，而不是解压完，
                         * 这才 20G，大一点你这就是在浪费用户时间**」。
                         *
                         * 判据**只读事实**（⛔ 不比任何文案）：这一趟在暂存目录里**真解出来的东西**有多少。
                         * 「密码错」的签名是"第一份数据就过不了 ⇒ 只留 0 字节桩 / 至多一个文件"；
                         * 「密码对、有个别文件坏了」的签名是"整个包基本都解出来了、只死在那一个坏文件上"。
                         * 后者再往下试密码**没有任何意义**（密码已经被这天趟解压本身证明了）。
                         */
                        bool wrongPwProvenByThisAttempt =
                            TryMeasureProducedContent(engineOutputPath, extractResult, out int wrongPwFiles, out long wrongPwBytes);

                        // 同一件事的事实位（换引擎兜底那条判据要它）：这一趟盘上留下了**非零字节** ⇒
                        // 引擎进得去这一份的数据流（0 字节桩不算，见上面 `anyCandidateWroteBytes` 的说明）。
                        anyCandidateWroteBytes |= wrongPwBytes > 0;

                        if (wrongPwProvenByThisAttempt)
                        {
                            passwordProvenDataFailed = true;
                            provenProducts = (wrongPwFiles, wrongPwBytes);
                            break;
                        }

                        hasWrongPassword = true;
                        task.PasswordStatus = StatusText.WrongPassword;

                        // 同上：候选不对是预期之内，⛔ 不许记 WARN（那会让"成功就丢"整条失效）。
                        AppendLog("INFO", $"{task.LogName}：这个密码候选不对，继续试下一个。");
                        continue;
                    }

                    /*
                     * ===== 两义那一档：引擎自己说"密码可能不对、也可能数据坏了"（用户 2026-09-30 真机）=====
                     *
                     * 与"密码错误"同一处置：**继续试下一个候选**（与 7-Zip 侧那句
                     * `CRC Failed in encrypted file. Wrong password?` 同一口径，见 Item37SafetyTests）。
                     * ⛔ 不许像"已损坏"那样当场 break —— 那正是真机"11 个候选只试了第 1 个"的成因；
                     * 也⛔ 不许落成"密码错误"（那是把真损坏说成密码错）。
                     * 收尾时状态与原因都取 `lastResult`（= 这一次的原话），于是结论里两种可能都在。
                     */
                    if (extractResult.DetectedErrorType == EngineErrorTypes.PasswordOrCorrupted)
                    {
                        // 与上面"密码错误"那一支同一条判据：**真解出了东西** ⇒ 密码已经证实，别再试候选。
                        bool ambiguousProvenByThisAttempt =
                            TryMeasureProducedContent(engineOutputPath, extractResult, out int ambiguousFiles, out long ambiguousBytes);

                        // 同上：这也是"引擎写出过非零字节"的一次。
                        anyCandidateWroteBytes |= ambiguousBytes > 0;

                        if (ambiguousProvenByThisAttempt)
                        {
                            passwordProvenDataFailed = true;
                            provenProducts = (ambiguousFiles, ambiguousBytes);
                            break;
                        }

                        ambiguousResult ??= extractResult;
                        task.PasswordStatus = StatusText.PasswordNeed;

                        AppendLog(
                            "WARN",
                            string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                StatusText.CandidatePasswordOrCorruptedLogFormat,
                                task.FileName));

                        continue;
                    }

                    task.Status = extractResult.Status;
                    task.ErrorMessage = extractResult.Message;

                    // 同上面那处：内嵌归档 + 分卷缺失时补一句事实（第 42 条）。
                    string volumeHint = BuildEmbeddedVolumeMissingHint(task, extractResult.DetectedErrorType);

                    if (!string.IsNullOrEmpty(volumeHint))
                    {
                        task.ErrorMessage = task.ErrorMessage + " " + volumeHint;
                    }

                    AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}");

                    break;
                }

                if (!extractSuccess)
                {
                    /*
                     * ===== 换引擎再试一遍（用户 2026-10-05 拍板"补"；唯一实现 PasswordEngineFallback）=====
                     *
                     * <para><b>真机现场</b>（AGENTS.md §11.5 最后一条）：同一个包、同一条密码 ——
                     * WinRAR 6.11 退出码 0、两卷原样解出；内置 7-Zip 26.03 退出码 2、
                     * <c>Wrong password</c>、一个字节都没出来。而候选循环只用 7-Zip，
                     * <c>WrongPassword</c> 又落在"不换引擎"那一档（AGENTS.md §3）⇒ 程序把
                     * "这个引擎吃不下这个 ZIP"如实报成了「达到密码尝试上限」——一条假结论。</para>
                     *
                     * <para>⛔ **正常路径一个字不改**：候选循环**内部**遇到 <c>WrongPassword</c>
                     * 照旧换下一个候选、照旧不换引擎（省时间）。这里只在"循环已经跑完 + 失败是密码类 +
                     * 归档确实加密 + 本机有 WinRAR"四条同时成立时才动，用的还是**同一批候选**。</para>
                     *
                     * <para>位置：紧跟候选循环之后、失败结论之前 —— 命中就按"这个密码是对的"
                     * 直接走收尾（与循环里成功那一支**同一个出口**），否则落到下面那段原样的失败处理。</para>
                     */
                    PasswordFallbackTriggerFacts fallbackFacts = BuildPasswordFallbackFacts(
                        extractSuccess: false,
                        hasWrongPassword,
                        ambiguousResult != null,
                        passwordProvenDataFailed,
                        passwordIrrelevant,
                        stoppedByStopRequest,
                        lastResult,
                        attemptedCandidates,
                        maxPasswordAttempts,
                        candidates.Count,
                        task.IsEncrypted || PasswordProbe.ShouldSkipEmptyPassword(preflightList) || preflightSaidEncryptedHeaders,
                        anyCandidateWroteBytes);

                    if (PasswordEngineFallback.ShouldFallBack(fallbackFacts))
                    {
                        PasswordCandidatesStep.FallbackCandidateSlice fallbackSlice = BuildFallbackCandidateSlice(candidates, maxPasswordAttempts);

                        PasswordEngineFallbackOutcome fallback = await PasswordEngineFallback
                            .TryAsync(new PasswordFallbackRequest
                            {
                                ArchivePath = engineArchivePath,
                                Candidates = fallbackSlice.Values,
                                TargetDirectory = engineOutputPath,
                                Label = task.FileName,
                                FailedEngineName = ResolveFailedEngineName(lastResult),
                                FailedErrorType = lastResult?.DetectedErrorType,

                                // 与循环里同一个清法（⛔ 不在这里另写一份"怎么清"）。
                                ResetTarget = token => DiscardStageProductsAsync(task, engineOutputPath, token),

                                // 与循环里同一个描述器（⛔ 绝不出明文）。
                                DescribeCandidate = (_, ordinal) => fallbackSlice.Describe(ordinal),

                                Log = AppendLog,
                                MainEngine = _archiveEngine,
                                CancellationToken = cancellationToken
                            })
                            .ConfigureAwait(true);

                        if (fallback.Succeeded)
                        {
                            /*
                             * 兜底报"命中"只说明 WinRAR 的退出码是 0 且产物目录里多出了东西 ——
                             * **不说明那些东西是对的**。所以收尾**先过这一层的结果校验**，
                             * 校验不过 ⇒ 这个候选不算数（不发布、不翻结论、现场留着）。
                             */
                            bool accepted = await FinishFallbackSuccessAsync(
                                    task,
                                    engineArchivePath,
                                    fallback.HitPassword ?? string.Empty,
                                    engineOutputPath,
                                    outputRedirectNote,
                                    sharedOutputRoot,
                                    oneClickRun,
                                    cancellationToken)
                                .ConfigureAwait(true);

                            /*
                             * ⛔ 这里**只置位、不 return**：下面那段"失败收尾"整块跳过（`extractSuccess` 就是它的
                             * 闸门），而方法**尾部**那段收尾（EndTime / 进度文案「完成」/ Operation）照旧要跑 ——
                             * 早退会让成功的一单永远停在"处理中"（显示口径那条口径见 AGENTS.md §11.5）。
                             */
                            extractSuccess = accepted;
                        }
                        else
                        {
                            passwordConclusionNote += fallback.Note;
                        }
                    }
                    else if (PasswordEngineFallback.ShouldReportMissingEngine(fallbackFacts))
                    {
                        /*
                         * 判据成立、本机没有 WinRAR ⇒ 不跑，**但必须说清**（用户 2026-10-05 口径：
                         * ⛔ 不许静默跳过、⛔ 不许把没跑过的兜底说成跑过了）。
                         */
                        passwordConclusionNote += PasswordEngineFallback
                            .ReportMissingEngine(task.FileName, ResolveFailedEngineName(lastResult), AppendLog)
                            .Note;
                    }
                    else if (PasswordEngineFallback.ShouldReportIndistinguishableEvidence(fallbackFacts))
                    {
                        /*
                         * 判据成立、但**主引擎已经写出过字节** ⇒ 不换引擎（证据分辨不出来，
                         * 见 `PasswordFallbackTriggerFacts.MainEngineWroteBytes`）。
                         * ⛔ 什么都不做、⛔ 结论一个字都不改，只写一行如实说明（不许静默跳过）。
                         */
                        PasswordEngineFallback.ReportIndistinguishableEvidence(
                            task.FileName,
                            ResolveFailedEngineName(lastResult),
                            AppendLog);
                    }
                }

                if (!extractSuccess)
                {
                    RecordPartialPublishFacts(
                        task,
                        engineOutputPath,
                        engineArchivePath,
                        selectedPassword,
                        lastLayerManifestEntries,
                        lastResult);

                    /*
                     * 产物校验始终没过的收场（用户 2026-09-24 要求）：
                     * 状态取**更准确**的那一个 —— 试过密码的用「密码错误」语义（用户会先去核对密码本），
                     * 原因里必须同时给出三件事：校验的数字、试了几个候选、以及"产物始终不完整"。
                     *
                     * 判据里的第二个条件（`task.OutputVerification == Failed`）是**结构化**的，不是比对文案：
                     * 收尾可能给出了**更具体**的原因（产物越界 / 超预算 / 定稿失败 / 同名冲突跳过），
                     * 那些分支都会把校验结论落回 NotAttempted —— 只有"收尾的结论就是校验未通过"这一种，
                     * 才该在这里被换成带候选数量的那版措辞。否则"越界"那句唯一能行动的信息会被吃掉。
                     *
                     * ⛔ 绝不允许落到成功：这是不变量 6 在这一条路上的落点。
                     */
                    if (lastVerification is { Verified: false } failedVerification &&
                        task.OutputVerification == OutputVerificationOutcome.Failed)
                    {
                        /*
                         * 结论归给谁？三档互斥，全部读**事实**（⛔ 不比对文案）：
                         * · passwordIrrelevant（包没加密 / 候选结果始终一样）→ 是**产物不完整**，与密码无关；
                         * · stoppedByStopRequest（用户中途喊停）→ 是**没试完**，不能说"密码错了"；
                         * · 其余才是真的"试过密码都不对"。
                         */
                        bool verdictIsPasswordProblem =
                            (hasWrongPassword || attemptedCandidates > 1) && !passwordIrrelevant && !stoppedByStopRequest;

                        task.Status = verdictIsPasswordProblem ? StatusText.WrongPassword : StatusText.ExtractFailed;

                        task.PasswordStatus = verdictIsPasswordProblem
                            ? StatusText.WrongPassword
                            : passwordIrrelevant
                                ? StatusText.PasswordNotNeeded
                                : task.PasswordStatus;

                        string verdictTail = passwordIrrelevant
                            ? $"（已试 {attemptedCandidates} 个候选，产物始终不完整；{passwordIrrelevantReason}，问题不在密码上）"
                            : stoppedByStopRequest
                                ? $"（已试 {attemptedCandidates} 个候选，产物始终不完整；已按「停止后续」中断，剩余候选未尝试）"
                                : $"（已试 {attemptedCandidates} 个候选，产物始终不完整）";

                        task.ErrorMessage = $"{failedVerification.FailureMessage}{verdictTail}";

                        /*
                         * 机器可判的两个事实必须一起落（用户 2026-09-24 要求）：
                         * · OutputVerification = Failed —— 汇总/失败清单据此把它算进失败侧
                         *   （⛔ 不是靠比对"校验未通过"这句中文）；
                         * · Outcome = Failed —— 删源 / 搬源 / 续解 / 危险模式删除四条路都读它，
                         *   这一档一律不放行。
                         */
                        task.OutputVerification = OutputVerificationOutcome.Failed;
                        task.VerifyMessage = failedVerification.FailureMessage;
                        task.Outcome = TaskOutcome.Failed;

                        AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}");
                    }
                    else if (task.Outcome is TaskOutcome.Failed or TaskOutcome.Skipped or TaskOutcome.PartiallyCompleted)
                    {
                        /*
                         * 收尾（或上面那条候选收尾）**已经给出了结论**：产物越界 / 超预算 / 定稿失败 /
                         * 同名冲突按用户选择跳过 —— 那些分支各自写好了状态与原因，这里一个字都不许覆盖。
                         *
                         * 为什么必须挡这一下（实测踩到的坑）：不挡的话下面那支会拿 `lastResult`（**引擎**那次的
                         * "成功"）把状态改回「解压成功」—— 引擎说成功、产物不行的那一整类结论就被抹掉了，
                         * 那正是本次要修的 bug 在收尾处的翻版。判据是**机器终态枚举**，不是比对文案。
                         */
                    }
                    else
                    {
                        // 与"先测试再解压"分支同一条规则：试到上限 ≠ 候选全都试过、更 ≠ 密码错误（AGENTS.md §9.2）。
                        bool stoppedByAttemptLimit = candidatesTruncated && hasWrongPassword;

                        /*
                         * ⚠ 与上面"先测试再解压"那条分支**同一个唯一出口**（说明见那一处 2026-10-13 那条注释）：
                         * "这一份可证是某个还没凑齐的分卷组的一员" ⇒ 结论是**缺卷**，⛔ 不是密码问题、⛔ 不是失败。
                         * ⚠ `requireNothingElseBeside: true`（单层路专用，理由同上）。
                         */
                        if (RecursiveExtractor.IsPieceOfIncompleteGroup(task.CurrentPath, requireNothingElseBeside: true))
                        {
                            task.Status = StatusText.VolumeMissing;
                            task.ErrorMessage =
                                "分卷缺失：这一片属于某个分卷组，而同组的入口包不在这一层 —— "
                                + "按「缺卷」处理（⛔ 不是「密码不对」，也⛔ 不是「解压失败」）；"
                                + "片原样留着，交给批末/跨链那一套去凑。";

                            // 与另一条收尾链同一口径：**结论是缺卷就必须进"缺卷留到最后再判"那份名单**
                            // （唯一出口，见那一处的说明；⛔ 不碰 Status / ErrorMessage，⛔ 幂等）。
                            RememberDeferredVolumeDeficit(task, task.CurrentPath);

                            AppendLog("WARN", $"{task.LogName}：{task.ErrorMessage}");
                        }
                        else if (stoppedByAttemptLimit)
                        {
                            task.Status = StatusText.PasswordAttemptLimitReached;
                            task.PasswordStatus = StatusText.PasswordNeed;
                            task.ErrorMessage =
                                $"已达到密码尝试上限：本层试了 {maxPasswordAttempts} 个候选（共 {candidates.Count} 个），未能确认密码。";
                        }
                        else if (passwordProvenDataFailed)
                        {
                            /*
                             * 密码**已经证实**（这一趟真解出了内容）之后的失败 = 数据层面。
                             * 收场落「文件损坏」而⛔ 不是「密码错误」（用户会拿着密码本白核对半天），
                             * 并如实点名"解出了多少、失败的是数据"——用户 2026-10-01 真机那一单就是
                             * 「1 个文件 CRC 失败、另外 557 个都好好的」。
                             */
                            task.Status = StatusText.Corrupted;
                            task.PasswordStatus = StatusText.PasswordCorrect;
                            task.ErrorMessage = string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                StatusText.PasswordProvenDataCorruptedFormat,
                                provenProducts.Files,
                                provenProducts.Bytes);
                            task.Outcome = TaskOutcome.Failed;

                            AppendLog(
                                "WARN",
                                $"{task.LogName}：{task.ErrorMessage}"
                                + "（密码已经由这一趟解压本身证实 —— 不再往下试密码候选，"
                                + "也不再把已经解出来的内容当成'密码候选不对'的牺牲品）");
                        }
                        else if (ambiguousResult != null)
                        {
                            /*
                             * 两义那一档优先于"密码错误"（用户 2026-09-30 真机）：引擎自己说过
                             * "密码可能不对、也可能数据坏" —— 这一层只要出现过这种原话，
                             * 结论就必须同时保留两种可能，⛔ 不许被后面某个候选的"密码错误"
                             * 盖成一句断言（那正是"单独定原因"）。原话在 ErrorMessage 里带着。
                             */
                            task.Status = ambiguousResult.Status;
                            task.PasswordStatus = StatusText.PasswordNeed;
                            task.ErrorMessage = ambiguousResult.Message;

                            AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}");
                        }
                        else if (hasWrongPassword)
                        {
                            task.Status = StatusText.WrongPassword;
                            task.PasswordStatus = StatusText.WrongPassword;
                            task.ErrorMessage = "密码错误或缺少正确密码";
                        }
                        else if (skippedOnlyEmptyPasswordBecauseEncrypted)
                        {
                            /*
                             * 加密包 + 一个候选都没有（只跳过的那一档空密码）：如实报"没有可用密码"，
                             * ⛔ 不许落到"未知解压失败"（那是"我们不知道"的意思，这里我们很清楚）。
                             */
                            task.Status = StatusText.WrongPassword;
                            task.PasswordStatus = StatusText.PasswordNeed;
                            task.ErrorMessage =
                                "整包已加密，但这次没有任何可用密码（空密码对已加密的包不可能成立，已跳过，"
                                + "没有浪费一次整包解压）。";

                            AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}");
                        }
                        else if (lastResult != null)
                        {
                            /*
                             * 候选循环里那条失败结论会走到这里**再落一次**（状态与原因都按最后那次引擎结果来）。
                             *
                             * ⚠ 为什么"容器里装的是分卷第一卷"那句补充必须**在这里也补一遍**（第 42 条实测踩到）：
                             * 上面那个分支里我们已经把补充说明接在 `task.ErrorMessage` 后面了，
                             * 但这里会把整条消息**重新赋值**（= 覆盖），于是失败清单里又只剩引擎那句
                             * "后续卷、缺少首卷"——与事实正好相反。日志里有、清单里没有，用户看到的还是错的。
                             */
                            task.Status = lastResult.Status;
                            task.ErrorMessage = lastResult.Message;

                            string volumeHint = BuildEmbeddedVolumeMissingHint(task, lastResult.DetectedErrorType);

                            if (!string.IsNullOrEmpty(volumeHint))
                            {
                                task.ErrorMessage = task.ErrorMessage + " " + volumeHint;
                            }
                        }
                        else
                        {
                            task.Status = StatusText.ExtractFailed;
                            task.ErrorMessage = "未知解压失败";
                        }

                        if (task.Status == StatusText.WrongPassword || task.Status == StatusText.PasswordAttemptLimitReached)
                        {
                            AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}");
                        }
                    }
                }

                /*
                 * ===== 收尾那行要点名"哪几条候选没试到"（用户 2026-10-05 真机第七批，要求 ①）=====
                 *
                 * 现场：候选共 12 个（空密码 + 密码本 11 条）、每层上限 10 ⇒ **第 11 条从没被试过**，
                 * 而日志只说「达到密码尝试上限」—— 用户看不出"是哪一条没试"，也就无从决定
                 * 要不要调上限 / 调顺序。点名用的是**既有描述器**（形如「尝试密码列表第 11 项」），
                 * ⛔ 绝不出明文（判据与折行都在 `PasswordCandidateGap`，两条解压路共用）。
                 *
                 * ⚠ 只在**真被每层上限截断**时才说（`candidatesTruncated`）：用户点了「停止后续」时
                 * 剩余候选同样是"没试到"，但那不是上限卡住的 —— 说成"上限卡住的"会指错方向
                 * （那一条的现场已经在候选循环里写过「已按「停止后续」中断」）。
                 */
                if (!extractSuccess && candidatesTruncated && !stoppedByStopRequest)
                {
                    PasswordCandidateGap.Gap? untriedCandidates = PasswordCandidateGap.Describe(
                        candidates,
                        maxPasswordAttempts,
                        maxPasswordAttempts + 1,
                        (item, ordinal) => _passwordService.BuildTryPasswordLogText(item, ordinal));

                    if (untriedCandidates.HasValue)
                    {
                        AppendLog(
                            "WARN",
                            PasswordCandidateGap.BuildLogLine(task.FileName, attemptLimit, untriedCandidates.Value));

                        passwordConclusionNote += PasswordCandidateGap.BuildConclusionSuffix(untriedCandidates.Value);
                    }
                }
            }

            /*
             * 把预检的"文件名已加密"结论恢复回来（见上面 preflightSaidEncryptedHeaders 的说明）。
             * 条件收得最紧：预检说过、而且这一层最后得到的**只是**"密码没通过"。
             */
            RestoreEncryptedHeadersConclusion(task, preflightSaidEncryptedHeaders, encryptedHeadersMessage);

            /*
             * ===== 密码类结论上那两句补充（位置刻意在"文件名已加密"恢复之后）=====
             *
             * 两件事都只在这条路上才有值（判据不成立时是空串 ⇒ 正常路径一个字都不改）：
             * ① **换引擎兜底**跑过 / 没跑成（"已换 WinRAR 再试过一遍" / "没找到 WinRAR，所以没换引擎再试"）；
             * ② **候选被每层上限截断**时点名"哪几条没试到"。
             *
             * ⛔ 必须排在上面那一支之后：它会把整条 ErrorMessage **重新赋值**（= 覆盖），
             * 写在它前面等于白写。
             */
            if (!string.IsNullOrEmpty(passwordConclusionNote) && !string.IsNullOrEmpty(task.ErrorMessage))
            {
                task.ErrorMessage += passwordConclusionNote;
            }

            // 密码类失败登记到本批，批次结束后合并成一次提示（不再在任务循环里逐个弹模态框）。
            RecordPasswordFailure(task);

            task.EndTime = DateTime.Now;
            task.ElapsedText = task.StartTime.HasValue
                ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                : "-";

            /*
             * 实时进度到此为止：把百分比与"长时间无响应"提示一起清掉。
             *
             * 光靠 EndTime 已经能让 HasLiveProgress 判否（界面上不会再显示百分比），
             * 但字段本身留着会变成一颗定时炸弹：将来任何一处把 EndTime 清空（重跑、重置），
             * 上一轮的 87% 就会莫名其妙地重新出现在界面上。收尾时清干净是唯一稳妥的做法。
             */
            task.ClearProgress();

            task.Operation = StatusText.OpWaiting;

            /*
             * 进度文案只在"这一单没有失败 / 取消结论"时才写"完成"。
             * 越界、超预算、递归部分完成这些分支自己写了 ProgressFailed，取消写的是"已取消"，
             * 无条件覆盖会让任务详情窗口出现"状态：解压失败 / 进度：完成"这种自相矛盾的显示。
             */
            if (task.ProgressText != StatusText.ProgressFailed &&
                task.ProgressText != StatusText.ProgressCancelled)
            {
                task.ProgressText = StatusText.ProgressCompleted;
            }

            task.LastUpdatedTime = DateTime.Now;
        }

        /// <summary>
        /// ⛔ 真正的 APK 不碰：不解压、不递归、不改名（用户 2026-10-10 拍板）。判据 = 内容证据，
        /// 唯一出口 <c>AndroidPackageDetector</c>；判不出 ⇒ 照旧照常处理。
        /// </summary>
        /// <returns>true = 这一单到此为止（状态与终态已经写好）。</returns>
        /// <summary>
        /// ⛔ 真正的 APK 不碰：不解压、不递归、不改名（用户 2026-10-10 拍板）。判据 = 内容证据，
        /// 唯一出口 <c>AndroidPackageDetector</c>；判不出 ⇒ 照旧照常处理。
        /// </summary>
        /// <returns>true = 这一单到此为止（状态与终态已经写好）。</returns>
        private bool TrySkipAndroidPackage(ArchiveTask task)
        {
            if (!AndroidPackageDetector.IsAndroidPackage(task.CurrentPath))
            {
                return false;
            }

            task.IsArchive = false;
            task.Status = StatusText.Skipped;
            task.Operation = StatusText.OpSkip;
            task.ProgressText = StatusText.ProgressCompleted;
            task.ErrorMessage = StatusText.AndroidPackageSkipped;

            // 机器终态显式落（与"未识别 ⇒ 跳过"那条同一个口径：只补 EndTime 会让终态收口按失败接手）。
            task.Outcome = TaskOutcome.Skipped;
            task.EndTime ??= DateTime.Now;

            AppendLog(
                "WARN",
                $"{task.LogName}：这是 Android 应用安装包（ZIP 里同时有 {AndroidPackageDetector.ManifestEntryName} 与 "
                + $"{AndroidPackageDetector.DexEntryName}）—— 按设置不解压、不递归、不改名，源文件一个字节都不动。"
                + "要处理它请用专门的 APK 工具。");

            return true;
        }

        /// <summary>
        /// 交给引擎的那一份归档路径：先修"坏名字的第一卷"（只改我们自己产出的那一卷），
        /// 再优先用盘上同一份的规范名（判据全在读事实，唯一出口 <c>RecursiveExtractor.PreferCanonicalSiblingName</c>）。
        /// ⛔ 只影响交给引擎的那一份：task.CurrentPath 与源包那一档一个字都不动。
        /// </summary>
        private string ResolveEngineArchivePath(ArchiveTask task, string engineArchivePath)
        {
            /*
             * 第二大步给了规范名入口（`X.7z.001` 这类硬链接别名）⇒ 引擎就用它。
             * ⛔ 任务账/源包/删除判据仍读 task.CurrentPath；这里只换“交给引擎的那一份”。
             */
            if (!string.IsNullOrWhiteSpace(task.EngineEntryPathOverride)
                && File.Exists(task.EngineEntryPathOverride))
            {
                return task.EngineEntryPathOverride;
            }

            string resolved = TryRepairBrokenVolumeChainName(task, engineArchivePath);

            return RecursiveExtractor.PreferCanonicalSiblingName(resolved);
        }

        /// <summary>
        /// 把预检的"文件名已加密"结论恢复回来：条件收得最紧 —— 预检说过、而且这一层最后得到的**只是**"密码没通过"。
        /// </summary>
        private void RestoreEncryptedHeadersConclusion(
            ArchiveTask task,
            bool preflightSaidEncryptedHeaders,
            string? encryptedHeadersMessage)
        {
            if (!preflightSaidEncryptedHeaders ||
                (task.Status != StatusText.WrongPassword && task.Status != StatusText.ExtractFailed))
            {
                return;
            }

            task.Status = StatusText.EncryptedHeaders;
            task.PasswordStatus = StatusText.PasswordNeed;

            if (!string.IsNullOrWhiteSpace(encryptedHeadersMessage))
            {
                // 连原因也一起恢复：泛泛的"密码错误"会让用户去翻密码本，
                // 而这里的结论是"先给它一个密码，它才肯把内容清单给你看"。
                task.ErrorMessage = encryptedHeadersMessage;
            }

            AppendLog(
                "WARN",
                $"{task.LogName}：这次拿到的是「{StatusText.EncryptedHeaders}」的结论" +
                "（不是泛泛的密码错误）—— 连内容清单都读不出来，需要正确密码才能列出内容。");
        }

        /// <summary>
        /// 阶段一：入仓（契约 §2.1）。从这一刻起本任务所有中间动作都在私有暂存目录里完成，
        /// 源目录与最终目录一个字节都不会被写。返回 false = 这一单停下（原因已经写进任务与日志）。
        /// </summary>
        private async Task<(bool Ok, string StageDirectory, string EngineOutputPath)> TryPrepareStagingAsync(
            ArchiveTask task,
            string outputPath,
            CancellationToken cancellationToken)
        {
            string stageDirectory = string.Empty;
            string engineOutputPath = string.Empty;
            stageDirectory = string.Empty;
            engineOutputPath = string.Empty;

            /*
             * ===== 阶段一：入仓（stage，契约 §2.1）=====
             *
             * 从这一刻起，本任务**所有**中间动作（抠内嵌归档之后的解压、第一层、内层分卷、递归）
             * 都在私有暂存目录 <c>&lt;work&gt;\&lt;taskId&gt;\stage</c> 里完成，
             * 源目录与最终目录一个字节都不会被写。
             *
             * 这条就是用户那句"分卷文件你居然又解压到外面来了"的根治点：
             * 旧实现把引擎的输出目录直接设成 destDir，内层分卷于是和内容物一起躺在用户目录里；
             * 一旦失败，用户目录里还留着半成品。
             */
            stageDirectory = _pathService.BuildTaskStageDirectory(task);

            if (string.IsNullOrWhiteSpace(stageDirectory))
            {
                task.Status = StatusText.ExtractFailed;
                task.ProgressText = StatusText.ProgressFailed;
                task.ErrorMessage = "无法确定暂存目录（工作区不可用）";
                AppendLog("ERROR", $"{task.LogName}：{task.ErrorMessage}");
                return (false, string.Empty, string.Empty);
            }

            // 记下"这个任务真正用的工作区目录"：收尾时的空壳清理要按它来，
            // **不能事后重算**（taskId 含源路径哈希，而成功路径会回写 CurrentPath，见字段说明）。
            TaskWorkspaceDirectories[task] = Path.GetDirectoryName(stageDirectory.TrimEnd('\\', '/')) ?? string.Empty;

            string? stageFailure = await PrepareStageDirectoryAsync(task, stageDirectory, cancellationToken);

            if (!string.IsNullOrWhiteSpace(stageFailure))
            {
                task.Status = StatusText.ExtractFailed;
                task.ProgressText = StatusText.ProgressFailed;
                task.ErrorMessage = stageFailure;
                AppendLog("ERROR", $"{task.LogName}：{stageFailure}");
                return (false, string.Empty, string.Empty);
            }

            // 引擎写盘的地方 = 暂存目录。**不是** outputPath —— 那是定稿的目标。
            engineOutputPath = stageDirectory;

            AppendLog("INFO", $"{task.LogName}：入仓目录 {stageDirectory}（中间产物先落这里，定稿时才搬进 {outputPath}）");

            return (true, stageDirectory, engineOutputPath);
        }

        /// <summary>
        /// 并发下的"最终目录"占位：只对"目录还不存在、且是每包一个目录的落点"占位（只占不建），
        /// 占不到就按同一套 `名字(1)` 约定换一个；收尾时由 ProcessExtractTaskAsync 释放。
        /// 返回占位后的输出目录 + 可能产生的改名说明。
        /// </summary>
        private (string OutputPath, string RedirectNote) ClaimFinalOutputDirectoryIfNeeded(
            ArchiveTask task,
            string outputPath,
            bool landsInSourceDirectory,
            bool sharedOutputRoot,
            string outputRedirectNote)
        {
            if (task.IsContinuationTask ||
                landsInSourceDirectory ||
                sharedOutputRoot ||
                string.IsNullOrWhiteSpace(outputPath) ||
                Directory.Exists(outputPath))
            {
                return (outputPath, outputRedirectNote);
            }

            return (ClaimOutputDirectory(outputPath, task, ref outputRedirectNote), outputRedirectNote);
        }

        /// <summary>
        /// 登记"失败时盘上还剩什么"（部分完成发布的输入）：先按唯一出口 `ProducedContentGate` 数一次
        /// 真解出来的非空文件（终态分类的判据输入），再交候选表，最后把引擎原话落一次日志。
        /// </summary>
        private void RecordPartialPublishFacts(
            ArchiveTask task,
            string engineOutputPath,
            string engineArchivePath,
            string selectedPassword,
            IReadOnlyList<(string Path, long Size)>? lastLayerManifestEntries,
            ArchiveOperationResult? lastResult)
        {
            /*
             * ===== 登记"失败时盘上还剩什么"（部分完成发布的输入，用户 2026-10-02）=====
             *
             * 位置刻意选在**候选循环已经跑完、结论已经定下来之后**：
             * · 产物目录里此刻留着的是**最后一个候选**留下的东西（每个候选开工前会清一次），
             *   所以尺子与现场必须是同一次候选的 —— `lastLayerManifestEntries` 与
             *   `lastResult` 都跟着循环滚动，天然对齐；
             * · 密码取 `selectedPassword`（最后一个候选的）：密码不对时收尾那次懒取清单
             *   照样列不出来 ⇒ 判"没有清单" ⇒ 一个字节都不发布。
             *
             * ⛔ 这里不判断③页开关（收尾时才读）—— 详见 RecordPartialPublishCandidate 的说明。
             */
            /*
             * "这一单整体有没有内容物解出"（终态分类那一档的判据输入，用户 2026-10-16 口径）：
             * 用**唯一出口** `ProducedContentGate`（与"密码已被证实"同一把尺子，⛔ 不另定阈值）
             * 数一次产物目录里真解出来的非空文件。
             */
            bool producedContent =
                ProducedContentGate.TryMeasure(
                    engineOutputPath,
                    lastResult?.ReportedSubItemErrors ?? 0,
                    lastResult?.FailedEntryNames?.Count ?? 0,
                    out int producedFiles,
                    out _)
                && producedFiles >= ProducedContentGate.MinimumFiles;

            RecordPartialPublishCandidate(
                task,
                engineOutputPath,
                engineArchivePath,
                selectedPassword,
                lastLayerManifestEntries,
                lastResult?.FailedEntryNames,
                lastResult?.ReportedSubItemErrors ?? 0,
                engineReportedFailure: lastResult != null && !lastResult.Success,
                detectedErrorType: lastResult?.DetectedErrorType,
                chainProducedContent: producedContent);

            /*
             * ===== 引擎原话落日志（用户 2026-09-27：「引擎原话从不落日志」）=====
             *
             * 位置刻意选在**结论已经定下来之后**（`lastResult` / `task.Status` 都写好了），
             * 而且整层只写一次：
             * · 候选循环里每个错密码都记一条 ERROR 是噪声（十个候选十条），
             *   而真正要看的恰恰是**最后那次**（走得最远的那一次，与失败原因同一份）；
             * · 详细档在循环里已经逐候选写过命令行与原话了，这里不重复。
             *
             * ⛔ 出口只有 <see cref="EngineOutputLog"/> 一个（递归路径同一个）。
             */
            EngineOutputLog.LogFailure(AppendLog, task.FileName, lastResult);
        }
    }
}

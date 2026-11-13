using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 单层首解阶段：内嵌归档直读 / 先测试密码再解压（`TestBeforeExtract`）/ 测试通过后的单层首解与收尾。
    ///
    /// <para>返回 true = 走了这两条路（不再进普通候选循环）；false = 普通模式，交给候选循环。
    /// ⛔ 判据/文案/终态/收尾仍走协调器既有出口。</para>
    /// </summary>
    internal sealed class ExtractionFirstAttempt
    {
        internal async Task<bool> RunAsync(
            ExtractionCoordinator host,
            ExtractionRunState state,
            ArchiveTask task,
            EmbeddedZipProbeResult? directZip,
            ArchiveListResult? preflightList,
            List<string> candidatePasswords,
            ProgressHeartbeatStep.Sink progressSink,
            bool shouldTestPasswordBeforeExtract,
            int maxPasswordAttempts,
            List<PasswordItem> candidates,
            bool candidatesTruncated,
            int attemptLimit,
            string engineArchivePath,
            string engineOutputPath,
            string outputRedirectNote,
            bool sharedOutputRoot,
            ExtractOptions extractOptions,
            bool oneClickRun,
            CancellationToken cancellationToken)
        {
            ArchiveOperationResult? lastResult = state.LastResult;
            string selectedPassword = state.SelectedPassword;
            bool passwordConfirmed = state.PasswordConfirmed;
            ArchiveOperationResult? ambiguousTestResult = state.AmbiguousTestResult;

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
                await host.RunEmbeddedZipDirectExtractionAsync(
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
                return true;
            }

            if (shouldTestPasswordBeforeExtract)
            {
                task.Operation = StatusText.OpTest;
                task.Status = StatusText.Testing;
                task.ProgressText = StatusText.ProgressProcessing;
                task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;

                host.AppendLog("INFO", $"{task.LogName}：开始解压前测试。");

                for (int i = 0; i < maxPasswordAttempts; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    PasswordItem candidate = candidates[i];
                    string password = candidate.Value ?? string.Empty;

                    host.AppendLog("INFO", $"{task.LogName}：测试密码候选 {i + 1}/{maxPasswordAttempts}，{host.PasswordService.BuildTryPasswordLogText(candidate, i + 1)}");

                    ArchiveOperationResult testResult = await host.ArchiveEngine.TestAsync(
                        host.ProgressHeartbeat.BuildTrackedRequest(engineArchivePath, password, null, progressSink),
                        cancellationToken);

                    lastResult = testResult;

                    // 内部日志永远详细：普通档由任务缓冲只写文件、不上屏幕。
                    EngineOutputLog.LogVerbose(host.AppendLogPair, task.FileName, testResult);

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
                        task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;

                        host.AppendLog("INFO", $"{task.LogName}：测试通过。");
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
                        host.AppendLog("INFO", $"{task.LogName}：这个密码候选不对，继续试下一个。");
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

                        host.AppendLog(
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
                    task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;

                    host.AppendLog("ERROR", $"{task.LogName}：测试失败，原因：{testResult.Message}");
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
                        host.RememberDeferredVolumeDeficit(task, task.CurrentPath);

                        host.AppendLog("WARN", $"{task.LogName}：{task.ErrorMessage}");
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

                    task.EndTime = ArchiveFixer.Helpers.Clock.Now;
                    task.ElapsedText = task.StartTime.HasValue
                        ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                        : "-";

                    task.Operation = StatusText.OpWaiting;
                    task.ProgressText = StatusText.ProgressCompleted;
                    task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;

                    host.AppendLog("ERROR", $"解压前测试失败：{task.FileName}，原因：{task.ErrorMessage}");

                    /*
                     * 密码类失败**登记到批次**，由 ShowPasswordFailuresSummaryAsync 合并成一次提示。
                     * 原来这里（以及下面的直接解压分支）各弹一次模态框、而且用的是同步 Dispatcher.Invoke：
                     * 一批几十个错包就要点几十次，还会阻塞调用线程。
                     */
                    host.RecordPasswordFailure(task);

                    state.LastResult = lastResult;
                    state.SelectedPassword = selectedPassword;
                    state.PasswordConfirmed = passwordConfirmed;
                    state.AmbiguousTestResult = ambiguousTestResult;
                    return true;
                }

                cancellationToken.ThrowIfCancellationRequested();

                task.Operation = StatusText.OpExtract;
                task.Status = StatusText.Extracting;
                task.ProgressText = StatusText.ProgressProcessing;
                task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;

                host.AppendLog("INFO", $"{task.LogName}：测试通过，开始正式解压。");

                ArchiveOperationResult extractResult = await host.ArchiveEngine.ExtractAsync(
     host.ProgressHeartbeat.BuildTrackedRequest(engineArchivePath, selectedPassword, engineOutputPath, progressSink),
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
                    var stage = await host.VerifyStageProductsAsync(
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
                        host.PasswordService.RecordPasswordSuccess(task.CurrentPath, selectedPassword);
                    }
                    else
                    {
                        host.AppendLog(
                            "WARN",
                            $"{task.LogName}：结果校验 —— {stage.Verification.Message}" +
                            "（密码是对的，但产物不完整，按失败收场）");
                    }

                    // 收尾可能把"解压成功"顶掉（产物越界 / 超预算 / 定稿失败 / 校验未通过）：只有结论仍然成立时才敢这么写日志。
                    bool conclusionStands = await host.PostProcessSuccessAsync(
                        task, engineArchivePath, selectedPassword, engineOutputPath, outputRedirectNote, sharedOutputRoot, oneClickRun, cancellationToken, stage.List);

                    if (conclusionStands)
                    {
                        host.AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
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
                    string volumeHint = host.BuildEmbeddedVolumeMissingHint(task, extractResult.DetectedErrorType);

                    if (!string.IsNullOrEmpty(volumeHint))
                    {
                        task.ErrorMessage = task.ErrorMessage + " " + volumeHint;
                    }

                    host.AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}", DiagnosticCodes.TaskFailed, "ExtractionCoordinator.ExtractTask");
                }
                state.LastResult = lastResult;
                state.SelectedPassword = selectedPassword;
                state.PasswordConfirmed = passwordConfirmed;
                state.AmbiguousTestResult = ambiguousTestResult;
                return true;
            }

            return false;
        }
    }
}


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
    /// 候选循环之后的**失败结论**：换引擎兜底 → 「已换引擎/没换」那句 → 未试候选点名提示。
    ///
    /// <para>为什么单独成类：这一段只把候选循环留下的账整理成结论；判据与执行体仍走协调器既有出口，⛔ 文案与终态一个字不改。</para>
    /// </summary>
    internal sealed class ExtractionTaskConclusionWriter
    {
        /// <summary>返回收尾后的（extractSuccess, passwordConclusionNote）两笔账，调用方写回局部变量。</summary>
        internal async Task<(bool ExtractSuccess, string PasswordConclusionNote)> WriteAsync(
            ExtractionCoordinator host,
            ArchiveTask task,
            ExtractionRunState state,
            bool oneClickRun,
            CancellationToken cancellationToken)
        {
            bool extractSuccess = state.ExtractSuccess;
            bool hasWrongPassword = state.HasWrongPassword;
            ArchiveOperationResult? ambiguousResult = state.AmbiguousResult;
            bool passwordProvenDataFailed = state.PasswordProvenDataFailed;
            bool passwordIrrelevant = state.PasswordIrrelevant;
            bool stoppedByStopRequest = state.StoppedByStopRequest;
            ArchiveOperationResult? lastResult = state.LastResult;
            int attemptedCandidates = state.AttemptedCandidates;
            int maxPasswordAttempts = state.MaxPasswordAttempts;
            bool candidatesTruncated = state.CandidatesTruncated;
            string passwordConclusionNote = state.PasswordConclusionNote;
            bool preflightSaidEncryptedHeaders = state.PreflightSaidEncryptedHeaders;
            string? encryptedHeadersMessage = state.EncryptedHeadersMessage;
            string engineArchivePath = state.EngineArchivePath;
            string engineOutputPath = state.EngineOutputPath;
            string outputRedirectNote = state.OutputRedirectNote;
            bool sharedOutputRoot = state.SharedOutputRoot;
            int attemptLimit = state.AttemptLimit;
            List<PasswordItem> candidates = state.Candidates;
            ArchiveListResult? preflightList = state.PreflightList;
            bool anyCandidateWroteBytes = state.AnyCandidateWroteBytes;
            string selectedPassword = state.SelectedPassword;
            IReadOnlyList<(string Path, long Size)>? lastLayerManifestEntries = state.LastLayerManifestEntries;
            OutputVerificationResult? lastVerification = state.LastVerification;
            string? passwordIrrelevantReason = state.PasswordIrrelevantReason;
            (int Files, long Bytes) provenProducts = state.ProvenProducts;
            bool skippedOnlyEmptyPasswordBecauseEncrypted = state.SkippedOnlyEmptyPasswordBecauseEncrypted;


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
                PasswordFallbackTriggerFacts fallbackFacts = host.BuildPasswordFallbackFacts(
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
                    PasswordCandidatesStep.FallbackCandidateSlice fallbackSlice = host.BuildFallbackCandidateSlice(candidates, maxPasswordAttempts);

                    PasswordEngineFallbackOutcome fallback = await PasswordEngineFallback
                        .TryAsync(new PasswordFallbackRequest
                        {
                            ArchivePath = engineArchivePath,
                            Candidates = fallbackSlice.Values,
                            TargetDirectory = engineOutputPath,
                            Label = task.FileName,
                            FailedEngineName = host.ResolveFailedEngineName(lastResult),
                            FailedErrorType = lastResult?.DetectedErrorType,

                            // 与循环里同一个清法（⛔ 不在这里另写一份"怎么清"）。
                            ResetTarget = token => host.DiscardStageProductsAsync(task, engineOutputPath, token),

                            // 与循环里同一个描述器（⛔ 绝不出明文）。
                            DescribeCandidate = (_, ordinal) => fallbackSlice.Describe(ordinal),

                            Log = host.AppendLogPair,
                            MainEngine = host.ArchiveEngine,
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
                        bool accepted = await host.FinishFallbackSuccessAsync(
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
                        .ReportMissingEngine(task.FileName, host.ResolveFailedEngineName(lastResult), host.AppendLogPair)
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
                        host.ResolveFailedEngineName(lastResult),
                        host.AppendLogPair);
                }
            }

            if (!extractSuccess)
            {
                host.RecordPartialPublishFacts(
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
                            ? $"（已试 {attemptedCandidates} 个候选，产物始终不完整；已按「暂停所有操作」中断，剩余候选未尝试）"
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

                    host.AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}", DiagnosticCodes.TaskFailed, "ExtractionCoordinator.ExtractTask");
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

                        host.AppendLog(
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

                        host.AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}", DiagnosticCodes.TaskFailed, "ExtractionCoordinator.ExtractTask");
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

                        host.AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}", DiagnosticCodes.TaskFailed, "ExtractionCoordinator.ExtractTask");
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

                        string volumeHint = host.BuildEmbeddedVolumeMissingHint(task, lastResult.DetectedErrorType);

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
                        host.AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}", DiagnosticCodes.TaskFailed, "ExtractionCoordinator.ExtractTask");
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
             * （那一条的现场已经在候选循环里写过「已按「暂停所有操作」中断」）。
             */
            if (!extractSuccess && candidatesTruncated && !stoppedByStopRequest)
            {
                PasswordCandidateGap.Gap? untriedCandidates = PasswordCandidateGap.Describe(
                    candidates,
                    maxPasswordAttempts,
                    maxPasswordAttempts + 1,
                    (item, ordinal) => host.PasswordService.BuildTryPasswordLogText(item, ordinal));

                if (untriedCandidates.HasValue)
                {
                    host.AppendLog(
                        "WARN",
                        PasswordCandidateGap.BuildLogLine(task.FileName, attemptLimit, untriedCandidates.Value));

                    passwordConclusionNote += PasswordCandidateGap.BuildConclusionSuffix(untriedCandidates.Value);
                }
            }

            return (extractSuccess, passwordConclusionNote);
        }
    }
}


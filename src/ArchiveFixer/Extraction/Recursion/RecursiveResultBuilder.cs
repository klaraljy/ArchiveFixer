using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 一层失败之后的那一段收尾（候选循环之外）：
    /// 结论取哪一次 → 换引擎兜底 → 未试候选点名 → 「文件名已加密」→ 缺卷 → 层报告。
    ///
    /// <para><b>为什么单独成类</b>：这一段过去在 <c>ExtractLayerAsync</c> 里排了近三百行，
    /// 而它只依赖"本层事实"与宿主的几个既有出口（层报告、停因观察口、换引擎兜底）——
    /// 把结论组装与"解压执行"混在一个方法里，读的人无法一眼看出"失败时到底谁说了算"。</para>
    ///
    /// <para>⛔ 行为一个字不改：顺序、判据、日志、错误码、写穿的两个实例字段
    /// （<see cref="RecursiveExtractor.PasswordFallbackNote"/> / 停因已报标记）都照旧；
    /// 这里只是把同一段语句搬到另一个类。</para>
    /// </summary>
    internal static class RecursiveResultBuilder
    {
        /// <summary>
        /// 候选循环一个都没成功时的收尾：返回这一层的 <see cref="LayerOutcome"/>（成功或停下）。
        /// </summary>
        /// <param name="reportStopReason">
        /// 停因观察口。**必须由调用方（<c>ExtractLayerAsync</c>）传进来**：它带
        /// <c>[CallerFilePath]</c>，观测到的源码位置要仍落在 <c>RecursiveExtractor.cs</c>（诊断 source_file 不变）。
        /// </param>
        internal static async Task<LayerOutcome> BuildFailureAsync(
            RecursiveExtractor host,
            LayerFailureContext context,
            Action<RecursionStopReason, string> reportStopReason,
            CancellationToken cancellationToken)
        {
            /*
             * 收尾取"走得最远的那一次"的失败（`InformativeFailure`，可能为 null）——
             * 最后一次候选往往只是"连门都没进去"，它会把真原因盖掉。
             *
             * ⚠ 两义那一档**优先**（`AmbiguousPasswordFailure`）：它是"引擎自己说两种可能都有"
             * 那次的原话，比"最后一次错密码"信息量大得多，也是结论能同时保留两种可能的唯一来源。
             */
            ArchiveOperationResult? conclusion =
                context.AmbiguousPasswordFailure ?? context.InformativeFailure ?? context.LastFailure;

            RecursionStopReason reason = RecursiveExtractor.ResolvePasswordStopReason(
                context.Candidates.Count - context.SkippedCandidates,
                context.TriedCandidateCount,
                conclusion,
                context.TriedAny);

            LayerOutcome? fallbackOutcome = await TryFallbackAsync(
                    host,
                    context,
                    conclusion,
                    reason,
                    cancellationToken)
                .ConfigureAwait(false);

            if (fallbackOutcome != null)
            {
                return fallbackOutcome;
            }

            AppendUntriedCandidatesNote(host, context, reason);

            /*
             * ===== 「文件名已加密」在递归路也要成立（2026-10-05 用户拍板）=====
             *
             * 判据与文案仍只有一处（<see cref="RecursiveFailureClassifier.ApplyEncryptedHeaders"/>）；
             * 这里只负责把它的结论接回来，并在命中时钉住"已在更精确的分支里报过停因"。
             */
            (RecursionStopReason failureReason, string? encryptedHeadersMessage, bool exactReported) =
                RecursiveFailureClassifier.ApplyEncryptedHeaders(
                    reason,
                    context.ListedAnyCandidate,
                    context.SawEncryptedHeadersListFailure,
                    context.SawOtherListFailure,
                    context.LayerLabel,
                    context.EncryptedHeadersListMessage,
                    reportStopReason,
                    host.Log);

            reason = failureReason;

            if (exactReported)
            {
                // 写穿到实例字段（嵌套重入时外层不许覆盖它，见字段说明）。
                host.MarkExactStopReasonReported();
            }

            /*
             * ⚠ 2026-10-13（困难批 `run-23232323` / `run-21212121`，运行期定案）：**候选试完 / 试满上限
             * 之后还要问一次"这一份到底是什么"** —— 这是同一个出口的**第三处**（前两处：
             * 「密码对了但数据坏了」、「引擎报错」）。
             *
             * <para>判据与处置**逐字同前两处**（唯一出口 <c>IsPieceOfIncompleteGroup</c>）：
             * 可证"这一份是某个还没凑齐的分卷组的一员" ⇒ 结论是**缺卷**（跳过语义），⛔ 不是失败；
             * 片记下来（<c>RememberUnresolvedVolumePiece</c>），交给跨链/批末那一套去凑。</para>
             *
             * <para>⛔ 判据没有放宽：内容判不出 / 可证不是分卷成员 ⇒ 照旧走引擎给的那一档。</para>
             */
            if (RecursiveExtractor.IsPieceOfIncompleteGroup(context.ArchivePath))
            {
                host.Log(
                    "WARN",
                    $"{context.LayerLabel}：这一片属于某个分卷组、而同组的入口包不在这一层 ⇒ "
                    + "按「缺卷」处理（⛔ 不是「解压失败」）—— 片原样留着，交给批末/跨链那一套去凑。");

                host.RememberUnresolvedVolumePiece(context.ArchivePath);

                return LayerOutcome.Stop(
                    host.BuildLayerReport(
                        context.Item,
                        result: null,
                        succeededPassword: null,
                        overrideMessage: null,
                        manifestEntries: context.LastListedEntries,
                        overrideStatus: StatusText.VolumeMissing),
                    RecursionStopReason.MissingVolume);
            }

            RecursionLayerReport failureReport = host.BuildLayerReport(
                context.Item,
                conclusion,
                succeededPassword: null,
                overrideMessage: encryptedHeadersMessage,
                manifestEntries: context.LastListedEntries,
                overrideStatus: exactReported ? StatusText.EncryptedHeaders : null);

            return LayerOutcome.Stop(failureReport, reason);
        }

        /// <summary>
        /// 换引擎再试一遍（用户 2026-10-05 拍板"补"；唯一实现 <see cref="PasswordEngineFallback"/>）。
        ///
        /// <para>⛔ 正常路径一个字不改：循环**内部**遇到 <c>WrongPassword</c> 照旧换下一个候选、
        /// 照旧不换引擎（省时间）。这一档**只在**"循环跑完 + 失败是密码类 + 归档确实加密 +
        /// 本机有 WinRAR"四条同时成立时才动，用的还是**同一批候选**。</para>
        ///
        /// <para>返回非 null = 兜底解开（已按"这个密码是对的"走 <see cref="RecursiveExtractor.FinishSuccessfulLayerAsync"/>）；
        /// 返回 null = 没解开 / 没跑，调用方落到下面原样的失败结论。</para>
        /// </summary>
        private static async Task<LayerOutcome?> TryFallbackAsync(
            RecursiveExtractor host,
            LayerFailureContext context,
            ArchiveOperationResult? conclusion,
            RecursionStopReason reason,
            CancellationToken cancellationToken)
        {
            bool passwordClassFailure =
                conclusion != null &&
                (conclusion.IsWrongPassword || conclusion.IsNeedPassword || conclusion.IsPasswordOrCorrupted) &&
                reason is RecursionStopReason.WrongPassword
                    or RecursionStopReason.PasswordAttemptsExceeded
                    or RecursionStopReason.PasswordOrCorrupted;

            PasswordFallbackTriggerFacts fallbackFacts = new()
            {
                AlreadySucceeded = false,
                PasswordClassFailure = passwordClassFailure,
                ArchiveEncrypted = context.SawEncryptedArchive,

                /*
                 * "候选跑完" = 该试的都试了（每层上限内）：试过的个数 ≥ min(候选数 − 被跳过的, 每层上限)。
                 * ⛔ 一条都没试（没候选 / 上限 0）不算 —— 那时兜底无事可做。
                 */
                CandidatesFinished = context.TriedCandidates.Count > 0
                    && reason is RecursionStopReason.WrongPassword
                        or RecursionStopReason.PasswordAttemptsExceeded
                        or RecursionStopReason.PasswordOrCorrupted,

                UserAskedToStop = host.StopRequested?.Invoke() == true,
                HasCandidates = context.TriedCandidates.Count > 0,
                EngineAvailable = PasswordEngineFallback.IsEngineAvailable,

                /*
                 * ===== 主引擎写出过字节 ⇒ 不认 WinRAR 的命中（2026-10-05 实测）=====
                 *
                 * `InformativeFailure` 就是"某一次候选失败时，这一层的产物目录里已经留下了字节" ——
                 * 它非空即"主引擎进得去这一份的数据流"。而 WinRAR 用错密码时也会按原大小把乱码写进
                 * 同一个目录（名字 / 大小 / 时间戳与真产物一样）⇒ 两个引擎给不出可分辨的证据
                 * ⇒ 判不出 ⇒ 不换引擎、结论照旧。
                 */
                MainEngineWroteBytes = context.InformativeFailure != null
            };

            if (PasswordEngineFallback.ShouldFallBack(fallbackFacts))
            {
                PasswordEngineFallbackOutcome fallback = await PasswordEngineFallback
                    .TryAsync(new PasswordFallbackRequest
                    {
                        ArchivePath = context.ArchivePath,
                        Candidates = context.TriedCandidates,
                        TargetDirectory = context.Item.Layer.OutputPath,
                        Label = context.LayerLabel,
                        FailedEngineName = host.ResolveFailedEngineName(conclusion),
                        FailedErrorType = conclusion?.DetectedErrorType,

                        // 与循环里同一个清法（⛔ 不在这里另写一份"怎么清"）。
                        ResetTarget = _ =>
                        {
                            RecursiveExtractor.TryResetLayerOutputDirectory(context.Item.Layer.OutputPath);
                            return Task.CompletedTask;
                        },

                        DescribeCandidate = host.DescribeCandidate,
                        Log = host.Log,
                        MainEngine = host.Engine,
                        CancellationToken = cancellationToken
                    })
                    .ConfigureAwait(false);

                if (fallback.Succeeded)
                {
                    /*
                     * 这一层**不是主引擎解的** ⇒ 报告里不带主引擎那次的结果（`success: null` 让状态
                     * 落在既有的「解压成功」上，与直读那条近路同一种做法）。清单照旧取"这一层列过的那一份"：
                     * 列目录与密码无关，能列出来的包任何候选都列得出来；列不出来的（`-mhe`）本来就是
                     * Unavailable ⇒ 下游落"判不出 ⇒ 什么都不做"。
                     *
                     * ⚠ 原实现在这两支里同时更新 `lastListedEntries`，但紧接着就 return、该局部量无人再读
                     * ⇒ 不搬（同一份清单改由下面的清单恢复逻辑处理，可观察行为不变）。
                     */
                    LayerManifest recoveredManifest;

                    if (fallback.ManifestListing is { Success: true })
                    {
                        // 清单与实际解压引擎同源：优先于主引擎（7z）的清单。
                        recoveredManifest = LayerManifest.From(fallback.ManifestListing);
                    }
                    else
                    {
                        recoveredManifest = context.ListedAnyCandidate && context.CandidateManifest.Available
                            ? context.CandidateManifest
                            : LayerManifest.Unavailable("这一层没能列出清单（换引擎兜底解开的，主引擎没列出过）");
                    }

                    /*
                     * 换引擎兜底解开了、主引擎之前没能列出清单 —— 现在密码已经由兜底确认，
                     * 必须拿这个密码再向主引擎要一次清单；不能把"加密头列不出"永久当成
                     * "完整性无法确认"，否则成功链里的内层过程物永远删不掉。
                     */
                    if (!recoveredManifest.Available && !string.IsNullOrEmpty(fallback.HitPassword))
                    {
                        EncryptedHeaderManifestRecoveryResult recovered = await EncryptedHeaderManifestRecovery
                            .TryRelistAsync(
                                host.Engine,
                                context.ArchivePath,
                                fallback.HitPassword,
                                cancellationToken,
                                message => host.Log("INFO", $"{context.LayerLabel}：{message}"))
                            .ConfigureAwait(false);

                        if (recovered.Manifest.Available)
                        {
                            recoveredManifest = recovered.Manifest;
                        }
                    }

                    return await host.FinishSuccessfulLayerAsync(
                            context.Item,
                            null,
                            recoveredManifest,
                            fallback.HitPassword,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                host.PasswordFallbackNote = fallback.Note;

                return null;
            }

            if (PasswordEngineFallback.ShouldReportMissingEngine(fallbackFacts))
            {
                /*
                 * 判据成立、本机没有 WinRAR ⇒ 不跑，**但必须说清**（用户 2026-10-05 口径：
                 * ⛔ 不许静默跳过、⛔ 不许把没跑过的兜底说成跑过了）。
                 */
                host.PasswordFallbackNote = PasswordEngineFallback
                    .ReportMissingEngine(context.LayerLabel, host.ResolveFailedEngineName(conclusion), host.Log)
                    .Note;

                return null;
            }

            if (PasswordEngineFallback.ShouldReportIndistinguishableEvidence(fallbackFacts))
            {
                /*
                 * 判据成立、但**主引擎已经写出过字节** ⇒ 不换引擎（证据分辨不出来）。
                 * ⛔ 什么都不做、⛔ 结论一个字都不改，只写一行如实说明（不许静默跳过）。
                 */
                PasswordEngineFallback.ReportIndistinguishableEvidence(
                    context.LayerLabel,
                    host.ResolveFailedEngineName(conclusion),
                    host.Log);
            }

            return null;
        }

        /// <summary>
        /// ===== 收尾那行要点名"哪几条候选没试到"（用户 2026-10-05 真机第七批，要求 ①）=====
        ///
        /// <para>与单层路**同一个出口**（<see cref="PasswordCandidateGap"/>）：候选共 12 个、每层上限 10 ⇒
        /// **第 11 条从没被试过**，只写一句「达到密码尝试上限」用户看不出是哪一条。
        /// ⛔ 只在**真被每层上限截断**时才说；用户点了「停止后续」时剩余候选同样没试到，
        /// 但那是另一回事（循环里已经写过它）。</para>
        ///
        /// <para>⚠ 序号从**循环自己的计数**接着往下数（<c>TriedCandidates.Count + 1</c>）：
        /// 被跳过的空密码不占序号，拿列表下标当序号会与日志里已经出现过的那几行对不上。</para>
        /// </summary>
        private static void AppendUntriedCandidatesNote(
            RecursiveExtractor host,
            LayerFailureContext context,
            RecursionStopReason reason)
        {
            if (reason != RecursionStopReason.PasswordAttemptsExceeded)
            {
                return;
            }

            int firstUntriedIndex = Math.Min(
                context.Candidates.Count,
                context.LastConsumedCandidateIndex + 1);

            PasswordCandidateGap.Gap? untriedCandidates = PasswordCandidateGap.Describe(
                context.Candidates,
                firstUntriedIndex,
                context.TriedCandidates.Count + 1,
                (value, ordinal) => host.DescribeCandidate?.Invoke(value, ordinal)
                    ?? $"尝试密码候选第 {ordinal} 项：******");

            if (!untriedCandidates.HasValue)
            {
                return;
            }

            host.Log(
                "WARN",
                PasswordCandidateGap.BuildLogLine(
                    context.LayerLabel,
                    host.Limits.MaxPasswordAttemptsPerLayer,
                    untriedCandidates.Value));

            host.PasswordFallbackNote = host.PasswordFallbackNote
                + PasswordCandidateGap.BuildConclusionSuffix(untriedCandidates.Value);
        }
    }
}

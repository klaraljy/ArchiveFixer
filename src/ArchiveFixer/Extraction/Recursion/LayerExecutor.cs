using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 一层的候选循环：把每个候选依次"列目录 → 跳过闸门 → 预算闸门 → 探针 → 解压 → 成功校验"，
    /// 失败交给 <see cref="CandidateFailureClassifier"/>，一个候选都没成则交给
    /// <see cref="RecursiveResultBuilder"/> 出结论。
    ///
    /// <para>本类只编排顺序：判据与执行体全在既有唯一出口里（清单事实、空密码跳过、单文件上限、
    /// 探针、产物校验、失败分类、层报告），⛔ 不复制第二份。</para>
    /// </summary>
    internal static class LayerExecutor
    {

        internal static async Task<LayerOutcome> ExecuteAsync(
            RecursiveExtractor host,
            LayerExecutionState state)
        {
            string archivePath = state.ArchivePath;
            ExtractOptions options = state.Options;
            IReadOnlyList<string> candidates = state.Candidates;
            WorkItem item = state.Item;
            CancellationToken cancellationToken = state.CancellationToken;
            IProgress<ArchiveProgress>? progress = state.Progress;
            Action<ArchiveStallNotice>? stalled = state.Stalled;
            string? carvedSource = state.CarvedSource;

            PasswordAttemptLedger passwordLedger = new(host.Limits.MaxPasswordAttemptsPerLayer);
            bool triedAny = false;
            ArchiveOperationResult? lastFailure = null;
            ArchiveOperationResult? informativeFailure = null;
            ArchiveOperationResult? success = null;
            string? succeededPassword = null;

            /*
             * ===== 换引擎兜底要用的两笔账（用户 2026-10-05 拍板"补"）=====
             *
             * · <c>triedCandidates</c> —— 这一层**真正试过**的那几个候选（保序）。
             *   兜底要把"同一批候选"再试一遍，所以⛔ 不能拿 `candidates`（那张表可能更长：
             *   含被跳过的空密码、也含被每层上限截掉的尾巴）—— 两处的候选集合必须逐字相同。
             * · <c>sawEncryptedArchive</c> —— 这一层的归档**确实加密**（判据第三条）。
             *   只读结构化事实：清单里的加密位 / 引擎报的加密头。⛔ 不比中文、⛔ 不自己猜。
             */
            var triedCandidates = new List<string>();
            bool sawEncryptedArchive = false;

            /*
             * 候选循环**走到过的最后一个候选**在 <see cref="candidates"/> 里的下标
             * （-1 = 一个都还没碰）。收尾点名"哪几条没试到"要用它算起点：
             * 上限卡住时循环是在**还没碰那一条**的时候就 break 的（break 在尝试之前），
             * 所以起点 = 它 + 1；被跳过的空密码也算"碰过"（它不是"没试到"，是"按规矩跳过"）。
             */
            int lastConsumedCandidateIndex = -1;

            /*
             * 两义那一档的"第一次出现"（引擎既说密码不对、又说数据坏了）：
             * 收尾时优先拿它当结论（<see cref="ResolvePasswordStopReason"/>），
             * 于是结论与日志同时保留两种可能，⛔ 不会退化成一句"文件损坏"或"密码错误"。
             */
            ArchiveOperationResult? ambiguousPasswordFailure = null;


            /*
             * 本层的清单（L3 的预期，见 <see cref="RecursionLayerReport.Manifest"/>）：
             * `candidateManifest` 是"当前这个候选列出来的清单"，`layerManifest` 只认
             * **真正解开这一层的那一个候选**的清单（见下面赋值处）。
             */
            LayerManifest candidateManifest = LayerManifest.Unavailable("这一层还没有列过清单");
            LayerManifest layerManifest = LayerManifest.Unavailable("这一层没能列出清单（解压前那次列目录没成功）");

            /*
             * 本层"最近一次列目录成功的逐条清单"（部分完成发布的输入，见 ManifestEntries）。
             * 只记**成功**的那一次：列不出来（密码不对 / 加密头）时盘上那份清单根本不存在，
             * 记一份空的/残缺的只会让下游把"缺得太多"当成"包坏了"。
             */
            IReadOnlyList<(string Path, long Size)> lastListedEntries = Array.Empty<(string, long)>();

            /*
             * 「可疑条目提示」每层只回传一次（见下面那一段的说明）：它是"这个包里有什么"，
             * 与用哪个密码打开无关，逐候选重报只会把日志刷成噪声。
             */
            bool dangerousEntriesHintReported = false;

            /*
             * 「引擎读不动 + 双面文件 + 单条目原样存 ⇒ 直接取那一段」这条近路，**这一层只试一次**
             * （挂点在候选循环里"引擎列不出清单"那一刻；试不成 ⇒ 照旧走老路，不逐候选重试）。
             */
            bool attemptedDirectTake = false;

            /*
             * 这一层的最多候选数：**与循环用的同一个上限**（`_limits.MaxPasswordAttemptsPerLayer`）。
             * 先算出来是为了让"候选 i/N"里的 N 与真正会试的个数一致 ——
             * 写成 candidates.Count 会在被上限截断时给出一个永远到不了的 N。
             */
            int layerCandidateLimit = Math.Min(candidates.Count, host.Limits.MaxPasswordAttemptsPerLayer);

            /*
             * 「加密包不试空密码」要用到的一个事实（用户 2026-10-04 真机）：
             * 这一层**除空密码以外还有没有候选可试**（一个都不剩时不许跳 —— 与单层路径同一条边界，
             * 见 `ExtractionCoordinator` 的 `skippedOnlyEmptyPasswordBecauseEncrypted`）。
             */
            int usableCandidates = candidates.Count(candidate => !string.IsNullOrEmpty(candidate));

            /*
             * 这一层**跳过**了几个候选（"整包已加密" / "文件名已加密"两档：它们一个字节都没解）。
             *
             * <para>为什么要单独记一笔（与单层路径同一条口径）：收尾的
             * <see cref="ResolvePasswordStopReason"/> 是拿"候选总数 vs 试过几个"判断
             * "是不是被每层上限截断了"的，而被跳过的那几个**根本没试** ——
             * 不把它们从总数里减掉，就会把"候选全试完了都不对"误报成
             * 「达到密码尝试上限（候选还有剩余）」。单层路径那边是"先 RemoveAll 再重算
             * maxPasswordAttempts"（见 <c>ExtractionCoordinator</c> 候选循环里那一段），
             * 同一个意思。</para>
             */
            int skippedCandidates = 0;

            /*
             * ===== 这一层"到底有没有列出过清单"（用户 2026-10-05 拍板的那半条）=====
             *
             * `-mhe`（7z）/ `-hp`（RAR）的包**每个候选都列不出来**，引擎在列目录那一步就说"加密头"。
             * 这是「文件名已加密」唯一的证据来源（与单层路径同一个判据）：
             * ① 从头到尾**一次都没列成功**；② 引擎说的确实是加密头（结构化错误类型，⛔ 不比中文）；
             * ③ 没出现过**别的**列目录错误 —— 三条同时成立才认（证据混了 / 判不出 ⇒ 退回既有口径）。
             *
             * ⛔ 只要任何一个候选成功列出过清单，这一档就不成立：那时失败发生在**解压**阶段，
             * 结论照旧走密码错误 / 两义 / 损坏 —— 别把"密码试错了但清单列得出来"的普通加密包说成它。
             */
            bool listedAnyCandidate = false;
            bool sawEncryptedHeadersListFailure = false;
            bool sawOtherListFailure = false;

            /* 引擎在列目录那一步的原话（加密头那一档要带进结论里，与单层路径同一句文案）。 */
            string encryptedHeadersListMessage = string.Empty;

            string layerLabel = state.LayerLabel;


            foreach (string candidate in candidates)
            {
                if (!passwordLedger.CanTry)
                {
                    break;
                }

                // 这一条**被循环碰过了**（无论后面是试、还是按规矩跳过）——收尾点名"哪几条没试到"的起点靠它。
                lastConsumedCandidateIndex++;

                /*
                 * ===== 「停止后续」在候选之间生效（2026-10-05 只读审计）=====
                 *
                 * 单层路径 2026-09-27 就修掉了这一档（用户原话：「我都暂停了，你还在尝试新的密码」），
                 * 判据是"正在解的那一个不打断、下一个密码一个都不再试"；可递归这条路**拿不到那个信号**，
                 * 于是用户点了「停止后续」，这一层剩下的候选照样一个一个试完（大包就是几十分钟）。
                 *
                 * 位置刻意在 `attempts` 递增**之前**、与单层路径同一个落点（候选之间）：
                 * 已经试过至少一个候选（`passwordLedger.Tried > 0`）才看信号 —— 一个都没试就停会让收场落到
                 * `triedAny == false`（"没有可用密码"），那是对用户的误报。
                 *
                 * ⛔ 停下之后按"候选还有、只是不再试"收尾（`passwordLedger.Tried < candidates.Count` ⇒
                 * 「达到密码尝试上限」那一档），**不许报成密码错误** —— 候选根本没试完。
                 */
                if (RecursiveStopPolicy.ShouldStop(passwordLedger.Tried, host.StopRequested))
                {
                    host.Log(
                        "WARN",
                        RecursiveStopPolicy.BuildMessage(
                            layerLabel,
                            layerCandidateLimit - passwordLedger.Tried));

                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                triedAny = true;

                /*
                 * ===== 候选日志（用户 2026-09-27：「试了几个候选」必须看得见）=====
                 *
                 * **默认档就写**：单层路径那条同类日志本来就在候选循环里（每候选一行 INFO），
                 * 而递归路径过去**一条都没有** —— 真机那次 13 分钟走的正是递归路径，
                 * 日志里连"试了几个候选"都看不出来。两条路的口径必须一样。
                 *
                 * 措辞与单层路径**共用同一个格式常量**（StatusText.PasswordCandidateAttemptLogFormat），
                 * 来源说明也共用同一份描述器（DescribeCandidate ← PasswordService.BuildTryPasswordLogText）。
                 */
                /*
                 * 第一道防线（不变量 4）：解压前先列目录，把危险条目名挑出来。
                 *
                 * 递归展开的内层包过去**完全绕过** Security 层（预检与落点校验都只接在单层 GUI 路径上），
                 * 于是一个带 `..\` 的内层包会被原样解开、产物落到目标根之外，而报告里一个字都不提。
                 * 这里按"当前这个密码候选"列一次目录：能列出来就顺手做预检，
                 * 列不出来（加密头 -mhe、损坏）**不拦** —— 拦下来会让正常包也解不开，
                 * 那道兜底是解压后的落点校验。
                 */
                (string? unsafeSummary, ArchiveListResult? listedThisCandidate) =
                    await host.CheckEntriesBeforeExtractAsync(
                            archivePath,
                            candidate,
                            cancellationToken)
                        .ConfigureAwait(false);

                /*
                 * 本层的清单（L3 的预期）**只认真正解开的那一个候选**：加密头包用错密码时
                 * 引擎也可能回一份残缺/空清单，拿它当预期会把"解得好好的"判成不完整。
                 * 于是每个候选各记一份，等这次解压成功（下面的 `result.Success`）才采信。
                 */
                (candidateManifest, item.ListingSaidNoEncryption) =
                    RecursiveCandidateFacts.FromListing(listedThisCandidate);

                /*
                 * ===== 引擎读不动它 + 它确实是双面文件 ⇒ 直接按偏移取出那唯一一个原样存的条目 =====
                 *
                 * 真机现场（`ArchiveFixer-本次操作_20261005_134118.txt`）：第 1 层三个双面视频，
                 * 每个都先白试一次引擎（`Cannot open the file as archive`，写盘的那一次解压，
                 * 日志里留下三条假 ERROR），再把尾部归档整份抠成副本（78/48/83 秒），
                 * 然后让 7-Zip 从副本里**再解一遍**那唯一一个条目（88/48/92 秒）—— 同一份字节写了两遍、读了两遍。
                 *
                 * 判据三条缺一不可：① 引擎**列不出**这一份（列得出来就说明引擎本来读得动它 ⇒ 照旧走原路，
                 * ⛔ 近路绝不抢在引擎前面）；② 尾部确实有一个自洽的内嵌归档（识别阶段同一个检测器）；
                 * ③ 里面**恰好一个原样存的条目**（见 <see cref="TryTakeSingleStoredEntryAsync"/>）。
                 * 判不出来 ⇒ 一个字都不改，照旧走今天这条路。
                 */
                if (carvedSource == null
                    && !attemptedDirectTake
                    && listedThisCandidate is { Success: false })
                {
                    attemptedDirectTake = true;

                    LayerOutcome? taken = await host.TryTakeSingleStoredEntryAsync(item, progress, cancellationToken)
                        .ConfigureAwait(false);

                    if (taken != null)
                    {
                        return taken;
                    }

                    LayerOutcome? direct = await host.TryTakeEmbeddedZipDirectAsync(
                            item,
                            candidates,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (direct != null)
                    {
                        return direct;
                    }

                    if (taken != null)
                    {
                        return taken;
                    }
                }

                if (listedThisCandidate is { Success: true })
                {
                    listedAnyCandidate = true;

                    lastListedEntries = PartialPublishPlanner.ToManifestEntries(listedThisCandidate);

                    // 「这一层的归档是加密的」（换引擎兜底判据的第三条）：只读清单里的结构化加密位。
                    if (listedThisCandidate.IsEncrypted)
                    {
                        sawEncryptedArchive = true;
                    }

                    /*
                     * ===== 可疑条目提示（2026-10-05 只读审计）=====
                     *
                     * 单层路径在源包预检之后就会统计"可执行 / 脚本类条目"并写进任务字段
                     * （`ExtractionCoordinator.AnalyzeDangerousEntries` + `PublishDangerousEntriesHint`），
                     * 而递归这条路**一次都不做** —— 出厂默认档（展开所有分支）下，内层包里的
                     * `.exe / .bat / .ps1` 一个提示都没有。
                     *
                     * ⛔ 判据与文案仍只有那一处（转调同一个 static，不复制第二份）；
                     * 本类只负责"这一层的清单里有"，显示在哪由调用方决定（<see cref="DangerousEntriesReported"/>）。
                     *
                     * 每层只回传一次：同一份清单每个候选都列得出来，逐候选重报只会把日志刷成噪声
                     * （提示说的是这个包有什么，与用哪个密码打开无关）。
                     */
                    dangerousEntriesHintReported = RecursiveLayerDiagnostics.ReportDangerousEntriesOnce(
                        dangerousEntriesHintReported,
                        host.ReportDangerousEntries,
                        host.DangerousEntriesReported,
                        listedThisCandidate);
                }
                else if (string.Equals(
                    listedThisCandidate?.ErrorType,
                    EngineErrorTypes.EncryptedHeaders,
                    StringComparison.OrdinalIgnoreCase))
                {
                    // 这一次列目录失败是"加密头"：它是「文件名已加密」那一档的证据（见上面的说明）。
                    sawEncryptedHeadersListFailure = true;

                    // 加密头的包当然**是加密的**（换引擎兜底判据的第三条，与上一条同一性质）。
                    sawEncryptedArchive = true;
                    encryptedHeadersListMessage = listedThisCandidate?.Message ?? string.Empty;
                }
                else
                {
                    /*
                     * 别的列目录失败（引擎抛异常时为 null、损坏、超时…）：证据混了 ⇒ 那一档不成立，
                     * 结论退回既有的密码口径（"判不出就什么都不改"）。
                     */
                    sawOtherListFailure = true;
                }

                /*
                 * ===== 加密包不试空密码（用户 2026-10-04 真机；**唯一判据** = PasswordProbe）=====
                 *
                 * 现场：真机日志「第 0 层：第6集.zip：开始解压，密码候选 1/10，尝试空密码」——
                 * 一个**已加密**的包先拿空密码白跑一整包。单层路径 2026-09-25 就修掉了这一档
                 * （`PasswordProbe.ShouldSkipEmptyPassword`），可**递归这条路没接**：
                 * 一键处理里内层包走的正是递归，于是同一件事在两条路上长成两种行为。
                 *
                 * 判据只读引擎给的事实（清单里有没有加密条目），⛔ 不比中文、也不自己猜；
                 * 边界与单层路径**逐字相同**：还有别的候选可试才跳（一个都不剩时照旧试空密码，
                 * 否则收场会落到一句更难懂的"未知解压失败"）。
                 *
                 * 账目：这一档**没解过任何东西**，所以不占尝试次数（撤掉 `attempts` 那一格）、
                 * 也不算"试过了"（`triedAny` 跟着退回去），候选总数 N 也跟着减 1 ——
                 * 否则日志里的「候选 i/N」会给出一个永远到不了的 N。
                 */
                EmptyPasswordSkip emptyPasswordSkip = RecursivePasswordSkipPolicy.Match(
                    candidate, usableCandidates, listedThisCandidate);

                if (emptyPasswordSkip != EmptyPasswordSkip.None)
                {
                    passwordLedger.Skip("空密码按规则跳过");
                    triedAny = passwordLedger.Tried > 0;
                    layerCandidateLimit = Math.Max(1, layerCandidateLimit - 1);
                    skippedCandidates++;

                    if (emptyPasswordSkip == EmptyPasswordSkip.EncryptedEntry)
                    {
                        host.Log(
                            "INFO",
                            string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                StatusText.RecursionSkippedEmptyPasswordLogFormat,
                                layerLabel,
                                Math.Min(usableCandidates, host.Limits.MaxPasswordAttemptsPerLayer)));
                    }
                    else
                    {
                        host.Log(
                            "INFO",
                            string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                StatusText.RecursionSkippedEmptyPasswordEncryptedHeadersLogFormat,
                                layerLabel,
                                listedThisCandidate?.ErrorType ?? string.Empty));
                    }

                    continue;
                }

                /*
                 * ===== 单文件上限：本层清单里最大的那个条目（2026-10-05 只读审计）=====
                 *
                 * 不变量 8 的四条"解压前"上限（单文件 / 总大小 / 文件数 / 展开比）里，递归这条路
                 * 过去只映了三条（见 `ExtractionCoordinator.BuildRecursionLimits`）——
                 * 出厂默认档下，一个含 200 GiB 单文件的包不会被这道闸门拦下，事后报的还不是这一条。
                 *
                 * 判据与文案**都转调解压前预算那一份**（<see cref="ResourceBudget.FindLargestFileEntry"/>
                 * / <see cref="ResourceBudget.DescribeSingleFileOverLimit"/>）：同一个上限在两条路上
                 * 必须用同一把尺子、同一句话（§9.5）。
                 *
                 * ⛔ 拿不到清单 ⇒ **不拦**（与 <see cref="IsExpansionRatioExceededAsync"/> 的兜底同口径）：
                 * 加密头包 / 列目录失败时盘上那份清单根本不存在，凭"不知道"去拦会把正常包误杀。
                 */
                if (listedThisCandidate is { Success: true }
                    && RecursiveLayerBudget.FindSingleFileViolation(
                        listedThisCandidate, host.Limits.MaxSingleFileSize) is string singleFileReason)
                {
                    host.Log("ERROR", $"{layerLabel}：{singleFileReason}");

                    return LayerOutcome.Stop(
                        host.BuildLayerReport(
                            item,
                            result: null,
                            succeededPassword: null,
                            overrideMessage: singleFileReason),
                        RecursionStopReason.MaxSingleFileSizeReached);
                }

                if (unsafeSummary != null)
                {
                    return LayerOutcome.Stop(
                        host.BuildLayerReport(item, lastFailure, succeededPassword: null, unsafeSummary),
                        RecursionStopReason.UnsafeEntry);
                }

                /*
                 * 走到这里 = 这个候选**真的会被拿去解**（上面那几档"按规矩跳过"的都没命中）
                 * ⇒ 记进"这一层真正试过的候选"：换引擎兜底要拿**同一批**再试一遍，
                 * ⛔ 不能把被跳过的空密码也算进去（加密包拿空密码去问 WinRAR 必然是白跑一次）。
                 */
                passwordLedger.Try();

                triedCandidates.Add(candidate);

                // 试过几个候选是**事实**：失败/撞上限那几条路也要带上（协调器据此把"已试 N 个候选"写进结论）。
                item.TriedCandidateCount = triedCandidates.Count;

                /*
                 * ===== 先只解最小的那个条目（探针），再解整包 =====
                 *
                 * 与单层路径**同一个出口**（<see cref="PasswordProbe"/>）：清单里那个最小的文件能解开
                 * = 这个候选是对的；解不开（引擎明确说密码不对）= 这个候选不对 ——
                 * 一个字节的整包数据都没动。挑不出探针、或引擎给不出确定结论 ⇒ 照旧解整包。
                 *
                 * 为什么递归这条路也必须接（用户 2026-10-04 真机）：递归里每个候选过去都是**一次完整解压**，
                 * 大包 + 多个候选 = 几十分钟白跑（单层路径 2026-09-25 修的就是这个）。
                 *
                 * 探针失败的结论**原样当成"这个候选不对"**（`lastFailure` 收那一份引擎结果）⇒
                 * 收尾的停因、两义那一档、失败清单的口径一个字都不用改。
                 */
                ArchiveOperationResult? probeFailure = await host.ProbeCandidateAsync(
                        item,
                        archivePath,
                        options,
                        listedThisCandidate,
                        candidate,
                        progress,
                        stalled,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (probeFailure != null)
                {
                    lastFailure = probeFailure;

                    host.Log(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.PasswordCandidateRejectedLogFormat,
                            layerLabel));

                    continue;
                }

                /*
                 * ===== 每换一个候选，先把这一层的产物目录清空（用户 2026-09-25 真机铁证）=====
                 *
                 * 与 ExtractionCoordinator 候选循环里同一处修复（那边有完整现场说明）：
                 * 7z 用**错密码**时也会先在输出目录里建出 0 字节的桩文件，然后才报 Wrong password；
                 * 而本层的 options 是 `SkipExisting`（`-aos`）—— 那是为了"归档内有重复条目时保留先落地的那个"，
                 * 结果下一个候选会被这些桩文件**跳过**、直接报成功，正确的密码反而永远没机会解一次。
                 *
                 * ⚠ 清的只是**这一层在工作区里的产物目录**（我们自己造的），绝不碰源文件、也绝不碰最终输出目录；
                 * 清不掉（被占用 / 权限）时不抛：引擎那边由结果校验兜底（校验不过就不算成功）。
                 */
                /*
                 * ⚠ 2026-10-17（`Item45LogAndPasswordTests.真7z_文件名也加密且密码排在后面_空密码照样跳过`）：
                 * **候选日志挪到真正解压之前** —— 原来它排在"列目录"之前，于是"空密码"这一档会先写一行
                 * `开始解压，密码候选 1/6，尝试空密码`，紧接着又写「整包已加密 —— 跳过「空密码」这一档」
                 * ⇒ 日志宣告了一件**根本没做**的事（用户 2026-09-27 那条要的是真事）。
                 * 两个跳过支都 `continue` ⇒ 放在这里等于"只有真去解才写这一行"。
                 */
                int ordinal = passwordLedger.Tried;
                string described = host.DescribeCandidate?.Invoke(candidate, ordinal)
                    ?? $"尝试密码候选第 {ordinal} 项：******";

                host.Log(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PasswordCandidateAttemptLogFormat,
                        layerLabel,
                        ordinal,
                        layerCandidateLimit,
                        described));

                /*
                 * ===== 「先测试再解压」（`Settings.TestBeforeExtract`，用户 2026-09-27 第 37 条）=====
                 *
                 * 每个候选先让引擎**只测试**、通过了才真解压。旧单层路有这一段，迁移成
                 * "普通任务全递归"之后**整条丢了**（运行期实测
                 * `ExtractionPipelineFixTests.先测试再解压_两义档要继续试下一个候选…`：
                 * `测试调用 0 次 / 解压调用 3 次` ⇒ 用户开了那个设置根本不生效）。
                 * ⛔ 判据与处置与单层路那一段**逐字同一条**（`ExtractionCoordinator` 的 pre-test 循环）。
                 */
                if (host.TestBeforeExtract)
                {
                    ArchiveOperationResult preTest = await host.Engine
                        .TestAsync(ArchiveRequest.For(archivePath, candidate), cancellationToken)
                        .ConfigureAwait(false);

                    host.ObserveEngineCall(preTest, archivePath, "test");

                    if (!preTest.Success)
                    {
                        lastFailure = preTest;

                        if (preTest.IsWrongPassword || preTest.IsNeedPassword)
                        {
                            host.Log("INFO", $"{layerLabel}：解压前测试 —— 这个密码候选不对，继续试下一个。");
                            continue;
                        }

                        /*
                         * 两义那一档（引擎自己说"可能密码不对、也可能数据坏了"）：与上面候选循环同一条
                         * 处置 —— **继续试下一个候选**，并把"两种可能"记下来当收尾结论
                         * （⛔ 不替引擎挑一个）。
                         */
                        if (preTest.IsPasswordOrCorrupted)
                        {
                            ambiguousPasswordFailure ??= preTest;

                            host.Log(
                                "WARN",
                                string.Format(
                                    System.Globalization.CultureInfo.CurrentCulture,
                                    StatusText.CandidatePasswordOrCorruptedLogFormat,
                                    layerLabel));

                            continue;
                        }

                        // 其余（权限 / 引擎不可用 / 不支持 …）：换密码解决不了，停下并原样上报。
                        host.Log("ERROR", $"{layerLabel}：解压前测试没过：{PasswordMasker.Sanitize(preTest.Message)}");

                        return LayerOutcome.Stop(
                            host.BuildLayerReport(item, preTest, succeededPassword: null),
                            RecursionStopReason.EngineFailed);
                    }
                }

                RecursiveExtractor.TryResetLayerOutputDirectory(item.Layer.OutputPath);

                ArchiveOperationResult result = await host.Engine.ExtractAsync(
                        new ArchiveRequest
                        {
                            ArchivePath = archivePath,
                            OutputPath = item.Layer.OutputPath,
                            Password = candidate,

                            /*
                             * 进度与"长时间无响应"提示（2026-09-22 补，本批之前递归内层完全没有进度）。
                             *
                             * 只**转发**，不在这里做任何加工：节流在引擎层（ArchiveProgressReporter，
                             * 最多 250ms 一条），落地在协调器的 TaskProgressSink。
                             * 参数为 null 时引擎侧一个字都不报 —— 既有调用点（测试、CLI 式用法）行为不变。
                             */
                            Progress = progress,
                            Stalled = stalled
                        },
                        options,
                        cancellationToken)
                    .ConfigureAwait(false);

                host.ObserveEngineCall(result, archivePath, "extract");

                if (result.Success)
                {
                    /*
                     * ===== 「引擎说成功」不等于「这一层的产物就是对的」（2026-10-16）=====
                     *
                     * 与单层路**同一条尺子**：候选解完之后先校验产物，判否 ⇒ **这个候选不算数**
                     * （清掉它写下的东西、接着试下一个候选）。两条路的口径必须一样
                     * （AGENTS.md：递归层与单层路行为必须对齐）。
                     *
                     * 现场（实测取的运行期事实，⛔ 不是读代码推的）：
                     * `OutputVerificationGateTests.第一个候选产出0字节_第二个候选正确_最终成功且两个候选都试过`
                     * —— 候选 1（错密码）在产物目录里留下两个 **0 字节桩文件**，7-Zip 退出码 0；
                     * 老写法当场 `break` 接受它 ⇒ 收尾校验判否 ⇒ **整单落「解压失败」**，
                     * 而候选 2（正确密码）**一次都没被试过**（探针逐字 `Passwords=wrong-one`，
                     * 日志里只有 `密码候选 1/2`）。单层路在同一个形状上会接着试下一个候选并成功。
                     *
                     * 例外两条（与单层路那两条例外**同源**）：
                     * · **产物越界**：那是"写到了目标根之外"这个更具体的问题，比"候选不行"更值得说，
                     *   交给 <see cref="FinishSuccessfulLayerAsync"/> 的落点校验去报（这里放行）；
                     * · **候选没有了**（这一条是最后一个 / 已经到了每层尝试上限）：没有下一个可试的，
                     *   把产物留着交给收尾说清为什么失败（⛔ 不许清掉 —— 那正是单层路
                     *   "最后一个候选不清盘"那条口径）。
                     */
                    bool productsVerified = RecursiveExtractor.CandidateProductsVerified(
                        item,
                        candidateManifest,
                        out string candidateVerifyMessage,
                        out OutputVerificationResult candidateVerification);

                    /*
                     * ⚠ 2026-10-17（用户 2026-09-27 口径「包没加密 / 结果不变 → 不许再烧候选」，
                     * 与单层路**逐字同一条**：`realContentProduced && stage.List is { Success: true, IsEncrypted: false }`，
                     * 其中 `realContentProduced = stage.Verification.ActualTotalSize > 0`）：
                     * **这一候选真写出了字节**、而**清单里一个加密条目都没有** ⇒ 换密码根本不可能改变结果
                     * ⇒ 不再烧后面的候选，产物留着交给收尾如实说。
                     *
                     * <para>⛔ 前半截（真写出了字节）缺不得 —— 第一版漏了它，0 字节桩那一档就再也不试下一个候选，
                     * 被既有守门 `OutputVerificationGateTests.第一个候选产出0字节_第二个候选正确_最终成功且两个候选都试过`
                     * 当场逮住（候选 2 才是对的）。</para>
                     *
                     * <para>实测（`ExtractionPipelineFixTests.没加密的包_产物校验不过时_不再换密码也不写密码错误`，
                     * 走的正是递归路）：包没加密、清单 `IsEncrypted=false`，候选却被**烧了 4 次**
                     * （逐字 `Assert.Single() Failure: The collection contained 4 items`）——
                     * 单层路早就有这条闸门，递归路一直没有（AGENTS：两条路的口径必须一样）。</para>
                     */
                    bool listingSaysNoEncryption =
                        candidateVerification.ActualTotalSize > 0
                        && listedThisCandidate is { Success: true, IsEncrypted: false };

                    /*
                     * ===== 「连续两个候选的结果完全相同」也要在**这一支**生效（2026-10-21）=====
                     *
                     * 用户 2026-09-27 的口径是"结果不变 ⇒ 不许再烧候选"，它并不区分引擎怎么说：
                     * 错密码那一支早就有（见上面候选循环里那条 WARN），而"引擎说成功、产物却没对上清单"
                     * 这一支一直没有 ⇒ 实测 `ExtractionPipelineFixTests.连续两个候选结果完全相同_不再试第三个`
                     * 把 4 个候选全烧了一遍（`Expected: 2 / Actual: 4`）。
                     *
                     * 判据只用事实：两次校验的**文件数 + 字节数**相同、而且真的解出了字节（0 字节桩那一档
                     * 必须继续试下一个 —— 正确密码可能就在后面，既有守门
                     * `OutputVerificationGateTests.第一个候选产出0字节_第二个候选正确_…` 钉着它）。
                     */
                    bool sameVerifiedResultAsPrevious =
                        !productsVerified
                        && candidateVerification.ActualTotalSize > 0
                        && item.LastVerifiedCandidateFileCount >= 0
                        && item.LastVerifiedCandidateFileCount == candidateVerification.ActualFileCount
                        && item.LastVerifiedCandidateBytes == candidateVerification.ActualTotalSize;

                    if (!productsVerified)
                    {
                        item.LastVerifiedCandidateFileCount = candidateVerification.ActualFileCount;
                        item.LastVerifiedCandidateBytes = candidateVerification.ActualTotalSize;
                    }

                    bool keepProductsForFailureVerdict =
                        listingSaysNoEncryption
                        || sameVerifiedResultAsPrevious
                        || RecursiveExtractor.FindLandingViolation(item.Layer.OutputPath) != null
                        // ⚠ `lastConsumedCandidateIndex` 是**0 起**的下标（初值 -1，每进一次循环 +1）⇒
                        // "这一条就是最后一个" = `>= candidates.Count - 1`。
                        // 第一版写成 `>= candidates.Count` ⇒ 最后一个候选也被判成"还有下一个" ⇒
                        // 0 字节产物被清掉、整层落「密码错误」—— 被既有守门
                        // `假引擎成功但产物全是0字节_不是成功_不续解_不搬其余物_源包不动` 当场逮住
                        // （逐字 `Expected: "解压失败" / Actual: "密码错误"`）。
                        || lastConsumedCandidateIndex >= candidates.Count - 1
                        || !passwordLedger.CanTry;

                    if (sameVerifiedResultAsPrevious)
                    {
                        item.CandidatesProducedIdenticalResults = true;

                        host.Log(
                            "WARN",
                            $"{layerLabel}：连续两个候选解出来的产物完全相同（{candidateVerification.ActualFileCount} 个文件 / "
                            + $"{candidateVerification.ActualTotalSize} 字节）"
                            + " —— 换密码不会改变结果，不再试后面的候选。");
                    }

                    if (!productsVerified && !keepProductsForFailureVerdict)
                    {
                        host.Log(
                            "WARN",
                            $"{layerLabel}：结果校验 —— {candidateVerifyMessage}" +
                            "（这个候选不算数，继续尝试下一个候选密码）");

                        // 与"换引擎兜底"同一处清法（⛔ 不在这里另写一份"怎么清"）：
                        // 7z 用错密码会留下 0 字节桩文件，而本层是 `-aos`（跳过已存在）
                        // ⇒ 不清掉的话下一个候选会被自己的桩文件跳过。
                        RecursiveExtractor.TryResetLayerOutputDirectory(item.Layer.OutputPath);
                        continue;
                    }

                    /*
                     * 走到这里、产物又没通过校验，只可能是上面那几条之一。其中"清单说没加密"这一档
                     * 要**说清为什么不再试后面的候选**（否则用户看到"只试了一个候选"会以为程序偷懒）。
                     */
                    if (!productsVerified && listingSaysNoEncryption)
                    {
                        host.Log(
                            "WARN",
                            $"{layerLabel}：结果校验 —— {candidateVerifyMessage}" +
                            "（这个包没有加密：清单里没有加密条目，换密码不会改变结果，不再试后面的候选密码）");
                    }

                    item.TriedCandidateCount = triedCandidates.Count;

                    succeededPassword = candidate;
                    success = result;
                    lastFailure = null;

                    // 这一层真正解开了 ⇒ 采信"解开它的那个候选"给出的清单（L3 的预期）。
                    layerManifest = candidateManifest;

                    /*
                     * 详细日志档：成功那一次的命令行与原话也写下来 ——
                     * 排查的人要拿"成功那次调了什么"去对照"失败那次差在哪"。
                     * 默认档一个字都不写（成功 = 一行摘要，第 44 条）。
                     */
                    if (host.VerboseLog)
                    {
                        EngineOutputLog.LogVerbose(host.Log, layerLabel, result);
                    }

                    break;
                }

                lastFailure = result;

                CandidateFailureResult failure = await CandidateFailureClassifier.ClassifyFailureAsync(
                        host,
                        new CandidateFailureContext
                        {
                            Item = item,
                            ArchivePath = archivePath,
                            LayerLabel = layerLabel,
                            CarvedSource = carvedSource,
                            Result = result,
                            LastListedEntries = lastListedEntries,
                            InformativeFailure = informativeFailure,
                            AmbiguousPasswordFailure = ambiguousPasswordFailure,
                            Progress = progress,
                            Stalled = stalled
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                informativeFailure = failure.InformativeFailure;
                ambiguousPasswordFailure = failure.AmbiguousPasswordFailure;

                if (failure.Outcome != null)
                {
                    return failure.Outcome;
                }
            }

            if (succeededPassword == null)
            {
                return await RecursiveResultBuilder.BuildFailureAsync(
                        host,
                        new LayerFailureContext
                        {
                            Item = item,
                            ArchivePath = archivePath,
                            LayerLabel = layerLabel,
                            Candidates = candidates,
                            SkippedCandidates = skippedCandidates,
                            TriedCandidateCount = passwordLedger.Tried,
                            TriedAny = triedAny,
                            LastConsumedCandidateIndex = lastConsumedCandidateIndex,
                            TriedCandidates = triedCandidates,
                            SawEncryptedArchive = sawEncryptedArchive,
                            AmbiguousPasswordFailure = ambiguousPasswordFailure,
                            InformativeFailure = informativeFailure,
                            LastFailure = lastFailure,
                            CandidateManifest = candidateManifest,
                            ListedAnyCandidate = listedAnyCandidate,
                            SawEncryptedHeadersListFailure = sawEncryptedHeadersListFailure,
                            SawOtherListFailure = sawOtherListFailure,
                            EncryptedHeadersListMessage = encryptedHeadersListMessage,
                            LastListedEntries = lastListedEntries
                        },
                        state.ReportStopReason,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await host.FinishSuccessfulLayerAsync(
                    item,
                    success,
                    layerManifest,
                    succeededPassword,
                    cancellationToken)
                .ConfigureAwait(false);
        }

    }
}

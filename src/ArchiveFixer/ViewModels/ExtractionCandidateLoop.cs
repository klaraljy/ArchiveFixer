using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 主候选循环：逐个密码候选 → 预检/清单/路径闸门 → 解压 → 产物校验/上次结果比较 → 成功/失败分支。
///
/// <para>跨阶段账全部走 <see cref="ExtractionRunState"/>（进循环时读一次、循环结束写回一次）；
/// 循环内部的临时量（probe/prefix/previousVerification）仍是局部。⛔ 判据/文案/终态一字不改。</para>
/// </summary>
internal sealed class ExtractionCandidateLoop
{
    internal async Task RunAsync(
        ExtractionCoordinator host,
        ExtractionRunState state,
        ArchiveTask task,
        CancellationToken cancellationToken,
        bool oneClickRun,
        ExtractOptions extractOptions,
        ProgressHeartbeatStep.Sink progressSink,
        string lastListErrorType)
    {
        int maxPasswordAttempts = state.MaxPasswordAttempts;
        bool candidatesTruncated = state.CandidatesTruncated;
        List<PasswordItem> candidates = state.Candidates;
        int attemptLimit = state.AttemptLimit;
        string engineArchivePath = state.EngineArchivePath;
        string engineOutputPath = state.EngineOutputPath;
        string outputRedirectNote = state.OutputRedirectNote;
        bool sharedOutputRoot = state.SharedOutputRoot;
        ArchiveListResult? preflightList = state.PreflightList;
        bool preflightSaidEncryptedHeaders = state.PreflightSaidEncryptedHeaders;
        string? encryptedHeadersMessage = state.EncryptedHeadersMessage;
        ArchiveOperationResult? lastResult = state.LastResult;
        string selectedPassword = state.SelectedPassword;
        bool passwordConfirmed = state.PasswordConfirmed;
        ArchiveOperationResult? ambiguousTestResult = state.AmbiguousTestResult;
        string passwordConclusionNote = state.PasswordConclusionNote;

        bool extractSuccess = state.ExtractSuccess;
        bool hasWrongPassword = state.HasWrongPassword;

        /*
         * 两义那一档（引擎既说密码不对、又说数据坏了）这一层出现过没有：
         * 过它的时候**不停候选循环**，收尾时状态与原因取它（引擎原话里两种可能都写着），
         * 只有它不存在时才退回"最后一次引擎结果"（老口径，行为不变）。
         */
        ArchiveOperationResult? ambiguousResult = state.AmbiguousResult;

        /*
         * 「密码已经证实、坏的是数据」这一档（用户 2026-10-01 真机 `giu910`，17.7 GiB 解了 13 分钟）：
         * true = 某一趟**真解出了内容**之后才失败的 ⇒ 密码不是问题，不再往下试候选。
         * 判据见下面候选循环里那两处 `TryMeasureProducedContent`。
         */
        bool passwordProvenDataFailed = state.PasswordProvenDataFailed;
        (int Files, long Bytes) provenProducts = state.ProvenProducts;

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
        bool anyCandidateWroteBytes = state.AnyCandidateWroteBytes;

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
        bool skippedOnlyEmptyPasswordBecauseEncrypted = state.SkippedOnlyEmptyPasswordBecauseEncrypted;

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
                host.AppendLog(
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
                host.AppendLog(
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
            host.ArchiveEngine is SevenZipEngine prefixEngine)
        {
            candidates = await host.RankCandidatesByDecryptedPrefixAsync(
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
        OutputVerificationResult? lastVerification = state.LastVerification;
        int attemptedCandidates = state.AttemptedCandidates;

        /*
         * 这一层的**逐条清单**（部分完成发布的尺子）：优先取"带着密码列出来的那一份"
         * （校验里那次 list），没有就退回解压前那一次预检清单 —— 未加密的包两者本来就一样。
         * ⛔ 只留最近一次成功的：候选循环里每个候选都会重列一次，留错哪一份就对着错的尺子量。
         */
        IReadOnlyList<(string Path, long Size)>? lastLayerManifestEntries = PartialPublishPlanner.ToManifestEntries(preflightList);

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
        bool passwordIrrelevant = state.PasswordIrrelevant;
        string? passwordIrrelevantReason = state.PasswordIrrelevantReason;
        bool stoppedByStopRequest = state.StoppedByStopRequest;

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
            if (host.IsStopping && attemptedCandidates > 0)
            {
                stoppedByStopRequest = true;

                host.AppendLog(
                    "WARN",
                    $"{task.LogName}：已按「暂停所有操作」中断 —— 剩余 "
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
            task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;

            /*
             * ===== 候选先过"最小条目"这一关，通过了才去解整包（第 37 条）=====
             *
             * 探针只解清单里最小的那个文件（≤16 MiB，见 PasswordProbe）；解不开 = 这个候选不对，
             * 而**整包一个字节都没动**。挑不出探针时 probeEntryPath 为 null，这里整段跳过（退回老路）。
             */
            if (probeEntryPath != null)
            {
                PasswordCandidatesStep.PasswordProbeOutcome probe = await host.ProbePasswordAsync(
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

                    host.AppendLog(
                        "WARN",
                        $"{task.LogName}：密码预检不通过（只解了 {PasswordProbe.Describe(probeEntryPath, probeEntrySize)}，"
                        + "整包一个字节都没动）—— 这个候选不对，试下一个。");

                    continue;
                }

                if (probe == PasswordCandidatesStep.PasswordProbeOutcome.Verified)
                {
                    host.AppendLog(
                        "INFO",
                        $"{task.LogName}：密码预检通过（{PasswordProbe.Describe(probeEntryPath, probeEntrySize)} 解开了）"
                        + "—— 这个候选是对的，开始解整包。");
                }
            }

            host.AppendLog("INFO", $"{task.LogName}：开始解压，密码候选 {i + 1}/{maxPasswordAttempts}，{host.PasswordService.BuildTryPasswordLogText(candidate, i + 1)}");

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
            await host.DiscardStageProductsAsync(task, engineOutputPath, cancellationToken);

            ArchiveOperationResult extractResult = await host.ArchiveEngine.ExtractAsync(
host.ProgressHeartbeat.BuildTrackedRequest(engineArchivePath, selectedPassword, engineOutputPath, progressSink),
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
            EngineOutputLog.LogVerbose(host.AppendLogPair, task.FileName, extractResult);

            host.Vm.Logger.Event(
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
                var stage = await host.VerifyStageProductsAsync(
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
                    host.PasswordService.RecordPasswordSuccess(task.CurrentPath, selectedPassword);
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
                            ? "已按「暂停所有操作」中断，剩余候选不再尝试"
                            : listSaysNoEncryption
                                ? $"这个包没有加密（清单里没有加密条目），换密码不会改变结果，"
                                  + $"剩余 {maxPasswordAttempts - attemptedCandidates} 个候选不再尝试"
                                : sameAsPrevious
                                    ? $"与上一个候选的结果完全相同，换密码不会改变结果，"
                                      + $"剩余 {maxPasswordAttempts - attemptedCandidates} 个候选不再尝试"
                                    : null;

                    if (landingViolation == null && stopReason == null)
                    {
                        host.AppendLog(
                            "WARN",
                            $"{task.LogName}：结果校验 —— {stage.Verification.Message}" +
                            "（这个候选不算数，继续尝试下一个候选密码）");

                        await host.DiscardStageProductsAsync(task, engineOutputPath, cancellationToken);
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

                    host.AppendLog(
                        "WARN",
                        $"{task.LogName}：结果校验 —— {stage.Verification.Message}" +
                        "（" + (landingViolation != null
                            ? "产物越界，按越界收场，不再换密码"
                            : stopReason ?? "按失败收场") + "）");
                }

                // 收尾否掉结论（越界 / 超预算 / 定稿失败 / 校验未通过 / 源包没有搬成）时不许写"解压成功"。
                // knownList 传刚校验过的那份清单：定稿那一步用**同一份**再校验，两条路不许各列一次目录。
                bool conclusionStands = await host.PostProcessSuccessAsync(
                    task, engineArchivePath, selectedPassword, engineOutputPath, outputRedirectNote, sharedOutputRoot, oneClickRun, cancellationToken, stage.List);

                if (conclusionStands && task.Status == StatusText.ExtractSuccess)
                {
                    host.AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
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
                    ExtractionCoordinator.TryMeasureProducedContent(engineOutputPath, extractResult, out int wrongPwFiles, out long wrongPwBytes);

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
                host.AppendLog("INFO", $"{task.LogName}：这个密码候选不对，继续试下一个。");
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
                    ExtractionCoordinator.TryMeasureProducedContent(engineOutputPath, extractResult, out int ambiguousFiles, out long ambiguousBytes);

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

                host.AppendLog(
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
            string volumeHint = host.BuildEmbeddedVolumeMissingHint(task, extractResult.DetectedErrorType);

            if (!string.IsNullOrEmpty(volumeHint))
            {
                task.ErrorMessage = task.ErrorMessage + " " + volumeHint;
            }

            host.AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}", DiagnosticCodes.TaskFailed, "ExtractionCoordinator.ExtractTask");

            break;
        }

        state.ExtractSuccess = extractSuccess;
        state.HasWrongPassword = hasWrongPassword;
        state.AmbiguousResult = ambiguousResult;
        state.PasswordProvenDataFailed = passwordProvenDataFailed;
        state.ProvenProducts = provenProducts;
        state.AnyCandidateWroteBytes = anyCandidateWroteBytes;
        state.SkippedOnlyEmptyPasswordBecauseEncrypted = skippedOnlyEmptyPasswordBecauseEncrypted;
        state.LastVerification = lastVerification;
        state.AttemptedCandidates = attemptedCandidates;
        state.LastLayerManifestEntries = lastLayerManifestEntries;
        state.PasswordIrrelevant = passwordIrrelevant;
        state.PasswordIrrelevantReason = passwordIrrelevantReason;
        state.StoppedByStopRequest = stoppedByStopRequest;
        state.MaxPasswordAttempts = maxPasswordAttempts;
        state.CandidatesTruncated = candidatesTruncated;
        state.LastResult = lastResult;
        state.SelectedPassword = selectedPassword;
        state.PasswordConfirmed = passwordConfirmed;
        state.AmbiguousTestResult = ambiguousTestResult;
        state.PasswordConclusionNote = passwordConclusionNote;
    }
}


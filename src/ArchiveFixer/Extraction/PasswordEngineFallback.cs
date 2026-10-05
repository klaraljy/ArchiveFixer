using ArchiveFixer.Engines;
using ArchiveFixer.Engines.WinRar;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **换引擎再试一遍**的触发判据（用户 2026-10-05 拍板"补"；真机现场见 AGENTS.md §11.5 最后一条）。
    ///
    /// <para><b>真机事实</b>：同一个包（真视频 + 尾部一个 WinZip AES / 压缩法 99 的 ZIP）、同一条密码 ——
    /// WinRAR 6.11 退出码 0、两卷原样解出；内置 7-Zip 26.03 退出码 2、<c>Wrong password</c>、一个字节都没出来。
    /// 而候选循环**只用 7-Zip**，<c>WrongPassword</c> 又落在"不换引擎"那一档 ⇒ 程序把
    /// "这个引擎吃不下这个 ZIP"如实报成了「达到密码尝试上限」。用户自己用 WinRAR 十秒就能打开。</para>
    ///
    /// <para><b>本判据管什么</b>：候选循环已经**跑完**（候选全试完 / 撞上每层上限）、失败是**密码类**、
    /// 这一层的归档**确实加密**、本机**有** <c>WinRAR.exe</c> ⇒ 换 WinRAR 把**同一批候选**再试一遍。
    /// ⛔ 正常路径一个字不改：循环**内部**遇到 <c>WrongPassword</c> 照旧换下一个候选、照旧不换引擎（省时间）。</para>
    ///
    /// <para>⛔ 纯判据（本类前半部分）与执行体（<see cref="PasswordEngineFallback.TryAsync"/>）分开：
    /// 判据能被单测直接喂事实，不必真装一个 WinRAR。</para>
    /// </summary>
    public sealed class PasswordFallbackTriggerFacts
    {
        /// <summary>这一批已经解开了（有候选成功）⇒ ⛔ 不兜底。</summary>
        public bool AlreadySucceeded { get; init; }

        /// <summary>
        /// 这次失败是**密码类**：结构化错误类型是 <c>WrongPassword</c> / <c>NeedPassword</c> /
        /// <c>PasswordOrCorrupted</c>（⛔ 不比中文、⛔ 不靠"引擎说密码不对就认了"）。
        /// </summary>
        public bool PasswordClassFailure { get; init; }

        /// <summary>这一层的归档**确实加密**（引擎清单说有条目加密 / 报过加密头 / 识别阶段判过加密）。</summary>
        public bool ArchiveEncrypted { get; init; }

        /// <summary>候选循环**跑完了**：候选全试完，或撞上每层上限（⛔ 不是"被别的停因提前收手"）。</summary>
        public bool CandidatesFinished { get; init; }

        /// <summary>用户点了「停止后续」/ 取消 ⇒ 一个候选都不再试（⛔ 不许拿兜底绕过用户的停手）。</summary>
        public bool UserAskedToStop { get; init; }

        /// <summary>手上还有候选可试（一条都没有 ⇒ 兜底无事可做）。</summary>
        public bool HasCandidates { get; init; }

        /// <summary>本机有没有可用的 <c>WinRAR.exe</c>（用户自装；⛔ 我们不打包、不复制）。</summary>
        public bool EngineAvailable { get; init; }

        /// <summary>
        /// **主引擎在这些候选上已经写出过字节**（= 它进得去这一份的数据流）⇒ ⛔ 不认 WinRAR 的命中。
        ///
        /// <para><b>为什么这是命中的一票否决（2026-10-05 实测逮到的假成功）</b>：WinRAR **用错密码**解一个
        /// 普通 <c>-p</c> 加密的 7z 时，照样回**退出码 0**、并且**先按原大小把解密出来的乱码写进产物目录**
        /// （实测 23 字节：**文件名、大小、时间戳**与真产物逐字一样 —— 时间戳还是它按归档里存的值还原的），
        /// 只有 CRC 校验那一步在它自己肚子里失败。于是"退出码 0 且产物目录里多出了东西"这条判据
        /// 会把**错密码**认成命中 ⇒ 整条链把乱码当产物发布、结论从「密码错误」翻成「解压成功」
        /// （不变量 6 的红线：假的成功）。</para>
        ///
        /// <para>而"主引擎已经写出过字节"正是**这一档分不出证据**的标志：主引擎进得去这一份的数据流、
        /// 只是校验/密码不过 ⇒ 两个引擎给出的产物在**名字 / 大小 / 时间戳**上完全一致，现有任何判据
        /// （<c>OutputVerifier</c> 只比文件数与总字节数）都分辨不出"真解开了"与"写的是乱码"。
        /// ⇒ 按项目口径**判不出 ⇒ 什么都不做**：不换引擎、不改结论（调用方照旧用原来那条密码类结论），
        /// 只写一行如实说明的日志。</para>
        ///
        /// <para>反过来，"主引擎一个字节都没写出来"正是这一档要修的**真机形状**（同一个包 7-Zip 报
        /// <c>Wrong password</c>、0 个字节出来，换 WinRAR 十秒就解开）—— 那一档判据成立、照旧允许换引擎。</para>
        ///
        /// <para>判据只读**字节数**这一个事实（0 字节桩文件不算"写出过字节"：
        /// <c>ProducedContentGate</c> / <c>RecursiveExtractor.ProducedBytesInLayerOutput</c> 都按这个口径数），
        /// ⛔ 不比任何引擎文案、⛔ 不看文件名。默认 false = 与加这条之前逐字相同。</para>
        /// </summary>
        public bool MainEngineWroteBytes { get; init; }
    }

    /// <summary>
    /// 一次兜底的输入（两个挂点各建一份，执行体只有这一处）。
    /// </summary>
    public sealed class PasswordFallbackRequest
    {
        /// <summary>要交给 WinRAR 的那一份归档（与主引擎拿到的是**同一份**：可能是抠出来的副本）。</summary>
        public string ArchivePath { get; init; } = string.Empty;

        /// <summary>**同一批候选**：候选循环刚才试过的那些，保序（⛔ 不重排、⛔ 不扩表）。</summary>
        public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();

        /// <summary>产物落点 = 这一层的暂存（产物）目录（与主引擎同一个落点，产物因此天然接得上管线）。</summary>
        public string TargetDirectory { get; init; } = string.Empty;

        /// <summary>日志行首的标签（单层路 = 任务名，递归路 = 「第 N 层：包名」）。</summary>
        public string Label { get; init; } = string.Empty;

        /// <summary>刚才报密码错的那个引擎的显示名（结论里必须点名是谁报的，⛔ 不许含糊）。</summary>
        public string FailedEngineName { get; init; } = string.Empty;

        /// <summary>主引擎报的结构化错误类型（<c>WrongPassword</c> / <c>PasswordOrCorrupted</c>）。</summary>
        public string? FailedErrorType { get; init; }

        /// <summary>
        /// 每次候选开工前清空这一层的产物目录（清法由调用方给 —— 单层路是 <c>DiscardStageProductsAsync</c>、
        /// 递归路是 <c>TryResetLayerOutputDirectory</c>，⛔ 本类绝不自己再写一份"怎么清"）。
        /// </summary>
        public Func<CancellationToken, Task>? ResetTarget { get; init; }

        /// <summary>
        /// 候选来源描述器（<c>候选值, 第几项</c> → 一句脱敏说明）—— **必须**是既有那一个
        /// （<c>PasswordService.BuildTryPasswordLogText</c>），两处的措辞才会逐字一致（§9.5）。
        /// </summary>
        public Func<string, int, string>? DescribeCandidate { get; init; }

        /// <summary>日志出口（level, message）；可空 = 不写日志（测试用）。</summary>
        public Action<string, string>? Log { get; init; }

        /// <summary>主引擎（只用于溯源：兜底真跑成时告诉 <see cref="EngineRouter"/>「这一份是 WinRAR 解的」）。</summary>
        public IArchiveEngine? MainEngine { get; init; }

        /// <summary>取消令牌。</summary>
        public CancellationToken CancellationToken { get; init; }

        /// <summary>测试注入用（不给 = 走本机 <see cref="ToolLocator.Default"/>）。</summary>
        internal WinRarProcessRunner? Runner { get; init; }
    }

    /// <summary>
    /// 兜底的结果：**命中的候选 + 结论**。⛔ 里面**绝不含明文密码**（只回传值给调用方自己用，
    /// 任何写进日志 / 结论的文本都经过描述器或 <see cref="Models.StatusText"/> 常量）。
    /// </summary>
    public sealed class PasswordEngineFallbackOutcome
    {
        /// <summary>兜底**真的跑过**（找到了 WinRAR 且至少试了一个候选）。</summary>
        public bool Attempted { get; init; }

        /// <summary>兜底**跑成**了（WinRAR 退出码 0，产物已经在目标目录里）。</summary>
        public bool Succeeded { get; init; }

        /// <summary>解开的那个候选（明文，**只给调用方**；⛔ 不进日志、不进结论）。</summary>
        public string? HitPassword { get; init; }

        /// <summary>命中的是第几个候选（1 起）。</summary>
        public int HitOrdinal { get; init; }

        /// <summary>试了几个候选。</summary>
        public int AttemptedCandidates { get; init; }

        /// <summary>最后一次调用的退出码（没跑过是 0）。</summary>
        public int LastExitCode { get; init; }

        /// <summary>WinRAR 的版本（报告与日志都要带，不变量 14）。</summary>
        public string EngineVersion { get; init; } = string.Empty;

        /// <summary>
        /// 写进**结论**的那一句（<see cref="StatusText"/> 常量拼的；兜底跑过 / 没跑成两种）。
        /// 空串 = 什么都没有可说（判据不成立时）。
        /// </summary>
        public string Note { get; init; } = string.Empty;

        /// <summary>判据成立、但本机没有 WinRAR（结论里要如实说清"没换引擎再试"）。</summary>
        public bool EngineMissing { get; init; }
    }

    /// <summary>
    /// **换引擎兜底**的唯一实现（两条解压路都转调这一个）。
    /// </summary>
    public static class PasswordEngineFallback
    {
        /// <summary>本机有没有可用的 WinRAR.exe（判据的输入之一；唯一来源仍是 <see cref="ToolLocator"/>）。</summary>
        public static bool IsEngineAvailable => ToolLocator.Default.WinRarExeExists;

        /// <summary>
        /// 该不该换引擎再试一遍（**触发规则的唯一出口**）。
        ///
        /// <para>七条同时成立：① 还没成功；② 失败是密码类；③ 归档确实加密；④ 候选循环跑完了
        /// （试完 / 撞上限）；⑤ 用户没喊停；⑥ 手上还有候选**且**本机有 WinRAR；
        /// ⑦ **主引擎在这些候选上一个字节都没写出来**（它写出过字节 ⇒ 换引擎给不出可分辨的证据，
        /// 见 <see cref="PasswordFallbackTriggerFacts.MainEngineWroteBytes"/>）。</para>
        /// </summary>
        public static bool ShouldFallBack(PasswordFallbackTriggerFacts facts)
        {
            return ReachedPasswordDeadEnd(facts) && !facts.MainEngineWroteBytes && facts.EngineAvailable;
        }

        /// <summary>
        /// 判据成立、但**本机没有 WinRAR** ⇒ 不跑，可是结论里必须如实说清"没换引擎再试"
        /// （⛔ 不许静默跳过、⛔ 不许把没跑过的兜底说成跑过了）。
        /// </summary>
        public static bool ShouldReportMissingEngine(PasswordFallbackTriggerFacts facts)
        {
            return ReachedPasswordDeadEnd(facts) && !facts.MainEngineWroteBytes && !facts.EngineAvailable;
        }

        /// <summary>
        /// 判据成立、但**主引擎已经写出过字节** ⇒ 不跑（判不出 ⇒ 什么都不做），只写一行如实说明。
        ///
        /// <para>这一档必须与"没装 WinRAR"分开说：两句对应的事实完全不同（一个是本机没这个工具，
        /// 一个是两个引擎给不出可分辨的证据），用户要做的动作也不一样。</para>
        /// </summary>
        public static bool ShouldReportIndistinguishableEvidence(PasswordFallbackTriggerFacts facts)
        {
            return ReachedPasswordDeadEnd(facts) && facts.MainEngineWroteBytes;
        }

        /// <summary>
        /// 三个出口共用的那半个判据（"走到密码死胡同了"，**不含**引擎是否可用 / 证据是否可分辨）——
        /// 单点，⛔ 不许在别处再判一遍。
        /// </summary>
        private static bool ReachedPasswordDeadEnd(PasswordFallbackTriggerFacts facts)
        {
            return facts != null
                && !facts.AlreadySucceeded
                && facts.PasswordClassFailure
                && facts.ArchiveEncrypted
                && facts.CandidatesFinished
                && !facts.UserAskedToStop
                && facts.HasCandidates;
        }

        /// <summary>
        /// 判据成立但机器上没有 WinRAR 时，写一行日志 + 给一句结论（两处挂点共用）。
        /// ⛔ 什么都不做：一个进程都不起、一个字节都不动。
        /// </summary>
        public static PasswordEngineFallbackOutcome ReportMissingEngine(string label, string failedEngineName, Action<string, string>? log)
        {
            string note = string.Format(
                CultureInfo.CurrentCulture,
                StatusText.PasswordEngineFallbackNotRetriedSuffixFormat,
                failedEngineName);

            log?.Invoke(
                "WARN",
                string.Format(
                    CultureInfo.CurrentCulture,
                    StatusText.PasswordEngineFallbackNoEngineLogFormat,
                    label,
                    failedEngineName));

            return new PasswordEngineFallbackOutcome
            {
                Attempted = false,
                Succeeded = false,
                EngineMissing = true,
                EngineVersion = "unknown",
                Note = note
            };
        }

        /// <summary>
        /// 判据成立、但**主引擎已经写出过字节**时，写一行如实说明（两处挂点共用）。
        ///
        /// <para>⛔ 什么都不做：一个进程都不起、一个字节都不动、**结论一个字都不改**
        /// （这一档的证据分不出来，见 <see cref="PasswordFallbackTriggerFacts.MainEngineWroteBytes"/>）；
        /// ⛔ 也不静默 —— 用户看日志要能知道"为什么没换引擎再试"。</para>
        /// </summary>
        public static void ReportIndistinguishableEvidence(
            string label,
            string failedEngineName,
            Action<string, string>? log)
        {
            log?.Invoke(
                "WARN",
                string.Format(
                    CultureInfo.CurrentCulture,
                    StatusText.PasswordEngineFallbackIndistinguishableLogFormat,
                    label,
                    failedEngineName));
        }

        /// <summary>
        /// 用 WinRAR 把**同一批候选**再试一遍（保序）。
        ///
        /// <para>每一步：换候选前先清这一层的产物目录（清法由调用方给）→ 起 WinRAR（
        /// <c>x -cfg- -ibck -o+ -y -p&lt;密码&gt; 归档 目标目录\</c>）→ 命中的判据是
        /// **退出码 0 且产物目录里真的多出了东西**（⛔ 不许只看退出码：实测 <c>-mhe</c> 的包用错密码时
        /// WinRAR 也回 0、却一个文件都没解出来）；密码类退出码（实测 10 / 文档 11 / 数据错 3）
        /// 继续试下一个，其余退出码如实记一笔但**照样把候选试完**（实测语义与文档不一致，漏试对的候选
        /// 会让整条兜底白做）。</para>
        ///
        /// <para>⛔ 一次都不改结论：跑完仍没命中 ⇒ 调用方照旧用原来那条密码类结论
        /// （⛔ 不许改成"密码对"、也⛔ 不许改成"文件损坏"）。</para>
        ///
        /// <para>⚠ 这个"命中"只说明**产物目录里多出了东西**，不说明那些东西是真的 —— 所以调用方
        /// 拿到命中之后**必须**照常走那一条路的产物校验（单层路 <c>VerifyStageProductsAsync</c>、
        /// 递归路 <c>OutputVerifier</c>），校验不过就**不算数**（不发布、不翻结论）。</para>
        /// </summary>
        public static async Task<PasswordEngineFallbackOutcome> TryAsync(PasswordFallbackRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            WinRarProcessRunner runner = request.Runner ?? new WinRarProcessRunner();

            if (!runner.IsAvailable)
            {
                return ReportMissingEngine(request.Label, request.FailedEngineName, request.Log);
            }

            var log = request.Log;
            var cancellationToken = request.CancellationToken;

            string version = runner.Version;
            string failedEngineName = request.FailedEngineName;

            log?.Invoke(
                "INFO",
                string.Format(
                    CultureInfo.CurrentCulture,
                    StatusText.PasswordEngineFallbackStartLogFormat,
                    request.Label,
                    failedEngineName,
                    request.FailedErrorType ?? StatusText.WrongPassword,
                    request.Candidates.Count,
                    version));

            int attempted = 0;
            int lastExitCode = 0;

            foreach (string candidate in request.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                attempted++;

                if (request.ResetTarget != null)
                {
                    /*
                     * 与主引擎的候选循环同一个道理（用户 2026-09-25 铁证）：每换一个候选都从**空目录**开始，
                     * 否则上一个候选留下的桩文件 / 半截产物会混进这一次的结果里。
                     * ⛔ 清法不在这里：调用方给的就是它自己那一份实现。
                     */
                    await request.ResetTarget(cancellationToken).ConfigureAwait(false);
                }

                string described = request.DescribeCandidate?.Invoke(candidate, attempted)
                    ?? $"尝试密码候选第 {attempted} 项：******";

                log?.Invoke(
                    "INFO",
                    string.Format(
                        CultureInfo.CurrentCulture,
                        StatusText.PasswordEngineFallbackCandidateLogFormat,
                        request.Label,
                        attempted,
                        request.Candidates.Count,
                        described));

                bool existedBefore = File.Exists(request.ArchivePath);

                /*
                 * ===== 命中判据的**前半句**：这一趟开工前产物目录里有多少东西 =====
                 *
                 * 退出码 0 **不足以**说明"解出来了"（实测见下面那段），所以每一次都要量一次盘上事实：
                 * 每换一个候选之前调用方都会把这个目录清空（`ResetTarget`），所以正常情况下这里是 (0,0)。
                 */
                (int FilesBefore, long BytesBefore) productsBefore = OutputVerifier.Measure(request.TargetDirectory);

                ArchiveOperationResult result = await runner
                    .ExtractAsync(request.ArchivePath, request.TargetDirectory, candidate, cancellationToken)
                    .ConfigureAwait(false);

                lastExitCode = result.ExitCode;

                /*
                 * ===== 安全网：交给 WinRAR 的那一份归档必须原地不动（不变量 1）=====
                 *
                 * 主防线是参数里的 `-cfg-`（实测：本机 WinRAR 设了「解压后删除压缩包 = 总是」时，
                 * 不加它 WinRAR 真会把源包删掉）。这里只做**事后如实报告** —— 已经删掉的东西找不回来，
                 * 但绝不许静默：源包消失是头等事故，日志里必须有一行 ERROR。
                 */
                if (existedBefore && !File.Exists(request.ArchivePath))
                {
                    log?.Invoke(
                        "ERROR",
                        string.Format(
                            CultureInfo.CurrentCulture,
                            StatusText.PasswordEngineFallbackSourceVanishedLogFormat,
                            request.Label));
                }

                if (result.Success)
                {
                    /*
                     * ===== 命中判据的**后半句**：这一趟真的往产物目录里写了东西吗 =====
                     *
                     * ⛔ **不许只看退出码**（用户 2026-10-05 原话，也是实测踩到的）：
                     * `-mhe`（连文件名一起加密）的 7z 用**错密码**时，WinRAR 照样回**退出码 0**、
                     * 却一个文件都没解出来 —— 只看退出码就会把它当成"这个密码是对的"，
                     * 一路走到**假的成功**（不变量 6 的红线：部分成功不得显示为成功）。
                     *
                     * 判据只读盘上事实（`OutputVerifier.Measure` 是既有那一个出口）：
                     * 文件数与字节数都**没有增长** ⇒ 这一次什么都没解出来 ⇒ 不算命中，试下一个。
                     *
                     * ⚠ 这一条**只筛掉"什么都没写"**，筛不掉"写的是乱码"（实测：普通 `-p` 的 7z 用错密码时
                     * WinRAR 会按原大小把乱码写出来，名字/大小/时间戳与真产物一样）—— 那一档由触发判据里
                     * 的 <see cref="PasswordFallbackTriggerFacts.MainEngineWroteBytes"/> 挡住，两处缺一不可。
                     */
                    (int FilesAfter, long BytesAfter) productsAfter = OutputVerifier.Measure(request.TargetDirectory);

                    bool producedSomething =
                        productsAfter.BytesAfter > 0 &&
                        (productsAfter.FilesAfter > productsBefore.FilesBefore ||
                         productsAfter.BytesAfter > productsBefore.BytesBefore);

                    if (!producedSomething)
                    {
                        log?.Invoke(
                            "WARN",
                            string.Format(
                                CultureInfo.CurrentCulture,
                                StatusText.PasswordEngineFallbackNoProductsLogFormat,
                                request.Label,
                                attempted,
                                request.Candidates.Count));

                        continue;
                    }

                    log?.Invoke(
                        "INFO",
                        string.Format(
                            CultureInfo.CurrentCulture,
                            StatusText.PasswordEngineFallbackHitLogFormat,
                            request.Label,
                            attempted,
                            request.Candidates.Count,
                            described,
                            version));

                    /*
                     * 溯源（不变量 14）：这一份是 WinRAR 解开的，报告里那一行"引擎：…"必须写它，
                     * ⛔ 不能记在 7-Zip 头上。与"内置 ZIP 直读"那条同一种做法（EngineRouter 收口）。
                     */
                    if (request.MainEngine is EngineRouter router)
                    {
                        router.RememberWinRarFallback(request.ArchivePath, version);
                    }

                    return new PasswordEngineFallbackOutcome
                    {
                        Attempted = true,
                        Succeeded = true,
                        HitPassword = candidate,
                        HitOrdinal = attempted,
                        AttemptedCandidates = attempted,
                        LastExitCode = result.ExitCode,
                        EngineVersion = version
                    };
                }

                if (result.IsCancelled || result.IsTimedOut)
                {
                    // 取消 / 超时：这一档必须收手（⛔ 不许再起新进程），结论照旧。
                    log?.Invoke(
                        "WARN",
                        string.Format(
                            CultureInfo.CurrentCulture,
                            StatusText.PasswordEngineFallbackCancelledLogFormat,
                            request.Label,
                            attempted,
                            request.Candidates.Count));

                    break;
                }

                if (IsRejectedCandidateExitCode(result.ExitCode))
                {
                    log?.Invoke(
                        "INFO",
                        string.Format(
                            CultureInfo.CurrentCulture,
                            StatusText.PasswordEngineFallbackCandidateRejectedLogFormat,
                            request.Label,
                            attempted,
                            request.Candidates.Count,
                            result.ExitCode));

                    continue;
                }

                /*
                 * 别的退出码：**如实记一笔，但照样把剩下的候选试完**。
                 *
                 * 为什么不停手（实测，2026-10-05）：WinRAR 的退出码语义与文档并不一致 ——
                 * 本机 6.11 上「密码不对」是 10（文档写 11），而**空密码与"打不开"都是 1**。
                 * 若把 1 当成"与密码无关 ⇒ 收手"，真机那条形状（候选表第一个就是空密码）会当场停在
                 * 第一个候选上，那个**对的**密码永远轮不到 —— 漏试的代价远大于多跑几个候选
                 * （实测每次约 300 ms，而每层上限本来就只有十几个）。
                 */
                log?.Invoke(
                    "WARN",
                    string.Format(
                        CultureInfo.CurrentCulture,
                        StatusText.PasswordEngineFallbackUnexpectedExitCodeLogFormat,
                        request.Label,
                        attempted,
                        request.Candidates.Count,
                        result.ExitCode));
            }

            log?.Invoke(
                "WARN",
                string.Format(
                    CultureInfo.CurrentCulture,
                    StatusText.PasswordEngineFallbackFailedLogFormat,
                    request.Label,
                    failedEngineName,
                    attempted,
                    lastExitCode));

            return new PasswordEngineFallbackOutcome
            {
                Attempted = true,
                Succeeded = false,
                AttemptedCandidates = attempted,
                LastExitCode = lastExitCode,
                EngineVersion = version,
                EngineMissing = false,

                /*
                 * 结论那一句：⛔ 不写成"密码不对"（真因可能是引擎吃不下），也⛔ 不改结论，
                 * 只是把"已经换引擎再试过一遍"这件事如实说清。硬失败与试完所有候选**同一句话**：
                 * 对用户来说要做的动作一样（核对密码 / 换工具），多一个"为什么提前收手"的分支
                 * 只会让结论更长而信息更少 —— 那一刻的退出码已经写进日志那一行了。
                 */
                Note = string.Format(
                    CultureInfo.CurrentCulture,
                    StatusText.PasswordEngineFallbackRetriedSuffixFormat,
                    failedEngineName)
            };
        }

        /// <summary>
        /// 这个退出码是"**这个候选没成**"（换下一个候选还有意义），还是"引擎说的别的事"。
        ///
        /// <para>实测（本机 WinRAR 6.11 + WinZip AES 的 ZIP）：正确密码 <c>0</c>、
        /// **密码不对 <c>10</c>**（文档写的是 11）、不是归档 <c>1</c>。<c>3</c> = CRC / 数据错
        /// （文档语义），一并按"这个候选没成"处置 —— 反正是否命中只由 <c>0</c> 决定，
        /// 多试一个候选最坏只是多花一点时间，而**漏试**对的候选会让整条兜底白做。</para>
        /// </summary>
        internal static bool IsRejectedCandidateExitCode(int exitCode)
        {
            return exitCode is 3 or 10 or 11;
        }
    }
}

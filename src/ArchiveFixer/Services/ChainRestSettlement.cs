using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RestHandlingTrigger = ArchiveFixer.Services.RestHandlingStep.RestHandlingTrigger;

namespace ArchiveFixer.Services;

/// <summary>
/// 链尾**结算**：这一单（以及整条链）配不配按档动源包/其余物，以及链结束后的源包补做
/// （生命周期：链尾；失败模型：判据任一条不成立 ⇒ 一个字节都不动，并写清是哪一条）。
///
/// <para>判据全部读**事实**（机器终态 / L4 可证完整 / 分卷完整性 / 内容物是否存在 / 取消令牌），
/// ⛔ 不读状态字符串、不按样本名加分支。两个入口：<see cref="ApplyAfterChainAsync"/>
/// （链尾按档处理；一键档在这里才动手，任务收尾那一刻内层包还是下一层的粮草）与
/// <see cref="RunDeferredSourceMoveWorkAsync"/>（链结束后对源包的补做；只「彻底删除」档有动作，
/// 且走与别处同一条闸门链）。</para>
///
/// <para><see cref="CountContentFiles"/> 是这一档的"内容物确实已定稿"事实：排除其余物、归档/分卷过程物
/// 与工作区自己那棵树；读不了就按 0 处理（宁可判"没有内容物"，也不放行不可逆动作）。</para>
/// </summary>
internal sealed class ChainRestSettlement
{
    private readonly IRestHandlingStepHost _host;
    private readonly RestHandlingBatchPolicy _policy;
    private readonly RestHandlingLog _log;
    private readonly RestPurgeStep _purge;

    internal ChainRestSettlement(
        IRestHandlingStepHost host,
        RestHandlingBatchPolicy policy,
        RestHandlingLog log,
        RestPurgeStep purge)
    {
        _host = host;
        _policy = policy;
        _log = log;
        _purge = purge;
    }

    /// <summary>
    /// 链尾对**单个**成功任务补做「删除操作」那一档（一键处理专用；幂等：已处理过的直接跳过）。
    /// </summary>
    /// <param name="chainTasks">
    /// 整条链的全部任务（根 + 续解子任务）。**调用方照旧按位置传**（同一批的链信息在别处还要用），
    /// 但本档 2026-10-04 起**不再读它** —— 判据只问"这一单自己的根源包那一层"，
    /// 不再要求链上每个内层包都成功（用户原话：「你不会读设置吗，我勾选了保留吗，没勾选你留着干什么」，
    /// 见下面那一段长注释）。
    /// </param>
    /// <param name="cancellationToken">取消令牌（取消那一档由执行体 <c>RestItemPurger</c> 第 3 道门槛判）。</param>
    internal async Task ApplyAfterChainAsync(
        ArchiveTask task,
        IReadOnlyList<ArchiveTask>? chainTasks,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        /*
         * RestHandling 只服务失败 / 部分完成路径。
         * 单一物（单包或整组）最终成功时，过程物由成功清理出口处理；
         * 这里不再按 Keep/Delete 碰其余物。
         */
        bool chainSucceeded = task.Outcome == TaskOutcome.Succeeded
                              && (chainTasks == null
                                  || chainTasks.All(item => item.Outcome is TaskOutcome.Succeeded
                                      or TaskOutcome.Skipped));

        if (chainSucceeded)
        {
            return;
        }

        string mode = RestHandlingModes.Normalize(_policy.Mode);

        if (string.Equals(mode, RestHandlingModes.Keep, StringComparison.OrdinalIgnoreCase))
        {
            /*
             * ⚠ 2026-10-13（困难批：6 份样本 ①✔ 而只差判据 ③，日志 + 运行期定案）：
             * 「其余物 = 不动」这一档过去在这里**整档早退** ⇒ 「程序自己产出、已被整组接走」的那份副本
             * （接片建的链接 / 产出方落点里那一份）**永远没人认领** ⇒ 判据 ③「盘上无残留」红。
             *
             * <para><b>用户口径（2026-10-13 拍板选 A + 当天再次点名）</b>：「不动其余物」的"不动"
             * = **不删用户的东西**；而**程序自己产出、且已被整组接走**的那份多余副本**允许挪位置**
             * （搬进同层的 `其余物\`，⛔ 一个字节都不删）；原话还有一句
             * 「连"程序自己产的、已经没用的那份副本" 这个不就是**过程物**吗」。</para>
             *
             * <para>⇒ 这一档只补调**"产出方那一份片"那一个出口**（它自己按档分派：Keep ⇒ 搬进同层其余物、
             * 一个字节都不删，见 <see cref="_host.PurgeGroupLandedPieces"/>）。⛔ **用户源片那一半照旧一步都不动**
             * —— 下面那两段（`SettleOneConsumedPieceTask` 的搬运与"借来用过的用户源片"账）**一个都不调**，
             * 所以"不动其余物"对用户自己的东西仍然是一个字节都不碰。</para>
             */

            return;
        }

        /*
         * ===== 判据逐条查、**每一条拦下都要说清是哪一条**（用户 2026-09-30 真机报的 bug）=====
         *
         * 现场：用户设了"源包放入其余物 + 其余物彻底删除"，链跑完（日志"没有发现可继续解压的内层包"），
         * 然后**什么都没有**：源包原地不动、其余物没生成、日志里一个字都没说为什么 ——
         * 老写法在下面那一支直接 `return`（一句注释"什么都不做"），用户只能猜。
         *
         * 现在：判据一条一条查出来，拦下时写一行 WARN，点名**是哪一条不成立**；
         * 其中 L4 那一档由唯一出口 <see cref="ResultCompletenessClassifier"/> 给出
         * 「哪一层 + 什么原因」（`verdict.Blocker`）。
         */
        string chainRestDirectory = RestDirectoryResolver.ResolveChainRestDirectory(task, chainTasks);

        if (task.Outcome != TaskOutcome.Succeeded)
        {
            _log.AppendRemovalBlocked(
                task,
                $"{StatusText.ChainRestBlockedTaskOutcomeFormat}（机器终态：{task.Outcome}）");

            return;
        }

        /*
         * ⚠ 2026-09-30（检验等级 L4）：第二条判据从"校验通过"**收紧**成"可证完整"——
         * 唯一出口 <see cref="ResultCompletenessClassifier"/>。老口径把"拿不到清单、
         * 只做了非空底线校验"也算成通过，这一档**没有任何证据**却能走到链尾删其余物。
         * （下游 <see cref="RestItemPurger"/> 还会再挡一次，但**同一件事只许有一个判据**：
         * 这里留一个更弱的版本，等于给下一个人两条互相矛盾的口径。）
         */
        ResultCompletenessVerdict completeness = ResultCompletenessClassifier.Classify(task);

        if (!completeness.AllowsSourceRemoval)
        {
            // 红线：判不出 / 可证不完整 ⇒ 什么都不做，但**必须写清是哪一条判据拦下的**。
            _log.AppendRemovalBlocked(task, completeness.Blocker);

            return;
        }

        if (string.IsNullOrWhiteSpace(chainRestDirectory))
        {
            _log.AppendRemovalBlocked(task, StatusText.ChainRestBlockedNoRestDirectoryFormat);

            return;
        }

        if (!Directory.Exists(chainRestDirectory))
        {
            _log.AppendRemovalBlocked(
                task,
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.ChainRestBlockedRestMissingFormat,
                    chainRestDirectory));

            return;
        }

        /*
         * ===== 借来用过的用户源片**也算源包**：跟这一组一起进其余物、一起按删除档处理 =====
         * （用户 2026-10-05 拍板原话：「那肯定啊，这个算原包，最后放到其余物里面一同删除」）
         *
         * 现场：一组跨盘 ZIP 的末片压在包里、其余三片散在三个源目录里 —— 递归层把三片硬链接过来
         * 凑齐、整组解开、结果发布成功。那三片就是**这一组的源包**，不能因为"它们自己那一单是跟班卷"
         * 就永远留在用户目录里。
         *
         * 位置刻意排在上面那几道判据**之后**（消费方成功 + 可证完整 + 其余物目录在）：判据一条都不放宽；
         * 搬运本体仍是既有出口 `ExecuteSourcePackageMove`（整组一起移、绝不覆盖、跨盘先复制再删原件、
         * 单个失败不影响其余），⛔ 不另写一套搬运动作。
         */
        _host.CollectConsumedSourcePiecesIntoRest(task);

        /*
         * ===== 「整条链都得成功」那道门槛 2026-10-04 **被用户推翻**（原第 37 条）=====
         *
         * 老口径（2026-09-25 第 37 条那次 12.22 GiB 事故之后加的）：链上**每一个**续解任务都成功
         * 且可证完整，链尾才按「删除操作」处理其余物 —— 理由是"链没跑完时过程物是唯一的产物线索"。
         *
         * 用户 2026-10-04 真机（日志 271/272）否掉了它，原话：
         * 「你不会读设置吗，我勾选了保留吗，没勾选你留着干什么」——
         * 他选了「其余物 = 彻底删除」，只因为**内层包**失败（根源包自己那一层是成功的、
         * 输出校验也过了）就整份留着，在他眼里就是"设置没生效"。
         *
         * 现在的口径：**判据只问"这一单自己的根源包那一层"** ——
         * ① 根源包成功（<see cref="TaskOutcome.Succeeded"/>，上面已查）；
         * ② 输出校验通过且**完整性可证**（L4 唯一出口，上面已查）；
         * ③ 未取消（<c>RestItemPurger</c> 第 3 道门槛，执行体里查）；
         * ④ 其余物目录是定稿那一刻记下来的那一个（上面已查 + 执行体第 4/5 道门槛）。
         * ⛔ **不再要求链上每个内层包都成功**。
         *
         * 红线一个字没动（全部由下面这些闸门继续钉着，与哪一档无关）：
         * · 失败 / 部分完成 / 取消的**那一单** ⇒ 源包原地不动、其余物不生成（上面两条早退 + 执行体）；
         * · 其余物删除只有 <c>RestItemPurger</c> 一个执行体，六道门槛（含「半套分卷」
         *   <c>RestVolumeCompletenessGate</c>）照旧全过；
         * · 「空间不足」模式与其余物处理的口径不变；
         * · 判据只读事实（枚举终态 / 校验结果 / 取消令牌），⛔ 不比中文。
         *
         * 确实不满足上面四条而没处理其余物时，仍然**逐链点名**说清原因 + 路径
         * （<see cref="RestHandlingLog.AppendRemovalBlocked"/> / <see cref="RestHandlingLog.RecordRestKept"/>，三处消费）。
         */
        ScheduledTaskRuntime runtime = _host.GetOrCreateRuntime(task);

        if (runtime.PurgedBytes > 0 || runtime.RestPurged)
        {
            return;
        }

        await _purge.RunAsync(
            task,
            runtime,
            oneClickRun: false,
            trigger: RestHandlingTrigger.ChainEnd);
    }

    /// <summary>
    /// 链结束后补搬的后台本体：判据 → 执行 → 结论。**只允许在后台线程上跑**。
    /// </summary>
    internal async Task<DeferredSourceMoveWork> RunDeferredSourceMoveWorkAsync(
        ArchiveTask rootTask,
        IReadOnlyList<ArchiveTask>? chainTasks,
        SourceHandlingMode sourceHandling,
        CancellationToken cancellationToken)
    {
        var logEntries = new List<(string Level, string Message)>();

        // 幂等（硬要求）：已经搬成的一律跳过 —— 不靠"文件还在不在"猜。
        if (rootTask.SourcePackageMove == SourcePackageMoveState.Done)
        {
            return new DeferredSourceMoveWork(logEntries);
        }

        if (sourceHandling == SourceHandlingMode.KeepInPlace)
        {
            logEntries.Add((
                "INFO",
                $"{rootTask.LogName}：源包处理档是「留在原地」，链结束后的补做按该档不动源包。"));
            return new DeferredSourceMoveWork(logEntries);
        }

        if (rootTask.IsContinuationTask)
        {
            // 只处理最外层源包：内层包的"源文件"是其余物里的过程物（与本轮直接搬同一口径）。
            return new DeferredSourceMoveWork(logEntries);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            logEntries.Add(("WARN", $"{rootTask.LogName}：链结束后的补搬被取消，源包留在原地（其余物不生成）。"));
            return new DeferredSourceMoveWork(logEntries);
        }

        /*
         * ===== 判据一律读**事实**，⛔ 不读状态字符串（用户 2026-09-24 明确要求）=====
         *
         * 老写法是 `OneClickCoordinator.IsSuccessStatus(rootTask)`，也就是拿
         * `task.Status == "解压成功"` 去决定"要不要动用户唯一无法再生的那份源包"。
         * 那个字符串**证明不了**产物是好的 —— 真机日志里恰好出现过"状态写着解压成功、校验却已判否"，
         * 万一那条路走到这儿，用户的源包就会被删掉。
         *
         * 现在读的是**校验那一刻的事实**，而且用的是 L4 的**唯一出口**
         * <see cref="ResultCompletenessClassifier"/>（= 可证完整）：它要求"产物非空 + 与引擎清单逐条对得上"
         * （`ManifestCrossChecked = true`）—— **拿不到清单那一档算"判不出"，同样不许动源包**
         * （2026-09-30 检验等级），加上"机器终态不是部分完成/失败/取消"（<see cref="TaskOutcome.Succeeded"/>）。
         * 这些字段都由管线在结论成立的那一刻写、之后没有任何地方会为了显示去改它们。
         */
        /*
         * ⚠ 2026-09-30（检验等级 L4）：从"校验通过"**收紧**成"可证完整"
         * （唯一出口 <see cref="ResultCompletenessClassifier"/>，与链尾其余物那两道门同一口径）。
         *
         * 漏掉这一处会怎样：链尾补搬是**移动用户唯一无法再生的源包**。老口径下"没拿到清单、
         * 只做了非空底线校验"也算校验通过 —— 链尾照样把源包搬进其余物，而接下来的
         * "删除操作"档一起来就把它删了。判不出就是判不出：一个字节都不许动。
         *
         * ⚠ 与下面 `DescribeChainVerificationGap` 那一处**判据相同、刻意查两遍**：根任务总会
         * 落进那个目录，所以严格说这一处是冗余的早退；留着的理由是它给出的是"根任务自己判不出"
         * 这句更准的话，而且它排在分卷 / 内容物那几道门之前（红检实测：把下面那一处退回旧判据，
         * 用例 `形状B_链上有一层判不出完整性_链尾不补搬源包` 变红；只退这一处则被下面那一处接住）。
         * ⛔ 别为了"去重"删掉任何一处 —— 它们挡的是同一个不可逆动作，而两处的调用场景并不完全相同。
         */
        ResultCompletenessVerdict rootCompleteness = ResultCompletenessClassifier.Classify(rootTask);

        if (!rootCompleteness.AllowsSourceRemoval)
        {
            logEntries.Add((
                "WARN",
                $"{rootTask.LogName}：{rootCompleteness.Blocker}" +
                $"（机器结论：{rootCompleteness.Evidence}），链结束后不动源包（源包留在原地）。"));
            return new DeferredSourceMoveWork(logEntries);
        }

        if (rootTask.Outcome != TaskOutcome.Succeeded)
        {
            logEntries.Add((
                "WARN",
                $"{rootTask.LogName}：根任务的机器终态不是「完成」（当前：{rootTask.Outcome}），" +
                $"链结束后不动源包（源包留在原地）。"));
            return new DeferredSourceMoveWork(logEntries);
        }

        if (rootTask.IsVolumeGroup && !rootTask.IsVolumeComplete)
        {
            logEntries.Add((
                "WARN",
                $"{rootTask.LogName}：分卷组不完整" +
                (string.IsNullOrWhiteSpace(rootTask.VolumeInfoText) ? string.Empty : $"（{rootTask.VolumeInfoText}）") +
                "，链结束后不动源包（源包留在原地）。"));
            return new DeferredSourceMoveWork(logEntries);
        }

        string destinationDirectory = _host.ResolveContinuationOutputDirectory(rootTask);

        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            logEntries.Add(("WARN", $"{rootTask.LogName}：算不出根任务的最终输出目录，链结束后不补搬源包。"));
            return new DeferredSourceMoveWork(logEntries);
        }

        string? verificationGap = _host.DescribeChainVerificationGap(destinationDirectory, rootTask, chainTasks);

        if (verificationGap != null)
        {
            logEntries.Add((
                "WARN",
                $"{rootTask.LogName}：链里的输出校验没有全部通过（{verificationGap}），" +
                $"链结束后不补搬源包（源包留在原地）。"));
            return new DeferredSourceMoveWork(logEntries);
        }

        int contentFiles = CountContentFiles(destinationDirectory);

        if (contentFiles == 0)
        {
            // 内容物压根没出现（例如第二层解压失败 / 密码不对）：源包留在原地，其余物不为它生成。
            logEntries.Add((
                "WARN",
                $"{rootTask.LogName}：{destinationDirectory} 里没有内容物（只有过程物），" +
                $"链结束后不补搬源包（源包留在原地）。"));
            return new DeferredSourceMoveWork(logEntries);
        }

        /*
         * ===== 按档位收尾：删 or 搬（**这一支以前只实现了"搬"**）=====
         *
         * 用户 2026-09-24 的第 19.4 条：设置=「校验通过后删除」时，选中**文件夹**里那些包一个都没被删，
         * 而单个文件会被删。诊断结论就落在这里 ——
         * 文件夹里的包绝大多数是"第一层只出内层包"的形状（假 MP4 + 尾部 ZIP + 内层加密分卷），
         * 于是它们全被记账成"留到链结束后补做"；而链结束后的这一段老代码写着
         * `if (sourceHandling != MoveToRest) { 写一句"按该档不搬源包"; return; }` ——
         * **DeleteAfterVerify 走到这里等于什么都没做**，源包自然而然留在了原地。
         *
         * 现在两种档位共用上面那同一套判据（内容物已定稿 + 全链校验通过 + 那个目录里确实有内容物 +
         * 未取消 + 不是内层包），只是最后的动作不同：一个搬进其余物、一个按 §9.5 删掉。
         * 失败 / 取消 / 校验未通过 → 上面每一条都已经 return，一个字节都不会动（红线不变）。
         */
        string artifactRoot = rootTask.RestDirectoryPath;

        if (string.IsNullOrWhiteSpace(artifactRoot))
        {
            // 定稿计划没留下其余物目录：宁可不搬，也不要把用户的源包扔到一个猜出来的位置。
            logEntries.Add((
                "WARN",
                $"{rootTask.LogName}：这一轮的其余物目录没有被记下来，为避免把源包搬到别处，" +
                $"链结束后不补搬（源包留在原地）。"));
            return new DeferredSourceMoveWork(logEntries);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            logEntries.Add(("WARN", $"{rootTask.LogName}：链结束后的补搬被取消，源包留在原地（其余物不生成）。"));
            return new DeferredSourceMoveWork(logEntries);
        }

        logEntries.Add((
            "INFO",
            $"{rootTask.LogName}：链结束后的补搬 —— 整条续解链已跑完，{destinationDirectory} 里有 " +
            $"{contentFiles} 个内容物且输出校验通过；本轮没有内容物可定稿、留到现在的源包按设置移入其余物。"));

        /*
         * ⛔ 2026-10-15（用户 2026-10-14 口径）：源包那一档只剩「留下 / 彻底删除」，而**两者都不搬** ——
         * 留下 = 一个字节都不动；彻底删除 = **就地删**（⛔ 不搬进其余物，用户逐字：
         * 「原包一直在原来的位置，选『彻底删除』也是就地删、不用移入其余物」）。
         * ⇒ 这里原来那次 `ExecuteSourcePackageMove(…, SourceMoveTrigger.AfterChain, …)`
         * （把源包搬进其余物 + 随后按「删除操作」清那一份）**整段删除**，
         * 改走与别处**同一条闸门链**（唯一出口 `_host.PurgeLayerSourcePackage`）。
         */
        SourceHandlingMode deferredSourceHandling = _host.RunOptions?.SourceHandling
            ?? AppSettings.ParseSourceHandling(_host.Settings?.SourceHandling);

        if (deferredSourceHandling != SourceHandlingMode.DeleteInPlace)
        {
            logEntries.Add((
                "INFO",
                $"{rootTask.LogName}：按「源包留下」这一档，链结束后源包一个字节都不动"
                + $"（留在原处，随时可手工处理）。"));

            return new DeferredSourceMoveWork(logEntries);
        }

        /*
         * 「彻底删除」：走**同一条闸门链**（L4 完整性 + L5 收尾复核 + 未取消 + 属于本任务 + 半套分卷）。
         *
         * ⚠ 2026-10-15：这里**喂的是真证据**，⛔ 不是放宽闸门 —— 上面刚算出来的
         * <paramref name="rootCompleteness"/>（L4 唯一出口 <see cref="ResultCompletenessClassifier"/>）
         * 就是 `_host.PurgeLayerSourcePackage` 那一档要的那份事实；机器终态、分卷完整性、内容物存在与否
         * 也都在上面逐条查过（任一条不过就在那一条 return 了，走不到这里）。
         * ⇒ 传 `reclaimVerdict` 之后，执行体不再要求 `commit`/`verification` 那两份"本轮定稿"事实
         * （链尾本来就没有它们），而 半套分卷闸门 / 幂等 / 路径形状 那几道**照旧全过**。
         */
        _host.PurgeLayerSourcePackage(
            rootTask,
            verification: null,
            commit: null,
            logEntries,
            LayerPurgeTrigger.SourceHandlingDelete,
            reclaimVerdict: rootCompleteness);

        return new DeferredSourceMoveWork(logEntries);
    }

    /// <summary>
    /// 链上的任务集合（调用方传进来的续解子任务 + 根任务自己），去重后逐个产出。
    /// 根任务单独兜一层：调用方可能只传了子任务，而"根任务也是链上的一员"这件事不能漏。
    /// </summary>
    internal static IEnumerable<ArchiveTask> EnumerateChainTasks(
        ArchiveTask rootTask,
        IReadOnlyList<ArchiveTask>? chainTasks)
    {
        var seen = new HashSet<ArchiveTask>();

        if (chainTasks != null)
        {
            foreach (ArchiveTask? candidate in chainTasks)
            {
                if (candidate != null && seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        if (seen.Add(rootTask))
        {
            yield return rootTask;
        }
    }

    /// <summary>
    /// 数一个最终目录里**真正的内容物文件**：排除 <c>其余物</c>（含旧名 <c>过程物</c>）里的东西，
    /// 排除归档与分卷这类"待续解的过程物"，**并排除工作区自己那棵树**（用户 2026-09-30）。
    ///
    /// <para>这是"内容物确实已定稿"的事实依据 —— 不用"本轮搬了几条"这种过程数字
    /// （用户明确要求：别拿 move 计数当唯一判据）。
    /// 读不了目录时返回 0：宁可判定"没有内容物"而不动源包。</para>
    ///
    /// <para>⛔ 为什么必须排除工作区：工作区默认就建在目标目录里面（<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>），
    /// 而这一支是**删/搬源包（不可逆）之前的闸门** —— 把工作区里的暂存产物数成"内容物"，
    /// 等于用一个假事实去放行不可逆动作。判据唯一出口 <see cref="WorkspaceTree"/>。</para>
    /// </summary>
    internal int CountContentFiles(string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory))
        {
            return 0;
        }

        string workRoot = _host.Paths.WorkDirectory;

        try
        {
            int count = 0;

            // 「内容物保留关键词」判据：命中它的那些**就是内容物**（用户 2026-10-04 的新功能），
            // 所以"这一层有没有内容物"这件事要把它们数进去（判据唯一出口 ContentKeepRules）。
            ContentKeepRules keepRules = ContentKeepRules.FromSettings(_host.Settings);

            foreach (string file in Directory.EnumerateFiles(destinationDirectory, "*", SearchOption.AllDirectories))
            {
                if (WorkspaceTree.ShouldSkipEntry(file, workRoot))
                {
                    continue;
                }

                if (ProcessArtifactLayout.IsInsideArtifactDirectory(file, destinationDirectory))
                {
                    continue;
                }

                if (StageFilePredicates.IsProcessArtifactFile(file) && !keepRules.ShouldKeep(file))
                {
                    continue;
                }

                count++;
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }
}

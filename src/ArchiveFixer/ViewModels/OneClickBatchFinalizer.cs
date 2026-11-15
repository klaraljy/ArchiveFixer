using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 一批的收尾：链尾补搬源包、批末副作用（清卷入口/清壳/恢复源名/清冗余片名）、
/// 一行汇总，以及"这一单算成功/失败/已处理"的既有口径。
///
/// <para>为什么单独成类：这些动作发生在**所有轮次都跑完之后**，与"这一轮解什么"无关；
/// 且 <see cref="BuildSummaryLine"/> 是用户唯一会读的那行结论，独立出来才好逐项对账。</para>
/// </summary>
internal sealed class OneClickBatchFinalizer
{
    private readonly MainViewModel _vm;
    private readonly ExtractionCoordinator _extractionCoordinator;

    private bool _batchFinalized;

    internal OneClickBatchFinalizer(MainViewModel vm, ExtractionCoordinator extractionCoordinator)
    {
        _vm = vm;
        _extractionCoordinator = extractionCoordinator;
    }

    /// <summary>新一批开始时复位幂等标记（调用方：一键处理主体开工）。</summary>
    internal void BeginBatch() => _batchFinalized = false;

    private ObservableCollection<ArchiveTask> Tasks => _vm.Tasks;
    private AppSettings Settings => _vm.Settings;

    /// <summary>这一批实际允许跑几轮（与一键处理主体同一个出口）。</summary>
    private int RoundLimit => Math.Clamp(
        Settings?.MaxRecursionDepth ?? 7,
        1,
        OneClickCoordinator.MaxRoundsCeiling);

    private void AppendLog(string level, string message) => _vm.AppendLog(level, message);

    /// <summary>
    /// 续解链跑完之后，对"本轮没有内容物、记账成待补搬"的**最外层源包**再给一次机会
    /// （2026-09-22 修复的真实缺陷，见调用点的说明）。
    ///
    /// <para>
    /// 三条不补搬的情况（都写日志，不静默）：
    /// ① 链没跑完（停止后续 / 撞轮数上限）→ 取消语义，源包留在原地；
    /// ② 没有一个源包需要补搬 → 什么都不做（连日志都不写，免得每批都刷一行废话）；
    /// ③ 具体的判据不成立（根任务没成功 / 校验没过 / 分卷不完整 / 那个目录里没有内容物）→
    ///    由 <see cref="SourceHandler.CompleteRootSourcePackagesAfterChainAsync"/> 逐条说清原因。
    /// </para>
    /// </summary>
    internal async Task CompleteRootSourcePackagesAsync(
        IReadOnlyList<ArchiveTask> rootTasks,
        IReadOnlyList<ArchiveTask> chainTasks,
        bool stopped,
        bool hitRoundLimit,
        OneClickRunOptions? runOptions = null)
    {
        int pending = rootTasks.Count(task => task.SourcePackageMove == SourcePackageMoveState.DeferredToChainEnd);


        if (stopped)
        {
            /*
             * ⚠ 2026-09-30：**即使 pending == 0 也要写一句**。
             * 链没跑完 ⇒ 链尾那一档整段不执行（"按「删除操作」处理其余物"也在里面），
             * 于是选了「彻底删除」的人会发现过程物还留着 —— 而老写法只在 pending > 0 时才吭声，
             * pending == 0 时**一个字都不写**，用户只能猜（与 ApplyRestHandlingAfterChainAsync
             * 那处静默 return 是同一类毛病）。
             */
            AppendLog(
                "WARN",
                $"一键处理：被「停止后续」中断，链尾那一档（补搬源包 / 按「删除操作」处理其余物）整段没有执行 —— "
                + $"源包与其余物都留在原地（{pending} 个源包本来要补搬）。");
            return;
        }

        /*
         * 「缺卷留到最后再判」：已停用（用户 2026-10-24）。
         * 原来这里调 `FinalizeDeferredVolumeDeficitsAsync`（链尾缺卷终判）；实现仍在
         * `SecondStepResidue/ExtractionCoordinator.DeferredVolumeDeficitStep.cs`，
         * 重建第二大步时再决定接法（不接 ⇒ 这几单如实留在「未处理」，不假报结论）。
         */

        if (hitRoundLimit)
        {
            AppendLog(
                "WARN",
                $"一键处理：还有更深的包没解（已达到 {RoundLimit} 轮上限），链尾那一档整段没有执行 —— "
                + $"源包与其余物都留在原地（{pending} 个源包本来要补搬）；内容物可能还不全，此时不动源包。");
            return;
        }

        if (pending > 0)
        {
            AppendLog(
                "INFO",
                $"一键处理：续解链已结束，对 {pending} 个「本轮没有内容物」的最外层源包做一次补搬（按源包处理档位）。");
        }

        /*
         * ⚠ **即使 pending == 0 也必须调下去**（2026-09-25 第 33 条被测试逮到的那一处）：
         * 链尾这一步不只做"源包补搬"，还负责**按「删除操作」那一档处理已经搬好的其余物**。
         * 形状 A（第一层直接出内容物）下源包在第 1 轮就当场搬进其余物了，
         * 到这里 `SourcePackageMove` 已经是 <c>Done</c> → 老的 `if (pending == 0) return;`
         * 让整条链尾钩子**一次都不跑** → 用户选了「彻底删除」，跑完其余物还在
         * （正是他真机报的那个现象；形状 B 的包因为 pending > 0 反而照常生效，
         * 所以这个缺陷只在"第一层就出内容物"的包上露出来）。
         */
        await _extractionCoordinator.Source.CompleteRootSourcePackagesAfterChainAsync(
            rootTasks,
            chainTasks,
            cancellationToken: default,
            runOptions);

    }

    /// <summary>
    /// 批末唯一副作用阶段：清卷入口、清 group-entry 壳、恢复源名、清冗余片名。
    /// 汇总函数只拼文字，调它不许改盘；本方法幂等。
    /// </summary>
    internal void FinalizeBatch()
    {
        if (_batchFinalized)
        {
            return;
        }

        _batchFinalized = true;

        _extractionCoordinator.VolumeStepPreflight.CleanupAllVolumeStepEntryDirectories();
        _extractionCoordinator.VolumeStepPreflight.CleanupGroupEntryShellsFor(Tasks);
        _extractionCoordinator.SourceProtection.RestoreKeepInPlaceSourceNames();
        _extractionCoordinator.PieceAdoption.PurgeRedundantPieceAliases();

        // 源快照删完、片名收完，再把这一趟用过的每个工作区根收一遍空壳（根下无文件才删）。
        _extractionCoordinator.CleanupRunWorkspaceShells();
    }

    /// <summary>
    /// 一行汇总。
    ///
    /// 硬要求：各分项加起来**必须等于本次处理的任务数**。
    /// 以前这里统计整个列表，还把"扫描过但从没被处理"的任务算成失败 ——
    /// 于是出现过 `成功 0 / 失败 1 / 跳过 2（共 4 个任务）`：0+1+2=3≠4，
    /// 用户根本没法判断到底发生了什么。汇总如果自己都对不上，还不如不显示。
    ///
    /// 续解加进来的任务同样计入"本次处理的任务数"（它们确实被处理了），口径写在 scope 里。
    /// </summary>
    /// <param name="pendingContinuation">
    /// 撞到轮数上限时"已加进列表、这一批没解"的内层包个数（用户 2026-09-24 第 16 条追加）。
    /// 大于 0 时汇总里必须写出**还剩多少 + 怎么继续** —— 到顶静默停下正是他要修的那件事。
    /// </param>
    internal string BuildSummaryLine(
        IReadOnlyList<ArchiveTask> targets,
        bool stopped = false,
        bool stopRequestedByUser = false,
        int continuationLayers = 0,
        bool hitRoundLimit = false,
        int pendingContinuation = 0,
        string stoppedPhase = "")
    {
        /*
         * ===== 分项一律按**机器终态**算，而且**数法只有一份** =====
         *
         * 这份数法（<see cref="BatchOutcomeTally"/>）是本行、批末「本批汇总」那一行、
         * 以及导出的日志头部**共用**的：三处读同一对字段（`Outcome` + `OutputVerification`）
         * 与同一个事实位（`CountsTowardBatchOutcome`），⛔ 不许哪一处再自己 `Count(task => …)`。
         *
         * 为什么收敛（用户 2026-10-02 真机日志）：过去三处各写一遍，于是同一批里
         * ①「跳过」一处算跟班卷、一处不算（2026-10-01 第三报的现场：
         *   10 个任务里 4 个是各组的分卷后续卷，一行「跳过 4」被他读成"还有 4 个没弄完"）；
         * ②「终态说成功、校验却判否」那一帧在一处算成功、另一处算失败
         *   （旧写法这里读的是状态字符串，而递归失败那条路过去只写状态、`Outcome` 留在 `Pending`
         *   ⇒ ①页说「部分完成」、这里说「未处理 1」、批末诊断说「下一步：其他」）。
         *
         * 恒等式「各分项之和 + 未处理 = 本次任务数」由 tally 内部兜底：
         * 跟班卷单列一档、`untouched` 按"总数 − 各分项"倒推，所以将来加了新终态也不会凭空漏一个数。
         */
        /*
         * ⛔ 同一任务对象可能被连续两轮追加进 processed（真机 2026-11-08：风景01 三单在
         * 第一/第二轮各出现一次，最终行写“本次 40 / 列表 37”）。汇总按**对象身份**去重，
         * 不能把同一单的两次处理算成两个任务。
         */
        targets = DistinctTargets(targets);

        BatchReport report = BatchReport.Create(targets);
        BatchOutcomeTally tally = report.Tally;

        var parts = new List<string>(tally.BuildParts());

        string scope = targets.Count == Tasks.Count
            ? $"本次 {targets.Count} 个任务"
            : $"本次 {targets.Count} 个 / 列表共 {Tasks.Count} 个";

        /*
         * 被「停止后续」打断时不许写成"完成"：失败/取消/部分完成不得显示成成功（不变量 6）。
         * 分项数字照旧自洽，只是把话说准。
         */
        string line = $"{(stopped ? "一键处理已停止" : "一键处理完成")}：{string.Join(" / ", parts)}（{scope}）。";

        int renameSuccess = targets.Count(t => t.Status == StatusText.RenameSuccess);

        if (renameSuccess > 0)
        {
            line += $" 已修正后缀 {renameSuccess} 个。";
        }

        // 跳过必须说清为什么，否则"跳过 2"等于没说
        // ⚠ 跟班卷单独说（下一句），⛔ 不许在这里再算一遍 —— 它跳过的理由不是"7-Zip 说它不是压缩包"。
        int notArchive = targets.Count(t =>
            t.Status == StatusText.Skipped && !t.IsArchive && t.CountsTowardBatchOutcome);

        if (notArchive > 0)
        {
            line += $" 跳过的 {notArchive} 个已由 7-Zip 确认不是压缩包。";
        }

        /*
         * 跟班卷单独一句：它**不是**"没做成"（整组由第一卷那一单解完，它按设计不重复解）。
         * 用户真正会读错的就是这个数字。
         *
         * ⚠ 文案与批末「本批汇总」那一行、日志导出头部**共用同一句**
         * （`BatchOutcomeTally.DescribeFollowerNote` —— 用户 2026-10-02 真机：同一份日志里
         * 一处「跳过 1」、一处「跳过 0」，两个口径；现在三处读同一个事实位、写同一句话）。
         */
        if (tally.FollowerSkipped > 0)
        {
            line += " " + tally.DescribeFollowerNote();
        }

        if (tally.ContentGroupProcessSkipped > 0)
        {
            line += " " + tally.DescribeContentGroupProcessNote();
        }

        int passwordError = targets.Count(t => t.Status == StatusText.WrongPassword);

        if (passwordError > 0)
        {
            line += $" 密码错误 {passwordError} 个：检查密码本里是否包含这些包的密码。";
        }

        int corrupted = targets.Count(t => t.Status == StatusText.Corrupted);

        if (corrupted > 0)
        {
            line += $" 文件损坏 {corrupted} 个：这类只能重新下载。";
        }

        if (stopped)
        {
            /*
             * ⚠ 两种"停下"必须分开说（2026-09-28 真机）：用户按了「停止后续」，和
             * "本轮有任务真的没轮到"（程序自己的判断）是两件事 —— 老代码一律写"已按「停止后续」中断"，
             * 于是用户没按过也被告知"你按了"，还会误以为程序没错。
             */
            /*
             * 停在第二大步的解析/试开/命名阶段是**另一种停**：那时还没有开始解那一层，
             * ⛔ 不许套"已按停止后续中断（已完成续解 N 层）"那句话（真机 2026-11-10 00:14:28 就是这么写的，
             * 实际 21 GB 已经解完）。
             */
            string reason = stoppedPhase.Length > 0
                ? $"已按「暂停所有操作」在{stoppedPhase}阶段停下（还没开始解那一层）"
                : stopRequestedByUser
                    ? "已按「停止后续」中断"
                    : "本轮有任务没轮到，已停下（不是你按的「停止后续」）";

            line += continuationLayers > 0
                ? $" {reason}（已完成续解 {continuationLayers} 层）。"
                : $" {reason}，没有继续解内层包。";
        }
        else if (continuationLayers > 0)
        {
            // 续解出来的内层包**不另建目录**：产物全部归到源包那一个输出目录里
            // （用户诉求："一个源包 = 一个最终目录"）。这句话就是给用户对账用的。
            line += $" 自动续解 {continuationLayers} 层（产物归入同一个输出目录，不再另建文件夹）。";
        }

        if (hitRoundLimit)
        {
            /*
             * 用户 2026-09-24 第 16 条追加：到顶必须"提示还剩多少 + 怎么继续"。
             * 只有"已达到 N 轮上限，没有继续"这句话时，用户看到的就是"又只解了两层"——
             * 他不知道还剩什么、也不知道下一步点哪里。
             */
            line += pendingContinuation > 0
                ? $" 已达到 {RoundLimit} 轮上限，还剩 {pendingContinuation} 个内层包没解（已加进列表并勾好，点「继续解」接着解）。"
                : $" 还有更深的内层包，但已达到 {RoundLimit} 轮上限，没有继续。";
        }

        /*
         * 「成品目录里还留着内层包、内容还没解出来」—— 用户 2026-11-10 真机口径：
         * 汇总里一个字都没有就等于没说。事实位在任务上（唯一写入点在链尾清点），这里只摆出来。
         */
        int leftBehindInnerPackages = targets
            .SelectMany(task => task.LeftBehindInnerPackageNames)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (leftBehindInnerPackages > 0)
        {
            line += string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.OneClickLeftBehindInnerPackagesSuffixFormat,
                leftBehindInnerPackages);
        }

        return line;
    }

    /// <summary>
    /// 解压成功（含"已覆盖"）**且产物校验没有判否**。
    ///
    /// <para>
    /// ⚠ 2026-09-24 起加上了后半句（不变量 6 的真机违反）：用户机器上出现过
    /// "状态写着解压成功、校验却已判否"的那一帧。**统计口径不许把那种任务算成成功**，
    /// 所以这里读一次校验事实 —— 这是**机器可判的字段**，不是中文文案比较
    /// （用户 2026-09-24 明确要求：统计与裁决不许依赖状态字符串）。
    /// </para>
    /// <para>
    /// ⛔ 它只用于**统计与显示**。凡是"要不要删源 / 搬源 / 继续往下解"的裁决，
    /// 一律读 <see cref="ArchiveTask.OutputVerification"/> 与 <see cref="ArchiveTask.Outcome"/>，
    /// 不许调这个方法（那是本次修复的另一半）。
    /// </para>
    /// </summary>
    internal static bool IsSuccessStatus(ArchiveTask task)
    {
        /*
         * 判据本体搬到了 Models/TaskOutcomeClassifier —— 这里转调。
         * 搬家的理由见那个类的注释：同一份名单以前在 OneClickCoordinator 与
         * TaskSummaryService 各写了一遍，漏一条就表现为"①页算失败、一键汇总算未处理"。
         */
        return TaskOutcomeClassifier.IsSuccessStatus(task);
    }

    /// <summary>
    /// 这个任务"这一轮处理过了"吗 —— 有终态的都算处理过。
    /// 没有终态的（已识别 / 等待解压 / 解压中…）说明它**根本没轮到**，那就是被「停止后续」截断的信号。
    ///
    /// internal 是为了让测试直接钉住"新增状态有没有被认账"（同类先例：旧后缀白名单）：
    /// 漏一个状态不会编译失败，只会让汇总把"跑完了"说成"已停止 / 未处理 N"。
    /// </summary>
    internal static bool IsHandled(ArchiveTask task)
    {
        if (task == null)
        {
            return false;
        }

        /*
         * **机器终态优先**（用户 2026-09-27 真机：口径打架的根子之一）。
         *
         * `Outcome` 不是 `Pending` 就说明这一单确实跑过了并落了结论 —— 这是事实，
         * 比任何状态名单都硬。旧写法只读状态名单，于是"状态写着某个名单外的中文、
         * 终态却已经是失败"的帧会被读成"没轮到"，汇总当场打印「未处理 1 / 已按停止后续中断」，
         * 而用户根本没按过停止（真机 `giu.7z.001` 就是这一帧）。
         */
        if (task.Outcome != TaskOutcome.Pending)
        {
            return true;
        }

        // 终态还没落的（少数路径 / 老数据 / 测试直接造的任务）：退回状态名单判。
        return IsSuccessStatus(task) ||
               task.Status == StatusText.PartiallyCompleted ||
               task.Status == StatusText.Cancelled ||
               task.Status == StatusText.Skipped ||
               /*
                * ⚠ 2026-10-08：**缺卷要单列**（用户拍板后它已不在失败名单里）——
                * 这一单确实跑过了并落了结论（只是不完整），⛔ 不许被读成"没轮到"
                * （那会让汇总打印"一键处理已停止 / 未处理 N"）。
                */
               task.Status == StatusText.VolumeMissing ||
               IsFailureStatus(task);
    }

    /// <summary>
    /// 真正"处理过并且失败了"的状态。
    ///
    /// 「达到密码尝试上限」也算（不变量 6 的反面同样成立：跑完了不许说成"被停止"）：
    /// 候选试到上限就停了，这是一次**正常的失败终态**。漏掉它会同时坏两件事：
    /// ① 汇总的"失败"里少算一个，分项之和与任务数对不上；
    /// ② <see cref="IsHandled"/> 跟着返回 false → 被当成"没轮到" → 汇总打印
    ///    "一键处理已停止 / 未处理 N"，把正常结束误报成被用户停止。
    /// 实测量到的错话（负向对照）：
    /// "一键处理已停止：成功 0 / 失败 0 / 跳过 0 / 未处理 1（本次 1 个任务）。已按「停止后续」中断…"
    /// </summary>
    internal ArchiveTask? FindSucceededTaskInLayer(string layer, string baseName)
    {
        if (string.IsNullOrWhiteSpace(layer)
            || string.IsNullOrWhiteSpace(baseName))
        {
            return null;
        }

        foreach (ArchiveTask task in Tasks)
        {
            if (task.Outcome != TaskOutcome.Succeeded
                || !task.CountsTowardBatchOutcome
                || string.IsNullOrWhiteSpace(task.CurrentPath))
            {
                continue;
            }

            string taskLayer = Path.GetDirectoryName(task.CurrentPath) ?? string.Empty;

            if (string.Equals(taskLayer, layer, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    FileNameHelper.GetArchiveBaseName(task.CurrentPath),
                    baseName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return task;
            }
        }

        return null;
    }

    private static IReadOnlyList<ArchiveTask> DistinctTargets(IReadOnlyList<ArchiveTask> targets)
    {
        var seen = new HashSet<ArchiveTask>();
        var result = new List<ArchiveTask>(targets.Count);

        foreach (ArchiveTask task in targets)
        {
            if (seen.Add(task))
            {
                result.Add(task);
            }
        }

        return result;
    }

    internal static bool IsFailureStatus(ArchiveTask task)
    {
        /*
         * 名单本体只有一个出口：<see cref="TaskOutcomeClassifier"/>（本方法只转调）。
         *
         * 旧写法是在这里 `task.Status is …` 再写一遍名单，与 ①页那份
         * （`TaskSummaryService.IsExtractFailureStatus`）各写各的 —— 2026-09-29 因此漏过两条，
         * 2026-09-27 真机 `giu.7z.001` 又出现了"①页算失败、一键汇总算未处理"。
         * ⛔ 以后加状态只改 <see cref="TaskOutcomeClassifier"/> 一处。
         */
        return task != null && TaskOutcomeClassifier.IsFailureStatus(task.Status);
    }

    /// <summary>
    /// 这一组对应的任务目标工作区根；用于把标准名入口放到“解压发生的地方”，
    /// 而不是源盘根目录。找不到任务/输出目录时返回 null，由调用方回落。
    /// </summary>
    internal string? ResolvePreparedGroupTargetWorkRoot(PreparedVolumeGroup group)
    {
        /*
         * 唯一出口：入口任务 ⇒ 生产者任务（账本 ProducedByTaskId）⇒ 都没有就不猜。
         * ⛔ 不在这里堆第二套路径比较；事实链在 SecondStepEntryRootResolver，测试直接钉它。
         */
        return SecondStepEntryRootResolver.Resolve(
            group,
            Tasks,
            _extractionCoordinator.VolumeHandoff.FirstStepLedger,
            AppendLog);
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 整条单一物（单包或整组）最终成功时，清掉曾经为失败保留的最后一层过程物。
    /// 失败 / 部分完成 / 待处理 / 取消的组一律不动；源包路径一律不碰。
    /// </summary>
    internal static int CleanupSuccessfulProcessArtifacts(IEnumerable<ArchiveTask>? tasks, Action<string>? log = null)
    {
        if (tasks == null)
        {
            return 0;
        }

        int deleted = 0;

        foreach (IGrouping<string, ArchiveTask> group in tasks
                     .Where(task => task != null && !string.IsNullOrWhiteSpace(task.ChainRootIdentity))
                     .GroupBy(task => task.ChainRootIdentity, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Any(task => task.Outcome is TaskOutcome.Failed
                        or TaskOutcome.PartiallyCompleted
                        or TaskOutcome.Pending
                        or TaskOutcome.Cancelled))
            {
                continue;
            }

            var sourcePaths = new HashSet<string>(
                group.SelectMany(task => new[] { task.CurrentPath, task.OriginalPath, task.RootSourcePath })
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => Path.GetFullPath(path)),
                StringComparer.OrdinalIgnoreCase);

            foreach (ArchiveTask task in group)
            {
                string artifact = task.LastProcessArtifactPath;

                if (string.IsNullOrWhiteSpace(artifact)
                    || string.IsNullOrWhiteSpace(task.RestDirectoryPath)
                    || !File.Exists(artifact))
                {
                    RemoveEmptyRestDirectory(task.RestDirectoryPath);
                    continue;
                }

                string fullArtifact = Path.GetFullPath(artifact);

                if (sourcePaths.Contains(fullArtifact)
                    || !ArchivePathGuard.IsInsideRoot(task.RestDirectoryPath, fullArtifact, out _))
                {
                    continue;
                }

                try
                {
                    FileOperations.Delete(fullArtifact);
                    task.LastProcessArtifactPath = string.Empty;
                    deleted++;
                    log?.Invoke(
                        $"{task.LogName}：单一物最终成功，已清掉此前保留的最后一层过程物（{Path.GetFileName(artifact)}）。");

                    RemoveEmptyRestDirectory(task.RestDirectoryPath);
                }
                catch (Exception ex)
                {
                    log?.Invoke(
                        $"{task.LogName}：最后一层过程物清理失败（{Path.GetFileName(artifact)}）：{ex.Message}");
                }
            }
        }

        return deleted;
    }

    private static void RemoveEmptyRestDirectory(string? restDirectory)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(restDirectory)
                && Directory.Exists(restDirectory)
                && !Directory.EnumerateFileSystemEntries(restDirectory).Any())
            {
                Directory.Delete(restDirectory, recursive: false);
            }
        }
        catch
        {
            // 只影响空壳清理由，不能影响任务结论。
        }
    }

    /// <summary>
    /// **这条名字是不是内容卡自证的规范名**（本方法只负责"拍一次卡"；判据唯一出口 =
    /// <see cref="Detection.VolumePieceFacts.IsNameItsOwnCanonicalName"/>）。
    /// </summary>
    internal static bool IsContentCanonicalName(string path)
    {
        try
        {
            return Detection.VolumePieceFacts.IsNameItsOwnCanonicalName(FileNature.Inspect(path));
        }
        catch
        {
            return false;
        }
    }
}


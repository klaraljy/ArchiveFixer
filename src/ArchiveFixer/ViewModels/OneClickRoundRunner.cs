using ArchiveFixer.BindingModels;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 一键处理的**轮前准备**与**轮尾结论**：这两段都只读写 <see cref="OneClickRunState"/> 里那笔
/// 跨阶段账，与"这一轮解什么、怎么解"无关。
///
/// <para>⛔ 判据与文案一个字不改（同一批日志、同一份汇总、同一个 <see cref="OneClickOutcome"/> 形状）；
/// 轮循环与第二大步仍留在 <see cref="OneClickCoordinator"/>，下一阶段再搬。</para>
/// </summary>
internal sealed class OneClickRoundRunner
{
    private readonly MainViewModel _vm;
    private readonly ExtractionCoordinator _extractionCoordinator;
    private readonly OneClickBatchFinalizer _batchFinalizer;
    private readonly InnerLayerCollector _innerLayerCollector;

    internal OneClickRoundRunner(
        MainViewModel vm,
        ExtractionCoordinator extractionCoordinator,
        OneClickBatchFinalizer batchFinalizer,
        InnerLayerCollector innerLayerCollector)
    {
        _vm = vm;
        _extractionCoordinator = extractionCoordinator;
        _batchFinalizer = batchFinalizer;
        _innerLayerCollector = innerLayerCollector;
    }

    private ObservableCollection<ArchiveTask> Tasks => _vm.Tasks;

    private AppSettings Settings => _vm.Settings;

    private void AppendLog(string level, string message) => _vm.AppendLog(level, message);

    /// <summary>
    /// 轮前准备：定下这一批的目标、选项快照、运行冻结快照，并清掉上一批的幂等标记。
    /// 返回本次运行的不可变快照（调用方写入 <c>CurrentRunSnapshot</c>；具体解压模式的早退也在调用方）。
    /// </summary>
    internal RunSnapshot Prepare(
        OneClickRunState state,
        IReadOnlyList<ArchiveTask> firstRoundTargets,
        OneClickRunOptions? runOptions)
    {
        /*
         * ⛔ 这一批只处理**还在任务列表里**的目标（2026-09-28 真机"只解一层就停"的守门）。
         *
         * 历史成因：一键处理开工前曾把无用物从列表里移掉，被移掉的任务既不在列表里、也不会被
         * 解压循环碰到 ⇒ 收尾守卫把它们读成"这一轮有任务没轮到" ⇒ 误判成用户叫停。
         * ⚠ 2026-11-01 起开工前**不再**移无用物（自动剔除只在文件夹导入那一次），但这条过滤
         * **保留**：任何"清单拍下之后列表又变了"的路径（以后新加的）都靠它兜住。
         *
         * ⚠ 判据是**引用**（那个任务对象还在不在列表里），不按路径比 ——
         * 同一个文件被重新导入会是一个新任务，路径一样但它不属于这一批。
         */
        var listedTasks = new HashSet<ArchiveTask>(Tasks);
        state.RootTargets = firstRoundTargets.Where(listedTasks.Contains).ToList();

        AppendLog("INFO", $"一键处理开始，共 {state.RootTargets.Count} 个任务。");

        state.SnapshotOptions = runOptions
            ?? OneClickRunOptions.FromSettings(Settings, _vm.SelectedOutputDirectory);

        _batchFinalizer.BeginBatch();

        state.Snapshot = RunSnapshot.Capture(
            state.RootTargets,
            state.SnapshotOptions,
            _vm.SpaceTightMode,
            _vm.SpaceTightKeepSource,
            Settings.UseSpecificExtraction,
            _vm.SelectedOutputDirectory,
            _extractionCoordinator.SpecificExtractionPasswords,
            state.StopToken);

        return state.Snapshot;
    }

    /// <summary>
    /// 轮尾：链尾补搬源包、回填内容组、批末副作用、一行汇总、批末诊断，交出最终结论。
    /// </summary>
    internal async Task<OneClickOutcome> BuildOutcomeAsync(OneClickRunState state)
    {
        /*
         * 链结束后的**源包补搬**（2026-09-22 修掉的真实缺陷）。
         *
         * 用户的真实文件（222.mp4 = 假 MP4 头 + 尾部 ZIP + 内层加密分卷）第一层只出内层分卷，
         * 一个内容物都没有 —— 最外层那一轮因此没法按"内容物已定稿"把源包搬进其余物，
         * 而真正产出内容物的是续解子任务，子任务按设计跳过源包处理。
         * 于是最外层那一轮把源包记账成"留到链结束后补搬"（SourcePackageMoveState.DeferredToChainEnd），
         * 由这里 —— **只有这里知道链什么时候结束** —— 补做一次。
         *
         * 链没跑完时一律不补搬（源包留在原地）：
         * · 被「停止后续」打断 / 有任务没轮到：这是取消语义，不该再动用户的源文件；
         * · 撞到轮数上限：更深的包还没解，内容物可能不全，不能在此时把源包搬走。
         * 两种情况都写 WARN 说明原因，绝不静默。
         */
        state.StopRequested |= state.StopToken.IsStopRequested;

        await _batchFinalizer.CompleteRootSourcePackagesAsync(
            state.RootTargets,
            state.Processed,
            state.Stopped || state.StopRequested,
            state.HitRoundLimit,
            state.SnapshotOptions);

        /*
         * 链尾解压/组终态已经全部落定，回填一次内容组最终事实。
         * 只读组/任务已有事实，不扫 Pending、不按文件存在性猜。
         */
        _extractionCoordinator.FinalizeContentGroups(state.Processed);

        /*
         * ===== 交界处批末对账（D 段，2026-10-24）=====
         *
         * 只打事实行：`[第二大步·交接] 本批交接 N 份（在盘 / 已不在）｜已消费 M｜待处理 K`
         * 与每组一行 `[第二大步·结论] 基名：…`；⛔ 不搬、不删、不改名。
         */
        _extractionCoordinator.AppendVolumeHandoffBatchSummary();

        _batchFinalizer.FinalizeBatch();

        string summary = _batchFinalizer.BuildSummaryLine(
            state.Processed,
            state.Stopped,
            state.StopRequested,
            state.ContinuationLayers,
            state.HitRoundLimit,
            state.PendingContinuation.Count,
            state.StoppedPhase);

        AppendLog("INFO", summary);

        /*
         * ===== 空间曲线的**补记那一针**（用户 2026-10-02 真机）=====
         *
         * 批末那条曲线的"收"是在**上面这次其余物处理之前**打的（`StartExtractAsync` 收尾时就写了），
         * 而其余物的删除发生在那之后 ⇒ 真机日志因此把真实收尾读小了。
         * 所以**其余物处理完之后**再补一针真实可用空间：走本批侦察器自己的探测函数、
         * 文案由 `SpaceTrendMonitor` 产出、没有侦察器时什么都不写（绝不编一个数字）。
         */
        _extractionCoordinator.RecordSpaceTrendPostscript("其余物处理之后");

        /*
         * 批末的**一份**结论（用户 2026-09-30 第 3 条："严重度管颜色、诊断管文字，
         * 两者都从同一份任务终态算出来，⛔ 不许各算一遍"）。
         * 判据与排版全在 Models\BatchSummaryDiagnostics.cs（唯一出口）。
         */
        BatchSummaryReport report = BatchSummaryDiagnosticsRules.Build(state.Processed);

        return new OneClickOutcome
        {
            Rounds = state.Round,
            ContinuationLayers = state.ContinuationLayers,
            Stopped = state.Stopped,
            HitRoundLimit = state.HitRoundLimit,
            PendingContinuationCount = state.PendingContinuation.Count,
            Summary = summary,
            Diagnostics = report.Text,
            DiagnosticLines = report.Lines,

            /*
             * 严重度从**这一批真正处理过的全部任务**算（根任务 + 所有续解子任务，就是 state.Processed）：
             * 判据是机器终态，⛔ 不比中文文案。空批会得到 Success —— "什么都没发生"用蓝色最诚实。
             */
            Severity = report.Severity
        };
    }

    /// <summary>
    /// A 段：一轮的识别 → 解压 → 产物内层包收集 → 轮判定。
    /// 返回 true = 这一轮按"停止后续 / 有任务没轮到"收场，调用方直接 break。
    /// </summary>
    internal async Task<bool> RunFirstStepAsync(
        OneClickCoordinator host,
        OneClickRunState state,
        OneClickRoundState roundState,
        OneClickRunOptions? runOptions)
    {
        /*
         * 给日志换一次来源上下文（⛔ 只影响日志记录，判据一个字不变）：
         * 大测试要求"只看日志排查" ⇒ 每一行都要能回答"这是第几轮、哪一步"。
         * 阶段先按本轮的入口步骤落"第一大步"；轮判定判出下一轮走第二大步时再改（见下面那一行）。
         */
        _vm.UpdateLogRunContext(round: state.Round, stage: "第一大步");

        // 第一步：识别（第 2 轮起只扫本轮新加进来的包）。
        await host.ScanRoundAsync(state.Round, state.RoundTargets);

        /*
         * ⛔ 本轮"开跑时"的目标快照（2026-09-28 真机）。
         *
         * 收尾那个"有任务没轮到就停"的守卫**只看这份快照**：本轮解压途中会往列表里加
         * **下一轮**才该解的内层包（AddInnerTasksAsync），拿实时清单去比，那些刚加进来、
         * 本来就该下一轮跑的任务会被当成"这一轮没轮到" → 正常续解被误判成"用户叫停"，
         * 于是**只解一层就收工**（真机现象；总结还会说"已按「停止后续」中断"，用户根本没按过）。
         *
         * ⚠ 2026-09-28 复检（补守门用例时当场量到的）：这份快照与 roundTargets 在同一个迭代里
         * **内容逐项相同**（roundTargets 只在本轮末尾才被重新赋值，中途没有任何地方改它），
         * 所以"改用快照"这一下本身不改变行为，真机上那个"只解一层就停"也不是它治好的 ——
         * 当时的成因是"目标清单里混进了已经从列表里移掉的任务"（开工前清无用物那一步）；
         * 2026-11-01 起开工前不再移无用物，那条过滤仍在 RunPipelineAsync 入口保留作通用守卫。
         * 保留这份快照的理由：它把"守卫只看本轮开跑时的目标"这条语义**写死**在这里，
         * 以后谁要是把 roundTargets 改成实时清单（比如直接传 Tasks），守卫不会跟着一起错。
         */
        List<ArchiveTask> roundStartTargets = state.RoundTargets.ToList();

        /*
         * 第二步：修正伪装后缀。只处理确实需要改的。
         *
         * **一键档不弹预览**（用户 2026-09-25 明确指示）："这个一键处理自己会自动改名自动解压，
         * 为什么遇到这种改名的压缩包还要我两次确认……那种情况只有手动档才会有"。
         * 每个改名照旧逐条写进日志与任务列表；手动档（②页/右键那几条命令）**照旧先出预览**。
         */

        /*
         * 解压**之前**先记下候选目录里已有的文件。
         *
         * 续解只认"这一轮新出现的"归档起点，不能把目录里本来就有的压缩包当成产物：
         * 输出目录可以就是源目录（设置里的"解压到原目录 + 不建包名文件夹"），
         * 那样一来用户自己放在旁边的包会被当成内层包重新解一遍 —— 等于偷偷处理了没勾选的文件。
         *
         * 目录枚举（可能几万个文件）在后台线程上做：一键处理跑在 UI 线程上，
         * 收尾阶段任何一次全目录遍历都会让窗口卡住（本项目最容易出卡死的一块）。
         */
        (HashSet<string> existingFiles, HashSet<string> unreadableDirectories) =
            await host.SnapshotCandidateDirectoriesAsync(state.RoundTargets);

        // 第三步：解压（密码本、旁路说明文件、分卷守卫都在解压流程里生效）。
        // 走**整理路径**的入口：定稿 + 校验通过 + 未取消之后按设置处理源包
        // （默认移入其余物，决策 D-9/D-11/D-12）。手动「只解压」按钮走的
        // StartExtractAsync 是地基路径，永远不动源包 —— 两者只在这一件事上不同。
        AppendLog("INFO", state.Round == 1 ? "一键处理：开始解压。" : $"一键处理：第 {state.Round} 层开始解压。");

        /*
         * ⚠ `continuationRound`（2026-10-05 口径 2）：第 2 轮起**不清**"缺卷留到最后再判"
         * 的那份名单（第 1 轮没解出来的第 1 卷很可能这一轮才出现）。
         * ⛔ 它不改任何解压行为、更不是第二套轮数 —— 轮数仍然只有 `RoundLimit` 一个出口。
         */
        await _extractionCoordinator.StartExtractForOneClickAsync(runOptions, continuationRound: state.Round > 1);

        /*
         * 组级最终内容判决：owner 已经落终态才回填；空输出/未逐条核对一律不判 Verified。
         * 这一行只读任务上的既有事实，不重算解压结果。
         */
        _extractionCoordinator.FinalizeContentGroups(Tasks);

        host.UpdateSummary();

        // 「停止后续」之后不许再续解：按下过停止，或者这一轮有任务根本没轮到。
        // ⚠ 用 roundStartTargets（本轮开跑时的快照），不用实时 roundTargets ——
        //    理由见上面那段注释：中途加进来的下一轮任务不该算"这一轮没轮到"。
        //
        // ⛔ **在册的"缺卷留到最后再判"任务不算"没轮到"**（2026-10-05 真机第八批）：
        //    它们**按设计**就是没有机器终态的（先记缺口、等批末再判）—— 老写法把它们读成
        //    "没轮到" ⇒ 这一轮判成"被停止" ⇒ 链尾整段不跑 ⇒ **补判永远不发生**，
        //    汇总还把一单"缺末片"报成「没轮到」。判据只读那份名单（事实位）。
        if (state.StopRequested
            || roundStartTargets.Any(t => !OneClickBatchFinalizer.IsHandled(t) && !_extractionCoordinator.IsDeferredVolumeDeficit(t)))
        {
            state.Stopped = true;
            return true;
        }

        /*
         * 从本轮成功任务的产物里找内层包。找不到就结束，但**不许静默**：
         * 扫描的每一条判据都会写进日志（认出谁 / 跳过谁 / 为什么），
         * 于是"只解了一层"这类问题**看日志就能定位**（用户 2026-09-25 明确要求）。
         */
        /*
         * 产物扫描也在 UI 线程上串行做（目录枚举才在 Task.Run 里）。
         * 先落真实阶段，卡顿时监控才能指到“扫描”而不是恢复后的下一段。
         */
        ArchiveFixer.Services.UiActivity.Mark(
            "第一大步产物扫描",
            detail: $"第 {state.Round} 轮，扫描新产物");

        /*
         * 产物扫描的正文全部是文件系统只读判定（不碰 UI 集合、不写任务状态），
         * 整段放后台：即使目录里有几千个新文件，UI 线程也不会被串行 I/O 钉住。
         * `CollectInnerLayersAsync` 自己只返回候选与日志；任务表由 await 回来后的
         * UI 线程继续 `AddInnerTasksAsync`。
         */
        (List<InnerArchiveCandidate> innerArchives, List<string> scanLog) =
            await Task.Run(() => _innerLayerCollector.CollectAsync(
                state.Round,
                state.RoundTargets,
                existingFiles,
                unreadableDirectories,
                state.SourcePaths)).ConfigureAwait(true);

        ArchiveFixer.Services.UiActivity.Mark(
            "轮判定",
            detail: $"第 {state.Round} 轮，分类扫描结果");

        /*
         * ===== 轮判定（用户 2026-10-11 定的轮次模型，本程序的基石）=====
         *
         * 一个轮 = **第一大步**（单一压缩包全部解压）+ **第二大步**（分卷，只解一层）；
         * 一轮结束时按"解出来的东西**是什么**"决定下一轮从哪一步开始（唯一出口
         * `RoundProductClassifier`，判据只转调既有那几把尺子）：
         *   单一压缩包 ⇒ 下一轮第一大步；分卷 ⇒ 下一轮第二大步（⛔ 不再过一次第一大步）；
         *   其余就是内容物 ⇒ 到此为止。
         *
         * ⚠ 同一批产物在不同轮里**角色不同**（用户打的比方是 `1+=1` 的重载：结果不是终点、
         * 它还会参与下一轮的计算）⇒ 每到轮边界**重新分类一次**，⛔ 不许继承上一轮的角色。
         * ⚠ 这一行**必须看得见**（轮边界只看日志就能定位）—— 用户 2026-09-25 的同一要求。
         */
        RoundProductClassifier.Summary roundSummary = RoundProductClassifier.Classify(
            innerArchives.Select(candidate => candidate.Path));

        AppendLog(
            roundSummary.Step == RoundProductClassifier.NextStep.Finished ? "INFO" : "INFO",
            "一键处理：" + roundSummary.Describe(state.Round));

        /*
         * 轮判定说了下一轮从哪一步开始 ⇒ 顺手把日志上下文里的"阶段"改成它
         * （⛔ 只影响日志记录）：大测试要能按"第一大步 / 第二大步"筛日志。
         */
        _vm.UpdateLogRunContext(
            stage: roundSummary.Step switch
            {
                RoundProductClassifier.NextStep.SinglePackageStep => "第一大步（单一压缩包全部解压）",
                RoundProductClassifier.NextStep.VolumeStep => "第二大步（分卷，只解一层）",
                _ => "收尾（没有还要解的东西）"
            });

        /*
         * 轮判定那一处判据**结构化落一行**（大测试要能按"轮次 + 判据名"筛出来：
         * 这一轮到底按什么分成"单一压缩包 / 分卷 / 内容物"，结论是下一步走哪一支）。
         */
        _vm.CriterionLog(
            criterion: "轮判定",
            inputs: $"单一压缩包={roundSummary.SinglePackages.Count} 个；分卷={roundSummary.VolumeEntries.Count} 组；"
                    + $"其余都是内容物={roundSummary.ContentCount} 个",
            verdict: roundSummary.Step.ToString(),
            action: roundSummary.Describe(state.Round),
            level: "INFO");

        roundState.InnerArchives = innerArchives;
        roundState.ScanLog = scanLog;
        roundState.RoundSummary = roundSummary;

        return false;
    }


    /// <summary>
    /// C 段：轮尾续解规划（到顶停手 / 下一轮目标 / 续解计数 / 下一轮输入）。
    /// 返回 true = 这一轮到此结束，调用方直接 break。
    /// </summary>
    internal async Task<bool> RunRoundTailAsync(
        OneClickCoordinator host,
        OneClickRunState state,
        OneClickRoundState roundState,
        SecondStepWorkScope secondStepWork)
    {
        /*
         * 第二大步在当前这一轮已经干完（解析/试开/命名都结束），这是"暂停会不会又跑起来"的最后一道闸：
         * 用户在这段里按过暂停/停止当前 ⇒ 在这里停下，绝不再进下一批 21 GB 解压（真机 2026-11-10）。
         */
        if (state.StopToken.IsStopRequested || secondStepWork.Token.IsCancellationRequested)
        {
            state.Stopped = true;
            state.StoppedPhase = "第二大步解析/试开/命名";
            return true;
        }

        if (roundState.InnerArchives.Count == 0 && roundState.VolumeEntryTasks.Count == 0)
        {
            /*
             * 这一句是必须的，不是噪音。
             *
             * 以前这里一声不吭地 break：用户完全分不清"本来就没有内层包"和"续解功能坏了、
             * 或者内层包落在了没被看到的目录里"。真实踩坑就是后者 —— 输出目录已存在时
             * 解压实际落到 xxx(1)，而续解只按解压**前**的目录快照找包，
             * 结果第二层根本没跑，汇总却照样打印"一键处理完成：成功 N"。
             * 有这一行日志（以及上面那串逐条明细），用户（和我们）才有能对照的线索。
             */
            AppendLog("INFO", $"一键处理：第 {state.Round} 层没有发现可继续解压的内层包，到此结束。");
            return true;
        }

        if (state.Round >= host.RoundLimit)
        {
            state.HitRoundLimit = true;

            /*
             * 用户 2026-09-24 第 16 条追加：到顶**不能静默停下**。
             *
             * 把这一层发现的内层包**照样加进任务列表并勾好**（走与正常续解同一条路），
             * 只是这一批不再解它们：于是用户看到的列表里就摆着"还没解的那些包"，
             * 汇总与弹窗写明还剩几个，界面给「继续解」接着跑下一批 10 轮。
             *
             * 为什么不直接继续解完：硬上限就是不变量 8 的那道闸（见 RoundLimit / MaxRoundsCeiling 的说明）。
             */
            try
            {
                state.PendingContinuation = await host.AddInnerTasksAsync(roundState.InnerArchives);
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", $"一键处理：把剩下的内层包加进任务列表失败 —— {ex.Message}（它们在产物目录里，可以自己添加）");
            }

            AppendLog(
                "WARN",
                $"一键处理：第 {state.Round + 1} 层还有 {roundState.InnerArchives.Count} 个内层包，"
                + $"但已达到 {host.RoundLimit} 轮上限，本批先停在这里 —— "
                + $"{state.PendingContinuation.Count} 个内层包已加进列表并勾好，点「继续解」接着解（每次最多再解 {host.RoundLimit} 轮）。");

            /*
             * ===== 到顶这一支也必须取消已处理任务的勾选（2026-10-05 只读审计）=====
             *
             * 现场：正常续解那一支在加下一轮任务之前会做这一步，而**到顶这一支没有** ——
             * 于是用户点①页「继续解」时，上一批**最后一轮**已经解过的包仍处于勾选态，
             * 会被**再解一遍**：产物同名时定稿走 AutoRename 生成 "(1)" 垃圾副本
             * （一键档的冲突框被抑制，不走改名询问），若那一层的包已被"逐层回收"删掉，
             * 还会落一句「文件不存在」。
             *
             * 顺序安全：`processed` 里只有**已经解过**的任务，`pendingContinuation`
             * （刚 AddInnerTasksAsync 加进来的那些）不在其中 —— 它们照旧保持勾选。
             */
            host.DeselectProcessedTasks(state.Processed);

            return true;
        }

        List<ArchiveTask> nextRound;

        try
        {
            /*
             * 已经处理过的任务全部取消勾选。
             * 不这样做，下一轮会把它们**重新解压一遍**，产出 "(1)" 这样的垃圾副本 ——
             * 解压流程只认勾选状态，这正是它该有的样子（不偷偷处理没勾的）。
             *
             * 判据与做法收在 DeselectProcessedTasks 一处：到顶停下那一支走的是**同一个出口**
             * （2026-10-05 只读审计 —— 那一支过去漏了这一步，"继续解"会把最后一轮重解一遍）。
             */
            host.DeselectProcessedTasks(state.Processed);

            nextRound = await host.AddInnerTasksAsync(roundState.InnerArchives);

            /*
             * B 段点名的第二大步入口任务，跟内层包一起进下一轮。
             * ⛔ 必须显式勾上：`DeselectProcessedTasks(processed)` 刚把它取消勾选
             * （它上一轮被闸门跳过、算"处理过"），下一轮只跑勾上的。
             */
            foreach (ArchiveTask volumeEntryTask in roundState.VolumeEntryTasks)
            {
                if (!nextRound.Contains(volumeEntryTask))
                {
                    nextRound.Add(volumeEntryTask);
                }

                volumeEntryTask.IsSelected = true;
            }
        }
        catch (Exception ex)
        {
            AppendLog("ERROR", $"一键处理：把内层包加进任务列表失败，停止续解 —— {ex.Message}");
            return true;
        }

        if (nextRound.Count == 0)
        {
            // 内层包全都已经在任务列表里了（路径重复）：再跑一轮只会空转。
            AppendLog("WARN", $"一键处理：第 {state.Round + 1} 层的 {roundState.InnerArchives.Count} 个内层包没能加进任务列表，停止续解。");
            return true;
        }

        state.ContinuationLayers++;

        /*
         * ===== 轮判定「分卷 ⇒ 下一轮按第二大步只解一层」：已整段停用（用户 2026-10-24）=====
         *
         * 原来这里按 `roundSummary.VolumeEntries` + 内容身份 `VolumeMember` 给下一轮任务
         * `RememberVolumeStepTaskForRound`（任务级事实位 ⇒ 只解一层）。
         * 这些都不属于第一大步；重建第二大步时再决定要不要、以什么形式接回来。
         */

        /*
         * 说清"第二层解到哪去"：内层包**不另建目录**，
         * 它的产物与父任务归到同一个最终目录（用户诉求：一个源包 = 一个目录）。
         */
        string continuationTarget = roundState.InnerArchives
            .Select(candidate => candidate.ParentOutputDirectory)
            .FirstOrDefault(directory => !string.IsNullOrWhiteSpace(directory)) ?? string.Empty;

        AppendLog(
            "INFO",
            $"一键处理：第 {state.Round + 1} 层发现 {roundState.InnerArchives.Count} 个内层包，继续解" +
            (string.IsNullOrWhiteSpace(continuationTarget)
                ? "。"
                : $"（产物归入同一个输出目录：{continuationTarget}，不再另建文件夹）。"));

        state.Processed.AddRange(nextRound);
        state.SourcePaths.UnionWith(OneClickCoordinator.BuildSourcePathSet(nextRound));
        state.RoundTargets = nextRound;

        return false;
    }
}

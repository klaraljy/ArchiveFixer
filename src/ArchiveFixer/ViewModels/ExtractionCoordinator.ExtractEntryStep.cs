using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
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
using System.Windows;


namespace ArchiveFixer.ViewModels
{
    /// <summary>解压入口（手动/一键）与核心批流程（第 11 块，从 Password 大杂烩搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// 批量解压入口 —— **地基路径**（界面「只解压（不改后缀）」走的就是它）。
        ///
        /// <para>
        /// 源包处理（决策 D-9；用户 2026-09-22 的最新规则）：
        /// </para>
        /// <list type="bullet">
        /// <item><description>「一键处理」= 整理路径 → 走 <see cref="StartExtractForOneClickAsync"/>；</description></item>
        /// <item><description>本方法 = 地基路径（单层「只解压」）。</description></item>
        /// </list>
        /// <para>
        /// ⚠ <b>两条路径的源包规则现在是同一个</b>：都读 <see cref="AppSettings.SourceHandling"/>，
        /// 都在"内容物已定稿 + 输出校验通过 + 未取消 + 分卷组完整"之后把整组源包移入 <c>其余物</c>。
        /// 用户原话：「如果成功了你就直接将源包放在其余物里面」，并明确"地基路径也照这条走"
        /// （这推翻了更早那版"地基路径永远不动源包"的约定）。<c>KeepInPlace</c> 档仍是"永不搬"的出口。
        /// </para>
        /// <para>
        /// 两条路径真正的差别只剩一处（回写 <c>oneClickRun</c> 一路传到底）：**一键处理有续解链**，
        /// 所以"第一层只出过程物"时它把源包搬运留到链结束后补做（<see cref="CompleteRootSourcePackagesAfterChainAsync"/>）；
        /// 单层的「只解压」没有链可等，当场按"定稿 + 校验通过"处理。
        /// 定稿、校验、归集、工作区清理的行为两条路径完全一致。
        /// </para>
        /// </summary>
        /// <param name="extractIntoSourceFolder">
        /// 这一次按**「解压到当前文件夹」**办（用户 2026-09-27 新增的手动档；默认 false）。
        ///
        /// <para>
        /// 语义 = WinRAR 右键的同一句话：内容物**不建包名那一层**，直接落在源包所在的那个目录里
        /// （<c>111\222.rar</c> → <c>111\内容物</c>）。它只活在这一批里，⛔ 绝不写进设置；
        /// **一键处理 / 继续解永远不会传它**（用户红线：批量处理一律套包名那一层，不摊平）。
        /// 同名文件/文件夹按当前冲突档处理 —— 摊平会更容易撞名，这个代价用户 2026-09-27 明确接受。
        /// </para>
        /// </param>
        public async Task StartExtractAsync(
            bool extractIntoSourceFolder = false,
            IReadOnlyList<ArchiveTask>? targetTasks = null)
        {
            BeginWorkspaceRun();

            try
            {
                await StartExtractCoreAsync(
                    oneClickRun: false,
                    runOptions: null,
                    extractIntoSourceFolder: extractIntoSourceFolder,
                    targetTasks: targetTasks);
            }
            finally
            {
                CleanupRunWorkspaceShells();
            }
        }

        /// <summary>
        /// 「一键处理」的显式入口：与 <see cref="StartExtractAsync"/> 同一个本体，
        /// 差别只在"有续解链"这件事上（见 <see cref="StartExtractAsync"/> 的说明）。
        /// </summary>
        /// <param name="runOptions">
        /// 本次选项面板选出来的**运行期快照**（规格 §9；null = 没弹面板 / 无界面宿主，按设置走）。
        ///
        /// <para>
        /// 它只活在这一批里：进方法时记下、<c>finally</c> 里置空。落点经它翻译成
        /// <see cref="ExtractOptions"/> 上那三个既有字段（唯一实现仍是 <c>OutputPlacement</c>），
        /// 终端落法与源包处理则在收尾处优先取它的值 ——
        /// **不勾"存为默认"时设置文件一个字节都不会被写**，因为这条路径上没有任何写设置的代码。
        /// </para>
        /// </param>
        /// <param name="continuationRound">
        /// 这一次是**同一条续解链的后续轮次**（一键处理第 2 轮起）。
        ///
        /// <para>它只决定一件事：批首要不要清掉"缺卷留到最后再判"的那份名单（用户 2026-10-05 口径 2）。
        /// 第 1 轮没解出来的第 1 卷很可能在第 2 轮才出现 ⇒ 那份名单必须**跨轮活到链尾**；
        /// 而用户重新点一次就是新的一批 ⇒ 那时清空（旧结论不许留到新批次上）。
        /// ⛔ 不改任何解压行为、不改轮数（轮数只有一个出口 <c>OneClickCoordinator.RoundLimit</c>）。</para>
        /// </param>
        public Task StartExtractForOneClickAsync(OneClickRunOptions? runOptions = null, bool continuationRound = false) =>
            StartExtractCoreAsync(oneClickRun: true, runOptions, continueDeferredVolumeDeficits: continuationRound);

        private async Task StartExtractCoreAsync(
            bool oneClickRun,
            OneClickRunOptions? runOptions = null,
            bool extractIntoSourceFolder = false,
            bool continueDeferredVolumeDeficits = false,
            IReadOnlyList<ArchiveTask>? targetTasks = null)
        {
            if (_isExtracting)
            {
                AppendLog("WARN", "当前已经在批量解压中，忽略重复启动。");
                return;
            }

            var selectedTasks = targetTasks != null
                ? targetTasks.Where(Tasks.Contains).Distinct().ToList()
                : Tasks.Where(x => x.IsSelected).ToList();

            if (selectedTasks.Count == 0)
            {
                /*
                 * 一律只认勾选（用户 2026-09-24 第 12 条）：一个都没勾 → 只提示、什么都不做。
                 * 提示语来自 StatusText 的唯一来源，与清理类命令、一键处理逐字相同 ——
                 * 以前这里各写一套，用户看到三种说法，还以为是三个不同的问题。
                 */
                _dialogService.ShowWarning(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.NoCheckedTaskPromptFormat,
                    oneClickRun ? "一键处理" : "只解压",
                    Tasks.Count));

                AppendLog("WARN", StatusText.NoCheckedTaskLogFormat);

                return;
            }

            if (!BatchEntry.InitializeBatch(selectedTasks, oneClickRun, runOptions, extractIntoSourceFolder))
            {
                return;
            }


            if (!await BatchEntry.PrepareBatchRemindersAsync(selectedTasks, runOptions))
            {
                return;
            }


            BatchEntry.BeginBatch(selectedTasks, oneClickRun, continueDeferredVolumeDeficits);


            try
            {
                _runSettings = SettingsSnapshot.Capture(_vm.Settings).ToSettings();

                /*
                 * 源根先于任何解压登记：账本项/产出物后面都要挂到这条稳定身份上。
                 * 后续轮次会重复调用，登记是幂等的；⛔ 不在这里判组。
                 */
                RegisterSourceRoots(selectedTasks, runOptions);

                try
                {
                    _operationCts?.Dispose();
                }
                catch
                {
                }

                _operationCts = new CancellationTokenSource();

                _batchPurgedTaskCount = 0;
                _batchPurgedBytes = 0;
                _batchPurgedPermanently = false;
                _stopNoticeLogged = false;

                AppendLog("INFO", "开始批量解压");

                /*
                 * 手动密码出口（WinRAR 参考 §2 H 组 / §3 附注采纳项）。
                 *
                 * 位置：**动手之前**问一次。理由很实在 —— 密码本没命中、统一密码也不对时，
                 * 用户以前只能"改设置 → 重跑整批"，而重跑意味着已经解过的包再解一遍。
                 * 现在这一步先问，答了就当成本批所有任务的候选（排在空密码之后、密码本之前：
                 * 它是用户刚刚给出的信息，比密码本里的历史条目更"新"）。
                 *
                 * 三条纪律：
                 * ① **整批一次性**（_passwordFailureRegistry.ManualPasswordPrompted）—— 不做成每个包问一次，那正是 D-4 要避免的；
                 * ② 手动「只解压」这条路上**只对本次运行有效、绝不落盘**（不变量 5）——
                 *    只是一个字段，不进设置、不进密码列表、不进日志；
                 * ③ **无 UI 宿主不弹窗、不死等** —— 判定见 PromptForManualBatchPasswordAsync。
                 *
                 * ⚠ **一键档不再走这个框**（用户 2026-09-29 拍板）：一键处理的红线是"批中间零弹窗"，
                 * 而这个框恰好卡在开工前，等于给一键档开了第二个口子。一键档**没有手动密码入口**
                 * （当时那个输入框是整块撤掉的，不是搬去了别处），只写一条"要手动给就去「密码」页
                 * 一键导入"的日志 —— 见 LogOneClickPasswordHint。
                 *
                 * ⇒ 由此可以断定：一键档下 `_passwordFailureRegistry.ManualBatchPasswords` **恒为空**，所以每轮开头那次
                 * `ResetManualBatchPassword()`（StartExtractForOneClickAsync 的批首）清掉的是"空表"，
                 * 不会丢任何输入。
                 * ⚠ 以后若真要把输入框加回一键档，**必须同时改那一处清空**：一键处理一轮 = 一次
                 * `StartExtractForOneClickAsync`，不清就等于用户在某一轮填的密码到下一轮就没了
                 * （而"每轮都问"又违反零弹窗）。
                 */
                if (oneClickRun)
                {
                    LogOneClickPasswordHint(selectedTasks);
                }
                else
                {
                    await PromptForManualBatchPasswordAsync(selectedTasks);
                }

                /*
                 * 批首准备（工作区根 / 补跑归组 / 第二大步点名 / 落点解析器 / 空间不足档 / 并发上限）：
                 * 实现与逐条理由见 TryPrepareBatchForExtract —— 这一段原来是 120 行流水，抽出来只为读得动。
                 */
                if (!BatchEntry.TryPrepareBatchForExtract(selectedTasks, oneClickRun, out int maxParallel, out bool fullSpeed))
                {
                    return;
                }

                /*
                 * 源文件保护快照必须等批首工作区根定下来才建：旧调用点在 OneClickCoordinator
                 * 的批首、工作区解析之前，导致旧实现只能拿盘根兜底，在 C/E/H 根目录留下
                 * `.ArchiveFixerSrcProtect`。现在第一次进入本方法（不是续解轮）时建，
                 * 快照只进 `<目标目录>\.ArchiveFixer.work\source-protect`；跨卷源包跳过。
                 */
                if (oneClickRun && !continueDeferredVolumeDeficits)
                {
                    BeginKeepInPlaceSourceProtection(selectedTasks);
                }

                /*
                 * ===== 空间规划 + 空间门（用户 2026-09-22 需求第 1 / 2 条）=====
                 *
                 * 顺序：
                 * ① 按空间需求**从小到大**排序（用户点名的反例：一开始就去解 5G+6G，两个都跑不动）；
                 * ② 算出"按当前可用空间，最多能并行几个"，与用户选的档位一起写进日志；
                 * ③ 每个任务**启动之前**再判一次空间（不是只在计划时判一次）——
                 *    空间是随跑随变的，计划时的数字只能当建议（见 SpaceReservationLedger 的说明）。
                 *
                 * ⚠ 排序只影响**执行顺序**，不动任务列表本身：用户在列表里看到的顺序、
                 * 任务的 Index 都不变（改列表顺序会让"我刚才是第 3 个"这种对照失效）。
                 */
                int userParallel = Math.Max(1, maxParallel);
                ExtractionSchedulePlan plan = BuildSchedulePlan(selectedTasks, userParallel, _spaceTightPolicy.Enabled);

                if (_spaceTightPolicy.Enabled)
                {
                    _spaceLimit = ResolveSpaceTightParallelForBatch(plan);
                    maxParallel = Math.Min(_spaceLimit, userParallel);
                }

                _cleanupLedger.Clear();
                PrepareRestHandlingForBatch();

                foreach (string line in plan.DescribeLines())
                {
                    AppendLog("INFO", line);
                }

                LogRestHandlingForBatch();

                LogExtractionLimitsForBatch();

                /*
                 * 「多层 + 空间小 ⇒ 中途才报空间不足」那句提醒（用户 2026-09-29 第 2 条）：
                 * 必须在**动手之前**说，而且与确认框里那句用**同一份判据、同一段文案**
                 * （见 EvaluateMultiLayerSpaceRisk / StatusText.MultiLayerSpaceRiskFormat）。
                 */
                MultiLayerSpaceRiskReporter.LogMultiLayerSpaceRisk(plan);

                _spaceLedger = new SpaceReservationLedger(
                    ProbeAvailableSpace(ResolveSpaceProbePath(selectedTasks)),
                    ReserveSpaceBytes);

                AppendLog("INFO", "空间账面：" + _spaceLedger.Describe());

                /*
                 * 空间侦察：从这一刻起"时刻"看着这块盘（每 5 秒一针 + 每个任务开工 / 收尾各一针），
                 * 批末写一条空间曲线。位置在账面之后、第一个任务之前 —— 起点的数字就是"开工前还剩多少"。
                 */
                StartSpaceTrendMonitor(ResolveSpaceProbePath(selectedTasks));

                var runningTasks = new List<Task>();
                var runningTaskMap = new Dictionary<Task, ArchiveTask>();
                var startedTasks = new HashSet<ArchiveTask>();

                if (_spaceTightPolicy.Enabled)
                {
                    _spaceLimit = ResolveDynamicSpaceParallel(plan.Ordered, runningCount: 0);
                    maxParallel = Math.Min(_spaceLimit, userParallel);
                    AppendLog(
                        "INFO",
                        $"空间不足动态并发：空间上限 {_spaceLimit} 个，用户上限 {userParallel} 个 ⇒ 批首实际 {maxParallel} 个");
                }

                /*
                 * 第 45 条（用户 2026-09-26）：等待并发位的提示**一批只说一次** + 批末给个计数。
                 *
                 * 真机日志里那一行出现了 **74 次**（76 个任务）：
                 * `并发已满（1/1 个任务正在跑），「018.7z」在队列里等一个空位。想让它立刻开跑：
                 *   勾上主界面的「全速」；或点「停止后续」……`
                 * 用户三条意见全中：①**主界面根本没有「全速」**（它在②页 →「并发与空间」）；
                 * ②他不知道"是要暂停下来开还是直接开"；③一条 60 多字的提示刷 74 遍，量与"简洁"背道而驰。
                 */
                int throttledTasks = 0;
                bool throttleExplained = false;

                /*
                 * ===== 空间门拒了 ≠ 这个任务没做成（用户 2026-10-02 真机，14 任务里 6 个白没做）=====
                 *
                 * 现场：14 个任务、并发档 8、盘上 35.86 GiB 可用。前 8 个一开跑，账本里"已经在跑的任务
                 * 预留"就顶到 35.11 GiB（那 8 个各自的全额需求之和本来就接近整块盘）—— 于是第 9 个包
                 * 哪怕自己只要新写 851.45 MiB，也被判「还差 9.77 MiB」**当场记成失败**。
                 * 那 8 个要跑 30 多分钟，其间陆续跑完、把预留一笔笔放出来（最大的那个一个人就放开 15.71 GiB），
                 * 可被拒的那 6 个**再也没人回头看它们**：批末报「失败 6」、诊断「磁盘空间不足：6 个」。
                 *
                 * 判据上的错在哪：**"此刻空间不够"是一个会变的瞬时事实，不是一个终态**。
                 * 所以拒下的任务进这张名单**等空间**，等有任务跑完（预留释放）再按原顺序（小→大）重试；
                 * 一直等到没人跑、空间不会再变多为止，剩下那些才如实报「空间不足」。
                 *
                 * ⛔ 两条不变：① 排队**不占并发位**（不会因为排队把盘跑满）；
                 * ② 真正开跑前仍然要过账本（`TryReserve`），**放行判据一个字没放宽**。
                 */
                var spaceWaiting = new List<(ArchiveTask Task, ScheduledExtractionItem Item, SpaceGateDecision Gate)>();
                var scheduler = new SlotScheduler<ScheduledWorkUnit>();
                scheduler.Reset(
                    plan.Ordered.Select(item => ScheduledWorkUnit.FromPlanItem(item)),
                    maxParallel,
                    work => work.EstimatedSeconds);
                var slotWorkByTask = new Dictionary<ArchiveTask, ScheduledWorkUnit>();
                while (scheduler.PendingCount > 0 || scheduler.ActiveCount > 0)
                {
                    if (IsStopping || _operationCts.IsCancellationRequested)
                    {
                        LogStopRequestOnce();
                        break;
                    }

                    // 每个任务各自的"已经在队列里等过"标记与开始等的时间（只有等得久才补一句，见下）。
                    bool queuedLogged = false;
                    DateTime queuedSince = ArchiveFixer.Helpers.Clock.Now;


                    // 等待有空闲并发位；期间若用户点了“停止后续”，不再为后面的任务等位。
                    while (runningTasks.Count >= maxParallel)
                    {
                        /*
                         * 「等待状态必须可见」（WinRAR 参考 §2 D 组采纳项）。
                         *
                         * 没有这一行时，被节流挡住的任务在界面上**完全看不出在等** ——
                         * 用户看到的是"点了开始，一半任务纹丝不动"，然后怀疑程序卡死
                         * （这正是本机历史上被投诉过的那类现象）。
                         */
                        throttledTasks++;

                        if (!queuedLogged)
                        {
                            queuedLogged = true;
                            queuedSince = ArchiveFixer.Helpers.Clock.Now;
                        }

                        if (!throttleExplained)
                        {
                            throttleExplained = true;
                            AppendLog(
                                "INFO",
                                $"并发已满（{runningTasks.Count}/{maxParallel} 个任务正在跑），后面的任务在队列里等空位。"
                                + "并发数按你选择的档位执行；"
                                + "也可以点①页「停止后续」不再启动后面的任务。"
                                + "（这条只在第一次排队时说一遍，本批还有几个在等，跑完会在结尾汇总。）");
                        }

                        Task finished = await Task.WhenAny(runningTasks);
                        runningTasks.Remove(finished);
                        await finished;

                        if (runningTaskMap.Remove(finished, out ArchiveTask? finishedTask)
                            && slotWorkByTask.Remove(finishedTask, out ScheduledWorkUnit? completedSlotWork))
                        {
                            scheduler.MarkCompleted(completedSlotWork);
                        }

                        if (_spaceTightPolicy.Enabled && !IsStopping)
                        {
                            var pending = plan.Ordered
                                .Where(candidate => !startedTasks.Contains(candidate.Task))
                                .ToList();

                            _spaceLimit = ResolveDynamicSpaceParallel(pending, runningTasks.Count);
                            int combined = Math.Min(_spaceLimit, userParallel);

                            if (combined != maxParallel)
                            {
                                AppendLog(
                                    "INFO",
                                    $"空间不足动态并发：空间上限 {_spaceLimit} 个，用户上限 {userParallel} 个 ⇒ 实际 {combined} 个（正在跑 {runningTasks.Count} 个）");
                                maxParallel = combined;
                                scheduler.SetCapacity(maxParallel);
                            }
                        }

                        if (IsStopping || _operationCts.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                    if (!scheduler.TryTakeNext(out ScheduledWorkUnit? scheduledWork))
                    {
                        break;
                    }
                    ScheduledExtractionItem item = scheduledWork!.PayloadAs<ScheduledExtractionItem>();
                    ArchiveTask task = item.Task;

                    /*
                     * 关键修复（实测复现）：上面的 break 只跳出了"等并发位"的 while，
                     * 落到这里仍会把当前任务 Add 进去 —— 表现为"点了停止后续，另一个包照样跑成功了"，
                     * 违反不变量 9（停止后续只阻止**启动后续**，绝不能再启动新任务）。
                     * 所以在真正启动之前必须再查一次。
                     */
                    if (IsStopping || _operationCts.IsCancellationRequested)
                    {
                        LogStopRequestOnce();
                        break;
                    }

                    if (queuedLogged)
                    {
                        /*
                         * 等过空位的任务：只在**等得久**的时候补一句（用户 2026-09-26 第 45 条：
                         * 74 条"并发已满 / 等到空位"同义反复就是噪声）。30 秒以内的等待不值得单独占一行，
                         * 批末那条汇总会把"有几个等过"说清。
                         */
                        TimeSpan waited = ArchiveFixer.Helpers.Clock.Now - queuedSince;

                        if (waited.TotalSeconds >= ThrottleNoticeThresholdSeconds)
                        {
                            AppendLog("INFO", $"「{task.FileName}」等了 {waited.TotalSeconds:F0} 秒才排上空位（本批同时最多跑 {maxParallel} 个）。");
                        }
                    }

                    /*
                     * 空间门（**启动之前**判，用户 2026-09-22 需求第 1 条）。
                     *
                     * 判据里含"已经在跑的任务预留了多少"（账本），所以 5G+6G 这种组合里
                     * 第二个大包会在启动前就被拦下，而不是等它写到一半才报磁盘满。
                     * 拦下时**跳过它、继续看后面的**（后面的包可能更小、正好塞得下）——
                     * 全部排完再一次性报告哪些被跳过、各需要多少。
                     *
                     * ⚠ 数字是 `RequiredBytes` = 内容物 + 过程物（`TaskSpaceEstimate.FreeSpaceDemandBytes`），
                     * **不是峰值**：源包已经在盘上、不在可用空间里，拿峰值比就是把源包算两遍
                     * （2026-09-29 真机：17.7 GiB 的三卷包在 33.45 GiB 可用的盘上被判"整盘都放不下"）。
                     */
                    ScheduledTaskRuntime runtime = GetOrCreateRuntime(task);

                    SpaceGateDecision gate = _spaceLedger.TryReserve(
                        item.RequiredBytes,
                        item.Estimate.ReclaimableBytes);

                    if (!gate.Allowed)
                    {
                        /*
                         * 空间不足模式：放不下就**当场**记空间不足并退出队列，不反复等待重试。
                         * 排序已经是从大到小；这个包此刻放不下，后面先跑小包。
                         * 源包一个字节都不动；用户清出空间后可以手动重试。
                         */
                        if (_spaceTightPolicy.Enabled)
                        {
                            MarkSpaceBlocked(task, item, gate);
                            scheduler.MarkCompleted(scheduledWork);
                            continue;
                        }

                        /*
                         * 不记失败、不改任务状态 —— 先挂进"等空间"名单（见上面那段说明）。
                         * 只写一行 INFO 说清"它在等什么"，用户才不会以为这个包已经被判死刑了。
                         */
                        spaceWaiting.Add((task, item, gate));
                        scheduler.MarkCompleted(scheduledWork);

                        AppendLog(
                            "INFO",
                            $"{task.LogName}：现在空间不够（{gate.Reason}）—— 先排队等空间，"
                            + "等已经在跑的任务收尾把预留放出来就自动补跑（不是失败，也不会漏掉它）。");

                        /*
                         * 「中途撞上空间不足」那一次纯提示**照旧当场发**（用户 2026-09-29 第 2 条：
                         * 一键档弹一次、后面被拦下的不再弹；手动档只写日志）。
                         *
                         * 为什么不挪到批末"确实排不上"那一刻再发：那一刻整批已经结束了，
                         * 用户坐在屏幕前等着的时候**什么都不知道** —— 而这条提示的全部意义就是
                         * "现在告诉你一声，别干等"。排队不等于没空间不足，这一条该说还得说。
                         */
                        NotifySpaceBlockedOnce(task, item.RequiredBytes, gate.AvailableBytes, gate.ShortfallBytes);

                        continue;
                    }

                    runtime.ReservedBytes = gate.ProbeFailed ? 0L : item.RequiredBytes;
                    runtime.AvailableBeforeStart = _spaceLedger.AvailableBytes;
                    startedTasks.Add(task);
                    Task runningTask = TaskRunner.RunScheduledTaskAsync(task, oneClickRun, runtime);
                    runningTaskMap[runningTask] = task;
                    runningTasks.Add(runningTask);
                    slotWorkByTask[task] = scheduledWork;
                }

                await BatchEntry.RunWaitAndVolumeRetryLoopAsync(selectedTasks, runningTasks, spaceWaiting, maxParallel, oneClickRun);

                /*
                 * **目标 ④ 的范围记录**（用户 2026-10-24）：手动「只解压」没有轮循环，本批**不自动接手**
                 * 分卷组（交接单照常登记、文件原地不动）。这里如实写一行，别让用户以为第二大步也跑了。
                 */
                if (!oneClickRun && VolumeHandoffEntries.Count > 0)
                {
                    AppendLog(
                        "WARN",
                        $"[第二大步] 手动「只解压」不自动接手分卷组：本批交接 {VolumeHandoffEntries.Count} 份"
                        + "（文件原地没动）。要解这几组请用「一键处理」；这条路径不做建组 / 搬片。");
                }

                /*
                 * 等到最后还是排不上的：**这时候才**如实记「空间不足」（状态 / 错误信息 / 日志 / 批末清单
                 * 走同一个落点 `MarkSpaceBlocked`）。⛔ 这些任务从头到尾一个字节都没动。
                 */
                foreach ((ArchiveTask waitingTask, ScheduledExtractionItem waitingItem, SpaceGateDecision waitingGate) in spaceWaiting)
                {
                    MarkSpaceBlocked(waitingTask, waitingItem, waitingGate);
                }

                AppendLog("INFO", "批量解压完成");

                DiagnosticLog.WriteBatchEnd(
                    _vm.Logger,
                    oneClickRun ? "一键处理" : "只解压",
                    selectedTasks,
                    "批量解压完成（逐任务终态见 tasks）");

                /*
                 * **批末把"改过名的行"那三列重算一遍**（检测格式 / 建议后缀 / 后缀状态）——
                 * 用户 2026-10-07 点名的三列过期：扫描那一刻写的是**旧名字**下的结论
                 * （真机 `111(2)_.zi删除p` 写着「后缀不匹配」，而我们随后把它改回了 `111(2)_.zip`）。
                 * ⛔ 只重算这几个纯事实字段、走扫描那边唯一的出口；⛔ 不碰 Status / 进度 / 落点 / 归组。
                 */
                await _vm.RefreshRenamedRowFactsAsync().ConfigureAwait(true);

                /*
                 * 并发排队的批末汇总（用户 2026-09-26 第 45 条）：过程里只在第一次排队时说一句，
                 * 这里把"到底有几个等过"一次说清 —— 比 74 行同义反复有用得多。
                 */
                if (throttledTasks > 0)
                {
                    AppendLog(
                        "INFO",
                        $"并发排队汇总：本批 {throttledTasks} 个任务等过空位（同时最多跑 {maxParallel} 个）。"
                        + "并发数按你选择的档位执行。");
                }

                /*
                 * **把①页每一行实际显示的那几格写进日志**（用户 2026-10-07：「你日志里面能否看清楚列表里面
                 * 显示什么内容」）。显示层（列绑定 / 行刷新）本来不进日志，于是"列表显示对不对"只能靠截图；
                 * 这一行把名称 / 大小 / 当前后缀 / 完整路径 / 状态 / 错误信息**按显示口径**原样打出来，
                 * 用户导出的日志就能与界面逐格对齐（⛔ 取的是同一批计算属性，不另算一套）。
                 *
                 * ⚠ 2026-10-08（EEEE 真机）：快照**必须取在"事实定稿之后"**。老写法只有这一处，
                 * 而链尾补判（`FinalizeDeferredVolumeDeficits`，由一键档在整批跑完之后调）还会改写几行的
                 * 状态 ⇒ 导出的日志里最后一份快照停在批首的中性档（真机那两行写着「等待解压」，
                 * 用户据此判定"显示不对"）。⇒ 收成唯一出口 `AppendTaskRowSnapshot`，两处都调它。
                 */
                AppendTaskRowSnapshot();


                AppendBatchSummary(selectedTasks);

                /*
                 * ⛔ **批末重试"过路层其余物按档删"**（真机 2026-10-06 20:07 的 48.35 MB）：
                 * 收场那一刻半套分卷闸门拦得对（同组的片还在成品树里），可那一组随后就解完了
                 * ⇒ 到这里障碍已经消失；不补这一次，那份其余物就永远没人再看一眼。
                 */
                RetryPendingPassThroughRestPurges();

                // 其余物处理的批末汇总（第 44 条：逐任务一行 + 这里一条，替掉以前几十行长文案）。
                (int purgedTasks, long purgedBytes, bool purgedPermanently) = ReadBatchPurge();

                if (purgedTasks > 0)
                {
                    AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.RestPurgedBatchSummaryFormat,
                            purgedTasks,
                            WorkspaceCleanupService.FormatSize(purgedBytes))
                        + (purgedPermanently
                            ? "（彻底删除，不进回收站、不可恢复）"
                            : "（已进回收站，可还原）"));
                }

                /*
                 * **如实报告**被空间门跳过的任务（用户 2026-09-22 需求第 2 条：
                 * "全部排不下时如实报告哪些包因为空间不足被跳过、各自需要多少，不要静默跳过"）。
                 *
                 * 每一条都带具体数字与建议动作 —— 这一行往往是用户唯一能拿到的行动线索。
                 */
                ReportSpaceBlockedTasks();

                /*
                 * 询问次数写进日志：决策 D-4 要求"本批只问一次"，这一行就是它的证据 ——
                 * 用户与排障都能看到"这一批到底打断了几次"（选了「全部 X」之后不该再涨）。
                 */
                int conflictPrompts = _conflictStep.PromptCount;


                if (conflictPrompts > 0)
                {
                    AppendLog(
                        "INFO",
                        $"本批同名冲突询问共 {conflictPrompts} 次（选了「全部 X」之后不再询问）。");
                }

                /*
                 * 密码错误**合并成一次提示**（P0）。
                 * 逐个任务弹模态框时，50 个错包就是 50 次阻塞点击 —— 批量处理根本跑不动。
                 * 放在批次结束后：此时才谈得上"本批 N 个包"，也不会挡住正在跑的任务。
                 *
                 * ⚠ 一键处理期间这条**只写日志、不弹框**（用户 2026-09-27：一键解压不许弹窗，
                 * 用户走开时没人点它，反而把"批已跑完"这件事卡在最后一个模态框上）。
                 * 日志那一行照旧写全（含"再点一次可以手动输密码"的出路），失败清单也能导出。
                 */
                await ShowPasswordFailuresSummaryAsync(showDialog: !oneClickRun);
            }
            finally
            {
                _runSettings = null;

                try
                {
                    _operationCts?.Dispose();
                }
                catch
                {
                }

                _operationCts = null;

                _isExtracting = false;
                IsBusy = false;
                IsStopping = false;

                // 本次选项**只活这一批**：批结束就丢掉，下一批（哪怕是同一次运行里的下一轮续解）
                // 由调用方重新给一份 —— 这样"覆盖"永远不会悄悄延续到别的批次上。
                _runOptions = null;

                /*
                 * 「解压到当前文件夹」与"本次选项"同一个寿命，而且**必须**在批尾清掉：
                 * 它是"这次点击"的语义，留着就会让下一次**一键处理**也悄悄摊平 ——
                 * 那正是用户 2026-09-27 划的红线（批量绝不摊平）。
                 */
                _extractIntoSourceFolderThisRun = false;

                /*
                 * 空间规划与本批记账同样"只活这一批"：
                 * 上一批的账本必须丢掉（下一批的可用空间与任务集都变了）。
                 */
                _spaceLedger = null;

                /*
                 * ⚠ 这里**刻意不**把 `RestHandlingThisBatch` 落回 Keep（2026-09-25 第 32 条被测试逮住）。
                 *
                 * 一键处理的「删除操作」是在**整条续解链跑完之后**做的，而那个链尾钩子
                 * （`CompleteRootSourcePackagesAfterChainAsync`，由 `OneClickCoordinator` 在本次解压批次
                 * 返回之后调用）读的就是这个字段 —— 在这里清掉，等于把用户选的「彻底删除 / 移入回收站」
                 * 静默降级成「不动其余物」：真机表现就是"我明明选了彻底删除，其余物还在"。
                 *
                 * 下一批开工时 `PrepareRestHandlingForBatch()` 会重新按当时的设置写一次，
                 * 所以留着的值只服务于"这一批自己的链尾"，不会影响任何后续批次。
                 */

                /*
                 * 「空间不足」模式**与上面那个字段相反：批尾必须清掉**。
                 *
                 * 理由：它读的是①页那个运行期勾（<see cref="MainViewModel.SpaceTightMode"/>），
                 * 而用户随时可能把它关掉 —— 留着 true 会让**批尾之后**的代码（链尾补搬、
                 * 单包重试、界面上的空间建议）继续按"删源包"那一套思考。
                 * 下一批开工时会重新 `= _vm.SpaceTightMode`，所以清掉不会丢任何东西。
                 */
                _spaceTightPolicy = SpaceTightPolicy.Normal;

                /*
                 * 「本批是不是一键档」同样只活这一批（它只服务于"中途空间不足要不要弹那一次纯提示"）：
                 * 留着 true 会让**批尾之后**的单包路径（重试、链尾、清理）也以为自己在一键批里。
                 * 下一次开工时重新按参数钉死。
                 */
                _oneClickThisBatch = false;

                // 空间侦察：停循环 + 把这一批的空间曲线写进日志（用户 2026-09-27 要求"时刻侦察空间变化"）。
                StopSpaceTrendMonitor();

                /*
                 * 顺手把**空的工作区壳**收掉（用户 2026-09-27：真机跑完 `…\.ArchiveFixer.work\recursive`
                 * 会留一个空壳在那儿）。
                 *
                 * 为什么要在这里做：任务是各清各的（每单自己的目录、递归逐层），但 `recursive`
                 * 这种"父壳"没人负责 —— 文件都没了，壳还在，用户看着就是残留。
                 *
                 * 三条安全边界（⛔ 一条都不许松）：
                 * ① **根下面只要还有一个文件就一个字节都不动**（宁可留着壳，也不许误删东西）；
                 * ② 只删工作区根**直接子目录**，绝不递归着往外走（不会碰到源包 / 成品目录）；
                 * ③ 全部包在 try/catch 里 —— 收尾的顺手活，失败只写日志，不许影响批结论。
                 */
                RemoveEmptyWorkspaceShells();

                /*
                 * 再收一层：**我们自己造出来的那个目标目录壳**（用户 2026-09-30 的新收尾）。
                 *
                 * 顺序不能反：先收 `<目标目录>\.ArchiveFixer.work`（上一步），这一层才可能变空。
                 * 判据两条同时成立才删 —— ① 它确实是这一次建出来的（批首记的事实）；
                 * ② 现在一个条目都没有（现枚举）。少了任何一条都会去碰用户自己的目录。
                 */
                RemoveEmptyTargetShellIfWeCreatedIt();

                // 批尾清掉这条批内状态（与 _spaceTightPolicy.Enabled / _oneClickThisBatch 同一口径）。
                BatchCreatedTargetDirectory = string.Empty;

                UpdateSummary();
            }
        }



        private ExtractionBatchEntry? _batchEntry;
        private ExtractionBatchEntry BatchEntry => _batchEntry ??= new ExtractionBatchEntry(this);

    }
}



using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 批解压入口的批首准备：引擎检查/运行期选项/批前提醒、批首状态清零、工作区与第二步点名、等待+补跑环；
/// 从 `ExtractionCoordinator.ExtractEntryStep.cs` 搬出的真实现，判据/文案/终态沿用原实现。
/// </summary>
internal sealed class ExtractionBatchEntry
{
    private readonly ExtractionCoordinator _host;

    internal ExtractionBatchEntry(ExtractionCoordinator host)
    {
        _host = host;
    }

    internal bool InitializeBatch(
        IReadOnlyList<ArchiveTask> selectedTasks,
        bool oneClickRun,
        OneClickRunOptions? runOptions,
        bool extractIntoSourceFolder)
    {
        if (!_host._archiveEngine.IsAvailable)
        {
            /*
             * 提示由 ToolLocator 现算（体检报告 §4 第 1 条）：
             * 这条判定的口径是"任一引擎可用"，而默认优先级是 WinRAR(UnRAR) → 7-Zip ——
             * 写死 7z 路径会把"其实只缺 UnRAR"的用户引去修一个没问题的目录。
             * 两条期望路径与当前优先级顺序都由 ToolLocator 给出（外部工具路径只有它一个来源）。
             */
            _host._dialogService.ShowError(ToolLocator.Default.DescribeNoEngineAvailable());
            _host.AppendLog("ERROR", StatusText.SevenZipMissing, DiagnosticCodes.EngineFailed, "ToolLocator");
            _host.AppendLog("ERROR", ToolLocator.Default.DescribeAvailability());
            return false;
        }

        _host._runOptions = runOptions;

        /*
         * 诊断日志 v2：批次开始就把“本批有谁、每个文件多大/什么身份/内容卡是什么、
         * 设置与工具是什么”写成一份独立可读的记录。用户以后只发日志时，不再依赖原包。
         */
        DiagnosticLog.WriteBatchStart(
            _host._vm.Logger,
            oneClickRun ? "一键处理" : "只解压",
            oneClickRun ? "一键处理" : "手动只解压",
            selectedTasks,
            _host.Settings,
            ToolLocator.Default.DescribeAvailability());

        /*
         * 手动档「解压到当前文件夹」：只认**手动那一条入口**传来的值。
         * 一键处理那条入口压根没有这个参数（永远 false）—— 这里再挡一道，防止以后有人顺手把它接过去：
         * 批量摊平会把几十个包的内容物混进同一层，是用户明令禁止的。
         */
        _host._extractIntoSourceFolderThisRun = extractIntoSourceFolder && !oneClickRun;

        if (extractIntoSourceFolder && oneClickRun)
        {
            _host.AppendLog("WARN", "一键处理不接受「解压到当前文件夹」（批量一律套包名那一层）—— 本次按普通一键处理执行。");
        }

        if (_host._extractIntoSourceFolderThisRun)
        {
            _host.AppendLog("INFO", StatusText.ExtractIntoSourceFolderReminderLog);
        }

        return true;
    }

    internal async Task<bool> PrepareBatchRemindersAsync(
        IReadOnlyList<ArchiveTask> selectedTasks,
        OneClickRunOptions? runOptions)
    {
        if (runOptions != null)
        {
            /*
             * 本次选项进日志（规格 §9.2 硬要求⑥）：用户事后要能回答"这次为什么解到这里"。
             * 逐任务那一行在 ExtractSingleTaskAsync 里写（带实际落点），这一行是本批的总纲。
             */
            _host.AppendLog("INFO", $"本次一键处理按「本次选项」执行：{runOptions.Describe()}");

            if (!runOptions.IsPlacementValid)
            {
                _host.AppendLog(
                    "WARN",
                    "本次选项里选了「指定位置」但没填路径 —— 为了避免它被解释成「未指定位置」那一档，" +
                    "本次落点回落设置里的值（其余两项照常生效）。");
            }
        }

        return await _host.BatchReminders.ConfirmBatchRemindersAsync(selectedTasks);
    }

    internal void BeginBatch(
        IReadOnlyList<ArchiveTask> selectedTasks,
        bool oneClickRun,
        bool continueDeferredVolumeDeficits)
    {
        _host.IsBusy = true;
        _host.IsStopping = false;
        _host._isExtracting = true;
        _host.PieceAdoption.RememberUserSourcePaths(selectedTasks);

        // 本批的密码失败登记从零开始：上一批的残留不能让这一批多弹一次提示。
        _host.ClearPasswordFailures();

        /*
         * "复用本批已成功的密码"这一档**只在本批内生效**（用户 2026-10-04：
         * "不同次文件一律严格按我列表顺序"）。
         *
         * ⚠ 2026-10-16 订正边界（用户当面澄清逐字：「**同一次就是指同一次导入的**」）：
         * 一键处理**一条导入会跑好几轮**（`OneClickCoordinator` 的 `round` 循环），
         * 轮与轮之间属于**同一次导入** ⇒ ⛔ 后续轮次不许清。
         * 原来这一句是**无条件**的，而 `OneClickCoordinator` 每轮都会调进来
         * （`:1067` 逐字 `StartExtractForOneClickAsync(runOptions, continuationRound: round > 1)`）
         * ⇒ 第 1 轮学到的密码在第 2 轮被忘掉、后面每一个包又从头把整张表试一遍。
         * **实测（红检态）**：把这一句改回无条件 ⇒
         * `PasswordReuseTests.一键处理的后续轮次不清复用清单_同一次导入跨轮照样复用` 当场红，
         * 逐字 `Assert.Single() Failure: The collection contained 6 items` +
         * `Collection: ["", "<示例密码1>", "<示例密码2>", "<示例密码3>", "<示例密码4>", ···]`。
         *
         * 判据与同一方法里的"缺卷名单"**同一个事实位**（`continueDeferredVolumeDeficits`）：
         * 后续轮次保留；**用户重新点一次 = 新的一次导入** ⇒ 那时清空（旧批次的结论不许活到新批次上）。
         */
        if (!continueDeferredVolumeDeficits)
        {
            _host._passwordService.BeginBatch();
        }

        // 同名冲突的记账同样从零开始：上一批答过的「全部覆盖」绝不许延续到这一批。
        _host.ResetBatchConflictState();

        /*
         * 一键档的"零弹窗"抑制放在这里 —— **必须在 _host.ResetBatchConflictState 之后**：
         * 那个方法会把 _conflictPromptUnavailable 置回 false，先抑制后清零等于没抑制
         * （2026-09-29 复核逮到：设置里选「询问」时一键档批中间仍会弹聚合询问框）。
         */
        if (oneClickRun)
        {
            _host.SuppressDecisionPromptsForOneClickRun();
        }

        // 「完成后打开输出目录」整批只开一次；上一批开过不算这一批的（见 _outputFolderOpenedThisBatch）。
        _host.BatchReminders.ResetBatchOutputFolderState();

        // 手动密码只对**本次运行**有效（不变量 5：绝不落盘）：批开始时重新问一次、重新记一次。
        _host.ResetManualBatchPassword();

        // 空间规划与本批记账清零（见 finally 里的说明：清在**批首**，自测才读得到这一批的证据）。
        _host.Schedule.ResetSpacePlanningState();

        /*
         * 「缺卷留到最后再判」那份名单的清空点（用户 2026-10-05 口径 2）。
         *
         * ⛔ 一键处理的**后续轮次不清**：第 1 轮没解出来的第 1 卷很可能第 2 轮才出现，
         * 清了就等于把"留到最后"变成"留到本轮末尾"（真机那条 `.002` 正是跨轮才补上的形状）。
         * 用户重新点一次 = 新的一批 ⇒ 清空（旧批次的缺口结论不许活到新批次上）。
         */
        if (!continueDeferredVolumeDeficits)
        {
            _host._volumeLedgerState.VolumeDeficitDeferred.Clear();
            _host.VolumeHandoff.TriedVolumeGroupSignatures.Clear();
            _host.VolumeHandoff.GatheredUserSourcePieces.Clear();
            _host._volumeLedgerState.DeferredVolumeGroupMember.Clear();
            _host._volumeLedgerState.ByteBudgetLinkedSources.Clear();
            _host._volumeLedgerState.ScoredSlotAttempts.Clear();
            _host._volumeLedgerState.ScoredSlotLinks.Clear();
            _host._volumeLedgerState.ScoredSlotAttempts.Clear();
            _host._volumeLedgerState.ScoredSlotLinks.Clear();
            _host._volumeLedgerState.ConsumedVolumeSources.Clear();
            _host._volumeLedgerState.GroupConsumerByName.Clear();
            _host._volumeLedgerState.VolumeGroupUnits.Clear();
            _host.PieceAdoption.ClearGroupPieceProducers();
            _host._volumeLedgerState.ProducerSourcesCollected.Clear();
            _host._volumeLedgerState.AdoptedLandedPieces.Clear();
            _host._volumeLedgerState.GroupLayerByBaseName.Clear();
            _host._volumeLedgerState.StandardNameFastPathTasks.Clear();
            _host._volumeLedgerState.StandardNameFastPathFallbackPending.Clear();
            _host._volumeLedgerState.VolumeGroupEntriesThisBatch.Clear();
            _host._volumeLedgerState.VolumeGroupEntriesThisBatch.Clear();
            _host._volumeLedgerState.VolumeDeficitFinalPass.Clear();

            /*
             * 只留交接账（第一大步唯一允许的分卷动作 = **登记**）；
             * `_volumeLedgerState.VolumeStepTasks`（第二大步的任务位）已随第二大步停用，
             * 不再在第一大步批首清理。
             */
            _host.VolumeHandoff.Ledger.ClearVolumeHandoffLedger();
            _host.ResetVolumeGroupService();
        }
    }

    /// <summary>
    /// 批首准备：补跑归组 → 定工作区根 → 点名第二大步 → 挂落点解析器 → 钉空间不足档与并发上限。
    /// 返回 false = 这一批停下（工作区没定下来，已经弹过错误、一个字节都没写）。
    /// </summary>
    internal bool TryPrepareBatchForExtract(
        IReadOnlyList<ArchiveTask> selectedTasks,
        bool oneClickRun,
        out int maxParallel,
        out bool fullSpeed)
    {
        maxParallel = 1;
        fullSpeed = false;

        /*
         * ===== 工作区根：本批的中间产物放哪（用户 2026-09-30：落在这一单的目标目录里）=====
         *
         * 位置刻意在**空间规划之前**：空间账面要按"工作区与成品是不是同一块卷"给口径，
         * 而目标目录正是定工作区根时算出来的（两处必须看同一个事实，不能各算一遍）。
         *
         * ⛔ 定不下来（没有任何任务算得出目标目录）→ **整批停手并报错**：
         * 老实现这里会悄悄回落到程序目录（可能就是 C 盘），用户 2026-09-30 点名否掉了那个方向。
         * return 在 try 里面，所以 finally 照常收尾（_host._isExtracting / _host.IsBusy 都会被复位）。
         */
        /*
         * ⚠ 2026-10-09（D 结案 + 用户「回上一步解决错误」）：**定工作区根之前**先给这一批每一单
         * 补跑一次"按它所在目录里的真实文件归组"。
         *
         * 为什么必须在这一刻（出处 `ViewModels/OneClickCoordinator.cs:1539-1564` 的注释）：
         * 续解层的内层任务是走 `AddPathsAsync(..., suppressAutoScan: true)` 加进来的，
         * **那条路不跑扫描期的分卷归组** ⇒ 它们的 `IsVolumeGroup` 一直是 false，
         * 而 `_host.ApplyBatchWorkspaceRoot` 里那道"跳过入口包还没解出来的分卷组单"的守卫
         * （`IsUnresolvedVolumeGroupMember`，它第一句就是 `if (!task.IsVolumeGroup) return false;`）
         * 因此对它们一律放行 ⇒ 真机 EEEE 的整批工作区根被那一单的**占位落点**
         * （`…\EEEE\111(4)\111`）挑走。
         *
         * ⇒ 这就是"上游没做完的事，别在下游补"：把归组补在**它该在的位置**（定锚点之前），
         * 而不是放宽那道守卫的判据。唯一出口仍是 `ResolveVolumeGroupFromDirectory`
         * （它自己再转调 `VolumeGroupDetector.Group` + `VolumeGroupingService.ApplyGroupInfo`，
         * 与扫描期、手动档完全同一条路）；归不上组 ⇒ 什么都不动（与以前一样）。
         */
        WorkspaceRootResolution workspaceResolution = _host.ApplyBatchWorkspaceRoot(selectedTasks);

        /*
         * ===== 分卷批首归组 / 点名：已整段搬到 `SecondStepResidue`（用户 2026-10-24 拍板）=====
         *
         * 原来这里先 `ResolveVolumeGroupFromDirectory`（按目录归组）再
         * `MarkVolumeStepEntriesForBatch`（组分析 + 点名第二大步入口 + 搬/链接片）。
         * 第一大步不再做任何分卷操作；实现仍在
         * `src/ArchiveFixer/SecondStepResidue/ExtractionCoordinator.BatchVolumeEntryStep.cs`，
         * 重建第二大步时去那儿挑可用的。
         */

        if (!workspaceResolution.Resolved)
        {
            _host._dialogService.ShowError(workspaceResolution.Reason);
            _host.AppendLog("ERROR", "这一批已经停下：工作区位置没定下来，一个字节都没写、源包一个都没碰。");
            return false;
        }

        /*
         * ===== 批级整组改名：把"名字被改坏的分卷组"改回标准名（在**任何任务开工之前**）=====
         *
         * 旧位置是"每个任务开工前各改各的"（2026-09-28 起，见 NormalizeDisguisedVolumeNamesAsync）。
         * 那个位置在并发下不成立：一个包的两卷是**两个任务**，谁先开工不定 ——
         * 先开工的那一单改名成功、把两卷都改成标准名，而另一单可能在同一瞬间已经按**旧名字**
         * 判过归组（名字太烂，认不出组）并接着往下跑，于是它拿着旧名一路走到「分卷缺失」，
         * 还把整组的链尾处理一起拦住（用户 2026-10-01 真机：三棵一模一样的产物树 +
         * `111(1)` / `333-Rar4(1)`，源包一个都没删）。
         *
         * 挪到批首之后，"这一批文件叫什么名字"在开工前就是定稿的：归组、源文件快照、
         * 落点、链尾闸门读到的都是同一份事实。开工时那一次照旧留着（幂等：名字已标准就快速返回），
         * 手动档「只解压」也只有那一次（单任务，本来就没有竞争）。
         */

        /*
         * 落点这一档要能回答"入口包还压在包里时，它将来落在哪"（真机 CCCC 2026-10-06：
         * 用户连着追问的"批的落点还是按任务表里这一组第一单选"）。
         *
         * 位置：**改名归一之后**（名字定了才谈得上推标准入口包名）、**工作区根之后**。
         * ⛔ 挂的就是"唯一出口"那个方法本身，判据一个字都不在这里写。
         *
         * ⚠ **为什么"工作区根之后"这一步不影响工作区根**（2026-10-11 核对，原先这里写的是
         * "工作区根之前"、与代码相反，害得它看起来像个活的缺陷）：`_host.ApplyBatchWorkspaceRoot` 里
         * 唯一消费落点的地方是挑锚点，而那条路有两个事实把它挡住了 ——
         *   ① 未挂钩时 `PathService` 直接返回 null（`PathService.cs` 里 `resolver == null ⇒ null`），
         *      落点退回老公式；
         *   ② 挂钩之后，这一档的判据第一句就是"**产出这一份的那一单**在不在任务表里"
         *      （`_host.ResolveProducerEntryArchivePathCore` 里 `FindProducerTaskForPackage` 为 null ⇒ 返回空串）
         *      —— 而"那一单"若在表里，它自己的文件就在盘上、落点一定算得出来 ⇒
         *      挑锚点时它**先于**这一单被收进 `destinations`（未解出入口包的那一单由
         *      `IsUnresolvedVolumeGroupMember` 挪到兜底位，见 `_host.ApplyBatchWorkspaceRoot`）。
         * ⇒ 两种情形下锚点都是同一个答案；顺序只影响可读性，不影响行为。
         * 实测佐证：真机 EEE 形状里那句 `本批有 1 单是分卷组的成员…工作区根改用别的单来定`
         * 后面跟的工作区根正是产出链那一层（`…\in\111\111\.ArchiveFixer.work`），
         * 当年那个 `…\EEEE\111(4)\111\.ArchiveFixer.work` 没再现。
         */
        ExtractionVolumeStepFacts volumeStepFacts = new(_host);
        _host._pathService.GroupProducerEntryResolver =
            (task, options, archivePath) => volumeStepFacts.ResolveProducerEntryArchivePathCore(task, options, archivePath, 0);

        _host._pathService.DiagnosticLog = message => _host.AppendLog("WARN", message);

        /*
         * 「空间不足」模式：**批首钉死**（用户 2026-09-27 拍板的手动开关，见 _host._spaceTightPolicy.Enabled）。
         * 位置刻意在并发与空间规划之前 —— 排序、并发、源包处理三件事都要读它。
         */
        _host._spaceTightPolicy = SpaceTightPolicy.ForBatch(_host._vm.SpaceTightMode);

        /*
         * 本批是不是一键档（只用于"中途空间不足要不要弹那一次纯提示"）：
         * 一键档的人常常已经走开，手动档他正坐在屏幕前看列表（任务状态会当场变红）。
         */
        _host._oneClickThisBatch = oneClickRun;
        _host.Schedule.SpaceBlockedNoticeShown = false;

        maxParallel = _host.BatchReminders.ResolveMaxParallel(selectedTasks, out fullSpeed);

        if (!_host._spaceTightPolicy.Enabled)
        {
            _host.AppendLog("INFO", $"并发：用户选择 {maxParallel} 个（1–4；普通解压按这个数）。");
        }

        return true;
    }

    /// <summary>
    /// 等待所有已启动的任务结束 + **等空间的补跑环** + 批末补判「缺卷」。
    /// 三件事必须在同一个环里做（见方法内注释：分开写会让补跑环一次都不进）。
    /// </summary>
    internal async Task RunWaitAndVolumeRetryLoopAsync(
        IReadOnlyList<ArchiveTask> selectedTasks,
        List<Task> runningTasks,
        List<(ArchiveTask Task, ScheduledExtractionItem Item, SpaceGateDecision Gate)> spaceWaiting,
        int maxParallel,
        bool oneClickRun)
    {
        /*
         * ===== 等待所有已启动的任务结束 + **等空间的补跑环**（用户 2026-10-02 真机）=====
         *
         * 两件事必须在**同一个环**里做，这是踩过一次才定下来的：补跑环如果写在"等所有任务结束"
         * **之后**，轮到它时 `runningTasks` 已经空了 —— 那个 `while` 一次都不进，
         * 被拒的任务照样在批末被记成「空间不足」（红检现场：用例里第二个包一直是"未处理"）。
         *
         * 规则：**每跑完一个任务就重试一遍等待名单**（它一收尾就把预留放出来了，空间是真变多了）；
         * 只要还有人在跑，空间就还可能变多，就继续等。没人跑了 ⇒ 空间不会再变多 ⇒ 剩下的如实报。
         *
         * ⛔ 三点纪律：① 重试仍然走同一个 `TryReserve`（放行判据一个字没放宽）；
         * ② 名单里的任务**不占并发位**，只有真排上了才进 `runningTasks`；
         * ③ 用户点「停止后续」⇒ 立刻停（剩下的照旧什么都不做、如实落状态）。
         *
         * ⚠ 2026-10-05 外面又套了一层（同一个环，⛔ 不是第二套批末流程）：**批末补判「缺卷」**
         * （用户口径 2）。它只能在"这一批的解压**都跑完**之后"判（那时缺的那几卷可能已经被解出来 /
         * 被收拢到位）；判出来"补齐了"的那几组要**照常跑**，所以它们必须回到**这个环**里等收尾 ——
         * 这也是"补跑环必须与等所有任务结束写在同一个环里"那条纪律的同一条道理。
         */
        /*
         * ⛔ 2026-10-15：**批末补判的重判次数护栏**（保命，不是性能）。
         *
         * 运行期实测到的死循环（全量测试里跑了 40 分钟、日志涨到 125 MB 都出不来）：
         * 批末补判说"「set.7z.002」这一组现在齐了（共 2 卷），照常跑这一组" ⇒ 排进 `runningTasks` ⇒
         * 它一开工，预检又说"这一组缺的是**第 1 卷** … 判不出该怎么收，整组一个字节都没动" ⇒
         * 回到本环再判 ⇒ 又是"齐了" ⇒ ……**两个判据对同一组给出相反结论，这一环永远出不去**。
         *
         * 上面那段注释写的出口（"撞上轮数上限"）在代码里**根本不存在** —— 这个 `while (true)`
         * 原来只有"批末补判返回空"一个出口。⇒ 补一道**硬上限**：重判超过 `MaxVolumeRecheckPasses`
         * 次就停下，把那几单**如实留在「未处理」那一档**（与既有那份"没有结论"的口径一致），
         * 并写一行点名它们。⛔ 判据一个字没放宽 —— 缺卷照旧不开始解。
         *
         * ⚠ 为什么是"趟数硬上限"而不是"看名单有没有变少"：实测的形状是**缩了又涨**
         * （这一趟补判把一单挪出名单 ⇒ 它跑起来撞缺卷 ⇒ 下一趟又有单进来）⇒
         * "连续不缩小才计数"那种写法在振荡下永远到不了阈值，等于没修。
         * 这里要的是**保证终止**，所以数的是总趟数。
         */
        while (true)
        {
            while (runningTasks.Count > 0)
            {
                Task finished = await Task.WhenAny(runningTasks);
                runningTasks.Remove(finished);
                await finished;

                if (_host.IsStopping || _host._operationCts!.IsCancellationRequested)
                {
                    break;
                }

                if (spaceWaiting.Count == 0)
                {
                    continue;
                }

                /*
                 * 刚跑完一个任务 ⇒ **当场再探一次可用空间**，再拿它重试等待名单。
                 *
                 * 账本平时由空间侦察每 5 秒刷一针；可"任务收尾把预留放出来"这件事是**立刻**发生的，
                 * 如果这里不重探，补跑环会拿着 5 秒前的旧数字白等一轮（真机上就是"明明跑完了一个大包、
                 * 空间已经回来了，那个小包还在等"）。
                 */
                _host.Schedule.SpaceLedger!.RefreshAvailable(
                    _host.Schedule.ProbeAvailableSpace(_host.Schedule.ResolveSpaceProbePath(selectedTasks)));

                for (int i = 0; i < spaceWaiting.Count;)
                {
                    if (runningTasks.Count >= maxParallel)
                    {
                        break;   // 并发位又满了：等下一轮（跑完一个再来看）
                    }

                    (ArchiveTask waitingTask, ScheduledExtractionItem waitingItem, SpaceGateDecision waitingGate) = spaceWaiting[i];

                    SpaceGateDecision retry = _host.Schedule.SpaceLedger.TryReserve(
                        waitingItem.RequiredBytes,
                        waitingItem.Estimate.ReclaimableBytes);

                    if (!retry.Allowed)
                    {
                        i++;
                        continue;
                    }

                    ScheduledTaskRuntime waitingRuntime = _host.Schedule.GetOrCreateRuntime(waitingTask);
                    waitingRuntime.ReservedBytes = retry.ProbeFailed ? 0L : waitingItem.RequiredBytes;
                    waitingRuntime.AvailableBeforeStart = _host.Schedule.SpaceLedger.AvailableBytes;

                    spaceWaiting.RemoveAt(i);

                    _host.AppendLog(
                        "INFO",
                        $"{waitingTask.LogName}：腾出空间了，现在补跑它（它刚才被拒的原因：{waitingGate.Reason}）");

                    runningTasks.Add(_host.TaskRunner.RunScheduledTaskAsync(waitingTask, oneClickRun, waitingRuntime));
                }
            }

            /*
             * 用户点了「停止后续」⇒ 批末补判整段不做（不变量 9：停止后续只阻止**启动后续**，
             * 不许变成"强杀当前"，也不许在停下之后又去起新的任务）。
             * 名单里那几单照旧什么都不做、状态留在「未处理」—— 如实反映"这一批没轮到它"。
             */
            if (_host.IsStopping || _host._operationCts!.IsCancellationRequested)
            {
                break;
            }

            /*
             * ===== 分卷批末补判：已整段搬到 `SecondStepResidue`（用户 2026-10-24 拍板）=====
             *
             * 第一大步只许「登记信息」，⛔ 不许再收卷 / 归组 / 补跑 / 把疑似片升级成缺卷。
             * 原来这里的 `RecheckDeferredVolumeDeficitsAsync`（整个补跑环）与
             * `UpgradeUnrecognizedMaybePieceVerdicts`（升级「分卷缺失」）已停用；
             * 实现仍在 `src/ArchiveFixer/SecondStepResidue/`，重建第二大步时去那儿挑。
             */
            break;
        }
    }
}


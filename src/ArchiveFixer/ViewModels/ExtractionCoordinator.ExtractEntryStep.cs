using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Globalization;
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
        public Task StartExtractAsync(bool extractIntoSourceFolder = false) =>
            StartExtractCoreAsync(oneClickRun: false, runOptions: null, extractIntoSourceFolder: extractIntoSourceFolder);

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
            bool continueDeferredVolumeDeficits = false)
        {
            if (_isExtracting)
            {
                AppendLog("WARN", "当前已经在批量解压中，忽略重复启动。");
                return;
            }

            var selectedTasks = Tasks.Where(x => x.IsSelected).ToList();

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

            if (!_archiveEngine.IsAvailable)
            {
                /*
                 * 提示由 ToolLocator 现算（体检报告 §4 第 1 条）：
                 * 这条判定的口径是"任一引擎可用"，而默认优先级是 WinRAR(UnRAR) → 7-Zip ——
                 * 写死 7z 路径会把"其实只缺 UnRAR"的用户引去修一个没问题的目录。
                 * 两条期望路径与当前优先级顺序都由 ToolLocator 给出（外部工具路径只有它一个来源）。
                 */
                _dialogService.ShowError(ToolLocator.Default.DescribeNoEngineAvailable());
                AppendLog("ERROR", StatusText.SevenZipMissing);
                AppendLog("ERROR", ToolLocator.Default.DescribeAvailability());
                return;
            }

            _runOptions = runOptions;

            /*
             * 手动档「解压到当前文件夹」：只认**手动那一条入口**传来的值。
             * 一键处理那条入口压根没有这个参数（永远 false）—— 这里再挡一道，防止以后有人顺手把它接过去：
             * 批量摊平会把几十个包的内容物混进同一层，是用户明令禁止的。
             */
            _extractIntoSourceFolderThisRun = extractIntoSourceFolder && !oneClickRun;

            if (extractIntoSourceFolder && oneClickRun)
            {
                AppendLog("WARN", "一键处理不接受「解压到当前文件夹」（批量一律套包名那一层）—— 本次按普通一键处理执行。");
            }

            if (_extractIntoSourceFolderThisRun)
            {
                AppendLog("INFO", StatusText.ExtractIntoSourceFolderReminderLog);
            }

            /*
             * ===== 一键处理期间**一个弹窗都不弹**（用户 2026-09-27 真机铁证）=====
             *
             * 原话："什么叫做一键解压，就是说用户选中你来，然后自己去忙了，根本就没有空管你，
             * 以后不要出现弹窗"。他那一批 13 个任务一共被打断 8–9 次（多分支确认框 ×6–7 +
             * 批次结束的提示 ×1）—— 每一次都要他回来点一下，而"一键"的全部意义就是不回来。
             *
             * 处置：凡是"需要用户回答"的框，一键档一律**不弹**（照旧做最不意外的那件事），
             * 并把"本来要问什么、按什么办了"写进日志：
             *   · 多分支要不要展开 → **按②页所选档位**（用户 2026-10-04 推翻"一键档一律保守不展开"：
             *     默认档「展开所有分支」下递归核心根本不会问；只有用户显式选了「单链自动展开」时
             *     才会停在这一层，见 RunRecursiveAsync 里 `expandAll` 那一段）；
             *   · 同名冲突怎么处理 → 自动重命名、绝不覆盖（走既有的 ConflictActions 保守档）；
             *   · 分卷缺失要不要指定目录 → 不补救，照旧落「分卷缺失」（不变量 7 不放松）；
             *   · 批次结束的密码提示 → 只写日志（含"再点一次可手动输密码"的出路）。
             * ⛔ 判定与日志一条都没少，少的只是弹窗；手动「只解压」仍然照旧弹（那时用户就在旁边）。
             *
             * ⚠ 抑制必须排在**本批状态清零之后**（2026-09-29 复核逮到）：`ResetBatchConflictState()`
             * 会把 `_conflictPromptUnavailable` 一起置回 false，老代码在它之前抑制 —— 于是设置里选
             * 「询问」的人，一键档批中间照样会撞上那个聚合询问框（违反"一键档批中间零弹窗"）。
             * 见下面 IsBusy = true 之后那一段。
             */

            if (runOptions != null)
            {
                /*
                 * 本次选项进日志（规格 §9.2 硬要求⑥）：用户事后要能回答"这次为什么解到这里"。
                 * 逐任务那一行在 ExtractSingleTaskAsync 里写（带实际落点），这一行是本批的总纲。
                 */
                AppendLog("INFO", $"本次一键处理按「本次选项」执行：{runOptions.Describe()}");

                if (!runOptions.IsPlacementValid)
                {
                    AppendLog(
                        "WARN",
                        "本次选项里选了「指定位置」但没填路径 —— 为了避免它被解释成「未指定位置」那一档，" +
                        "本次落点回落设置里的值（其余两项照常生效）。");
                }
            }

            /*
             * ===== 解压前的提醒（用户 2026-09-22 需求第 8 条）=====
             *
             * 位置：**引擎可用性检查之后**（一个引擎都没有时该报的是「没有可用的解压引擎」，
             * 而不是"源目录里有些说明文件"）、**IsBusy = true 之前**（这是"还没动手"的最后一步：
             * 用户在这里选「先不处理」，这一批就是一个字节都没动过的干净状态）。
             *
             * 整批只弹一次，两段合并成同一个框（项目先例：多任务清理合并成 1 次确认）；
             * 两段都为空时**不弹** —— 空对话框只会训练用户闭眼点确定。
             */
            if (!await ConfirmBatchRemindersAsync(selectedTasks))
            {
                return;
            }

            IsBusy = true;
            IsStopping = false;
            _isExtracting = true;

            // 本批的密码失败登记从零开始：上一批的残留不能让这一批多弹一次提示。
            ClearPasswordFailures();

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
                _passwordService.BeginBatch();
            }

            // 同名冲突的记账同样从零开始：上一批答过的「全部覆盖」绝不许延续到这一批。
            ResetBatchConflictState();

            /*
             * 一键档的"零弹窗"抑制放在这里 —— **必须在 ResetBatchConflictState 之后**：
             * 那个方法会把 _conflictPromptUnavailable 置回 false，先抑制后清零等于没抑制
             * （2026-09-29 复核逮到：设置里选「询问」时一键档批中间仍会弹聚合询问框）。
             */
            if (oneClickRun)
            {
                SuppressDecisionPromptsForOneClickRun();
            }

            // 「完成后打开输出目录」整批只开一次；上一批开过不算这一批的（见 _outputFolderOpenedThisBatch）。
            ResetBatchOutputFolderState();

            // 手动密码只对**本次运行**有效（不变量 5：绝不落盘）：批开始时重新问一次、重新记一次。
            ResetManualBatchPassword();

            // 空间规划与本批记账清零（见 finally 里的说明：清在**批首**，自测才读得到这一批的证据）。
            ResetSpacePlanningState();

            /*
             * 「缺卷留到最后再判」那份名单的清空点（用户 2026-10-05 口径 2）。
             *
             * ⛔ 一键处理的**后续轮次不清**：第 1 轮没解出来的第 1 卷很可能第 2 轮才出现，
             * 清了就等于把"留到最后"变成"留到本轮末尾"（真机那条 `.002` 正是跨轮才补上的形状）。
             * 用户重新点一次 = 新的一批 ⇒ 清空（旧批次的缺口结论不许活到新批次上）。
             */
            if (!continueDeferredVolumeDeficits)
            {
                _volumeDeficitDeferred.Clear();
                _triedVolumeGroupSignatures.Clear();
                _gatheredUserSourcePieces.Clear();
                _deferredVolumeGroupMember.Clear();
                _byteBudgetLinkedSources.Clear();
                _scoredSlotAttempts.Clear();
                _scoredSlotLinks.Clear();
                _scoredSlotAttempts.Clear();
                _scoredSlotLinks.Clear();
                _consumedVolumeSources.Clear();
                _groupConsumerByName.Clear();
                _volumeGroupUnits.Clear();
                ClearGroupPieceProducers();
                _producerSourcesCollected.Clear();
                _adoptedLandedPieces.Clear();
                _groupLayerByBaseName.Clear();
                _volumeStepTasks.Clear();
                _volumeGroupEntriesThisBatch.Clear();
                _volumeGroupEntriesThisBatch.Clear();
                _volumeDeficitFinalPass.Clear();
                ClearVolumeHandoffLedger();
            }

            try
            {
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
                 * ① **整批一次性**（_manualPasswordPrompted）—— 不做成每个包问一次，那正是 D-4 要避免的；
                 * ② 手动「只解压」这条路上**只对本次运行有效、绝不落盘**（不变量 5）——
                 *    只是一个字段，不进设置、不进密码列表、不进日志；
                 * ③ **无 UI 宿主不弹窗、不死等** —— 判定见 PromptForManualBatchPasswordAsync。
                 *
                 * ⚠ **一键档不再走这个框**（用户 2026-09-29 拍板）：一键处理的红线是"批中间零弹窗"，
                 * 而这个框恰好卡在开工前，等于给一键档开了第二个口子。一键档**没有手动密码入口**
                 * （当时那个输入框是整块撤掉的，不是搬去了别处），只写一条"要手动给就去「密码」页
                 * 一键导入"的日志 —— 见 LogOneClickPasswordHint。
                 *
                 * ⇒ 由此可以断定：一键档下 `_manualBatchPasswords` **恒为空**，所以每轮开头那次
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
                if (!TryPrepareBatchForExtract(selectedTasks, oneClickRun, out int maxParallel, out bool fullSpeed))
                {
                    return;
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
                ExtractionSchedulePlan plan = BuildSchedulePlan(selectedTasks, maxParallel, _spaceTightThisBatch);

                if (_spaceTightThisBatch)
                {
                    /*
                     * 并发档在**计划之后**定（它要读计划里的净占用与空间建议）——
                     * 顺序与普通档相反，因为普通档读的是设置、这里读的是刚刚算出来的空间。
                     */
                    maxParallel = ResolveSpaceTightParallelForBatch(plan);
                }

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
                LogMultiLayerSpaceRisk(plan);

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

                foreach (ScheduledExtractionItem item in plan.Ordered)
                {
                    ArchiveTask task = item.Task;

                    if (IsStopping || _operationCts.IsCancellationRequested)
                    {
                        LogStopRequestOnce();
                        break;
                    }

                    // 每个任务各自的"已经在队列里等过"标记与开始等的时间（只有等得久才补一句，见下）。
                    bool queuedLogged = false;
                    DateTime queuedSince = DateTime.Now;

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
                            queuedSince = DateTime.Now;
                        }

                        if (!throttleExplained)
                        {
                            throttleExplained = true;

                            /*
                             * 一批只说一次（用户 2026-09-26 第 45 条：这条刷了 74 遍）。
                             * 三个必须写对的点：
                             * ①**位置**：「全速（本批不节流）」在**②页 →「并发与空间」**，不在主界面；
                             * ②**怎么用**：勾上**立刻**就放开本批（下面那个 if 会当场生效），不用停也不用重开；
                             * ③另一条出路是「停止后续」。
                             */
                            AppendLog(
                                "INFO",
                                $"并发已满（{runningTasks.Count}/{maxParallel} 个任务正在跑），后面的任务在队列里等空位。" +
                                (fullSpeed
                                    ? "（已开「全速」，本批不再节流。）"
                                    : "想立刻放开：②页 →「并发与空间」勾上「全速（本批不节流）」—— 勾上当场生效，" +
                                      "不用停止、也不用重开；或点①页「停止后续」不再启动后面的任务。" +
                                      "（这条只在第一次排队时说一遍，本批还有几个在等，跑完会在结尾汇总。）"));
                        }

                        /*
                         * 中途勾上「全速」→ **立刻放开**（用户问的正是"是要暂停下来开还是直接开"：直接开）。
                         * 这里重读一次那个开关（它是 MainViewModel 上的本次运行开关，不是落盘设置）。
                         *
                         * ⚠ 「空间不足」模式下这一条**不生效**（用户 2026-09-27："忽略全速和并发档"）：
                         * 那个模式的并发数是按空间算出来的，中途放开等于把"别把盘写满"这条自己拆掉。
                         */
                        if (!_spaceTightThisBatch && !fullSpeed && _vm.RunAtFullSpeed)
                        {
                            fullSpeed = true;
                            maxParallel = Math.Max(1, plan.Ordered.Count);

                            AppendLog("INFO", $"检测到「全速」已勾上，本批立刻放开并发（同时最多 {maxParallel} 个）。");

                            break;
                        }

                        Task finished = await Task.WhenAny(runningTasks);
                        runningTasks.Remove(finished);
                        await finished;

                        if (IsStopping || _operationCts.IsCancellationRequested)
                        {
                            break;
                        }
                    }

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
                        TimeSpan waited = DateTime.Now - queuedSince;

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
                         * 不记失败、不改任务状态 —— 先挂进"等空间"名单（见上面那段说明）。
                         * 只写一行 INFO 说清"它在等什么"，用户才不会以为这个包已经被判死刑了。
                         */
                        spaceWaiting.Add((task, item, gate));

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

                    runningTasks.Add(RunScheduledTaskAsync(task, oneClickRun, runtime));
                }

                await RunWaitAndVolumeRetryLoopAsync(selectedTasks, runningTasks, spaceWaiting, maxParallel, oneClickRun);

                /*
                 * 等到最后还是排不上的：**这时候才**如实记「空间不足」（状态 / 错误信息 / 日志 / 批末清单
                 * 走同一个落点 `MarkSpaceBlocked`）。⛔ 这些任务从头到尾一个字节都没动。
                 */
                foreach ((ArchiveTask waitingTask, ScheduledExtractionItem waitingItem, SpaceGateDecision waitingGate) in spaceWaiting)
                {
                    MarkSpaceBlocked(waitingTask, waitingItem, waitingGate);
                }

                AppendLog("INFO", "批量解压完成");

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
                        + (fullSpeed
                            ? "本批已开「全速」。"
                            : "下次想全程不排队：②页 →「并发与空间」勾上「全速（本批不节流）」（勾上当场生效）。"));
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
                _spaceTightThisBatch = false;
                _spaceTightKeepSourceThisBatch = false;

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

                // 批尾清掉这条批内状态（与 _spaceTightThisBatch / _oneClickThisBatch 同一口径）。
                BatchCreatedTargetDirectory = string.Empty;

                UpdateSummary();
            }
        }


        /// <summary>
        /// 批首准备：补跑归组 → 定工作区根 → 点名第二大步 → 挂落点解析器 → 钉空间不足档与并发上限。
        /// 返回 false = 这一批停下（工作区没定下来，已经弹过错误、一个字节都没写）。
        /// </summary>
        private bool TryPrepareBatchForExtract(
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
             * return 在 try 里面，所以 finally 照常收尾（_isExtracting / IsBusy 都会被复位）。
             */
            /*
             * ⚠ 2026-10-09（D 结案 + 用户「回上一步解决错误」）：**定工作区根之前**先给这一批每一单
             * 补跑一次"按它所在目录里的真实文件归组"。
             *
             * 为什么必须在这一刻（出处 `ViewModels/OneClickCoordinator.cs:1539-1564` 的注释）：
             * 续解层的内层任务是走 `AddPathsAsync(..., suppressAutoScan: true)` 加进来的，
             * **那条路不跑扫描期的分卷归组** ⇒ 它们的 `IsVolumeGroup` 一直是 false，
             * 而 `ApplyBatchWorkspaceRoot` 里那道"跳过入口包还没解出来的分卷组单"的守卫
             * （`IsUnresolvedVolumeGroupMember`，它第一句就是 `if (!task.IsVolumeGroup) return false;`）
             * 因此对它们一律放行 ⇒ 真机 EEEE 的整批工作区根被那一单的**占位落点**
             * （`…\EEEE\111(4)\111`）挑走。
             *
             * ⇒ 这就是"上游没做完的事，别在下游补"：把归组补在**它该在的位置**（定锚点之前），
             * 而不是放宽那道守卫的判据。唯一出口仍是 `ApplyVolumeGroupingFromDirectory`
             * （它自己再转调 `VolumeGroupDetector.Group` + `VolumeGroupingService.ApplyGroupInfo`，
             * 与扫描期、手动档完全同一条路）；归不上组 ⇒ 什么都不动（与以前一样）。
             */
            foreach (ArchiveTask groupingTarget in selectedTasks)
            {
                if (groupingTarget != null && !groupingTarget.IsVolumeGroup)
                {
                    OneClickCoordinator.ApplyVolumeGroupingFromDirectory(groupingTarget);
                }
            }

            WorkspaceRootResolution workspaceResolution = ApplyBatchWorkspaceRoot(selectedTasks);

            // 第二大步点名：批首只读组分析，把可证完整那一组的入口任务标成第二大步（只解一层）。
            MarkVolumeStepEntriesForBatch(selectedTasks);

            if (!workspaceResolution.Resolved)
            {
                _dialogService.ShowError(workspaceResolution.Reason);
                AppendLog("ERROR", "这一批已经停下：工作区位置没定下来，一个字节都没写、源包一个都没碰。");
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
             * "工作区根之前"、与代码相反，害得它看起来像个活的缺陷）：`ApplyBatchWorkspaceRoot` 里
             * 唯一消费落点的地方是挑锚点，而那条路有两个事实把它挡住了 ——
             *   ① 未挂钩时 `PathService` 直接返回 null（`PathService.cs` 里 `resolver == null ⇒ null`），
             *      落点退回老公式；
             *   ② 挂钩之后，这一档的判据第一句就是"**产出这一份的那一单**在不在任务表里"
             *      （`ResolveProducerEntryArchivePathCore` 里 `FindProducerTaskForPackage` 为 null ⇒ 返回空串）
             *      —— 而"那一单"若在表里，它自己的文件就在盘上、落点一定算得出来 ⇒
             *      挑锚点时它**先于**这一单被收进 `destinations`（未解出入口包的那一单由
             *      `IsUnresolvedVolumeGroupMember` 挪到兜底位，见 `ApplyBatchWorkspaceRoot`）。
             * ⇒ 两种情形下锚点都是同一个答案；顺序只影响可读性，不影响行为。
             * 实测佐证：真机 EEE 形状里那句 `本批有 1 单是分卷组的成员…工作区根改用别的单来定`
             * 后面跟的工作区根正是产出链那一层（`…\in\111\111\.ArchiveFixer.work`），
             * 当年那个 `…\EEEE\111(4)\111\.ArchiveFixer.work` 没再现。
             */
            _pathService.GroupProducerEntryResolver =
                (task, options, archivePath) => ResolveProducerEntryArchivePathCore(task, options, archivePath, 0);

            _pathService.DiagnosticLog = message => AppendLog("WARN", message);

            /*
             * 「空间不足」模式：**批首钉死**（用户 2026-09-27 拍板的手动开关，见 _spaceTightThisBatch）。
             * 位置刻意在并发与空间规划之前 —— 排序、并发、源包处理三件事都要读它。
             * 后面那一行是它的**安全档**（「不删原包」）—— 两档一起钉，同一批口径一致。
             */
            _spaceTightThisBatch = _vm.SpaceTightMode;
            _spaceTightKeepSourceThisBatch = _spaceTightThisBatch && _vm.SpaceTightKeepSource;

            /*
             * 本批是不是一键档（只用于"中途空间不足要不要弹那一次纯提示"）：
             * 一键档的人常常已经走开，手动档他正坐在屏幕前看列表（任务状态会当场变红）。
             */
            _oneClickThisBatch = oneClickRun;
            _spaceBlockedNoticeShown = false;

            maxParallel = ResolveMaxParallel(selectedTasks, out fullSpeed);

            if (_spaceTightThisBatch && fullSpeed)
            {
                /*
                 * 「全速」在这个模式下**不生效**（用户原话："忽略全速和并发档"；
                 * 2026-09-27 追加：并发档改成"只许往下压"，见 ResolveSpaceTightParallelForBatch）。
                 * 说清原因再关掉它：否则日志里"已开全速"与"同时最多跑 3 个"会自相矛盾。
                 */
                AppendLog(
                    "WARN",
                    "「空间不足」模式已开：本批「全速」不生效，并发由空间自己定"
                    + "（若②页的「最大并发解压数」比它更低，就按更低的跑；设置里的值一个字节都没改）。");

                fullSpeed = false;
            }

            return true;
        }

        /// <summary>
        /// 等待所有已启动的任务结束 + **等空间的补跑环** + 批末补判「缺卷」。
        /// 三件事必须在同一个环里做（见方法内注释：分开写会让补跑环一次都不进）。
        /// </summary>
        private async Task RunWaitAndVolumeRetryLoopAsync(
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
            const int MaxVolumeRecheckPasses = 20;
            int volumeRecheckPasses = 0;

            while (true)
            {
                while (runningTasks.Count > 0)
                {
                    Task finished = await Task.WhenAny(runningTasks);
                    runningTasks.Remove(finished);
                    await finished;

                    if (IsStopping || _operationCts!.IsCancellationRequested)
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
                    _spaceLedger!.RefreshAvailable(ProbeAvailableSpace(ResolveSpaceProbePath(selectedTasks)));

                    for (int i = 0; i < spaceWaiting.Count;)
                    {
                        if (runningTasks.Count >= maxParallel)
                        {
                            break;   // 并发位又满了：等下一轮（跑完一个再来看）
                        }

                        (ArchiveTask waitingTask, ScheduledExtractionItem waitingItem, SpaceGateDecision waitingGate) = spaceWaiting[i];

                        SpaceGateDecision retry = _spaceLedger.TryReserve(
                            waitingItem.RequiredBytes,
                            waitingItem.Estimate.ReclaimableBytes);

                        if (!retry.Allowed)
                        {
                            i++;
                            continue;
                        }

                        ScheduledTaskRuntime waitingRuntime = GetOrCreateRuntime(waitingTask);
                        waitingRuntime.ReservedBytes = retry.ProbeFailed ? 0L : waitingItem.RequiredBytes;
                        waitingRuntime.AvailableBeforeStart = _spaceLedger.AvailableBytes;

                        spaceWaiting.RemoveAt(i);

                        AppendLog(
                            "INFO",
                            $"{waitingTask.LogName}：腾出空间了，现在补跑它（它刚才被拒的原因：{waitingGate.Reason}）");

                        runningTasks.Add(RunScheduledTaskAsync(waitingTask, oneClickRun, waitingRuntime));
                    }
                }

                /*
                 * 用户点了「停止后续」⇒ 批末补判整段不做（不变量 9：停止后续只阻止**启动后续**，
                 * 不许变成"强杀当前"，也不许在停下之后又去起新的任务）。
                 * 名单里那几单照旧什么都不做、状态留在「未处理」—— 如实反映"这一批没轮到它"。
                 */
                if (IsStopping || _operationCts!.IsCancellationRequested)
                {
                    break;
                }

                /*
                 * ===== 批末补判「缺卷」（用户 2026-10-05 口径 2）=====
                 *
                 * 判据与执行体都在 `RecheckDeferredVolumeDeficits` 里：再收一次卷 → 现归一次组
                 * → 齐了就返回该补跑的那几单；仍然缺 ⇒ 一键档留给链尾（还有后续轮次）、
                 * 手动档**到这里**才如实报缺卷。
                 *
                 * ⛔ 兜底：这个方法要么给出"该跑的那几单"，要么把状态落定 —— 绝不会让名单里的任务
                 * 停在"没人管"那一档（唯一例外是整条链被用户停下，那时它如实留在「未处理」）。
                 */
                List<ArchiveTask> deferredVolumeTasks = await RecheckDeferredVolumeDeficitsAsync(
                    finalPass: !oneClickRun,
                    // 这一站没有 CancellationToken 在手（老签名就没有）⇒ 传 None，与改成 async 之前等价。
                    CancellationToken.None).ConfigureAwait(false);

                /*
                 * 「7-Zip 打不开」的那一份若看着就是本批某一组的一片 ⇒ 到这里才按**严格判据**
                 * 升级成「分卷缺失」（用户 2026-10-09 口径：未识别的先跳过、到了最后再重新检验一步）。
                 * ⚠ 2026-10-17：这两个成员从 2026-10-16 加进来就一直**没有调用点**（名单只写不读）⇒ 本处补上。
                 * 幂等：只动机器终态还是「已跳过」的那几单。
                 */
                UpgradeUnrecognizedMaybePieceVerdicts();

                if (deferredVolumeTasks.Count == 0)
                {
                    break;
                }

                /*
                 * 保命护栏（见上面那段说明）：**总趟数硬上限**。
                 *
                 * 正常情形下一两趟就清空名单（每一趟会把名单里所有能补上的组一次性补齐）；
                 * 到了上限还在打转，说明批末补判与开工前预检对同一组给出的结论相反 ——
                 * ⛔ 再判下去只会把日志写爆、把这一批永远挂住 ⇒ 停下 + 点名 + 如实留在「未处理」。
                 */
                volumeRecheckPasses++;

                if (volumeRecheckPasses > MaxVolumeRecheckPasses)
                {
                    int stillDeferred = _volumeDeficitDeferred.Count;

                    AppendLog(
                        "WARN",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.VolumeRecheckPassLimitReachedFormat,
                            volumeRecheckPasses,
                            stillDeferred,
                            string.Join(
                                "、",
                                _volumeDeficitDeferred.Take(5).Select(item => item.LogName))));

                    break;
                }

                foreach (ArchiveTask deferredTask in deferredVolumeTasks)
                {
                    runningTasks.Add(RunScheduledTaskAsync(
                        deferredTask,
                        oneClickRun,
                        GetOrCreateRuntime(deferredTask)));
                }
            }
        }
    }
}
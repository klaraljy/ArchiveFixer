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
    /// <summary>空间体检/趋势/调度计划/空间闸门/多层风险/批级上限（第 12 块，从 Password2 大杂烩搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// 给界面算一次"按当前可用空间，最多能并行几个"（用户 2026-09-22 需求第 2 条）。
        ///
        /// <para>它与真正开跑时用的是**同一段实现**（<see cref="BuildSchedulePlan"/>），
        /// 所以界面上显示的建议与日志里那一刻的调度口径不会分叉 ——
        /// "界面写一个数、跑起来按另一个数"正是本项目最不能接受的那类形态。</para>
        ///
        /// <para>⚠ 它会 stat 每个源包（以及分卷的每一卷），所以调用方要放后台线程。</para>
        /// </summary>
        internal ExtractionSchedulePlan BuildSpaceAdvice(IReadOnlyList<ArchiveTask> tasks)
        {
            int requested = Math.Clamp(Settings.MaxParallelExtractCount, 1, ExtractionScheduler.ParallelCeiling);

            return BuildSchedulePlan(tasks ?? Array.Empty<ArchiveTask>(), requested);
        }

        /// <summary>
        /// 供界面显示的"当前空间档位"一句话（其余物会不会被自动处理也在这句里说清）。
        /// </summary>
        internal string DescribeSpaceMode()
        {
            string mode = RestHandlingModes.Normalize(Settings.RestHandlingAfterVerify);
            SourceHandlingMode sourceMode = AppSettings.ParseSourceHandling(Settings.SourceHandling);
            string scope = DescribeRestScope(sourceMode);

            string rest = mode switch
            {
                // ⚠ 2026-10-15：回收站那一档整条下线 ⇒ 这里只剩两档。
                RestHandlingModes.Delete =>
                    $"其余物：任务成功后彻底删除（{scope}，不可恢复）",
                _ => "其余物：保留（不动其余物）"
            };

            // ⚠ 2026-10-15：源包那一档只剩「留下 / 彻底删除（就地）」——「移入其余物」已随用户口径删除。
            string source = sourceMode == SourceHandlingMode.KeepInPlace
                ? "源包：留下（原地不动）"
                : "源包：彻底删除（就地删，不搬进其余物）";

            return source + "；" + rest;
        }

        private long? ProbeAvailableSpace(string path)
        {
            return SpaceProbeOverride != null
                ? SpaceProbeOverride(path)
                : SpaceChecker.GetAvailableFreeSpace(path);
        }

        /// <summary>周期采样的间隔（用户要的是"时刻"，但一秒一针又纯属噪声 —— 5 秒够看清曲线）。</summary>
        internal static readonly TimeSpan SpaceTrendInterval = TimeSpan.FromSeconds(5);

        /// <summary>起侦察：批首采一针，然后每 <see cref="SpaceTrendInterval"/> 采一针。</summary>
        private void StartSpaceTrendMonitor(string probePath)
        {
            StopSpaceTrendMonitor();

            /*
             * 新一批开工 ⇒ 上一批那个"等着补记一针"的侦察器作废（它探的是上一批的盘、
             * 采样表也是上一批的）。不清掉的话，这一批跑完时补出来的可能是上一批的数字。
             */
            _spaceTrendPostscript = null;

            if (string.IsNullOrWhiteSpace(probePath))
            {
                AppendLog("WARN", "空间侦察没起来：这一批连「目标盘在哪」都没定下来（探测路径为空）。");
                return;
            }

            _spaceTrend = new SpaceTrendMonitor(() => ProbeAvailableSpace(probePath));
            _spaceTrendCts = new CancellationTokenSource();

            SpaceTrendChange? first = _spaceTrend.Record("批首");

            if (first != null)
            {
                AppendLog("INFO", SpaceTrendMonitor.DescribeChange(first, "目标盘") + $"（每 {SpaceTrendInterval.TotalSeconds:0} 秒侦察一次）");
            }

            SpaceTrendMonitor monitor = _spaceTrend;
            CancellationToken token = _spaceTrendCts.Token;

            _ = Task.Run(
                () => monitor.RunAsync(SpaceTrendInterval, line => AppendLog("INFO", line), token),
                CancellationToken.None);
        }

        /// <summary>停侦察 + 批末把空间曲线写进日志（这是"这一批空间怎么变的"唯一交代）。</summary>
        private void StopSpaceTrendMonitor()
        {
            try
            {
                _spaceTrendCts?.Cancel();
            }
            catch
            {
                // 取消失败不影响收尾。
            }

            try
            {
                _spaceTrendCts?.Dispose();
            }
            catch
            {
            }

            _spaceTrendCts = null;

            SpaceTrendMonitor? monitor = _spaceTrend;
            _spaceTrend = null;

            if (monitor == null)
            {
                return;
            }

            // 收尾再采一针，然后写曲线（收工那一刻的数字是用户最关心的那一个）。
            monitor.Record("批末");

            foreach (string line in monitor.DescribeReport())
            {
                AppendLog("INFO", line);
            }

            /*
             * 侦察器**不丢**：批末那条曲线的"收"是在**其余物删除之前**打的（删其余物发生在
             * `OneClickCoordinator` 的链尾那一步，比这里更晚）—— 真机上那一针因此比真实可用少 6 GB，
             * 用户按日志读成"这一批净吃掉 5.5 GB"（实际净省 0.5 GB）。
             *
             * 留着它，等一键处理把其余物处理完之后由 <see cref="RecordSpaceTrendPostscript"/> 补一针：
             * **同一个探测函数、同一份采样表、同一套曲线文案** —— 不新开第二套取数。
             * 下一批开工时 `StartSpaceTrendMonitor` 会先把它清掉，所以它只服务这一批的收尾。
             */
            _spaceTrendPostscript = monitor;
        }

        /// <summary>
        /// **其余物处理完之后**补一针真实可用空间并写一行 INFO（用户 2026-10-02 真机）。
        ///
        /// <para>为什么要补：批末那条空间曲线的"收"打在其余物删除**之前**，随后那些源包 / 过程物
        /// 被彻底删掉，盘上真实可用比曲线里的"收"多一大截 —— 不补这一针，用户会把这一批读成
        /// "净吃掉好几 GB"（真机那一批实际净省 0.5 GB）。</para>
        ///
        /// <para>⛔ 三条纪律：① 这一针走的是**本批那个侦察器自己的探测函数**（与每 5 秒那一针、
        /// 与每个任务开工 / 收尾那一针完全同源），不是第二套取数；② 文案由 <c>SpaceTrendMonitor</c>
        /// 产出，不是第二套曲线；③ **一批只补一针**（取走即清），没有侦察器（手动档 / 单包重试）
        /// 时什么都不写 —— 绝不编一个数字。</para>
        /// </summary>
        /// <param name="note">这一针的来由（写进那一行）。</param>
        /// <returns>真的写了一行才返回 true。</returns>
        internal bool RecordSpaceTrendPostscript(string note)
        {
            SpaceTrendMonitor? monitor = _spaceTrendPostscript;

            // 取走即清：同一批里被调用两次也不会补出两针（第二针的数字没有意义）。
            _spaceTrendPostscript = null;

            if (monitor == null)
            {
                return false;
            }

            monitor.Record(note, at: null, notify: false);

            string? line = monitor.DescribePostscript(note);

            if (string.IsNullOrWhiteSpace(line))
            {
                // 这一针与之前的针都取不到可用空间：如实不写（DescribePostscript 已经说明了原因）。
                return false;
            }

            AppendLog("INFO", line!);
            return true;
        }

        /// <summary>
        /// 逐任务采一针（开工 / 收尾各一次）。
        ///
        /// <para>两条纪律，缺一条就会踩到别人的规矩上：</para>
        /// <list type="number">
        /// <item><description><b>只记不报</b>（`notify: false`）：这一针不进过程日志 ——
        /// 第 45 条定的是"成功的任务只留一行"，在这里多写一行就是废掉它。</description></item>
        /// <item><description><b>注脚里不写任务文件名</b>（只写"任务开工 / 任务收尾"）：批末那条空间曲线
        /// 会把最低点的注脚抄进去，而它同样是"含该文件名的行" ——
        /// 全量回归实测：两条真 7z 用例（`Item45LogAndPasswordTests` 里用"行数 ≤ 3"钉着的）正是这么变红的。</description></item>
        /// </list>
        /// <para>曲线本身不受影响：这一针照样进采样表，最低点仍然写清"哪一刻、在什么节点上"。</para>
        /// </summary>
        private void RecordSpaceTrend(string note)
        {
            _spaceTrend?.Record(note, at: null, notify: false);
        }

        /// <summary>
        /// 批首清零空间规划与本批记账（账本、逐任务运行期记录、精确估算、被跳过的清单）。
        /// </summary>
        private void ResetSpacePlanningState()
        {
            _spaceLedger = null;
            _spaceProbePath = string.Empty;

            lock (_spaceRuntimeLock)
            {
                _spaceRuntime.Clear();
            }

            _spaceBlockedTasks.Clear();
            _unrecognizedMaybePiece.Clear();

            // 那一次"中途空间不足"的纯提示也跟着本批记账一起清零：下一批该弹还得弹。
            _spaceBlockedNoticeShown = false;
        }

        private ScheduledTaskRuntime GetOrCreateRuntime(ArchiveTask task)
        {
            lock (_spaceRuntimeLock)
            {
                if (!_spaceRuntime.TryGetValue(task, out ScheduledTaskRuntime? runtime))
                {
                    runtime = new ScheduledTaskRuntime();
                    _spaceRuntime[task] = runtime;
                }

                return runtime;
            }
        }

        /// <summary>
        /// 排一次执行计划：按空间需求从小到大排序 + 算建议并行数。
        ///
        /// <para>估算用**粗估**（只 stat 文件）而不是逐个列目录：50–200 个包的场景下，
        /// 为了排序去把每个包的目录都列一遍，等于在"还没开始解"之前先跑一遍整批。
        /// 真正的精确值在解压前的预检里算（那时本来就要列目录），并会在开工后调整预留。</para>
        /// </summary>
        private ExtractionSchedulePlan BuildSchedulePlan(
            IReadOnlyList<ArchiveTask> tasks,
            int requestedParallel,
            bool spaceTightOrdering = false)
        {
            _spaceProbePath = ResolveSpaceProbePath(tasks);

            /*
             * 排序键（空间不足模式 2026-10-25 新口径）：**源包总体积从大到小**。
             * 那个模式下成功即删源包，最大的包先过；后面越来越小，并发才能按剩余空间提高。
             * ⚠ 只改顺序：放行判断照旧按需求。
             */
            Func<ScheduledExtractionItem, long>? sortKey = spaceTightOrdering
                ? item => -item.Estimate.SourceBytes
                : null;

            return ExtractionScheduler.Build(
                tasks,
                task => SpaceEstimator.FromSourceFiles(task, DirectReadAppliesTo(task), PartialPublishActive)
                    .WithVolumeLayout(ResolveWorkspaceVolumeShare(ResolveSpaceProbePathOrBatch(task))),
                ProbeAvailableSpace(_spaceProbePath),
                ReserveSpaceBytes,
                requestedParallel,
                sortKey,
                spaceTightOrdering ? ExtractionSchedulePlan.SourceSizeDescendingOrderBasis : null);
        }

        /// <summary>
        /// 空间不足模式的动态并发：按当前可用空间与**剩余队列的源包体积**算还能跑几个；
        /// 每完成一个任务都重算，后续小包阶段并发自然提高。
        /// </summary>
        private int ResolveDynamicSpaceParallel(
            IReadOnlyList<ScheduledExtractionItem> pending,
            int runningCount)
        {
            int userCap = Math.Clamp(Settings.MaxParallelExtractCount, 1, 8);

            if (pending == null || pending.Count == 0)
            {
                return Math.Max(1, Math.Min(userCap, Math.Max(1, runningCount)));
            }

            long? available = ProbeAvailableSpace(_spaceProbePath);

            if (!available.HasValue)
            {
                return Math.Max(1, Math.Min(userCap, runningCount + 1));
            }

            long budget = Math.Max(0, available.Value - ReserveSpaceBytes);
            List<long> sizes = pending
                .Select(item => item.Estimate.SourceBytes)
                .ToList();

            int launchable = SpaceConcurrencyPlanner.CountLaunchable(
                budget,
                reservedBytes: 0,
                sizes,
                maxParallel: userCap);

            int desired = Math.Min(userCap, runningCount + launchable);
            return Math.Max(1, Math.Max(runningCount, desired));
        }

        /// <summary>
        /// 工作区根与这个落点是不是**同一块盘**（空间口径用，见 <see cref="TaskSpaceEstimate"/> 的同卷 / 跨卷说明）。
        ///
        /// <para>取不到（工作区根还没定 / 落点为空 / 路径认不出盘符）一律返回 null ——
        /// **绝不默认成同卷**：默认同卷等于把"工作区那块盘也要留一份"这件事从账面上抹掉，
        /// 而那正是用户 2026-09-24 要求如实算进去的那一条。</para>
        /// </summary>
        private bool? ResolveWorkspaceVolumeShare(string? targetPath)
        {
            string workRoot = _pathService.WorkDirectory;

            if (string.IsNullOrWhiteSpace(workRoot) || string.IsNullOrWhiteSpace(targetPath))
            {
                return null;
            }

            string workDrive = ResolveDriveOf(workRoot);
            string targetDrive = ResolveDriveOf(targetPath);

            if (string.IsNullOrWhiteSpace(workDrive) || string.IsNullOrWhiteSpace(targetDrive))
            {
                return null;
            }

            return string.Equals(workDrive, targetDrive, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>"这个路径在哪块盘上"：默认 <see cref="Path.GetPathRoot(string)"/>，单测可用假盘映射覆盖。</summary>
        private string ResolveDriveOf(string path)
        {
            string full = SafePathHelper.GetFullPathSafe(path);

            if (WorkspaceDriveOverride != null)
            {
                return WorkspaceDriveOverride(full);
            }

            try
            {
                return Path.GetPathRoot(full) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 这个任务的**内嵌归档**这次会不会走直读（<c>Extraction/EmbeddedZipStreamExtractor</c>）。
        ///
        /// <para>两个条件缺一不可：</para>
        /// <list type="number">
        /// <item><description>识别阶段探过、结论是"能直读"（<see cref="ArchiveTask.EmbeddedDirectReadSupported"/>）；</description></item>
        /// <item><description>这一单是**第一大步**（不是第二大步的分卷组任务）：直读只针对
        /// “单一完整压缩包（含媒体前缀的双面文件）”，而那是第一大步的工作；第二大步只解一层分卷，
        /// 不在这里做直读，空间核算也按实抠取记账。</description></item>
        /// </list>
        ///
        /// <para>它只回答"空间账面上要不要预留那份副本"，不决定解压怎么做；解压那一刻会再探一次，
        /// 探不通就原地回落到抠取（见 <c>ExtractSingleTaskAsync</c> 里内嵌归档那一段）。</para>
        /// </summary>
        private bool DirectReadAppliesTo(ArchiveTask? task)
        {
            if (task == null ||
                task.EmbeddedArchiveOffset <= 0 ||
                !task.EmbeddedDirectReadSupported)
            {
                return false;
            }

            return !IsSecondStepTask(task);
        }

        /// <summary>
        /// 本批探测哪块盘：优先归集目标目录（开了归集时产物最终都落那儿），
        /// 其次是第一个任务的输出目录，最后退回源包所在目录。
        /// </summary>
        private string ResolveSpaceProbePath(IReadOnlyList<ArchiveTask> tasks)
        {
            if (Settings.CollectResultsToDirectory && !string.IsNullOrWhiteSpace(Settings.CollectTargetDirectory))
            {
                return Settings.CollectTargetDirectory;
            }

            foreach (ArchiveTask task in tasks ?? Array.Empty<ArchiveTask>())
            {
                string path = ResolveSpaceProbePathForTask(task);

                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }

            return string.Empty;
        }

        private static string ResolveSpaceProbePathForTask(ArchiveTask task)
        {
            if (task == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(task.OutputPath))
            {
                return task.OutputPath;
            }

            return task.DirectoryPath;
        }

        private string ResolveSpaceProbePathOrBatch(ArchiveTask task)
        {
            string path = ResolveSpaceProbePathForTask(task);

            return string.IsNullOrWhiteSpace(path) ? _spaceProbePath : path;
        }

        /// <summary>
        /// 空间门拦下：任务**不启动**，状态落「磁盘空间不足」，原因里带具体数字与建议动作。
        ///
        /// <para>为什么用这个状态而不是「解压失败」：归档本身没有任何问题，连开始都没开始 ——
        /// 用户要做的是清空间 / 换盘 / 用危险模式，而不是去怀疑包坏了。</para>
        /// </summary>
        private void MarkSpaceBlocked(ArchiveTask task, ScheduledExtractionItem item, SpaceGateDecision gate)
        {
            MarkSpaceBlockedCore(
                task,
                gate.ToLogLine() + $"（{item.Estimate.Describe()}；依据：{item.Estimate.Basis}）",
                item.RequiredBytes,
                gate.AvailableBytes,
                gate.ShortfallBytes);
        }

        /// <summary>
        /// 空间门拦下的共用落点（启动前与解压前预检两条路都走它）：
        /// 状态、错误信息、日志、批末报告清单**一次写全**。
        /// </summary>
        private void MarkSpaceBlockedCore(
            ArchiveTask task,
            string message,
            long requiredBytes,
            long availableBytes,
            long shortfallBytes)
        {
            task.Status = StatusText.DiskSpaceInsufficient;
            task.ProgressText = StatusText.ProgressFailed;
            task.Operation = StatusText.OpExtract;
            task.ErrorMessage = message;
            task.EndTime = DateTime.Now;
            task.LastUpdatedTime = DateTime.Now;

            /*
             * 终态**显式落 Failed**（用户 2026-10-02 真机暴露出来的不一致）：这个方法过去只写 Status，
             * 不写 Outcome —— 于是同一个"空间不足"在不同路径下落出两种机器终态：
             * 真机那批（解压前预检那条路）报**失败 6**，而用例里（启动前那道门）批末汇总是**未处理 1**，
             * 同一批日志里还有一行写着**跳过 N 个** —— 三种说法指同一件事。
             *
             * 为什么是 Failed 而不是 Skipped：它**没有做成**、而且是要用户动手（清空间 / 换盘）的那种，
             * 落 Skipped 会被汇总与色带读成"按设计跳过、不用管"。⛔ 落 Failed 也绝不等于"源包动了"——
             * 这一档从头到尾一个字节都没动（源包原地、其余物不生成）。
             */
            task.Outcome = TaskOutcome.Failed;

            /*
             * 用户 2026-09-30 第 1 条：批末那个汇总框要"点名差多少"。
             * 这三个数**就是这里手上的**（空间门刚算出来的），原样记到任务上 ——
             * 批末诊断清单只读它（Models\BatchSummaryDiagnostics.cs），⛔ 不重算一遍。
             * 只在这里写：启动前那道门与解压前预检两条路都收口到本方法。
             */
            task.SpaceBlocked = new SpaceBlockedFacts
            {
                RequiredBytes = requiredBytes,
                AvailableBytes = availableBytes,
                ShortfallBytes = shortfallBytes
            };

            // 措辞用"未解压"而不是"未启动"：这一条同时覆盖两条路 —— 批调度里根本没开跑的那些，
            // 以及跑起来之后在解压前预检里被拦下的那些（那时引擎一个字节都还没写）。
            AppendLog("ERROR", $"空间不足，未解压：{task.FileName} —— {message}");

            _spaceBlockedTasks.Add((
                string.IsNullOrWhiteSpace(task.FileName) ? task.CurrentPath : task.FileName,
                requiredBytes,
                availableBytes,
                shortfallBytes));

            NotifySpaceBlockedOnce(task, requiredBytes, availableBytes, shortfallBytes);
        }

        /// <summary>
        /// 中途撞上空间不足时那**一次纯提示**（用户 2026-09-29 第 2 条）。
        ///
        /// <para>他的新口径：「错误也可以弹窗，因为这就相当于结束了，而且是解压失败了」——
        /// 但一键档的红线是"批中间不许要用户点一下才能继续"，所以这里三条都钉死：</para>
        /// <list type="number">
        /// <item><description><b>非模态</b>（<c>DialogService.ShowSpaceBlockedNotice</c> → <c>Show()</c>）：
        /// 窗口浮着，批一秒都不停；</description></item>
        /// <item><description><b>一个按钮</b>（只有"确定"）；</description></item>
        /// <item><description><b>同一批只弹一次</b>：后面的同类情况只写日志 —— 一批几十个包逐个弹
        /// 等于把他按在屏幕前点几十次，而那正是这条红线要防的。合并后的完整清单在批末
        /// （<see cref="ReportSpaceBlockedTasks"/> 逐条点名）与批末汇总框里。</description></item>
        /// </list>
        ///
        /// <para>⚠ 手动档**照旧只写日志**（用户原话："手动档照旧"）：他坐在屏幕前，任务状态当场变红，
        /// 不需要再飘一个窗口。</para>
        /// </summary>
        private void NotifySpaceBlockedOnce(ArchiveTask task, long requiredBytes, long availableBytes, long shortfallBytes)
        {
            if (!_oneClickThisBatch || _spaceBlockedNoticeShown)
            {
                return;
            }

            _spaceBlockedNoticeShown = true;

            string name = string.IsNullOrWhiteSpace(task.FileName) ? task.CurrentPath : task.FileName;

            _dialogService.ShowSpaceBlockedNotice(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.SpaceBlockedNoticeFormat,
                1,
                name,
                TaskSpaceEstimate.FormatSize(requiredBytes),
                availableBytes < 0 ? "未知" : TaskSpaceEstimate.FormatSize(availableBytes),
                TaskSpaceEstimate.FormatSize(shortfallBytes)));
        }

        /// <summary>
        /// 批末如实报告被空间门跳过的任务（不变量：不许静默跳过）。
        /// 一条汇总 + 逐个点名"需要多少、差多少、当时可用多少"。
        /// </summary>
        private void ReportSpaceBlockedTasks()
        {
            if (_spaceBlockedTasks.Count == 0)
            {
                return;
            }

            AppendLog(
                "WARN",
                $"空间不足，本批没做成 {_spaceBlockedTasks.Count} 个任务（一个字节都没动过）：");

            foreach ((string name, long required, long available, long shortfall) in _spaceBlockedTasks)
            {
                AppendLog(
                    "WARN",
                    $"  没做成：{name} —— 需要 {TaskSpaceEstimate.FormatSize(required)}，"
                    + $"当时可用 {(available < 0 ? "未知" : TaskSpaceEstimate.FormatSize(available))}，"
                    + $"差 {TaskSpaceEstimate.FormatSize(shortfall)}");
            }

            AppendLog(
                "WARN",
                "处理办法：清理「其余物」腾空间 / 换一个空间更大的输出盘 / "
                + "开①页那个「空间不足」模式（边解边删源包，每解完一个就收回一份空间；"
                + "⚠ 它会永久删除源包，只对这一次运行有效、不写设置）。"
                + (_spaceTightThisBatch ? "本批已经开着那个模式了 —— 那说明连它也不够：请换盘或先清理。" : string.Empty));
        }

        /// <summary>
        /// 批首写一条"这一批用的是哪四条安全上限"的日志（用户 2026-09-25 第 36 条）。
        ///
        /// <para>为什么要在**每一批**都写：那四条上限以前是硬编码的，用户被拦下时
        /// 手里只有一句"超过单文件上限 4 GiB"和满肚子疑问（"我这盘还有 47 GB，为什么不行"）。
        /// 现在它们既是设置项、又在这里当场报出来 —— 事后翻日志就能对上"是哪个上限拦的、当时设的是多少"。</para>
        /// </summary>
        private void LogExtractionLimitsForBatch()
        {
            AppendLog(
                "INFO",
                "安全上限（解压前预算）：" + BudgetLimits.Describe()
                + "。超过就不解这个包（预检拒绝，一个字节都不写）；"
                + "可在⑥设置 →「安全上限」里按自己的盘与资源改。");
        }

        /// <summary>
        /// 本批最多允许解几层（②页「最大嵌套层数」）。
        ///
        /// <para>⚠ 这个数**不是第二套轮数**：它就是 <c>OneClickCoordinator.RoundLimit</c> 读的那一个
        /// 设置项（`MaxRecursionDepth`，默认 7、夹在 1~10）。这里只把它取出来说明白"最多几层"，
        /// 绝不参与"实际跑几轮"的裁决（那个裁决只有 <c>OneClickCoordinator</c> 一处）。</para>
        /// </summary>
        private int ResolveMaxLayersForBatch() =>
            Math.Clamp(Settings.MaxRecursionDepth, 1, OneClickCoordinator.MaxRoundsCeiling);

        /// <summary>
        /// 本批**有没有可能**解出第二层：一键档按轮数上限（&gt;1 就可能续解），
        /// 手动档还多一条"递归模式没关"（手动路径的递归走 <c>RecursiveExtractor</c>）。
        /// </summary>
        private bool IsMultiLayerPossibleForBatch() =>
            ResolveMaxLayersForBatch() > 1;

        /// <summary>
        /// 「多层解压可能中途空间不足」的判据（**唯一出口**，批首日志与确认框都走它）。
        ///
        /// <para>输入全部来自**现成的估算口径**：各任务的
        /// <see cref="TaskSpaceEstimate.FreeSpaceDemandBytes"/> 之和（= 这次要从可用空间里新写多少，
        /// 已经含一层内层展开的增量）与排计划那一刻的可用空间。⛔ 不用 PeakBytes（含源包，
        /// 拿它比可用空间就是把源包算两遍 —— 2026-09-29 那场真机事故的成因）。</para>
        ///
        /// <para>它只是一句提醒：不拦任务、不改设置、不改勾选（放行仍然由每个任务开工前那道空间门决定）。</para>
        /// </summary>
        internal MultiLayerSpaceRisk EvaluateMultiLayerSpaceRisk(ExtractionSchedulePlan plan)
        {
            if (plan == null)
            {
                return new MultiLayerSpaceRisk { AvailableBytes = -1 };
            }

            return MultiLayerSpaceRiskRules.Evaluate(
                multiLayerPossible: IsMultiLayerPossibleForBatch(),
                maxLayers: ResolveMaxLayersForBatch(),
                totalDemandBytes: MultiLayerSpaceRiskRules.SumDemand(
                    plan.Ordered.Select(item => item.RequiredBytes)),
                availableBytes: plan.AvailableBytes);
        }

        /// <summary>那句话（日志与确认框**同一段文案**，免得两处说法分叉）。</summary>
        internal static string DescribeMultiLayerSpaceRisk(MultiLayerSpaceRisk risk)
        {
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.MultiLayerSpaceRiskFormat,
                risk.MaxLayers,
                TaskSpaceEstimate.FormatSize(risk.TotalDemandBytes),
                TaskSpaceEstimate.FormatSize(risk.AvailableBytes),
                TaskSpaceEstimate.FormatSize(risk.ShortfallBytes));
        }

        /// <summary>批首那一条 WARN（用户要求"开工前就要看得见"；不成立时一个字都不写）。</summary>
        private void LogMultiLayerSpaceRisk(ExtractionSchedulePlan plan)
        {
            MultiLayerSpaceRisk risk = EvaluateMultiLayerSpaceRisk(plan);

            if (!risk.Applies)
            {
                return;
            }

            AppendLog("WARN", DescribeMultiLayerSpaceRisk(risk));
        }

        /// <summary>
        /// 拿开工时申请的预留与精确需求对一次账：不够就加、多了就还。
        /// 加不上（盘已经不够了）时按「磁盘空间不足」处理 —— **这一步必须在真正写盘之前**。
        ///
        /// <para>⚠ 用的数字是 <see cref="TaskSpaceEstimate.FreeSpaceDemandBytes"/>（内容物 + 过程物），
        /// **不是** <see cref="TaskSpaceEstimate.PeakBytes"/>：源包已经在盘上、不在可用空间里，
        /// 拿峰值比可用空间就是把源包算两遍（2026-09-29 真机事故，见那个属性的说明）。</para>
        /// </summary>
        private SpaceGateDecision ReconcileReservation(ArchiveTask task, TaskSpaceEstimate refined)
        {
            SpaceReservationLedger? ledger = _spaceLedger;

            if (ledger == null)
            {
                // 没有账本（例如从别处直接调解压管线）：退化成一次直接判断，不假装知道并发情况。
                return SpaceGate.Check(
                    refined.FreeSpaceDemandBytes,
                    ProbeAvailableSpace(ResolveSpaceProbePathOrBatch(task)),
                    ReserveSpaceBytes,
                    0,
                    refined.ReclaimableBytes);
            }

            ScheduledTaskRuntime runtime = GetOrCreateRuntime(task);
            SpaceGateDecision decision = ledger.Adjust(
                runtime.ReservedBytes,
                refined.FreeSpaceDemandBytes,
                refined.ReclaimableBytes);

            if (decision.Allowed && !decision.ProbeFailed)
            {
                runtime.ReservedBytes = refined.FreeSpaceDemandBytes;
            }

            return decision;
        }


        /// <summary>本批探测可用空间用的路径（一次定住：同一批里所有判断都对着同一块盘，数字才可比）。</summary>
        private string _spaceProbePath = string.Empty;

        /// <summary>
        /// 可用空间探测（可注入）。
        ///
        /// <para>为什么必须留这个口子：真机上没法把盘写成只剩 3 MiB，而"空间不足就不启动"这条
        /// 是本批最核心的行为之一 —— 只靠读代码是证明不了的。单测用它造一块假盘。</para>
        /// </summary>
        internal Func<string, long?>? SpaceProbeOverride { get; set; }

        /// <summary>
        /// 要保留的余量（可注入）。默认取 <see cref="SpaceGate.DefaultReserveBytes"/>（512 MiB）。
        /// 单测里不调小它，任何几十字节的样本都会被那 512 MiB 拦下 —— 那样测的就不是被测逻辑了。
        /// </summary>
        internal long? SpaceReserveOverride { get; set; }

        private SpaceTrendMonitor? _spaceTrend;

        private CancellationTokenSource? _spaceTrendCts;

        /// <summary>
        /// 批末收工之后**还留着**的那个侦察器：等一键处理把其余物处理完，再补一针真实可用空间
        /// （见 <see cref="RecordSpaceTrendPostscript"/>）。它是**同一个**侦察器（同一个探测函数、
        /// 同一份采样表），不是第二套取数；一批只补一针，下一批开工时清掉。
        /// </summary>
        private SpaceTrendMonitor? _spaceTrendPostscript;

        private long ReserveSpaceBytes => SpaceReserveOverride ?? SpaceGate.DefaultReserveBytes;


        /// <summary>
        /// 解压前的资源预算上限（不变量 8），四条全部来自设置（用户 2026-09-25 第 36 条）。
        ///
        /// <para>为什么收口成一个属性：这套上限有**四个**用它的地方 —— ①解压前预检
        /// （<see cref="ResourceBudget.CheckBeforeExtract"/>）②产物事后核算 ③内嵌 ZIP 直读
        /// ④递归核心的累计上限。以前①③④各写各的默认值（预检与直读是 <c>ResourceBudgetOptions.Default</c>、
        /// 递归是 <c>RecursionLimits.Default</c> 的 50 GiB），用户在设置里改一处并不会同时生效 ——
        /// 现在只有这一个出口，口径不可能再分叉。</para>
        ///
        /// <para>旧写法：解压前预检直接用 <c>new ResourceBudget()</c>（= 硬编码 4 GiB 单文件 / 20 GiB 总量），
        /// 界面上一个字都没有 —— 他 12 GiB 的分卷包被拦下时看到的就是那句判决。</para>
        /// </summary>
        private ResourceBudgetOptions BudgetLimits => ResourceBudgetOptions.FromSettings(Settings);

        /// <summary>本批的空间预留账本（串行时也建，只是永远只有一个任务在账上）。</summary>
        private SpaceReservationLedger? _spaceLedger;

        private readonly object _spaceRuntimeLock = new();

        private readonly Dictionary<ArchiveTask, ScheduledTaskRuntime> _spaceRuntime = new();

        /// <summary>
        /// 空间不足：本批因此被跳过（没启动）的任务。
        /// 批末要**如实报告**它们各自需要多少 —— 静默跳过是明令禁止的。
        /// </summary>
        private readonly List<(string Name, long RequiredBytes, long AvailableBytes, long ShortfallBytes)> _spaceBlockedTasks = new();

        /// <summary>
        /// 本批那条"中途空间不足"的纯提示**弹过没有**（用户 2026-09-29 第 2 条：
        /// "同一批只弹一次（合并计数）"）。
        ///
        /// <para>后面的同类情况只写日志 —— 一批几十个包时逐个弹等于把批卡死在他面前。</para>
        /// </summary>
        private bool _spaceBlockedNoticeShown;

        /// <summary>
        /// 本批是不是按**「空间不足」模式**跑（批首定一次；用户 2026-09-27 拍板的模式）。
        ///
        /// <para>它是一个**运行期**开关（<see cref="MainViewModel.SpaceTightMode"/>，⛔ 不写设置、不记忆），
        /// 但一经开跑就**钉死在这一批上**：同一批里不许"前半段边解边删源包、后半段又不删了" ——
        /// 那会让空间账面与用户看到的行为前后矛盾。批首定一次、批内只读它。</para>
        ///
        /// <para>它一共改四件事（每一件都写在下面对应位置的注释里）：
        /// ① 并发档（忽略设置里的档与「全速」，按 <c>ExtractionScheduler.ResolveSpaceTightParallelCount</c>）；
        /// ② 排序（按净占用从小到大，先解"解完占地最少"的）；
        /// ③ 源包处理（✅ 定稿 + 校验通过 → **当场永久删除源包**，不进其余物）；
        /// ④ 删除操作（其余物 = 过程物，任务成功后彻底删除）。
        /// ⛔ 它**不动**任何设置：用户设置里的三档在这次运行里一个字节都不会被改写。</para>
        /// </summary>
        private bool _spaceTightThisBatch;

    }
}
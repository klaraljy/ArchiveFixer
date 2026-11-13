using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels
{
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

        internal OneClickRoundRunner(
            MainViewModel vm,
            ExtractionCoordinator extractionCoordinator,
            OneClickBatchFinalizer batchFinalizer)
        {
            _vm = vm;
            _extractionCoordinator = extractionCoordinator;
            _batchFinalizer = batchFinalizer;
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
    }
}

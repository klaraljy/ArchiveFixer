using ArchiveFixer.Models;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 一键处理的**轮循环**：把这一批从第一层一路解到轮数上限（每一轮 = 识别 → 解 → 找内层包 →
/// 第二大步 → 排下一轮），最后交出 <see cref="OneClickOutcome"/>。
///
/// <para>从 <c>OneClickCoordinator</c> 按职责搬出来：轮前准备 / 轮尾结论在
/// <see cref="OneClickRoundRunner"/>，第二大步在 <see cref="OneClickSecondStepRunner"/>，
/// 本类只做"轮与轮之间的编排"。⛔ 轮数上限仍然只有 <c>OneClickCoordinator.RoundLimit</c> 一个出口。</para>
///
/// <para>⚠ 它吃的是**已经准备好**的 <see cref="OneClickRunState"/>：特定解压那条早退仍在
/// <c>OneClickCoordinator.RunPipelineAsync</c>（那个分支要走 partial 里的私有实现，不搬）。</para>
/// </summary>
internal sealed class OneClickRoundLoop
{
    private readonly MainViewModel _vm;
    private readonly ExtractionCoordinator _extractionCoordinator;
    private readonly OneClickRoundRunner _roundRunner;
    private readonly OneClickSecondStepRunner _secondStepRunner;

    internal OneClickRoundLoop(
        MainViewModel vm,
        ExtractionCoordinator extractionCoordinator,
        OneClickRoundRunner roundRunner,
        OneClickSecondStepRunner secondStepRunner)
    {
        _vm = vm;
        _extractionCoordinator = extractionCoordinator;
        _roundRunner = roundRunner;
        _secondStepRunner = secondStepRunner;
    }

    /// <summary>
    /// 跑完这一批的所有轮次（<paramref name="state"/> 已由轮前准备填好目标与选项快照）。
    /// </summary>
    /// <param name="host">
    /// 一键处理协调器：轮内入口（扫描 / 补任务 / 取消勾选 / 轮数上限）仍然只有它一个出口，
    /// 与 <see cref="OneClickRoundRunner"/>、<see cref="OneClickSecondStepRunner"/> 的 host 口径一致。
    /// </param>
    /// <param name="runOptions">本次选项快照（一路传到最后一轮；null = 按设置走）。</param>
    internal async Task<OneClickOutcome> RunAsync(
        OneClickCoordinator host,
        OneClickRunState state,
        OneClickRunOptions? runOptions)
    {
        // 处理过的任务按轮累加：汇总要算"本次一共处理了多少个"，只算第一轮会和实际不符。
        state.Processed = new List<ArchiveTask>(state.RootTargets);

        // 源文件（含分卷各卷）不算内层包：不能把用户最初给的那个 .mp4 / .7z.001 又加一遍。
        state.SourcePaths = OneClickContinuationWorkbench.BuildSourcePathSet(state.RootTargets);

        state.RoundTargets = state.RootTargets.ToList();

        /*
         * 「用户按过停止后续」这件事必须单独记一笔。
         *
         * 只看 _vm.IsStopping 不行：ExtractionCoordinator 在批次开始和收尾时都会把它复位成 false，
         * 等我们拿回控制权时它已经是 false 了。只看"有没有任务没被处理"也不完全可靠：
         * 并发位被占着时批量循环还会再启动一个任务，未必留下未处理的任务。
         * 订阅属性变更拿到的是"用户确实按过"这个事实，不依赖上面两条时序。
         */
        void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsStopping) && _vm.IsStopping)
            {
                state.StopToken.RequestStop();
                state.StopRequested = true;
            }
        }

        _vm.PropertyChanged += OnViewModelPropertyChanged;

        // 这一趟的工作区跟踪从零开始：一键处理一条链会跨多轮，链尾统一收空壳。
        _extractionCoordinator.BeginWorkspaceRun();
        try
        {
            while (state.Round < host.RoundLimit)
            {
                state.Round++;

                var roundState = new OneClickRoundState();

                if (await _roundRunner.RunFirstStepAsync(host, state, roundState, runOptions).ConfigureAwait(true))
                {
                    break;
                }

                List<InnerArchiveCandidate> innerArchives = roundState.InnerArchives;
                List<string> scanLog = roundState.ScanLog;

                /*
                 * ⚠ **第二大步的轮边界：这一轮解开过的那几组卷，按删除档收掉**（2026-10-11，用户口径：
                 * 「**分卷记账是第二大步的事**」）。判据与执行体只有协调器那一个出口
                 * （`HarvestSolvedGroupVolumesForRound`：**入口包所在那一层** + 六道门槛 + 族尺子 + 用户源路径红线），
                 * ⛔ 这里不读任务表、⛔ 不看时序 —— 只把这一轮**判成是分卷**的那几个入口包交下去。
                 *
                 * ⚠ 位置必须在**轮判定这一行紧后面**：放到"有下一轮目标"那个块里 ⇒ 最后一轮（正是这一组解完的那一轮）
                 * 直接 `break` 跳过它（实测：EEE ③ 一点没变）。
                 */
                /*
                 * ===== 交界处消费端 · 第一段（只读）：用交接单做组分析 =====
                 *
                 * 用户 2026-10-24：交接处先只做「读」—— 交接单是事实来源（族/卷号/首卷/自述片数/自述整包），
                 * 这一遍只打 `[第二大步·组分析]` 事实行；⛔ 不点名、不搬片、不改名、不动盘。
                 * 组分析实现从 `SecondStepResidue/VolumeCandidateDiscovery.cs` 挑回来的（只读纯函数）。
                 */
                using SecondStepWorkScope secondStepWork = _extractionCoordinator.BeginSecondStepWork();

                if (await _secondStepRunner.RunAsync(host, state, roundState, innerArchives, scanLog, secondStepWork).ConfigureAwait(true))
                {
                    break;
                }

                if (await _roundRunner.RunRoundTailAsync(host, state, roundState, secondStepWork).ConfigureAwait(true))
                {
                    break;
                }
            }
        }
        finally
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            state.StopToken.Dispose();
        }

        return await _roundRunner.BuildOutcomeAsync(state).ConfigureAwait(true);
    }
}

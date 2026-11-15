using ArchiveFixer.BindingModels;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 一键处理的**批处理编排**（点一次 → 跑到批末结论）：确认流程 → 忙标志 → 跑管线 →
/// 过程物清理与结果展示 → 异常 / 取消收口。
///
/// <para>从 <c>OneClickCoordinator</c> 按职责搬出来：公开入口与"只处理勾选"那道守卫仍留在
/// 协调器（UI 边界不变），本类只负责"确认之后到批末"这一段编排。⛔ 判据与文案一个字不改。</para>
/// </summary>
internal sealed class OneClickBatchDriver
{
    private readonly MainViewModel _vm;
    private readonly ExtractionCoordinator _extractionCoordinator;
    private readonly DialogService _dialogService;
    private readonly OneClickConfirmFlow _confirmFlow;
    private readonly OneClickResultPresenter _resultPresenter;
    private readonly Func<IReadOnlyList<ArchiveTask>, OneClickRunOptions?, Task<OneClickOutcome>> _runPipeline;

    internal OneClickBatchDriver(
        MainViewModel vm,
        ExtractionCoordinator extractionCoordinator,
        DialogService dialogService,
        OneClickConfirmFlow confirmFlow,
        OneClickResultPresenter resultPresenter,
        Func<IReadOnlyList<ArchiveTask>, OneClickRunOptions?, Task<OneClickOutcome>> runPipeline)
    {
        _vm = vm;
        _extractionCoordinator = extractionCoordinator;
        _dialogService = dialogService;
        _confirmFlow = confirmFlow;
        _resultPresenter = resultPresenter;
        _runPipeline = runPipeline;
    }

    private ObservableCollection<ArchiveTask> Tasks => _vm.Tasks;

    private void AppendLog(string level, string message) => _vm.AppendLog(level, message);

    /*
     * 忙碌标志必须**成对进出**（MainViewModel.EnterBusy/ExitBusy 是嵌套计数）。
     *
     * 旧写法是直接 `IsBusy = true` / `finally IsBusy = false`：一键处理跑到一半会去调
     * RenameCoordinator / ExtractionCoordinator，而它们各自的 finally 也会把标志置回 false ——
     * 于是外层还在跑的时候「停止后续 / 取消当前」按钮就变灰了（它们只看 IsBusy），
     * 守卫跟着失效，用户能在间隙里再点一次解压，同一个包被解两遍。
     * 计数之后只有最外层退出才真正变成"不忙"，见 MainViewModel.EnterBusy。
     */
    private void EnterBusy() => _vm.EnterBusy();

    private void ExitBusy() => _vm.ExitBusy();

    /// <summary>
    /// 跑这一批（调用方已经确认过"有勾选任务"）。
    ///
    /// <para>⚠ 确认框的位置**必须在 <c>EnterBusy</c> 之前**（用户 2026-09-24 第 17 条），
    /// 见下面第一段注释。</para>
    /// </summary>
    internal async Task RunAsync(IReadOnlyList<ArchiveTask> targets)
    {
        /*
         * ===== 确认框的位置：**EnterBusy 之前**（用户 2026-09-24 第 17 条）=====
         *
         * 旧写法是「先 EnterBusy，再弹面板」，而主界面上那个进度条绑的正是 IsBusy
         * （MainWindow.xaml 里的不确定进度条 + 「正在处理」那两行文字的可见性）——
         * 于是用户还没点确认，进度条就已经在动了。用户原话：
         * 「为什么我一键处理还没有开始确认，你的进度条就开始动了」。
         *
         * 换到 EnterBusy 之前以后，"没确认 = 什么都没发生"在**状态上**也是真的：
         * IsBusy 还是 false、引擎一次都没被调用、输出目录一个字节都没写。
         * ⛔ 别把它挪回 EnterBusy 后面 —— 那正是用户点名的那条 bug。
         *
         * 另外两件事也在这段里定下来：
         * ① 提醒（无用物 / 没有可用密码）**并进这一个框**（PrepareBatchReminderForCallerAsync），
         *    整条一键处理（含续解的第 2/3 轮）不再弹第二个框 —— 用户要的是"只能有一个弹窗"；
         * ② 勾了「以后不再询问」就**一个框都不弹**（含那个提醒），直接按设置开始。
         */
        try
        {
            (bool proceed, OneClickRunOptions? runOptions) = await _confirmFlow
                .AskAsync(targets)
                .ConfigureAwait(true);

            if (!proceed)
            {
                return;
            }

            /*
             * ⛔ 2026-11-01（用户口径）：一键处理开工前**不再扫无用物**。
             * 无用物的自动剔除只发生在「选文件夹导入」那一次（见 ScanCoordinator.RemoveJunkTasksFromListAsync）：
             * 导入文件一般是用户点名的单个原包，解压过程中不应再动列表；手动清理走「移除勾选任务」。
             */

            EnterBusy();

            try
            {
                // 新的一批开始：上一次留下的"还剩 N 个内层包"提示先清掉（跑完按本次结论重设）。
                _vm.ReportPendingContinuation(0);

                OneClickOutcome outcome = await _runPipeline(targets, runOptions).ConfigureAwait(true);

                OneClickBatchFinalizer.CleanupSuccessfulProcessArtifacts(
                    Tasks,
                    message => AppendLog("INFO", message));

                _resultPresenter.Present(outcome);
            }
            finally
            {
                ExitBusy();
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("WARN", "一键处理被取消。");
        }
        catch (Exception ex)
        {
            AppendLog("ERROR", "一键处理失败：" + ex.Message);
            _vm.Logger.Exception("一键处理失败", ex);
            _dialogService.ShowException(ex, "一键处理失败");
        }
        finally
        {
            // 「这一次由调用方负责弹框」只管这一次一键处理：跑完（含取消）就收回默认口径。
            _extractionCoordinator.BatchReminders.EndCallerHandledReminderBatch();
        }
    }
}

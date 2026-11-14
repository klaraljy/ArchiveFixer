using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 一键处理的**批末结果**：把 <see cref="OneClickOutcome"/> 摆到用户眼前一次，并逐组写进日志。
///
/// <para>从 <c>OneClickCoordinator</c> 按职责搬出来：判定（严重度 / 诊断清单）在
/// <c>OneClickBatchFinalizer</c> 与 <c>BatchSummaryDiagnosticsRules</c> 里算，
/// 本类只负责"同一份结论既弹一次框、又写一遍日志"，⛔ 一个字都不重新判断。</para>
/// </summary>
internal sealed class OneClickResultPresenter
{
    private readonly MainViewModel _vm;
    private readonly DialogService _dialogService;

    internal OneClickResultPresenter(MainViewModel vm, DialogService dialogService)
    {
        _vm = vm;
        _dialogService = dialogService;
    }

    private void AppendLog(string level, string message) => _vm.AppendLog(level, message);

    /// <summary>
    /// 批末：挂起继续数 → 最终汇总框 → 汇总一行 → 诊断清单逐组。
    /// 顺序与文案与拆分前逐字相同（日志一条都不少）。
    /// </summary>
    internal void Present(OneClickOutcome outcome)
    {
        _vm.ReportPendingContinuation(outcome.PendingContinuationCount);

        /*
         * 批末的**最终汇总框**（用户 2026-09-27 第二次拍板："最终汇总框可以留、其他都删"）。
         *
         * 他上一次（同一天）说的是"一键处理期间一个弹窗都不弹"，因为那一批被 8–9 个
         * 「确认」框打断（多分支递归各弹一次）—— 那些才是要删的。**批已经跑完**之后这一个
         * 汇总是另一回事：那时他已经回来（不回来看不见"跑完了"），而且他点掉它就行，
         * 不点也不会卡住任何任务（所有任务都已经结束、工作区都已收尾）。
         *
         * ⛔ 判定与日志一条都没少：下面那一行 `一键处理汇总：…` 照旧写进日志，
         * 这个框只是把同一份 <see cref="OneClickOutcome.Summary"/> 摆到他眼前一次。
         * ⛔ 也**只留这一个** —— 批中间那些"要用户回答"的框一律还是保守档 + 日志。
         *
         * ⚠ 2026-09-29 第 3 条：这个框按**机器终态**分三档配色（蓝 / 橙 / 红顶部色带，
         * 正文白底黑字）。严重度由 <see cref="OneClickOutcome.Severity"/> 带过来 ——
         * 窗口一个字都不判断（见 BatchSummarySeverityRules 的说明）。
         *
         * ⚠ 2026-09-30 第 1 条：正文不只是那一行数字了 —— 还要像编译器那样**点名说清错在哪**
         * （空间不足差多少 / 哪些包可能是密码不对 / 111.7z.001 缺哪几卷…）。
         * 那份清单与严重度出自同一次调用（见 <c>RunPipelineAsync</c> 里
         * <c>BatchSummaryDiagnosticsRules.Build</c> 那一行），这里只是把它摆出来。
         */
        _dialogService.ShowBatchSummary(outcome.DialogMessage, outcome.Severity);

        AppendLog("INFO", "一键处理汇总：" + outcome.Summary.Replace(Environment.NewLine, "；"));

        /*
         * 那份清单同时逐组写进日志：用户不看日志，但**我们**要靠它排障 ——
         * 而且真机反馈回来时，"框里写了什么"与"日志里写了什么"必须是同一份文字。
         * 有问题才写（全成功那一批一个字都不加），组数与行数由清单本身封顶。
         */
        foreach (string line in outcome.DiagnosticLines)
        {
            AppendLog(
                outcome.Severity == BatchSummarySeverity.Failed ? "WARN" : "INFO",
                StatusText.BatchDiagnosticsLogPrefix + line);
        }
    }
}

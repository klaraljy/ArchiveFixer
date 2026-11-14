using ArchiveFixer.Models;
using ArchiveFixer.Packing;
using ArchiveFixer.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// ⑤打包页的「执行」那一组：这一次运行的状态（跑没跑 / 进度 / 当前步骤 / 取消令牌 / 本次密码）
/// 与真跑的流程（校验 → 小确认弹窗 → 写设置 → 调服务 → 回填结果）。
///
/// <para>⛔ 顺序是硬要求：用户确认之前**一个字节都不许动**（连输出文件夹都不建）；
/// 取消 = 什么都没发生（设置也不写回，因为他没确认）。"本次操作开始"那条标记同理 ——
/// 只在**用户确认之后、调服务之前**打一次，取消时一次都不许打。</para>
/// </summary>
internal sealed class PackingRunCoordinator
{
    private readonly PackingViewModel _vm;
    private readonly PackingOptionsCoordinator _options;
    private readonly PackingSummaryComposer _summary;
    private readonly PackingService _service;
    private readonly DialogService _dialogService;
    private readonly PathService _pathService;
    private readonly ClipboardService _clipboardService;

    /// <summary>本次运行的那一套选择（落点 / 原包 / 其余物），由小确认弹窗回填。</summary>
    private PackingRunOptions _runOptions = new();

    private CancellationTokenSource? _cancellation;

    private string _lastUsedPassword = string.Empty;

    internal PackingRunCoordinator(
        PackingViewModel viewModel,
        PackingOptionsCoordinator options,
        PackingSummaryComposer summary,
        PackingService service,
        DialogService dialogService,
        PathService pathService,
        ClipboardService clipboardService)
    {
        _vm = viewModel;
        _options = options;
        _summary = summary;
        _service = service;
        _dialogService = dialogService;
        _pathService = pathService;
        _clipboardService = clipboardService;
    }

    /// <summary>本次打包用过的密码还在内存里（「复制密码」按钮据此可用）。</summary>
    internal bool CanCopyPassword => PackingPasswordPolicy.IsUsable(_lastUsedPassword);

    /// <summary>
    /// 开始打包（用户 2026-09-26 第 46 条的流程）：
    /// **先弹小确认弹窗**（落点 / 原包操作 / 其余物操作）→ 用户确认 → 才动任何字节。
    /// </summary>
    internal async Task StartAsync()
    {
        if (_vm.IsRunning)
        {
            return;
        }

        _vm.ClearNotice();
        _vm.ClearResult();

        string inputError = _options.ValidateStart();

        if (inputError.Length > 0)
        {
            _vm.Warn(inputError);
            return;
        }

        /*
         * 先用**页面上那一套**（落点由⑤页选、两档操作由设置记得的那一套）算一遍规划，
         * 弹窗要把"最终产物在哪"显示出来。
         *
         * ⚠ 2026-09-26 追加改口径（用户原话："选择还是得放在页面"）：落点的**选择**在⑤页，
         * 弹窗里那块只**显示**（不再有本地/指定位置的单选）。
         */
        PackingRunOptions initial = _vm.BuildPlacementOptions();

        if (!PackingPlan.TryCreate(_options.BuildRequest(initial), out PackingPlan? previewPlan, out string planError)
            || previewPlan == null)
        {
            _vm.Warn(planError);
            return;
        }

        // ───────── 小确认弹窗（取消 = 什么都不做）─────────
        //
        // ⚠ 弹窗里那句"最终产物"必须按**实际生效的容器**写（没装 WinRAR 时是 `.7z`）。
        PackingRunOptions? confirmed = _dialogService.ShowPackingConfirm(
            PackingConfirmRequest.FromPlan(previewPlan, initial, _vm.EffectiveContainer));

        if (confirmed == null)
        {
            _vm.Info("已取消：什么都没做（源与其余物一个字节都没动）。");
            return;
        }

        /*
         * 只有弹窗里那**两档操作**来自弹窗；落点始终是⑤页上那一套（⛔ 弹窗不许偷偷改落点）。
         * 合并之后写回设置（"相应的保存记忆操作"）。
         */
        PackingRunOptions merged = _options.MergeConfirmed(initial, confirmed);

        _options.SaveConfirmed(merged);

        _runOptions = merged;

        if (!PackingPlan.TryCreate(_options.BuildRequest(merged), out PackingPlan? plan, out string error) || plan == null)
        {
            _vm.Warn(error);
            return;
        }

        _lastUsedPassword = _vm.Password;
        _vm.NotifyCanCopyPasswordChanged();

        // 到这一步才真的开始干活：给操作日志打一条"本次操作开始：打包"
        // （⛔ 取消 / 规划被拒时一次都不许触发 —— 这条也钉在用例里）。
        _vm.OperationStarted?.Invoke();

        _vm.IsRunning = true;
        _vm.ProgressPercent = 0;
        _vm.CurrentStepText = StatusText.PackStepPreparing;

        _cancellation = new CancellationTokenSource();

        Progress<PackingProgress> progress = new(report =>
        {
            _vm.CurrentStepText = report.StepText;

            if (report.Percent >= 0)
            {
                _vm.ProgressPercent = report.Percent;
            }

            _vm.ProgressDetailText = report.Detail;
        });

        PackingRequest request = _options.BuildRequest(merged);

        PackingResult result;

        try
        {
            result = await _service.PackAsync(request, _vm.LogSink, progress, _cancellation.Token);
        }
        catch (Exception ex)
        {
            // 引擎层已经把可预期的失败都收成了结果；这里兜的是"连结果都没拿到的意外"。
            result = new PackingResult
            {
                State = PackingState.Failed,
                Message = StatusText.PackFailed,
                FailureReason = "打包过程中出现意外错误：" + PackProcessRunner.DescribeTool(PackToolKind.SevenZip)
                                 + " 调用链抛异常 —— "
                                 + PackPasswordGuard.Sanitize(ex.Message, request.Password)
            };
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            _vm.IsRunning = false;
        }

        _summary.ApplyResult(result, plan);
    }

    /// <summary>取消（只杀自己启动的那个 PID 及其子进程）。</summary>
    internal void Cancel()
    {
        try
        {
            _cancellation?.Cancel();
            _vm.CurrentStepText = StatusText.PackCancelled;
        }
        catch
        {
            // 令牌可能已经释放：取消是个"尽力而为"的动作，不抛给界面。
        }
    }

    /// <summary>把本次用的密码复制到剪贴板（用户自己也要用）。</summary>
    internal void CopyPassword()
    {
        if (!CanCopyPassword)
        {
            _vm.Warn("这次还没有设置过密码，没有可复制的内容。");
            return;
        }

        /*
         * ⚠ 这里**刻意不过 PasswordMasker.Sanitize**：它会把长得像 "password: xxx" 的密码
         * 整段换成占位符，用户复制到的就不是自己的密码了。密码进剪贴板是用户主动点的，
         * 也正是设计里给的出口（docs/打包功能.md §3）。
         */
        if (_clipboardService.CopyText(_lastUsedPassword))
        {
            _vm.Info("密码已复制到剪贴板（请自行妥善保存；程序不落盘、日志里也不记）。");
        }
        else
        {
            _vm.Warn("复制到剪贴板失败（剪贴板被别的程序占着？）。密码仍然留在本窗口内存里。");
        }
    }

    /// <summary>打开输出文件夹（优先打开结果所在的那一层）。</summary>
    internal void OpenOutputFolder()
    {
        string? directory = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(_vm.ResultOuterPath))
            {
                directory = Path.GetDirectoryName(_vm.ResultOuterPath);
            }

            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = _vm.OutputFolder;
            }

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                _vm.Warn("输出文件夹还不存在：" + (directory ?? string.Empty));
                return;
            }

            _pathService.OpenDirectory(directory);
        }
        catch (Exception ex)
        {
            _vm.Warn("打开输出文件夹失败：" + PackPasswordGuard.Sanitize(ex.Message, null));
        }
    }
}

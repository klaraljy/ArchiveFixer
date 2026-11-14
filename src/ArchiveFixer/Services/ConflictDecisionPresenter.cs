using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ArchiveFixer.Services;

/// <summary>
/// 同名冲突询问（从 <see cref="DialogService"/> 按职责拆出的协作类）：
/// **一次问清"这一个 / 整批"怎么处理**（照 WinRAR 六档的精神，按批量场景裁剪）。
///
/// <para>
/// 六个选项 = 三个按钮 × 勾选框两态：
/// 「覆盖 / 跳过 / 自动重命名」与「覆盖全部 / 跳过全部 / 全部自动重命名」；
/// 直接关掉窗口（标题栏 ✕）= **取消本批**（等价于既有取消语义）。
/// 为什么是 3 个按钮：自绘对话框最多同时放三个按钮（<c>Views/AppDialogWindow</c> 不归本轮改动管），
/// 用"勾选框 = 全部"正好覆盖 WinRAR 六档的语义，而且比六个按钮更好读。
/// </para>
///
/// <para>
/// <b>线程与降级</b>（与值返回型对话框同一口径，历史卡死就出在这一段）：
/// </para>
/// <list type="bullet">
/// <item><description><b>无 UI 宿主</b>（<c>Application.Current == null</c>，单元测试 / 控制台宿主）：
/// 写一条降级日志并返回 <c>null</c> —— **不弹窗、不死等**，调用方按保守档（自动重命名，绝不覆盖）继续。</description></item>
/// <item><description><b>UI 线程</b>：直接 <c>ShowDialog()</c>（内部是嵌套消息泵，界面照常刷新）。</description></item>
/// <item><description><b>后台线程</b>：<c>Dispatcher.InvokeAsync</c>（**不是**同步 <c>Invoke</c>）+
/// 有界等待；等待期间**响应取消令牌**（用户点了取消就把窗口关掉并返回 null）。</description></item>
/// </list>
///
/// <para>
/// 返回 <c>null</c> 的语义是"没问到答案"（无界面宿主 / 被取消 / 超时）：
/// 调用方**必须**按保守档处理，绝不允许把它当成"用户同意覆盖"。
/// </para>
/// </summary>
internal static class ConflictDecisionPresenter
{
    /// <summary>勾上它 = 对后续所有同名冲突都照此办理（本批不再询问）。决策 D-4 的前提就是这一勾。</summary>
    private const string ApplyToAllText = "对后面所有同名冲突都照此办理（本批不再询问）";

    /// <summary>当前正显示着的冲突询问窗口（取消时要能把它关掉，否则用户会对着一个已经作废的框点）。</summary>
    private static readonly object ActiveDialogLock = new();

    private static IAppDialogWindow? _activeDialog;

    /// <summary>
    /// 显示同名冲突询问并拿回答案；无界面宿主 / 取消 / 超时 / 失败一律返回 <c>null</c>。
    /// </summary>
    internal static Task<ConflictDecision?> ShowAsync(
        DialogService.ConflictPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        prompt ??= new DialogService.ConflictPrompt();

        Dispatcher? dispatcher = DialogUiThread.TryGetDispatcher();

        if (dispatcher == null)
        {
            DialogFallbackLog.Record(
                "ShowConflictDecision",
                prompt.Message,
                "当前宿主没有 WPF 界面，同名冲突询问无法进行，已按保守档（自动重命名，绝不覆盖）处理");

            return Task.FromResult<ConflictDecision?>(null);
        }

        try
        {
            if (dispatcher.CheckAccess())
            {
                return Task.FromResult(ShowModal(prompt));
            }

            DispatcherOperation<ConflictDecision?> operation =
                dispatcher.InvokeAsync(() => ShowModal(prompt));

            return AwaitDialogAsync(operation, prompt, cancellationToken);
        }
        catch (Exception ex)
        {
            DialogFallbackLog.Record("ShowConflictDecision", prompt.Message, "显示同名冲突询问失败：" + ex.Message);
            return Task.FromResult<ConflictDecision?>(null);
        }
    }

    /// <summary>
    /// 六档询问的**答案映射**（唯一实现处；抽成纯函数是为了能脱离窗口单测）。
    ///
    /// 六个选项 = 三个按钮 × 勾选框两态：
    /// Yes=覆盖、No=跳过、Cancel=自动重命名，勾选框 = 对整批都照此办理；
    /// ✕ 关窗（<paramref name="dialogResult"/> 为 null —— 两条按钮路径都会显式写 DialogResult）
    /// = **取消本批**，勾选框不改变这个结论。
    /// </summary>
    internal static ConflictDecision MapAnswer(
        MessageBoxResult result,
        bool? dialogResult,
        bool applyToAll)
    {
        switch (result)
        {
            case MessageBoxResult.Yes:
                return ScopeChoice(ConflictChoice.Overwrite, applyToAll);

            case MessageBoxResult.No:
                return ScopeChoice(ConflictChoice.Skip, applyToAll);

            case MessageBoxResult.Cancel when dialogResult is null:
                return ConflictDecision.Once(ConflictChoice.CancelBatch);

            case MessageBoxResult.Cancel:
                return ScopeChoice(ConflictChoice.AutoRename, applyToAll);

            default:
                // 未知结果（理论上到不了）：按最保守的"取消本批"处理，绝不落到"覆盖"。
                return ConflictDecision.Once(ConflictChoice.CancelBatch);
        }
    }

    /// <summary>
    /// 后台线程那条路：拿答案，但**绝不无限等**，也绝不无视取消令牌。
    /// </summary>
    private static async Task<ConflictDecision?> AwaitDialogAsync(
        DispatcherOperation<ConflictDecision?> operation,
        DialogService.ConflictPrompt prompt,
        CancellationToken cancellationToken)
    {
        Task<ConflictDecision?> dialog = operation.Task;

        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using CancellationTokenRegistration registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => cancelled.TrySetResult(true))
            : default;

        // 有界等待：对话框先有结果就把这个计时器取消掉，别让一个 5 分钟的定时器白挂着。
        using var timeoutCts = new CancellationTokenSource(DialogUiThread.BackgroundWaitTimeout);
        Task timeout = Task.Delay(DialogUiThread.BackgroundWaitTimeout, timeoutCts.Token);

        Task finished = await Task.WhenAny(dialog, cancelled.Task, timeout).ConfigureAwait(false);

        if (finished == dialog)
        {
            return await dialog.ConfigureAwait(false);
        }

        // 取消 / 超时：把窗口关掉（它已经作废了），返回 null 让调用方走保守档。
        CloseActiveDialog(operation.Dispatcher);

        DialogFallbackLog.Record(
            "ShowConflictDecision",
            prompt.Message,
            finished == cancelled.Task
                ? "同名冲突询问期间被取消，已按保守档处理（绝不覆盖）"
                : $"等待用户响应超过 {DialogUiThread.BackgroundWaitTimeout.TotalMinutes:0} 分钟，已按保守档处理（绝不覆盖）");

        return null;
    }

    /// <summary>
    /// 在 UI 线程上显示冲突询问并读回答案。
    ///
    /// 六个选项的映射：Yes=覆盖、No=跳过、Cancel=自动重命名，勾选框 = 全部；
    /// ✕ 关窗时 <c>Result</c> 停在默认的 Cancel 而
    /// <c>DialogResult</c> **始终是 null**（按钮那条路会显式把它置成 false）——
    /// 这就是"取消本批"与"自动重命名"的区分点。
    /// </summary>
    private static ConflictDecision? ShowModal(DialogService.ConflictPrompt prompt)
    {
        var request = new AppDialogRequest
        {
            Title = "同名冲突",
            Message = prompt.Message,
            Subtitle = prompt.Subtitle,
            Detail = prompt.Detail,
            Icon = AppDialogIcon.Question,
            Buttons = AppDialogButtons.YesNoCancel,
            Destructive = prompt.Destructive,
            YesText = "覆盖",
            NoText = "跳过",
            CancelText = "自动重命名",
            OptionText = ApplyToAllText,
            OptionChecked = false
        };

        IAppDialogWindow? window = DialogWindowFactories.AppDialog?.Invoke(request);

        if (window == null)
        {
            DialogFallbackLog.Record("ShowConflictDecision", prompt.Message, "宿主没有注册对话框窗口实现，已按保守档处理（绝不覆盖）");
            return null;
        }

        window.AttachOwner();

        /*
         * 默认按钮（回车）不能是「覆盖」：AppDialogWindow 对 YesNoCancel 一律把 Yes 设为默认，
         * 而这里的 Yes 正是危险动作「覆盖」。模态框可能在用户打字时弹出来，回车即覆盖
         * 等于把"默认不得覆盖"（不变量 3）做反了。所以显式把默认按钮换成「跳过」。
         */
        window.ApplyConflictDefaults();

        lock (ActiveDialogLock)
        {
            _activeDialog = window;
        }

        try
        {
            window.ShowDialog();
        }
        finally
        {
            lock (ActiveDialogLock)
            {
                _activeDialog = null;
            }
        }

        bool applyToAll = window.IsOptionChecked;

        return MapAnswer(window.Result, window.DialogResult, applyToAll);
    }

    private static ConflictDecision ScopeChoice(ConflictChoice choice, bool applyToAll) =>
        applyToAll ? ConflictDecision.ForAll(choice) : ConflictDecision.Once(choice);

    /// <summary>把已经作废的冲突询问窗口关掉（必须在 UI 线程上做）。</summary>
    private static void CloseActiveDialog(Dispatcher dispatcher)
    {
        try
        {
            dispatcher.BeginInvoke(
                new Action(() =>
                {
                    IAppDialogWindow? window;

                    lock (ActiveDialogLock)
                    {
                        window = _activeDialog;
                    }

                    try
                    {
                        window?.Close();
                    }
                    catch
                    {
                        // 关不掉也只是留一个框，绝不允许把降级路径变成新的失败。
                    }
                }));
        }
        catch
        {
            // 投递失败同上：降级路径不允许再制造失败。
        }
    }
}

using System;
using System.Windows;
using System.Windows.Threading;

namespace ArchiveFixer.Services;

/// <summary>
/// 对话框服务的线程模型与有界等待（从 <see cref="DialogService"/> 按职责拆出的协作类）。
///
/// <para>这是<b>硬约束</b>的唯一实现处（历史上"卡死"就出在这一段），所有对话框都从这里派发：</para>
/// <list type="bullet">
/// <item><description><b>无 UI 宿主</b>（<c>Application.Current == null</c>，单元测试 / 控制台宿主）：
/// 写一条降级记录并返回默认值 —— 不弹窗、不等待、不抛异常。</description></item>
/// <item><description><b>UI 线程</b>：直接显示（<c>ShowDialog()</c> 自己跑嵌套消息泵，界面照常刷新）。</description></item>
/// <item><description><b>后台线程</b>：值返回型用 <c>Dispatcher.InvokeAsync</c>（**不是**同步 <c>Invoke</c>，
/// 那会阻塞调用线程并放大死锁）派发，并带**有界等待**；通知型用 <c>BeginInvoke</c> 投递即返回。</description></item>
/// </list>
///
/// <para>三档降级方向不同，所以它们是三个入口而不是一个泛化入口：
/// 值返回型（<see cref="RunValueDialog{T}"/>）超时/失败按默认值（破坏性分支一律"不做"）；
/// 通知型（<see cref="RunNotification"/>）只记录、不阻塞；
/// 文件选择（<see cref="RunFileDialog{T}"/>）返回空结果。任何一步失败都只写日志，不向上抛。</para>
/// </summary>
internal static class DialogUiThread
{
    /// <summary>
    /// 后台线程等待用户点击对话框的上限。
    ///
    /// <para>为什么有上限：值返回的 API 是同步的（要拿用户的答案），后台线程必须等；
    /// 万一 UI 线程已经忙死或已开始关闭，**不能无限等**（那就是"死等"）。
    /// 超时后按"用户没有确认"处理并写日志 —— 与确认框返回 false 的语义一致。</para>
    /// </summary>
    internal static TimeSpan BackgroundWaitTimeout { get; } = TimeSpan.FromMinutes(5);

    /// <summary>取当前进程的 UI 调度器；没有宿主 / 已在关闭 ⇒ <c>null</c>（= 走降级）。</summary>
    internal static Dispatcher? TryGetDispatcher()
    {
        Application? app = Application.Current;

        if (app == null)
        {
            return null;
        }

        Dispatcher? dispatcher = app.Dispatcher;

        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return null;
        }

        return dispatcher;
    }

    /// <summary>
    /// 值返回型对话框：拿用户的答案。无宿主 / 超时 / 显示失败一律返回 <paramref name="fallback"/>。
    /// </summary>
    internal static T RunValueDialog<T>(
        AppDialogRequest request,
        Func<IAppDialogWindow, T> read,
        string context,
        T fallback)
    {
        Dispatcher? dispatcher = TryGetDispatcher();

        if (dispatcher != null && DialogWindowFactories.AppDialog == null)
        {
            DialogFallbackLog.Record(context, request.Message, "宿主没有注册对话框窗口实现，已按无界面宿主降级");
            return fallback;
        }

        if (dispatcher == null)
        {
            DialogFallbackLog.Record(context, request.Message, "当前宿主没有 WPF 界面，已按默认值处理");
            return fallback;
        }

        try
        {
            if (dispatcher.CheckAccess())
            {
                // UI 线程：ShowDialog 自己跑嵌套消息泵，界面不会卡住。
                return AppDialogWindowHost.ShowModal(request, read);
            }

            // 后台线程：InvokeAsync（不是同步 Invoke，那会阻塞调用线程并放大死锁）+ 有界等待。
            DispatcherOperation<T> operation = dispatcher.InvokeAsync(() => AppDialogWindowHost.ShowModal(request, read));

            if (operation.Task.Wait(BackgroundWaitTimeout))
            {
                return operation.Task.GetAwaiter().GetResult();
            }

            DialogFallbackLog.Record(
                context,
                request.Message,
                $"等待用户响应超过 {BackgroundWaitTimeout.TotalMinutes:0} 分钟，已按默认值处理");
            return fallback;
        }
        catch (Exception ex)
        {
            DialogFallbackLog.Record(context, request.Message, "显示对话框失败：" + ex.Message);
            return fallback;
        }
    }

    /// <summary>
    /// 通知型对话框（没有返回值）：后台线程投递即返回，不阻塞调用方。
    /// </summary>
    /// <param name="modeless">
    /// true = 用**非模态**窗口（<c>Show()</c>）—— 调用方在跑批，不能被一个提示卡住
    /// （见 <see cref="DialogService.ShowSpaceBlockedNotice"/>）。false = 原来的模态 <c>ShowDialog()</c>。
    ///
    /// <para>两者在**后台线程**上的行为一致：都是 <c>BeginInvoke</c> 投递完立刻返回 ——
    /// 非模态那一档真正解决的是"**UI 线程**上调用时不卡住批处理"。</para>
    /// </param>
    internal static void RunNotification(AppDialogRequest request, string context, bool modeless = false)
    {
        Dispatcher? dispatcher = TryGetDispatcher();

        if (dispatcher == null)
        {
            DialogFallbackLog.Record(context, request.Message, "当前宿主没有 WPF 界面，仅记录日志");
            return;
        }

        if (dispatcher.CheckAccess())
        {
            try
            {
                ShowNotificationOnUiThread(request, modeless);
            }
            catch (Exception ex)
            {
                DialogFallbackLog.Record(context, request.Message, "显示对话框失败：" + ex.Message);
            }

            return;
        }

        try
        {
            dispatcher.BeginInvoke(
                new Action(() =>
                {
                    try
                    {
                        ShowNotificationOnUiThread(request, modeless);
                    }
                    catch (Exception ex)
                    {
                        DialogFallbackLog.Record(context, request.Message, "显示对话框失败：" + ex.Message);
                    }
                }));
        }
        catch (Exception ex)
        {
            DialogFallbackLog.Record(context, request.Message, "投递对话框失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 文件选择类对话框同样要能在无 UI 宿主下安全降级（测试会构造本服务）。
    /// 与值返回型同一套有界等待，但降级记录用"文件对话框"口径、超时不额外记一条。
    /// </summary>
    internal static T RunFileDialog<T>(Func<T> show, string context, T fallback)
    {
        Dispatcher? dispatcher = TryGetDispatcher();

        if (dispatcher == null)
        {
            DialogFallbackLog.Record(context, "(文件对话框)", "当前宿主没有 WPF 界面，返回空结果");
            return fallback;
        }

        try
        {
            if (dispatcher.CheckAccess())
            {
                return show();
            }

            DispatcherOperation<T> operation = dispatcher.InvokeAsync(show);

            return operation.Task.Wait(BackgroundWaitTimeout)
                ? operation.Task.GetAwaiter().GetResult()
                : fallback;
        }
        catch (Exception ex)
        {
            DialogFallbackLog.Record(context, "(文件对话框)", "显示失败：" + ex.Message);
            return fallback;
        }
    }

    private static void ShowNotificationOnUiThread(AppDialogRequest request, bool modeless)
    {
        if (modeless)
        {
            AppDialogWindowHost.ShowModeless(request);
        }
        else
        {
            AppDialogWindowHost.ShowModal(request, window => window.Result);
        }
    }
}

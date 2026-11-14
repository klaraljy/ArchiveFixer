using ArchiveFixer.Models;
using ArchiveFixer.Password;
using System;

namespace ArchiveFixer.Services;

/// <summary>
/// 提示类对话框（从 <see cref="DialogService"/> 按职责拆出的协作类）：
/// 信息 / 警告 / 错误 / 异常 / 批末汇总 / 空间提示。
///
/// <para>共同点：**只告诉用户一件事，不拿答案**，所以全部走通知型派发
/// （<see cref="DialogUiThread.RunNotification"/>：UI 线程直调、后台线程投递即返回）；
/// 只有"空间不足拦下一个任务"那一档用非模态窗口，因为它出现在跑批中间，
/// ⛔ 不许要用户点一下才能继续。</para>
///
/// <para>正文与明细原样传递：判定（例如批末汇总的红/橙/蓝）不在这里做，
/// 也不许靠中文文案去猜 —— 严重度由调用方按既有唯一出口算好传进来。</para>
/// </summary>
internal static class InformationDialogPresenter
{
    /// <summary>信息提示。</summary>
    internal static void ShowInfo(string message)
    {
        DialogUiThread.RunNotification(
            new AppDialogRequest
            {
                Title = "提示",
                Message = message,
                Icon = AppDialogIcon.Info,
                Buttons = AppDialogButtons.Ok
            },
            "ShowInfo");
    }

    /// <summary>警告提示。</summary>
    internal static void ShowWarning(string message)
    {
        DialogUiThread.RunNotification(
            new AppDialogRequest
            {
                Title = "警告",
                Message = message,
                Icon = AppDialogIcon.Warning,
                Buttons = AppDialogButtons.Ok
            },
            "ShowWarning");
    }

    /// <summary>
    /// **批末汇总**（用户 2026-09-29：这个框要按结果分颜色）。
    ///
    /// <para>与 <see cref="ShowInfo"/> 的唯一区别是 <paramref name="severity"/>：
    /// 窗口按它在顶部放一条色带（蓝 = 全成功 / 橙 = 有部分完成或跳过 / 红 = 有失败），
    /// 正文照旧白底黑字。判定**不在这里**（⛔ 对话框不许读中文状态去猜），
    /// 它来自 <see cref="BatchSummarySeverityRules"/> 那一个出口。</para>
    /// </summary>
    internal static void ShowBatchSummary(string message, BatchSummarySeverity severity)
    {
        DialogUiThread.RunNotification(
            new AppDialogRequest
            {
                Title = severity switch
                {
                    BatchSummarySeverity.Failed => "一键处理汇总（有失败）",
                    BatchSummarySeverity.Partial => "一键处理汇总（有没做成的）",
                    _ => "一键处理汇总"
                },

                /*
                 * 图标跟着严重度走：它同时决定这个框的**提醒强度**
                 * （AppDialogWindow.ResolveAttention：Warning / Error = 强提醒，Info = 响一声）。
                 * 一批全成功还要"闪 + 连响三声"就是骚扰。
                 */
                Icon = severity switch
                {
                    BatchSummarySeverity.Failed => AppDialogIcon.Error,
                    BatchSummarySeverity.Partial => AppDialogIcon.Warning,
                    _ => AppDialogIcon.Info
                },
                Message = message,
                Buttons = AppDialogButtons.Ok,
                SummarySeverity = severity
            },
            "ShowBatchSummary");
    }

    /// <summary>
    /// **中途因空间不足被拦下**的那一次提示（用户 2026-09-29 第 2 条）。
    ///
    /// <para>它必须是**纯提示、一个按钮、不阻塞后续任务**：一键处理的红线是"批中间不许要用户点一下
    /// 才能继续"，而空间门拦下单个任务之后**整批照常在跑**（不变量 9：单个任务失败不中断整批）。
    /// 所以这里走**非模态**窗口（<c>Show()</c>，不是 <c>ShowDialog()</c>）——
    /// 窗口浮在那儿让他看得见，批一秒都不停。调用方负责"同一批只弹一次"（合并计数）。</para>
    /// </summary>
    internal static void ShowSpaceBlockedNotice(string message)
    {
        DialogUiThread.RunNotification(
            new AppDialogRequest
            {
                Title = "磁盘空间不足",
                Message = message,
                Icon = AppDialogIcon.Warning,
                Buttons = AppDialogButtons.Ok
            },
            "ShowSpaceBlockedNotice",
            modeless: true);
    }

    /// <summary>
    /// **空间体检**那一个警告框（用户 2026-09-27："还可以弄一个弹窗，因为这个比较危险"）。
    ///
    /// <para>与 <see cref="ShowWarning"/> 的区别只有一个，但很关键：正文之外还给一块
    /// <b>Detail</b>（等宽、可滚动、可复制）逐条列出"哪些包放不下、各差多少" ——
    /// 那些名字与数字塞进正文会把框撑高到看不完，而它们正是用户要照着清理的清单。</para>
    /// </summary>
    internal static void ShowSpaceShortageWarning(string message, string detail)
    {
        DialogUiThread.RunNotification(
            new AppDialogRequest
            {
                Title = "空间不足",
                Message = message,
                Detail = detail ?? string.Empty,
                Icon = AppDialogIcon.Warning,
                Buttons = AppDialogButtons.Ok
            },
            "ShowSpaceShortageWarning");
    }

    /// <summary>错误提示。</summary>
    internal static void ShowError(string message)
    {
        DialogUiThread.RunNotification(
            new AppDialogRequest
            {
                Title = "错误",
                Message = message,
                Icon = AppDialogIcon.Error,
                Buttons = AppDialogButtons.Ok
            },
            "ShowError");
    }

    /// <summary>
    /// 异常提示。
    ///
    /// <para>⚠ 正文与明细都过 <see cref="PasswordMasker.Sanitize"/>：异常文本可能夹带命令行片段
    /// （7z 的 <c>-p&lt;明文密码&gt;</c> 就写在参数里，异常消息经常把整条命令带出来），
    /// 弹窗与日志同一口径 —— 密码只存内存，任何出口都不得出现明文（AGENTS.md §6 第 5 条）。</para>
    /// </summary>
    internal static void ShowException(Exception ex, string prefix)
    {
        string message = ex == null
            ? prefix
            : $"{prefix}：{ex.Message}";

        // 类型名单独一行：用户回报问题时"是什么异常"比"异常说了什么"更管用，
        // 而且不夹带堆栈（堆栈里有路径，太长也不该塞进对话框正文）。
        string detail = ex == null
            ? string.Empty
            : ex.GetType().FullName ?? ex.GetType().Name;

        DialogUiThread.RunNotification(
            new AppDialogRequest
            {
                Title = "错误",
                Message = PasswordMasker.Sanitize(message),
                Detail = PasswordMasker.Sanitize(detail),
                Icon = AppDialogIcon.Error,
                Buttons = AppDialogButtons.Ok
            },
            "ShowException");
    }
}

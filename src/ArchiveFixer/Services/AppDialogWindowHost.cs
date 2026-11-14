using System;

namespace ArchiveFixer.Services;

/// <summary>
/// 自绘对话框窗口的**薄适配**（从 <see cref="DialogService"/> 按职责拆出的协作类）。
///
/// <para>它只做两件事：问 <see cref="DialogWindowFactories.AppDialog"/> 要一个
/// <see cref="IAppDialogWindow"/>，然后把窗口边界以内的步骤（挂 Owner、<c>ShowDialog()</c> /
/// <c>Show()</c>、读回答案）走完。线程策略与降级在 <see cref="DialogUiThread"/>，
/// ⛔ 这里不做任何等待、不判任何"该不该弹"。</para>
/// </summary>
internal static class AppDialogWindowHost
{
    /// <summary>
    /// 在 UI 线程上创建并模态显示统一对话框，显示完用 <paramref name="read"/> 读回答案。
    /// </summary>
    internal static T ShowModal<T>(AppDialogRequest request, Func<IAppDialogWindow, T> read)
    {
        IAppDialogWindow window = DialogWindowFactories.AppDialog!.Invoke(request);

        window.AttachOwner();
        window.ShowDialog();

        return read(window);
    }

    /// <summary>
    /// 在 UI 线程上创建并**非模态**显示统一对话框（<c>Show()</c>，不跑嵌套消息泵、不等用户）。
    ///
    /// <para>为什么需要它：批处理跑在 UI 线程上，"中途发现空间不足"这类**纯提示**要是用
    /// <c>ShowDialog()</c>，就会把整批卡在"等用户点确定"上 —— 而一键档的红线正是
    /// "批中间不许要用户点一下才能继续"（AGENTS.md §11）。非模态窗口浮在旁边，
    /// 批一秒都不停，用户回来照样看得见。</para>
    ///
    /// <para>关窗路径不用改：<c>AppDialogWindow</c> 在非模态下设置 <c>DialogResult</c>
    /// 会抛 <see cref="InvalidOperationException"/>，它自己已经捕获并回落到 <c>Close()</c>。</para>
    /// </summary>
    internal static void ShowModeless(AppDialogRequest request)
    {
        if (DialogWindowFactories.AppDialog == null)
        {
            DialogFallbackLog.Record("ShowModeless", request.Message, "宿主没有注册对话框窗口实现，已按无界面宿主降级");
            return;
        }

        IAppDialogWindow window = DialogWindowFactories.AppDialog.Invoke(request);

        window.AttachOwner();
        window.Show();
    }
}

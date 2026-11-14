using System.Windows;

namespace ArchiveFixer.Services;

/// <summary>
/// 危险操作的确认（从 <see cref="DialogService"/> 按职责拆出的协作类）：
/// 主按钮用实心红（<see cref="AppDialogRequest.Destructive"/> = true），且文案写清动作本身
/// （"清空""删除""彻底删除"），而不是含糊的"是"。
///
/// <para>这一档与普通确认的**唯一**差别就是危险色 + 不可逆的语义，所以单独成类：
/// 默认按不确认处理（无界面宿主 / 超时 / 显示失败一律 <c>false</c>），
/// ⛔ 任何一档都不许因为"没人点"而放行。</para>
/// </summary>
internal static class DestructiveConfirmPresenter
{
    /// <summary>
    /// 危险操作的二次确认：主按钮实心红，文案写清动作本身。默认按不确认处理。
    /// </summary>
    /// <param name="message">正文，建议写清"会发生什么、能不能撤销"。</param>
    /// <param name="confirmText">主按钮文案。</param>
    internal static bool ShowDestructiveConfirm(string message, string confirmText)
    {
        return DialogUiThread.RunValueDialog(
            new AppDialogRequest
            {
                Title = "危险操作确认",
                Message = message,
                Icon = AppDialogIcon.Warning,
                Buttons = AppDialogButtons.YesNo,
                Destructive = true,
                YesText = string.IsNullOrWhiteSpace(confirmText) ? "确定" : confirmText,
                NoText = "取消"
            },
            window => window.Result == MessageBoxResult.Yes,
            "ShowDestructiveConfirm",
            fallback: false);
    }

    /// <summary>
    /// 危险操作的**二次确认**（红色 + 勾选框）。
    ///
    /// <para>为什么需要它：规格 §3.2 的"彻底删除"档要求同时满足"界面红色标识"与"二次确认（勾选/输入）"，
    /// 而这两个位在只带红色与只带勾选框的两个 API 里各占一半。少任何一半都不满足规格：
    /// 只有红色 = 一次点掉就永久删除；只有勾选框 = 危险动作看起来像普通询问。</para>
    /// </summary>
    /// <param name="confirmText">主按钮文案（写动作本身："彻底删除"）。</param>
    /// <param name="optionText">勾选框文案（"我知道不可恢复"）。</param>
    /// <param name="optionChecked">用户最终是否勾选（返回 false 时它恒为 false）。</param>
    internal static bool ShowDestructiveConfirm(
        string message,
        string confirmText,
        string optionText,
        out bool optionChecked)
    {
        bool checkedState = false;

        bool confirmed = DialogUiThread.RunValueDialog(
            new AppDialogRequest
            {
                Title = "危险操作确认",
                Message = message,
                Icon = AppDialogIcon.Warning,
                Buttons = AppDialogButtons.YesNo,
                Destructive = true,
                YesText = string.IsNullOrWhiteSpace(confirmText) ? "确定" : confirmText,
                NoText = "取消",
                OptionText = optionText ?? string.Empty,
                OptionChecked = false
            },
            window =>
            {
                checkedState = window.IsOptionChecked;
                return window.Result == MessageBoxResult.Yes;
            },
            "ShowDestructiveConfirm",
            fallback: false);

        optionChecked = confirmed && checkedState;
        return confirmed;
    }

    /// <summary>
    /// 危险操作确认 + **可选的**危险档勾选框（用户 2026-09-22 拍板的"其余物删除确认用勾选"）。
    ///
    /// <para>与 <see cref="ShowDestructiveConfirm(string, string, string, out bool)"/> 的区别只有一处，
    /// 但很关键：<b>不勾选也能确认</b>。语义是"默认走安全档（移入回收站），勾上才切到激进档"——
    /// 勾选框在这里是**档位选择**，不是"必须承认才能继续"的确认位。上一版把两者混用，
    /// 导致用户不勾就没法走默认的回收站档。</para>
    ///
    /// <para>激进档自己的二次确认（红色 + "我知道不可恢复"必勾）由调用方在拿到
    /// <paramref name="optionChecked"/> == true 之后再弹一次，本方法不代劳。</para>
    /// </summary>
    /// <param name="optionChecked">用户最终是否勾选（未确认时恒为 false）。</param>
    internal static bool ShowDestructiveConfirmWithOption(
        string message,
        string confirmText,
        string optionText,
        bool optionCheckedByDefault,
        out bool optionChecked)
    {
        bool checkedState = optionCheckedByDefault;

        bool confirmed = DialogUiThread.RunValueDialog(
            new AppDialogRequest
            {
                Title = "危险操作确认",
                Message = message,
                Icon = AppDialogIcon.Warning,
                Buttons = AppDialogButtons.YesNo,
                Destructive = true,
                YesText = string.IsNullOrWhiteSpace(confirmText) ? "确定" : confirmText,
                NoText = "取消",
                OptionText = optionText ?? string.Empty,
                OptionChecked = optionCheckedByDefault
            },
            window =>
            {
                checkedState = window.IsOptionChecked;
                return window.Result == MessageBoxResult.Yes;
            },
            "ShowDestructiveConfirmWithOption",
            fallback: false);

        optionChecked = confirmed && checkedState;
        return confirmed;
    }
}

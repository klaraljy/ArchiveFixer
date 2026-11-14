using ArchiveFixer.Packing;
using System.Collections.Generic;

namespace ArchiveFixer.Services;

/// <summary>
/// 由宿主注入的**辅助模态小窗**（从 <see cref="DialogService"/> 按职责拆出的协作类）：
/// 打包前的小确认（<see cref="IPackingConfirmWindow"/>）与「从密码列表里选」
/// （<see cref="IPasswordPickerWindow"/>）。
///
/// <para>这两个窗口不是 <see cref="AppDialogRequest"/> 那一套自绘框，而是 Views 里的独立窗口，
/// 由 <see cref="DialogWindowFactories"/> 注入；本类只认接口，⛔ Services 不引用 Views。
/// 它们借用文件选择那一档的派发（<see cref="DialogUiThread.RunFileDialog{T}"/>）：
/// 同样是"要么拿到用户的选择、要么拿到空结果"，无界面宿主下分别降级为
/// <c>null</c>（一个字节都不许提前动）与空串（等于用户没选）。</para>
/// </summary>
internal static class AuxiliaryDialogPresenter
{
    /// <summary>
    /// **打包的小确认弹窗**（用户 2026-09-26 第 46 条）：只显示"最终压缩包放在哪 + 原包操作 + 其余物操作"。
    ///
    /// <para>返回用户确认后的那一套选择；用户取消时返回 <c>null</c>（⛔ 一个字节都不许提前动）。</para>
    /// </summary>
    internal static PackingRunOptions? ShowPackingConfirm(PackingConfirmRequest request)
    {
        return DialogUiThread.RunFileDialog<PackingRunOptions?>(
            () =>
            {
                IPackingConfirmWindow? window = DialogWindowFactories.PackingConfirm?.Invoke(request);

                return window != null && window.ShowDialog() == true ? window.Result : null;
            },
            "ShowPackingConfirm",
            null);
    }

    /// <summary>
    /// 「从密码列表里选」的小窗口（用户 2026-09-26 第 46 条）。
    /// 返回选中那条；取消 / 列表为空时返回空串。⛔ 不做任何记账、不写日志。
    /// </summary>
    internal static string ShowPasswordPicker(string title, IReadOnlyList<string> passwords)
    {
        if (passwords == null || passwords.Count == 0)
        {
            return string.Empty;
        }

        return DialogUiThread.RunFileDialog(
            () =>
            {
                IPasswordPickerWindow? window = DialogWindowFactories.PasswordPicker?.Invoke(title, passwords);

                return window != null && window.ShowDialog() == true ? window.Picked ?? string.Empty : string.Empty;
            },
            "ShowPasswordPicker",
            string.Empty);
    }
}

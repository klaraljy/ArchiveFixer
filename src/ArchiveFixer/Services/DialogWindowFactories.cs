using ArchiveFixer.Packing;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Services;

/// <summary>
/// 窗口实现的注入点：由宿主（<c>App</c>）在启动时把 Views 里的真实窗口装进来；
/// Services 不引用 Views。⛔ 未注入时按"无窗口宿主"走既有降级路径。
/// </summary>
public static class DialogWindowFactories
{
    /// <summary>创建自绘对话框窗口。</summary>
    public static Func<AppDialogRequest, IAppDialogWindow>? AppDialog { get; set; }

    /// <summary>创建打包小确认窗口。</summary>
    public static Func<PackingConfirmRequest, IPackingConfirmWindow>? PackingConfirm { get; set; }

    /// <summary>创建密码选择窗口。</summary>
    public static Func<string, IReadOnlyList<string>, IPasswordPickerWindow>? PasswordPicker { get; set; }

    /// <summary>
    /// 创建密码列表窗口；参数是 <c>PasswordListViewModel</c>，Services 不引用 ViewModels，故用 object 承载。
    /// 未注入时按“无窗口宿主”处理（单测/控制台不弹窗）。
    /// </summary>
    public static Func<object, bool?>? PasswordList { get; set; }

    /// <summary>创建「改名…」小窗；参数是默认后缀，返回 null = 用户取消。</summary>
    public static Func<string, ArchiveFixer.Models.RenameDialogResult?>? RenameDialog { get; set; }

    /// <summary>创建「手动输入密码」小窗；返回 null = 跳过。</summary>
    public static Func<string, string?>? PasswordPrompt { get; set; }
}


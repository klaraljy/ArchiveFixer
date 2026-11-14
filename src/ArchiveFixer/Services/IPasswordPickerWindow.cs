using System.Collections.Generic;

namespace ArchiveFixer.Services;

/// <summary>「从密码列表里选」窗口的最小契约（实现在 Views；Services 只认接口）。</summary>
public interface IPasswordPickerWindow
{
    /// <summary>用户选中的那条；取消时为 null。</summary>
    string? Picked { get; }

    /// <summary>模态显示并返回 DialogResult。</summary>
    bool? ShowDialog();
}


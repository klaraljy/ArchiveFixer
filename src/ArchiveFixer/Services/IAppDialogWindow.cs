using System.Windows;

namespace ArchiveFixer.Services;

/// <summary>
/// 自绘对话框窗口的最小契约：Services 只认这个接口，具体 Window（<c>Views/AppDialogWindow</c>）
/// 由宿主在启动时注入；⛔ 判据/文案/返回语义不变。
/// </summary>
public interface IAppDialogWindow
{
    /// <summary>用户点的是哪一个按钮（WPF <see cref="MessageBoxResult"/> 口径，与原实现一致）。</summary>
    MessageBoxResult Result { get; }

    /// <summary>窗口 DialogResult（✕ 关窗时仍是 null，与原实现一致）。</summary>
    bool? DialogResult { get; set; }

    /// <summary>可选项位（"不再提示"等）是否勾选。</summary>
    bool IsOptionChecked { get; }

    /// <summary>模态显示并返回 DialogResult。</summary>
    bool? ShowDialog();

    /// <summary>非模态显示（不跑嵌套消息泵、不等用户）。</summary>
    void Show();

    /// <summary>按原逻辑挑宿主窗口并挂 Owner（实现方负责 WPF 细节）。</summary>
    void AttachOwner();

    /// <summary>同名冲突询问的默认按钮：主按钮「覆盖」不作默认（回车不覆盖）。</summary>
    void ApplyConflictDefaults();

    /// <summary>关闭窗口。</summary>
    void Close();
}


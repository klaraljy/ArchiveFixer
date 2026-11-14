using ArchiveFixer.Packing;

namespace ArchiveFixer.Services
{
    /// <summary>打包小确认窗口的最小契约（实现在 Views；Services 只认接口）。</summary>
    public interface IPackingConfirmWindow
    {
        /// <summary>用户确认后的选择；取消时为 null。</summary>
        PackingRunOptions? Result { get; }

        /// <summary>模态显示并返回 DialogResult。</summary>
        bool? ShowDialog();
    }
}


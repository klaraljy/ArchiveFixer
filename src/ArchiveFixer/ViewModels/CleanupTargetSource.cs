namespace ArchiveFixer.ViewModels;

internal enum CleanupTargetSource
{
    /// <summary>一个都没勾 —— 什么都不做（提示用户先勾选）。</summary>
    None = 0,

    /// <summary>以**勾选**为准（批量清理的入口）。</summary>
    Checked = 1,

    /// <summary>只作用于当前选中的一行（右侧任务检查器）。</summary>
    Selected = 2
}

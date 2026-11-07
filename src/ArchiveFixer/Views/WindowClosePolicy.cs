namespace ArchiveFixer.Views
{
    /// <summary>
    /// 主窗口 X 的唯一判据（WinRAR 式，用户 2026-11-07 确认）：
    /// 空闲 ⇒ 正常退出；有任务 ⇒ 收托盘，任务继续。
    /// </summary>
    internal static class WindowClosePolicy
    {
        internal static bool ShouldHideToTray(bool isBusy) => isBusy;
    }
}

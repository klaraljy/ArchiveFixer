namespace ArchiveFixer.Views
{
    /// <summary>
    /// 主窗口 X 的唯一判据（WinRAR 式，用户 2026-11-07 确认）：
    /// 空闲 ⇒ 正常退出；有任务 ⇒ 收托盘，任务继续。
    /// </summary>
    internal static class WindowClosePolicy
    {
        internal static bool ShouldHideToTray(bool isBusy) => isBusy;

        /// <summary>
        /// 主窗口真正关闭后，生产宿主必须结束进程。
        ///
        /// <para>⛔ 不要再用 <c>Application.MainWindow == this</c> 当条件：StartupUri 建出来的窗口
        /// 不保证在任何时刻都等于 <see cref="System.Windows.Application.MainWindow"/>；一旦不相等，
        /// 空闲点 X 会关掉窗口却不退出，留下一个没有窗口、没有托盘的进程，第二次启动也只会
        /// 给这个死进程发“显示”事件。</para>
        ///
        /// <para>测试探针用的是普通 <see cref="System.Windows.Application"/>，不是 ArchiveFixer.App，
        /// 所以这里仍能在测试里避免误杀 testhost。</para>
        /// </summary>
        internal static bool ShouldShutdownAfterClose(bool allowClose, bool isProductionApplication)
            => allowClose && isProductionApplication;
    }
}

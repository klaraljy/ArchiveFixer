namespace ArchiveFixer.Views
{
    /// <summary>
    /// 主窗口 X 的唯一判据（用户 2026-11-10 口径：任务列表非空 ⇒ 收托盘；空 ⇒ 退出）：
    /// 任务列表非空 ⇒ 收托盘（与忙闲无关）；列表为空 ⇒ 正常退出。
    /// </summary>
    internal static class WindowClosePolicy
    {
        internal static bool ShouldHideToTray(int taskCount) => taskCount > 0;

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
        /// <summary>
        /// 当前是不是**生产宿主**（真正的 ArchiveFixer.exe，而不是 dotnet test 的 testhost）。
        ///
        /// <para>判据只认入口程序集名：测试里虽然 `Application.Current` 是 `ArchiveFixer.App`
        /// （为了加载资源），但入口程序集是 <c>testhost</c> —— 这种情况下**绝不建真实托盘图标**，
        /// 否则每跑一次测试集就会在用户桌面上弹一条「程序仍在运行」气泡
        /// （用户 2026-11-10 原话：「老是出现这个弹窗」）。</para>
        /// </summary>
        internal static bool IsProductionHost()
        {
            try
            {
                string? entry = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;

                return string.Equals(entry, "ArchiveFixer", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
        internal static bool ShouldShutdownAfterClose(bool allowClose, bool isProductionApplication)
            => allowClose && isProductionApplication;
    }
}

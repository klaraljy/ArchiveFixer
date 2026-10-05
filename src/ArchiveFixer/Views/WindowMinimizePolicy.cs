using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ArchiveFixer.Views
{
    /// <summary>
    /// 模态子窗的"不许最小化"统一策略（用户 2026-10-05 拍板 A2）。
    ///
    /// <para><b>为什么要有这一条</b>：模态子窗关闭之前，主窗口是**被禁用**的
    /// （<c>Owner = 主窗口</c> + <c>ShowDialog()</c>）。这种窗口一旦被最小化，
    /// 用户就没有任何办法把它找回来：它自己缩到任务栏下面（<c>ShowInTaskbar="False"</c> 的那几个
    /// 连任务栏按钮都没有），而唯一能点的主窗口此刻不接受输入 —— 等于把自己关在门外，
    /// 只能去任务管理器结束进程。用户真机报的原话就是"密码本的最小化行为和界面说明的最小化行为不一样"：
    /// 同样点最小化，一个还能从任务栏找回来、另一个直接消失。</para>
    ///
    /// <para><b>两道一起做，缺一不可</b>：</para>
    /// <list type="number">
    /// <item><description><b>Win32 把标题栏那颗最小化按钮置灰</b>（<c>GetSystemMenu</c> +
    /// <c>EnableMenuItem(SC_MINIMIZE, MF_BYCOMMAND | MF_GRAYED)</c>，句柄走 <see cref="WindowInteropHelper"/>）——
    /// 它同时管住标题栏那颗按钮**和**点标题栏图标弹出的系统菜单里的「最小化」，
    /// 用户在能点的地方就点不动（比"点了再弹回来"这种手感好得多）。</description></item>
    /// <item><description><b><c>StateChanged</c> 兜底</b>——置灰拦不住 <c>Win+D</c> / 「显示桌面」/ 任务栏右键
    /// 这类系统级最小化，所以窗口真的被最小化时立刻还原回 <see cref="WindowState.Normal"/>。</description></item>
    /// </list>
    ///
    /// <para>⛔ <b>刻意不改 <c>ResizeMode</c></b>：A2 的口径是"不许最小化、但保留拖边框改大小"，
    /// <c>ResizeMode="CanMinimize"</c> 会把"能不能最小化"又变成每个窗口各写各的（正是这一轮要拆掉的东西）。</para>
    ///
    /// <para>⛔ <b>只挂给模态子窗</b>（<c>ShowDialog</c> 那一批）；日志窗是用户自己点开的非模态窗口，
    /// 最小化 + 任务栏按钮正是它该有的行为，<b>不接这条策略</b>；主窗口同理。</para>
    /// </summary>
    public static class WindowMinimizePolicy
    {
        /// <summary>系统菜单里「最小化」那一项的标识。</summary>
        private const uint SC_MINIMIZE = 0xF020;

        /// <summary>按标识（而不是按位置）找菜单项 —— 菜单顺序变了也不会误伤别的项。</summary>
        private const uint MF_BYCOMMAND = 0x00000000;

        /// <summary>置灰并且**不可用**（<c>MF_DISABLED</c> 只是不能点，<c>MF_GRAYED</c> 才会画成灰的）。</summary>
        private const uint MF_GRAYED = 0x00000001;

        /// <summary>
        /// 给一个模态子窗挂上"不许最小化"。
        ///
        /// <para>必须在窗口构造（<c>InitializeComponent()</c> 之后）时调用；<b>重复调用只挂一次</b>
        /// （幂等），免得同一次最小化触发两遍还原。</para>
        /// </summary>
        public static void Apply(Window window)
        {
            if (window == null)
            {
                return;
            }

            // 幂等：与 WindowAttention 同一种手法 —— 用一个附加属性当"已经挂过"的标记，不给窗口加字段。
            if (window.GetValue(AttachedProperty) is bool attached && attached)
            {
                return;
            }

            window.SetValue(AttachedProperty, true);

            // 句柄要等窗口初始化完才有（构造函数里还没有），所以置灰挂在 SourceInitialized 上。
            window.SourceInitialized += (_, _) => GrayOutMinimizeButton(window);

            // 已经初始化过的窗口（非构造函数里调用）不会再触发 SourceInitialized，补一次直接置灰。
            if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            {
                GrayOutMinimizeButton(window);
            }

            window.StateChanged += (_, _) =>
            {
                /*
                 * 判据只有 ShouldRestoreOnMinimize 一处（可单测的纯函数）：
                 * 「最小化整个程序」（用户点主窗口的最小化 / Win+D）时主窗口也是 Minimized，
                 * 那种情况下跟着一起最小化是对的 —— 硬还原会把整程序的最小化搅成闪烁。
                 */
                if (ShouldRestoreOnMinimize(window.WindowState, window.Owner?.WindowState))
                {
                    window.WindowState = WindowState.Normal;
                }
            };
        }

        /// <summary>
        /// 这一刻要不要把窗口从最小化还原回普通状态。
        ///
        /// <para>判据：<b>自己被最小化了，而 Owner（主窗口）没有同时被最小化</b>
        /// —— 前者说明这是模态子窗被单独缩了下去（用户就找不回它了），后者说明用户是故意
        /// "最小化整个程序"，那时不该按兵不动之外做任何事。没有 Owner 时（<c>null</c>）
        /// 按"主窗口没被最小化"办。</para>
        /// </summary>
        internal static bool ShouldRestoreOnMinimize(WindowState self, WindowState? owner)
        {
            return self == WindowState.Minimized && owner != WindowState.Minimized;
        }

        /// <summary>
        /// 把标题栏那颗最小化按钮置灰。
        ///
        /// <para>失败一律吞掉：拿不到句柄（窗口还没建出来 / 已在关闭）、取不到系统菜单、
        /// 无桌面会话 —— 都不该让"显示一个窗口"变成一次失败；真拦不住还有 <c>StateChanged</c> 那条兜底。</para>
        /// </summary>
        private static void GrayOutMinimizeButton(Window window)
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(window).Handle;

                if (handle == IntPtr.Zero)
                {
                    return;
                }

                // bRevert = false：取**当前**那份系统菜单，不是"恢复成默认菜单"。
                IntPtr menu = GetSystemMenu(handle, false);

                if (menu == IntPtr.Zero)
                {
                    return;
                }

                _ = EnableMenuItem(menu, SC_MINIMIZE, MF_BYCOMMAND | MF_GRAYED);
            }
            catch
            {
                // 见方法说明：置灰只是"更好用的第一道"，坏不了事。
            }
        }

        /// <summary>"已经挂过最小化策略"的标记（附加属性，避免给窗口加字段）。</summary>
        private static readonly DependencyProperty AttachedProperty =
            DependencyProperty.RegisterAttached(
                "MinimizePolicyAttached",
                typeof(bool),
                typeof(WindowMinimizePolicy),
                new PropertyMetadata(false));

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool bRevert);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);
    }
}

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
    /// 用户就可能找不回它 —— 而"同一个界面里两个同类窗口行为不一样"正是用户真机报的那句
    /// （「密码本的最小化行为和界面说明的不一样」）。</para>
    ///
    /// <para><b>做法只有一道：Win32 把标题栏那颗最小化按钮置灰</b>（<c>GetSystemMenu</c> +
    /// <c>EnableMenuItem(SC_MINIMIZE, MF_BYCOMMAND | MF_GRAYED)</c>，句柄走 <see cref="WindowInteropHelper"/>）——
    /// 它同时管住标题栏那颗按钮**和**点标题栏图标弹出的系统菜单里的「最小化」，
    /// 用户在能点的地方就点不动。</para>
    ///
    /// <para>⛔ <b>绝对不许再挂 <c>StateChanged</c> 去"强行还原"</b>（2026-10-05 真机当场踩到）：
    /// 用户最小化主窗口 / 按 Win+D 时，**子窗的 <c>StateChanged</c> 可能先于主窗口的状态更新触发**，
    /// 那一刻判据看到的是"主窗口还没最小化" ⇒ 把子窗弹回 <c>Normal</c> ⇒ 紧接着系统又把它跟着主窗口
    /// 缩下去 ⇒ 弹回 / 缩下反复，**真机现象就是"窗口一闪一闪的"**（用户原话：
    /// 「之前还能在状态栏里面现在就直接一闪一闪的，密码本也是这样」）。
    /// 置灰之后用户根本点不动最小化，剩下的系统级最小化（Win+D）本来就会跟着主窗口一起回来，
    /// 不需要、也不允许再补一道"还原"。</para>
    ///
    /// <para>⛔ <b>刻意不改 <c>ResizeMode</c></b>：A2 的口径是"不许最小化、但保留拖边框改大小"，
    /// <c>ResizeMode="CanMinimize"</c> 会把"能不能最小化"又变成每个窗口各写各的（正是这一轮要拆掉的东西）。</para>
    ///
    /// <para>⛔ <b>刻意不动 <c>ShowInTaskbar</c></b>：任务栏上那个按钮是用户**找回窗口**的手段
    /// （他真机原话「之前还能在状态栏里面」）—— 各窗口保持它原本的值，⛔ 不许拿"统一口径"当理由去关掉它。</para>
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
        /// <para>必须在窗口构造（<c>InitializeComponent()</c> 之后）时调用；<b>重复调用只挂一次</b>（幂等）。</para>
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
        }

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
                // 见方法说明：置灰只是"让用户点不动"，坏不了事；⛔ 失败时不补任何"还原"逻辑。
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

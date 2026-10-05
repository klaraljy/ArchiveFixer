using System.Windows;

namespace ArchiveFixer.Views
{
    /// <summary>
    /// 子窗的"缩到任务栏"统一策略（用户 2026-10-05 的口径）。
    ///
    /// <para><b>用户要的是什么</b>：原话「我想要的是最小化全部缩到状态栏里面，你现在不要搞那些多余的东西」
    /// —— 也就是**普通窗口的行为**：点最小化 ⇒ 缩到任务栏、任务栏上有它的按钮、点一下就回来。</para>
    ///
    /// <para><b>所以这里只做一件事</b>：把子窗的 <see cref="Window.ShowInTaskbar"/> 置为 <c>true</c>。
    /// ⛔ 那些 <c>ShowInTaskbar="False"</c> 的子窗（说明 / 密码选择 / 提示 / 一键处理确认 / 打包确认 / 改名框）
    /// 一旦被最小化，任务栏上**没有**它的按钮 ⇒ 找不回来（主窗口此刻还被模态禁用）。
    /// 注意这个属性**只能在窗口显示之前设**，所以本方法必须在构造函数里调用（显示后再改会抛）。</para>
    ///
    /// <para>⛔ <b>不许再往这里加"聪明的"东西</b>（都在 2026-10-05 当天被用户否掉或真机踩到）：</para>
    /// <list type="bullet">
    /// <item><description>⛔ 不许把最小化按钮**置灰** / 不许改成 <c>ResizeMode="NoResize"</c> / <c>CanMinimize</c>
    /// —— 用户的诉求正是"能最小化"，把按钮拿掉是反着来。</description></item>
    /// <item><description>⛔ 不许挂 <c>StateChanged</c> 去"强行还原"：用户最小化主窗口 / 按 Win+D 时，
    /// 子窗的状态变化**可能先于**主窗口触发 ⇒ 判据看到"主窗口还没最小化"就把子窗弹回来 ⇒
    /// 系统又把它跟着缩下去 ⇒ 弹回/缩下反复 ⇒ **真机现象是一闪一闪**（用户原话：
    /// 「之前还能在状态栏里面现在就直接一闪一闪的，密码本也是这样全都有问题」）。</description></item>
    /// <item><description>⛔ 不许碰 <c>ResizeMode</c>：能不能改大小是各窗口自己的事。</description></item>
    /// </list>
    ///
    /// <para>⛔ <b>只挂给子窗</b>；主窗口与日志窗本来就是普通窗口，不需要它。</para>
    /// </summary>
    public static class WindowMinimizePolicy
    {
        /// <summary>
        /// 让这个子窗出现在任务栏上（于是它被最小化时有个能点回来的入口）。
        ///
        /// <para>在窗口构造（<c>InitializeComponent()</c> 之后）时调用；⛔ 显示之后再改这个属性会抛异常。
        /// 窗口为 null 时什么都不做（调用点可能拿不到窗口）。</para>
        /// </summary>
        public static void Apply(Window window)
        {
            if (window == null)
            {
                return;
            }

            window.ShowInTaskbar = true;
        }
    }
}

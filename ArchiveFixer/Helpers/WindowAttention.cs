using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ArchiveFixer.Helpers
{
    /// <summary>需要引起注意的强度（决定响几声、闪多久）。</summary>
    public enum AttentionStrength
    {
        /// <summary>普通提示：闪任务栏 + 响一声。</summary>
        Normal,

        /// <summary>必须有人处理的提示（警告 / 错误 / 询问 / 密码）：闪任务栏（一直闪到被点）+ 连响三声。</summary>
        Strong
    }

    /// <summary>
    /// 让一个模态窗口**真的被看见**（用户 2026-09-22 反馈的原话：
    /// 「密码本那个窗口弹出来之后躲到主窗口后面了，只有声音、没有闪烁，声音还很轻 ——
    /// WinRAR 会强闪烁 + 大声提醒」）。
    ///
    /// <para><b>三件事</b>：</para>
    /// <list type="number">
    /// <item><description><b>从别的窗口后面拎到前面</b>：被最小化就还原，然后 <c>Activate()</c>，
    /// 再用 <c>Topmost</c> 脉冲把 z 序顶上去（<c>Topmost</c> 立刻复位，不是"永远置顶"）。
    /// 另加一次 <c>SetForegroundWindow</c> 尽力而为 —— 前台锁可能拒绝它，那时靠闪烁兜底。</description></item>
    /// <item><description><b>任务栏闪烁</b>：<c>FlashWindowEx</c> + <c>FLASHW_TIMERNOFG</c>
    /// （一直闪到窗口被激活为止，正是 WinRAR 那种"强提醒"的手感）。窗口获得焦点时立刻停。</description></item>
    /// <item><description><b>提示音</b>：连响 <see cref="ChimeCount"/> 声（间隔 <see cref="ChimeIntervalMs"/> 毫秒）。
    /// ⚠ 音量由**系统音量**决定，程序不能（也不该）超过它 —— 这条写进「已知限制」，不假装"更响"。</description></item>
    /// </list>
    ///
    /// <para><b>什么时候不做</b>（这两条是给测试与自动化留的出口，也是纪律要求）：</para>
    /// <list type="bullet">
    /// <item><description><c>ShowActivated == false</c>：调用方明确说了"别抢焦点"，那就一声不响、一下不闪
    /// （离屏窗口探针就是这么显示窗口的，见 <c>WindowInstantiationTests</c> 的 §13 说明）；</description></item>
    /// <item><description>窗口在屏幕外（<c>Left/Top ≤ -10000</c>）：看不见的窗口不需要提醒，
    /// 提醒了反而会在无桌面会话里制造噪音。</description></item>
    /// </list>
    ///
    /// <para><b>与 AGENTS.md §13 的关系</b>：那一节管的是**代理脚本**不许抢用户的前台；
    /// 这里是**程序自己**在"需要用户输入"时把自己拎出来 —— 用户明确要求的行为，两者不冲突。</para>
    /// </summary>
    public static class WindowAttention
    {
        /// <summary>两声提示音之间的间隔（毫秒）。</summary>
        public const int ChimeIntervalMs = 600;

        /// <summary>离屏判据的阈值（窗口 Left/Top 小于它就算屏幕外）。</summary>
        public const double OffScreenThreshold = -10000d;

        private const uint FLASHW_STOP = 0;
        private const uint FLASHW_ALL = 3;
        private const uint FLASHW_TIMERNOFG = 12;

        /// <summary>要响几声：普通 1 声（"知道一下"），强提醒 3 声（"过来处理"）。</summary>
        public static int ChimeCount(AttentionStrength strength)
        {
            return strength == AttentionStrength.Strong ? 3 : 1;
        }

        /// <summary>
        /// 这个窗口该不该被"提醒"：屏幕外的不提醒、<c>ShowActivated=false</c> 的不提醒。
        /// </summary>
        public static bool ShouldDemandAttention(bool showActivated, double left, double top)
        {
            if (!showActivated)
            {
                return false;
            }

            return left > OffScreenThreshold && top > OffScreenThreshold;
        }

        /// <summary>
        /// 需不需要"拎到前面"：最小化、不可见、或可见但没被激活，都需要。
        /// 已经在前台就什么都不做（避免"抢一下焦点"这种无谓打扰）。
        /// </summary>
        public static bool NeedsBringToFront(WindowState state, bool isVisible, bool isActive)
        {
            if (!isVisible)
            {
                return true;
            }

            return state == WindowState.Minimized || !isActive;
        }

        /// <summary>
        /// 给窗口挂上注意力行为：显示时提醒一次，获得焦点 / 关掉时立刻收干净。
        ///
        /// <para>必须在窗口构造（<c>InitializeComponent</c> 之后）时调用；
        /// 重复调用只挂一次（幂等），免得同一次显示响两轮。</para>
        /// </summary>
        public static void Attach(Window? window, AttentionStrength strength = AttentionStrength.Strong)
        {
            if (window == null)
            {
                return;
            }

            // 幂等：用一个附加属性当"已经挂过"的标记（不引入新的字段与继承体系）。
            if (window.GetValue(AttachedProperty) is bool attached && attached)
            {
                return;
            }

            window.SetValue(AttachedProperty, true);

            var timer = new DispatcherTimer(DispatcherPriority.Normal, window.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(ChimeIntervalMs)
            };

            int remaining = 0;

            timer.Tick += (_, _) =>
            {
                if (remaining <= 0)
                {
                    timer.Stop();
                    return;
                }

                remaining--;
                PlayChime();

                if (remaining <= 0)
                {
                    timer.Stop();
                }
            };

            window.Loaded += (_, _) =>
            {
                if (!ShouldDemandAttention(window.ShowActivated, window.Left, window.Top))
                {
                    // 屏幕外 / 明确不抢焦点（测试与自动化用的就是这条路）：静默。
                    return;
                }

                DemandAttention(window, strength, timer, ref remaining);
            };

            // 用户一来就闭嘴：激活（点到了）与关闭都要把闪烁与提示音停掉。
            window.Activated += (_, _) => Stop(window, timer);
            window.Closed += (_, _) => Stop(window, timer);
        }

        /// <summary>手动触发一次提醒（挂过 <see cref="Attach"/> 的窗口才有意义）。</summary>
        public static void Demand(Window? window, AttentionStrength strength)
        {
            if (window == null || !window.IsLoaded)
            {
                return;
            }

            if (!ShouldDemandAttention(window.ShowActivated, window.Left, window.Top))
            {
                return;
            }

            var timer = new DispatcherTimer(DispatcherPriority.Normal, window.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(ChimeIntervalMs)
            };

            int remaining = 0;

            timer.Tick += (_, _) =>
            {
                remaining--;
                PlayChime();

                if (remaining <= 0)
                {
                    timer.Stop();
                }
            };

            DemandAttention(window, strength, timer, ref remaining);
        }

        private static void DemandAttention(
            Window window,
            AttentionStrength strength,
            DispatcherTimer timer,
            ref int remaining)
        {
            try
            {
                BringToFront(window);
                StartFlash(window);
            }
            catch
            {
                /*
                 * 提醒本身绝不允许把"显示一个对话框"变成一次失败：
                 * 拿不到窗口句柄（还在初始化）、前台锁拒绝、无桌面会话 —— 全部吞掉，
                 * 窗口该怎么显示还怎么显示。
                 */
            }

            remaining = ChimeCount(strength);
            PlayChime();
            remaining--;

            if (remaining > 0)
            {
                timer.Start();
            }
        }

        private static void BringToFront(Window window)
        {
            if (!NeedsBringToFront(window.WindowState, window.IsVisible, window.IsActive))
            {
                return;
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            /*
             * Topmost 脉冲：先置顶再复位。
             *
             * 为什么用这一招：另一个线程刚把窗口显示出来时，Windows 的前台锁常常拒绝
             * SetForegroundWindow/Activate —— 表现就是用户看到的"框躲在主窗口后面，只有声音"。
             * Topmost 的置位不受前台锁限制，能稳定把 z 序顶到最上面；立刻复位是为了不留一个
             * "永远压着别人"的窗口。
             */
            bool wasTopmost = window.Topmost;

            try
            {
                window.Topmost = true;
                window.Topmost = wasTopmost;
            }
            catch
            {
                // 极少数窗口不允许改 Topmost：忽略，后面还有闪烁兜底。
            }

            try
            {
                window.Activate();
            }
            catch
            {
                // 同上：激活失败不影响"窗口已经在前台"这件事。
            }

            IntPtr handle = TryGetHandle(window);

            if (handle != IntPtr.Zero)
            {
                try
                {
                    _ = SetForegroundWindow(handle);
                }
                catch
                {
                    // 前台锁拒绝是正常的，闪烁会替它把用户叫过来。
                }
            }
        }

        private static void StartFlash(Window window)
        {
            IntPtr handle = TryGetHandle(window);

            if (handle == IntPtr.Zero)
            {
                return;
            }

            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = handle,
                dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                uCount = uint.MaxValue,
                dwTimeout = 0
            };

            _ = FlashWindowEx(ref info);
        }

        private static void Stop(Window window, DispatcherTimer timer)
        {
            try
            {
                timer.Stop();

                IntPtr handle = TryGetHandle(window);

                if (handle == IntPtr.Zero)
                {
                    return;
                }

                var info = new FLASHWINFO
                {
                    cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                    hwnd = handle,
                    dwFlags = FLASHW_STOP,
                    uCount = 0,
                    dwTimeout = 0
                };

                _ = FlashWindowEx(ref info);
            }
            catch
            {
                // 收尾同样不允许抛：窗口正在关，句柄可能已经没了。
            }
        }

        private static IntPtr TryGetHandle(Window window)
        {
            try
            {
                return new WindowInteropHelper(window).Handle;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static void PlayChime()
        {
            try
            {
                System.Media.SystemSounds.Exclamation.Play();
            }
            catch
            {
                // 没有音频设备（无声卡 / 静音 / 远程会话）时什么都不做。
            }
        }

        /// <summary>"已经挂过注意力行为"的标记（附加属性，避免给窗口加字段）。</summary>
        private static readonly DependencyProperty AttachedProperty =
            DependencyProperty.RegisterAttached(
                "Attached",
                typeof(bool),
                typeof(WindowAttention),
                new PropertyMetadata(false));

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }
    }
}

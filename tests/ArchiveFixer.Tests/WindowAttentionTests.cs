using ArchiveFixer.Helpers;
using ArchiveFixer.Views;
using System;
using System.IO;
using System.Windows;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「提醒」的回归（用户 2026-09-22 反馈的缺陷：**弹出来的窗口躲在主窗口后面，只有声音、没有闪烁，
    /// 声音还很轻** —— 他要的是 WinRAR 那种强提醒）。
    ///
    /// <para>这里钉住的是**判据**（什么时候该提醒、提醒多强），不是"真的闪了没有" ——
    /// 后者是系统行为，只能在真机上眼看一次（见 <c>docs/人工测试清单.md</c>）。
    /// 另外钉住"三个关键窗口都挂上了提醒"，免得以后有人顺手把那一行删掉而没有任何测试变红。</para>
    /// </summary>
    public class WindowAttentionTests
    {
        [Fact]
        public void 强提醒响三声_普通提醒响一声()
        {
            Assert.Equal(3, WindowAttention.ChimeCount(AttentionStrength.Strong));
            Assert.Equal(1, WindowAttention.ChimeCount(AttentionStrength.Normal));

            // 间隔必须在"听得出是两声"与"不拖沓"之间。
            Assert.InRange(WindowAttention.ChimeIntervalMs, 300, 1200);
        }

        [Fact]
        public void 警告错误询问与危险操作都算强提醒_普通信息只响一声()
        {
            Assert.Equal(
                AttentionStrength.Strong,
                AppDialogWindow.ResolveAttention(new AppDialogRequest { Icon = AppDialogIcon.Warning }));

            Assert.Equal(
                AttentionStrength.Strong,
                AppDialogWindow.ResolveAttention(new AppDialogRequest { Icon = AppDialogIcon.Error }));

            Assert.Equal(
                AttentionStrength.Strong,
                AppDialogWindow.ResolveAttention(new AppDialogRequest { Icon = AppDialogIcon.Question }));

            // 危险操作（红按钮那一类）：哪怕图标是"信息"也要强提醒。
            Assert.Equal(
                AttentionStrength.Strong,
                AppDialogWindow.ResolveAttention(new AppDialogRequest { Icon = AppDialogIcon.Info, Destructive = true }));

            Assert.Equal(
                AttentionStrength.Normal,
                AppDialogWindow.ResolveAttention(new AppDialogRequest { Icon = AppDialogIcon.Info }));

            // 空请求不许抛（构造 AppDialogWindow 时 request 可能是 null）。
            Assert.Equal(AttentionStrength.Normal, AppDialogWindow.ResolveAttention(null));
        }

        [Fact]
        public void 离屏窗口和明确不抢焦点的窗口都不提醒()
        {
            // 正常前台窗口：提醒。
            Assert.True(WindowAttention.ShouldDemandAttention(showActivated: true, left: 100, top: 100));

            // 屏幕外（离屏窗口探针就是这么显示窗口的）：不提醒 —— 看不见的窗口提醒了只是噪音。
            Assert.False(WindowAttention.ShouldDemandAttention(showActivated: true, left: -32000, top: -32000));
            Assert.False(WindowAttention.ShouldDemandAttention(showActivated: true, left: 100, top: -32000));

            // 调用方明确说了"别抢焦点"：不提醒、不发声（测试与自动化走的正是这条路）。
            Assert.False(WindowAttention.ShouldDemandAttention(showActivated: false, left: 100, top: 100));
        }

        /// <summary>
        /// 用户 2026-09-26 收窄的那条口径（原话："点开弹窗就会出现声音响两下，闪几下，
        /// 你要知道这个提醒的作用是什么 —— 如果我现在的小窗没关闭导致我大窗口不能操作才需要这样的提醒，
        /// 而不是现在的每时每刻提醒"）：
        ///
        /// <para>⛔ **已经出现在最前面的窗口一声不响、一下不闪**；只有"它没能出现在最前面"
        /// （躲在大窗口后面 / 用户正在别的程序里）才提醒。</para>
        /// </summary>
        [Fact]
        public void 已经出现在最前面的窗口不响也不闪_只有没在最前面才提醒()
        {
            // 用户刚点出来、窗口就在他眼前：安静。
            Assert.False(WindowAttention.ShouldAlert(
                showActivated: true, left: 100, top: 100, isInFront: true));

            // 躲在大窗口后面 / 用户在别的程序里：提醒（拎到前面 + 闪 + 响）。
            Assert.True(WindowAttention.ShouldAlert(
                showActivated: true, left: 100, top: 100, isInFront: false));

            // 前两条老判据仍然优先：屏幕外、ShowActivated=false 一律不提醒（哪怕它不在前面）。
            Assert.False(WindowAttention.ShouldAlert(
                showActivated: true, left: -32000, top: -32000, isInFront: false));
            Assert.False(WindowAttention.ShouldAlert(
                showActivated: false, left: 100, top: 100, isInFront: false));

            // 判定延迟必须为"等窗口落定"留出时间（太小会误判成"不在前面"→ 又变成每次都响）。
            Assert.InRange(WindowAttention.SettleDelayMs, 80, 800);
        }

        /// <summary>
        /// 源码守卫：`Attach` 里必须**先判"在不在最前面"再决定提醒** ——
        /// 直接调 `DemandAttention` 的那种写法就是 2026-09-26 被投诉的"每时每刻提醒"。
        /// </summary>
        [Fact]
        public void 挂提醒的那段代码必须经过_在不在最前面_这一道判据()
        {
            string source = File.ReadAllText(Path.Combine(
                XamlBindingScan.RepositoryRoot, "src", "ArchiveFixer", "Helpers", "WindowAttention.cs"));

            Assert.Contains("ShouldAlert(", source, StringComparison.Ordinal);
            Assert.Contains("IsInFront(", source, StringComparison.Ordinal);
            Assert.Contains("GetForegroundWindow", source, StringComparison.Ordinal);
        }

        [Fact]
        public void 最小化不可见或不在前台都要拎到前面_已经在前台就不动它()
        {
            Assert.True(WindowAttention.NeedsBringToFront(WindowState.Minimized, isVisible: true, isActive: false));
            Assert.True(WindowAttention.NeedsBringToFront(WindowState.Minimized, isVisible: true, isActive: true));
            Assert.True(WindowAttention.NeedsBringToFront(WindowState.Normal, isVisible: true, isActive: false));
            Assert.True(WindowAttention.NeedsBringToFront(WindowState.Normal, isVisible: false, isActive: false));

            // 已经在前台：不抢（避免"点开设置窗口时又闪又响"这种无谓打扰）。
            Assert.False(WindowAttention.NeedsBringToFront(WindowState.Normal, isVisible: true, isActive: true));
        }

        [Fact]
        public void 三个需要被看见的窗口都挂上了提醒_不许被顺手删掉()
        {
            // ① 统一对话框（警告 / 错误 / 确认都走它）。
            AssertFileMentions(
                Path.Combine("src", "ArchiveFixer", "Views", "AppDialogWindow.xaml.cs"),
                "WindowAttention.Attach");

            // ② 密码本窗口（用户点名的那一个）。
            AssertFileMentions(
                Path.Combine("src", "ArchiveFixer", "Views", "PasswordListWindow.xaml.cs"),
                "WindowAttention.Attach");

            // ③ 解压中途"手动输入密码"那个窗口（它由后台线程投递显示，最容易躲到主窗口后面）。
            AssertFileMentions(
                Path.Combine("src", "ArchiveFixer", "ViewModels", "ExtractionCoordinator.cs"),
                "WindowAttention.Attach");

            // 提醒强度必须是强提醒：这三种都是"没人处理就卡住整批"的情形。
            AssertFileMentions(
                Path.Combine("src", "ArchiveFixer", "ViewModels", "ExtractionCoordinator.cs"),
                "AttentionStrength.Strong");
        }

        private static void AssertFileMentions(string relativePath, string needle)
        {
            string full = Path.Combine(XamlBindingScan.RepositoryRoot, relativePath);

            Assert.True(File.Exists(full), $"找不到文件：{full}");

            Assert.Contains(needle, File.ReadAllText(full), StringComparison.Ordinal);
        }
    }
}

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

using ArchiveFixer.Helpers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ArchiveFixer.Views;

/// <summary>
/// 「手动输入密码」小窗的真实实现；ViewModel 只拿到结果字符串，不碰 Window。
/// </summary>
internal static class PasswordPromptWindowFactory
{
    public static string? Show(string message)
    {
        var window = new System.Windows.Window
        {
            Title = "手动输入密码（只对本次运行有效）",
            Width = 520,
            Height = 300,
            MinWidth = 460,
            MinHeight = 280,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            Owner = System.Windows.Application.Current?.MainWindow,
            Background = System.Windows.Media.Brushes.White
        };

        /*
         * ⛔ 模态子窗一律不许最小化（用户 2026-10-05 拍板 A2）—— 这个框也是**代码里内联建的**，
         * 不在 XAML 名单里。它现在是 `NoResize`（压根点不动最小化），接上策略是为了①系统菜单那一项
         * 也置灰、②万一被 Win+D 这类系统级操作最小化，只要主窗口没跟着最小化就立刻还原。
         */
        ArchiveFixer.Views.WindowMinimizePolicy.Apply(window);

        var root = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(16) };

        for (int i = 0; i < 3; i++)
        {
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
            {
                Height = i == 1 ? new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
                                : System.Windows.GridLength.Auto
            });
        }

        var messageBlock = new System.Windows.Controls.TextBlock
        {
            Text = message,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12)
        };

        System.Windows.Controls.Grid.SetRow(messageBlock, 0);
        root.Children.Add(messageBlock);

        var passwordBox = new System.Windows.Controls.PasswordBox
        {
            Height = 30,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new System.Windows.Thickness(0, 0, 0, 12)
        };

        System.Windows.Controls.Grid.SetRow(passwordBox, 1);
        root.Children.Add(passwordBox);

        var buttonPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        var okButton = new System.Windows.Controls.Button
        {
            Content = "用这个密码",
            MinWidth = 110,
            Height = 30,
            Margin = new System.Windows.Thickness(0, 0, 8, 0)
        };

        var skipButton = new System.Windows.Controls.Button
        {
            Content = "跳过",
            MinWidth = 86,
            Height = 30,
            IsDefault = true,
            IsCancel = true
        };

        string? result = null;

        okButton.Click += (_, _) =>
        {
            result = passwordBox.Password;
            window.DialogResult = true;
            window.Close();
        };

        skipButton.Click += (_, _) =>
        {
            result = null;
            window.DialogResult = false;
            window.Close();
        };

        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(skipButton);

        System.Windows.Controls.Grid.SetRow(buttonPanel, 2);
        root.Children.Add(buttonPanel);

        window.Content = root;

        /*
         * 提醒（用户 2026-09-22 反馈的原话：「密码本那个窗口弹出来之后躲到主窗口后面了，
         * 只有声音、没有闪烁，声音还很轻」）。
         *
         * ⚠ 这一条**修正**了本方法早先那句"不置前、不 Topmost"的注释：那个口径来自 AGENTS.md §13，
         * 而 §13 管的是**代理脚本不许抢用户前台**（免得用户没法干别的事）。这里是**程序自己**
         * 在"整批卡在等密码"时把输入框拎出来 —— 用户明确要求这个行为，两者不冲突。
         * 强度是 Strong：一直闪任务栏（闪到被点）+ 连响三声。
         */
        ArchiveFixer.Helpers.WindowAttention.Attach(window, ArchiveFixer.Helpers.AttentionStrength.Strong);

        // 焦点给输入框（窗口被激活之后才拿得到键盘焦点）。
        window.Loaded += (_, _) => passwordBox.Focus();

        window.ShowDialog();

        return result;
    }
}

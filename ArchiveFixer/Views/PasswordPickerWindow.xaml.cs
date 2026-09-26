using System;
using System.Collections.Generic;
using System.Windows;

namespace ArchiveFixer.Views
{
    /// <summary>
    /// 「从密码列表里选」的小窗口（用户 2026-09-26 第 46 条：打包的密码可以直接从密码列表里挑）。
    ///
    /// <para>它只回传**选中的那一条**给调用方；⛔ 自己不做任何记账、不写日志、不落盘
    /// （不变量 5：密码只存内存）。</para>
    /// </summary>
    public partial class PasswordPickerWindow : Window
    {
        public PasswordPickerWindow(string title, IReadOnlyList<string> passwords)
        {
            InitializeComponent();

            Title = string.IsNullOrWhiteSpace(title) ? "从密码列表里选" : title;

            foreach (string password in passwords ?? Array.Empty<string>())
            {
                PasswordList.Items.Add(password);
            }

            if (PasswordList.Items.Count > 0)
            {
                PasswordList.SelectedIndex = 0;
            }

            HintText.Text = $"从密码列表里挑一条（共 {PasswordList.Items.Count} 条；只在内存里用到，"
                          + "不写进日志、不落盘）。";
        }

        /// <summary>选中的密码（取消时是 null）。</summary>
        public string? Picked { get; private set; }

        private void OnPick(object sender, RoutedEventArgs e) => Confirm();

        private void OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Confirm();

        private void Confirm()
        {
            if (PasswordList.SelectedItem is not string picked)
            {
                MessageBox.Show(this, "先选中一条。", "密码", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Picked = picked;
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            Picked = null;
            DialogResult = false;
        }
    }
}

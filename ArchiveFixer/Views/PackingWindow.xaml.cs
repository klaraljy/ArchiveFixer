using ArchiveFixer.Helpers;
using ArchiveFixer.ViewModels;
using System;
using System.Windows;
using System.Windows.Controls;

namespace ArchiveFixer.Views
{
    /// <summary>
    /// 「打包文件夹为加密分卷」窗口（用户 2026-09-22 需求第 10 条）。
    ///
    /// <para>code-behind 只做三件事：接上 ViewModel、把 <see cref="PasswordBox"/> 的内容推进
    /// ViewModel、关窗。密码走 <c>PasswordChanged</c> 事件而不是绑定 ——
    /// <c>PasswordBox.Password</c> 不是依赖属性，而且明文进绑定链本身就是不必要的暴露面。</para>
    ///
    /// <para>提醒强度用 <see cref="AttentionStrength.Normal"/>：这个窗口是用户**自己点开**的，
    /// 不是"卡住等输入"的那种，不该连响三声。</para>
    /// </summary>
    public partial class PackingWindow : Window
    {
        /// <summary>防重入：程序把值写回 PasswordBox 时会触发 PasswordChanged。</summary>
        private bool _syncingPasswords;

        public PackingWindow()
            : this(new PackingViewModel())
        {
        }

        public PackingWindow(PackingViewModel viewModel)
        {
            InitializeComponent();

            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = ViewModel;

            WindowAttention.Attach(this, AttentionStrength.Normal);
        }

        public PackingViewModel ViewModel { get; }

        private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingPasswords || sender is not PasswordBox box)
            {
                return;
            }

            ViewModel.Password = box.Password;
        }

        private void ConfirmPasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingPasswords || sender is not PasswordBox box)
            {
                return;
            }

            ViewModel.ConfirmPassword = box.Password;
        }

        private void OuterPasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingPasswords || sender is not PasswordBox box)
            {
                return;
            }

            ViewModel.OuterPassword = box.Password;
        }

        private void OuterConfirmPasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingPasswords || sender is not PasswordBox box)
            {
                return;
            }

            ViewModel.OuterConfirmPassword = box.Password;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            /*
             * 关窗就把内存里的密码擦掉（不变量 5：密码只存内存）。
             *
             * ⚠ 这只是"尽量缩短它在内存里的存活时间"，**不是**安全保证：
             * .NET 的字符串是不可变的，GC 之前旧值仍可能留在堆上；真正的防线是
             * "不写盘、不进日志"。这一点写在 docs/使用说明.md 的已知限制里，不假装解决了。
             */
            try
            {
                _syncingPasswords = true;

                PasswordInput.Clear();
                ConfirmPasswordInput.Clear();
                OuterPasswordInput.Clear();
                OuterConfirmPasswordInput.Clear();

                ViewModel.Password = string.Empty;
                ViewModel.ConfirmPassword = string.Empty;
                ViewModel.OuterPassword = string.Empty;
                ViewModel.OuterConfirmPassword = string.Empty;
            }
            catch
            {
                // 关窗路径不允许因为"擦内存"失败而抛。
            }
            finally
            {
                _syncingPasswords = false;
            }

            base.OnClosed(e);
        }
    }
}

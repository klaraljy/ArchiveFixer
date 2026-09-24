using ArchiveFixer.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace ArchiveFixer.Views.Tabs
{
    /// <summary>
    /// ⑤ 打包页的 code-behind。
    ///
    /// <para>
    /// 与原来的打包窗口同一套做法：密码走 <c>PasswordChanged</c> 事件推进 ViewModel
    /// （<c>PasswordBox.Password</c> 不是依赖属性，而且明文进绑定链本身就是不必要的暴露面）。
    /// 页面上的 DataContext 是 <see cref="MainViewModel.PackingEditor"/>，
    /// 所以这里要从主 ViewModel 取那个编辑器。
    /// </para>
    /// <para>
    /// ⚠ 与窗口版的差别（如实记一笔）：窗口版在 <c>OnClosed</c> 里把内存里的密码擦掉；
    /// 选项卡没有"关窗"这个时刻，所以**不再自动擦除** —— 密码仍然只存内存、不写设置、不记日志
    /// （不变量 5 的那条底线没变），只是存活时间长到用户离开这个页面之后。
    /// </para>
    /// </summary>
    public partial class PackingTab : UserControl
    {
        /// <summary>防重入：程序把值写回 PasswordBox 时会触发 PasswordChanged。</summary>
        private bool _syncingPasswords;

        public PackingTab()
        {
            InitializeComponent();
        }

        private PackingViewModel? ViewModel => (DataContext as MainViewModel)?.PackingEditor;

        private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
        {
            Push(sender, (vm, value) => vm.Password = value);
        }

        private void ConfirmPasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
        {
            Push(sender, (vm, value) => vm.ConfirmPassword = value);
        }

        private void OuterPasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
        {
            Push(sender, (vm, value) => vm.OuterPassword = value);
        }

        private void OuterConfirmPasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
        {
            Push(sender, (vm, value) => vm.OuterConfirmPassword = value);
        }

        private void Push(object sender, System.Action<PackingViewModel, string> apply)
        {
            if (_syncingPasswords || sender is not PasswordBox box || ViewModel is not { } viewModel)
            {
                return;
            }

            try
            {
                _syncingPasswords = true;
                apply(viewModel, box.Password);
            }
            finally
            {
                _syncingPasswords = false;
            }
        }
    }
}

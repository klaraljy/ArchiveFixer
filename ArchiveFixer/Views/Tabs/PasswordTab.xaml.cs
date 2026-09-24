using ArchiveFixer.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace ArchiveFixer.Views.Tabs
{
    /// <summary>
    /// ④ 密码页的 code-behind。
    ///
    /// <para>
    /// 只做一件事：<see cref="PasswordBox.Password"/> 不是依赖属性、明文也不该进绑定链
    /// （AGENTS.md §6 第 5 条），所以统一密码的"显示/隐藏"要用一个 TextBox 与一个 PasswordBox
    /// 叠在一起，两边的值由这里同步。原来这段在 <c>MainWindow.xaml.cs</c> 里，跟着控件搬过来。
    /// </para>
    /// </summary>
    public partial class PasswordTab : UserControl
    {
        private bool _syncingPassword;

        /// <summary>已经订阅过的 ViewModel（换 DataContext 时先退订，别让旧对象被挂住）。</summary>
        private System.ComponentModel.INotifyPropertyChanged? _hooked;

        public PasswordTab()
        {
            InitializeComponent();

            Loaded += PasswordTab_Loaded;
            DataContextChanged += PasswordTab_DataContextChanged;
        }

        private void PasswordTab_Loaded(object sender, RoutedEventArgs e)
        {
            HookViewModel();
            SyncPasswordBoxFromViewModel();
        }

        private void PasswordTab_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            HookViewModel();
            SyncPasswordBoxFromViewModel();
        }

        private void HookViewModel()
        {
            if (_hooked != null)
            {
                _hooked.PropertyChanged -= ViewModel_PropertyChanged;
                _hooked = null;
            }

            if (DataContext is System.ComponentModel.INotifyPropertyChanged vm)
            {
                _hooked = vm;
                _hooked.PropertyChanged += ViewModel_PropertyChanged;
            }
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.GlobalPassword) ||
                e.PropertyName == nameof(MainViewModel.ShowPassword))
            {
                SyncPasswordBoxFromViewModel();
            }
        }

        private void SyncPasswordBoxFromViewModel()
        {
            if (_syncingPassword || DataContext is not MainViewModel viewModel || GlobalPasswordBox == null)
            {
                return;
            }

            try
            {
                _syncingPassword = true;

                string password = viewModel.GlobalPassword ?? string.Empty;

                if (GlobalPasswordBox.Password != password)
                {
                    GlobalPasswordBox.Password = password;
                }
            }
            finally
            {
                _syncingPassword = false;
            }
        }

        private void GlobalPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingPassword || DataContext is not MainViewModel viewModel || sender is not PasswordBox box)
            {
                return;
            }

            try
            {
                _syncingPassword = true;
                viewModel.GlobalPassword = box.Password;
            }
            finally
            {
                _syncingPassword = false;
            }
        }
    }
}

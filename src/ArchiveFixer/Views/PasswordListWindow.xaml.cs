using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.ComponentModel;
using System.Windows;

namespace ArchiveFixer.Views
{
    public partial class PasswordListWindow : Window
    {
        private PasswordListViewModel? _viewModel;

        public PasswordListViewModel ViewModel
        {
            get
            {
                if (_viewModel == null)
                {
                    _viewModel = new PasswordListViewModel(new PasswordService(), new DialogService());
                    DataContext = _viewModel;
                    AttachViewModel(_viewModel);
                }

                return _viewModel;
            }
            private set
            {
                if (ReferenceEquals(_viewModel, value))
                {
                    return;
                }

                DetachViewModel(_viewModel);

                _viewModel = value;
                DataContext = _viewModel;

                AttachViewModel(_viewModel);
            }
        }

        public PasswordListWindow()
        {
            InitializeComponent();

            // 模态子窗：不许最小化（A2），但保留拖边框改大小。
            WindowMinimizePolicy.Apply(this);

            DataContextChanged += PasswordListWindow_DataContextChanged;

            /*
             * 密码本窗口是高强度提醒（用户 2026-09-22 反馈：它"躲到主窗口后面，只有声音、没有闪烁，
             * 声音还很轻"）。这里的强度固定为 Strong —— 解压卡在"等密码"上时整批都不动，
             * 用户必须过来处理，所以一直闪到被点 + 连响三声。
             */
            ArchiveFixer.Helpers.WindowAttention.Attach(this, ArchiveFixer.Helpers.AttentionStrength.Strong);

            if (DataContext is PasswordListViewModel existingViewModel)
            {
                ViewModel = existingViewModel;
            }
            else
            {
                ViewModel = new PasswordListViewModel(new PasswordService(), new DialogService());
            }
        }

        public PasswordListWindow(PasswordListViewModel viewModel)
        {
            InitializeComponent();

            // 模态子窗：不许最小化（A2），但保留拖边框改大小。
            WindowMinimizePolicy.Apply(this);

            DataContextChanged += PasswordListWindow_DataContextChanged;

            ArchiveFixer.Helpers.WindowAttention.Attach(this, ArchiveFixer.Helpers.AttentionStrength.Strong);

            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        }

        private void PasswordListWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (ReferenceEquals(e.NewValue, _viewModel))
            {
                return;
            }

            if (e.NewValue is PasswordListViewModel newViewModel)
            {
                DetachViewModel(_viewModel);
                _viewModel = newViewModel;
                AttachViewModel(_viewModel);
            }
        }

        private void AttachViewModel(PasswordListViewModel? viewModel)
        {
            if (viewModel == null)
            {
                return;
            }

            viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        private void DetachViewModel(PasswordListViewModel? viewModel)
        {
            if (viewModel == null)
            {
                return;
            }

            viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            DataContextChanged -= PasswordListWindow_DataContextChanged;
            DetachViewModel(_viewModel);

            /*
             * 退订密码服务上的事件（那个服务与主界面同寿命，不退订就把这个 ViewModel 一直拽住）。
             * 只调"退订"这一个公开方法，不碰主 ViewModel 的任何状态。
             */
            _viewModel?.Detach();

            /*
             * 关窗时刷新主界面的密码本摘要（「密码本：x 条（启用 y 条）」那一行）。
             *
             * 为什么由窗口来推这一下：主 ViewModel 只在**关窗之后**才刷新摘要
             * （MainViewModel.OpenPasswordList 里 ShowDialog 之后那一句），
             * 而窗口里刚导入完、密码列表已经变了的时候，主界面那一行还是旧的。
             * 这里主动推一次，用户一关窗就能看到新数字，不必再等一次开关窗口。
             *
             * 只调"刷新显示"这一个公开方法，不碰主 ViewModel 的任何状态。
             */
            try
            {
                if (Application.Current?.MainWindow?.DataContext is MainViewModel mainViewModel)
                {
                    mainViewModel.RefreshPasswordBookSummary();
                }
            }
            catch
            {
                // 主窗口还没建好等边缘情况：刷新失败不影响关窗。
            }

            base.OnClosed(e);
        }
    }
}

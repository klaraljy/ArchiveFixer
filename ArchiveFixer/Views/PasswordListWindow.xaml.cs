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

            DataContextChanged += PasswordListWindow_DataContextChanged;

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

            DataContextChanged += PasswordListWindow_DataContextChanged;

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
            base.OnClosed(e);
        }
    }
}

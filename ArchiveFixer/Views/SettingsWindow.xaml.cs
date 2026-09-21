using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.ComponentModel;
using System.Windows;

namespace ArchiveFixer.Views
{
    public partial class SettingsWindow : Window
    {
        private SettingsViewModel? _viewModel;

        public SettingsViewModel ViewModel
        {
            get
            {
                if (_viewModel == null)
                {
                    _viewModel = new SettingsViewModel(new AppSettings(), new SettingsService());
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
                AttachViewModel(_viewModel);
                DataContext = _viewModel;
            }
        }

        public AppSettings ResultSettings => ViewModel.Settings;

        public SettingsWindow()
            : this(new AppSettings(), new SettingsService())
        {
        }

        public SettingsWindow(AppSettings settings)
            : this(settings, new SettingsService())
        {
        }

        public SettingsWindow(AppSettings settings, SettingsService settingsService)
        {
            InitializeComponent();

            DataContextChanged += SettingsWindow_DataContextChanged;

            ViewModel = new SettingsViewModel(settings, settingsService);
            DataContext = ViewModel;
        }

        private void SettingsWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is SettingsViewModel newViewModel)
            {
                ViewModel = newViewModel;
            }
        }

        private void AttachViewModel(SettingsViewModel? viewModel)
        {
            if (viewModel == null)
            {
                return;
            }

            viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        private void DetachViewModel(SettingsViewModel? viewModel)
        {
            if (viewModel == null)
            {
                return;
            }

            viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SettingsViewModel.DialogResult))
            {
                return;
            }

            if (!ViewModel.DialogResult.HasValue)
            {
                return;
            }

            DialogResult = ViewModel.DialogResult.Value;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            DataContextChanged -= SettingsWindow_DataContextChanged;
            DetachViewModel(_viewModel);
            base.OnClosed(e);
        }
    }
}

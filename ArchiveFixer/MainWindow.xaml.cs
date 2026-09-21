using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using ArchiveFixer.Views;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ArchiveFixer
{
    public partial class MainWindow : Window
    {
        private bool _syncingPassword;

        public MainWindow()
        {
            InitializeComponent();

            Loaded += MainWindow_Loaded;
            DataContextChanged += MainWindow_DataContextChanged;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            HookViewModelPropertyChanged();
            SyncPasswordBoxFromViewModel();
        }

        private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is INotifyPropertyChanged oldVm)
            {
                oldVm.PropertyChanged -= ViewModel_PropertyChanged;
            }

            HookViewModelPropertyChanged();
            SyncPasswordBoxFromViewModel();
        }

        private void HookViewModelPropertyChanged()
        {
            if (DataContext is INotifyPropertyChanged vm)
            {
                vm.PropertyChanged -= ViewModel_PropertyChanged;
                vm.PropertyChanged += ViewModel_PropertyChanged;
            }
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.GlobalPassword) ||
                e.PropertyName == nameof(MainViewModel.ShowPassword))
            {
                SyncPasswordBoxFromViewModel();
            }
        }

        private void SyncPasswordBoxFromViewModel()
        {
            if (_syncingPassword)
            {
                return;
            }

            if (DataContext is not MainViewModel viewModel)
            {
                return;
            }

            if (GlobalPasswordBox == null)
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
            if (_syncingPassword)
            {
                return;
            }

            if (DataContext is not MainViewModel viewModel)
            {
                return;
            }

            if (sender is not PasswordBox passwordBox)
            {
                return;
            }

            try
            {
                _syncingPassword = true;
                viewModel.GlobalPassword = passwordBox.Password;
            }
            finally
            {
                _syncingPassword = false;
            }
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    return;
                }

                if (DataContext is not MainViewModel viewModel)
                {
                    return;
                }

                string[]? paths = e.Data.GetData(DataFormats.FileDrop) as string[];

                if (paths == null || paths.Length == 0)
                {
                    return;
                }

                e.Handled = true;

                await viewModel.AddPathsAsync(paths);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "拖拽导入失败：" + ex.Message,
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }

            e.Handled = true;
        }

        /// <summary>
        /// 右键点击 DataGrid 行时，自动选中当前行。
        /// 否则右键菜单可能操作的是上一次选中的任务。
        /// </summary>
        private void TaskDataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject? source = e.OriginalSource as DependencyObject;

            DataGridRow? row = FindParent<DataGridRow>(source);

            if (row != null)
            {
                row.IsSelected = true;
                TaskDataGrid.SelectedItem = row.Item;
                TaskDataGrid.Focus();
            }
        }

        /// <summary>
        /// 右键菜单：智能修正此文件后缀。
        /// 这里会把当前右键的任务设为 IsSelected，然后调用主 ViewModel 的 SmartRenameCommand。
        /// </summary>
        private void SmartRenameCurrentTaskMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel viewModel)
            {
                return;
            }

            if (TaskDataGrid?.SelectedItem is not ArchiveTask task)
            {
                MessageBox.Show(
                    this,
                    "请先选择一个任务。",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            foreach (ArchiveTask item in viewModel.Tasks)
            {
                item.IsSelected = false;
            }

            task.IsSelected = true;

            if (viewModel.SmartRenameCommand.CanExecute(null))
            {
                viewModel.SmartRenameCommand.Execute(null);
            }
        }

        private void TaskDetailMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (TaskDataGrid?.SelectedItem is not ArchiveTask task)
            {
                MessageBox.Show(
                    this,
                    "请先选择一个任务。",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            var window = new TaskDetailWindow(task)
            {
                Owner = this
            };

            window.ShowDialog();
        }

        private static T? FindParent<T>(DependencyObject? child)
            where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T target)
                {
                    return target;
                }

                child = VisualTreeHelper.GetParent(child);
            }

            return null;
        }
    }
}

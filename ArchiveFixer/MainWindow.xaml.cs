using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using ArchiveFixer.Views;
using System;
using System.Collections.Specialized;
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
        private readonly DialogService _dialogService = new();

        private INotifyCollectionChanged? _hookedLogs;
        private ScrollViewer? _logScrollViewer;
        private bool _logPinnedToBottom = true;

        /// <summary>日志滚动已排队（一个排空周期只滚一次，见 <see cref="Logs_CollectionChanged"/>）。</summary>
        private bool _scrollToEndPending;

        public MainWindow()
        {
            InitializeComponent();

            Loaded += MainWindow_Loaded;
            DataContextChanged += MainWindow_DataContextChanged;

            /*
             * Ctrl+A 的可靠入口（用户 2026-09-22 追加需求："有一个全选的选项"）。
             *
             * 为什么不用 Window.InputBindings 里的 KeyBinding：DataGrid **自己**注册了 Ctrl+A
             * （它选的是"行高亮"，跟勾选框是两套东西），焦点在表格里时那条键轮不到窗口级绑定。
             * PreviewKeyDown 是**隧道**事件，在 DataGrid 看到之前就到达窗口 —— 于是
             * "Ctrl+A = 勾选全部"这件事在本窗口里是确定的。
             */
            PreviewKeyDown += MainWindow_PreviewKeyDown;
        }

        /// <summary>
        /// Ctrl+A / Ctrl+D / Ctrl+I：全选 / 全不选 / 反选（都作用在**勾选框**上）。
        ///
        /// <para>
        /// Ctrl+D 与 Ctrl+I 走 <c>Window.InputBindings</c> 就够了（DataGrid 没用这两个键），
        /// 只有 Ctrl+A 需要在这里抢下来 —— 见构造函数的注释。
        /// </para>
        /// </summary>
        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.A || (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
            {
                return;
            }

            if (DataContext is not MainViewModel viewModel)
            {
                return;
            }

            // 只在焦点还在任务列表（或窗口本体）时接管：别把密码框等处的 Ctrl+A
            //（"全选文本"）也吃掉 —— 那会变成另一种"点了没用"。
            if (Keyboard.FocusedElement is DependencyObject focused && !IsWithinTaskGrid(focused))
            {
                return;
            }

            if (!viewModel.SelectAllTasksCommand.CanExecute(null))
            {
                return;
            }

            viewModel.SelectAllTasksCommand.Execute(null);
            e.Handled = true;
        }

        /// <summary>焦点是不是落在任务表格里（表格自身、行、单元格、勾选框都算）。</summary>
        private bool IsWithinTaskGrid(DependencyObject? focused)
        {
            while (focused != null)
            {
                if (ReferenceEquals(focused, TaskDataGrid))
                {
                    return true;
                }

                focused = focused is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(focused)
                    : LogicalTreeHelper.GetParent(focused);
            }

            return false;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            HookViewModelPropertyChanged();
            SyncPasswordBoxFromViewModel();
            HookLogAutoScroll();
        }

        private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is INotifyPropertyChanged oldVm)
            {
                oldVm.PropertyChanged -= ViewModel_PropertyChanged;
            }

            HookViewModelPropertyChanged();
            SyncPasswordBoxFromViewModel();
            HookLogAutoScroll();
        }

        /// <summary>
        /// 日志区跟随最新一行。
        ///
        /// 为什么要有它：以前追加日志**不滚动**，跑完一批之后日志区还停在最早那几行，
        /// 用户看到的是"什么都没发生 / 卡住了"（端到端验收时实测到这个问题）。
        ///
        /// 但也不能无条件滚：用户往上翻看历史时被一直拽回底部同样难受。
        /// 所以用"当前是否贴底"作为开关 —— 贴底才跟随，翻上去就尊重用户。
        /// </summary>
        private void HookLogAutoScroll()
        {
            if (_hookedLogs != null)
            {
                _hookedLogs.CollectionChanged -= Logs_CollectionChanged;
                _hookedLogs = null;
            }

            if (_logScrollViewer != null)
            {
                _logScrollViewer.ScrollChanged -= LogScrollViewer_ScrollChanged;
                _logScrollViewer = null;
            }

            if (DataContext is MainViewModel vm && vm.Logs is INotifyCollectionChanged notifier)
            {
                _hookedLogs = notifier;
                _hookedLogs.CollectionChanged += Logs_CollectionChanged;
            }

            // ListBox 的 ScrollViewer 要等模板应用后才在可视树里，Loaded 时取一次即可。
            _logScrollViewer = FindDescendant<ScrollViewer>(LogList);

            if (_logScrollViewer != null)
            {
                _logScrollViewer.ScrollChanged += LogScrollViewer_ScrollChanged;
            }

            _logPinnedToBottom = true;
        }

        private void LogScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            /*
             * 只认"用户真的滚了"（VerticalChange != 0）。
             * 内容变多也会触发 ScrollChanged，但那时 VerticalChange 为 0 ——
             * 若把它也算进来，刚追加一行就会因为"底部变远了"而被判成"没贴底"，
             * 后续日志再也不会跟随（这正是这类实现最常见的坑）。
             */
            if (Math.Abs(e.VerticalChange) < 0.1)
            {
                return;
            }

            // 8px 容差：滚动偏移是浮点，正好到底时未必严格相等。
            _logPinnedToBottom = e.ExtentHeight <= e.ViewportHeight ||
                                 e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 8;
        }

        private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != NotifyCollectionChangedAction.Add || LogList.Items.Count == 0)
            {
                return;
            }

            if (!_logPinnedToBottom)
            {
                return;
            }

            /*
             * 合并滚动（用户 2026-09-24 第 12 条"卡死"的修法之一）。
             *
             * 旧写法是**每加一行就 ScrollIntoView** —— 而导入/一键处理会连着打几十上百行
             * （批首的排期日志就是一个没有 await 的循环里连打 N+2 行），每行都强制一次
             * 布局 + 滚动命中测试，界面线程被日志拖着走。
             *
             * 现在一个"排空周期"只滚一次：请求排在 Background 优先级上，
             * 那些同样是排队进来的日志行（Normal 优先级）先全部处理完，滚动才发生。
             * 用户看到的效果一样（始终贴在最后一行），省掉的是成百次布局。
             */
            if (_scrollToEndPending)
            {
                return;
            }

            _scrollToEndPending = true;

            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() =>
                {
                    _scrollToEndPending = false;

                    if (!_logPinnedToBottom || LogList.Items.Count == 0)
                    {
                        return;
                    }

                    LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
                }));
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);

                if (child is T hit)
                {
                    return hit;
                }

                T? deeper = FindDescendant<T>(child);

                if (deeper != null)
                {
                    return deeper;
                }
            }

            return null;
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
                // 拖拽导入失败也要走统一对话框（系统 MessageBox 与本程序的观感不搭）
                _dialogService.ShowException(ex, "拖拽导入失败");
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
        ///
        /// SmartRenameCommand 作用在"勾选"的任务上（OneClickCoordinator 里判的就是 IsSelected），
        /// 所以右键单个文件时必须把它自己勾上、并让其余任务退出这次操作的 scope。
        /// 旧写法的问题不是"清了别的勾选"，而是**清了却一句提示都没有**：用户勾了 20 个，
        /// 右键其中一个修后缀，汇总区的"选中："从 20 变成 1，紧接着「一键处理」「移除选中」
        /// 「清空列表」的作用范围全变了，日志里查不到原因。
        /// 兜底做法：作用范围写在列表上方的常驻提示行里，用户点之前就知道这次会动几个。
        /// </summary>
        private void SmartRenameCurrentTaskMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel viewModel)
            {
                return;
            }

            if (TaskDataGrid?.SelectedItem is not ArchiveTask task)
            {
                _dialogService.ShowInfo("请先选择一个任务。");
                return;
            }

            /*
             * 一次"只留这一个"（用户 2026-09-24 第 12 条"卡死"的修法之一）。
             *
             * 旧写法逐项 item.IsSelected = false —— 每一项都会触发一次全表汇总重算
             * （MainViewModel.Task_SelectionPropertyChanged）+ 38 条命令可用性重查，
             * 几百项的任务列表就是几百次全表重算。现在套既有批量守卫：
             * 整段改完只刷**一次**汇总（磁盘上一个字节都不动）。
             */
            viewModel.RunBulkSelectionUpdate(() =>
            {
                foreach (ArchiveTask item in viewModel.Tasks)
                {
                    item.IsSelected = ReferenceEquals(item, task);
                }
            });

            if (viewModel.SmartRenameCommand.CanExecute(null))
            {
                viewModel.SmartRenameCommand.Execute(null);
            }
        }

        private void TaskDetailMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (TaskDataGrid?.SelectedItem is not ArchiveTask task)
            {
                _dialogService.ShowInfo("请先选择一个任务。");
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

using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using ArchiveFixer.Views;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ArchiveFixer.Views.Tabs
{
    /// <summary>
    /// ① 任务页的 code-behind。
    ///
    /// <para>
    /// 这里装的是**只有视图才能做**的事：日志跟随最新一行、右键选中当前行、
    /// 日志区的拖动与收起、Ctrl+A 的抢占。原来这些都在 <c>MainWindow.xaml.cs</c> 里
    /// （那时界面只有一页）；搬进选项卡之后它们必须跟着控件走 ——
    /// 窗口级的 code-behind 已经看不到选项卡内部的控件了。
    /// </para>
    /// <para>
    /// 业务命令一个都不在这里：所有按钮仍然绑 <see cref="MainViewModel"/> 的命令，
    /// 本文件只处理"控件自身的交互"。
    /// </para>
    /// </summary>
    public partial class TaskTab : UserControl
    {
        private readonly DialogService _dialogService = new();
        private readonly LogAutoScroll _logAutoScroll;

        /// <summary>日志区当前是不是收起状态（视图菜单与按钮共用这一份状态）。</summary>
        private bool _logAreaCollapsed;

        public TaskTab()
        {
            InitializeComponent();

            _logAutoScroll = new LogAutoScroll(LogList);

            Loaded += TaskTab_Loaded;
            DataContextChanged += TaskTab_DataContextChanged;
        }

        /// <summary>
        /// 焦点是不是落在任务表格里（表格自身、行、单元格、勾选框都算）。
        ///
        /// <para>
        /// Ctrl+A 的抢占留在窗口那一层（<c>MainWindow.PreviewKeyDown</c>）：
        /// DataGrid 自己注册了 Ctrl+A，必须在隧道阶段就截住。但"焦点在不在表格里"
        /// 只有本页知道，所以由窗口反过来问这一句 —— 顺带保证密码框里的 Ctrl+A（全选文本）
        /// 不会被吃掉。
        /// </para>
        /// </summary>
        public bool IsKeyboardFocusWithinTaskGrid(DependencyObject? focused)
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

        private void TaskTab_Loaded(object sender, RoutedEventArgs e)
        {
            HookLogAutoScroll();
        }

        private void TaskTab_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            HookLogAutoScroll();
        }

        private void HookLogAutoScroll()
        {
            _logAutoScroll.Attach((DataContext as MainViewModel)?.Logs);
        }

        /// <summary>「日志放大」：把日志开到独立窗口（用户自己点开，不置前、不最大化）。</summary>
        private void OpenLogWindowButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var window = new LogWindow
                {
                    DataContext = DataContext,
                    Owner = Window.GetWindow(this)
                };

                window.Show();
            }
            catch (Exception ex)
            {
                _dialogService.ShowException(ex, "打开日志窗口失败");
            }
        }

        private void ToggleLogAreaButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleLogArea();
        }

        /// <summary>
        /// 收起 / 展开日志区（「视图 → 展开或收起日志区」也走这里，两处共用同一份状态）。
        ///
        /// <para>
        /// 收起时行高设为 0、并把分隔条一起藏掉 —— 留着一条 6px 的分隔条会让用户
        /// 以为"日志还在下面、只是被压扁了"，然后去拖它。
        /// </para>
        /// </summary>
        public void ToggleLogArea()
        {
            _logAreaCollapsed = !_logAreaCollapsed;

            LogRow.Height = _logAreaCollapsed ? new GridLength(0) : new GridLength(28, GridUnitType.Star);
            LogSplitter.Visibility = _logAreaCollapsed ? Visibility.Collapsed : Visibility.Visible;
            LogArea.Visibility = _logAreaCollapsed ? Visibility.Collapsed : Visibility.Visible;
            LogToggleButton.Content = _logAreaCollapsed ? "展开" : "收起";
        }

        /// <summary>「视图 → 跳到我选中的任务」：把列表滚到当前选中的那一行。</summary>
        public void ScrollToSelectedTask()
        {
            if (DataContext is MainViewModel vm && vm.SelectedTask != null)
            {
                TaskDataGrid.ScrollIntoView(vm.SelectedTask);
            }
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
        /// <para>
        /// SmartRenameCommand 作用在"勾选"的任务上，所以右键单个文件时必须把它自己勾上、
        /// 并让其余任务退出这次操作的 scope。一次批量"只留这一个"（不逐项改 → 不触发 N 次全表重算）。
        /// </para>
        /// <para>
        /// ⚠ "用完把勾选还回去"这一条在 <see cref="MainViewModel.RunSmartRenameForSingleTaskAsync"/> 里：
        /// 以前这里改完就完事，于是用户批量勾了 20 个、右键修一个，回来发现勾选全没了（2026-09-26 审计）。
        /// </para>
        /// </summary>
        private async void SmartRenameCurrentTaskMenuItem_Click(object sender, RoutedEventArgs e)
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

            await viewModel.RunSmartRenameForSingleTaskAsync(task);
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
                Owner = Window.GetWindow(this)
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

using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using System.Windows;

namespace ArchiveFixer.Views
{
    /// <summary>
    /// 手动操作弹窗：只在用户选中一个任务并主动点「手动操作」时出现。
    /// 不占任务页宽度，也不改变主流程；窗口里的命令仍只作用于当前 SelectedTask。
    /// </summary>
    public partial class ManualOperationWindow : Window
    {
        public ManualOperationWindow(MainViewModel viewModel, ArchiveTask task)
        {
            InitializeComponent();

            WindowMinimizePolicy.Apply(this);
            DataContext = viewModel;
            Title = $"手动操作 - {task.LogName}";

            // 打开即按基名做一次只读查找；扫描在后台线程，不阻塞窗口显示。
            Loaded += (_, _) =>
            {
                if (viewModel.FindVolumeSiblingsCommand.CanExecute(null))
                {
                    viewModel.FindVolumeSiblingsCommand.Execute(null);
                }
            };
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

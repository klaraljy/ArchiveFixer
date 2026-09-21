using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Text;
using System.Windows;

namespace ArchiveFixer.Views
{
    /// <summary>
    /// 任务详情窗口。
    /// 只展示任务信息，不展示明文密码。
    /// </summary>
    public partial class TaskDetailWindow : Window
    {
        private readonly ClipboardService _clipboardService;

        public ArchiveTask? TaskItem { get; private set; }

        public TaskDetailWindow()
        {
            InitializeComponent();

            _clipboardService = new ClipboardService();

            TaskLogTextBox.Text = "未传入任务。";
        }

        public TaskDetailWindow(ArchiveTask task)
        {
            InitializeComponent();

            _clipboardService = new ClipboardService();

            SetTask(task);
        }

        public void SetTask(ArchiveTask? task)
        {
            TaskItem = task;
            DataContext = task;

            if (task == null)
            {
                TaskLogTextBox.Text = "未传入任务。";
                return;
            }

            TaskLogTextBox.Text = BuildDetailText(task);
        }

        private void CopyAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (TaskItem == null)
            {
                MessageBox.Show(
                    this,
                    "没有可复制的任务信息。",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            try
            {
                string text = BuildDetailText(TaskItem);
                _clipboardService.CopyText(text);

                MessageBox.Show(
                    this,
                    "任务信息已复制到剪贴板。",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "复制失败：" + ex.Message,
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private static string BuildDetailText(ArchiveTask task)
        {
            if (task == null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();

            builder.AppendLine("【基本信息】");
            builder.AppendLine("序号：" + task.Index);
            builder.AppendLine("文件名：" + Safe(task.FileName));
            builder.AppendLine("原始路径：" + Safe(task.OriginalPath));
            builder.AppendLine("当前路径：" + Safe(task.CurrentPath));
            builder.AppendLine("所在目录：" + Safe(task.DirectoryPath));
            builder.AppendLine();

            builder.AppendLine("【识别与后缀】");
            builder.AppendLine("当前后缀：" + Safe(task.CurrentExtension));
            builder.AppendLine("检测格式：" + Safe(task.DetectedFormat));
            builder.AppendLine("建议后缀：" + Safe(task.SuggestedExtension));
            builder.AppendLine("后缀状态：" + Safe(task.ExtensionStatus));
            builder.AppendLine("是否压缩包：" + task.IsArchive);
            builder.AppendLine("是否加密：" + task.IsEncrypted);
            builder.AppendLine();

            builder.AppendLine("【解压与状态】");
            builder.AppendLine("输出目录：" + Safe(task.OutputPath));

            // 注意：这里只显示密码状态，不显示 task.Password。
            builder.AppendLine("密码状态：" + Safe(task.PasswordStatus));

            builder.AppendLine("当前操作：" + Safe(task.Operation));
            builder.AppendLine("状态：" + Safe(task.Status));
            builder.AppendLine("进度：" + Safe(task.ProgressText));
            builder.AppendLine("错误信息：" + Safe(task.ErrorMessage));
            builder.AppendLine();

            builder.AppendLine("【时间信息】");
            builder.AppendLine("开始时间：" + FormatTime(task.StartTime));
            builder.AppendLine("结束时间：" + FormatTime(task.EndTime));
            builder.AppendLine("耗时：" + Safe(task.ElapsedText));
            builder.AppendLine("最后更新：" + FormatTime(task.LastUpdatedTime));
            builder.AppendLine();

            builder.AppendLine("【安全说明】");
            builder.AppendLine("此详情信息不会包含明文密码。");

            return builder.ToString();
        }

        private static string Safe(string? value)
        {
            return value ?? string.Empty;
        }

        private static string FormatTime(DateTime? time)
        {
            if (!time.HasValue)
            {
                return string.Empty;
            }

            if (time.Value == default)
            {
                return string.Empty;
            }

            return time.Value.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
}

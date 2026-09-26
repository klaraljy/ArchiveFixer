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
    ///
    /// <para>⚠ 窗口里那份文本快照必须跟着任务走（2026-09-26 同步审计逮到）：状态 / 进度那几行是**绑定**
    /// （实时），而下面这个文本框是打开那一刻拼的一份 —— 给一个正在跑的任务打开详情，
    /// 上面写"解压中 45%"、下面还停在"已识别"，点「复制全部」拿到的却是新的（屏幕与剪贴板不一致，
    /// 而这个文件的注释自己写着"跟屏幕上看的不一致等于白贴"）。现在订阅任务的属性变化，值一变就重拼。</para>
    /// </summary>
    public partial class TaskDetailWindow : Window
    {
        private readonly ClipboardService _clipboardService;
        private readonly DialogService _dialogService;

        private ArchiveTask? _subscribedTask;

        public ArchiveTask? TaskItem { get; private set; }

        public TaskDetailWindow()
        {
            InitializeComponent();

            _clipboardService = new ClipboardService();
            _dialogService = new DialogService();

            TaskLogTextBox.Text = "未传入任务。";
        }

        public TaskDetailWindow(ArchiveTask task)
        {
            InitializeComponent();

            _clipboardService = new ClipboardService();
            _dialogService = new DialogService();

            SetTask(task);
        }

        public void SetTask(ArchiveTask? task)
        {
            if (_subscribedTask != null)
            {
                _subscribedTask.PropertyChanged -= OnTaskPropertyChanged;
                _subscribedTask = null;
            }

            TaskItem = task;
            DataContext = task;

            if (task == null)
            {
                TaskLogTextBox.Text = "未传入任务。";
                return;
            }

            task.PropertyChanged += OnTaskPropertyChanged;
            _subscribedTask = task;

            TaskLogTextBox.Text = BuildDetailText(task);
        }

        /// <summary>任务上任何一个值变了就重拼那一份文本（只有窗口开着时才订阅，见 SetTask / OnClosed）。</summary>
        private void OnTaskPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (TaskItem != null)
            {
                TaskLogTextBox.Text = BuildDetailText(TaskItem);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            // ⛔ 必须退订：任务活得比窗口长（列表里还留着），不退订就是内存泄漏 + 关窗之后还在拼字符串。
            if (_subscribedTask != null)
            {
                _subscribedTask.PropertyChanged -= OnTaskPropertyChanged;
                _subscribedTask = null;
            }

            base.OnClosed(e);
        }

        private void CopyAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (TaskItem == null)
            {
                _dialogService.ShowInfo("没有可复制的任务信息。");
                return;
            }

            try
            {
                string text = BuildDetailText(TaskItem);
                _clipboardService.CopyText(text);

                _dialogService.ShowInfo("任务信息已复制到剪贴板（不含明文密码）。");
            }
            catch (Exception ex)
            {
                _dialogService.ShowException(ex, "复制失败");
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

            // 主界面 DataGrid 的「分卷」列默认隐藏，这里补上，保证"缺哪几卷"始终看得到。
            builder.AppendLine("分卷：" + Safe(task.VolumeInfoText));

            builder.AppendLine("当前操作：" + Safe(task.Operation));

            /*
             * 状态 / 进度都用**界面上真正显示的那一份**（StatusDisplayText / ProgressDetail）。
             * 复制出去的那份文本是用户贴回来排障用的，跟屏幕上看的不一致等于白贴 ——
             * 而任务在跑的时候屏幕上就是"解压中 45%"，这里也必须是"解压中 45%"。
             */
            builder.AppendLine("状态：" + Safe(task.StatusDisplayText));
            builder.AppendLine("进度：" + Safe(task.ProgressDetail));
            builder.AppendLine("错误信息：" + Safe(task.ErrorMessage));

            /*
             * 解压前那一遍条目预检算出来的两条提示（可疑条目 / 路径过长）。
             * 窗口里空着就收起，但**复制出去的那一份要能带走完整的排障信息** ——
             * 用户把详情粘给我时，"这个包里有 3 个 exe"往往就是关键线索。
             *
             * ⚠ 这里**不加**标签前缀：两个值本身就是完整的句子，且各自带前缀
             * （"可疑条目提示：…" / "路径过长，可能失败：…"）—— 再加一层标签就会印成
             * "路径过长：路径过长，可能失败：…"。没有内容时整行不写（不编"无"）。
             */
            if (!string.IsNullOrWhiteSpace(task.DangerousEntriesWarning))
            {
                builder.AppendLine(task.DangerousEntriesWarning);
            }

            if (!string.IsNullOrWhiteSpace(task.PathLengthWarning))
            {
                builder.AppendLine(task.PathLengthWarning);
            }

            // "长时间无响应"提示：同样"空着就不写"，复制出去的那一份要带上（它正是
            // 用户问"是不是卡死了"时最需要的那条信息）。
            if (!string.IsNullOrWhiteSpace(task.ResponsivenessHint))
            {
                builder.AppendLine(task.ResponsivenessHint);
            }

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

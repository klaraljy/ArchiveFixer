using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 剪贴板服务。
    /// 
    /// 职责：
    /// 1. 复制普通文本。
    /// 2. 复制失败列表。
    /// 3. 复制任务路径。
    /// 4. 复制错误信息。
    /// 
    /// 注意：
    /// 不复制明文密码。
    /// </summary>
    public class ClipboardService
    {
        private readonly TaskSummaryService _taskSummaryService;

        public ClipboardService()
        {
            _taskSummaryService = new TaskSummaryService();
        }

        public ClipboardService(TaskSummaryService taskSummaryService)
        {
            _taskSummaryService = taskSummaryService ?? new TaskSummaryService();
        }

        /// <summary>
        /// 复制文本到剪贴板。
        /// </summary>
        public bool CopyText(string text)
        {
            try
            {
                Clipboard.SetText(text ?? string.Empty);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 复制失败列表。
        /// </summary>
        public bool CopyFailedList(IEnumerable<ArchiveTask> tasks)
        {
            string text = _taskSummaryService.BuildFailedListText(tasks);
            return CopyText(text);
        }

        /// <summary>
        /// 复制任务当前路径。
        /// </summary>
        public bool CopyTaskPath(ArchiveTask task)
        {
            if (task == null)
            {
                return CopyText(string.Empty);
            }

            return CopyText(task.CurrentPath ?? string.Empty);
        }

        /// <summary>
        /// 复制任务原始路径。
        /// </summary>
        public bool CopyOriginalTaskPath(ArchiveTask task)
        {
            if (task == null)
            {
                return CopyText(string.Empty);
            }

            return CopyText(task.OriginalPath ?? string.Empty);
        }

        /// <summary>
        /// 复制错误信息。
        /// </summary>
        public bool CopyErrorMessage(ArchiveTask task)
        {
            if (task == null)
            {
                return CopyText(string.Empty);
            }

            return CopyText(task.ErrorMessage ?? string.Empty);
        }

        /// <summary>
        /// 复制任务简要信息。
        /// 不包含明文密码。
        /// </summary>
        public bool CopyTaskInfo(ArchiveTask task)
        {
            if (task == null)
            {
                return CopyText(string.Empty);
            }

            var builder = new StringBuilder();

            builder.AppendLine("序号：" + task.Index);
            builder.AppendLine("文件名：" + (task.FileName ?? string.Empty));
            builder.AppendLine("原始路径：" + (task.OriginalPath ?? string.Empty));
            builder.AppendLine("当前路径：" + (task.CurrentPath ?? string.Empty));
            builder.AppendLine("当前后缀：" + (task.CurrentExtension ?? string.Empty));
            builder.AppendLine("检测格式：" + (task.DetectedFormat ?? string.Empty));
            builder.AppendLine("建议后缀：" + (task.SuggestedExtension ?? string.Empty));
            builder.AppendLine("后缀状态：" + (task.ExtensionStatus ?? string.Empty));
            builder.AppendLine("密码状态：" + (task.PasswordStatus ?? string.Empty));
            builder.AppendLine("输出目录：" + (task.OutputPath ?? string.Empty));
            builder.AppendLine("操作：" + (task.Operation ?? string.Empty));
            builder.AppendLine("状态：" + (task.Status ?? string.Empty));
            builder.AppendLine("进度：" + (task.ProgressText ?? string.Empty));
            builder.AppendLine("错误信息：" + (task.ErrorMessage ?? string.Empty));
            builder.AppendLine("耗时：" + (task.ElapsedText ?? string.Empty));

            return CopyText(builder.ToString().TrimEnd());
        }

        /// <summary>
        /// 复制多个任务路径。
        /// </summary>
        public bool CopyTaskPaths(IEnumerable<ArchiveTask> tasks)
        {
            var builder = new StringBuilder();

            if (tasks != null)
            {
                foreach (ArchiveTask task in tasks)
                {
                    if (task == null)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(task.CurrentPath))
                    {
                        builder.AppendLine(task.CurrentPath);
                    }
                }
            }

            return CopyText(builder.ToString().TrimEnd());
        }
    }
}

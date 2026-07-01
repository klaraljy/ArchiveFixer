using ArchiveFixer.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 任务汇总服务。
    /// </summary>
    public class TaskSummaryService
    {
        /// <summary>
        /// 构建任务汇总。
        /// </summary>
        public TaskSummary BuildSummary(IEnumerable<ArchiveTask> tasks)
        {
            var list = tasks?.Where(x => x != null).ToList() ?? new List<ArchiveTask>();

            return new TaskSummary
            {
                TotalCount = list.Count,
                SelectedCount = list.Count(x => x.IsSelected),

                RecognizedCount = list.Count(x =>
                    x.IsArchive ||
                    x.Status == "已识别" ||
                    x.ExtensionStatus == "后缀正常" ||
                    x.ExtensionStatus == "后缀缺失" ||
                    x.ExtensionStatus == "后缀不匹配" ||
                    x.ExtensionStatus == "多重后缀疑似伪装"),

                UnknownCount = list.Count(x =>
                    x.DetectedFormat == "Unknown" ||
                    x.Status == "格式未知" ||
                    x.ExtensionStatus == "格式未知"),

                RenameSuccessCount = list.Count(x => x.Status == "改名成功"),
                RenameFailedCount = list.Count(x => x.Status == "改名失败"),

                TestSuccessCount = list.Count(x => x.Status == "测试通过"),
                TestFailedCount = list.Count(x => x.Status == "测试失败"),

                ExtractSuccessCount = list.Count(x =>
                    x.Status == "解压成功" ||
                    x.Status == "已覆盖"),

                ExtractFailedCount = list.Count(x =>
                    x.Status == "解压失败" ||
                    x.Status == "密码错误" ||
                    x.Status == "文件损坏" ||
                    x.Status == "权限不足" ||
                    x.Status == "输出路径冲突" ||
                    x.Status == "分卷缺失" ||
                    x.Status == "路径过长" ||
                    x.Status == "未知错误" ||
                    x.Status == "7z不存在"),

                PasswordErrorCount = list.Count(x =>
                    x.Status == "密码错误" ||
                    x.PasswordStatus == "密码错误"),

                CorruptedCount = list.Count(x => x.Status == "文件损坏"),

                SkippedCount = list.Count(x =>
                    x.Status == "已跳过" ||
                    x.Operation == "跳过"),

                CancelledCount = list.Count(x => x.Status == "已取消"),

                OtherFailedCount = list.Count(x =>
                    IsFailedStatus(x.Status) &&
                    x.Status != "解压失败" &&
                    x.Status != "密码错误" &&
                    x.Status != "文件损坏" &&
                    x.Status != "权限不足" &&
                    x.Status != "输出路径冲突" &&
                    x.Status != "分卷缺失" &&
                    x.Status != "路径过长" &&
                    x.Status != "7z不存在")
            };
        }

        /// <summary>
        /// 获取失败任务。
        /// </summary>
        public List<ArchiveTask> GetFailedTasks(IEnumerable<ArchiveTask> tasks)
        {
            return tasks?
                .Where(x => x != null && IsFailedOrUnknownTask(x))
                .ToList()
                ?? new List<ArchiveTask>();
        }

        /// <summary>
        /// 构建失败列表文本。
        /// 
        /// 格式：
        /// 333.7z - 密码错误
        /// 444.rar - 文件损坏
        /// 555.jpg - 格式未知
        /// </summary>
        public string BuildFailedListText(IEnumerable<ArchiveTask> tasks)
        {
            List<ArchiveTask> failedTasks = GetFailedTasks(tasks);

            var builder = new StringBuilder();

            foreach (ArchiveTask task in failedTasks)
            {
                string fileName = !string.IsNullOrWhiteSpace(task.FileName)
                    ? task.FileName
                    : Path.GetFileName(task.CurrentPath);

                string reason = GetFailureReason(task);

                builder.Append(fileName);
                builder.Append(" - ");
                builder.Append(reason);
                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// 获取失败原因。
        /// </summary>
        public string GetFailureReason(ArchiveTask task)
        {
            if (task == null)
            {
                return "未知错误";
            }

            if (!string.IsNullOrWhiteSpace(task.ErrorMessage))
            {
                return task.ErrorMessage;
            }

            if (!string.IsNullOrWhiteSpace(task.Status))
            {
                return task.Status;
            }

            if (!string.IsNullOrWhiteSpace(task.ExtensionStatus))
            {
                return task.ExtensionStatus;
            }

            return "未知错误";
        }

        /// <summary>
        /// 判断是否失败状态。
        /// </summary>
        public bool IsFailedStatus(string status)
        {
            return status is
                "格式未知" or
                "改名失败" or
                "测试失败" or
                "解压失败" or
                "密码错误" or
                "文件损坏" or
                "权限不足" or
                "输出路径冲突" or
                "分卷缺失" or
                "路径过长" or
                "7z不存在" or
                "未知错误";
        }

        /// <summary>
        /// 判断是否需要进入失败列表。
        /// </summary>
        public bool IsFailedOrUnknownTask(ArchiveTask task)
        {
            if (task == null)
            {
                return false;
            }

            if (IsFailedStatus(task.Status))
            {
                return true;
            }

            if (task.DetectedFormat == "Unknown" ||
                task.ExtensionStatus == "格式未知")
            {
                return true;
            }

            return false;
        }
    }
}

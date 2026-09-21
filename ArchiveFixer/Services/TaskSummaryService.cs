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
                    x.Status == StatusText.Recognized ||
                    x.ExtensionStatus == StatusText.ExtensionNormal ||
                    x.ExtensionStatus == StatusText.ExtensionMissing ||
                    x.ExtensionStatus == StatusText.ExtensionMismatch ||
                    x.ExtensionStatus == StatusText.ExtensionMultiFake ||
                    x.ExtensionStatus == StatusText.ExtensionVolume),

                UnknownCount = list.Count(x =>
                    x.DetectedFormat == "Unknown" ||
                    x.Status == StatusText.UnknownFormat ||
                    x.ExtensionStatus == StatusText.UnknownFormat),

                RenameSuccessCount = list.Count(x => x.Status == StatusText.RenameSuccess),
                RenameFailedCount = list.Count(x => x.Status == StatusText.RenameFailed),

                TestSuccessCount = list.Count(x => x.Status == StatusText.TestPassed),
                TestFailedCount = list.Count(x => x.Status == StatusText.TestFailed),

                ExtractSuccessCount = list.Count(x =>
                    x.Status == StatusText.ExtractSuccess ||
                    x.Status == StatusText.Overwritten),

                ExtractFailedCount = list.Count(x =>
                    x.Status == StatusText.ExtractFailed ||
                    x.Status == StatusText.WrongPassword ||
                    x.Status == StatusText.Corrupted ||
                    x.Status == StatusText.AccessDenied ||
                    x.Status == StatusText.OutputConflict ||
                    x.Status == StatusText.VolumeMissing ||
                    x.Status == StatusText.PathTooLong ||
                    x.Status == StatusText.UnknownError ||
                    x.Status == StatusText.SevenZipMissing),

                PasswordErrorCount = list.Count(x =>
                    x.Status == StatusText.WrongPassword ||
                    x.PasswordStatus == StatusText.WrongPassword),

                CorruptedCount = list.Count(x => x.Status == StatusText.Corrupted),

                SkippedCount = list.Count(x =>
                    x.Status == StatusText.Skipped ||
                    x.Operation == StatusText.OpSkip),

                CancelledCount = list.Count(x => x.Status == StatusText.Cancelled),

                OtherFailedCount = list.Count(x =>
                    IsFailedStatus(x.Status) &&
                    x.Status != StatusText.ExtractFailed &&
                    x.Status != StatusText.WrongPassword &&
                    x.Status != StatusText.Corrupted &&
                    x.Status != StatusText.AccessDenied &&
                    x.Status != StatusText.OutputConflict &&
                    x.Status != StatusText.VolumeMissing &&
                    x.Status != StatusText.PathTooLong &&
                    x.Status != StatusText.SevenZipMissing)
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
                return StatusText.UnknownError;
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

            return StatusText.UnknownError;
        }

        /// <summary>
        /// 判断是否失败状态。
        /// </summary>
        public bool IsFailedStatus(string status)
        {
            return status is
                StatusText.UnknownFormat or
                StatusText.RenameFailed or
                StatusText.TestFailed or
                StatusText.ExtractFailed or
                StatusText.WrongPassword or
                StatusText.Corrupted or
                StatusText.AccessDenied or
                StatusText.OutputConflict or
                StatusText.VolumeMissing or
                StatusText.PathTooLong or
                StatusText.SevenZipMissing or
                StatusText.UnknownError;
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
                task.ExtensionStatus == StatusText.UnknownFormat)
            {
                return true;
            }

            return false;
        }
    }
}

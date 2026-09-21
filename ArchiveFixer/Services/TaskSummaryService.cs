using ArchiveFixer.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 任务汇总的**结果分桶**：每个任务恰好落进一个桶。
    ///
    /// 为什么要有这个东西：旧的 <see cref="TaskSummaryService.BuildSummary"/> 用一堆互不排斥的
    /// <c>Count(...)</c> 拼出来，同一个任务会同时进"解压失败"和"其他失败"（`UnknownError`）、
    /// 同时进"解压失败"和"密码错误"（`WrongPassword`）—— 用户看到的就是"汇总数字前后对不上：
    /// 成功 + 失败 + 跳过 比总数还大"。分桶是唯一真相来源，界面字段只能从桶里取数，
    /// 不允许再出现第二套判断。
    /// </summary>
    public enum SummaryBucket
    {
        /// <summary>还没有结果：等待扫描 / 已识别 / 进行中。它也是分桶的一部分，只是界面上没有单独一项。</summary>
        Pending = 0,

        RenameSuccess,
        RenameFailed,
        TestPassed,
        TestFailed,
        ExtractSuccess,

        /// <summary>密码错误（<see cref="StatusText.WrongPassword"/>）。**只有它算这一类**。</summary>
        PasswordError,

        Corrupted,

        /// <summary>解压侧其它失败：解压失败 / 权限不足 / 输出路径冲突 / 分卷缺失 / 路径过长 / 7z不存在 / 未知错误 / 达到密码尝试上限。</summary>
        ExtractFailed,

        Skipped,
        Cancelled,

        /// <summary>部分完成、格式未知，以及将来新增但还没归类的失败状态。</summary>
        OtherFailed
    }

    /// <summary>
    /// 各分桶的计数。**硬不变量：<see cref="Total"/> 恒等于任务总数**（互斥且可加），
    /// 所以"各分项之和 == 任务数"是构造出来的性质，不是靠调用方自觉。
    /// </summary>
    public sealed class SummaryBucketCounts
    {
        public int Pending { get; init; }

        public int RenameSuccess { get; init; }

        public int RenameFailed { get; init; }

        public int TestPassed { get; init; }

        public int TestFailed { get; init; }

        public int ExtractSuccess { get; init; }

        public int PasswordError { get; init; }

        public int Corrupted { get; init; }

        public int ExtractFailed { get; init; }

        public int Skipped { get; init; }

        public int Cancelled { get; init; }

        public int OtherFailed { get; init; }

        /// <summary>全部分桶之和 —— 按定义等于任务总数。</summary>
        public int Total =>
            Pending +
            RenameSuccess + RenameFailed +
            TestPassed + TestFailed +
            ExtractSuccess + ExtractFailed +
            PasswordError + Corrupted +
            Skipped + Cancelled + OtherFailed;

        /// <summary>失败侧合计（与界面"失败总数"同口径，不含"待处理"，也不含任何重复计数）。</summary>
        public int FailureTotal =>
            RenameFailed + TestFailed + ExtractFailed + PasswordError + Corrupted + OtherFailed;

        public int Get(SummaryBucket bucket)
        {
            return bucket switch
            {
                SummaryBucket.Pending => Pending,
                SummaryBucket.RenameSuccess => RenameSuccess,
                SummaryBucket.RenameFailed => RenameFailed,
                SummaryBucket.TestPassed => TestPassed,
                SummaryBucket.TestFailed => TestFailed,
                SummaryBucket.ExtractSuccess => ExtractSuccess,
                SummaryBucket.PasswordError => PasswordError,
                SummaryBucket.Corrupted => Corrupted,
                SummaryBucket.ExtractFailed => ExtractFailed,
                SummaryBucket.Skipped => Skipped,
                SummaryBucket.Cancelled => Cancelled,
                SummaryBucket.OtherFailed => OtherFailed,
                _ => Pending
            };
        }
    }

    /// <summary>
    /// 任务汇总服务。
    /// </summary>
    public class TaskSummaryService
    {
        /// <summary>
        /// 把一个任务归到唯一的结果分桶。
        ///
        /// 判定顺序：先看<strong>终态</strong> <see cref="ArchiveTask.Status"/>（它是"这件事最后怎么了"的唯一权威），
        /// 再看 <see cref="ArchiveTask.Operation"/>（只用来兜底"跳过"：有些路径只标了操作没标状态）。
        /// <see cref="ArchiveTask.PasswordStatus"/> **不参与**判定 —— 它会在同一次任务里被反复改
        /// （试了错密码、换了下一个候选、最后解压成功），拿它分桶必然重复计数。
        /// </summary>
        public static SummaryBucket ClassifyOutcome(ArchiveTask? task)
        {
            if (task == null)
            {
                return SummaryBucket.Pending;
            }

            string status = task.Status ?? string.Empty;

            if (status == StatusText.RenameSuccess)
            {
                return SummaryBucket.RenameSuccess;
            }

            if (status == StatusText.RenameFailed)
            {
                return SummaryBucket.RenameFailed;
            }

            if (status == StatusText.TestPassed)
            {
                return SummaryBucket.TestPassed;
            }

            if (status == StatusText.TestFailed)
            {
                return SummaryBucket.TestFailed;
            }

            if (status == StatusText.ExtractSuccess || status == StatusText.Overwritten)
            {
                return SummaryBucket.ExtractSuccess;
            }

            if (status == StatusText.WrongPassword)
            {
                return SummaryBucket.PasswordError;
            }

            if (status == StatusText.Corrupted)
            {
                return SummaryBucket.Corrupted;
            }

            if (IsExtractFailureStatus(status))
            {
                return SummaryBucket.ExtractFailed;
            }

            if (status == StatusText.Skipped)
            {
                return SummaryBucket.Skipped;
            }

            if (status == StatusText.Cancelled)
            {
                return SummaryBucket.Cancelled;
            }

            if (status == StatusText.PartiallyCompleted || status == StatusText.UnknownFormat)
            {
                return SummaryBucket.OtherFailed;
            }

            // 非终态：只有操作被标成"跳过"时才算跳过，其余都是"还没结果"。
            if (task.Operation == StatusText.OpSkip)
            {
                return SummaryBucket.Skipped;
            }

            return SummaryBucket.Pending;
        }

        /// <summary>按分桶统计。这是 <see cref="BuildSummary"/> 唯一的取数来源。</summary>
        public SummaryBucketCounts CountBuckets(IEnumerable<ArchiveTask>? tasks)
        {
            int[] counts = new int[Enum.GetValues<SummaryBucket>().Length];

            foreach (ArchiveTask task in tasks ?? Enumerable.Empty<ArchiveTask>())
            {
                if (task == null)
                {
                    continue;
                }

                counts[(int)ClassifyOutcome(task)]++;
            }

            return new SummaryBucketCounts
            {
                Pending = counts[(int)SummaryBucket.Pending],
                RenameSuccess = counts[(int)SummaryBucket.RenameSuccess],
                RenameFailed = counts[(int)SummaryBucket.RenameFailed],
                TestPassed = counts[(int)SummaryBucket.TestPassed],
                TestFailed = counts[(int)SummaryBucket.TestFailed],
                ExtractSuccess = counts[(int)SummaryBucket.ExtractSuccess],
                PasswordError = counts[(int)SummaryBucket.PasswordError],
                Corrupted = counts[(int)SummaryBucket.Corrupted],
                ExtractFailed = counts[(int)SummaryBucket.ExtractFailed],
                Skipped = counts[(int)SummaryBucket.Skipped],
                Cancelled = counts[(int)SummaryBucket.Cancelled],
                OtherFailed = counts[(int)SummaryBucket.OtherFailed]
            };
        }

        /// <summary>
        /// 构建任务汇总。
        ///
        /// 11 个分项（改名成功/改名失败/测试通过/测试失败/解压成功/解压失败/密码错误/文件损坏/已跳过/已取消/其他失败）
        /// 全部来自互斥分桶，**两两不重叠**，因此：
        /// 分项之和 + 待处理（<see cref="SummaryBucket.Pending"/>，界面上没有单项）= 任务总数。
        ///
        /// 已识别 / 格式未知这两项是"识别阶段"的属性，不是结果分桶，**不参与求和**
        /// （一个包可以既"已识别"又"解压成功"）。
        /// </summary>
        public TaskSummary BuildSummary(IEnumerable<ArchiveTask> tasks)
        {
            var list = tasks?.Where(x => x != null).ToList() ?? new List<ArchiveTask>();
            SummaryBucketCounts buckets = CountBuckets(list);

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
                    x.ExtensionStatus == StatusText.ExtensionVolume ||
                    // 内嵌归档也算"已识别"：它的格式是确定的（ZIP，只是藏在文件尾部），
                    // 归到"未知"会让用户以为识别失败，从而去改一个根本不该改的后缀。
                    x.ExtensionStatus == StatusText.ExtensionEmbedded),

                UnknownCount = list.Count(x =>
                    x.DetectedFormat == "Unknown" ||
                    x.Status == StatusText.UnknownFormat ||
                    x.ExtensionStatus == StatusText.UnknownFormat),

                RenameSuccessCount = buckets.RenameSuccess,
                RenameFailedCount = buckets.RenameFailed,

                TestSuccessCount = buckets.TestPassed,
                TestFailedCount = buckets.TestFailed,

                ExtractSuccessCount = buckets.ExtractSuccess,
                ExtractFailedCount = buckets.ExtractFailed,

                PasswordErrorCount = buckets.PasswordError,
                CorruptedCount = buckets.Corrupted,

                SkippedCount = buckets.Skipped,
                CancelledCount = buckets.Cancelled,

                /*
                 * "部分完成"计入失败侧：它确实没做完，用户还要处理。
                 * 宁可让统计显得悲观，也不能让"部分完成"混进成功数里 —— 那正是设计.md 不变量 9 要防的事。
                 */
                OtherFailedCount = buckets.OtherFailed
            };
        }

        /// <summary>解压侧的失败状态（不含密码错误 / 文件损坏：它们各自单独成项）。</summary>
        private static bool IsExtractFailureStatus(string status)
        {
            return status is
                StatusText.ExtractFailed or
                StatusText.AccessDenied or
                StatusText.OutputConflict or
                StatusText.VolumeMissing or
                StatusText.PathTooLong or
                StatusText.SevenZipMissing or
                StatusText.UnknownError or
                StatusText.PasswordAttemptLimitReached;
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
                StatusText.PasswordAttemptLimitReached or
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

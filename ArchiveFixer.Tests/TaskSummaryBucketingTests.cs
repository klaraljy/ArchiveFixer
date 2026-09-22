using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Converters;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 汇总分桶：**每个任务只落一个桶，分项之和恒等于任务总数**。
    ///
    /// 这一组是用户那句"汇总数字前后对不上"的回归测试：旧实现用一堆互不排斥的
    /// <c>Count(...)</c> 拼分项，同一个任务会同时进两个分项 ——
    /// <c>UnknownError</c> 同时进"解压失败"和"其他失败"，
    /// <c>WrongPassword</c> 同时进"解压失败"和"密码错误"，
    /// <c>RenameFailed</c>/<c>TestFailed</c> 同时进各自分项和"其他失败"，
    /// 于是"成功 + 失败 + 跳过"能比任务总数还大。
    /// </summary>
    public class TaskSummaryBucketingTests
    {
        private static ArchiveTask Make(string status, bool selected = true)
        {
            return new ArchiveTask($@"C:\tmp\{status}.7z")
            {
                Status = status,
                IsSelected = selected,
                IsArchive = true,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal
            };
        }

        [Fact]
        public void 各种状态混合时_分桶之和等于任务总数且各分项互不重叠()
        {
            var tasks = new List<ArchiveTask>
            {
                Make(StatusText.ExtractSuccess),
                Make(StatusText.Overwritten),
                Make(StatusText.UnknownError),                 // 旧实现：同时进"解压失败"+"其他失败"
                Make(StatusText.WrongPassword),                // 旧实现：同时进"解压失败"+"密码错误"
                Make(StatusText.RenameFailed),                 // 旧实现：同时进"改名失败"+"其他失败"
                Make(StatusText.TestFailed),                   // 旧实现：同时进"测试失败"+"其他失败"
                Make(StatusText.Corrupted),
                Make(StatusText.ExtractFailed),
                Make(StatusText.AccessDenied),
                Make(StatusText.OutputConflict),
                Make(StatusText.VolumeMissing),
                Make(StatusText.PathTooLong),
                Make(StatusText.SevenZipMissing),
                Make(StatusText.PasswordAttemptLimitReached),  // 新增状态：算"解压失败"，不算"密码错误"
                Make(StatusText.Cancelled),
                Make(StatusText.PartiallyCompleted),
                Make(StatusText.EncryptedHeaders),            // 新增状态：算"其他失败"，不算"密码错误"、更不算成功
                Make(StatusText.Skipped),
                Make(StatusText.RenameSuccess),
                Make(StatusText.TestPassed),
                Make(StatusText.UnknownFormat),
                Make(StatusText.Recognized),                   // 还没结果
                Make(StatusText.WaitingScan)                    // 还没结果
            };

            var service = new TaskSummaryService();
            TaskSummary summary = service.BuildSummary(tasks);
            SummaryBucketCounts buckets = service.CountBuckets(tasks);

            // 硬不变量①：每个任务恰好落一个桶 —— 分桶之和 == 任务总数。
            Assert.Equal(tasks.Count, buckets.Total);
            Assert.Equal(tasks.Count, summary.TotalCount);

            // 硬不变量②：界面上的每个分项都恰好来自一个桶（没有第二套判断）。
            Assert.Equal(buckets.RenameSuccess, summary.RenameSuccessCount);
            Assert.Equal(buckets.RenameFailed, summary.RenameFailedCount);
            Assert.Equal(buckets.TestPassed, summary.TestSuccessCount);
            Assert.Equal(buckets.TestFailed, summary.TestFailedCount);
            Assert.Equal(buckets.ExtractSuccess, summary.ExtractSuccessCount);
            Assert.Equal(buckets.ExtractFailed, summary.ExtractFailedCount);
            Assert.Equal(buckets.PasswordError, summary.PasswordErrorCount);
            Assert.Equal(buckets.Corrupted, summary.CorruptedCount);
            Assert.Equal(buckets.Skipped, summary.SkippedCount);
            Assert.Equal(buckets.Cancelled, summary.CancelledCount);
            Assert.Equal(buckets.OtherFailed, summary.OtherFailedCount);

            // 硬不变量③：失败侧合计 == 落在失败桶里的任务数（不再是"各分项直接相加"的重复计数）。
            Assert.Equal(buckets.FailureTotal, summary.FailedTotalCount);

            // 具体数字：漏一个状态、重复一个状态都会在这里露出来。
            Assert.Equal(2, summary.ExtractSuccessCount);                  // 解压成功 + 已覆盖
            Assert.Equal(1, summary.RenameSuccessCount);
            Assert.Equal(1, summary.RenameFailedCount);
            Assert.Equal(1, summary.TestSuccessCount);
            Assert.Equal(1, summary.TestFailedCount);
            Assert.Equal(1, summary.PasswordErrorCount);                   // 只有"密码错误"（文件名已加密不算它）
            Assert.Equal(1, summary.CorruptedCount);
            Assert.Equal(8, summary.ExtractFailedCount);                   // 解压失败/未知错误/权限/输出冲突/分卷缺失/路径过长/7z不存在/达到上限
            Assert.Equal(1, summary.CancelledCount);
            Assert.Equal(3, summary.OtherFailedCount);                     // 部分完成 + 格式未知 + 文件名已加密
            Assert.Equal(1, summary.SkippedCount);

            // 待处理（分桶里有、界面上没有单项）也要算进去，否则"分项之和 == 任务数"是假的。
            int displayed = summary.RenameSuccessCount + summary.RenameFailedCount +
                summary.TestSuccessCount + summary.TestFailedCount +
                summary.ExtractSuccessCount + summary.ExtractFailedCount +
                summary.PasswordErrorCount + summary.CorruptedCount +
                summary.SkippedCount + summary.CancelledCount + summary.OtherFailedCount;

            Assert.Equal(tasks.Count, displayed + buckets.Pending);
        }

        [Fact]
        public void 未知错误只算解压失败_不再重复计入其他失败()
        {
            TaskSummary summary = new TaskSummaryService().BuildSummary(new[] { Make(StatusText.UnknownError) });

            Assert.Equal(1, summary.ExtractFailedCount);
            Assert.Equal(0, summary.OtherFailedCount);
            Assert.Equal(1, summary.FailedTotalCount);
        }

        [Fact]
        public void 密码错误只算密码错误_不再重复计入解压失败()
        {
            TaskSummary summary = new TaskSummaryService().BuildSummary(new[] { Make(StatusText.WrongPassword) });

            Assert.Equal(1, summary.PasswordErrorCount);
            Assert.Equal(0, summary.ExtractFailedCount);
            Assert.Equal(1, summary.FailedTotalCount);
        }

        [Fact]
        public void 改名失败与测试失败不再重复计入其他失败()
        {
            var service = new TaskSummaryService();
            TaskSummary summary = service.BuildSummary(new[]
            {
                Make(StatusText.RenameFailed),
                Make(StatusText.TestFailed)
            });

            Assert.Equal(1, summary.RenameFailedCount);
            Assert.Equal(1, summary.TestFailedCount);
            Assert.Equal(0, summary.OtherFailedCount);
            Assert.Equal(2, summary.FailedTotalCount);
        }

        [Fact]
        public void 部分完成与已取消都不得计入成功()
        {
            TaskSummary summary = new TaskSummaryService().BuildSummary(new[]
            {
                Make(StatusText.PartiallyCompleted),
                Make(StatusText.Cancelled)
            });

            Assert.Equal(0, summary.ExtractSuccessCount);
            Assert.Equal(1, summary.OtherFailedCount);   // 部分完成计失败侧
            Assert.Equal(1, summary.CancelledCount);
        }

        [Fact]
        public void 密码状态不再参与分桶_避免同一次任务被数两次()
        {
            /*
             * PasswordStatus 在一次任务里会被反复改写（试了错密码 → 换候选 → 最后解压成功）。
             * 旧实现拿它统计"密码错误"，于是"解压成功"的任务也会出现在"密码错误"里。
             */
            ArchiveTask task = Make(StatusText.ExtractSuccess);
            task.PasswordStatus = StatusText.WrongPassword;

            TaskSummary summary = new TaskSummaryService().BuildSummary(new[] { task });

            Assert.Equal(1, summary.ExtractSuccessCount);
            Assert.Equal(0, summary.PasswordErrorCount);
            Assert.Equal(0, summary.FailedTotalCount);
        }

        [Fact]
        public void 空集合与null不会炸_分项之和为零()
        {
            var service = new TaskSummaryService();

            TaskSummary empty = service.BuildSummary(new List<ArchiveTask>());
            Assert.Equal(0, empty.TotalCount);
            Assert.Equal(0, service.CountBuckets(new List<ArchiveTask>()).Total);

            Assert.NotNull(service.BuildSummary(null!));
        }

        // ---------------------------------------------------------------- 新增状态：三处同改

        /// <summary>
        /// "达到密码尝试上限"是 §9.2 要求的新状态。AGENTS.md §7 要求三处同改：
        /// StatusText（文案）+ StatusToBrushConverter（配色）+ TaskSummaryService（统计与失败清单）。
        /// 这条测试把三处一次性钉住，少改一处就红。
        /// </summary>
        [Fact]
        public void 新增状态_达到密码尝试上限_三处都已接通()
        {
            // ① 文案
            Assert.Equal("达到密码尝试上限", StatusText.PasswordAttemptLimitReached);

            // ② 配色：和"密码错误"同属失败侧，绝不能给成功色
            var converter = new StatusToBrushConverter();

            Assert.Same(
                converter.ErrorBrush,
                converter.Convert(StatusText.PasswordAttemptLimitReached, typeof(object), null!, null!));
            Assert.NotSame(
                converter.SuccessBrush,
                converter.Convert(StatusText.PasswordAttemptLimitReached, typeof(object), null!, null!));

            // ③ 统计：算"解压失败"分项，**不算**"密码错误"；并且要进失败清单
            var service = new TaskSummaryService();
            ArchiveTask task = Make(StatusText.PasswordAttemptLimitReached);

            TaskSummary summary = service.BuildSummary(new[] { task });

            Assert.Equal(1, summary.ExtractFailedCount);
            Assert.Equal(0, summary.PasswordErrorCount);
            Assert.True(service.IsFailedStatus(StatusText.PasswordAttemptLimitReached));
            Assert.True(service.IsFailedOrUnknownTask(task));
            Assert.Contains(StatusText.PasswordAttemptLimitReached, service.BuildFailedListText(new[] { task }));
        }
    }
}

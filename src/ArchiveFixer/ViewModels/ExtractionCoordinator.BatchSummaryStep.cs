using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;


namespace ArchiveFixer.ViewModels
{
    /// <summary>批末一条汇总（任务数/成功/失败/跳过/用时 + 失败逐条；第 8 块，同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        private void AppendBatchSummary(IReadOnlyList<ArchiveTask>? tasks)
        {
            CleanupConfirmedOrderStagingDirectories();

            if (tasks == null || tasks.Count == 0)
            {
                return;
            }

            /*
             * 数法只有一份（`Models/BatchOutcomeTally`）—— 这一行、一键汇总那一行、
             * 以及导出的日志头部读的都是它，⛔ 不许在这里再写一遍 `Count(task => …)`。
             *
             * 现场（用户 2026-10-02 真机日志）：这两行各写了一遍数法，于是同一份日志里
             * ① 同一个"跳过"一处算跟班卷、一处不算（`CountsTowardBatchOutcome == false` 的跟班卷
             *    不是"没做成"）；②「终态说成功、校验却判否」那一帧在一处算成功、另一处算失败。
             *    ⇒ 两个"失败的数"，用户拿哪一行去对数都对不上。
             *
             * 跟班卷单独一句、仍占总数一个名额，所以"未处理"那一档要把它们减掉 ——
             * 恒等式「各分项之和 + 未处理 = 本次任务数」由 tally 内部兜底，两处都不许破。
             */
            BatchOutcomeTally tally = BatchOutcomeTally.Count(tasks);

            DateTime? first = tasks.Where(task => task.StartTime.HasValue).Min(task => task.StartTime);
            DateTime? last = tasks.Where(task => task.EndTime.HasValue).Max(task => task.EndTime);
            string elapsed = first.HasValue && last.HasValue && last.Value > first.Value
                ? (last.Value - first.Value).ToString(@"hh\:mm\:ss")
                : string.Empty;

            var parts = new List<string>(tally.BuildParts());

            if (!string.IsNullOrWhiteSpace(elapsed))
            {
                parts.Add("用时 " + elapsed);
            }

            AppendLog(
                tally.Failed > 0 ? "WARN" : "INFO",
                $"本批汇总：{tally.Total} 个任务 —— {string.Join(" / ", parts)}。"
                + (tally.FollowerSkipped > 0 ? " " + tally.DescribeFollowerNote() : string.Empty));

            /*
             * 用户 2026-10-06 报的那一格：**"`111.z0删除2` 没改名"** —— 那一行是"如设计"，
             * 但列表上看不出来，很容易被读成漏改。⇒ 批末补一句说清这类文件是**分卷的片**：
             * 按设计**不改名**（不变量 1：默认不改源文件），整组随源包处理。
             *
             * ⛔ 只补一句说明，一个判据都不放宽；名字标准 / 没有分卷组的批不写（那是噪声）。
             */
            List<ArchiveTask> volumePieces = tasks
                .Where(task => task != null
                               && !string.IsNullOrWhiteSpace(task.CurrentPath)
                               && FileNameHelper.IsVolumePartFileName(FileNameHelper.GetFileName(task.CurrentPath))
                               && task.VolumePaths.Count > 1)
                .ToList();

            if (volumePieces.Count > 0)
            {
                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.BatchVolumePiecesKeptNameFormat,
                        volumePieces.Count,
                        string.Join("、", volumePieces.Take(3).Select(task => task.FileName))
                        + (volumePieces.Count > 3 ? $"（还有 {volumePieces.Count - 3} 个）" : string.Empty)));
            }

            /*
             * 批末那条**红字**（用户 2026-09-29 要求）：把"可能是没有密码 / 密码不对"的那些单独点出来，
             * 好让他一眼知道"该去补密码"而不是去怀疑文件坏了。
             *
             * ⚠ 只按**机器判定的密码类终态**点名，并且必须写"可能" —— 同一个包也可能是缺卷 / 损坏，
             * 引擎给的结论并不专一（用户原话："可能由于，因为有的时候出错不仅仅是在这里"）。
             */
            List<ArchiveTask> passwordSuspects = tasks
                .Where(task => task.Status is
                    StatusText.WrongPassword or
                    StatusText.PasswordAttemptLimitReached or
                    StatusText.EncryptedHeaders)
                .ToList();

            if (passwordSuspects.Count > 0)
            {
                AppendLog(
                    "ERROR",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.BatchPasswordSuspectsLogFormat,
                        passwordSuspects.Count,
                        string.Join("、", passwordSuspects.Take(MaxBatchSummaryFailures).Select(task => task.FileName))));
            }

            if (tally.Failed == 0)
            {
                return;
            }

            /*
             * 逐条列的判据与上面那个数字**同一个出口**（`BatchOutcomeTally.IsCountedAsFailure`）——
             * 否则"终态说成功、校验却判否"那一帧会被算进失败数、却不在失败清单里，
             * 用户数一遍名字发现少一个。
             */
            foreach (ArchiveTask task in tasks.Where(BatchOutcomeTally.IsCountedAsFailure).Take(MaxBatchSummaryFailures))
            {
                AppendLog("WARN", $"  失败：{task.FileName} —— {task.Status}");
            }

            if (tally.Failed > MaxBatchSummaryFailures)
            {
                AppendLog("WARN", $"  …还有 {tally.Failed - MaxBatchSummaryFailures} 个失败没列出来（完整清单见「导出失败清单」）");
            }
        }

    }
}
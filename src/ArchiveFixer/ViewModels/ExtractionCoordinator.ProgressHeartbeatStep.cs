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
    /// <summary>长任务心跳与引擎长时间无输出提示（第 7 块，从 Volume 大杂烩搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>长任务心跳间隔（第 44 条：瘦身不许把"还在动"一起瘦掉）。</summary>
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

        /// <summary>
        /// 心跳：**绕开"成功就丢"缓冲**直接写出去 —— 它要回答的正是"此刻还在动吗"。
        /// 任务收尾时的摘要仍然照写，两者不冲突。
        /// </summary>
        private void AppendHeartbeat(string message) => _vm.AppendLog("INFO", message);

        /// <summary>
        /// 引擎超过阈值没有任何输出时的处理：**写一条 WARN + 在任务上挂提示，绝不杀进程**。
        ///
        /// <para>
        /// 为什么只提示不处理：用户点「取消当前」才是唯一的中止语义（不变量 9）。
        /// 自动杀进程会把"慢"误判成"死"，而真实场景里几十秒不输出（固实块、慢盘、杀毒软件扫描）
        /// 完全正常 —— 那正是这个功能存在的理由，不能反过来用它去打断正常任务。
        /// </para>
        /// </summary>
        private void OnEngineStalled(ArchiveTask task, ArchiveStallNotice notice)
        {
            if (notice == null)
            {
                return;
            }

            PostToUiThread(() =>
            {
                if (task.EndTime != null)
                {
                    // 任务已经收尾，这条提示已经过时（迟到的看门狗巡查）。
                    return;
                }

                int idleSeconds = (int)Math.Round(notice.Idle.TotalSeconds);
                int thresholdSeconds = (int)Math.Round(notice.Threshold.TotalSeconds);

                task.ResponsivenessHint =
                    $"{StatusText.LongTimeNoResponse}：已 {idleSeconds} 秒没有任何引擎输出，" +
                    "进程仍在运行；需要中止请点「取消当前」。";

                // WARN 只写一次（引擎层对同一段沉默只报一次），所以不会刷屏。
                AppendLog(
                    "WARN",
                    $"{task.LogName}：{StatusText.LongTimeNoResponse} —— 已 {idleSeconds} 秒没有任何引擎输出" +
                    $"（阈值 {thresholdSeconds} 秒），进程仍在运行。程序不会自动结束它；需要中止请点「取消当前」。");
            });
        }


        /// <summary>
        /// 跨 10% 档位的进度日志行数上限（10 个档位 = 最多 10 行）。
        ///
        /// 为什么必须限：引擎的进度是**每 250ms 一条**，一次 10 分钟的解压有 2400 条 ——
        /// 每条都写日志会把日志彻底淹掉，用户反而看不到真正重要的那几行（密码、失败原因）。
        /// </summary>
        private const int ProgressLogDecileStep = 10;

        /// <summary>进度日志里当前条目名最多留几个字符（再长就截断，免得一行日志比屏幕还宽）。</summary>
        private const int ProgressLogEntryMaxLength = 60;

        /// <summary>
        /// 一个任务的进度接收端。
        ///
        /// <para>
        /// 它接的是引擎层**节流之后**的进度（最多 250ms 一次，见 <c>ArchiveProgressReporter</c>），
        /// 所以这里不需要再去重；它只负责把结论落到任务上、按档位写日志。
        /// </para>
        ///
        /// <para>
        /// 线程：<see cref="Report"/> 可能来自读管道的线程，实际落地一律经
        /// <see cref="PostToUiThread"/> 回到 UI 线程（无界面宿主时就地执行）。
        /// </para>
        /// </summary>
        private sealed class TaskProgressSink : IProgress<ArchiveProgress>
        {
            private readonly ExtractionCoordinator _owner;
            private int _lastLoggedDecile = -1;

            /// <summary>上一次"心跳"写出去的时刻（长任务专用，见 <see cref="HeartbeatInterval"/>）。</summary>
            private DateTime _lastHeartbeatUtc = DateTime.UtcNow;

            public TaskProgressSink(ExtractionCoordinator owner, ArchiveTask task)
            {
                _owner = owner;
                Task = task;
            }

            public ArchiveTask Task { get; }

            public void Report(ArchiveProgress? value)
            {
                if (value == null)
                {
                    return;
                }

                PostToUiThread(() => Apply(value));
            }

            private void Apply(ArchiveProgress progress)
            {
                /*
                 * 任务已经收尾时，迟到的进度一律不落到界面上。
                 *
                 * 引擎层已经把"进程退出后不再回调"做到了（reporter.Complete()），这里是第二道：
                 * 批量解压里上一个任务的最后一帧进度完全可能在它标成"解压成功"之后才排到 UI 队列，
                 * 那一帧如果照写，用户就会看到"解压成功 87%"（不变量 6：收尾后不许再显示进度）。
                 */
                if (Task.EndTime != null)
                {
                    return;
                }

                Task.ApplyProgress(progress.Percent, progress.CurrentEntry, progress.Layer);

                // 详情窗口的「耗时」也跟着走：不然任务在跑的时候那一格一直停在 00:00:00。
                Task.UpdateElapsedText();

                if (progress.Percent < 0)
                {
                    // "只知道在动"（扫描阶段）：不写档位日志，免得开头连写好几行没信息量的东西。
                    return;
                }

                int decile = progress.Percent / ProgressLogDecileStep;

                /*
                 * 进度**回退**（或从"未知"回到具体值）＝ 新的一轮：换一个密码候选重试、或者递归解到了下一层。
                 * 档位计数必须跟着重来，否则后面每一轮都因为"decile 没超过上一轮"而一行日志都写不出来
                 * （实测形态：递归第 2 层开始日志里再也没有进度行，看起来像卡住了）。
                 */
                if (decile < _lastLoggedDecile)
                {
                    _lastLoggedDecile = -1;
                }

                if (decile <= _lastLoggedDecile)
                {
                    /*
                     * ── 长任务心跳（第 44 条的补充，用户"如果失败的话你就可以多一点"的另一面）──
                     *
                     * 瘦身之后，一个 40 GB 的包在日志里就是"开始解压"……然后**半小时没动静**，
                     * 直到收尾摘要才再出现一行 —— 用户会以为卡死了（他以前就报过"看着像卡住"）。
                     * 所以：**每 30 秒**至少放一条进度出去（含百分比、当前条目、已用时间），
                     * 明确标成"仍在解压"，它**绕过"成功就丢"的缓冲**（心跳的意义就是"现在还在动"）。
                     */
                    if (DateTime.UtcNow - _lastHeartbeatUtc >= HeartbeatInterval)
                    {
                        _lastHeartbeatUtc = DateTime.UtcNow;

                        string heartbeatEntry = DescribeEntry(progress.CurrentEntry);

                        _owner.AppendHeartbeat(
                            string.IsNullOrWhiteSpace(heartbeatEntry)
                                ? $"{Task.LogName}：仍在解压 {progress.Percent}%"
                                : $"{Task.LogName}：仍在解压 {progress.Percent}%（当前：{heartbeatEntry}）");
                    }

                    return;
                }

                _lastLoggedDecile = decile;
                _lastHeartbeatUtc = DateTime.UtcNow;

                _owner.AppendLog(
                    "INFO",
                    string.IsNullOrWhiteSpace(DescribeEntry(progress.CurrentEntry))
                        ? $"{Task.LogName}：进度 {progress.Percent}%"
                        : $"{Task.LogName}：进度 {progress.Percent}%（当前：{DescribeEntry(progress.CurrentEntry)}）");
            }

            /// <summary>当前条目名（超长截断）—— 进度行与心跳共用同一份口径。</summary>
            private static string DescribeEntry(string? currentEntry)
            {
                string entry = currentEntry ?? string.Empty;

                return entry.Length > ProgressLogEntryMaxLength
                    ? entry[..ProgressLogEntryMaxLength] + "…"
                    : entry;
            }
        }

        /// <summary>
        /// 造一个带进度 / 卡住提示的引擎请求（本次改动的接线点）。
        ///
        /// 阈值用引擎层的默认值（90 秒）：它**不是**设置项 —— 这一条只提示、不阻断，
        /// 不需要用户去调；用户真正要做的动作是「取消当前」，界面上已经有了。
        /// </summary>
        private ArchiveRequest BuildTrackedRequest(
            string archivePath,
            string password,
            string? outputPath,
            TaskProgressSink progressSink)
        {
            return new ArchiveRequest
            {
                ArchivePath = archivePath,
                Password = password,
                OutputPath = outputPath,
                Progress = progressSink,
                Stalled = notice => OnEngineStalled(progressSink.Task, notice),
                StallThreshold = EngineOutputActivityMonitor.DefaultStallThreshold
            };
        }

    }
}
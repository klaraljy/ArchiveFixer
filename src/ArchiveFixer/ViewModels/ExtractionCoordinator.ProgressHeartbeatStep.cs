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

    }
}
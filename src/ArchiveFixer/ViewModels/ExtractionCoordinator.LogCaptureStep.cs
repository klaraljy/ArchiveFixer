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
    /// <summary>任务级日志捕获/摘要行/批清账（第 11 块，从 Password 大杂烩搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// **把①页每一行实际显示的那几格写进日志**（用户 2026-10-07：「你日志里面能否看清楚列表里面
        /// 显示什么内容」）。显示层（列绑定 / 行刷新）本来不进日志，于是"列表显示对不对"只能靠截图；
        /// 这一行把名称 / 大小 / 当前后缀 / 完整路径 / 状态 / 错误信息**按显示口径**原样打出来，
        /// 用户导出的日志就能与界面逐格对齐（⛔ 取的是同一批计算属性，不另算一套）。
        ///
        /// <para>两处调用：批循环收尾那一处、<see cref="FinalizeDeferredVolumeDeficits"/> 那一处
        /// （整批跑完之后的定稿）。⛔ 快照格式只此一个出口。</para>
        ///
        /// <para>⛔ 只在「详细日志（排查用）」打开时才写：默认关（成功任务只留一行是既有红线，
        /// 有守门用例钉着），所以这些行默认不出现。</para>
        /// </summary>
        private void AppendTaskRowSnapshot()
        {
            if (!Settings.VerboseLog)
            {
                return;
            }

            foreach (ArchiveTask row in SnapshotTaskTable(Tasks))
            {
                if (row == null)
                {
                    continue;
                }

                AppendLog(
                    "WARN",
                    $"[行] 名={row.DisplayFileName} ｜ 大小={row.DisplaySizeText} ｜ 后缀={row.DisplayExtension}"
                        + $" ｜ 路径={row.DisplayPath} ｜ 状态={row.StatusDisplayText} ｜ 错误={row.DisplayErrorMessage}");
            }
        }

        /// <summary>
        /// 写一处**判据**（人读一行 + 机器读一条，字段固定；唯一收口 <see cref="MainViewModel.CriterionLog"/>）。
        ///
        /// <para>⚠ 刻意**不走任务级捕获缓冲**（那条缓冲会把成功任务的 INFO 丢掉）：判据是大测试"只看日志排查"的
        /// 唯一线索 ⇒ 它每一处都必须落进日志文件。⛔ 只是记录：判据与行为一个字都不改。</para>
        /// </summary>
        private void CriterionLog(
            string criterion,
            string inputs,
            string verdict,
            string action = "",
            string file = "",
            string identity = "",
            string level = "INFO")
            => _vm.CriterionLog(criterion, inputs, verdict, action, file, identity, level);

        private sealed class TaskLogCapture
        {
            public List<(string Level, string Message)> Buffer { get; } = new();

            public bool DetailsVisible { get; set; }
        }

        private readonly AsyncLocal<TaskLogCapture?> _currentTaskLog = new();

        /// <summary>进度类日志：成功时一律丢（量最大，信息量最低），失败时随细节一起吐。</summary>
        private static bool IsProgressNoise(string message) =>
            message.Contains("：进度 ", StringComparison.Ordinal)
            || message.Contains("：取出内嵌归档 ", StringComparison.Ordinal);

        /// <summary>
        /// **测试用**的显式口子：true = 成功时也把全部细节写进日志
        /// （默认 <c>false</c> = 产品行为：成功的任务在日志里只留一行摘要）。
        ///
        /// <para>为什么留这个口子：一大批既有用例把"日志里那一行"当作**行为证据**（跨盘搬运走了复制、
        /// 工作区被清理、空间依据写了"同卷只算一份"……），而第 44 条的瘦身改的是**默认呈现**、
        /// 不是行为本身。让那些用例显式声明"我这个用例要看细节"，比把它们一条条改成断言摘要更诚实
        /// —— 也让"默认现在是简洁的"这件事只由 <c>LogVolumePolicyTests</c> 一处钉住。</para>
        ///
        /// <para>⚠ 用户侧的入口是 ⑥设置 →「详细日志（排查用）」（<see cref="AppSettings.VerboseLog"/>，
        /// 默认关），二者在 <see cref="VerboseTaskLogEnabled"/> 里合成同一个行为；
        /// ⛔ 这里这个口子只给测试用，界面不绑它。</para>
        /// </summary>
        internal bool KeepTaskDetailInLog { get; set; }

        /// <summary>
        /// **测试缝**：收尾（定稿/归集/搬源）**已经开始、第一道取消闸门之后**回调一次。
        ///
        /// <para>为什么需要它（用户「残余缺口：收尾内部按取消无用例钉住」）：收尾内部有三处
        /// `ThrowIfCancellationRequested()`（本文件 `:2015` / `:2227` / `:2260` 附近），
        /// 而**没有任何用例能看见它们** —— 现有那两个钩子（`Engine.OnList/OnExtract`）都发生在收尾之前，
        /// 更早的闸门就先看见了取消（`ExtractionPipelineFixTests` 那条用例的注释自己写明了这一点）。
        /// ⇒ 只有在这里回调，测试才能把"取消"按在**收尾已经开始之后**。</para>
        ///
        /// <para>⛔ 默认 `null` = 一个字都不做（生产路径行为与改动前逐字相同）。</para>
        /// </summary>
        internal Action? PostProcessStartedForTests { get; set; }

        private void BeginTaskLogCapture(ArchiveTask task)
        {
            // 详细档（⑥设置 →「详细日志（排查用）」，默认关）或测试显式要求 → 不攒，全部原样写出去。
            _currentTaskLog.Value = VerboseTaskLogEnabled ? null : new TaskLogCapture();

            /*
             * ⚠ 这里**不再**单独写一行"开始解压"：它与收尾摘要合成一行（用户 2026-09-25 第 44 条追加：
             * "把开始解压 + 摘要合成一行"）—— 一个任务在日志里就**一行**，136 个任务的批次从 270 行降到 140 行以内。
             */
        }

        /// <summary>详细日志：设置里的开关（默认关）或测试/排查用的显式口子。</summary>
        private bool VerboseTaskLogEnabled => KeepTaskDetailInLog || Settings.VerboseLog;

        private void FlushTaskLogBuffer(TaskLogCapture capture)
        {
            if (capture.Buffer.Count == 0)
            {
                return;
            }

            foreach ((string level, string message) in capture.Buffer)
            {
                _vm.AppendLog(level, message);
            }

            capture.Buffer.Clear();
        }

        /// <summary>
        /// 任务收尾：按**机器终态**决定细节留不留（⛔ 不比对中文文案），再补一行"收尾摘要"。
        /// </summary>
        private void EndTaskLogCapture(ArchiveTask task)
        {
            TaskLogCapture? capture = _currentTaskLog.Value;

            _currentTaskLog.Value = null;

            /*
             * ⚠ 详细档（capture == null）也要写这一行：它是"开始解压 + 摘要合成一行"里的那一行，
             * 详细档只是**额外**把中间过程全写出去（用户 2026-09-25 第 44 条追加），
             * ⛔ 不是"详细档就没有收尾摘要"。
             */
            if (capture != null)
            {
                bool succeeded = task.Outcome == TaskOutcome.Succeeded;

                if (succeeded)
                {
                    capture.Buffer.Clear();   // 成功：样板与进度全丢，只留下面那一行
                }
                else
                {
                    FlushTaskLogBuffer(capture);   // 失败 / 取消 / 部分完成：细节全留
                }
            }

            _vm.AppendLog("INFO", BuildTaskSummaryLine(task));
        }

        /// <summary>一行说清这个任务的结果（成功时的**唯一**一行）。</summary>
        private static string BuildTaskSummaryLine(ArchiveTask task)
        {
            var parts = new List<string>();

            // 成功 = "解压 100%"（用户要的形状："开始解压，A.rar 解压100%，解压成功"合成一行）。
            if (task.Outcome == TaskOutcome.Succeeded)
            {
                parts.Add($"{StatusText.OpExtract} 100%");
            }

            parts.Add(task.Status);

            if (task.OutputVerification == OutputVerificationOutcome.Passed)
            {
                parts.Add("校验通过");
            }
            else if (task.OutputVerification == OutputVerificationOutcome.Failed)
            {
                parts.Add("校验未通过");
            }

            string destination = LogLineText.DescribeShortDestination(task.CollectedPath, task.OutputPath);

            if (!string.IsNullOrWhiteSpace(destination))
            {
                parts.Add("→ " + destination);
            }

            if (task.IsContinuationTask && task.Outcome == TaskOutcome.Succeeded)
            {
                parts.Add("续解");
            }

            /*
             * 第 42 条：这一单在工作区里接过分卷（默认关的那个开关）—— 那是**动过盘**的动作，
             * 而默认档"成功就丢"会把过程细节丢掉，所以必须出现在这一行摘要里。
             */
            if (!string.IsNullOrWhiteSpace(task.SplitVolumeAssemblyNote))
            {
                parts.Add(task.SplitVolumeAssemblyNote);
            }

            if (!string.IsNullOrWhiteSpace(task.ElapsedText)
                && task.ElapsedText != "-"
                && !string.Equals(task.ElapsedText, "00:00:00", StringComparison.Ordinal))
            {
                // 秒级任务不写"用时 00:00:00"（68 个包就是 68 个零，纯噪声）。
                parts.Add("用时 " + task.ElapsedText);
            }

            return $"{task.LogName}：{string.Join(" ｜ ", parts)}";
        }

        /// <summary>收尾摘要里允许的路径长度上限（超过就只留末两段）。</summary>
        private const int MaxSummaryPathLength = 72;

        /// <summary>本批"其余物已处理"的记账（逐任务只写一行，批末一条汇总 —— 第 44 条）。</summary>
        private int _batchPurgedTaskCount;

        private long _batchPurgedBytes;

        /// <summary>批级记账的锁（任务是并发跑的，普通字段会被两个线程同时改）。</summary>
        private readonly object _batchPurgeGate = new();

        private void NoteBatchPurge(long freedBytes, bool permanent)
        {
            lock (_batchPurgeGate)
            {
                _batchPurgedTaskCount++;
                _batchPurgedBytes += freedBytes;

                if (permanent)
                {
                    _batchPurgedPermanently = true;
                }
            }
        }

        private bool _batchPurgedPermanently;

        /// <summary>
        /// 「停止后续」的那条日志**本批只写一次**（2026-09-27 真机：他连点两次按钮，
        /// 日志里"已请求停止后续任务"出现两遍，跑完收尾又是第三句不同措辞）。
        /// 批开始处（<c>_operationCts = new CancellationTokenSource()</c> 那一段）重置。
        /// </summary>
        private bool _stopNoticeLogged;

        /// <summary>见 <see cref="StatusText.StopRequestedNotice"/>：措辞统一、只写一次。</summary>
        private void LogStopRequestOnce()
        {
            if (_stopNoticeLogged)
            {
                return;
            }

            _stopNoticeLogged = true;

            AppendLog("WARN", StatusText.StopRequestedNotice);
        }

        private (int Tasks, long Bytes, bool Permanent) ReadBatchPurge()
        {
            lock (_batchPurgeGate)
            {
                return (_batchPurgedTaskCount, _batchPurgedBytes, _batchPurgedPermanently);
            }
        }

        private void UpdateSummary() => _vm.UpdateSummary();


        private void AppendLog(string message) => _vm.AppendLog(message);

        private void AppendLog(string level, string message)
        {
            TaskLogCapture? capture = _currentTaskLog.Value;
            if (capture != null && !string.IsNullOrEmpty(message))
            {
                bool isInfo = string.Equals(level, "INFO", StringComparison.OrdinalIgnoreCase);

                if (isInfo && IsProgressNoise(message))
                {
                    // 进度：攒着（失败时才有用），成功时随缓冲一起丢。
                    capture.Buffer.Add((level, message));
                    return;
                }

                if (isInfo && !capture.DetailsVisible)
                {
                    capture.Buffer.Add((level, message));
                    return;
                }

                if (!isInfo && !capture.DetailsVisible)
                {
                    // WARN / ERROR：先把"之前发生了什么"吐出来，再把这一行放出去（顺序才对得上）。
                    FlushTaskLogBuffer(capture);
                    capture.DetailsVisible = true;
                }
            }

            _vm.AppendLog(level, message);
        }

    }
}
using ArchiveFixer.BindingModels;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Threading;


namespace ArchiveFixer.ViewModels;

/// <summary>
/// 任务级日志捕获 / 摘要行 / 批清账（第 11 块，原 <c>ExtractionCoordinator.LogCaptureStep.cs</c> 的全部实现）——
/// 按职责整块搬到本协作类。
///
/// <para>M5 类型总行数治理：协调器那一分片已**删除**，不再留同名薄转发。宿主现在只剩
/// ① 唯一实例 <c>ExtractionCoordinator.LogCapture</c>（加锁懒建，见那里的说明）
/// ② 宿主级入口 <c>AppendLog</c>（两个带 caller 的重载）/ <c>AppendLogPair</c> / <c>AppendLogWithCode</c> /
/// <c>CriterionLog</c> / <c>UpdateSummary</c>（全仓 300+ 调用点直接走它们，它们各自转调本类同名成员）
/// ③ 两个测试/别的 Handler 直接读写的状态（<c>KeepTaskDetailInLog</c> / <c>PostProcessStartedForTests</c>）。
/// 批记账的四个字段（<c>_batchPurged*</c> / <c>_stopNoticeLogged</c>）随实现搬进本类，
/// 批首清零仍只有 <c>ExtractionCoordinator.ResetBatchPurgeCounters</c> 一个出口（转调 <see cref="ResetBatchPurgeCounters"/>）。</para>
///
/// <para>只持有宿主 <see cref="ExtractionCoordinator"/>：判据、文案、日志级别与落盘顺序与原来逐字一致。</para>
///
/// <para>本类有**实例状态**（任务级 <c>AsyncLocal</c> 捕获缓冲 + 批记账的锁）：宿主只有一个实例，
/// 懒建的那一个 <see cref="LogCaptureHandler"/> 全类共用，生命周期与原来完全相同。
/// ⛔ 任何调用方都**不得**自己 <c>new LogCaptureHandler(host)</c> —— 那会造出第二个缓冲，
/// 让"开始捕获"与"写日志"落到两个不同实例上（普通档下成功任务的 INFO 细节会漏上屏幕，踩既有红线）。</para>
///
/// <para>⚠ 诊断日志里的"调用点"字段：宿主转发保留了
/// <c>CallerFilePath</c> / <c>CallerLineNumber</c> / <c>CallerMemberName</c>，
/// 所以**本类之外**的调用点（别的 partial / Handler）报进日志的文件与行号与搬家前一个字符都不差；
/// 只有本类内部那几处自调（<c>AppendTaskRowSnapshot</c> / <c>LogStopRequestOnce</c> / <c>AppendTaskLog</c> /
/// <c>AppendLogPair</c> / <c>AppendLogWithCode</c> / 收尾摘要行）的来源文件从
/// <c>…ExtractionCoordinator.LogCaptureStep.cs</c> 变成 <c>…LogCaptureHandler.cs</c>（成员名不变、级别与文案不变）。</para>
/// </summary>
internal sealed class LogCaptureHandler
{
    private readonly ExtractionCoordinator _host;

    internal LogCaptureHandler(ExtractionCoordinator host)
    {
        _host = host;
    }

    /// <summary>
    /// **把①页每一行实际显示的那几格写进日志**（用户 2026-10-07：「你日志里面能否看清楚列表里面
    /// 显示什么内容」）。显示层（列绑定 / 行刷新）本来不进日志，于是"列表显示对不对"只能靠截图；
    /// 这一行把名称 / 大小 / 当前后缀 / 完整路径 / 状态 / 错误信息**按显示口径**原样打出来，
    /// 用户导出的日志就能与界面逐格对齐（⛔ 取的是同一批计算属性，不另算一套）。
    ///
    /// <para>两处调用：批循环收尾那一处、<c>FinalizeDeferredVolumeDeficits</c> 那一处
    /// （整批跑完之后的定稿）。⛔ 快照格式只此一个出口。</para>
    ///
    /// <para>⛔ 只在「详细日志（排查用）」打开时才写：默认关（成功任务只留一行是既有红线，
    /// 有守门用例钉着），所以这些行默认不出现。</para>
    /// </summary>
    internal void AppendTaskRowSnapshot()
    {
        /*
         * 详细档才上屏幕；普通档也要写进内部 `.log`（排查证据），但不污染用户看到的日志。
         * 用户 2026-10-26：内部日志必须永远详细；用户选项只管显示/导出。
         */
        bool screenVisible = _host.Settings.VerboseLog || _host.KeepTaskDetailInLog;

        foreach (ArchiveTask row in VolumeNameNormalizeHandler.SnapshotTaskTable(_host.Tasks))
        {
            if (row == null)
            {
                continue;
            }

            string line = $"[行] 名={row.DisplayFileName} ｜ 大小={row.DisplaySizeText} ｜ 后缀={row.DisplayExtension}"
                + $" ｜ 路径={row.DisplayPath} ｜ 状态={row.StatusDisplayText} ｜ 错误={row.DisplayErrorMessage}";

            /*
             * ⛔ 2026-11-01：成功/跳过/未处理的行不是"错误"，不许再打成 WARN
             * （真机日志里成功任务也写 WARN，用户会以为出问题了）。
             */
            string level = row.Outcome is TaskOutcome.Failed
                    or TaskOutcome.Cancelled
                    or TaskOutcome.PartiallyCompleted
                ? "WARN"
                : "INFO";

            if (screenVisible)
            {
                AppendLog(level, line);
            }
            else
            {
                _host._vm.AppendLogFileOnly(level, line);
            }
        }
    }

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

    internal void BeginTaskLogCapture(ArchiveTask task)
    {
        task?.Diagnostic.Reset();

        // 详细档（⑥设置 →「详细日志（排查用）」，默认关）或测试显式要求 → 不攒，全部原样写出去。
        _currentTaskLog.Value = VerboseTaskLogEnabled ? null : new TaskLogCapture();

        /*
         * ⚠ 这里**不再**单独写一行"开始解压"：它与收尾摘要合成一行（用户 2026-09-25 第 44 条追加：
         * "把开始解压 + 摘要合成一行"）—— 一个任务在日志里就**一行**，136 个任务的批次从 270 行降到 140 行以内。
         */
    }

    /// <summary>详细日志：设置里的开关（默认关）或测试/排查用的显式口子。</summary>
    private bool VerboseTaskLogEnabled => _host.KeepTaskDetailInLog || _host.Settings.VerboseLog;

    private void FlushTaskLogBuffer(TaskLogCapture capture, bool screenVisible = true)
    {
        if (capture.Buffer.Count == 0)
        {
            return;
        }

        foreach ((string level, string message) in capture.Buffer)
        {
            if (screenVisible)
            {
                _host._vm.AppendLog(level, message);
            }
            else
            {
                // 成功任务不把细节推上屏幕，但内部 `.log` / `.jsonl` 必须留全。
                _host._vm.AppendLogFileOnly(level, message);
            }
        }

        capture.Buffer.Clear();
    }

    /// <summary>
    /// 任务收尾：按**机器终态**决定细节留不留（⛔ 不比对中文文案），再补一行"收尾摘要"。
    /// </summary>
    internal void EndTaskLogCapture(ArchiveTask task)
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
                // 成功：屏幕只留摘要；内部详细日志照常落盘（用户 2026-10-26 口径）。
                FlushTaskLogBuffer(capture, screenVisible: false);
            }
            else
            {
                FlushTaskLogBuffer(capture);   // 失败 / 取消 / 部分完成：细节全留（屏幕也留）
            }
        }

        _host._vm.AppendLog("INFO", BuildTaskSummaryLine(task));
        CompleteTaskDiagnostics(task);
    }

    /// <summary>
    /// 任务收尾的唯一诊断落点：把当前 span/最近一条失败事件/终态兜底码收进任务，
    /// 再写一条 task.end。⛔ 只读事实，不改变任务状态。
    /// </summary>
    private void CompleteTaskDiagnostics(ArchiveTask task)
    {
        TaskDiagnosticFacts facts = task.Diagnostic;
        DiagnosticError? last = DiagnosticSpan.CurrentLastDecision ?? DiagnosticSpan.CurrentLastError;

        facts.SpanId = DiagnosticSpan.CurrentSpanId;
        facts.ParentSpanId = DiagnosticSpan.CurrentParentSpanId;
        facts.StartedAt = DiagnosticSpan.CurrentTaskStartedAt;
        facts.EndedAt = DateTime.UtcNow;
        facts.DurationMs = DiagnosticSpan.CurrentTaskElapsedMs;
        facts.Attempts = task.RecursionTriedCandidateCount;

        bool unsuccessful = task.Outcome is TaskOutcome.Failed or TaskOutcome.PartiallyCompleted;

        if (unsuccessful && last != null)
        {
            facts.ErrorCode = last.ErrorCode;
            facts.Component = last.Component;
            facts.SourceFile = last.SourceFile;
            facts.SourceLine = last.SourceLine;
            facts.Caller = last.Caller;
            facts.Stack = last.Stack;
            facts.Engine = last.Engine;
            facts.ExitCode = last.ExitCode;
            facts.LastEventId = last.EventId;
            facts.LastMessage = last.Message;
            facts.CodeSource = string.IsNullOrWhiteSpace(last.CodeSource) ? "event" : last.CodeSource;
        }

        DiagnosticError? engineCall = DiagnosticSpan.CurrentLastEngine;

        if (unsuccessful && engineCall != null)
        {
            if (string.IsNullOrWhiteSpace(facts.Engine))
            {
                facts.Engine = engineCall.Engine;
                facts.ExitCode = engineCall.ExitCode;
            }
        }

        if (unsuccessful && string.IsNullOrWhiteSpace(facts.ErrorCode))
        {
            facts.ErrorCode = DiagnosticCodes.ForTask(task);
            facts.Component = DiagnosticCodes.ComponentFor(facts.ErrorCode);
            facts.CodeSource = string.IsNullOrWhiteSpace(facts.ErrorCode) ? string.Empty : "fallback";
        }

        if (string.IsNullOrWhiteSpace(facts.Stage))
        {
            facts.Stage = _host._vm.Logger.RunContext.Stage;
        }

        DiagnosticLog.WriteTaskEnd(_host._vm.Logger, task);
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

    /// <summary>批级记账的锁（任务是并发跑的，普通字段会被两个线程同时改）。</summary>
    private readonly object _batchPurgeGate = new();

    /// <summary>
    /// 本批"其余物已处理"的记账（逐任务只写一行，批末一条汇总 —— 第 44 条）。
    ///
    /// <para>⚠ M5：计数本体随实现搬进本类（原先留在宿主分片，因为批首清零由宿主直接赋值）；
    /// 批首清零的唯一出口仍是 <c>ExtractionCoordinator.ResetBatchPurgeCounters</c>
    /// （<c>ExtractionCoordinator.ExtractEntryStep.cs</c>），它转调本类的 <see cref="ResetBatchPurgeCounters"/>。</para>
    /// </summary>
    private int _batchPurgedTaskCount;

    private long _batchPurgedBytes;

    private bool _batchPurgedPermanently;

    /// <summary>
    /// 「停止后续」的那条日志**本批只写一次**（2026-09-27 真机：他连点两次按钮，
    /// 日志里"已请求停止后续任务"出现两遍，跑完收尾又是第三句不同措辞）。
    /// 批开始处（<c>_operationCts = new CancellationTokenSource()</c> 那一段）转调
    /// <see cref="ResetBatchPurgeCounters"/> 复位。
    /// </summary>
    private bool _stopNoticeLogged;

    /// <summary>
    /// 批首那四句记账清零（原 <c>StartExtractCoreAsync</c> 开头，顺序与原来一致）：
    /// 其余物记账三项 + 「停止后续只写一次」。
    /// </summary>
    internal void ResetBatchPurgeCounters()
    {
        _batchPurgedTaskCount = 0;
        _batchPurgedBytes = 0;
        _batchPurgedPermanently = false;
        _stopNoticeLogged = false;
    }

    internal void NoteBatchPurge(long freedBytes, bool permanent)
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

    /// <summary>见 <see cref="StatusText.StopRequestedNotice"/>：措辞统一、只写一次。</summary>
    internal void LogStopRequestOnce()
    {
        if (_stopNoticeLogged)
        {
            return;
        }

        _stopNoticeLogged = true;

        AppendLog("WARN", StatusText.StopRequestedNotice);
    }

    internal (int Tasks, long Bytes, bool Permanent) ReadBatchPurge()
    {
        lock (_batchPurgeGate)
        {
            return (_batchPurgedTaskCount, _batchPurgedBytes, _batchPurgedPermanently);
        }
    }

    internal void AppendLog(string message) => _host._vm.AppendLog(message);

    /// <summary>任务级日志：统一带任务名前缀，并走与普通解压相同的成功/失败捕获规则。</summary>
    internal void AppendTaskLog(ArchiveTask task, string message) =>
        AppendTaskLog(task, "INFO", message);

    internal void AppendTaskLog(ArchiveTask task, string level, string message)
    {
        if (task == null || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        AppendLog(level, $"{task.LogName}：{message}");
    }

    /// <summary>
    /// 适配既有 <see cref="Action{T1,T2}"/> 注入点的两参数入口（引擎原话/失败清单写入器）。
    /// 它只是转到带 caller 的唯一入口；⛔ 不另建一套日志逻辑。
    /// </summary>
    internal void AppendLogPair(string level, string message) => AppendLog(level, message);

    /// <summary>适配 <see cref="Action{T1,T2,T3,T4}"/> 注入点（失败清单写入器）。</summary>
    internal void AppendLogWithCode(string level, string message, string errorCode, string component) =>
        AppendLog(level, message, errorCode, component);

    internal void AppendLog(
        string level,
        string message,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLine = 0,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        // 所有 ERROR 至少带兜底码 + 真实调用点；具体分支再给精确码。
        if (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            AppendLog(
                level,
                message,
                DiagnosticCodes.TaskFailed,
                "ExtractionCoordinator",
                sourceFile,
                sourceLine,
                caller);
            return;
        }

        AppendLog(level, message, string.Empty, string.Empty, sourceFile, sourceLine, caller);
    }

    internal void AppendLog(
        string level,
        string message,
        string errorCode,
        string component,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLine = 0,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            _host._vm.AppendDiagnosticLog(
                level,
                message,
                errorCode,
                component,
                sourceFile: sourceFile,
                sourceLine: sourceLine,
                caller: caller);
            return;
        }

        TaskLogCapture? capture = _currentTaskLog.Value;

        /*
         * 用户选普通档时：INFO 细节进缓冲（成功只写内部文件，失败再补上屏幕）；
         * WARN / ERROR 是用户必须当场看见的结论，直接上屏幕，⛔ 不因为细节缓冲被推迟。
         *
         * 详细档 capture == null，所有行照旧直接走屏幕 + 文件。
         */
        bool isInfo = string.Equals(level, "INFO", StringComparison.OrdinalIgnoreCase);

        if (capture != null && isInfo && !string.IsNullOrEmpty(message))
        {
            capture.Buffer.Add((level, message));
            return;
        }

        _host._vm.AppendLog(level, message, sourceFile, sourceLine, caller);
    }
}

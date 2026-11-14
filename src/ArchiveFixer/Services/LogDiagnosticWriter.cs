using ArchiveFixer.Models;
using System;
using System.Text.Json;

namespace ArchiveFixer.Services;

/// <summary>
/// **诊断与写入收口**：错误码/组件/源码位置字段的组装，结构化事件、异常事件、判据四个字段，
/// 以及"屏幕 + 人读文件 + 机器读文件"的唯一收口 <see cref="WriteEntry"/>。
///
/// <para>为什么单独一个类：这些方法的共同点是**它们决定日志记录长什么样**（级别归一、错误码兜底、
/// 判据的四个字段各占一格、异常栈单独进机器读那一份）。判据本身在别的层，这里⛔ 一条都不判、
/// ⛔ 一个字节都不动盘上的东西。拆分前它们与屏幕集合、文件生命周期、导出住在同一个类型上，
/// 于是"给日志加一个字段"要先穿过四件不相干的事。</para>
///
/// <para>⛔ 写入顺序与拆分前逐字一致：先归一级别与脱敏，再补错误码，再登记
/// <see cref="DiagnosticSpan"/>，最后**在一把锁里**加屏幕行 + 写三份文件，出了锁才通知
/// <see cref="Published"/>（对应拆分后的 <see cref="LogService.LogAdded"/>）。</para>
/// </summary>
internal sealed class LogDiagnosticWriter
{
    private readonly LogOperationContext _context;
    private readonly LogScreenBuffer _screen;
    private readonly LogFileWriter _files;

    internal LogDiagnosticWriter(LogOperationContext context, LogScreenBuffer screen, LogFileWriter files)
    {
        _context = context;
        _screen = screen;
        _files = files;
    }

    /// <summary>
    /// 屏幕日志新增后的通知口（接到 <see cref="LogService.LogAdded"/>）。
    /// ⛔ 只在出了锁之后调用 —— 与拆分前 <c>LogAdded?.Invoke</c> 的位置一致。
    /// </summary>
    internal Action<OperationLogItem>? Published { get; set; }

    /// <summary>写入日志（普通级别行；ERROR 会自动补一个兜底错误码）。</summary>
    internal void Write(
        string level,
        string message,
        string taskPath = "",
        string sourceFile = "",
        int sourceLine = 0,
        string caller = "")
    {
        LogEventFields? evidence = null;

        if (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            evidence = new LogEventFields(
                Event: "log.error",
                EventId: DiagnosticCodes.AppUnknown,
                ErrorCode: DiagnosticCodes.AppUnknown,
                Component: DiagnosticCodes.ComponentFor(DiagnosticCodes.AppUnknown),
                SourceFile: DiagnosticSource.NormalizeFile(sourceFile),
                SourceLine: sourceLine,
                Caller: caller,
                CodeSource: "fallback");
        }

        WriteEntry(level, message, taskPath, criterion: null, evidence);
    }

    /// <summary>带稳定错误码/组件的日志行（失败、异常、判据否定必须走这里或 Exception）。</summary>
    internal void WriteWithCode(
        string level,
        string message,
        string taskPath,
        string errorCode,
        string component,
        string eventId,
        string sourceFile,
        int sourceLine,
        string caller)
    {
        string normalizedCode = string.IsNullOrWhiteSpace(errorCode)
            ? DiagnosticCodes.AppUnknown
            : errorCode.Trim();

        WriteEntry(
            level,
            message,
            taskPath,
            criterion: null,
            new LogEventFields(
                Event: "log.event",
                EventId: string.IsNullOrWhiteSpace(eventId) ? normalizedCode : eventId,
                ErrorCode: normalizedCode,
                Component: string.IsNullOrWhiteSpace(component)
                    ? DiagnosticCodes.ComponentFor(normalizedCode)
                    : component,
                TaskId: DiagnosticSpan.CurrentTaskId,
                SourceFile: DiagnosticSource.NormalizeFile(sourceFile),
                SourceLine: sourceLine,
                Caller: caller,
                CodeSource: string.IsNullOrWhiteSpace(errorCode) ? "fallback" : "explicit"));
    }

    /// <summary>
    /// **屏幕 + 人读文件 + 机器读文件的唯一收口**（拆分前 <c>LogService.WriteInternal</c>；
    /// <see cref="Write"/> 与 <see cref="Criterion"/> 都只经过这里）。
    /// </summary>
    internal void WriteEntry(
        string level,
        string message,
        string taskPath,
        CriterionFields? criterion,
        LogEventFields? evidence = null)
    {
        level = string.IsNullOrWhiteSpace(level) ? "INFO" : level.ToUpperInvariant();
        message = LogSanitizer.Sanitize(message);

        if (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase)
            && (evidence == null || string.IsNullOrWhiteSpace(evidence.ErrorCode)))
        {
            evidence = new LogEventFields(
                Event: "log.error",
                EventId: DiagnosticCodes.AppUnknown,
                ErrorCode: DiagnosticCodes.AppUnknown,
                Component: DiagnosticCodes.ComponentFor(DiagnosticCodes.AppUnknown),
                CodeSource: "fallback");
        }

        if (evidence != null && !string.IsNullOrWhiteSpace(evidence.ErrorCode))
        {
            DiagnosticSpan.NoteError(new DiagnosticError(
                evidence.ErrorCode,
                evidence.Component,
                evidence.EventId,
                evidence.SourceFile,
                evidence.SourceLine,
                evidence.Caller,
                evidence.Stack,
                message,
                CodeSource: evidence.CodeSource));

            message = message + "（错误码 " + evidence.ErrorCode + "）";
        }

        var item = new OperationLogItem(level, message, taskPath);

        lock (_context.SyncRoot)
        {
            _screen.Add(item);
            _files.AppendEntry(item, criterion, evidence);
        }

        Published?.Invoke(item);
    }

    /// <summary>
    /// **写一条结构化诊断事件**：人读一行（事件名 + 摘要），机器读那一份带
    /// <c>event/source/target/action/result/detail/stack/engine/exitCode/bytes/durationMs/group/identity</c>。
    /// 这是“只靠日志排查”的入口；⛔ 不改任何业务判据。
    /// </summary>
    internal void Event(
        string eventName,
        string message,
        string detail = "",
        string level = "INFO",
        string file = "",
        string source = "",
        string target = "",
        string action = "",
        string result = "",
        string stack = "",
        string engine = "",
        int? exitCode = null,
        long? bytes = null,
        long? durationMs = null,
        string group = "",
        string identity = "",
        string errorCode = "",
        string component = "",
        string eventId = "",
        string spanId = "",
        string taskId = "",
        string stage = "",
        string sourceFile = "",
        int sourceLine = 0,
        string caller = "")
    {
        string normalizedCode = string.IsNullOrWhiteSpace(errorCode)
            ? (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase) ? DiagnosticCodes.AppUnknown : string.Empty)
            : errorCode.Trim();
        string normalizedComponent = string.IsNullOrWhiteSpace(component)
            ? DiagnosticCodes.ComponentFor(normalizedCode)
            : component;
        string normalizedEventId = string.IsNullOrWhiteSpace(eventId) ? eventName : eventId;
        string normalizedSourceFile = DiagnosticSource.NormalizeFile(sourceFile);
        string codeSource = !string.IsNullOrWhiteSpace(errorCode)
            ? "explicit"
            : (normalizedCode.Length > 0 ? "fallback" : string.Empty);

        if (normalizedCode.Length > 0)
        {
            var diagnosticError = new DiagnosticError(
                normalizedCode,
                normalizedComponent,
                normalizedEventId,
                normalizedSourceFile,
                sourceLine,
                caller,
                stack,
                message,
                engine,
                exitCode,
                codeSource);

            DiagnosticSpan.NoteError(diagnosticError);

            if (string.Equals(eventName, "decision.stop", StringComparison.Ordinal))
            {
                DiagnosticSpan.NoteDecision(diagnosticError);
            }
        }

        string human = $"事件「{eventName}」：{message}"
            + (source.Length > 0 || target.Length > 0
                ? $"（{source} → {target}）"
                : string.Empty)
            + (result.Length > 0 ? $" ⇒ {result}" : string.Empty)
            + (normalizedCode.Length > 0 ? $"（错误码 {normalizedCode}）" : string.Empty);

        level = string.IsNullOrWhiteSpace(level) ? "INFO" : level.ToUpperInvariant();
        var item = new OperationLogItem(level, LogSanitizer.Sanitize(human), file);

        /*
         * 诊断事件只进 `.trace.jsonl`：成功任务的屏幕/人读日志仍保持“一行”纪律，
         * 而详细事实（批次清单/任务卡/文件操作/引擎原话）在独立文件里，行数与屏幕无关。
         */
        lock (_context.SyncRoot)
        {
            if (!_context.EnableFileLog || string.IsNullOrWhiteSpace(_context.TracePath))
            {
                return;
            }

            try
            {
                _files.AppendTrace(
                    item,
                    new LogEventFields(
                        Event: eventName,
                        Source: LogSanitizer.Sanitize(source),
                        Target: LogSanitizer.Sanitize(target),
                        Action: LogSanitizer.Sanitize(action),
                        Result: LogSanitizer.Sanitize(result),
                        Detail: LogSanitizer.Sanitize(detail),
                        Stack: LogSanitizer.Sanitize(stack),
                        Engine: LogSanitizer.Sanitize(engine),
                        ExitCode: exitCode,
                        Bytes: bytes,
                        DurationMs: durationMs,
                        Group: LogSanitizer.Sanitize(group),
                        Identity: LogSanitizer.Sanitize(identity),
                        EventId: normalizedEventId,
                        ErrorCode: normalizedCode,
                        Component: normalizedComponent,
                        SpanId: string.IsNullOrWhiteSpace(spanId) ? DiagnosticSpan.CurrentSpanId : spanId,
                        ParentSpanId: DiagnosticSpan.CurrentParentSpanId,
                        TaskId: string.IsNullOrWhiteSpace(taskId) ? DiagnosticSpan.CurrentTaskId : taskId,
                        Stage: stage,
                        SourceFile: normalizedSourceFile,
                        SourceLine: sourceLine,
                        Caller: caller,
                        CodeSource: codeSource));
            }
            catch
            {
                // 诊断日志写失败不影响主日志与业务。
            }
        }
    }

    /// <summary>写一条异常事件（异常类型/消息进人读行，完整 ToString 进机器读的 stack）。</summary>
    internal void Exception(
        string context,
        Exception exception,
        string taskPath = "",
        string errorCode = DiagnosticCodes.Unhandled,
        string component = "",
        string sourceFile = "",
        int sourceLine = 0,
        string caller = "")
    {
        if (exception == null)
        {
            return;
        }

        string normalizedCode = string.IsNullOrWhiteSpace(errorCode)
            ? DiagnosticCodes.Unhandled
            : errorCode;
        string normalizedComponent = string.IsNullOrWhiteSpace(component)
            ? DiagnosticCodes.ComponentFor(normalizedCode)
            : component;

        string detail = JsonSerializer.Serialize(new
        {
            type = exception.GetType().FullName,
            message = exception.Message,
            inner = exception.InnerException?.GetType().FullName,
            inner_message = exception.InnerException?.Message,
            source = exception.Source,
            hresult = exception.HResult
        });

        Event(
            "exception",
            $"{context}：{exception.GetType().Name}：{exception.Message}",
            detail: detail,
            level: "ERROR",
            file: taskPath,
            stack: exception.ToString(),
            errorCode: normalizedCode,
            component: normalizedComponent,
            eventId: normalizedCode,
            taskId: DiagnosticSpan.CurrentTaskId,
            sourceFile: sourceFile,
            sourceLine: sourceLine,
            caller: caller);
    }

    /// <summary>
    /// **写一处判据**（大测试的"只看日志排查"主要靠它）：人读一行 + 机器读一条（字段固定）。
    ///
    /// <para>人读那一行的形状：<c>判据「&lt;名字&gt;」｜输入：&lt;判据输入&gt; ⇒ 结论：&lt;结论&gt;（动作：&lt;动作&gt;）</c>；
    /// 机器读那条把 <paramref name="criterion"/> / <paramref name="inputs"/> / <paramref name="verdict"/> /
    /// <paramref name="action"/> 各自放进**独立字段**（⛔ 不埋在散文里，否则没法逐条筛）。</para>
    ///
    /// <para>⛔ 只是**记录**：它不改任何判据、不写盘上的东西。</para>
    /// </summary>
    internal void Criterion(
        string criterion,
        string inputs,
        string verdict,
        string action = "",
        string file = "",
        string identity = "",
        string level = "INFO",
        string errorCode = "",
        string component = "",
        string sourceFile = "",
        int sourceLine = 0,
        string caller = "")
    {
        string human = $"判据「{criterion}」｜输入：{inputs} ⇒ 结论：{verdict}"
            + (action.Length > 0 ? $"（动作：{action}）" : string.Empty);
        bool negative = level.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
            || level.Equals("WARN", StringComparison.OrdinalIgnoreCase)
            || verdict.Contains("失败", StringComparison.Ordinal)
            || verdict.Contains("未通过", StringComparison.Ordinal)
            || verdict.Contains("不完整", StringComparison.Ordinal)
            || verdict.Contains('缺')
            || verdict.Contains("判不出", StringComparison.Ordinal);
        string code = string.IsNullOrWhiteSpace(errorCode)
            ? (negative ? DiagnosticCodes.Decision : string.Empty)
            : errorCode;
        string normalizedComponent = string.IsNullOrWhiteSpace(component)
            ? DiagnosticCodes.ComponentFor(code)
            : component;

        WriteEntry(
            level,
            human,
            file,
            new CriterionFields(criterion, inputs, verdict, action, file, identity),
            new LogEventFields(
                Event: "criterion",
                EventId: criterion,
                ErrorCode: code,
                Component: normalizedComponent,
                TaskId: DiagnosticSpan.CurrentTaskId,
                SourceFile: DiagnosticSource.NormalizeFile(sourceFile),
                SourceLine: sourceLine,
                Caller: caller,
                CodeSource: string.IsNullOrWhiteSpace(code)
                    ? string.Empty
                    : string.IsNullOrWhiteSpace(errorCode) ? "fallback" : "explicit"));
    }
}

using System;
using System.Diagnostics;
using System.Threading;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 轻量诊断 span 上下文（不引第三方、不改变业务）。
    /// logs 每行只读当前 span_id/parent/task；阶段本身仍由现有 UiActivity/日志事件记录。
    /// </summary>
    public static class DiagnosticSpan
    {
        private sealed class Frame
        {
            public string SpanId { get; init; } = string.Empty;
            public string ParentSpanId { get; init; } = string.Empty;
            public string TaskId { get; init; } = string.Empty;
            public string OperationName { get; init; } = string.Empty;
            public DateTime StartedAtUtc { get; init; }
            public long StartedTimestamp { get; init; }
            public DiagnosticError? LastError { get; set; }
            public DiagnosticError? LastException { get; set; }
            public DiagnosticError? LastEngine { get; set; }
            public DiagnosticError? LastDecision { get; set; }
        }

        private static readonly AsyncLocal<Frame?> Current = new();
        private static long _sequence;

        public static string CurrentSpanId => Current.Value?.SpanId ?? string.Empty;
        public static string CurrentParentSpanId => Current.Value?.ParentSpanId ?? string.Empty;
        public static string CurrentTaskId => Current.Value?.TaskId ?? string.Empty;
        public static string CurrentOperationName => Current.Value?.OperationName ?? string.Empty;

        /// <summary>当前任务开始时间（UTC）；不在任务里时为空。</summary>
        public static DateTime? CurrentTaskStartedAt =>
            Current.Value is { TaskId.Length: > 0 } frame ? frame.StartedAtUtc : null;

        /// <summary>当前任务已运行毫秒数；不在任务里时为 0。</summary>
        public static long CurrentTaskElapsedMs
        {
            get
            {
                Frame? frame = Current.Value;

                if (frame == null || frame.TaskId.Length == 0 || frame.StartedTimestamp == 0)
                {
                    return 0;
                }

                return (long)Stopwatch.GetElapsedTime(frame.StartedTimestamp).TotalMilliseconds;
            }
        }

        /// <summary>当前任务里最后一条带码的失败/否定事实；任务收尾时由 task.end 带走。</summary>
        public static DiagnosticError? CurrentLastError
        {
            get
            {
                Frame? frame = Current.Value;
                return frame?.LastException ?? frame?.LastError;
            }
        }

        /// <summary>当前任务里最后一次引擎调用事实（引擎/退出码）；没有时为 null。</summary>
        public static DiagnosticError? CurrentLastEngine => Current.Value?.LastEngine;

        /// <summary>当前任务里最后一次“停因判定”事实；task.end 优先用它定位到判定点。</summary>
        public static DiagnosticError? CurrentLastDecision => Current.Value?.LastDecision;

        public static void StartOperation(string operationId, string operationName)
        {
            Current.Value = new Frame
            {
                SpanId = string.IsNullOrWhiteSpace(operationId) ? NewId() : operationId,
                ParentSpanId = string.Empty,
                TaskId = string.Empty,
                OperationName = operationName ?? string.Empty,
                StartedAtUtc = DateTime.UtcNow,
                StartedTimestamp = Stopwatch.GetTimestamp()
            };
        }

        public static IDisposable BeginTask(string taskId, string operationName)
        {
            Frame? parent = Current.Value;
            var frame = new Frame
            {
                SpanId = NewId(),
                ParentSpanId = parent?.SpanId ?? string.Empty,
                TaskId = taskId ?? string.Empty,
                OperationName = operationName ?? parent?.OperationName ?? string.Empty,
                StartedAtUtc = DateTime.UtcNow,
                StartedTimestamp = Stopwatch.GetTimestamp()
            };

            Current.Value = frame;
            return new Scope(parent);
        }

        /// <summary>
        /// 记录当前任务里最后一条带码事件。只应由 <see cref="LogService"/> 的唯一写入出口调用；
        /// ⛔ 不参与任何业务判据，只作为任务收尾时的诊断事实。
        /// </summary>
        public static void NoteError(DiagnosticError error)
        {
            if (error == null || string.IsNullOrWhiteSpace(error.ErrorCode)
                || string.Equals(error.ErrorCode, DiagnosticCodes.UiStall, StringComparison.Ordinal))
            {
                return;
            }

            Frame? frame = Current.Value;

            if (frame == null || frame.TaskId.Length == 0)
            {
                return;
            }

            frame.LastError = error;

            if (!string.IsNullOrWhiteSpace(error.Stack))
            {
                frame.LastException = error;
            }

            if (!string.IsNullOrWhiteSpace(error.Engine))
            {
                frame.LastEngine = error;
            }
        }

        /// <summary>记录停因判定点（只由 decision.stop 事件调用；⛔ 不参与判据）。</summary>
        public static void NoteDecision(DiagnosticError error)
        {
            if (error == null || string.IsNullOrWhiteSpace(error.ErrorCode))
            {
                return;
            }

            Frame? frame = Current.Value;

            if (frame == null || frame.TaskId.Length == 0)
            {
                return;
            }

            frame.LastDecision = error;
        }

        private static string NewId() =>
            Interlocked.Increment(ref _sequence).ToString("x", System.Globalization.CultureInfo.InvariantCulture);

        private sealed class Scope : IDisposable
        {
            private readonly Frame? _parent;
            private bool _disposed;

            public Scope(Frame? parent) => _parent = parent;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Current.Value = _parent;
            }
        }
    }
}

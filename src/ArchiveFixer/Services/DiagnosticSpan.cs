using System;
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
        }

        private static readonly AsyncLocal<Frame?> Current = new();
        private static long _sequence;

        public static string CurrentSpanId => Current.Value?.SpanId ?? string.Empty;
        public static string CurrentParentSpanId => Current.Value?.ParentSpanId ?? string.Empty;
        public static string CurrentTaskId => Current.Value?.TaskId ?? string.Empty;
        public static string CurrentOperationName => Current.Value?.OperationName ?? string.Empty;

        public static void StartOperation(string operationId, string operationName)
        {
            Current.Value = new Frame
            {
                SpanId = string.IsNullOrWhiteSpace(operationId) ? NewId() : operationId,
                ParentSpanId = string.Empty,
                TaskId = string.Empty,
                OperationName = operationName ?? string.Empty
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
                OperationName = operationName ?? parent?.OperationName ?? string.Empty
            };

            Current.Value = frame;
            return new Scope(parent);
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

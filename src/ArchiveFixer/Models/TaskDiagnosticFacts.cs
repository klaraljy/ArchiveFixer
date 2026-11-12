using System;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// 任务级诊断事实：把“失败发生在哪、为什么、代码在哪”从日志上下文带到任务对象上。
    ///
    /// <para>它只由日志收尾和诊断记录写入；⛔ 不参与解压、删除、落点等任何业务判据，
    /// 也不改变任务状态。诊断包、failed-tasks.json、summary 都读同一份事实。</para>
    /// </summary>
    public sealed class TaskDiagnosticFacts
    {
        public string ErrorCode { get; set; } = string.Empty;

        public string Component { get; set; } = string.Empty;

        public string Stage { get; set; } = string.Empty;

        public string SourceFile { get; set; } = string.Empty;

        public int SourceLine { get; set; }

        public string Caller { get; set; } = string.Empty;

        /// <summary>actual = 失败行真实产生的码；fallback = 只按终态推出来的兜底码。</summary>
        public string CodeSource { get; set; } = string.Empty;

        public string Stack { get; set; } = string.Empty;

        public string Engine { get; set; } = string.Empty;

        public int? ExitCode { get; set; }

        public int Attempts { get; set; }

        public string SpanId { get; set; } = string.Empty;

        public string ParentSpanId { get; set; } = string.Empty;

        public DateTime? StartedAt { get; set; }

        public DateTime? EndedAt { get; set; }

        public long DurationMs { get; set; }

        public string LastEventId { get; set; } = string.Empty;

        public string LastMessage { get; set; } = string.Empty;

        public bool HasErrorCode => !string.IsNullOrWhiteSpace(ErrorCode);

        /// <summary>任务对象会被复用：新一趟开工前清空上一趟的诊断事实。</summary>
        public void Reset()
        {
            ErrorCode = string.Empty;
            Component = string.Empty;
            Stage = string.Empty;
            SourceFile = string.Empty;
            SourceLine = 0;
            Caller = string.Empty;
            CodeSource = string.Empty;
            Stack = string.Empty;
            Engine = string.Empty;
            ExitCode = null;
            Attempts = 0;
            SpanId = string.Empty;
            ParentSpanId = string.Empty;
            StartedAt = null;
            EndedAt = null;
            DurationMs = 0;
            LastEventId = string.Empty;
            LastMessage = string.Empty;
        }
    }
}

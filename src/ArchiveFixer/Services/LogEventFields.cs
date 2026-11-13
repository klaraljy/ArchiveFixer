namespace ArchiveFixer.Services
{
    /// <summary>
    /// 一条结构化诊断事件的字段（唯一写入口 <see cref="Event"/>）；⛔ 不参与任何业务判据。
    /// </summary>
    public sealed record LogEventFields(
        string Event = "",
        string Source = "",
        string Target = "",
        string Action = "",
        string Result = "",
        string Detail = "",
        string Stack = "",
        string Engine = "",
        int? ExitCode = null,
        long? Bytes = null,
        long? DurationMs = null,
        string Group = "",
        string Identity = "",
        string EventId = "",
        string ErrorCode = "",
        string Component = "",
        string SpanId = "",
        string ParentSpanId = "",
        string TaskId = "",
        string OperationName = "",
        string Stage = "",
        string SourceFile = "",
        int SourceLine = 0,
        string Caller = "",
        string CodeSource = "");
}


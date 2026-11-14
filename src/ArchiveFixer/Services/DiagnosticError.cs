namespace ArchiveFixer.Services
{
    /// <summary>
    /// 一条“失败/否定”诊断事实：错误码、组件、事件、源码位置、栈。
    /// 它是 <see cref="LogService"/> 与任务收尾之间的桥：失败行写到哪里，任务收尾就能把同一份事实带走。
    /// </summary>
    public sealed record DiagnosticError(
        string ErrorCode,
        string Component,
        string EventId,
        string SourceFile,
        int SourceLine,
        string Caller,
        string Stack,
        string Message,
        string Engine = "",
        int? ExitCode = null,
        string CodeSource = "");
}

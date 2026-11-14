namespace ArchiveFixer.Services;

/// <summary>进度/心跳这一步要问宿主的事（只有两件事：写普通日志、写"绕开成功就丢缓冲"的心跳）。</summary>
internal interface IProgressHeartbeatHost
{
    void AppendLog(string level, string message);

    void AppendHeartbeat(string message);
}

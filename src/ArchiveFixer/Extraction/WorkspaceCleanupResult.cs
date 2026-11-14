namespace ArchiveFixer.Extraction;

/// <summary>
/// 清理工作区（<see cref="ExtractionWorkspace.Cleanup"/>）的结果。
///
/// 为什么不返回 <c>void</c>：调用方必须能在日志里区分"真的删掉了"和"因为安全校验 / 占用而没删"。
/// 后者用户需要知道 —— 数据还在盘上、为什么还在（AGENTS.md §9.5 对不可逆操作留证据的同一要求）。
/// 把结论丢掉等于让日志只剩一半信息，而删目录是不可逆的。
/// </summary>
public sealed class WorkspaceCleanupResult
{
    /// <summary>被判定为"本任务工作区"的那个目录（即使没删也带回来，方便日志与用户指认）。</summary>
    public string TaskDirectory { get; init; } = string.Empty;

    /// <summary>
    /// 动完之后工作区是不是**真的不在了**：本次删掉、或本来就已经不存在都算 true。
    /// false = 还留在盘上（路径越界没动手 / 删除失败），调用方应当按 WARN 留证据。
    /// </summary>
    public bool Cleaned { get; init; }

    /// <summary>可以直接写进日志的一句话（含路径与原因，不含任务名）。</summary>
    public string Message { get; init; } = string.Empty;
}

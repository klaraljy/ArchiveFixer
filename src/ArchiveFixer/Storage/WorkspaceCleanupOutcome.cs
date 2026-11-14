namespace ArchiveFixer.Storage;

/// <summary>一个工作区目录的清理结果。</summary>
public sealed class WorkspaceCleanupOutcome
{
    public string DirectoryPath { get; init; } = string.Empty;

    /// <summary>真的删掉了（或本来就不在了）。</summary>
    public bool Deleted { get; init; }

    /// <summary>删不掉的原因（删掉了就是空串）。</summary>
    public string Message { get; init; } = string.Empty;
}

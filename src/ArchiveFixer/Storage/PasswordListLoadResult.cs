namespace ArchiveFixer.Storage;

/// <summary>读记忆的结果。<see cref="Snapshot"/> 在失败时是空的一份，不是 null（调用方少写分支）。</summary>
public sealed class PasswordListLoadResult
{
    public PasswordListLoadResult(PasswordListLoadStatus status, PasswordListSnapshot? snapshot, string failureReason)
    {
        Status = status;
        Snapshot = snapshot ?? new PasswordListSnapshot();
        FailureReason = failureReason ?? string.Empty;
    }

    public PasswordListLoadStatus Status { get; }

    public PasswordListSnapshot Snapshot { get; }

    /// <summary>失败原因（**已经脱敏**：只说文件名与类别，不带完整路径、不带任何密码）。</summary>
    public string FailureReason { get; }

    /// <summary>真的读到了一份可用记忆（<see cref="PasswordListLoadStatus.Loaded"/>）。</summary>
    public bool HasMemory => Status == PasswordListLoadStatus.Loaded;

    /// <summary>是不是一次"值得告诉用户"的失败（没有文件 / 用户关掉了开关都不算）。</summary>
    public bool IsFailure =>
        Status is PasswordListLoadStatus.Undecryptable
            or PasswordListLoadStatus.NotOurFormat
            or PasswordListLoadStatus.Unreadable;
}


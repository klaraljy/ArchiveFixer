namespace ArchiveFixer.Storage;

/// <summary>写记忆的结论。<see cref="FailureReason"/> 同样**不含路径与明文**。</summary>
public sealed class PasswordListSaveResult
{
    public PasswordListSaveResult(bool success, string failureReason, string filePath)
    {
        Success = success;
        FailureReason = failureReason ?? string.Empty;
        FilePath = filePath ?? string.Empty;
    }

    public bool Success { get; }

    public string FailureReason { get; }

    public string FilePath { get; }

    /// <summary>写失败时的日志文案：**只有文件名 + 类别**（§8：个人路径不入日志）。</summary>
    public string DescribeForLog() =>
        Success
            ? $"密码列表记忆已更新（{PasswordListStore.FileName}）。"
            : $"密码列表记忆写入失败（{PasswordListStore.FileName}）：{FailureReason}";
}


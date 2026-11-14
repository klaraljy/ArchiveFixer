namespace ArchiveFixer.Extraction;

/// <summary>一个被跳过的第一层子目录：路径 + 原因 + 说明。</summary>
public sealed class EmptyFolderSkip
{
    public string Path { get; init; } = string.Empty;

    public EmptyFolderSkipReason Reason { get; init; }

    /// <summary>中文说明，可直接给用户看。</summary>
    public string Message { get; init; } = string.Empty;
}

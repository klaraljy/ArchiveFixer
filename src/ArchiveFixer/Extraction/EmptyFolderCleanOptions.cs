using ArchiveFixer.Storage;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 空文件夹清理的选项。
///
/// 默认值按 docs/输出与整理模型.md §5：**默认关**（调用方决定要不要执行）、
/// **必须二次确认**（<see cref="UserConfirmed"/> 默认 false）、**默认移入回收站**。
/// </summary>
public sealed class EmptyFolderCleanOptions
{
    /// <summary>作用范围：这个根目录的**直接子目录**（递归判断内部有没有文件），不得越出。</summary>
    public string RootDirectory { get; init; } = string.Empty;

    /// <summary>用户是否已确认。<b>默认 false = 一个都不删</b>，只返回"本来会删哪些"。</summary>
    public bool UserConfirmed { get; init; }

    /// <summary>删除档位，默认回收站。</summary>
    public DeleteMode Mode { get; init; } = DeleteMode.RecycleBin;

    /// <summary>写进删除日志的理由。</summary>
    public string Reason { get; init; } = "空文件夹清理";
}

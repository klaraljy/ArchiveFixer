using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>空文件夹清理的结果。</summary>
public sealed class EmptyFolderCleanResult
{
    public string RootDirectory { get; init; } = string.Empty;

    /// <summary>是否成功读到了根目录并完成了判定（根目录不存在 / 读不到时为 false）。</summary>
    public bool Scanned { get; init; }

    /// <summary>是否真的执行过删除（未确认 / 没有候选时为 false，此时一个字节都没动）。</summary>
    public bool Attempted { get; init; }

    /// <summary>判定为"任意层级都没有文件"的第一层子目录（不管最后删没删，都在这里）。</summary>
    public IReadOnlyList<string> EmptyTrees { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DeletedDirectories { get; init; } = Array.Empty<string>();

    public IReadOnlyList<EmptyFolderSkip> Skipped { get; init; } = Array.Empty<EmptyFolderSkip>();

    /// <summary>彻底删除真正释放的字节数。</summary>
    public long FreedBytes { get; init; }

    /// <summary>移入回收站的字节数（未真正释放空间）。</summary>
    public long RecycledBytes { get; init; }

    /// <summary>本次删除动作产生的日志（由 <see cref="RecycleBinService"/> 统一产出）。</summary>
    public DeleteResult? DeleteResult { get; init; }

    /// <summary>一句话结论，可直接进日志 / 提示框。</summary>
    public string Message { get; init; } = string.Empty;
}

namespace ArchiveFixer.Services;

/// <summary>清理的作用域（两档互相独立，绝不合并成一个开关）。</summary>
public enum CleanupScope
{
    /// <summary>其余物（旧的 <c>过程物</c> 目录，规格 §3.2）。</summary>
    Artifacts = 0,

    /// <summary>当前任务输出根下"任意层级都没有文件"的第一层子目录（规格 §5）。</summary>
    EmptyFolders = 1
}

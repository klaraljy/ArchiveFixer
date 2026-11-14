namespace ArchiveFixer.Models;

/// <summary>差异类型。顺序即文案里的列举顺序（大小 → 修改时间 → 不见）。</summary>
public enum SourceChangeKind
{
    Size = 0,
    LastWriteTime = 1,
    Missing = 2
}

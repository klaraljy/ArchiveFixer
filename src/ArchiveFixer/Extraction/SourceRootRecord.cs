using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 一条用户导入的源根。身份用**导入那一刻的规范化原始路径**，不随改名/搬移改变。
/// </summary>
public sealed class SourceRootRecord
{
    public string RootId { get; init; } = string.Empty;

    public string TaskId { get; init; } = string.Empty;

    public string OriginalPath { get; init; } = string.Empty;

    public string CurrentPath { get; init; } = string.Empty;

    public string PlacementKey { get; init; } = string.Empty;

    public bool IsFolderSelection { get; init; }

    public string SelectionRoot { get; init; } = string.Empty;

    public SourceHandlingMode SourceHandling { get; init; } = SourceHandlingMode.KeepInPlace;
}

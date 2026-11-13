
namespace ArchiveFixer.Extraction
{
    /// <summary>一条清理对象事实。</summary>
    public sealed class CleanupItem
    {
        public string Path { get; init; } = string.Empty;
        public CleanupItemKind Kind { get; init; }
        public string ChainId { get; init; } = string.Empty;
        public string OwnerTaskId { get; init; } = string.Empty;
        public int LayerIndex { get; init; } = -1;
        public long Size { get; init; }
    }
}


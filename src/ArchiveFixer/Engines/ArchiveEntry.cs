namespace ArchiveFixer.Engines
{
    /// <summary>归档里的一个条目。</summary>
    public sealed class ArchiveEntry
    {
        public string Path { get; init; } = string.Empty;

        /// <summary>解压后大小；目录为 0。</summary>
        public long Size { get; init; }

        public bool IsDirectory { get; init; }

        public bool IsEncrypted { get; init; }
    }
}

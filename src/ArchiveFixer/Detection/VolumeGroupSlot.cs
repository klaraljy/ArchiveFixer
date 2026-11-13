namespace ArchiveFixer.Detection
{
    /// <summary>定序结论里的一卷：路径 + 卷号（1 起）。</summary>
    public sealed class VolumeGroupSlot
    {
        public string Path { get; init; } = string.Empty;

        public int Number { get; init; }
    }
}


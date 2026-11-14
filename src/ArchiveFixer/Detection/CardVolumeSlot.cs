namespace ArchiveFixer.Detection
{
    /// <summary>一组里的一片（槽位只按"能确定的那几件事"编号，⛔ 不是卷号）。</summary>
    public readonly record struct CardVolumeSlot(int Index, string Path, long Size, bool IsLast);
}

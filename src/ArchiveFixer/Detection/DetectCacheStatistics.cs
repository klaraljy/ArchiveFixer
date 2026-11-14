namespace ArchiveFixer.Detection;

/// <summary>缓存计数（用例靠它证明"相同文件真的走了缓存"，也供排障）。</summary>
public sealed class DetectCacheStatistics
{
    public long Hits { get; internal set; }

    public long Misses { get; internal set; }

    public long Stores { get; internal set; }

    public int Entries { get; internal set; }

    public override string ToString() =>
        $"命中 {Hits} / 未命中 {Misses} / 存入 {Stores} / 当前 {Entries} 条";
}

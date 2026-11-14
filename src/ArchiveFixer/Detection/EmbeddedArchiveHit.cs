namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 一次"媒体前缀 + 尾部归档"的定位结果（**只描述事实**，不含任何动作）。
    /// </summary>
    /// <param name="Offset">归档在文件里的起点（&gt; 0 才叫"前面垫了东西"）。</param>
    /// <param name="Length">归档区间字节数（判不出时为 0，⛔ 不编）。</param>
    /// <param name="Format">归档格式。</param>
    /// <param name="Evidence">判据输入的一句话（给人看，⛔ 不许拿来当判据）。</param>
    public readonly record struct EmbeddedArchiveHit(
        long Offset,
        long Length,
        VolumeContentFormat Format,
        string Evidence);
}

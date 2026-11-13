namespace ArchiveFixer.Detection
{
    /// <summary>这一条缓存是拿哪些字节算出来的（见 <see cref="DetectCacheKey"/>）。</summary>
    internal enum DetectCacheEvidence
    {
        /// <summary>整个文件都进了指纹（文件比读入上限还短）—— 结论**逐字节可复现**。</summary>
        WholeFile = 0,

        /// <summary>格式在文件头里就定了（头以后的字节与结论无关）。</summary>
        Head = 1,

        /// <summary>头认不出来：还拿了文件尾 256 KiB 的指纹（结论多半出自那一遍顺序扫）。</summary>
        HeadAndTail = 2
    }
}

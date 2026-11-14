namespace ArchiveFixer.Extraction
{
    /// <summary>一次「部分完成发布」的完整结论：判据 → 二次空间体检 → 真搬。</summary>
    public sealed class PartialPublishOutcome
    {
        /// <summary>真的发布了（至少一个文件搬进了 <c>部分完成\</c>）。</summary>
        public bool Published { get; init; }

        /// <summary>逐条对账的结论（没发布时也带着"为什么"）。</summary>
        public PartialPublishVerdict Verdict { get; init; } = new();

        /// <summary>发布的执行结论（没走到那一步时是"一个字节都没动"）。</summary>
        public PartialPublisher.Result Publish { get; init; } = new();

        /// <summary>发布前的二次空间体检没过（不是"判不出该不该发"，而是"现在不该发"）。</summary>
        public bool BlockedBySpace { get; init; }

        /// <summary>给人看的一句话（走 <c>StatusText</c> 常量，调用方原样写日志）。</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>发布落点（<c>&lt;包名&gt;\部分完成</c>）；没发布时为空串。</summary>
        public string DestinationDirectory => Publish.DestinationDirectory;

        public int PublishedCount => Publish.MovedCount;

        public long PublishedBytes => Publish.MovedBytes;
    }
}

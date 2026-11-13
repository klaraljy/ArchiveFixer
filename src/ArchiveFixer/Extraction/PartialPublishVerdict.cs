using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction
{
    /// <summary>要不要按「部分完成」发布、发哪些、为什么 —— **机器可判的唯一结论**。</summary>
    public sealed class PartialPublishVerdict
    {
        /// <summary>能不能发布。</summary>
        public bool CanPublish { get; init; }

        /// <summary>逐条结论（顺序 = 清单顺序）。</summary>
        public IReadOnlyList<PartialEntryVerdict> Entries { get; init; } = Array.Empty<PartialEntryVerdict>();

        /// <summary>可发布的文件（相对路径 + 清单上的字节数）。</summary>
        public IReadOnlyList<(string Path, long Size)> Publishable { get; init; } = Array.Empty<(string, long)>();

        /// <summary>不发布的条目（相对路径 + 为什么）—— 报告里要点名的那一份。</summary>
        public IReadOnlyList<(string Path, PartialEntryState State)> Rejected { get; init; } =
            Array.Empty<(string, PartialEntryState)>();

        /// <summary>机器可判的原因码（日志与文案都读它，⛔ 不比中文）。</summary>
        public string ReasonCode { get; init; } = string.Empty;

        /// <summary>给人看的一句话（点名原因；文案常量在 <c>StatusText</c>）。</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>可发布的字节数 / 清单总字节数（报告里用）。</summary>
        public long PublishableBytes { get; init; }

        public long ExpectedBytes { get; init; }
    }
}

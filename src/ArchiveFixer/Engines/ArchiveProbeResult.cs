namespace ArchiveFixer.Engines
{
    /// <summary>识别结果。置信度用字符串而不是枚举，是为了和既有 <c>DetectResult</c> 的界面口径保持一致。</summary>
    public sealed class ArchiveProbeResult
    {
        public bool IsArchive { get; init; }

        public string Format { get; init; } = "Unknown";

        public string SuggestedExtension { get; init; } = string.Empty;

        /// <summary>Certain / High / Medium / Low / Unknown（设计.md §六 的五档）。</summary>
        public string Confidence { get; init; } = "Unknown";

        public bool IsEncrypted { get; init; }

        public bool IsMultiVolume { get; init; }

        public string Message { get; init; } = string.Empty;
    }
}

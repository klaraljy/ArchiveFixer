namespace ArchiveFixer.Detection
{
    /// <summary>一次加密判读的结论（含依据与"这次一共读了多少字节"）。</summary>
    public sealed class ArchiveEncryptionReading
    {
        /// <summary>结论（三态 + 数据/头加密之分）。</summary>
        public ArchiveEncryptionState State { get; init; } = ArchiveEncryptionState.Unknown;

        /// <summary>
        /// 依据的一句话（**只写结构化事实**：命中哪个结构的哪一位、或哪一步读不下去）。
        /// 它进日志 / 排障，⛔ 不含任何中文判断（AGENTS.md §7：判据不许看文案）。
        /// </summary>
        public string Basis { get; init; } = string.Empty;

        /// <summary>这一次从文件里**读进内存**多少字节（⛔ 上限见各判据器的 <c>MaxHeaderBytes</c>）。</summary>
        public int BytesRead { get; init; }

        /// <summary>要不要报"这是加密包"（只有两条**有证据**的档为真）。</summary>
        public bool IsEncrypted =>
            State == ArchiveEncryptionState.DataEncrypted || State == ArchiveEncryptionState.HeadersEncrypted;
    }
}

using System;

namespace ArchiveFixer.Storage
{
    /// <summary>一针空间采样：什么时候、那块盘还剩多少、当时在干什么。</summary>
    public sealed class SpaceTrendSample
    {
        public DateTime At { get; init; }

        /// <summary>可用字节数（&lt; 0 = 取不到）。</summary>
        public long FreeBytes { get; init; }

        /// <summary>为什么采这一针（"批首" / "每 5 秒" / "「x.7z」开工"…）——进日志用。</summary>
        public string Note { get; init; } = string.Empty;
    }
}

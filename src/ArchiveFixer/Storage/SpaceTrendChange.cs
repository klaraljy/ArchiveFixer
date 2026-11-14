using System;

namespace ArchiveFixer.Storage
{
    /// <summary>一次**有意义的变化**（够大、够久才算，免得刷屏）。</summary>
    public sealed class SpaceTrendChange
    {
        public SpaceTrendSample Previous { get; init; } = new();

        public SpaceTrendSample Current { get; init; } = new();

        /// <summary>当前 − 上一针（负数 = 空间在变少）。</summary>
        public long DeltaBytes => Current.FreeBytes - Previous.FreeBytes;

        public TimeSpan Since => Current.At - Previous.At;
    }
}

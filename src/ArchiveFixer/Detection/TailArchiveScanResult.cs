using System;
using System.Collections.Generic;

namespace ArchiveFixer.Detection
{
    /// <summary>一遍扫描的全部收获（用户 2026-09-25 第 40 条）。</summary>
    public sealed class TailArchiveScanResult
    {
        /// <summary>第一个 RAR / 7z 魔数命中（没有就是 null）。</summary>
        public TailArchiveSignature? Archive { get; init; }

        /// <summary>
        /// 扫到的 <c>PK\x05\x06</c>（EOCD）候选偏移，**从后往前**逐个校验用的（最新的排在最后）。
        /// 命中列表不等于结论 —— 每个候选都要过 <c>EmbeddedArchiveDetector</c> 的自洽校验。
        /// </summary>
        public IReadOnlyList<long> ZipEocdOffsets { get; init; } = Array.Empty<long>();

        /// <summary>
        /// 魔数命中之前有没有见过 <c>PK\x03\x04</c>（ZIP 局部文件头）。
        /// 只用来决定"要不要为了 ZIP 再多读一段" —— 见 <see cref="TailArchiveScanner.Scan"/> 里的取舍说明。
        /// </summary>
        public bool SawZipLocalHeader { get; init; }
    }
}

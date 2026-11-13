using ArchiveFixer.Engines;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 一个候选解压失败之后的处置结论。
    ///
    /// <para><see cref="Outcome"/> 为 null = **接着试下一个候选**（错密码 / 两义两档）；
    /// 非 null = 这一层就停在这里（缺卷 / 损坏 / 双面文件重解 / 引擎错误）。
    /// 另外两笔账回写给候选循环，收尾（<see cref="RecursiveResultBuilder"/>）要用。</para>
    /// </summary>
    internal sealed class CandidateFailureResult
    {
        internal LayerOutcome? Outcome { get; set; }

        internal ArchiveOperationResult? InformativeFailure { get; set; }

        internal ArchiveOperationResult? AmbiguousPasswordFailure { get; set; }
    }
}

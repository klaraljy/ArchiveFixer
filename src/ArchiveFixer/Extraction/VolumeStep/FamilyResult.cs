using System.Collections.Generic;

namespace ArchiveFixer.Extraction.VolumeStep
{
    /// <summary>族算法的结论（唯一输出形状）。</summary>
    public sealed record FamilyResult(
        PreparedGroupState Verdict,
        IReadOnlyList<string> OrderedPieces,
        IReadOnlyList<int> MissingNumbers,
        CanonicalNamePlan? NamePlan,
        string Evidence);
}

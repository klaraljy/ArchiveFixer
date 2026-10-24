using System.Collections.Generic;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **分卷专属算法的统一契约**（一族的全部职责就在这四件事）：
    /// 成员判据 / 卷号来源 / 片数（完整性） / 规范名计划。
    ///
    /// <para>输入只有 <see cref="VolumeGroupUnit"/>（里面是交界处交来的原始事实），⛔ 不重读盘、不猜名字；
    /// 判不出就返回 <see cref="GroupAnalysis.GroupAnalysisVerdict.Unknown"/>，调用方一个字节都不动。</para>
    /// </summary>
    public interface IVolumeFamilyAlgorithm
    {
        ArchiveFixer.Detection.VolumeNamingFamily Family { get; }

        FamilyResult Evaluate(VolumeGroupUnit unit);
    }

    /// <summary>族算法的结论（唯一输出形状）。</summary>
    public sealed record FamilyResult(
        GroupAnalysis.GroupAnalysisVerdict Verdict,
        IReadOnlyList<string> OrderedPieces,
        IReadOnlyList<int> MissingNumbers,
        CanonicalNamePlan? NamePlan,
        string Evidence);
}

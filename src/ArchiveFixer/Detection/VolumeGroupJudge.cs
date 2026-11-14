using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Detection;

/// <summary>
/// 组判定 —— 哪些桶算分卷组、组键长什么样、组里的卷按什么顺序给出。
///
/// <para><b>为什么独立成层</b>：这是"证据收集"之后唯一允许下"成组"结论的地方。三个条件必须一起判，
/// 拆开就会自相矛盾：① <b>只有本体不算组</b>（<c>x.zip</c> / <c>x.rar</c> 单看名字确实是"第 1 卷"，
/// 但只有同目录同基名下真的存在 <c>.z01</c>/<c>.r00</c> 时才算一组 —— 否则一个装满普通 zip 的目录
/// 会被整体识别成分卷组，那比漏识别糟得多）；② <b>名字被伪装过的组必须再过尺寸规律</b>
/// （<c>001删除</c> / <c>001.txt</c> 这一档，认错就会去改一批不相干文件的名字）；
/// ③ <b>同基名多套命名要分别拿组键</b>（见 <see cref="VolumeGroupKey.GroupKeyWithFamily"/>）。</para>
///
/// <para><b>失败模型</b>：一个桶都过不了 ⇒ 空结果（<see cref="Array.Empty{T}"/>），
/// ⛔ 绝不为了凑数放宽"只有本体不算组"那一条；内容证明（<c>contentProof</c>）
/// 只用来放行"伪装名 + 尺寸不规律"那一档，不成立时仍按尺寸规律判。</para>
///
/// <para>生命周期：纯静态、无状态；只读桶事实与调用方给的内容证明回调，不查盘、不调引擎。</para>
/// </summary>
internal static class VolumeGroupJudge
{
    /// <summary>把桶判定成组；返回顺序与桶顺序一致（外层排序在 <see cref="VolumeGroupSorting"/>）。</summary>
    public static IReadOnlyList<VolumeGroup> Judge(
        IEnumerable<VolumeGroupBucket> buckets,
        Func<IReadOnlyList<VolumeCandidate>, bool>? contentProof)
    {
        // 只有"本体"没有分卷标记的桶不是分卷组（普通 x.zip / x.rar 就落在这里）。
        List<VolumeGroupBucket> survivors = buckets.Where(b => b.HasVolumeMark).ToList();

        /*
         * 名字**被伪装过**的组（`001删除` / `001.txt`）必须再过一道"尺寸规律"：
         * 除最后一卷外大小完全相等、最后一卷 ≤ 整卷大小。理由（用户 2026-09-28 三层方案）：
         * 宽松的名字判据有可能把"碰巧带数字段"的一堆文件凑成一组，而尺寸规律是**与名字无关**的硬证据；
         * 一旦认错组，一键处理就会去改一批不相干文件的名字 —— 认错比不认糟得多。
         */
        survivors = survivors
            .Where(b => !b.HasDisguisedName
                        || b.HasRegularVolumeSizes
                        || (contentProof != null && contentProof(b.Candidates)))
            .ToList();

        if (survivors.Count == 0)
        {
            return Array.Empty<VolumeGroup>();
        }

        // 同一"目录+基名"下有几套命名；只有多于一套时 GroupKey 才需要加族标记来保证唯一。
        Dictionary<string, int> familyCounts = survivors
            .GroupBy(b => VolumeGroupKey.BaseKey(b.DirectoryPath, b.BaseName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return survivors.Select(bucket => Build(bucket, familyCounts)).ToList();
    }

    /// <summary>把一个判定成立的桶拼成 <see cref="VolumeGroup"/>；缺卷/完整度/文案全部转交 <see cref="VolumeGroupGap"/>。</summary>
    private static VolumeGroup Build(VolumeGroupBucket bucket, Dictionary<string, int> familyCounts)
    {
        List<int> indices = bucket.SortedIndices;
        List<VolumeCandidate> volumes = indices.Select(i => bucket[i]).ToList();

        string baseKey = VolumeGroupKey.BaseKey(bucket.DirectoryPath, bucket.BaseName);
        string groupKey = familyCounts.TryGetValue(baseKey, out int sameBaseFamilyCount) && sameBaseFamilyCount > 1
            ? VolumeGroupKey.GroupKeyWithFamily(baseKey, bucket.Family)
            : baseKey;
        VolumeGroupGap gap = VolumeGroupGap.Assess(bucket, indices);

        return new VolumeGroup
        {
            GroupKey = groupKey,
            DirectoryPath = bucket.DirectoryPath,
            BaseName = bucket.BaseName,
            Volumes = volumes,
            FirstVolumePath = volumes[0].Path,
            KnownVolumeCount = volumes.Count,
            ExpectedVolumeCount = 0,
            IsComplete = gap.IsComplete,
            MissingVolumeNames = gap.MissingVolumeNames,
            Note = gap.Describe(bucket, volumes)
        };
    }
}

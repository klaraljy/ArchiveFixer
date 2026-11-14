using ArchiveFixer.Helpers;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Detection;

/// <summary>
/// 证据收集 —— 把调用方给的候选清单变成"按目录 + 基名 + 族分好的桶"，一份名字只解析一次。
///
/// <para><b>为什么独立成层</b>：收集段只做三件事：① 同一个文件被喂两次（"扫描目录"与"用户手工添加"
/// 各来一遍）只算一次；② 每个名字只解析一次，解析不出（不是分卷/本体）就不进任何桶；
/// ③ 桶键由 <see cref="VolumeGroupKey.BucketKey"/> 给，族必须进键。它不判"成不成组"，
/// 判定在 <see cref="VolumeGroupJudge"/>；它也不排序，顺序在 <see cref="VolumeGroupSorting"/>。</para>
///
/// <para><b>失败模型</b>：候选为 <c>null</c> / 路径为空 / 名字判不出 ⇒ <b>跳过这一份</b>
/// （⛔ 不猜、不补、不硬凑进某个桶）；<c>files</c> 为 <c>null</c> ⇒ 空桶清单。</para>
///
/// <para>生命周期：每次调用新建桶集合，纯同步；只读调用方给的路径事实，不查盘、不读内容。</para>
/// </summary>
internal static class VolumeGroupEvidenceCollector
{
    /// <summary>收集桶；返回顺序 = 桶首次出现的顺序（外层排序由 <see cref="VolumeGroupSorting"/> 负责）。</summary>
    public static IReadOnlyList<VolumeGroupBucket> Collect(IEnumerable<VolumeCandidate>? files)
    {
        if (files == null)
        {
            return Array.Empty<VolumeGroupBucket>();
        }

        var buckets = new List<VolumeGroupBucket>();
        var byKey = new Dictionary<string, VolumeGroupBucket>(StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (VolumeCandidate candidate in files)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Path))
            {
                continue;
            }

            // 同一个文件被喂两次（"扫描目录"和"用户手工添加"各来一遍）只算一次。
            if (!seenPaths.Add(candidate.Path))
            {
                continue;
            }

            VolumeNameInfo? info = VolumeNameEvidence.Analyze(candidate.Path);
            if (info == null)
            {
                continue;
            }

            string directoryPath = FileNameHelper.GetDirectoryName(candidate.Path);
            string bucketKey = VolumeGroupKey.BucketKey(directoryPath, info.BaseName, info.Family);

            if (!byKey.TryGetValue(bucketKey, out VolumeGroupBucket? bucket))
            {
                bucket = new VolumeGroupBucket(directoryPath, info.BaseName, info.Family);
                byKey.Add(bucketKey, bucket);
                buckets.Add(bucket);
            }

            bucket.Add(candidate, info);
        }

        return buckets;
    }
}

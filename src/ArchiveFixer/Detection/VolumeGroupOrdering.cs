using ArchiveFixer.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Detection;

/// <summary>
/// 卷序编排 —— 候选怎么排、槽位怎么挑、试开要试哪几种排列、引擎认下来的顺序怎么折回结论。
///
/// <para><b>为什么排序要单独成层</b>：排序/槽位是**弱证据的原料**（"按体积 + 位置推定"），
/// 而弱证据只在"没有卷号的候选"这一档使用。把它和判定混在一起，就看不出"哪一步只是排序、
/// 哪一步才是在下结论"。这里产出的顺序**一律只是假设**：成不成立由证据与试开回答。</para>
///
/// <para><b>失败模型</b>：候选为空 / 第 1 卷没定位出来 / 卷号有洞 ⇒ 凑不出可试的顺序 ⇒ 返回空集合
/// （调用方按"这一次没取到证据"记，⛔ 不是"不成立"）；绝不抛、绝不硬凑一个顺序出去。</para>
///
/// <para>生命周期：纯静态、无状态；只读调用方给的事实清单，不枚举目录、不碰文件系统、不调引擎。</para>
/// </summary>
internal static class VolumeGroupOrdering
{
    /// <summary>试开最多试几种排列（试开一次是一次进程调用，不能让它在几十种排列上跑）。</summary>
    public const int MaxTrialOrderings = 8;

    /// <summary>候选排序：先等大的（裸切续卷），再按目录内位置、时间、名字（都只当弱证据的排序依据）。</summary>
    public static IEnumerable<VolumeGroupEntry> RankCandidates(IReadOnlyList<VolumeGroupEntry> candidates) =>
        candidates
            .OrderByDescending(e => e.Size)
            .ThenBy(e => e.OrderIndex)
            .ThenBy(e => e.LastWriteTimeUtc)
            .ThenBy(e => Path.GetFileName(e.Path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 给一个候选挑槽。
    ///
    /// <para><c>.z01</c>/<c>.r00</c> 族里那个**不带卷号的本体位**优先给它 ——
    /// 那正是"改坏了名字"最自然的落地处（真案 ②）。其余情况按卷序从小到大填。</para>
    /// </summary>
    public static int PickSlot(IReadOnlyList<int> slots, VolumeNameFamily family)
    {
        List<int> ordered = slots.OrderBy(i => i).ToList();

        if (family is VolumeNameFamily.ZipSpanned or VolumeNameFamily.RarOld && ordered.Contains(1))
        {
            return 1;
        }

        return ordered[0];
    }

    /// <summary>试开要试的几种排列（第 1 卷由 <see cref="VolumeTrialRequest.FirstVolumePath"/> 单独给）。</summary>
    public static List<List<VolumeCandidate>> BuildTrialOrderings(VolumeGroupResolution baseline)
    {
        var result = new List<List<VolumeCandidate>>();
        var byIndex = new Dictionary<int, ResolvedVolume>();

        foreach (ResolvedVolume volume in baseline.Volumes)
        {
            if (!byIndex.ContainsKey(volume.Index))
            {
                byIndex[volume.Index] = volume;
            }
        }

        if (byIndex.Count <= 1)
        {
            return result;
        }

        int max = byIndex.Keys.Max();

        // 只有"1..max 一个不缺"才凑得出可试的完整顺序（缺一卷 7-Zip 必然报 Missing volume）。
        for (int index = 1; index <= max; index++)
        {
            if (!byIndex.ContainsKey(index))
            {
                return result;
            }
        }

        var ordered = new List<VolumeCandidate>();

        for (int index = 2; index <= max; index++)
        {
            ordered.Add(new VolumeCandidate { Path = byIndex[index].Path, Size = byIndex[index].Size });
        }

        result.Add(ordered);

        /*
         * 排列的变体：把"靠推定才认进来的"那些卷**互换位置**再试 ——
         * 位置上分不清谁是谁的时候，让引擎来分。上限 MaxTrialOrderings：试开一次是一次进程调用。
         */
        var swapable = new List<int>();

        for (int i = 0; i < ordered.Count; i++)
        {
            if (byIndex[i + 2].InferredByPosition)
            {
                swapable.Add(i);
            }
        }

        for (int i = 0; i < swapable.Count && result.Count < MaxTrialOrderings; i++)
        {
            for (int j = i + 1; j < swapable.Count && result.Count < MaxTrialOrderings; j++)
            {
                var variant = new List<VolumeCandidate>(ordered);
                (variant[swapable[i]], variant[swapable[j]]) = (variant[swapable[j]], variant[swapable[i]]);
                result.Add(variant);
            }
        }

        return result;
    }

    /// <summary>把试开认下来的顺序折回 <see cref="ResolvedVolume"/>（名字这一条事实按原名重算）。</summary>
    public static IReadOnlyList<ResolvedVolume> RebuildFromTrial(
        VolumeGroupResolution baseline,
        VolumeTrialOutcome trial)
    {
        var byPath = new Dictionary<string, ResolvedVolume>(StringComparer.OrdinalIgnoreCase);

        foreach (ResolvedVolume volume in baseline.Volumes)
        {
            byPath[volume.Path] = volume;
        }

        var result = new List<ResolvedVolume>();

        foreach (string? path in trial.OrderedVolumePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string full = SafePathHelper.GetFullPathSafe(path);

            if (full.Length == 0 || result.Any(v => string.Equals(v.Path, full, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (byPath.TryGetValue(full, out ResolvedVolume? known))
            {
                result.Add(new ResolvedVolume
                {
                    Path = known.Path,
                    Index = result.Count + 1,
                    Size = known.Size,
                    NameCarriesVolumeNumber = known.NameCarriesVolumeNumber,
                    InferredByPosition = known.InferredByPosition,
                    ConfirmedByTrialOpen = true
                });

                continue;
            }

            result.Add(new ResolvedVolume
            {
                Path = full,
                Index = result.Count + 1,
                Size = VolumeFileFacts.SizeOf(full),
                NameCarriesVolumeNumber = false,
                InferredByPosition = false,
                ConfirmedByTrialOpen = true
            });
        }

        return result.Count > 0 ? result : baseline.Volumes;
    }
}

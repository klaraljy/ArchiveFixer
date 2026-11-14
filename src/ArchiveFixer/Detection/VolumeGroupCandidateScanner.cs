using ArchiveFixer.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Detection;

/// <summary>
/// 候选池扫描 —— 只回答"同目录里哪些文件算这一组的候选、它们占哪个槽、哪一些只是推定"。
///
/// <para><b>为什么独立成层</b>：候选那一档是**弱证据的唯一来源**，也是最容易把"同目录的另一份东西"
/// 当成本组续卷的地方。它有三件事必须一起做才不会自相矛盾：① 在位的卷（带卷号）先占死槽位；
/// ② 同一卷的名字变体**不占独立卷号**（用户 2026-09-27 真机：<c>222.zip</c> 与 <c>222.zi删除p</c>
/// 是同一个逻辑卷的两个名字）；③ 名字对得上但没卷号的候选才按体积 + 位置排队进剩下的槽。
/// 这三件事拆开写，就会出现"同一句话里既说缺 <c>222.zip</c>、又说那个文件就是第 1 卷"（整层定稿作废）。</para>
///
/// <para>⛔ 这里**一个判据都不新造**：族由 <see cref="VolumeGroupNaming"/> 定、排序与槽位由
/// <see cref="VolumeGroupOrdering"/> 给、体积口径由 <see cref="LooksLikeVolumeCandidate"/> 回答。
/// 本类只负责"分类 + 装进 <see cref="VolumeCandidateScan"/> 这一份快照"。</para>
///
/// <para><b>失败模型</b>：组里一卷可解析卷号的都没有 ⇒ 立刻返回空快照（<see cref="VolumeCandidateScan.Located"/>
/// 为空），判定方据此报"判不出"；⛔ 不猜槽位、不硬凑候选、不读盘（只读调用方给的事实清单）。</para>
///
/// <para>生命周期：纯静态、无状态；输入是"同目录事实 + 归组结果 + 族 + 基名"，输出一份只读快照。</para>
/// </summary>
internal static class VolumeGroupCandidateScanner
{
    /// <summary>把同目录事实分类成"在位的卷 / 名字变体 / 没卷号的候选"，并给出槽位与推定说明。</summary>
    public static VolumeCandidateScan Scan(
        IReadOnlyList<VolumeGroupEntry> entries,
        VolumeGroup group,
        VolumeNameFamily family,
        string baseName)
    {
        var located = new List<ResolvedVolume>();
        var locatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (VolumeCandidate volume in group.Volumes)
        {
            string full = SafePathHelper.GetFullPathSafe(volume.Path);
            int index = LegacyVolumeNames.TryGetIndex(Path.GetFileName(full)) ?? 0;

            if (index <= 0 || !locatedPaths.Add(full))
            {
                continue;
            }

            located.Add(new ResolvedVolume
            {
                Path = full,
                Index = index,
                Size = volume.Size,
                NameCarriesVolumeNumber = true,
                InferredByPosition = false,
                ConfirmedByTrialOpen = false
            });
        }

        located.Sort((a, b) => a.Index.CompareTo(b.Index));

        if (located.Count == 0)
        {
            return new VolumeCandidateScan { Located = located };
        }

        int maxFound = located[^1].Index;
        long fullSize = located[0].Size;
        var openSlots = new List<int>();

        for (int index = 1; index <= maxFound; index++)
        {
            if (!located.Any(v => v.Index == index))
            {
                openSlots.Add(index);
            }
        }

        /*
         * 边界槽 = `.z01`/`.r00` 族那个「规范名不带卷号」的本体位（`x.zip` / `x.rar`）。
         * 它天然就是"名字被改坏"落地的地方（真案 ②：一只顶美.z删除ip），所以这一档的候选算弱证据。
         */
        bool boundarySlotOpen = family is VolumeNameFamily.ZipSpanned or VolumeNameFamily.RarOld
            && openSlots.Contains(1);

        var unrecognized = new List<VolumeGroupEntry>();
        string groupStem = VolumeGroupNaming.StemOf(baseName);

        /*
         * **同一卷的名字变体**（用户 2026-09-27 真机）。
         *
         * 现场：暂存目录里同时有 `222.zip` 与 `222.zi删除p` —— 它们是**同一个逻辑卷的两个名字**
         * （`zi删除p` 就是 `zip` 被塞了两个中文字），而不是"第 1 卷 + 一个不认识的兄弟"。
         * 老判据只看"基名段相同 + 体积对得上"，于是把后者当成"没有卷号的候选"，
         * 推进边界槽 1，再据此写出**自相矛盾**的那句话：
         * 「名字上看缺 `222.zip`；同目录的 `222.zi删除p` 按体积 + 位置推定是第 1 卷」
         * —— 组里那一位就是 `222.zip`，它却报"缺"。整层定稿因此作废，用户的产物一个字节没落地。
         *
         * 判据：候选"去掉杂质之后的后缀"若与该槽**规范名逐字相等**（`222.zi删除p` → `222.zip`、
         * `222.zscip` → `222.zip`），它就是那一卷的名字变体：
         *   · 算组成员（保护名单照旧，⛔ 更不会进可删过程物）；
         *   · **不占独立卷号、不进推断清单** —— 组齐不齐由真实在位的那些卷回答。
         */
        var variantPaths = new List<string>();
        bool variantHasDisguisedName = false;

        foreach (VolumeGroupEntry entry in entries)
        {
            string full = SafePathHelper.GetFullPathSafe(entry.Path);

            if (locatedPaths.Contains(full) ||
                !string.Equals(VolumeGroupNaming.StemOf(Path.GetFileName(full)), groupStem, StringComparison.OrdinalIgnoreCase) ||
                !LooksLikeVolumeCandidate(entry.Size, fullSize))
            {
                continue;
            }

            if (VolumeGroupNaming.TryMatchVolumeVariant(Path.GetFileName(full), located, out bool variantIsDisguised))
            {
                variantPaths.Add(full);

                /*
                 * 同号的那个名字**不是这一族的规范名** ⇒ 整组算"组里有名字不标准的卷"。
                 *
                 * 为什么必须这样（用户 2026-09-27 真机）：`222.zip` 与 `222.zi删除p` 是同一个逻辑卷的
                 * 两个名字，可**只有一份**是引擎认得的那个（7-Zip 按原名只认 `.zip`）。真机里源包被改名
                 * 成 `222.zscip.zip`（见 `VolumeNameRepair`：目标名被占就不改），所以盘上那个
                 * `222.zi删除p` 很可能就是**没被改到名的另一卷** —— 它名字不标准，整组就不许进
                 * 可删的其余物（25 GB 那次的同一条红线）。
                 *
                 * ⛔ 反过来，同号两个名字**都标准**时（`x.7z.001` 与 `x.7z.001副本` 这种）不算 ——
                 * 那一档是"同一个文件被列了两次"，按规范名办事没有任何歧义。
                 */
                if (variantIsDisguised)
                {
                    variantHasDisguisedName = true;
                }

                continue;
            }

            unrecognized.Add(entry);
        }

        /*
         * 多余的候选（槽不够塞）⇒ 名字序号后面**可能还有卷**：
         * 这就是"伪装成 .mp4 / .apk 的续卷"（真案 ③）与"末卷被改名成别的后缀"落地的地方。
         * 每多一个候选就多开一个虚拟槽（卷序接着最大值往下排）。
         */
        var fillSlots = new List<int>(openSlots);

        for (int k = 1; k <= unrecognized.Count - openSlots.Count; k++)
        {
            fillSlots.Add(maxFound + k);
        }

        var inferred = new List<ResolvedVolume>();
        var inferredNotes = new List<string>();
        var remaining = new List<int>(fillSlots);

        foreach (VolumeGroupEntry entry in VolumeGroupOrdering.RankCandidates(unrecognized))
        {
            if (remaining.Count == 0)
            {
                break;
            }

            int slot = VolumeGroupOrdering.PickSlot(remaining, family);
            remaining.Remove(slot);

            string note = $"{Path.GetFileName(entry.Path)}（{VolumeGroupEvidenceBuilder.FormatBytes(entry.Size)}）按体积 + 位置推定是第 {slot} 卷";

            if (slot != 1 || !boundarySlotOpen)
            {
                note += " —— ⛔ 它的名字里没有任何卷号，所以只当推定、不当已找到的卷（程序不改你的文件名）";
            }

            inferredNotes.Add(note);

            inferred.Add(new ResolvedVolume
            {
                Path = SafePathHelper.GetFullPathSafe(entry.Path),
                Index = slot,
                Size = entry.Size,
                NameCarriesVolumeNumber = false,
                InferredByPosition = true,
                ConfirmedByTrialOpen = false
            });
        }

        return new VolumeCandidateScan
        {
            Located = located,
            MaxFound = maxFound,
            OpenSlots = openSlots,
            BoundarySlotOpen = boundarySlotOpen,
            VariantPaths = variantPaths,
            VariantHasDisguisedName = variantHasDisguisedName,
            Unrecognized = unrecognized,
            Inferred = inferred,
            InferredNotes = inferredNotes,
            RemainingSlots = remaining
        };
    }

    /// <summary>
    /// 这个候选"像不像一卷"：裸切 ⇒ 与满卷**等大**；边界槽（<c>.z01</c>/<c>.r00</c> 族那个没卷号的本体位）
    /// 允许更短。⛔ 体积不搭的一律不当候选 —— 这是把"同目录的说明文件 / 封面"挡在外面的唯一一道闸。
    /// </summary>
    private static bool LooksLikeVolumeCandidate(long size, long fullSize)
    {
        /*
         * 基名段已经相等（调用方筛过）之后，体积只要 0 < size ≤ 满卷 就算候选 ——
         * 裸切的续卷与满卷**等大**，而**最后一卷必然更短**，所以"更短的"恰恰是
         * "末卷被改名成别的后缀"最常见的形状。
         *
         * ⛔ 比满卷**还大**的一律不当候选：裸切切不出比满卷更大的片，那是同目录的另一份东西。
         *
         * ⚠ 代价写在明处：这一条宽判据会让"同目录一份同基名的说明文件"也算候选，
         * 于是整组只到「疑缺卷」这一档、定稿计划作废（**什么都不搬、不删**）。
         * 这个取舍是故意的 —— 认错组比不认糟得多：漏掉一个被改名的末卷，它会被当成内容物搬走，
         * 而其余卷进了可删的其余物（叠加「空间不足」模式的永久删除就是不可逆的数据丢失）。
         * 要确认只能靠试开（VolumeEvidenceKind.TrialOpen）。
         */
        return size > 0 && fullSize > 0 && size <= fullSize;
    }
}

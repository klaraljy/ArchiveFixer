using System;
using System.Collections.Generic;
using System.Linq;

using ArchiveFixer.Helpers;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// **"缺什么补什么"的打分与分配**（用户 2026-10-14 凌晨口径 ①③，唯一出口）。
    ///
    /// <para>用户原话（逐字）：「后缀相似算法、空间计算法（除了末卷都相同）、基名匹配，你可以升级这几条
    /// 的匹配机制，**这个是第二大步一开始就要做的事**」＋「根据**剩下的缺什么，就补什么**去设计尝试一下……
    /// `.7z.005729572` 这种，就要分析同伴缺什么，缺 `003` 那就很大概率是 `003`（只是可能不是一定）；
    /// 两个都看不懂就看**大小**、看**后缀有没有相似的地方**」。</para>
    ///
    /// <para><b>这一档只做"算"</b>：给"缺哪几个槽位" + "几个名字读不出卷号的候选"，按三条既有证据打分，
    /// 给出一对一的**最佳分配**（以及"次佳"，供上层在引擎试开失败时换边）。
    /// ⛔ 不碰盘上任何东西、⛔ 不自己拼名字（规范槽位名仍由
    /// <see cref="VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex"/> 给）。</para>
    ///
    /// <para>三条证据与权重（都要能被日志打印出来，用户口径②要求顺序与次数可调）：
    /// <list type="number">
    /// <item><description><b>基名匹配</b>（权重 <see cref="BaseNameWeight"/>）：归档基名（唯一出口
    /// <see cref="FileNameHelper.GetArchiveBaseName"/>）与这一组的基名逐字相同；</description></item>
    /// <item><description><b>后缀相似</b>（权重 <see cref="SuffixWeight"/>）：名字与"规范槽位名"的相似度
    /// （<see cref="Similarity"/>：公共子序列占较长者的比例）—— 治的是"卷标记里夹垃圾"
    /// （`.005729572` 与 `.003` 仍有共同前缀 `.00`）；</description></item>
    /// <item><description><b>空间计算</b>（权重 <see cref="SizeWeight"/>）："除末卷外等大"那条既有规律
    /// （<see cref="VolumeGroupDetector.HasRegularVolumeSizes"/> 用的就是它）—— 满槽位要求体积 == 已知满体积，
    /// 末槽位要求 ≤ 满体积。</description></item>
    /// </list></para>
    /// </summary>
    internal static class VolumeSlotScoring
    {
        internal const int BaseNameWeight = 4;
        internal const int SuffixWeight = 3;
        internal const int SizeWeight = 3;

        /// <summary>一个候选片（只带"算分要用的事实"，⛔ 不持有任务、⛔ 不碰盘）。</summary>
        internal sealed class Candidate
        {
            internal string Path { get; init; } = string.Empty;

            internal string FileName { get; init; } = string.Empty;

            internal long Size { get; init; }

            /// <summary>内容读出来的卷号（判不出 = null）。</summary>
            internal int? ContentNumber { get; init; }
        }

        /// <summary>一次分配：哪个候选去哪个槽位、总分多少（日志用）。</summary>
        internal sealed class Assignment
        {
            internal IReadOnlyList<(Candidate Candidate, int Slot)> Pairs { get; init; }
                = Array.Empty<(Candidate, int)>();

            internal int TotalScore { get; init; }

            /// <summary>这一趟是不是唯一最优（不唯一 ⇒ 上层要留着"换边"）。</summary>
            internal bool HasTie { get; init; }

            /// <summary>每一对的逐项得分（写成日志的判据输入）。</summary>
            internal IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
        }

        /// <summary>
        /// 给"缺的槽位"与"名字读不出卷号的候选"做一对一分配（⛔ 数目不等 ⇒ 返回 null：判不出，什么都不做）。
        ///
        /// <para>返回的是**按总分排序的前两名**：第一名先试，失败再试第二名（用户口径③的"换一边"）。
        /// 两名之间分数相同（<see cref="Assignment.HasTie"/>）时也照给 —— 上层据此知道"这一趟只是概率"。</para>
        /// </summary>
        /// <param name="missingSlots">缺哪几个卷号（1 起，例如 { 3 } 或 { 2, 3 }）。</param>
        /// <param name="candidates">名字读不出卷号的候选（数目必须与槽位数相等）。</param>
        /// <param name="groupBaseName">这一组的基名（唯一出口给出来的那个）。</param>
        /// <param name="fullVolumeSize">已知的"满体积"（除末卷外等大那条规律里的那个大小）；判不出 = 0。</param>
        /// <param name="lastSlot">这一组的末槽位号（用来判"这个槽位是不是末卷"）；判不出 = 0。</param>
        /// <param name="canonicalNameForSlot">槽位号 ⇒ 规范槽位名（由上层用既有命名出口给）。</param>
        internal static IReadOnlyList<Assignment> Rank(
            IReadOnlyList<int> missingSlots,
            IReadOnlyList<Candidate> candidates,
            string groupBaseName,
            long fullVolumeSize,
            int lastSlot,
            Func<int, string> canonicalNameForSlot)
        {
            if (missingSlots == null || candidates == null
                || missingSlots.Count == 0 || candidates.Count != missingSlots.Count)
            {
                return Array.Empty<Assignment>();
            }

            // 候选数与槽位数都很少（≤6）⇒ 直接枚举全排列取最优，不做启发式（少一处"看起来对"的近似）。
            if (missingSlots.Count > 6)
            {
                return Array.Empty<Assignment>();
            }

            var scored = new List<Assignment>();

            foreach (int[] permutation in Permutations(missingSlots.Count))
            {
                var pairs = new List<(Candidate, int)>();
                var evidence = new List<string>();
                int total = 0;

                for (int i = 0; i < permutation.Length; i++)
                {
                    Candidate candidate = candidates[i];
                    int slot = missingSlots[permutation[i]];

                    string canonical = canonicalNameForSlot(slot) ?? string.Empty;

                    int baseScore = string.Equals(
                        FileNameHelper.GetArchiveBaseName(candidate.FileName),
                        groupBaseName,
                        StringComparison.OrdinalIgnoreCase)
                        ? BaseNameWeight
                        : 0;

                    double similarity = Similarity(candidate.FileName, canonical);
                    int suffixScore = (int)Math.Round(similarity * SuffixWeight, MidpointRounding.AwayFromZero);

                    int sizeScore = ScoreSize(candidate.Size, slot, lastSlot, fullVolumeSize);

                    total += baseScore + suffixScore + sizeScore;

                    evidence.Add(
                        $"{candidate.FileName} → {slot}（基名 {baseScore}/{BaseNameWeight}，"
                        + $"后缀相似 {suffixScore}/{SuffixWeight}（{similarity:0.00}），"
                        + $"体积 {sizeScore}/{SizeWeight}）");
                }

                pairs.AddRange(permutation.Select((slotIndex, candidateIndex) => (candidates[candidateIndex], missingSlots[slotIndex])));

                scored.Add(new Assignment { Pairs = pairs, TotalScore = total, Evidence = evidence });
            }

            List<Assignment> ordered = scored.OrderByDescending(item => item.TotalScore).ToList();

            bool tie = ordered.Count > 1 && ordered[1].TotalScore == ordered[0].TotalScore;

            return ordered
                .Take(2)
                .Select(item => new Assignment
                {
                    Pairs = item.Pairs,
                    TotalScore = item.TotalScore,
                    HasTie = tie,
                    Evidence = item.Evidence
                })
                .ToList();
        }

        /// <summary>
        /// "空间计算"那一票：满槽位（不是末卷）要求体积**正好等于**已知满体积；末卷槽位要求 ≤ 它。
        /// 判不出（满体积 0 / 末槽位 0）⇒ 0 分（⛔ 不给"猜"的分数）。
        /// </summary>
        private static int ScoreSize(long size, int slot, int lastSlot, long fullVolumeSize)
        {
            if (size <= 0 || fullVolumeSize <= 0)
            {
                return 0;
            }

            if (lastSlot > 0 && slot == lastSlot)
            {
                return size <= fullVolumeSize ? SizeWeight : 0;
            }

            return size == fullVolumeSize ? SizeWeight : 0;
        }

        /// <summary>
        /// 名字相似度（0..1）：公共子序列长度 ÷ 较长者长度（⛔ 不用编辑距离 —— 它会把
        /// `.005729572` 与 `.003` 判得比 `.003` 与 `.004` 更远，而这一档要的正是"卷标记的骨架像不像"）。
        /// </summary>
        internal static double Similarity(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return 0;
            }

            int[,] table = new int[left.Length + 1, right.Length + 1];

            for (int i = 1; i <= left.Length; i++)
            {
                for (int j = 1; j <= right.Length; j++)
                {
                    table[i, j] = left[i - 1] == right[j - 1]
                        ? table[i - 1, j - 1] + 1
                        : Math.Max(table[i - 1, j], table[i, j - 1]);
                }
            }

            return (double)table[left.Length, right.Length] / Math.Max(left.Length, right.Length);
        }

        /// <summary>0..n-1 的全排列（n ≤ 6 ⇒ 最多 720 个，安全）。</summary>
        private static IEnumerable<int[]> Permutations(int count)
        {
            var current = Enumerable.Range(0, count).ToArray();

            return Enumerate(current, 0);

            static IEnumerable<int[]> Enumerate(int[] items, int start)
            {
                if (start == items.Length - 1)
                {
                    yield return (int[])items.Clone();
                    yield break;
                }

                for (int i = start; i < items.Length; i++)
                {
                    (items[start], items[i]) = (items[i], items[start]);

                    foreach (int[] next in Enumerate(items, start + 1))
                    {
                        yield return next;
                    }

                    (items[start], items[i]) = (items[i], items[start]);
                }
            }
        }
    }
}

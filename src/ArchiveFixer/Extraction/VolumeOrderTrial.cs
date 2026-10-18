using System;
using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **中间片顺序的高概率尝试**（用户 2026-10-18 口径）。只回答"该按什么顺序去试"，一个字节都不动。
    ///
    /// <para><b>为什么需要它</b>：通用 ZIP 分片只有**末片**能从内容确认；中间片内容里没有卷号，
    /// 名字被伪装成 `.png` / `.zip` 之类时也读不出"我是第几片"。此时不能靠名字硬判（会把两份
    /// 等大的中间片排反 ⇒ 引擎 `Unexpected end of archive`），也不能不试（整组永远出不来）。
    /// 正确做法：名字/后缀能给出卷号的先定；读不出卷号的中间片按"与规范槽位名的后缀相似度"
    /// 排一个高概率顺序先试，试不出再把剩余排列全部试一遍（用户：片数最多 5 片，试到 5 片为止）。</para>
    ///
    /// <para>⛔ 本类**不读文件内容、不建链接、不调引擎**；试开与落地由调用方（唯一出口）做。
    /// 返回的第一项 = 相似度最高的那一版；顺序列表已去重、按分数从高到低。</para>
    /// </summary>
    internal static class VolumeOrderTrial
    {
        /// <summary>用户口径：见过的分卷片数上限（超过这个数不在这里猜）。</summary>
        internal const int MaxPieces = 5;

        /// <summary>排列上限：中间片最多 4 片 ⇒ 24 种；给调用方的试开次数封顶。</summary>
        internal const int MaxOrders = 24;

        /// <summary>
        /// 生成候选顺序（第一项 = 高概率顺序，后面 = 其余排列）。
        ///
        /// <para>已知末片由 <paramref name="tailPath"/> 固定（通用 ZIP 只有它能从内容确认；
        /// part/N 族由调用方按 N 最大者给）；名字里读得出卷号的片固定在对应槽位；
        /// 只剩"既不是末片、又读不出卷号"的中间片做排列。</para>
        /// </summary>
        internal static IReadOnlyList<IReadOnlyList<string>> BuildCandidateOrders(
            IReadOnlyList<string>? pieces,
            string? tailPath,
            VolumeNamingFamily family,
            string stem)
        {
            var distinct = new List<string>();

            foreach (string? piece in pieces ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(piece))
                {
                    continue;
                }

                if (!distinct.Any(existing => string.Equals(existing, piece, StringComparison.OrdinalIgnoreCase)))
                {
                    distinct.Add(piece);
                }
            }

            if (distinct.Count < 2 || distinct.Count > MaxPieces || string.IsNullOrWhiteSpace(stem))
            {
                return Array.Empty<IReadOnlyList<string>>();
            }

            int count = distinct.Count;
            string? tail = !string.IsNullOrWhiteSpace(tailPath)
                && distinct.Any(piece => string.Equals(piece, tailPath, StringComparison.OrdinalIgnoreCase))
                    ? distinct.First(piece => string.Equals(piece, tailPath, StringComparison.OrdinalIgnoreCase))
                    : null;

            // 槽位 1..count：**末片硬固定**；其余片的名字卷号只当“高概率排序依据”，⛔ 不当硬约束 ——
            // 名字被伪装成 `.zip` 时名字读出来的“第 1 卷”常常是假的（真第 1 片可能叫 `.png`），
            // 硬固定会把正确顺序排除在外，试开就永远救不回来。
            var fixedSlots = new Dictionary<int, string>();
            var freePieces = new List<string>();

            foreach (string piece in distinct)
            {
                if (tail != null && string.Equals(piece, tail, StringComparison.OrdinalIgnoreCase))
                {
                    fixedSlots[count] = piece;
                    continue;
                }

                freePieces.Add(piece);
            }

            var freeSlots = Enumerable.Range(1, count)
                .Where(slot => !fixedSlots.ContainsKey(slot))
                .ToList();

            // 名字相似度 + 名字卷号与槽位一致 = “高概率”的两条依据；两者都只影响排序，不影响是否尝试。
            var expectedNames = VolumeNumberFromContent.BuildStandardNames(stem, family, count);
            var orders = new List<(int Score, IReadOnlyList<string> Order)>();

            foreach (IReadOnlyList<string> permutation in Permute(freePieces))
            {
                var order = new string[count];

                foreach ((int slot, string piece) in fixedSlots)
                {
                    order[slot - 1] = piece;
                }

                for (int i = 0; i < freeSlots.Count; i++)
                {
                    order[freeSlots[i] - 1] = permutation[i];
                }

                if (order.Any(piece => string.IsNullOrEmpty(piece)))
                {
                    continue;
                }

                int score = 0;

                for (int i = 0; i < count; i++)
                {
                    score += Similarity(FileNameHelper.GetFileName(order[i]), i < expectedNames.Count ? expectedNames[i] : string.Empty);

                    int? index = VolumeGroupDetector.TryGetVolumeIndex(FileNameHelper.GetFileName(order[i]));

                    if (index is int known && known == i + 1)
                    {
                        // 名字卷号与槽位一致：高概率优先，但排在它后面的“另一种顺序”仍会保留。
                        score += 1000;
                    }
                }

                orders.Add((score, order));
            }

            return orders
                .OrderByDescending(item => item.Score)
                .ThenBy(item => string.Join("|", item.Order), StringComparer.OrdinalIgnoreCase)
                .Take(MaxOrders)
                .Select(item => item.Order)
                .ToList();
        }

        /// <summary>后缀/前缀相似度：共同前缀 + 共同后缀；规范槽位名对不上时也不编，只给低分。</summary>
        private static int Similarity(string name, string expected)
        {
            if (name.Length == 0 || expected.Length == 0)
            {
                return 0;
            }

            int prefix = 0;

            while (prefix < name.Length && prefix < expected.Length
                   && char.ToLowerInvariant(name[prefix]) == char.ToLowerInvariant(expected[prefix]))
            {
                prefix++;
            }

            int suffix = 0;

            while (suffix < name.Length && suffix < expected.Length
                   && char.ToLowerInvariant(name[name.Length - 1 - suffix]) == char.ToLowerInvariant(expected[expected.Length - 1 - suffix]))
            {
                suffix++;
            }

            return prefix + suffix;
        }

        private static IEnumerable<IReadOnlyList<string>> Permute(IReadOnlyList<string> items)
        {
            if (items.Count == 0)
            {
                yield return Array.Empty<string>();
                yield break;
            }

            if (items.Count > MaxPieces)
            {
                yield break;
            }

            var used = new bool[items.Count];
            var current = new string[items.Count];

            IEnumerable<IReadOnlyList<string>> Fill(int position)
            {
                if (position == items.Count)
                {
                    yield return current.ToArray();
                    yield break;
                }

                for (int i = 0; i < items.Count; i++)
                {
                    if (used[i])
                    {
                        continue;
                    }

                    used[i] = true;
                    current[position] = items[i];

                    foreach (IReadOnlyList<string> result in Fill(position + 1))
                    {
                        yield return result;
                    }

                    used[i] = false;
                }
            }

            foreach (IReadOnlyList<string> result in Fill(0))
            {
                yield return result;
            }
        }
    }
}

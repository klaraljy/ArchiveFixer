using ArchiveFixer.Detection;
using System.IO;

namespace ArchiveFixer.Extraction;

/// <summary>
/// **失败模型 + 定案**（第二大步发现层的第 5 段）：一组候选到底是"可证完整 / 缺片 / 判不出"，
/// 以及判不出时如实写出"为什么"。
///
/// <para>这里是**唯一出口**：`Unknown`（判不出 ⇒ 下游什么都不做）、缺卷清单、以及每条证据措辞
/// 都在本类里生成；⛔ 别的层不许自己拼"缺几片/判不出"的文案，也不许把名字/大小猜成结论。</para>
///
/// <para>判据顺序（与拆分前一个字不改）：① 内容卷号 → ② 名字卷标记 → ③ 7z 自述整包对账 →
/// ④ 只按大小定序（只回答"谁可能是末片"，回答不了"该有几片"⇒ 判不出、⛔ 不猜缺卷）；
/// 单卷完整包（`SingleComplete` + 可证非成员）直判"不是分卷组"，⛔ 不给假 Complete；
/// 跨盘 ZIP 另走名字定序（<see cref="VolumeGroupPieceOrdering.TryOrderSpannedZip"/>）。</para>
///
/// <para><b>失败模型的三档</b>：① 判不出（证据不足/冲突）：<c>MissingNumbers</c> 空、入口尽量给、
/// 证据写清缺什么；② 缺片（能点名）：只写"1..N 里缺哪几号"；③ 不可证完整：绝不给"看起来齐了"。
/// 每一片都必须有卷号/名字槽才可能 Complete（真机 `2` 组的教训）。</para>
///
/// <para><b>生命周期</b>：纯静态、无状态；每次调用都是"一批片事实 + 交接单 → 一个候选结论"，
/// 不缓存、不写盘、不改名/不删片。</para>
/// </summary>
internal static class VolumeCandidateVerdict
{
    /// <summary>
    /// 对**一组已经收好的片**定序 + 判完整性，产出一个候选结论。
    /// ⛔ 排不出顺序或连不成 1..N ⇒ 判不出（绝不猜缺哪几片）。
    /// </summary>
    public static VolumeCandidateDiscovery.VolumeCandidate Evaluate(
        string baseName,
        List<string> pieces,
        IReadOnlyDictionary<string, ArchiveFixer.Detection.VolumeHandoffEntry>? handoffByPath)
    {
        /*
         * 普通媒体内容物不是分卷组的一片（用户 2026-11-11：媒体前缀只服务单一完整包）。
         * 先按内容事实剔掉，再谈几何；⛔ 不靠后缀/名字猜。
         */
        var allItems = pieces
            .Select(path => VolumeCandidateEvidence.BuildFacts(path, handoffByPath))
            .ToList();

        int contentDropped = allItems.RemoveAll(item => VolumeGroupMembership.IsKnownContent(item));
        List<VolumeCandidatePieceFacts> items = allItems;

        ArchiveFixer.Detection.VolumeContentFormat format = items
            .Select(item => item.Format)
            .FirstOrDefault(value => value != ArchiveFixer.Detection.VolumeContentFormat.Unknown);

        int declaredPieceCount = items.Select(item => item.DeclaredPieceCount).DefaultIfEmpty(0).Max();
        long declaredTotalBytes = items.Select(item => item.DeclaredTotalBytes).DefaultIfEmpty(0).Max();

        /*
         * 几何过滤必须发生在**首片事实已知之后、字节求和之前**：
         * 336 真机就是 PNG（53,418 字节）被算进已收字节，组报 636,790,140 > 自述 636,736,722
         * ⇒ SizeAnomaly。这里只保留"有卷号/名字槽/首片/ZIP split 等身份证据"的片；
         * 完全没有身份证据的候选，只有其字节和正好补齐自述整包时才留下（唯一的尾片那一档）。
         */
        (items, int geometryDropped) = VolumeGroupMembership.FilterPiecesByGeometry(items, declaredTotalBytes);

        long receivedBytes = items.Sum(item => item.Size);

        List<VolumeFacts> publicFacts = items.Select(VolumeCandidateEvidence.ToPublicFact).ToList();
        List<ArchiveFixer.Detection.FileNatureCard> cards = VolumeCandidateEvidence.CardsOf(items);

        int dropped = contentDropped + geometryDropped;

        /*
         * ⛔ 2026-11-01：单卷完整包（父包 / 普通单卷 zip）不是分卷组。
         * 没有名字卷槽、内容自证 SingleComplete + 可证非成员 ⇒ 直判 Unknown，
         * 绝不再给"名字卷标记连成 1..1"这种假 Complete。
         */
        if (items.Count == 1 && VolumeGroupMembership.IsProvablySingleCompleteNonMember(items[0]))
        {
            return VolumeCandidateEvidence.Finish(new VolumeCandidateDiscovery.VolumeCandidate
            {
                BaseName = baseName,
                Format = format,
                Pieces = new[] { items[0].Path },
                EntryPath = items[0].Path,
                DeclaredPieceCount = declaredPieceCount,
                ReceivedPieceCount = 1,
                DeclaredTotalBytes = declaredTotalBytes,
                ReceivedBytes = receivedBytes,
                Evidence = "内容自证是完整单卷（可证不是分卷成员）⇒ 不是分卷组"
            }, publicFacts, cards, dropped);
        }

        /*
         * ===== ⓪ 跨盘 ZIP 先按名字定序（用户 2026-10-11 口径：那一族"头尾在名字编号里是反的"）=====
         * `x.z01/.z02/……/x.zip`：名字里 `.zip` 是末片、`.z01` 才是第 1 片；
         * 而内容的 EOCD 盘号只有 `.zip` 那个末片带得出来（`.z01/.z02` 读不出）。
         * ⇒ 这一族**只按名字**定序（`z01`→1、`z02`→2、`.zip`→N），内容卷号不参与，
         *   否则两套编号混在一起会得出"缺 1"这种假缺卷（真机 `10101010` 的 `222-L1`）。
         */
        if (VolumeGroupPieceOrdering.TryOrderSpannedZip(
                items,
                out List<VolumeCandidatePieceFacts> spannedOrder,
                out List<int> spannedNumbers))
        {
            return VolumeCandidateEvidence.Finish(
                Judge(
                    baseName,
                    format,
                    spannedOrder,
                    spannedNumbers,
                    "跨盘 ZIP 名字定序",
                    declaredPieceCount,
                    declaredTotalBytes,
                    receivedBytes),
                publicFacts,
                cards,
                dropped);
        }

        // ① 内容卷号（能读出来的最有分量；交接单里的卷号也算"内容卷号"）。
        List<int> contentNumbers = items
            .Select(item => item.VolumeNumber)
            .Where(number => number.HasValue)
            .Select(number => number!.Value)
            .Distinct()
            .OrderBy(number => number)
            .ToList();

        if (contentNumbers.Count > 0)
        {
            /*
             * 内容卷号优先，但**内容读不出的那几片**用名字卷标记补齐：
             * 跨盘 ZIP 只有末片 `.zip` 的 EOCD 带盘号，`.z01/.z02` 内容判不出 ⇒
             * 只看内容会误判"缺 1,2"（真机 10101010 的 `222-L1` 就是这一档）。
             * ⛔ 不把名字当主判据：内容能读出来的号一个都不改，只补"读不出"的那些片。
             */
            var merged = new SortedSet<int>(contentNumbers);

            foreach (VolumeCandidatePieceFacts item in items)
            {
                if (item.VolumeNumber.HasValue)
                {
                    continue;
                }

                int? nameIndex = LegacyVolumeNames.TryGetIndex(
                    Path.GetFileName(item.Path));

                if (nameIndex.HasValue)
                {
                    merged.Add(nameIndex.Value);
                }
            }

            string source = merged.Count > contentNumbers.Count ? "内容卷号+名字补缺" : "内容卷号";

            return VolumeCandidateEvidence.Finish(
                Judge(
                    baseName,
                    format,
                    items,
                    merged.ToList(),
                    source,
                    declaredPieceCount,
                    declaredTotalBytes,
                    receivedBytes),
                publicFacts,
                cards,
                dropped);
        }

        // ② 名字里的卷标记（7z 中间/末片、ZIP 裸切中间片在内容上判不出，名字常常还读得出来）。
        List<int> nameNumbers = items
            .Select(item => LegacyVolumeNames.TryGetIndex(
                Path.GetFileName(item.Path)))
            .Where(number => number.HasValue)
            .Select(number => number!.Value)
            .Distinct()
            .OrderBy(number => number)
            .ToList();

        if (nameNumbers.Count > 0)
        {
            return VolumeCandidateEvidence.Finish(
                Judge(
                    baseName,
                    format,
                    items,
                    nameNumbers,
                    "名字卷标记",
                    declaredPieceCount,
                    declaredTotalBytes,
                    receivedBytes),
                publicFacts,
                cards,
                dropped);
        }

        /*
         * ③ **7z 的自述整包对账**（用户 2026-10-24 口径：自述片数/整包字节数随交接单过来）：
         * 7z 中间片的内容里没有卷号，但第 1 片的起始头写着"整包应该有多少字节"。
         * ⇒ 已收字节合计 == 自述整包字节数 ⇒ 可证完整；小于 ⇒ 缺片（卷号读不出 ⇒ ⛔ 不点名缺哪几片）。
         * ⛔ 大于 ⇒ 判不出（多出来的片不该算进这一组）。
         */
        if (declaredTotalBytes > 0)
        {
            bool complete = receivedBytes == declaredTotalBytes;

            return VolumeCandidateEvidence.Finish(new VolumeCandidateDiscovery.VolumeCandidate
            {
                BaseName = baseName,
                Format = format,
                Pieces = VolumeGroupPieceOrdering.OrderByEntryThenVolumeIdentity(
                    items,
                    items.FirstOrDefault(item => item.IsFirstPiece)?.Path),
                EntryPath = items.FirstOrDefault(item => item.IsFirstPiece)?.Path ?? string.Empty,
                MissingNumbers = Array.Empty<int>(),
                DeclaredPieceCount = declaredPieceCount,
                ReceivedPieceCount = items.Count,
                DeclaredTotalBytes = declaredTotalBytes,
                ReceivedBytes = receivedBytes,
                Evidence = complete
                    ? "自述整包对账：已收字节合计 == 自述整包字节数（入口 = 第 1 片）"
                    : receivedBytes < declaredTotalBytes
                        ? $"自述整包 {declaredTotalBytes} 字节，只收到 {receivedBytes} 字节 ⇒ 缺片"
                        : $"已收 {receivedBytes} 字节 > 自述整包 {declaredTotalBytes} 字节 ⇒ 判不出（不猜）"
            }, publicFacts, cards, dropped);
        }

        /*
         * ④ 都判不出 ⇒ **只按大小定序**（除末片外等大、末片更小或相等）。
         *
         * ⚠ 这一档能回答"谁可能是末片"，但回答不了"这一组该有几片"（N 只能由入口包自述的整包字节数
         * 给出，那一条属于分卷专属算法）⇒ 结论只能落"判不出"，⛔ 不猜缺卷。
         */
        List<VolumeCandidatePieceFacts> bySize = items
            .OrderBy(item => item.Size)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool regularSizes = bySize.Count >= 2
            && bySize.Take(bySize.Count - 1).All(item => item.Size == bySize[0].Size)
            && bySize[^1].Size <= bySize[0].Size;

        return VolumeCandidateEvidence.Finish(new VolumeCandidateDiscovery.VolumeCandidate
        {
            BaseName = baseName,
            Format = format,
            Pieces = bySize.Select(item => item.Path).ToList(),
            EntryPath = string.Empty,
            DeclaredPieceCount = declaredPieceCount,
            ReceivedPieceCount = items.Count,
            DeclaredTotalBytes = declaredTotalBytes,
            ReceivedBytes = receivedBytes,
            Evidence = regularSizes
                ? "卷号判不出：只能按大小定序（除末片外等大），⛔ 不猜缺哪几片"
                : "卷号判不出、大小也不规律：只给候选清单，⛔ 什么都不做"
        }, publicFacts, cards, dropped);
    }

    /// <summary>用一组卷号判完整性（连成 1..N ⇒ 完整；1..max 有洞 ⇒ 缺卷）。</summary>
    private static VolumeCandidateDiscovery.VolumeCandidate Judge(
        string baseName,
        ArchiveFixer.Detection.VolumeContentFormat format,
        List<VolumeCandidatePieceFacts> items,
        List<int> numbers,
        string evidenceSource,
        int declaredPieceCount,
        long declaredTotalBytes,
        long receivedBytes)
    {
        List<VolumeCandidatePieceFacts> ordered = VolumeGroupPieceOrdering.OrderForJudging(format, items);

        /*
         * 完整性的 N 取「名字/内容卷号的最大值」与「入口包自述片数」里的较大者：
         * ⛔ 只要有自述片数，就不许把"只找到 1 片、名字恰好是 1"当成 1..1 完整
         * （真机 V03：只看到 `组.7z.001`、自述 4 片，老判据会判 Complete ⇒ 第二大步白跑一轮）。
         */
        int expected = format == ArchiveFixer.Detection.VolumeContentFormat.SevenZip
                       && declaredTotalBytes > 0
                       && declaredTotalBytes == receivedBytes
            ? Math.Max(numbers[^1], 1)
            : Math.Max(numbers[^1], declaredPieceCount);

        bool contiguous = numbers[0] == 1
            && numbers.Count == expected
            && numbers.SequenceEqual(Enumerable.Range(1, expected));

        List<int> missing = contiguous
            ? new List<int>()
            : Enumerable.Range(1, expected).Except(numbers).ToList();

        string declaredNote = declaredPieceCount > numbers[^1]
            ? $"（自述 {declaredPieceCount} 片）"
            : string.Empty;

        string entry = VolumeGroupPieceOrdering.FindEntry(items);

        /*
         * ⛔ 2026-11-01：**每一片都必须有卷号/名字槽**才可能 Complete。
         * 真机 `2` 组：4 片里只有 `.part1/.part2` 有槽（另外两片没有槽），
         * 旧判据 `numbers.Count == expected` 只数有槽的片 ⇒ 判成 Complete，
         * 与建组那边的 `RarPart 判不出` 直接打架，回收也随之卡死。
         */
        if (numbers.Count != items.Count)
        {
            return new VolumeCandidateDiscovery.VolumeCandidate
            {
                BaseName = baseName,
                Format = format,
                Pieces = ordered.Select(item => item.Path).ToList(),
                EntryPath = entry,
                DeclaredPieceCount = declaredPieceCount,
                ReceivedPieceCount = items.Count,
                DeclaredTotalBytes = declaredTotalBytes,
                ReceivedBytes = receivedBytes,
                Evidence = $"{items.Count - numbers.Count} 片没有卷号/名字槽"
                    + $"（有槽 {string.Join(",", numbers)}）⇒ 判不出，不能证明它们是本组的一片"
            };
        }

        /*
         * 7z 的中间片内容里没有卷号；首卷自述片数/整包字节与实收对不上时，
         * 只能证明“自述与实收不一致”，不能把自述片数减实收片数写成缺卷清单。
         * 这一档在候选层也必须是 Unknown；最终完整性仍由 SevenZipFamilyAlgorithm 回答。
         */
        if (format == ArchiveFixer.Detection.VolumeContentFormat.SevenZip
            && (declaredPieceCount > 0
                && (items.Count != declaredPieceCount
                    || (declaredTotalBytes > 0 && receivedBytes != declaredTotalBytes))))
        {
            return new VolumeCandidateDiscovery.VolumeCandidate
            {
                BaseName = baseName,
                Format = format,
                Pieces = ordered.Select(item => item.Path).ToList(),
                EntryPath = entry,
                MissingNumbers = Array.Empty<int>(),
                DeclaredPieceCount = declaredPieceCount,
                ReceivedPieceCount = items.Count,
                DeclaredTotalBytes = declaredTotalBytes,
                ReceivedBytes = receivedBytes,
                Evidence = $"{evidenceSource}自述 {declaredPieceCount} 片 / 整包 {declaredTotalBytes} 字节，"
                    + $"实收 {items.Count} 片 / {receivedBytes} 字节 ⇒ 判不出，不能证明具体少哪几片"
            };
        }

        return new VolumeCandidateDiscovery.VolumeCandidate
        {
            BaseName = baseName,
            Format = format,
            Pieces = ordered.Select(item => item.Path).ToList(),
            EntryPath = entry,
            MissingNumbers = missing,
            DeclaredPieceCount = declaredPieceCount,
            ReceivedPieceCount = items.Count,
            DeclaredTotalBytes = declaredTotalBytes,
            ReceivedBytes = receivedBytes,
            Evidence = contiguous
                ? $"{evidenceSource}连成 1..{expected}{declaredNote}"
                : $"{evidenceSource}只找到 {string.Join(",", numbers)}（1..{expected} 里缺 {string.Join(",", missing)}）{declaredNote}"
        };
    }
}

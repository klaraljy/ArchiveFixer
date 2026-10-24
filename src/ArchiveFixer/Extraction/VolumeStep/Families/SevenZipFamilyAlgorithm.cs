using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **7z 分卷族算法**（第二大步 P4，当前真实链条的总闸门）。
    ///
    /// <para>证据：只有**首卷**的起始头自述整包字节数与片数（`VolumeFirstPiece` ⇒
    /// `DeclaredTotalBytes` / `DeclaredVolumeCount`）；其余片内容里没有身份信息。</para>
    ///
    /// <para>判定：片数 == 自述片数，且**已收字节和 == 自述整包字节数**（两者都对上才算完整）。
    /// 槽位：首卷 = 1；其余先按名字序号；名字读不出的，只允许“剩一个槽位”或
    /// “剩两个槽位且能按大小分清末片（严格更小）”时补位，否则 Unknown。
    /// 规范名 `stem.7z.001..00N`，入口 = 首卷。</para>
    /// </summary>
    public sealed class SevenZipFamilyAlgorithm : IVolumeFamilyAlgorithm
    {
        public VolumeNamingFamily Family => VolumeNamingFamily.SevenZipNumbered;

        public FamilyResult Evaluate(VolumeGroupUnit unit)
        {
            if (unit == null || unit.Pieces.Count == 0)
            {
                return Unknown("7z：没有片事实 ⇒ 判不出");
            }

            List<GroupPieceFact> firsts = unit.Pieces
                .Where(piece => piece.BodyKind == ArchiveBodyKind.VolumeFirstPiece)
                .ToList();

            if (firsts.Count != 1)
            {
                return Unknown($"7z：首卷自述的证据有 {firsts.Count} 个（应恰好 1 个）⇒ 判不出");
            }

            GroupPieceFact first = firsts[0];
            int total = first.DeclaredVolumeCount;

            if (total < 2)
            {
                return Unknown($"7z：首卷没自述片数（DeclaredVolumeCount={total}）⇒ 判不出");
            }

            if (first.DeclaredTotalBytes <= 0)
            {
                return Unknown("7z：首卷没自述整包字节数 ⇒ 判不出");
            }

            var slots = new Dictionary<int, GroupPieceFact> { [1] = first };
            var unassigned = new List<GroupPieceFact>();

            foreach (GroupPieceFact piece in unit.Pieces)
            {
                if (ReferenceEquals(piece, first))
                {
                    continue;
                }

                int? index = VolumeGroupDetector.TryGetVolumeIndex(Path.GetFileName(piece.Path));

                if (index == null)
                {
                    unassigned.Add(piece);
                    continue;
                }

                if (index.Value < 2 || index.Value > total || slots.ContainsKey(index.Value))
                {
                    return Unknown($"7z：名字序号越界/重复（{Path.GetFileName(piece.Path)}）⇒ 判不出");
                }

                slots[index.Value] = piece;
            }

            if (unit.Pieces.Count > total)
            {
                return Unknown($"7z：已收 {unit.Pieces.Count} 片，比首卷自述的 {total} 片还多 ⇒ 判不出");
            }

            List<int> missingSlots = Enumerable.Range(1, total).Except(slots.Keys).OrderBy(index => index).ToList();

            if (missingSlots.Count != unassigned.Count)
            {
                return new FamilyResult(
                    GroupAnalysis.GroupAnalysisVerdict.Missing,
                    slots.OrderBy(pair => pair.Key).Select(pair => pair.Value.Path).ToList(),
                    missingSlots,
                    null,
                    $"7z 首卷自述 {total} 片；已认出 {slots.Count - 1 + unassigned.Count} 片（未定槽 {unassigned.Count} 片）");
            }

            if (unassigned.Count == 1)
            {
                slots[missingSlots[0]] = unassigned[0];
            }
            else if (unassigned.Count == 2
                && missingSlots.Contains(total)
                && missingSlots.Count == 2)
            {
                /*
                 * 7z -v 切出来的除末片外等大、末片更小。只剩两个槽位且其中一个必是末片时，
                 * 只有“严格更小的那一份是末片”这一条能定序；两份一样大 ⇒ 顺序不明，不猜。
                 */
                List<GroupPieceFact> bySize = unassigned.OrderBy(piece => piece.Size).ToList();

                if (bySize[0].Size < bySize[1].Size)
                {
                    slots[total] = bySize[0];
                    slots[missingSlots.First(slot => slot != total)] = bySize[1];
                }
                else
                {
                    return Unknown("7z：最后两片大小一样，末片与中间片分不出 ⇒ 判不出");
                }
            }
            else if (unassigned.Count > 0)
            {
                return Unknown($"7z：有 {unassigned.Count} 片名字读不出、剩 {missingSlots.Count} 个槽位，顺序不明 ⇒ 判不出");
            }

            long receivedBytes = unit.Pieces.Sum(piece => piece.Size);

            if (receivedBytes < first.DeclaredTotalBytes)
            {
                return new FamilyResult(
                    GroupAnalysis.GroupAnalysisVerdict.Missing,
                    slots.OrderBy(pair => pair.Key).Select(pair => pair.Value.Path).ToList(),
                    Array.Empty<int>(),
                    null,
                    $"7z 首卷自述整包 {first.DeclaredTotalBytes} 字节，只收到 {receivedBytes} 字节 ⇒ 缺片");
            }

            if (receivedBytes > first.DeclaredTotalBytes)
            {
                return Unknown(
                    $"7z 已收 {receivedBytes} 字节 > 自述整包 {first.DeclaredTotalBytes} 字节 ⇒ 判不出");
            }

            string stem = unit.BaseName;

            if (string.IsNullOrWhiteSpace(stem))
            {
                return Unknown("7z：基名判不出 ⇒ 出不了规范名计划");
            }

            IReadOnlyList<string> targets = VolumeNumberFromContent.BuildStandardNames(
                stem,
                VolumeNamingFamily.SevenZipNumbered,
                total);

            if (targets.Count != total)
            {
                return Unknown("7z：规范名生成数量不符 ⇒ 判不出");
            }

            var items = new List<CanonicalNamePlanItem>();

            for (int index = 1; index <= total; index++)
            {
                GroupPieceFact piece = slots[index];
                items.Add(BuildItem(piece.Path, targets[index - 1], isEntry: index == 1, unit.SourceLayer));
            }

            return new FamilyResult(
                GroupAnalysis.GroupAnalysisVerdict.Complete,
                items.Select(item => item.PiecePath).ToList(),
                Array.Empty<int>(),
                new CanonicalNamePlan
                {
                    Items = items,
                    Evidence = $"7z 首卷自述 {total} 片 / {first.DeclaredTotalBytes} 字节；已收字节和与自述整包相等"
                },
                $"7z 分卷连成 1..{total}（入口 = 首卷）");
        }

        private static CanonicalNamePlanItem BuildItem(
            string piecePath,
            string targetName,
            bool isEntry,
            string sourceLayer)
        {
            string targetPath = Path.Combine(sourceLayer, targetName);
            string conflict = string.Empty;

            if (!string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(piecePath), StringComparison.OrdinalIgnoreCase)
                && File.Exists(targetPath))
            {
                conflict = "目标名已被别的文件占用";
            }

            return new CanonicalNamePlanItem(piecePath, targetName, isEntry, conflict);
        }

        private static FamilyResult Unknown(string evidence) =>
            new(
                GroupAnalysis.GroupAnalysisVerdict.Unknown,
                Array.Empty<string>(),
                Array.Empty<int>(),
                null,
                evidence);
    }
}

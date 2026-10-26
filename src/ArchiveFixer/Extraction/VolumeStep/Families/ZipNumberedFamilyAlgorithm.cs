using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **ZIP 通用分片族算法**（`.zip.001..00N` 裸切，第二大步 P3）。
    ///
    /// <para>内容证据：只有**末片**尾部有 EOCD 且中央目录起在更早的片里（`cdOffset ≥ 本文件长度`）；
    /// **首片**开头是 zip 本地头 `PK\x03\x04`；中间片是裸字节流，内容里没有卷号。</para>
    ///
    /// <para>因此：① 3 片时可以纯内容定序（本地头=1、EOCD=末、剩下=2）；
    /// ② 超过 3 片时，先看名字序号能不能把每一片都点出来（连续 1..N、首片=1、末片=N）；
    /// 名字也点不出 ⇒ 本版如实 Unknown（试开 tie-breaker 是下一小步，⛔ 不在这里猜）。</para>
    ///
    /// <para>规范名：`stem.zip.001..00N`（入口 = 第 1 片）。</para>
    /// </summary>
    public sealed class ZipNumberedFamilyAlgorithm : IVolumeFamilyAlgorithm
    {
        public VolumeNamingFamily Family => VolumeNamingFamily.ZipNumbered;

        public FamilyResult Evaluate(VolumeGroupUnit unit)
        {
            if (unit == null || unit.Pieces.Count < 3)
            {
                return Unknown("ZIP 通用分片：片数少于 3 ⇒ 判不出");
            }

            List<GroupPieceFact> ends = unit.Pieces
                .Where(piece => piece.HasZipEndRecord && piece.ZipEndRecordMeansSplit)
                .ToList();

            if (ends.Count != 1)
            {
                return Unknown($"ZIP 通用分片：带 EOCD 的末片有 {ends.Count} 个（应恰好 1 个）⇒ 判不出");
            }

            GroupPieceFact end = ends[0];

            List<GroupPieceFact> firsts = unit.Pieces
                .Where(piece => piece.HasZipLocalHeader && !ReferenceEquals(piece, end))
                .ToList();

            if (firsts.Count > 1)
            {
                return Unknown($"ZIP 通用分片：带本地头的首片有 {firsts.Count} 个 ⇒ 判不出");
            }

            int total = unit.Pieces.Count;
            List<GroupPieceFact> ordered;

            if (unit.ConfirmedOrder is { Count: > 0 } confirmedOrder
                && confirmedOrder.Count == total)
            {
                var byPath = unit.Pieces.ToDictionary(
                    piece => Path.GetFullPath(piece.Path),
                    piece => piece,
                    StringComparer.OrdinalIgnoreCase);

                if (confirmedOrder
                        .Select(Path.GetFullPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count() != total
                    || confirmedOrder.Any(path => !byPath.ContainsKey(Path.GetFullPath(path))))
                {
                    return Unknown("ZIP 通用分片：试开确认顺序与片清单对不上 ⇒ 判不出");
                }

                ordered = confirmedOrder.Select(path => byPath[Path.GetFullPath(path)]).ToList();

                if (firsts.Count != 1
                    || !ReferenceEquals(ordered[0], firsts[0])
                    || !ReferenceEquals(ordered[^1], end))
                {
                    return Unknown("ZIP 通用分片：试开确认顺序与“首片本地头 / 末片 EOCD”对不上 ⇒ 判不出");
                }
            }
            else if (total == 3)
            {
                if (firsts.Count != 1)
                {
                    return Unknown("ZIP 通用分片：3 片时缺“本地头首片”这条内容证据 ⇒ 判不出");
                }

                GroupPieceFact first = firsts[0];
                GroupPieceFact middle = unit.Pieces
                    .First(piece => !ReferenceEquals(piece, first) && !ReferenceEquals(piece, end));

                ordered = new List<GroupPieceFact> { first, middle, end };
            }
            else
            {
                /*
                 * >3 片：中间片内容没有卷号 ⇒ 先用名字序号把每一片点出来；
                 * 必须连续 1..N、首片=1、末片=N，否则 Unknown（试开 tie-breaker 后续再接）。
                 */
                if (firsts.Count != 1)
                {
                    return Unknown($"ZIP 通用分片：{total} 片时“本地头首片”证据有 {firsts.Count} 个 ⇒ 判不出");
                }

                var byNumber = new Dictionary<int, GroupPieceFact>();

                foreach (GroupPieceFact piece in unit.Pieces)
                {
                    int? index = VolumeGroupDetector.TryGetVolumeIndex(Path.GetFileName(piece.Path));

                    if (index == null || index.Value < 1 || index.Value > total || byNumber.ContainsKey(index.Value))
                    {
                        return Unknown(
                            $"ZIP 通用分片：{total} 片但名字序号点不全（{Path.GetFileName(piece.Path)}）⇒ 判不出");
                    }

                    byNumber[index.Value] = piece;
                }

                ordered = Enumerable.Range(1, total).Select(index => byNumber[index]).ToList();

                if (!ReferenceEquals(ordered[0], firsts[0]) || !ReferenceEquals(ordered[^1], end))
                {
                    return Unknown("ZIP 通用分片：名字序号与“首片本地头 / 末片 EOCD”对不上 ⇒ 判不出");
                }
            }

            string stem = unit.BaseName;

            if (string.IsNullOrWhiteSpace(stem))
            {
                return Unknown("ZIP 通用分片：基名判不出 ⇒ 出不了规范名计划");
            }

            IReadOnlyList<string> targets = VolumeNumberFromContent.BuildStandardNames(
                stem,
                VolumeNamingFamily.ZipNumbered,
                total);

            if (targets.Count != total)
            {
                return Unknown("ZIP 通用分片：规范名生成数量不符 ⇒ 判不出");
            }

            var items = new List<CanonicalNamePlanItem>();

            for (int index = 0; index < total; index++)
            {
                items.Add(BuildItem(ordered[index].Path, targets[index], isEntry: index == 0, unit.SourceLayer));
            }

            return new FamilyResult(
                GroupAnalysis.GroupAnalysisVerdict.Complete,
                ordered.Select(piece => piece.Path).ToList(),
                Array.Empty<int>(),
                new CanonicalNamePlan
                {
                    Items = items,
                    Evidence = total == 3
                        ? "ZIP 通用分片：3 片纯内容定序（本地头=1、EOCD=末、剩下=2）"
                        : $"ZIP 通用分片：{total} 片名字序号连成 1..{total}（首片本地头 / 末片 EOCD 已核对）"
                },
                $"ZIP 通用分片连成 1..{total}（入口 = 第 1 片）");
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

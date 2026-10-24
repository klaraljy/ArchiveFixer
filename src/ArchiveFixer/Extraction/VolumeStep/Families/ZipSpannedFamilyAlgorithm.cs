using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **跨盘 ZIP 族算法**（第二大步 P2）。
    ///
    /// <para>证据（用户口径：ZIP 第二简单）：末片 `X.zip` 的 EOCD 盘号 `d`（0 起）⇒ 总片数 `B = d+1`；
    /// 名字 `X.z01 … X.z(B-1)` + 末片 `X.zip`（⛔ `.zip` 是末片，不是第 1 片）。</para>
    ///
    /// <para>判定：B 必须由末片内容给出（判不出 ⇒ Unknown，⛔ 不拿名字硬凑）；中间片先按名字卷标记
    /// （容忍垃圾尾巴，`VolumeGroupDetector.TryGetVolumeIndex`）；名字读不出的中间片**只允许在
    /// “剩余片数 == 剩余槽位数”时用消去法补位**（B 已知、一份对一个），否则 Unknown。
    /// 规范名 = `stem.z01…stem.zip`，入口是末片 `.zip`（引擎只认它）。</para>
    /// </summary>
    public sealed class ZipSpannedFamilyAlgorithm : IVolumeFamilyAlgorithm
    {
        private static readonly Regex ZipName = new(@"\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /*
         * 跨盘 ZIP 的名字序号**就是磁盘序号**：`.z01`→1、`.z02`→2（`.zip` 是末片 = B）。
         * ⛔ 不用 `VolumeGroupDetector.TryGetVolumeIndex`：它按“单卷本体 = 第 1 卷”的口径，
         * 会把 `.zip` 算成 1、`.z01` 算成 2 —— 那一套与跨盘族相反，混用会造出假缺卷。
         * 这里容忍垃圾尾巴（`.i_$z01` 也算 1），只认“末尾 z+两位数字”。
         */
        private static readonly Regex TolerantZIndex = new(
            @"z(?<index>\d{2})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public VolumeNamingFamily Family => VolumeNamingFamily.ZipSpanned;

        public FamilyResult Evaluate(VolumeGroupUnit unit)
        {
            if (unit == null || unit.Pieces.Count < 2)
            {
                return Unknown("跨盘 ZIP：片数不足 ⇒ 判不出");
            }

            GroupPieceFact? zipPiece = null;
            var named = new List<(GroupPieceFact Fact, int Index)>();
            var unnamed = new List<GroupPieceFact>();

            foreach (GroupPieceFact piece in unit.Pieces)
            {
                string name = Path.GetFileName(piece.Path);

                /*
                 * 第 1 片的内容证据：开头有本地文件头（允许先出现 `PK\x07\x08` 跨盘标记）。
                 * 它没有 EOCD、名字也被包坏时，靠这一条占住槽 1；⛔ 不靠名字猜。
                 */
                if (piece.HasZipLocalHeader)
                {
                    named.Add((piece, 1));
                    continue;
                }

                bool contentSaysLast =
                    piece.VolumeNumber is int contentNumber
                    && contentNumber >= 2
                    && piece.DeclaredVolumeCount == contentNumber;

                if (ZipName.IsMatch(name) || contentSaysLast)
                {
                    if (zipPiece != null)
                    {
                        return Unknown("跨盘 ZIP：出现两个 `.zip` 末片 ⇒ 判不出");
                    }

                    zipPiece = piece;
                    continue;
                }

                Match part = TolerantZIndex.Match(name);

                if (part.Success
                    && int.TryParse(part.Groups["index"].Value, out int namedIndex))
                {
                    named.Add((piece, namedIndex));
                }
                else
                {
                    unnamed.Add(piece);
                }
            }

            if (zipPiece == null)
            {
                return Unknown("跨盘 ZIP：没有 `.zip` 末片 ⇒ 判不出");
            }

            int? contentTotal = zipPiece.VolumeNumber;

            if (contentTotal == null || contentTotal.Value < 2)
            {
                return Unknown($"跨盘 ZIP：末片盘号判不出（{zipPiece.ContentFail}）⇒ 不拿名字硬凑，判不出");
            }

            int total = contentTotal.Value;

            List<int> indices = named
                .Select(item => item.Index)
                .OrderBy(index => index)
                .ToList();

            if (indices.GroupBy(index => index).Any(group => group.Count() > 1))
            {
                return Unknown("跨盘 ZIP：卷标记有重复 ⇒ 判不出");
            }

            if (indices.Count > 0 && (indices[0] < 1 || indices[^1] > total - 1))
            {
                return Unknown("跨盘 ZIP：卷标记越界 ⇒ 判不出");
            }

            List<int> missing = Enumerable.Range(1, total - 1).Except(indices).ToList();

            /*
             * 名字读不出的中间片：**只允许“剩余片数 == 剩余槽位数”时用消去法补位**
             * （B 已知、一份对一个；⛔ 多片对多槽 = 顺序不明，不猜）。
             */
            if (unnamed.Count > 0)
            {
                if (missing.Count != unnamed.Count)
                {
                    return new FamilyResult(
                        GroupAnalysis.GroupAnalysisVerdict.Missing,
                        named.OrderBy(item => item.Index).Select(item => item.Fact.Path)
                            .Append(zipPiece.Path)
                            .ToList(),
                        missing,
                        null,
                        $"跨盘 ZIP 末片盘号说 {total} 片；已认出 {indices.Count} 个卷标记、{unnamed.Count} 片无名，对不上（缺 {string.Join(",", missing)}）");
                }

                if (unnamed.Count > 1)
                {
                    return Unknown(
                        $"跨盘 ZIP：有 {unnamed.Count} 片读不出卷标记、剩 {missing.Count} 个槽位，顺序不明 ⇒ 判不出");
                }

                named.Add((unnamed[0], missing[0]));
                missing = new List<int>();
                unnamed.Clear();
            }

            if (missing.Count > 0)
            {
                return new FamilyResult(
                    GroupAnalysis.GroupAnalysisVerdict.Missing,
                    named.OrderBy(item => item.Index).Select(item => item.Fact.Path)
                        .Append(zipPiece.Path)
                        .ToList(),
                    missing,
                    null,
                    $"跨盘 ZIP 末片盘号说 {total} 片；卷标记只找到 {string.Join(",", indices)}（缺 {string.Join(",", missing)}）");
            }

            string stem = ZipName.Replace(Path.GetFileName(zipPiece.Path), string.Empty);
            var planItems = new List<CanonicalNamePlanItem>();

            foreach ((GroupPieceFact fact, int index) in named.OrderBy(item => item.Index))
            {
                string targetName = stem + ".z" + index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
                planItems.Add(BuildItem(fact.Path, targetName, isEntry: false, unit.SourceLayer));
            }

            planItems.Add(BuildItem(zipPiece.Path, stem + ".zip", isEntry: true, unit.SourceLayer));

            return new FamilyResult(
                GroupAnalysis.GroupAnalysisVerdict.Complete,
                planItems.Select(item => item.PiecePath).ToList(),
                Array.Empty<int>(),
                new CanonicalNamePlan
                {
                    Items = planItems,
                    Evidence = $"跨盘 ZIP 末片盘号 = {total - 1} ⇒ 总片数 {total}；名字 .z01..z{total - 1:D2} + .zip"
                },
                $"跨盘 ZIP 内容盘号连成 1..{total}（入口 = 末片 .zip）");
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **RAR 族专属算法**（2026-10-24 重建，P1）。
    ///
    /// <para>证据：RAR5 / RAR4 新编号 / RAR4 老编号的主头都能给卷号（或首卷锚点 + 相对卷号差），
    /// ⇒ 槽位直接来自内容；数量 = 最大卷号；连成 1..N 才算完整。**不用试开**。</para>
    ///
    /// <para>规范名：新编号 `基名.partN.rar`（保留入口前缀/宽度）；老编号 `基名.rar + .r00/.r01…`。
    /// 入口名不动；只给其余片出“链接成规范名”的计划，⛔ 不落盘、不覆盖。</para>
    /// </summary>
    public sealed class RarFamilyAlgorithm : IVolumeFamilyAlgorithm
    {
        private static readonly Regex PartPattern = new(
            @"^(?<prefix>.*?part)(?<num>\d+)(?<suffix>\..+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex LegacyPattern = new(
            @"^(?<stem>.*?)(?<ext>\.rar|\.r\d{2})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /*
         * 脏卷标记：`pa删除rt1.rar` 这类“part 中间被塞了字”的名字，正则找不到连续的 `part`。
         * 引擎按**它眼前那个名字**找兄弟卷（真机 1111：`aaa-L2.pa删除rt1.rar` ⇒ 要 `…pa删除rt2.rar`）
         * ⇒ 规范名必须保留同一个前缀，只换末尾那个卷号；⛔ 不把前缀“洗干净”。
         */
        private static readonly Regex DirtyPartPattern = new(
            @"^(?<prefix>.*?)(?<num>\d+)(?<suffix>\.[^.]+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public VolumeNamingFamily Family => VolumeNamingFamily.RarPart;

        public FamilyResult Evaluate(VolumeGroupUnit unit)
        {
            if (unit == null || unit.Pieces.Count == 0)
            {
                return new FamilyResult(
                    GroupAnalysis.GroupAnalysisVerdict.Unknown,
                    Array.Empty<string>(),
                    Array.Empty<int>(),
                    null,
                    "RAR：没有片事实 ⇒ 判不出");
            }

            var numbered = new List<(GroupPieceFact Fact, int Number)>();
            var unnumbered = new List<GroupPieceFact>();

            foreach (GroupPieceFact piece in unit.Pieces)
            {
                int? number = piece.VolumeNumber;

                /*
                 * **内容自证“我是第 1 卷”压过一切名字线索**（真机 21212121：真首卷
                 * `资源222-L2.part1.rar` 的首卷标记被忽略，名字更“像 1”的 `.txt-176` 抢了入口）。
                 * 然后再用名字补“内容读不出卷号”的片；⛔ 两套编号不合流。
                 */
                if (piece.FirstVolumeFlag
                    || piece.BodyKind == ArchiveBodyKind.VolumeFirstPiece)
                {
                    number = 1;
                }

                if (number == null)
                {
                    number = VolumeGroupDetector.TryGetVolumeIndex(Path.GetFileName(piece.Path));
                }

                if (number == null || number.Value < 1)
                {
                    unnumbered.Add(piece);
                }
                else
                {
                    numbered.Add((piece, number.Value));
                }
            }

            if (unnumbered.Count > 0 || numbered.Count == 0)
            {
                return new FamilyResult(
                    GroupAnalysis.GroupAnalysisVerdict.Unknown,
                    Array.Empty<string>(),
                    Array.Empty<int>(),
                    null,
                    $"RAR：{unnumbered.Count} 片卷号判不出（内容/名字都没有）⇒ 判不出，不动");
            }

            if (numbered.GroupBy(item => item.Number).Any(group => group.Count() > 1))
            {
                return new FamilyResult(
                    GroupAnalysis.GroupAnalysisVerdict.Unknown,
                    Array.Empty<string>(),
                    Array.Empty<int>(),
                    null,
                    "RAR：卷号有重复 ⇒ 判不出，不动");
            }

            List<int> numbers = numbered
                .Select(item => item.Number)
                .OrderBy(number => number)
                .ToList();

            int max = numbers[^1];
            bool contiguous = numbers[0] == 1
                && numbers.Count == max
                && numbers.SequenceEqual(Enumerable.Range(1, max));

            List<string> ordered = numbered
                .OrderBy(item => item.Number)
                .Select(item => item.Fact.Path)
                .ToList();

            /*
             * ⛔ 规范名必须按**卷号顺序**映射（第 i 个目标名 → 卷号 i 那一片）。
             * `unit.Pieces` 的顺序是候选扫描顺序，不保证按卷号排；直接拿它映射会把
             * 第 3 卷的链接指到第 1 卷上（真机 10101010：part3 变成了 part1 的硬链接，
             * 引擎随后报 part4 缺失）。
             */
            List<(GroupPieceFact Fact, int Number)> orderedNumbered = numbered
                .OrderBy(item => item.Number)
                .ToList();

            if (!contiguous)
            {
                List<int> missing = Enumerable.Range(1, max).Except(numbers).ToList();

                return new FamilyResult(
                    GroupAnalysis.GroupAnalysisVerdict.Missing,
                    ordered,
                    missing,
                    null,
                    $"RAR 内容卷号只找到 {string.Join(",", numbers)}（1..{max} 里缺 {string.Join(",", missing)}）");
            }

            CanonicalNamePlan plan = BuildNamePlan(unit, orderedNumbered);

            return new FamilyResult(
                GroupAnalysis.GroupAnalysisVerdict.Complete,
                ordered,
                Array.Empty<int>(),
                plan,
                $"RAR 内容卷号连成 1..{max}（入口 = 第 1 卷）");
        }

        /// <summary>
        /// 每片内容卡上的规范名直接当目标名；缺一片、基名不一致、后缀不是 `.rar/.rNN`
        /// 都返回 null，交给后面的按名字兜底。⛔ 不在这里猜后缀。
        /// </summary>
        private static List<string>? TryBuildCanonicalTargets(
            List<(GroupPieceFact Fact, int Number)> numbered)
        {
            if (numbered.Count == 0)
            {
                return null;
            }

            var targets = new List<string>();
            string? baseName = null;

            foreach ((GroupPieceFact fact, _) in numbered)
            {
                string canonical = Path.GetFileName(fact.CanonicalVolumeName ?? string.Empty);

                if (canonical.Length == 0)
                {
                    return null;
                }

                bool isRarName = canonical.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
                    || Regex.IsMatch(canonical, @"(?i)\.r\d{2}$");

                if (!isRarName)
                {
                    return null;
                }

                string factBase = fact.ArchiveBaseName ?? string.Empty;

                if (factBase.Length == 0)
                {
                    return null;
                }

                if (baseName == null)
                {
                    baseName = factBase;
                }
                else if (!string.Equals(baseName, factBase, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                targets.Add(canonical);
            }

            if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
            {
                return null;
            }

            return targets;
        }

        private static CanonicalNamePlan BuildNamePlan(
            VolumeGroupUnit unit,
            List<(GroupPieceFact Fact, int Number)> numbered)
        {
            /*
             * 规范名必须按**族算法认出来的第 1 卷**那条名字来推，⛔ 不能用 unit.EntryPath：
             * 组分析按名字挑入口，可能挑到内容卷号是 2 的伪装片（真机 21212121：
             * 入口被挑成 `.txt`，真首卷是 `…part1.rar`，按 `.txt` 推不出任何规范名）。
             */
            string entryName = Path.GetFileName(
                numbered.FirstOrDefault(item => item.Number == 1).Fact?.Path ?? unit.EntryPath);

            if (string.IsNullOrWhiteSpace(entryName))
            {
                entryName = Path.GetFileName(unit.EntryPath);
            }

            string sourceLayer = unit.SourceLayer;

            List<string>? targets = null;
            string namingEvidence = "RAR 入口名认不出卷标记 ⇒ 不出规范名计划";

            /*
             * **内容卡已经给出的规范名优先**：每片卡上的 CanonicalVolumeName 是
             * `VolumeGroupResolver.TryGetCanonicalVolumeName` 的唯一出口算出来的；
             * 8.15 的根因就是这一层没有把卡上的规范名带进 GroupPieceFact，族算法只能
             * 拿入口名 `9953.part1.a1` 的 `.a1` 后缀去猜。
             */
            targets = TryBuildCanonicalTargets(numbered);

            if (targets != null)
            {
                namingEvidence = "RAR 采用内容卡规范名（按卷号映射）";
            }
            else
            {
                Match part = PartPattern.Match(entryName);

                if (part.Success)
                {
                    string prefix = part.Groups["prefix"].Value;
                    int width = part.Groups["num"].Value.Length;

                    /*
                     * 规范名必须是 RAR 的 `.rar`，不能把入口原来的后缀（`.a1`/`.b2`/`.c3`）
                     * 原样带进每一片。
                     */
                    const string suffix = ".rar";

                    targets = new List<string>();

                    for (int index = 1; index <= numbered.Count; index++)
                    {
                        targets.Add(
                            prefix
                            + index.ToString(
                                "D" + width.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                System.Globalization.CultureInfo.InvariantCulture)
                            + suffix);
                    }

                    namingEvidence = $"RAR 新编号（{prefix}partN.rar，宽度 {width}）";
                }
                else
                {
                    Match dirty = DirtyPartPattern.Match(entryName);

                    if (dirty.Success
                        && dirty.Groups["prefix"].Value.IndexOf("pa", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string dirtyPrefix = dirty.Groups["prefix"].Value;
                        int dirtyWidth = dirty.Groups["num"].Value.Length;

                        targets = new List<string>();

                        for (int index = 1; index <= numbered.Count; index++)
                        {
                            targets.Add(
                                dirtyPrefix
                                + index.ToString(
                                    "D" + dirtyWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                    System.Globalization.CultureInfo.InvariantCulture)
                                + ".rar");
                        }

                        namingEvidence = $"RAR 脏卷标记（保留前缀 {dirtyPrefix}，卷标记归一到 partN.rar）";
                    }
                }
            }

            if (targets == null)
            {
                Match legacy = LegacyPattern.Match(entryName);

                if (legacy.Success)
                {
                    string stem = legacy.Groups["stem"].Value;
                    targets = new List<string> { stem + ".rar" };

                    for (int index = 2; index <= numbered.Count; index++)
                    {
                        targets.Add(stem + ".r" + (index - 2).ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
                    }

                    namingEvidence = $"RAR 老编号（{stem}.rar + .rNN）";
                }
                else
                {
                    namingEvidence = "RAR 入口名认不出卷标记 ⇒ 不出规范名计划";
                }
            }

            if (targets == null || targets.Count != numbered.Count)
            {
                return new CanonicalNamePlan { Evidence = namingEvidence };
            }

            var items = new List<CanonicalNamePlanItem>();

            for (int index = 0; index < numbered.Count; index++)
            {
                GroupPieceFact piece = numbered[index].Fact;
                string targetName = targets[index];
                string targetPath = Path.Combine(sourceLayer, targetName);
                string conflict = string.Empty;

                if (!string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(piece.Path), StringComparison.OrdinalIgnoreCase)
                    && File.Exists(targetPath))
                {
                    conflict = "目标名已被别的文件占用";
                }

                items.Add(new CanonicalNamePlanItem(
                    piece.Path,
                    targetName,
                    numbered[index].Number == 1,
                    conflict));
            }

            return new CanonicalNamePlan
            {
                Items = items,
                Evidence = namingEvidence
            };
        }
    }
}

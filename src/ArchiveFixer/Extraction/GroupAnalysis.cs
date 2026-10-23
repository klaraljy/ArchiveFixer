using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **第二大步的组分析（全树版）** —— 用户 2026-10-23 口径：
    ///
    /// <para>「有些分卷是看不出来的」⇒ 判据不能只靠内容：<b>基名（第一个点之前）+ 大小分布</b> 是基石；
    /// 内容只回答"能回答的那部分"（卷号 / 首卷标记 / 整包字节数）。</para>
    ///
    /// <para>输入 = 第一大步登记的交接事实 / 任意几份候选文件（含中间卷、判不出的片）；
    /// 扫描根 = 本批产物树 + 用户源目录（可多个）。只回答"这一组是谁、找齐了没有、入口是哪一片"，
    /// ⛔ 不改名、⛔ 不搬、⛔ 不删（执行归分卷专属算法）。</para>
    ///
    /// <para>判据顺序：① 基名相同（全树收片）⇒ ② 定序：内容卷号优先，名字卷标记次之，
    /// 都没有，就**按大小**（除末片外等大、末片更小）⇒ ③ 连成 1..N 才算可证完整；
    /// 连不成 ⇒ 缺卷（缺口能点名）；连定序都做不到 ⇒ 判不出（什么都不做）。</para>
    ///
    /// <para>⚠ 用户口径（2026-10-23）：「基名相同、体积也相同但其实是两个包」的概率非常小，**不做处理** ——
    /// 所以这一层<b>不按族/格式分桶</b>，族只作为证据字段如实写出来。</para>
    /// </summary>
    public static class GroupAnalysis
    {
        public enum GroupAnalysisVerdict
        {
            /// <summary>判不出（缺证据 / 卷号冲突 / 连不成 1..N）。</summary>
            Unknown = 0,

            /// <summary>可证完整：连成 1..N，入口 = 第 1 片。</summary>
            Complete = 1,

            /// <summary>明显缺卷（缺口能点名）。</summary>
            Missing = 2
        }

        public sealed class GroupCandidate
        {
            public string BaseName { get; init; } = string.Empty;

            public ArchiveFixer.Detection.VolumeContentFormat Format { get; init; }

            public IReadOnlyList<string> Pieces { get; init; } = Array.Empty<string>();

            public string EntryPath { get; init; } = string.Empty;

            public GroupAnalysisVerdict Verdict { get; init; }

            public string Evidence { get; init; } = string.Empty;

            /// <summary>判不出时"缺口"是空的；判出缺卷时列出缺的卷号（1 起）。</summary>
            public IReadOnlyList<int> MissingNumbers { get; init; } = Array.Empty<int>();

            public string Describe() =>
                $"{BaseName} ｜ 族={Format} ｜ 找到 {Pieces.Count} 片 ｜ 结论={Verdict}"
                + $" ｜ 入口={(EntryPath.Length == 0 ? "判不出" : Path.GetFileName(EntryPath))}"
                + (MissingNumbers.Count == 0 ? string.Empty : $" ｜ 缺={string.Join(",", MissingNumbers)}")
                + $" ｜ {Evidence}";
        }

        /// <summary>
        /// 全树组分析（只读）。<paramref name="seeds"/> 是任意几份文件（含中间卷、判不出的片），
        /// <paramref name="scanRoots"/> 是要扫的根目录（本批产物树 / 用户源目录）。
        /// </summary>
        public static List<GroupCandidate> Analyze(
            IEnumerable<string>? seeds,
            IEnumerable<string>? scanRoots)
        {
            var result = new List<GroupCandidate>();
            var roots = (scanRoots ?? Enumerable.Empty<string>())
                .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string seed in (seeds ?? Enumerable.Empty<string>())
                         .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)))
            {
                string baseName = ResolveGroupBaseName(seed);

                if (baseName.Length == 0)
                {
                    continue;
                }

                // 基名相同就是一组（⛔ 不分族：用户口径"跨族同基名同体积"的概率极小，不做处理）。
                if (result.Any(existing => string.Equals(existing.BaseName, baseName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                List<string> pieces = CollectPieces(seed, baseName, roots);

                result.Add(Evaluate(baseName, pieces));
            }

            return result;
        }

        /// <summary>
        /// 组基名 = **第二大步那份**（<see cref="ArchiveFixer.Helpers.FileNameHelper.SecondStepBaseName"/>，
        /// 第一个点之前）。⛔ 不读内容、不问族：7z 非首卷 / ZIP 裸切中间片的内容本来就给不出身份。
        /// </summary>
        private static string ResolveGroupBaseName(string path) =>
            ArchiveFixer.Helpers.FileNameHelper.SecondStepBaseName(path);

        /// <summary>在扫描根里收"同基名"的片（全树，去重按物理身份）。</summary>
        private static List<string> CollectPieces(
            string seed,
            string baseName,
            IReadOnlyList<string> roots)
        {
            var pieces = new List<string> { seed };
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (ArchiveFixer.Detection.FileIdentity.TryGetIdentity(seed, out ulong seedVolume, out ulong seedIndex))
            {
                identities.Add(seedVolume.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ":" + seedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            IEnumerable<string> source = roots.Count == 0
                ? Directory.EnumerateFiles(Path.GetDirectoryName(seed) ?? ".", "*", SearchOption.TopDirectoryOnly)
                : roots.SelectMany(root => SafeEnumerate(root));

            foreach (string path in source)
            {
                if (string.Equals(path, seed, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(path))
                {
                    continue;
                }

                if (!string.Equals(
                        ArchiveFixer.Helpers.FileNameHelper.SecondStepBaseName(path),
                        baseName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ArchiveFixer.Detection.FileIdentity.TryGetIdentity(path, out ulong volume, out ulong index))
                {
                    string key = volume.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

                    if (!identities.Add(key))
                    {
                        continue;
                    }
                }

                pieces.Add(path);
            }

            return pieces;
        }

        /// <summary>
        /// 定序 + 判完整性：内容卷号 &gt; 名字卷标记 &gt; 大小分布。
        /// ⛔ 排不出顺序或连不成 1..N ⇒ 判不出（绝不猜缺哪几片）。
        /// </summary>
        private static GroupCandidate Evaluate(string baseName, List<string> pieces)
        {
            var items = pieces
                .Select(path => new Piece(path, ArchiveFixer.Detection.FileNature.Inspect(path)))
                .ToList();

            ArchiveFixer.Detection.VolumeContentFormat format = items
                .Select(item => item.Card.Format)
                .FirstOrDefault(value => value != ArchiveFixer.Detection.VolumeContentFormat.Unknown);

            // ① 内容卷号（能读出来的最有分量）。
            List<int> contentNumbers = items
                .Select(item => item.Card.VolumeNumber)
                .Where(number => number.HasValue)
                .Select(number => number!.Value)
                .Distinct()
                .OrderBy(number => number)
                .ToList();

            if (contentNumbers.Count > 0)
            {
                return Judge(baseName, format, items, contentNumbers, "内容卷号");
            }

            // ② 名字里的卷标记（7z 中间/末片、ZIP 裸切中间片在内容上判不出，名字常常还读得出来）。
            List<int> nameNumbers = items
                .Select(item => ArchiveFixer.Detection.VolumeGroupDetector.TryGetVolumeIndex(
                    Path.GetFileName(item.Path)))
                .Where(number => number.HasValue)
                .Select(number => number!.Value)
                .Distinct()
                .OrderBy(number => number)
                .ToList();

            if (nameNumbers.Count > 0)
            {
                return Judge(baseName, format, items, nameNumbers, "名字卷标记");
            }

            /*
             * ③ 都判不出 ⇒ **只按大小定序**（除末片外等大、末片更小或相等）。
             *
             * ⚠ 这一档能回答"谁可能是末片"，但回答不了"这一组该有几片"（N 只能由入口包自述的整包字节数
             * 给出，那一条属于分卷专属算法）⇒ 结论只能落"判不出"，⛔ 不猜缺卷。
             */
            List<Piece> bySize = items
                .OrderBy(item => item.Card.Size)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool regularSizes = bySize.Count >= 2
                && bySize.Take(bySize.Count - 1).All(item => item.Card.Size == bySize[0].Card.Size)
                && bySize[^1].Card.Size <= bySize[0].Card.Size;

            return new GroupCandidate
            {
                BaseName = baseName,
                Format = format,
                Pieces = bySize.Select(item => item.Path).ToList(),
                EntryPath = string.Empty,
                Verdict = GroupAnalysisVerdict.Unknown,
                Evidence = regularSizes
                    ? "卷号判不出：只能按大小定序（除末片外等大），⛔ 不猜缺哪几片"
                    : "卷号判不出、大小也不规律：只给候选清单，⛔ 什么都不做"
            };
        }

        /// <summary>用一组卷号判完整性（连成 1..N ⇒ 完整；1..max 有洞 ⇒ 缺卷）。</summary>
        private static GroupCandidate Judge(
            string baseName,
            ArchiveFixer.Detection.VolumeContentFormat format,
            List<Piece> items,
            List<int> numbers,
            string evidenceSource)
        {
            var ordered = items
                .OrderBy(item => item.Card.VolumeNumber ?? int.MaxValue)
                .ThenBy(item => item.Card.Size)
                .ToList();

            int max = numbers[^1];
            bool contiguous = numbers[0] == 1
                && numbers.Count == max
                && numbers.SequenceEqual(Enumerable.Range(1, max));

            List<int> missing = contiguous
                ? new List<int>()
                : Enumerable.Range(1, max).Except(numbers).ToList();

            string entry = items
                .FirstOrDefault(item => item.Card.FirstVolumeFlag || item.Card.VolumeNumber == 1)
                ?.Path ?? string.Empty;

            return new GroupCandidate
            {
                BaseName = baseName,
                Format = format,
                Pieces = ordered.Select(item => item.Path).ToList(),
                EntryPath = entry,
                Verdict = contiguous ? GroupAnalysisVerdict.Complete : GroupAnalysisVerdict.Missing,
                MissingNumbers = missing,
                Evidence = contiguous
                    ? $"{evidenceSource}连成 1..{max}"
                    : $"{evidenceSource}只找到 {string.Join(",", numbers)}（1..{max} 里缺 {string.Join(",", missing)}）"
            };
        }

        private sealed record Piece(string Path, ArchiveFixer.Detection.FileNatureCard Card);

        private static IEnumerable<string> SafeEnumerate(string root)
        {
            try
            {
                return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }
    }
}

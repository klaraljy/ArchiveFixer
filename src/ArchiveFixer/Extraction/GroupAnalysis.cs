using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Helpers;

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

            /// <summary>自述片数（7z 首片起始头算出来的；随交接单过来）；判不出 = 0。</summary>
            public int DeclaredPieceCount { get; init; }

            /// <summary>已收片数（按物理身份去重后）。</summary>
            public int ReceivedPieceCount { get; init; }

            /// <summary>自述整包字节数（7z 硬证据）；判不出 = 0。</summary>
            public long DeclaredTotalBytes { get; init; }

            /// <summary>已收字节数合计（与自述整包对账用）。</summary>
            public long ReceivedBytes { get; init; }

            /// <summary>每片的原始事实（第二大步建组用；卷号可为空，⛔ 不在这里补）。</summary>
            public IReadOnlyList<GroupPieceFact> PieceFacts { get; set; } = Array.Empty<GroupPieceFact>();

            /// <summary>每片的底层内容卡（族判定唯一出口要用它；已登记的片由交接单还原，⛔ 不重读盘）。</summary>
            public IReadOnlyList<ArchiveFixer.Detection.FileNatureCard> Cards { get; set; } =
                Array.Empty<ArchiveFixer.Detection.FileNatureCard>();

            public string Describe() =>
                $"{BaseName} ｜ 族={Format} ｜ 找到 {Pieces.Count} 片 ｜ 结论={Verdict}"
                + $" ｜ 入口={(EntryPath.Length == 0 ? "判不出" : Path.GetFileName(EntryPath))}"
                + (MissingNumbers.Count == 0 ? string.Empty : $" ｜ 缺={string.Join(",", MissingNumbers)}")
                + (DeclaredPieceCount > 0
                    ? $" ｜ 片数来自自述 {DeclaredPieceCount} 片（已收 {ReceivedPieceCount}）"
                    : string.Empty)
                + (DeclaredTotalBytes > 0
                    ? $" ｜ 自述整包 {DeclaredTotalBytes} 字节（已收 {ReceivedBytes}）"
                    : string.Empty)
                + $" ｜ {Evidence}";
        }

        /// <summary>
        /// 全树组分析（只读）。<paramref name="seeds"/> 是任意几份文件（含中间卷、判不出的片），
        /// <paramref name="scanRoots"/> 是要扫的根目录（本批产物树 / 用户源目录）。
        /// </summary>
        public static List<GroupCandidate> Analyze(
            IEnumerable<string>? seeds,
            IEnumerable<string>? scanRoots)
            => AnalyzeCore(seeds, scanRoots, handoffByPath: null);

        /// <summary>
        /// **按交接单做全树组分析**（用户 2026-10-24：第二大步要吃交接单里的底层事实）：
        /// 种子 = 交接单里有路径的那些；每片的族 / 卷号 / 首卷 / 自述片数 / 自述整包字节数**直接取交接单**，
        /// ⛔ 不再对已经登记过的片重新 `FileNature.Inspect`；盘上只补“同基名但没登记过的兄弟片”，
        /// 补到的那几片才回盘上读一次证据。
        /// </summary>
        public static List<GroupCandidate> Analyze(
            IReadOnlyList<ArchiveFixer.Detection.VolumeHandoffEntry>? handoffs,
            IEnumerable<string>? scanRoots,
            IEnumerable<string>? extraSeeds = null)
        {
            var handoffByPath = new Dictionary<string, ArchiveFixer.Detection.VolumeHandoffEntry>(
                StringComparer.OrdinalIgnoreCase);
            var seeds = new List<string>();

            foreach (ArchiveFixer.Detection.VolumeHandoffEntry entry
                     in handoffs ?? Array.Empty<ArchiveFixer.Detection.VolumeHandoffEntry>())
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Path))
                {
                    continue;
                }

                handoffByPath[entry.Path] = entry;
                seeds.Add(entry.Path);
            }

            /*
             * 额外种子 = 这一轮认出来的内层归档（它们没被闸门拦下、所以不在交接单里）——
             * 只加种子，不加事实：它们仍按盘上证据判。
             */
            foreach (string seed in extraSeeds ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(seed))
                {
                    seeds.Add(seed);
                }
            }

            return AnalyzeCore(seeds, scanRoots, handoffByPath);
        }

        private static List<GroupCandidate> AnalyzeCore(
            IEnumerable<string>? seeds,
            IEnumerable<string>? scanRoots,
            IReadOnlyDictionary<string, ArchiveFixer.Detection.VolumeHandoffEntry>? handoffByPath)
        {
            var result = new List<GroupCandidate>();
            var roots = (scanRoots ?? Enumerable.Empty<string>())
                .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var seenBaseAndFormat = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string seed in (seeds ?? Enumerable.Empty<string>())
                         .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)))
            {
                string baseName = ResolveGroupBaseName(seed);

                if (baseName.Length == 0)
                {
                    continue;
                }

                /*
                 * 同基名不一定同组：970 批 `29292929` 的真机形状是
                 * `pack111-L1.part1.rar`（RAR4）和 `pack111-L1.z01/.zip`（ZipSpanned）同基名。
                 * 旧口径“基名相同就是一组、不分族”把 RAR 片塞进 ZIP 组，4 片对自述 3 片 ⇒ Missing。
                 * ⇒ 组键必须是「基名 + 内容族」；内容判不出族的片仍允许补位，但不许把已证实的异族并进来。
                 */
                ArchiveFixer.Detection.VolumeContentFormat seedFormat =
                    ArchiveFixer.Detection.FileNature.Inspect(seed).Format;

                string key = baseName + "|" + seedFormat;

                if (!seenBaseAndFormat.Add(key))
                {
                    continue;
                }

                List<string> pieces = CollectPieces(seed, baseName, seedFormat, roots);

                result.Add(Evaluate(baseName, pieces, handoffByPath));
            }

            EnrichGroupsWithContentMembers(result, handoffByPath);

            return result;
        }

        /// <summary>
        /// 内容卡补位：基名先分候选组之后，再把“内容已证明是这一族某卷、但名字/基名没进组”的片
        /// 按**缺卷号**补进唯一匹配的组。名字在这里只用于“同目录歧义消解”，不再作为入场券。
        ///
        /// <para>只补能证明的缺口：候选必须是 VolumeMember，卷号非空，且目标组当前正好缺这个号；
        /// 同目录同族有多个组同时缺这个号 ⇒ 判不出，不补。</para>
        /// </summary>
        private static void EnrichGroupsWithContentMembers(
            List<GroupCandidate> groups,
            IReadOnlyDictionary<string, ArchiveFixer.Detection.VolumeHandoffEntry>? handoffByPath)
        {
            if (handoffByPath == null || handoffByPath.Count == 0)
            {
                return;
            }

            foreach (ArchiveFixer.Detection.VolumeHandoffEntry entry in handoffByPath.Values)
            {
                if (entry == null
                    || string.IsNullOrWhiteSpace(entry.Path)
                    || !File.Exists(entry.Path)
                    || entry.Membership != ArchiveFixer.Detection.VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                    || entry.VolumeNumber is not int number
                    || number < 1)
                {
                    continue;
                }

                string directory = SafePathHelper.GetFullPathSafe(entry.SourceLayer);

                if (directory.Length == 0)
                {
                    directory = SafePathHelper.GetFullPathSafe(Path.GetDirectoryName(entry.Path) ?? string.Empty);
                }

                List<GroupCandidate> targets = groups
                    .Where(group => group.Format == entry.Format
                        && string.Equals(
                            SafePathHelper.GetFullPathSafe(Path.GetDirectoryName(group.EntryPath) ?? string.Empty),
                            directory,
                            StringComparison.OrdinalIgnoreCase)
                        && group.MissingNumbers.Contains(number))
                    .ToList();

                if (targets.Count != 1)
                {
                    continue;
                }

                GroupCandidate target = targets[0];
                var pieces = new List<string>(target.Pieces);

                if (pieces.Any(path => string.Equals(path, entry.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                pieces.Add(entry.Path);
                GroupCandidate enriched = Evaluate(target.BaseName, pieces, handoffByPath);

                int index = groups.IndexOf(target);
                groups[index] = enriched;

                // 候选自己那一组如果只装着这一片，已经被合并进目标组，去掉重复组。
                groups.RemoveAll(group => !ReferenceEquals(group, enriched)
                    && group.Pieces.Count == 1
                    && string.Equals(group.Pieces[0], entry.Path, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// 组基名 = **第二大步那份**（<see cref="ArchiveFixer.Helpers.FileNameHelper.SecondStepBaseName"/>，
        /// 第一个点之前）。⛔ 不读内容、不问族：7z 非首卷 / ZIP 裸切中间片的内容本来就给不出身份。
        /// </summary>
        private static string ResolveGroupBaseName(string path) =>
            ArchiveFixer.Helpers.FileNameHelper.SecondStepBaseName(path);

        /// <summary>
        /// 在扫描根里收"同基名、同内容族"的片（全树，去重按物理身份）。
        /// 内容族未知的片允许补位；已证实的异族片绝不并进这一组。
        /// </summary>
        private static List<string> CollectPieces(
            string seed,
            string baseName,
            ArchiveFixer.Detection.VolumeContentFormat seedFormat,
            IReadOnlyList<string> roots)
        {
            var pieces = new List<string> { seed };
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /*
             * 2026-10-25：独立 `.rar`（例如合集里的 `弦音sic NO.001 ... .rar`）不是分卷。
             * 只有 seed 自己带卷标记（`.001` / `.part1.rar` / `.r00` 等），或者候选文件带卷标记时，
             * 才把它收进同一组；否则每个包必须各自成任务/各自输出。
             * 这条不按样本名开分支，防的是“同基名的普通归档被误当分卷”。
             */
            bool seedHasVolumeMarker = ArchiveFixer.Helpers.FileNameHelper.IsVolumePartFileName(Path.GetFileName(seed));

            if (ArchiveFixer.Detection.FileIdentity.TryGetIdentity(seed, out ulong seedVolume, out ulong seedIndex))
            {
                identities.Add(seedVolume.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ":" + seedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            /*
             * 种子自己所在目录**永远要扫**：组分析不能因为调用方传进来的 roots 没覆盖到
             * 它这一层（例如同目录、不同产出层的兄弟片）就把同组片漏在候选池外。
             * roots 仍然保留，用于跨目录收片；这里只补上 seed 的物理目录。
             */
            var scanRoots = new List<string>(roots);
            string? seedDirectory = Path.GetDirectoryName(seed);

            if (!string.IsNullOrWhiteSpace(seedDirectory))
            {
                scanRoots.Add(seedDirectory);
            }

            IEnumerable<string> source = scanRoots
                .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(root => SafeEnumerate(root));

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

                if (!seedHasVolumeMarker
                    && !ArchiveFixer.Helpers.FileNameHelper.IsVolumePartFileName(Path.GetFileName(path)))
                {
                    continue;
                }

                if (seedFormat != ArchiveFixer.Detection.VolumeContentFormat.Unknown)
                {
                    ArchiveFixer.Detection.VolumeContentFormat candidateFormat;

                    try
                    {
                        candidateFormat = ArchiveFixer.Detection.FileNature.Inspect(path).Format;
                    }
                    catch
                    {
                        // 读不出内容不阻止补位；由族算法/完整性判据兜底。
                        candidateFormat = ArchiveFixer.Detection.VolumeContentFormat.Unknown;
                    }

                    if (candidateFormat != ArchiveFixer.Detection.VolumeContentFormat.Unknown
                        && candidateFormat != seedFormat)
                    {
                        continue;
                    }
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
        private static GroupCandidate Evaluate(
            string baseName,
            List<string> pieces,
            IReadOnlyDictionary<string, ArchiveFixer.Detection.VolumeHandoffEntry>? handoffByPath)
        {
            var items = pieces
                .Select(path => BuildFacts(path, handoffByPath))
                .ToList();

            ArchiveFixer.Detection.VolumeContentFormat format = items
                .Select(item => item.Format)
                .FirstOrDefault(value => value != ArchiveFixer.Detection.VolumeContentFormat.Unknown);

            int declaredPieceCount = items.Select(item => item.DeclaredPieceCount).DefaultIfEmpty(0).Max();
            long declaredTotalBytes = items.Select(item => item.DeclaredTotalBytes).DefaultIfEmpty(0).Max();
            long receivedBytes = items.Sum(item => item.Size);

            List<GroupPieceFact> publicFacts = items.Select(ToPublicFact).ToList();
            List<ArchiveFixer.Detection.FileNatureCard> cards = items.Select(item => item.Card).ToList();

            GroupCandidate Finish(GroupCandidate candidate)
            {
                candidate.PieceFacts = publicFacts;
                candidate.Cards = cards;
                return candidate;
            }

            /*
             * ===== ⓪ 跨盘 ZIP 先按名字定序（用户 2026-10-11 口径：那一族"头尾在名字编号里是反的"）=====
             * `x.z01/.z02/……/x.zip`：名字里 `.zip` 是末片、`.z01` 才是第 1 片；
             * 而内容的 EOCD 盘号只有 `.zip` 那个末片带得出来（`.z01/.z02` 读不出）。
             * ⇒ 这一族**只按名字**定序（`z01`→1、`z02`→2、`.zip`→N），内容卷号不参与，
             *   否则两套编号混在一起会得出"缺 1"这种假缺卷（真机 `10101010` 的 `222-L1`）。
             */
            if (TryOrderSpannedZip(items, out List<PieceFacts> spannedOrder, out List<int> spannedNumbers))
            {
                return Finish(Judge(
                    baseName,
                    format,
                    spannedOrder,
                    spannedNumbers,
                    "跨盘 ZIP 名字定序",
                    declaredPieceCount,
                    declaredTotalBytes,
                    receivedBytes));
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

                foreach (PieceFacts item in items)
                {
                    if (item.VolumeNumber.HasValue)
                    {
                        continue;
                    }

                    int? nameIndex = ArchiveFixer.Detection.VolumeGroupDetector.TryGetVolumeIndex(
                        Path.GetFileName(item.Path));

                    if (nameIndex.HasValue)
                    {
                        merged.Add(nameIndex.Value);
                    }
                }

                string source = merged.Count > contentNumbers.Count ? "内容卷号+名字补缺" : "内容卷号";

                return Finish(Judge(
                    baseName,
                    format,
                    items,
                    merged.ToList(),
                    source,
                    declaredPieceCount,
                    declaredTotalBytes,
                    receivedBytes));
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
                return Finish(Judge(baseName, format, items, nameNumbers, "名字卷标记", declaredPieceCount, declaredTotalBytes, receivedBytes));
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

                return Finish(new GroupCandidate
                {
                    BaseName = baseName,
                    Format = format,
                    Pieces = items
                        .OrderBy(item => item.Size)
                        .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                        .Select(item => item.Path)
                        .ToList(),
                    EntryPath = items.FirstOrDefault(item => item.IsFirstPiece)?.Path ?? string.Empty,
                    Verdict = complete ? GroupAnalysisVerdict.Complete : GroupAnalysisVerdict.Missing,
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
                });
            }

            /*
             * ④ 都判不出 ⇒ **只按大小定序**（除末片外等大、末片更小或相等）。
             *
             * ⚠ 这一档能回答"谁可能是末片"，但回答不了"这一组该有几片"（N 只能由入口包自述的整包字节数
             * 给出，那一条属于分卷专属算法）⇒ 结论只能落"判不出"，⛔ 不猜缺卷。
             */
            List<PieceFacts> bySize = items
                .OrderBy(item => item.Size)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool regularSizes = bySize.Count >= 2
                && bySize.Take(bySize.Count - 1).All(item => item.Size == bySize[0].Size)
                && bySize[^1].Size <= bySize[0].Size;

            return Finish(new GroupCandidate
            {
                BaseName = baseName,
                Format = format,
                Pieces = bySize.Select(item => item.Path).ToList(),
                EntryPath = string.Empty,
                Verdict = GroupAnalysisVerdict.Unknown,
                DeclaredPieceCount = declaredPieceCount,
                ReceivedPieceCount = items.Count,
                DeclaredTotalBytes = declaredTotalBytes,
                ReceivedBytes = receivedBytes,
                Evidence = regularSizes
                    ? "卷号判不出：只能按大小定序（除末片外等大），⛔ 不猜缺哪几片"
                    : "卷号判不出、大小也不规律：只给候选清单，⛔ 什么都不做"
            });
        }

        /// <summary>
        /// 一片的证据：**已登记过交接单的片直接用交接单里的底层事实**（⛔ 不重读盘）；
        /// 只有盘上补进来的、没登记过的兄弟片才 `FileNature.Inspect` 一次。
        /// </summary>
        private static PieceFacts BuildFacts(
            string path,
            IReadOnlyDictionary<string, ArchiveFixer.Detection.VolumeHandoffEntry>? handoffByPath)
        {
            if (handoffByPath != null && handoffByPath.TryGetValue(path, out ArchiveFixer.Detection.VolumeHandoffEntry? handoff))
            {
                /*
                 * ⛔ 不重读盘：交接单里的内容事实直接还原成一张卡（HasZip* 那几格交接单没有，
                 * 保持默认 false；ZIP 族的判定在族算法里会结合名字/盘号，不靠这几格猜）。
                 */
                var handoffCard = new ArchiveFixer.Detection.FileNatureCard
                {
                    Path = handoff.Path,
                    Size = handoff.Size,
                    Format = handoff.Format,
                    Membership = handoff.Membership,
                    VolumeNumber = handoff.VolumeNumber,
                    FirstVolumeFlag = handoff.FirstVolumeFlag,
                    ContentFail = handoff.ContentFail,
                    BodyKind = handoff.BodyKind,
                    DeclaredTotalBytes = handoff.DeclaredTotalBytes,
                    DeclaredVolumeCount = handoff.DeclaredVolumeCount,
                    RawVolumeField = handoff.RawVolumeField,
                    HasZipEndRecord = handoff.HasZipEndRecord,
                    ZipEndRecordMeansSplit = handoff.ZipEndRecordMeansSplit,
                    HasZipLocalHeader = handoff.HasZipLocalHeader
                };

                return new PieceFacts(
                    path,
                    handoff.Format,
                    handoff.VolumeNumber,
                    handoff.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.VolumeFirstPiece,
                    handoff.Size,
                    handoff.DeclaredVolumeCount,
                    handoff.DeclaredTotalBytes,
                    handoff,
                    handoffCard);
            }

            ArchiveFixer.Detection.FileNatureCard card = ArchiveFixer.Detection.FileNature.Inspect(path);

            long size = card.Size;

            if (size <= 0)
            {
                try
                {
                    size = new FileInfo(path).Length;
                }
                catch
                {
                    // 读不到大小就按 0 计入（对账时会自然落到"缺片"或"判不出"）。
                }
            }

            return new PieceFacts(
                path,
                card.Format,
                card.VolumeNumber,
                card.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.VolumeFirstPiece,
                size,
                card.DeclaredVolumeCount,
                card.DeclaredTotalBytes,
                null,
                card);
        }

        /// <summary>把内部证据折成第二大步拿得走的原始事实（⛔ 不补判不出的字段）。</summary>
        private static GroupPieceFact ToPublicFact(PieceFacts item) =>
            new(
                item.Path,
                item.Size,
                item.Card.Format,
                item.Card.BodyKind,
                item.Card.VolumeNumber,
                item.Card.FirstVolumeFlag,
                item.Card.Membership,
                item.Card.ContentFail,
                item.Card.DeclaredTotalBytes,
                item.Card.DeclaredVolumeCount,
                item.Card.RawVolumeField,
                item.Handoff?.SourceLayer ?? (Path.GetDirectoryName(item.Path) ?? string.Empty),
                item.Handoff?.Source,
                item.Handoff != null,
                item.Card.HasZipEndRecord,
                item.Card.ZipEndRecordMeansSplit,
                item.Card.HasZipLocalHeader);

        /// <summary>用一组卷号判完整性（连成 1..N ⇒ 完整；1..max 有洞 ⇒ 缺卷）。</summary>
        private static GroupCandidate Judge(
            string baseName,
            ArchiveFixer.Detection.VolumeContentFormat format,
            List<PieceFacts> items,
            List<int> numbers,
            string evidenceSource,
            int declaredPieceCount,
            long declaredTotalBytes,
            long receivedBytes)
        {
            var ordered = items
                .OrderBy(item => item.VolumeNumber ?? int.MaxValue)
                .ThenBy(item => item.Size)
                .ToList();

            /*
             * 完整性的 N 取「名字/内容卷号的最大值」与「入口包自述片数」里的较大者：
             * ⛔ 只要有自述片数，就不许把"只找到 1 片、名字恰好是 1"当成 1..1 完整
             * （真机 V03：只看到 `组.7z.001`、自述 4 片，老判据会判 Complete ⇒ 第二大步白跑一轮）。
             */
            int expected = Math.Max(numbers[^1], declaredPieceCount);

            bool contiguous = numbers[0] == 1
                && numbers.Count == expected
                && numbers.SequenceEqual(Enumerable.Range(1, expected));

            List<int> missing = contiguous
                ? new List<int>()
                : Enumerable.Range(1, expected).Except(numbers).ToList();

            string declaredNote = declaredPieceCount > numbers[^1]
                ? $"（自述 {declaredPieceCount} 片）"
                : string.Empty;

            string entry = items
                .FirstOrDefault(item => item.IsFirstPiece || item.VolumeNumber == 1)
                ?.Path ?? string.Empty;

            if (entry.Length == 0)
            {
                entry = items
                    .FirstOrDefault(item => ArchiveFixer.Detection.VolumeGroupDetector.TryGetVolumeIndex(
                        Path.GetFileName(item.Path)) == 1)
                    ?.Path ?? string.Empty;
            }

            return new GroupCandidate
            {
                BaseName = baseName,
                Format = format,
                Pieces = ordered.Select(item => item.Path).ToList(),
                EntryPath = entry,
                Verdict = contiguous ? GroupAnalysisVerdict.Complete : GroupAnalysisVerdict.Missing,
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

        /// <summary>
        /// 跨盘 ZIP 名字定序：`x.z01`..`x.zNN` + 末片 `x.zip` ⇒ 第 1..N 片。
        /// ⛔ 只要有一份不是这个形状（或 z 号不连续）⇒ 返回 false，交给别的判据。
        /// </summary>
        private static bool TryOrderSpannedZip(
            List<PieceFacts> items,
            out List<PieceFacts> ordered,
            out List<int> numbers)
        {
            ordered = new List<PieceFacts>();
            numbers = new List<int>();

            if (items.Count < 2)
            {
                return false;
            }

            var disks = new List<(int Disk, PieceFacts Item)>();
            PieceFacts? tailZip = null;

            foreach (PieceFacts item in items)
            {
                string extension = Path.GetExtension(Path.GetFileName(item.Path));

                if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    if (tailZip != null)
                    {
                        return false;
                    }

                    tailZip = item;
                    continue;
                }

                if (extension.Length == 4
                    && (extension[1] == 'z' || extension[1] == 'Z')
                    && char.IsAsciiDigit(extension[2])
                    && char.IsAsciiDigit(extension[3]))
                {
                    disks.Add((int.Parse(extension.Substring(2, 2), System.Globalization.CultureInfo.InvariantCulture), item));
                    continue;
                }

                return false;
            }

            if (tailZip == null || disks.Count == 0)
            {
                return false;
            }

            disks.Sort((left, right) => left.Disk.CompareTo(right.Disk));

            for (int index = 0; index < disks.Count; index++)
            {
                if (disks[index].Disk != index + 1)
                {
                    return false;
                }

                ordered.Add(disks[index].Item);
                numbers.Add(index + 1);
            }

            ordered.Add(tailZip);
            numbers.Add(disks.Count + 1);

            return true;
        }

        /// <summary>一片的证据（交接单优先，盘上补片才回盘上读一次）。</summary>
        private sealed record PieceFacts(
            string Path,
            ArchiveFixer.Detection.VolumeContentFormat Format,
            int? VolumeNumber,
            bool IsFirstPiece,
            long Size,
            int DeclaredPieceCount,
            long DeclaredTotalBytes,
            ArchiveFixer.Detection.VolumeHandoffEntry? Handoff,
            ArchiveFixer.Detection.FileNatureCard Card);

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

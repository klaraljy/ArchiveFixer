using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
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

            /// <summary>
            /// 候选建议，不是最终结论；⛔ 第二大步的完整性/缺片/规范名只认
            /// <see cref="VolumeGroupService"/> + 族算法。保留 <see cref="Verdict"/> 只做旧调用兼容。
            /// </summary>
            public GroupAnalysisVerdict SuggestedVerdict { get; init; }

            /// <summary>兼容旧调用：等价于 <see cref="SuggestedVerdict"/>；⛔ 不许当最终判据。</summary>
            public GroupAnalysisVerdict Verdict => SuggestedVerdict;

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

            /// <summary>
            /// 边界 resolver 已经解析好的组（骨架阶段可能为 null）。
            /// 有它时，入口/顺序/标准名以它为准，⛔ 不再从脏名重排。
            /// </summary>
            public PreparedVolumeGroup? PreparedGroup { get; init; }

            /// <summary>入口片的原始路径（任务表/落点/source 处理用；临时标准名不算）。</summary>
            public string OriginalEntryPath { get; init; } = string.Empty;

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

        /// <summary>
        /// **吃边界 resolver 的 prepared 组**（共用骨架的新入口）：
        /// 只把已经标准化的片收成候选，⛔ 不再扫用户源目录、⛔ 不再从脏名重排。
        /// 旧 <see cref="Analyze(IEnumerable{string}?, IEnumerable{string}?)"/> 保留给未迁移族。
        /// </summary>
        public static List<GroupCandidate> AnalyzePrepared(IReadOnlyList<PreparedVolumeGroup>? groups)
        {
            var result = new List<GroupCandidate>();

            foreach (PreparedVolumeGroup group in groups ?? Array.Empty<PreparedVolumeGroup>())
            {
                if (group == null || string.IsNullOrWhiteSpace(group.BaseName))
                {
                    continue;
                }

                List<string> pieces = group.Pieces
                    .Select(piece => string.IsNullOrWhiteSpace(group.EntryLayer)
                        ? piece.OriginalPath
                        : Path.Combine(group.EntryLayer, piece.CanonicalName))
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    .ToList();

                string entryPath = !string.IsNullOrWhiteSpace(group.CanonicalEntryPath)
                    ? group.CanonicalEntryPath
                    : group.OriginalEntryPath;

                var candidate = new GroupCandidate
                {
                    BaseName = group.BaseName,
                    Format = FormatForFamily(group.Family),
                    Pieces = pieces,
                    EntryPath = entryPath,
                    SuggestedVerdict = group.State switch
                    {
                        PreparedGroupState.Complete => GroupAnalysisVerdict.Complete,
                        PreparedGroupState.Missing => GroupAnalysisVerdict.Missing,
                        _ => GroupAnalysisVerdict.Unknown
                    },
                    Evidence = group.Describe(),
                    MissingNumbers = group.MissingNumbers,
                    ReceivedPieceCount = pieces.Count,
                    PreparedGroup = group,
                    OriginalEntryPath = group.OriginalEntryPath
                };

                result.Add(candidate);
            }

            return result;
        }

        private static ArchiveFixer.Detection.VolumeContentFormat FormatForFamily(VolumeNamingFamily? family)
        {
            return family switch
            {
                VolumeNamingFamily.SevenZipNumbered => ArchiveFixer.Detection.VolumeContentFormat.SevenZip,
                VolumeNamingFamily.RarPart => ArchiveFixer.Detection.VolumeContentFormat.Rar,
                VolumeNamingFamily.ZipSpanned or VolumeNamingFamily.ZipNumbered
                    => ArchiveFixer.Detection.VolumeContentFormat.Zip,
                _ => ArchiveFixer.Detection.VolumeContentFormat.Unknown
            };
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

            /*
             * 种子按**基名**归桶（用户口径：第二大步的组键是基名，不是“目录 + 基名 + 族”）：
             * 同一组的片可能散在兄弟目录（包装产物），所以同基名的种子要**一起收片**再判；
             * 但两个**各自完整**的同名组不能因此被并成一个 —— 由下面的自述事实分桶，
             * 而不是按目录猜。
             */
            var seedsByBaseName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (string seed in (seeds ?? Enumerable.Empty<string>())
                         .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)))
            {
                /*
                 * ⛔ 2026-11-01 真机（2.exe / 2.part*.rar）：**已经自证是完整单卷的文件，不许再当分卷组种子**。
                 * 父包 2.exe 与它解出来的 2.part1/2/3.rar 基名都是 "2"，旧逻辑只比基名+族，
                 * 于是父包被收进子分卷组 ⇒ 组永远判不出、过程物也删不掉。
                 * 判据只认内容事实：BodyKind=SingleComplete + Membership=ProvablyNotVolumeMember + 没有名字卷槽。
                 */
                if (handoffByPath != null
                    && handoffByPath.TryGetValue(seed, out ArchiveFixer.Detection.VolumeHandoffEntry? seedHandoff)
                    && IsProvablySingleCompleteNonMember(seedHandoff))
                {
                    continue;
                }

                string baseName = ResolveGroupBaseName(seed);

                if (baseName.Length == 0)
                {
                    continue;
                }

                if (!seedsByBaseName.TryGetValue(baseName, out List<string>? baseSeeds))
                {
                    baseSeeds = new List<string>();
                    seedsByBaseName[baseName] = baseSeeds;
                }

                if (!baseSeeds.Contains(seed, StringComparer.OrdinalIgnoreCase))
                {
                    baseSeeds.Add(seed);
                }
            }

            foreach (KeyValuePair<string, List<string>> pair in seedsByBaseName)
            {
                string baseName = pair.Key;

                /*
                 * 同基名的所有种子一起收片：每个种子扫自己那一层 + 调用方给的 roots，
                 * 去重按路径（物理身份在 CollectPieces 里已经去过重）。
                 */
                var pieces = new List<string>();
                var seenPieces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string seed in pair.Value)
                {
                    ArchiveFixer.Detection.VolumeContentFormat seedFormat =
                        ArchiveFixer.Detection.FileNature.Inspect(seed).Format;

                    foreach (string piece in CollectPieces(seed, baseName, seedFormat, roots, handoffByPath))
                    {
                        if (seenPieces.Add(piece))
                        {
                            pieces.Add(piece);
                        }
                    }
                }

                /*
                 * 同基名跨目录收片是对的，但**两个各自完整的同名组**不能被并成一个：
                 * 判据用第一片的自述事实（DeclaredTotalBytes）分桶 —— 不是按目录猜。
                 * 守门 `GroupAnalysisNonUniformTests.同名同族但在不同目录_必须分成两组`：
                 * `a\组.7z.001` 自述 1200 字节、`b\组.7z.001` 自述 600 字节 ⇒ 这是两组。
                 * ⛔ 只有"≥2 个互不相同的自述整包"时才分桶；任一片无法唯一归桶 ⇒ **不分**
                 * （保持跨目录合并，交给族 resolver 判），⛔ 不猜。
                 */
                foreach (List<string> cluster in SplitPiecesByDeclaredGroup(pieces, handoffByPath))
                {
                    result.Add(Evaluate(baseName, cluster, handoffByPath));
                }
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

                // 候选自己那一组如果整组都已经被并进目标组，去掉重复组。
                groups.RemoveAll(group => !ReferenceEquals(group, enriched)
                    && group.Pieces.Count > 0
                    && group.Pieces.All(piece => enriched.Pieces.Contains(
                        piece,
                        StringComparer.OrdinalIgnoreCase)));
            }
        }

        /// <summary>
        /// 组基名 = **第二大步那份**（<see cref="ArchiveFixer.Helpers.FileNameHelper.SecondStepBaseName"/>，
        /// 第一个点之前）。⛔ 不读内容、不问族：7z 非首卷 / ZIP 裸切中间片的内容本来就给不出身份。
        /// </summary>
        private static string ResolveGroupBaseName(string path) =>
            ArchiveFixer.Helpers.FileNameHelper.SecondStepBaseName(path);

        /// <summary>
        /// 同目录里这一族是否**恰好只有一个**内容首卷。只有一个 ⇒ 可以用内容卡收兄弟片；
        /// 多个 ⇒ 名字仍是必要的歧义消解，不能绕基名。
        /// </summary>
        private static bool HasSingleContentFirstInDirectory(
            string? directory,
            ArchiveFixer.Detection.VolumeContentFormat format)
        {
            if (string.IsNullOrWhiteSpace(directory)
                || format == ArchiveFixer.Detection.VolumeContentFormat.Unknown
                || !Directory.Exists(directory))
            {
                return false;
            }

            int firsts = 0;

            try
            {
                foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    ArchiveFixer.Detection.FileNatureCard card;

                    try
                    {
                        card = ArchiveFixer.Detection.FileNature.Inspect(path);
                    }
                    catch
                    {
                        continue;
                    }

                    if (card.Format != format)
                    {
                        continue;
                    }

                    if (card.FirstVolumeFlag || card.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.VolumeFirstPiece)
                    {
                        firsts++;
                    }
                }
            }
            catch
            {
                return false;
            }

            return firsts == 1;
        }

        /// <summary>
        /// 在扫描根里收"同基名、同内容族"的片（全树，去重按物理身份）。
        /// 内容族未知的片允许补位；已证实的异族片绝不并进这一组。
        /// </summary>
        private static List<string> CollectPieces(
            string seed,
            string baseName,
            ArchiveFixer.Detection.VolumeContentFormat seedFormat,
            IReadOnlyList<string> roots,
            IReadOnlyDictionary<string, ArchiveFixer.Detection.VolumeHandoffEntry>? handoffByPath)
        {
            var pieces = new List<string> { seed };
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /*
             * 2026-10-25：独立 `.rar`（例如合集里的 `弦音sic NO.001 ... .rar`）不是分卷。
             * 只有 seed 自己带卷标记（`.001` / `.part1.rar` / `.r00` 等），或者候选文件带卷标记时，
             * 才把它收进同一组；否则每个包必须各自成任务/各自输出。
             * 这条不按样本名开分支，防的是“同基名的普通归档被误当分卷”。
             */
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

            /*
             * 同目录里**只有一个内容首卷**的同族候选，可以绕过“同基名”入场：
             * 内容卡已经证明它们属于这一族，名字只用于显示/规范名。
             * 同目录出现多个首卷 ⇒ 名字仍是必要的歧义消解，回退同基名收片。
             */
            bool contentGrouping = HasSingleContentFirstInDirectory(seedDirectory, seedFormat);

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

                bool sameBase = string.Equals(
                    ArchiveFixer.Helpers.FileNameHelper.SecondStepBaseName(path),
                    baseName,
                    StringComparison.OrdinalIgnoreCase);

                if (!sameBase)
                {
                    /*
                     * 内容卡绕基名收片只允许**种子自己的物理目录**：跨 roots 的目录里
                     * 可能是已解出的内容物树（PNG/MP4）；对每个文件做 FileNature.Inspect
                     * 会把 UI 线程拖死（2026-10-27 真机 19 组时卡在 PNG 嗅探）。
                     */
                    if (!contentGrouping
                        || seedFormat == ArchiveFixer.Detection.VolumeContentFormat.Unknown
                        || string.IsNullOrWhiteSpace(seedDirectory)
                        || !string.Equals(
                            SafePathHelper.GetFullPathSafe(Path.GetDirectoryName(path) ?? string.Empty),
                            SafePathHelper.GetFullPathSafe(seedDirectory),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    ArchiveFixer.Detection.FileNatureCard contentCard;

                    try
                    {
                        contentCard = ArchiveFixer.Detection.FileNature.Inspect(path);
                    }
                    catch
                    {
                        continue;
                    }

                    if (contentCard.Format != seedFormat
                        || contentCard.Membership != ArchiveFixer.Detection.VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember)
                    {
                        continue;
                    }
                }

                /*
                 * ⛔ 2026-11-01 真机（2.exe / 2.part*.rar）：**父包/已自证完整的单卷文件不许混进子分卷组**。
                 * 它们和子组基名相同（都是 "2"）、族也相同（RAR），旧逻辑只查基名+族 ⇒ 父包被收进组。
                 * 判据只认内容事实：SingleComplete + ProvablyNotVolumeMember + 没有名字卷槽。
                 */
                if (IsProvablySingleCompleteNonMember(handoffByPath, path))
                {
                    continue;
                }

                /*
                 * 同基名跨目录仍然是一组：包装会把同一组分到兄弟目录。
                 * 目录不再是组键；是否同组由基名 + 内容族 + 完整性判据回答。
                 */

                if (seedFormat != ArchiveFixer.Detection.VolumeContentFormat.Unknown)
                {
                    ArchiveFixer.Detection.FileNatureCard? candidateCard = null;

                    try
                    {
                        candidateCard = ArchiveFixer.Detection.FileNature.Inspect(path);
                    }
                    catch
                    {
                        // 读不出内容不阻止补位；由族算法/完整性判据兜底。
                    }

                    ArchiveFixer.Detection.VolumeContentFormat candidateFormat =
                        candidateCard?.Format ?? ArchiveFixer.Detection.VolumeContentFormat.Unknown;

                    if (candidateFormat != ArchiveFixer.Detection.VolumeContentFormat.Unknown
                        && candidateFormat != seedFormat)
                    {
                        continue;
                    }

                    if (IsProvablySingleCompleteNonMember(candidateCard, path))
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
        /// 这一份是不是**自证完整单卷、且可证不是分卷成员**（父包 / 普通单卷 zip / 完整单卷 7z）。
        ///
        /// <para>⛔ 这类文件只能当自己的任务，绝不能当别人分卷组的一片 —— 真机 2026-11-01：
        /// `2.exe`（完整 RAR5）与它解出来的 `2.part1/2/3.rar` 基名同为 "2"，被收进同一组，
        /// 组永远判不出、过程物也删不掉。判据只认内容事实，不看名字像不像。</para>
        /// </summary>
        private static bool IsProvablySingleCompleteNonMember(
            ArchiveFixer.Detection.VolumeHandoffEntry? entry)
        {
            return entry != null
                && entry.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.SingleComplete
                && entry.Membership == ArchiveFixer.Detection.VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember
                && !entry.NameVolumeIndex.HasValue
                && !entry.FirstVolumeFlag;
        }

        private static bool IsProvablySingleCompleteNonMember(
            IReadOnlyDictionary<string, ArchiveFixer.Detection.VolumeHandoffEntry>? handoffByPath,
            string path)
        {
            return handoffByPath != null
                && handoffByPath.TryGetValue(path, out ArchiveFixer.Detection.VolumeHandoffEntry? entry)
                && IsProvablySingleCompleteNonMember(entry);
        }

        private static bool IsProvablySingleCompleteNonMember(
            ArchiveFixer.Detection.FileNatureCard? card,
            string path)
        {
            return card != null
                && card.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.SingleComplete
                && card.Membership == ArchiveFixer.Detection.VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember
                && !ArchiveFixer.Detection.VolumeGroupDetector.TryGetVolumeIndex(
                    Path.GetFileName(path)).HasValue;
        }

        private static bool IsProvablySingleCompleteNonMember(PieceFacts item)
        {
            if (item.Handoff != null)
            {
                return IsProvablySingleCompleteNonMember(item.Handoff);
            }

            return IsProvablySingleCompleteNonMember(item.Card, item.Path);
        }

        /// <summary>
        /// **同名同族但在不同目录的两个组**：按第一片自述整包分桶（内容事实，不按目录猜）。
        ///
        /// <para>只有一个自述 / 分不干净（任一片找不到唯一归属）⇒ 返回原样一组，
        /// 保持"同组跨目录合并"的既有行为；⛔ 判不出就不拆。</para>
        /// </summary>
        private static IReadOnlyList<List<string>> SplitPiecesByDeclaredGroup(
            List<string> pieces,
            IReadOnlyDictionary<string, ArchiveFixer.Detection.VolumeHandoffEntry>? handoffByPath)
        {
            if (handoffByPath == null || handoffByPath.Count == 0 || pieces.Count < 2)
            {
                return new[] { pieces };
            }

            var buckets = new List<(long DeclaredTotal, List<string> Paths)>();
            var others = new List<string>();

            foreach (string path in pieces)
            {
                if (handoffByPath.TryGetValue(path, out ArchiveFixer.Detection.VolumeHandoffEntry? entry)
                    && entry != null
                    && entry.DeclaredTotalBytes > 0
                    && (entry.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.VolumeFirstPiece
                        || entry.FirstVolumeFlag))
                {
                    int index = buckets.FindIndex(bucket => bucket.DeclaredTotal == entry.DeclaredTotalBytes);

                    if (index < 0)
                    {
                        buckets.Add((entry.DeclaredTotalBytes, new List<string> { path }));
                    }
                    else
                    {
                        buckets[index].Paths.Add(path);
                    }
                }
                else
                {
                    others.Add(path);
                }
            }

            if (buckets.Count < 2)
            {
                return new[] { pieces };
            }

            foreach (string path in others)
            {
                string directory = SafePathHelper.GetFullPathSafe(Path.GetDirectoryName(path) ?? string.Empty);
                long size = FileSizeOf(path);

                /*
                 * 归属证据①：与某一桶的首片**同目录**，且这样的桶唯一。
                 * 归属证据②：这一片的字节数恰好补齐某一桶的自述整包，且这样的桶唯一。
                 * 两条都判不出唯一 ⇒ 整组不拆（保持原行为）。
                 */
                var byDirectory = buckets
                    .Where(bucket => SafePathHelper.GetFullPathSafe(
                            Path.GetDirectoryName(bucket.Paths[0]) ?? string.Empty) == directory)
                    .ToList();

                if (byDirectory.Count == 1)
                {
                    byDirectory[0].Paths.Add(path);
                    continue;
                }

                if (byDirectory.Count > 1)
                {
                    return new[] { pieces };
                }

                var byExactFit = buckets
                    .Where(bucket => bucket.DeclaredTotal - SumFileSizes(bucket.Paths) == size)
                    .ToList();

                if (byExactFit.Count != 1)
                {
                    return new[] { pieces };
                }

                byExactFit[0].Paths.Add(path);
            }

            return buckets.Select(bucket => bucket.Paths).ToList();
        }

        private static long FileSizeOf(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        private static long SumFileSizes(IReadOnlyList<string> paths)
        {
            long sum = 0;

            foreach (string path in paths)
            {
                sum += FileSizeOf(path);
            }

            return sum;
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
             * 7z 分卷的片顺序：入口/首卷在前，其余按名字卷号；名字判不出再按内容卷号。
             * ⛔ 非均匀分卷下不能按大小排序：首卷可以比第二卷更大，也可以更小。
             */
            IReadOnlyList<string> OrderVolumePieces(IReadOnlyList<PieceFacts> pieces, string? entryPath)
            {
                return pieces
                    .OrderBy(item => !string.IsNullOrWhiteSpace(entryPath)
                                     && string.Equals(item.Path, entryPath, StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : 1)
                    .ThenBy(item => ArchiveFixer.Detection.VolumeGroupDetector.TryGetVolumeIndex(
                        Path.GetFileName(item.Path)) ?? int.MaxValue)
                    .ThenBy(item => item.VolumeNumber ?? int.MaxValue)
                    .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(item => item.Path)
                    .ToList();
            }

            /*
             * ⛔ 2026-11-01：单卷完整包（父包 / 普通单卷 zip）不是分卷组。
             * 没有名字卷槽、内容自证 SingleComplete + 可证非成员 ⇒ 直判 Unknown，
             * 绝不再给"名字卷标记连成 1..1"这种假 Complete。
             */
            if (items.Count == 1 && IsProvablySingleCompleteNonMember(items[0]))
            {
                return Finish(new GroupCandidate
                {
                    BaseName = baseName,
                    Format = format,
                    Pieces = new[] { items[0].Path },
                    EntryPath = items[0].Path,
                    SuggestedVerdict = GroupAnalysisVerdict.Unknown,
                    DeclaredPieceCount = declaredPieceCount,
                    ReceivedPieceCount = 1,
                    DeclaredTotalBytes = declaredTotalBytes,
                    ReceivedBytes = receivedBytes,
                    Evidence = "内容自证是完整单卷（可证不是分卷成员）⇒ 不是分卷组"
                });
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
                    Pieces = OrderVolumePieces(items, items.FirstOrDefault(item => item.IsFirstPiece)?.Path),
                    EntryPath = items.FirstOrDefault(item => item.IsFirstPiece)?.Path ?? string.Empty,
                    SuggestedVerdict = complete ? GroupAnalysisVerdict.Complete : GroupAnalysisVerdict.Missing,
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
                SuggestedVerdict = GroupAnalysisVerdict.Unknown,
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
                item.Card.HasZipLocalHeader,
                item.Card.CanonicalVolumeName,
                item.Card.FirstVolumeNameByName,
                item.Card.ArchiveBaseName);

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
            List<PieceFacts> ordered = format == ArchiveFixer.Detection.VolumeContentFormat.SevenZip
                ? items
                    .OrderBy(item => item.IsFirstPiece ? 0 : 1)
                    .ThenBy(item => ArchiveFixer.Detection.VolumeGroupDetector.TryGetVolumeIndex(
                        Path.GetFileName(item.Path)) ?? int.MaxValue)
                    .ThenBy(item => item.VolumeNumber ?? int.MaxValue)
                    .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : items
                    .OrderBy(item => item.VolumeNumber ?? int.MaxValue)
                    .ThenBy(item => item.Size)
                    .ToList();

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

            /*
             * ⛔ 2026-11-01：**每一片都必须有卷号/名字槽**才可能 Complete。
             * 真机 `2` 组：4 片里只有 `.part1/.part2` 有槽（另外两片没有槽），
             * 旧判据 `numbers.Count == expected` 只数有槽的片 ⇒ 判成 Complete，
             * 与建组那边的 `RarPart 判不出` 直接打架，回收也随之卡死。
             */
            if (numbers.Count != items.Count)
            {
                return new GroupCandidate
                {
                    BaseName = baseName,
                    Format = format,
                    Pieces = ordered.Select(item => item.Path).ToList(),
                    EntryPath = entry,
                    SuggestedVerdict = GroupAnalysisVerdict.Unknown,
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
                return new GroupCandidate
                {
                    BaseName = baseName,
                    Format = format,
                    Pieces = ordered.Select(item => item.Path).ToList(),
                    EntryPath = entry,
                    SuggestedVerdict = GroupAnalysisVerdict.Unknown,
                    MissingNumbers = Array.Empty<int>(),
                    DeclaredPieceCount = declaredPieceCount,
                    ReceivedPieceCount = items.Count,
                    DeclaredTotalBytes = declaredTotalBytes,
                    ReceivedBytes = receivedBytes,
                    Evidence = $"{evidenceSource}自述 {declaredPieceCount} 片 / 整包 {declaredTotalBytes} 字节，"
                        + $"实收 {items.Count} 片 / {receivedBytes} 字节 ⇒ 判不出，不能证明具体少哪几片"
                };
            }

            return new GroupCandidate
            {
                BaseName = baseName,
                Format = format,
                Pieces = ordered.Select(item => item.Path).ToList(),
                EntryPath = entry,
                SuggestedVerdict = contiguous ? GroupAnalysisVerdict.Complete : GroupAnalysisVerdict.Missing,
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
                return Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }
    }
}

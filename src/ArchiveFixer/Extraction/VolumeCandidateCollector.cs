using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using System.IO;

namespace ArchiveFixer.Extraction;

/// <summary>
/// **候选收集**（第二大步发现层的第 1 段）：从任意几份种子出发，把"可能属于同一组"的片
/// 收进候选池，并保持"同基名跨目录是一组、两个各自完整的同名组不合并"的既有口径。
///
/// <para>两个来源、同一套口径：① 种子来源（第一大步交接单 / 任意候选路径）：种子准入
/// （0 字节/内容物/可证完整单卷/父包一律不种）→ 按基名分桶 → 每个种子在自己那一层 +
/// 调用方给的 roots 里全树收片（物理身份去重）→ 同基名里按"第一片自述整包"分桶
/// （两个各自完整的同名组不能并成一个）→ 内容卡补位（内容已证明是这一族某卷、名字没进组的片，
/// 按缺卷号补进唯一匹配的组）；② 边界 resolver 来源 = 已经标准化的 prepared 组
/// （<see cref="CollectPrepared"/>，只收片、不再扫盘重排）。</para>
///
/// <para><b>不自带判据、不定序、不下结论</b>：收不收由 <see cref="VolumeGroupMembership"/> 的判据回答
/// （本类只调用，不复制）；定序在 <see cref="VolumeGroupPieceOrdering"/>、完整/缺片/判不出在
/// <see cref="VolumeCandidateVerdict"/>。⛔ 不收片以外的事：不改名、不搬、不删。</para>
///
/// <para><b>失败模型</b>：扫不动的目录/读不到的文件跳过（返回空集合），⛔ 不抛；
/// 收不到任何片就返回空列表，由调用方按"没有候选"处理。</para>
///
/// <para><b>生命周期</b>：纯静态、无状态；一次调用 = 一批种子 → 一批候选结论，
/// 不缓存、不落盘；候选的公开事实/内容卡仍由 <see cref="VolumeCandidateEvidence"/> 建。</para>
/// </summary>
internal static class VolumeCandidateCollector
{
    /// <summary>
    /// 全树组分析（只读）的收集阶段：<paramref name="seeds"/> 是任意几份文件（含中间卷、判不出的片），
    /// <paramref name="scanRoots"/> 是要扫的根目录（本批产物树 / 用户源目录）。
    /// </summary>
    public static List<VolumeCandidateDiscovery.VolumeCandidate> Collect(
        IEnumerable<string>? seeds,
        IEnumerable<string>? scanRoots,
        IReadOnlyDictionary<string, ArchiveFixer.Detection.VolumeHandoffEntry>? handoffByPath,
        IReadOnlyCollection<string>? excludedSources = null)
    {
        var result = new List<VolumeCandidateDiscovery.VolumeCandidate>();
        var excluded = new HashSet<string>(
            (excludedSources ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
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

        /*
         * ⛔ 2026-11-01 真机（2916/3100 的 风景01.mp4 + 风景02.mp4）：
         * 7z 首片自述整包 = 本片 + 同目录一份**无魔数**的尾片（字节精确补齐），
         * 但两片基名不同（风景01 / 风景02），永远进不了同一个基名桶。
         * ⇒ 在按基名分桶之前先做一次"字节补齐配对"，配出来的组直接产出候选，
         * 这两条路径不再参与后面的基名收片（避免又被同名首片合并成 Unknown）。
         */
        var byteFitConsumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string pairBaseName, List<string> pairPieces) in FormByteFitPairs(handoffByPath))
        {
            foreach (string piece in pairPieces)
            {
                byteFitConsumed.Add(piece);
            }

            VolumeCandidateDiscovery.VolumeCandidate pairCandidate =
                VolumeCandidateVerdict.Evaluate(pairBaseName, pairPieces, handoffByPath);
            pairCandidate.ByteFitPaired = true;
            result.Add(pairCandidate);
        }

        foreach (string seed in (seeds ?? Enumerable.Empty<string>())
                     .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)))
        {
            if (byteFitConsumed.Contains(seed))
            {
                continue;
            }

            /*
             * ⛔ 2026-11-01 真机：根任务的**源包本身**（`2.exe`，内容卡 Unknown、无卷槽）
             * 与它解出来的 `2.part*.rar` 基名同为 "2"，被收进子分卷组 ⇒ 组永远 Unknown。
             * 由交界处把"没有分卷证据的根任务源路径"传进来，这里一律不种、不收。
             */
            if (excluded.Contains(seed))
            {
                continue;
            }

            /*
             * 普通媒体内容物不是分卷种子（用户 2026-11-11：媒体前缀只服务单一完整包）。
             * 只认内容事实；判不出（裸切续卷伪装成 .png）照旧进候选。
             */
            if (VolumeGroupMembership.IsKnownNormalMedia(handoffByPath, seed)
                || VolumeGroupMembership.IsKnownContentOnDisk(seed))
            {
                continue;
            }

            // 0 字节不可能是归档/分卷的一片（真机：0 字节 *.7z.001 曾被快通道当完整组解一层）。
            if (VolumeCandidateEvidence.FileSizeOf(seed) <= 0)
            {
                continue;
            }

            /*
             * ⛔ 2026-11-01 真机（2.exe / 2.part*.rar）：**已经自证是完整单卷的文件，不许再当分卷组种子**。
             * 父包 2.exe 与它解出来的 2.part1/2/3.rar 基名都是 "2"，旧逻辑只比基名+族，
             * 于是父包被收进子分卷组 ⇒ 组永远判不出、过程物也删不掉。
             * 判据只认内容事实：BodyKind=SingleComplete + Membership=ProvablyNotVolumeMember + 没有名字卷槽。
             */
            if (handoffByPath != null
                && handoffByPath.TryGetValue(seed, out ArchiveFixer.Detection.VolumeHandoffEntry? seedHandoff)
                && VolumeGroupMembership.IsProvablySingleCompleteNonMember(seedHandoff))
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

                foreach (string piece in CollectPieces(seed, baseName, seedFormat, roots, handoffByPath, excluded))
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
             * 守门 `VolumeCandidateDiscoveryNonUniformTests.同名同族但在不同目录_必须分成两组`：
             * `a\组.7z.001` 自述 1200 字节、`b\组.7z.001` 自述 600 字节 ⇒ 这是两组。
             * ⛔ 只有"≥2 个互不相同的自述整包"时才分桶；任一片无法唯一归桶 ⇒ **不分**
             * （保持跨目录合并，交给族 resolver 判），⛔ 不猜。
             */
            foreach (List<string> cluster in SplitPiecesByDeclaredGroup(pieces, handoffByPath))
            {
                result.Add(VolumeCandidateVerdict.Evaluate(baseName, cluster, handoffByPath));
            }
        }

        EnrichGroupsWithContentMembers(result, handoffByPath);

        return result;
    }

    /// <summary>
    /// 内容卡补位：基名先分候选组之后，再把"内容已证明是这一族某卷、但名字/基名没进组"的片
    /// 按**缺卷号**补进唯一匹配的组。名字在这里只用于"同目录歧义消解"，不再作为入场券。
    ///
    /// <para>只补能证明的缺口：候选必须是 VolumeMember，卷号非空，且目标组当前正好缺这个号；
    /// 同目录同族有多个组同时缺这个号 ⇒ 判不出，不补。</para>
    /// </summary>
    private static void EnrichGroupsWithContentMembers(
        List<VolumeCandidateDiscovery.VolumeCandidate> groups,
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

            List<VolumeCandidateDiscovery.VolumeCandidate> targets = groups
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

            VolumeCandidateDiscovery.VolumeCandidate target = targets[0];
            var pieces = new List<string>(target.Pieces);

            if (pieces.Any(path => string.Equals(path, entry.Path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            pieces.Add(entry.Path);
            VolumeCandidateDiscovery.VolumeCandidate enriched =
                VolumeCandidateVerdict.Evaluate(target.BaseName, pieces, handoffByPath);

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
    /// 在扫描根里收"同基名、同内容族"的片（全树，去重按物理身份）。
    /// 内容族未知的片允许补位；已证实的异族片绝不并进这一组。
    /// </summary>
    private static List<string> CollectPieces(
        string seed,
        string baseName,
        ArchiveFixer.Detection.VolumeContentFormat seedFormat,
        IReadOnlyList<string> roots,
        IReadOnlyDictionary<string, ArchiveFixer.Detection.VolumeHandoffEntry>? handoffByPath,
        IReadOnlySet<string> excluded)
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
        bool contentGrouping = VolumeGroupMembership.HasSingleContentFirstInDirectory(seedDirectory, seedFormat);

        IEnumerable<string> source = scanRoots
            .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(root => SafeEnumerate(root));

        foreach (string path in source)
        {
            if (string.Equals(path, seed, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(path)
                || excluded.Contains(path)
                || VolumeCandidateEvidence.FileSizeOf(path) <= 0)
            {
                continue;
            }

            bool sameBase = string.Equals(
                ArchiveFixer.Helpers.FileNameHelper.SecondStepBaseName(path),
                baseName,
                StringComparison.OrdinalIgnoreCase);

            /*
             * 同基名的普通媒体（例如 336.png 与 336.7z.001 同基名）不是这一组的一片。
             * ⛔ 判据只认内容事实：媒体结构正好占满整个文件且没有尾部归档；
             * 媒体前缀的完整单卷（EmbeddedOffset > 0）与裸切续卷伪装成 .png 都不在这一档。
             */
            if (sameBase
                && (VolumeGroupMembership.IsKnownNormalMedia(handoffByPath, path)
                    || VolumeGroupMembership.IsKnownContentOnDisk(path)))
            {
                continue;
            }

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
            if (VolumeGroupMembership.IsProvablySingleCompleteNonMember(handoffByPath, path))
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

                if (VolumeGroupMembership.IsProvablySingleCompleteNonMember(candidateCard, path))
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
    /// **字节补齐配对**（2026-11-01 真机 2916/3100 的 `风景01.mp4` + `风景02.mp4`）：
    ///
    /// <para>7z 首片自述"整包应该有多少字节"，而尾片既没有魔数、名字也没有卷标记，
    /// 两片基名还不一样（`风景01` / `风景02`）⇒ 按基名收片永远配不到一起。</para>
    ///
    /// <para>判据（严格、不猜）：首片 `VolumeFirstPiece` + `DeclaredTotalBytes &gt; 自身`；
    /// 同目录里**恰好一份**没有魔数、没有名字卷槽、不是完整单卷的文件，其字节数正好等于
    /// `DeclaredTotalBytes - 首片字节` ⇒ 这两片就是一组（第一片 + 唯一的尾片）。
    /// 同目录有多份候选 / 已有同目录同基名的账本兄弟 ⇒ 不配对（交给正常基名路径）。</para>
    /// </summary>
    private static IEnumerable<(string BaseName, List<string> Pieces)> FormByteFitPairs(
        IReadOnlyDictionary<string, ArchiveFixer.Detection.VolumeHandoffEntry>? handoffByPath)
    {
        if (handoffByPath == null || handoffByPath.Count == 0)
        {
            yield break;
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ArchiveFixer.Detection.VolumeHandoffEntry entry
                 in handoffByPath.Values
                     .Where(item => item != null)
                     .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(entry.Path)
                || used.Contains(entry.Path)
                || entry.BodyKind != ArchiveFixer.Detection.ArchiveBodyKind.VolumeFirstPiece
                || entry.Size <= 0
                || entry.DeclaredTotalBytes <= entry.Size
                || entry.Membership == ArchiveFixer.Detection.VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember)
            {
                continue;
            }

            string baseName = !string.IsNullOrWhiteSpace(entry.BaseName)
                ? entry.BaseName
                : ResolveGroupBaseName(entry.Path);

            string directory = Path.GetDirectoryName(entry.Path) ?? string.Empty;

            if (baseName.Length == 0 || directory.Length == 0 || !Directory.Exists(directory))
            {
                continue;
            }

            // 同目录同基名已有别的账本片 ⇒ 这是正常的基名组，配对逻辑让位。
            bool hasSameDirectorySibling = handoffByPath.Values.Any(other =>
                other != null
                && !string.Equals(other.Path, entry.Path, StringComparison.OrdinalIgnoreCase)
                && string.Equals(other.BaseName, baseName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    SafePathHelper.GetFullPathSafe(Path.GetDirectoryName(other.Path) ?? string.Empty),
                    SafePathHelper.GetFullPathSafe(directory),
                    StringComparison.OrdinalIgnoreCase));

            if (hasSameDirectorySibling)
            {
                continue;
            }

            long remainder = entry.DeclaredTotalBytes - entry.Size;
            var matches = new List<string>();

            try
            {
                foreach (string path in Directory.EnumerateFiles(directory))
                {
                    if (string.Equals(path, entry.Path, StringComparison.OrdinalIgnoreCase)
                        || used.Contains(path))
                    {
                        continue;
                    }

                    /*
                     * 尾片可能已经由第一大步登记进账本（本次真机就是这样：风景02.mp4
                     * 作为 Unknown 任务进了账本）。只接受"纯 Unknown 尾片"这一种账本事实：
                     * 没有卷号/名字槽/首卷标记/自述整包，也不是可证完整单卷。
                     */
                    if (handoffByPath.TryGetValue(path, out ArchiveFixer.Detection.VolumeHandoffEntry? tailHandoff)
                        && (!VolumeGroupMembership.IsByteFitTailCandidate(tailHandoff)
                            || VolumeGroupMembership.IsKnownNormalMedia(tailHandoff)))
                    {
                        continue;
                    }

                    if (ContentAdmission.Probe(
                            path,
                            ContentAdmissionScope.VolumeCandidate,
                            deep: true).Class == FileContentClass.KnownContent)
                    {
                        continue;
                    }

                    long size;

                    try
                    {
                        size = new FileInfo(path).Length;
                    }
                    catch
                    {
                        continue;
                    }

                    if (size != remainder)
                    {
                        continue;
                    }

                    ArchiveFixer.Detection.FileNatureCard card;

                    try
                    {
                        card = ArchiveFixer.Detection.FileNature.Inspect(path);
                    }
                    catch
                    {
                        continue;
                    }

                    if (card.Format != ArchiveFixer.Detection.VolumeContentFormat.Unknown
                        || card.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.SingleComplete
                        || VolumeGroupMembership.IsKnownNormalMedia(card)
                        || card.IsKnownContent
                        || VolumeGroupMembership.IsProvablySingleCompleteNonMember(card, path)
                        || LegacyVolumeNames.TryGetIndex(
                            FileNameHelper.GetFileName(path)).HasValue)
                    {
                        continue;
                    }

                    matches.Add(path);
                }
            }
            catch
            {
                continue;
            }

            if (matches.Count != 1)
            {
                continue;
            }

            used.Add(entry.Path);
            used.Add(matches[0]);

            yield return (baseName, new List<string> { entry.Path, matches[0] });
        }
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
            long size = VolumeCandidateEvidence.FileSizeOf(path);

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

    private static long SumFileSizes(IReadOnlyList<string> paths)
    {
        long sum = 0;

        foreach (string path in paths)
        {
            sum += VolumeCandidateEvidence.FileSizeOf(path);
        }

        return sum;
    }

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

    /// <summary>
    /// **吃边界 resolver 的 prepared 组**（共用骨架的第二个候选来源）：
    /// 只把已经标准化的片收成候选，⛔ 不再扫用户源目录、⛔ 不再从脏名重排。
    ///
    /// <para>`SizeAnomaly` 且没填 `Pieces` 的组只描述"结构异常"，⛔ 不可提交，
    /// 也不能变成 0 片候选去替换发现层已经算对的那一组。</para>
    /// </summary>
    public static List<VolumeCandidateDiscovery.VolumeCandidate> CollectPrepared(
        IReadOnlyList<PreparedVolumeGroup>? groups)
    {
        var result = new List<VolumeCandidateDiscovery.VolumeCandidate>();

        foreach (PreparedVolumeGroup group in groups ?? Array.Empty<PreparedVolumeGroup>())
        {
            if (group == null || string.IsNullOrWhiteSpace(group.BaseName))
            {
                continue;
            }

            /*
             * SizeAnomaly 的 prepared 组按设计不填 Pieces（它只描述“结构异常”）。
             * 这种组不可提交，不能变成 0 片候选去替换发现层已经算对的那一组。
             */
            if (group.State == PreparedGroupState.SizeAnomaly && group.Pieces.Count == 0)
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

            var candidate = new VolumeCandidateDiscovery.VolumeCandidate
            {
                BaseName = group.BaseName,
                Format = FormatForFamily(group.Family),
                Pieces = pieces,
                EntryPath = entryPath,
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
}

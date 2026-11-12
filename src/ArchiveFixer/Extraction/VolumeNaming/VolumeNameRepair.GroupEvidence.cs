using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction
{
    public static partial class VolumeNameRepair
    {
        private static string DescribeStillMissing(int diskCount, IReadOnlyList<string> missing) =>
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.SpannedZipDisksStillMissingFormat,
                diskCount,
                string.Join("、", missing));

        /// <summary>
        /// 找卷的**基准** = 手上**最浅的那一卷**（用户 2026-10-05 口径：「你要以一开始的分卷文件为准，
        /// 不要以 001 为准，否则这时的第一层父文件夹就探测不到」）。
        ///
        /// <para>判据只读**路径事实**：目录层数最少的那一份；层数相同优先取**入口自己**
        /// （入口是调用方手上那一份，语义最稳），再按卷号、名字定序 —— 于是同一批候选每次得到**同一个**基准
        /// （⛔ 不随机、⛔ 不依赖枚举顺序）。判不出（池子空）⇒ 退回入口。</para>
        /// </summary>
        private static string ResolveShallowestAnchor(string entryPath, IReadOnlyList<GatherPiece> pool)
        {
            GatherPiece? shallowest = pool
                .OrderBy(p => Depth(p.Path))
                .ThenBy(p => SamePath(p.Path, entryPath) ? 0 : 1)
                .ThenBy(p => p.Index)
                .ThenBy(p => SafeFileName(p.Path), StringComparer.OrdinalIgnoreCase)
                .Cast<GatherPiece?>()
                .FirstOrDefault();

            return shallowest?.Path ?? entryPath;

            static int Depth(string path)
            {
                int count = 0;
                string current = SafePathHelper.GetFullPathSafe(path);

                while (!string.IsNullOrEmpty(current))
                {
                    count++;

                    string? parent = Path.GetDirectoryName(current);

                    if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    current = parent;
                }

                return count;
            }
        }

        /// <summary>一次"这一组齐不齐"的读数（纯事实）。</summary>
        private readonly record struct GroupEvidence(
            bool Complete,
            bool NeedsNoWork,
            bool BytesBudgetKnown,
            bool Ambiguous,
            IReadOnlyList<string> MissingNames,
            string Detail)
        {
            public string Describe() => Detail;
        }

        /// <summary>
        /// 期望基名：把池里那几片**只按文件名**喂给 <see cref="VolumeGroupDetector.Group"/>
        /// （目录无所谓 —— 这一问只想知道"这一组叫什么"），取含入口那一份的那个组的基名。
        ///
        /// <para>⛔ 与下面那道闸门**同一条代码路径**：那样才不会被"组键档不剥卷标记"这类档位差异坑到
        /// （实测旧的归组键档对 `HK.7z.001` 原样返回 `HK.7z.001`，
        /// 而组里算出来的是 `HK.7z`）。判不出来 ⇒ 返回 null（调用方整档不做）。</para>
        /// </summary>
        private static string? ResolveGatherBaseName(IReadOnlyList<GatherPiece> pool, string entryName)
        {
            try
            {
                var named = pool
                    .Select(piece => new VolumeCandidate { Path = SafeFileName(piece.Path), Size = piece.Size })
                    .ToList();

                /*
                 * ===== 卡优先（安全版）：卡**只剔除"可证不是分卷成员"的那些** =====
                 * 用户 2026-10-17：「底层识别一直是最强的」。但实测证明两件事：
                 * ① 卡**不能当组键标签**（卡上 `ArchiveBaseName`=「HK」，组键=「HK.7z」，形状不同）；
                 * ② 也**不能让"卡挑出来的子集"去分组**（子集的标签会变，`CrossLayerVolumeGatherTests`
                 *    `完整单卷包…` 与 `凑不齐…` 两条当场红）。
                 * ⇒ 卡的正当用法只剩一种**不可能误伤**的：内容可证"我不是这一族任何分卷"的那几份
                 * （`VolumeMembershipVerdict.ProvablyNotVolumeMember`）从桶里剔掉；其余照旧。
                 * 剔除后不足 2 份 ⇒ 一个都不剔（老口径）。
                 */
                var cardFiltered = new List<VolumeCandidate>();

                foreach (VolumeCandidate candidate in named)
                {
                    bool provablyNotMember;

                    try
                    {
                        provablyNotMember = ArchiveFixer.Detection.FileNature.Inspect(candidate.Path).Membership
                            == ArchiveFixer.Detection.VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember;
                    }
                    catch
                    {
                        provablyNotMember = false;
                    }

                    if (!provablyNotMember)
                    {
                        cardFiltered.Add(candidate);
                    }
                }

                if (cardFiltered.Count >= 2 && cardFiltered.Count < named.Count)
                {
                    named = cardFiltered;
                }

                /*
                 * ⚠ 2026-10-16（真机 `14141414` 的收拢探针逐字：池子里 3 份都在、第 1 卷也在，却
                 * `推不出这一组的期望基名（那几份的名字/尺寸进不了任何一个桶）`）：入口那一份的名字
                 * **可能不是它那一族的规范卷名** —— 现场入口叫 `打包111-L2.rar`（老式编号族的入口形状），
                 * 而同组兄弟是 `打包111-L2.part2.rar` / `打包111-L2.part3.rar` ⇒ 按名字分桶时"族"对不上
                 * ⇒ 整档判不出，而这一组其实**齐了**。
                 *
                 * ⇒ 分桶前**只给入口那一份**换一个用于分桶的名字：同组里有一片叫 `<基名>.partN.rar`（N≥2）
                 * ⇒ 这一组是 partN 族 ⇒ 入口按 `<基名>.part1.rar` 参与分桶（名字仍由既有命名出口拼，
                 * 拿兄弟那片当样本）。⛔ **只改这一趟分桶用的名字，盘上一个字节都不动**；
                 * 对不上的组（入口本来就是规范名 / 兄弟不在 partN 族）⇒ 与加这条之前逐字相同。
                 */
                string entryBucketName = entryName;
                string entryStem = FileNameHelper.GetArchiveBaseName(entryName);
                string? partFamilySample = entryStem.Length == 0
                    ? null
                    : pool
                        .Select(piece => SafeFileName(piece.Path))
                        .FirstOrDefault(name =>
                            name.StartsWith(entryStem + ".part", StringComparison.OrdinalIgnoreCase)
                            && LegacyVolumeNames.TryGetIndex(name) > 1);

                if (partFamilySample != null
                    && VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex(
                        partFamilySample,
                        1,
                        out string firstVolumeBucketName)
                    && firstVolumeBucketName.Length > 0)
                {
                    entryBucketName = firstVolumeBucketName;
                }

                for (int index = 0; index < named.Count; index++)
                {
                    if (string.Equals(named[index].Path, entryName, StringComparison.OrdinalIgnoreCase))
                    {
                        named[index] = new VolumeCandidate { Path = entryBucketName, Size = named[index].Size };
                    }
                }

                foreach (VolumeGroup group in VolumeGroupDetector.Group(named))
                {
                    if (group.Volumes.Any(v => string.Equals(
                            SafeFileName(v.Path),
                            entryBucketName,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        return group.BaseName;
                    }
                }
            }
            catch
            {
                // 判不出来 ⇒ 什么都不做（调用方退回"照旧让引擎去判"）。
            }

            /*
             * ===== 名字分桶判不出时，让**底层识别卡**再答一次（用户 2026-10-17：名字只兜底）=====
             * 卡上凡是自报"我是这一族的一片"（`CardSaysItIsOneOfTheGroup`）且带 `ArchiveBaseName` 的，
             * 取它们的基名；**唯一一致** ⇒ 就是这一组的基名。
             * ⚠ 为什么只做兜底而不是首选：实测把它放到名字分桶之前会打红
             * `SiblingFolderVolumeGatherTests` 的"基准/窗口"那几条（卡上的基名与
             * `VolumeGroupDetector.Group` 算出来的组键不是一个形状），所以顺序必须是"名字先、卡兜底"。
             */
            try
            {
                var cardBases = new List<string>();

                foreach (GatherPiece piece in pool)
                {
                    try
                    {
                        ArchiveFixer.Detection.FileNatureCard pieceCard =
                            ArchiveFixer.Detection.FileNature.Inspect(piece.Path);

                        if (ArchiveFixer.Detection.VolumePieceFacts.CardSaysItIsOneOfTheGroup(pieceCard)
                            && pieceCard.ArchiveBaseName.Length > 0)
                        {
                            cardBases.Add(pieceCard.ArchiveBaseName);
                        }
                    }
                    catch
                    {
                        // 读不出来就少一张卡。
                    }
                }

                if (cardBases.Count > 0
                    && cardBases.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                {
                    return cardBases[0];
                }
            }
            catch
            {
                // 卡也判不出 ⇒ 判不出。
            }

            return null;
        }

        /// <summary>
        /// 判"这几片是不是完整的一组"：⛔ 判据只用既有出口，⛔ 不另写尺子。
        ///
        /// <list type="number">
        /// <item><description><b>结构自洽</b>：把这几片按"**移动之后的样子**"虚拟出来
        /// （路径 = 入口那一层 + 规范卷名，尺寸 = 各片实际字节数）喂给
        /// <see cref="TryConfirmSelfConsistentVolumeGroup(IEnumerable{VolumeCandidate}, string, string)"/>。
        /// 为什么必须虚拟：真实的几片躺在不同目录里，而 <see cref="VolumeGroupDetector.Group"/>
        /// 按"目录 + 基名 + 族"分桶 —— 直接喂真路径的话每一片都会自成一个"从 1 起、无缺号"的完整桶，
        /// 这条闸门就成了恒真。</description></item>
        /// <item><description><b>字节数硬证据</b>（7z）：起始头自述的整包长度 vs 这几片之和。
        /// 少了 ⇒ 缺卷（报"还差 N 字节"）；多了 ⇒ 只接受既有那把"唯一说得通的缩池"
        /// （<see cref="TryShrinkToExactByteBudget"/>），缩不出来 ⇒ <c>Ambiguous</c>（判不出，不免试）。</description></item>
        /// <item><description><b>名字上的洞</b>：同一命名空间里配一次组，<c>MissingVolumeNames</c> 就是
        /// "能点名的缺卷"。⚠ 名字里没有"共几卷"这个信息，末卷之后的缺号**一个字都不编**。</description></item>
        /// </list>
        /// </summary>
        private static GroupEvidence EvaluateGatherGroup(
            IReadOnlyList<GatherPiece> pieces,
            string entryPath,
            string entryDirectory,
            string expectedBaseName,
            IReadOnlyDictionary<int, string> targetNames,
            VolumeContentFormat format)
        {
            var missing = new List<string>();

            if (pieces.Count == 0)
            {
                return new GroupEvidence(false, false, false, true, missing, StatusText.CrossLayerGatherNoCandidates);
            }

            List<VolumeCandidate> Project() => pieces
                .Where(piece => targetNames.ContainsKey(piece.Index))
                .Select(piece => new VolumeCandidate
                {
                    Path = Path.Combine(entryDirectory, targetNames[piece.Index]),
                    Size = piece.Size
                })
                .ToList();

            List<VolumeCandidate> virtualCandidates = Project();

            bool selfConsistent = TryConfirmSelfConsistentVolumeGroup(
                virtualCandidates, entryPath, expectedBaseName);

            // 名字上的洞（同一命名空间里配组）：能从名字上看出来的缺卷逐个点名。
            try
            {
                foreach (VolumeGroup group in VolumeGroupDetector.Group(Project()))
                {
                    if (group.Volumes.Any(v => string.Equals(
                            SafeFileName(v.Path),
                            SafeFileName(entryPath),
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        missing.AddRange(group.MissingVolumeNames);
                    }
                }
            }
            catch
            {
                // 配不出来就是"点不了名"，不影响任何结论。
            }

            bool needsNoWork = pieces.All(piece =>
                targetNames.ContainsKey(piece.Index) &&
                SamePath(piece.Path, Path.Combine(entryDirectory, targetNames[piece.Index])));

            if (format != VolumeContentFormat.SevenZip)
            {
                /*
                 * 其他族没有"整包多长"这个自述数 ⇒ 只有名字这一条证据（与今天的行为一致：
                 * 完整一组就解、不完整就让引擎去报它自己那句"分卷缺失"）。
                 */
                return new GroupEvidence(
                    selfConsistent,
                    needsNoWork,
                    BytesBudgetKnown: false,
                    Ambiguous: false,
                    missing,
                    selfConsistent
                        ? StatusText.CrossLayerGatherSelfConsistent
                        : StatusText.CrossLayerGatherNotSelfConsistent);
            }

            SevenZipByteBudget budget = SevenZipStartHeader.Measure(
                entryPath,
                pieces.Select(piece => piece.Path).ToList());

            if (!budget.Known)
            {
                // 读不出起始头（入口这一份没有 7z 魔数）⇒ 这条证据缺席 ⇒ 照旧走名字那条。
                return new GroupEvidence(
                    selfConsistent,
                    needsNoWork,
                    BytesBudgetKnown: false,
                    Ambiguous: false,
                    missing,
                    StatusText.CrossLayerGatherNoStartHeader);
            }

            if (budget.Verdict == SevenZipByteBudgetVerdict.Excess)
            {
                List<VolumeCandidate> others = pieces
                    .Where(piece => !SamePath(piece.Path, entryPath))
                    .Select(piece => new VolumeCandidate { Path = piece.Path, Size = piece.Size })
                    .ToList();

                IReadOnlyList<VolumeCandidate>? shrunk = TryShrinkToExactByteBudget(
                    entryPath, others, budget.ExpectedBytes);

                if (shrunk == null)
                {
                    // 判不出多出来的是哪一份 ⇒ 不下任何结论（照旧让引擎去试）。
                    return new GroupEvidence(
                        selfConsistent,
                        needsNoWork,
                        BytesBudgetKnown: true,
                        Ambiguous: true,
                        missing,
                        budget.Describe());
                }

                budget = SevenZipStartHeader.Measure(
                    entryPath,
                    new[] { entryPath }.Concat(shrunk.Select(c => c.Path)));
            }

            return new GroupEvidence(
                selfConsistent && budget.Verdict == SevenZipByteBudgetVerdict.Exact,
                needsNoWork,
                BytesBudgetKnown: true,
                Ambiguous: false,
                missing,
                budget.Describe());
        }

        /// <summary>
        /// 造"把这几卷收进入口那一层"的计划。执行体仍是 <see cref="TryApply"/>；
        /// <paramref name="blocker"/> = 为什么收不成（跨盘 / 目标名被占 / 没什么可做）。
        /// </summary>
        private static VolumeNameRepairPlan? TryBuildGatherPlan(
            string entryPath,
            IReadOnlyList<GatherPiece> pieces,
            IReadOnlyDictionary<int, string> targetNames,
            out string blocker)
        {
            blocker = string.Empty;

            string entryDirectory = Path.GetDirectoryName(entryPath) ?? string.Empty;
            var slots = new List<VolumeGroupSlot>();
            var names = new List<string>();
            string? primary = null;

            foreach (GatherPiece piece in pieces.OrderBy(p => p.Index))
            {
                if (!targetNames.TryGetValue(piece.Index, out string? canonical))
                {
                    blocker = StatusText.CrossLayerGatherNoCandidates;
                    return null;
                }

                slots.Add(new VolumeGroupSlot { Path = piece.Path, Number = piece.Index });
                names.Add(canonical);

                if (primary == null && !SamePath(piece.Path, Path.Combine(entryDirectory, canonical)))
                {
                    // 计划里的"当前项"必须指向**真要动的那一卷**（不然 TryApply 一进来就判"名字本来就对"）。
                    primary = piece.Path;
                }
            }

            if (primary == null)
            {
                blocker = StatusText.VolumeRepairAlreadyStandard;
                return null;
            }

            var order = new VolumeGroupOrder { Confirmed = true, Slots = slots };
            VolumeNameRepairPlan plan = BuildPlanFromOrder(primary, order, names, entryPath);

            if (!plan.CanRepair)
            {
                blocker = plan.Reason;
                return null;
            }

            return plan;
        }

        /// <summary>
        /// 收卷那一行日志要说的内容（"收了几卷、**从哪**收到哪"）：逐卷写
        /// <c>旧名（从 &lt;那一层&gt; 收来）→ 新名</c>；原地只改名的写 <c>（原地改名）</c>。
        ///
        /// <para>⛔ 只写**目录名**（<c>layer-001</c> 这种），不写完整路径 —— §8 隐私红线与既有日志口径
        /// （"内层包已移入其余物 …（&lt;那一层&gt; 那一层）"）同一条。</para>
        /// </summary>
        private static string DescribeGatherPlan(VolumeNameRepairPlan plan, string entryDirectory)
        {
            var parts = new List<string>();

            foreach (VolumeRepairItem item in plan.Items ?? Array.Empty<VolumeRepairItem>())
            {
                if (string.Equals(item.CurrentPath, item.TargetPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string folder = DescribeSourceLayer(item.CurrentPath);
                bool renamed = !string.Equals(
                    item.CurrentFileName,
                    item.SuggestedFileName,
                    StringComparison.OrdinalIgnoreCase);

                bool sameDirectory = string.Equals(
                    Path.GetDirectoryName(item.CurrentPath),
                    entryDirectory,
                    StringComparison.OrdinalIgnoreCase);

                string where = sameDirectory ? "原地改名" : $"从 {folder} 收来";

                parts.Add(renamed
                    ? $"{item.CurrentFileName} → {item.SuggestedFileName}（{where}）"
                    : $"{item.CurrentFileName}（{where}）");
            }

            return parts.Count == 0 ? StatusText.VolumeRepairAlreadyStandard : string.Join("；", parts);
        }

        /// <summary>
        /// 这一卷**原来在哪一层**（写进收卷那一行日志的"从 &lt;哪&gt; 收来"）。
        ///
        /// <para>⛔ 为什么不直接写"最后一段目录名"（用户 2026-10-05 真机）：层产物目录固定叫
        /// <c>output</c>（<c>…\recursive\&lt;任务id&gt;\layer-001\output</c>），于是两卷分别来自
        /// <c>layer-001</c> 与 <c>layer-002</c> 时，日志两边都印成"从 output 收来" ——
        /// 用户根本看不出是哪一层（他点名要求："要印出是哪一层"）。</para>
        ///
        /// <para>判据只读路径事实：最后一段是层目录（<see cref="ExtractionWorkspace.LayerDirectoryPrefix"/>）
        /// 就用它；否则往上看一层，那一层是层目录就用它；都不是 ⇒ 退回最后一段目录名（⛔ 不编）。</para>
        /// </summary>
        private static string DescribeSourceLayer(string filePath)
        {
            string directory = SafePathHelper.GetFullPathSafe(Path.GetDirectoryName(filePath) ?? string.Empty);
            string leaf = SafeFileName(directory);

            if (leaf.StartsWith(ExtractionWorkspace.LayerDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return leaf;
            }

            string parent = SafeFileName(Path.GetDirectoryName(directory) ?? string.Empty);

            return parent.StartsWith(ExtractionWorkspace.LayerDirectoryPrefix, StringComparison.OrdinalIgnoreCase)
                ? parent
                : leaf;
        }

        private static bool SameDirectory(string a, string b) =>
            string.Equals(
                Path.GetDirectoryName(SafePathHelper.GetFullPathSafe(a)),
                Path.GetDirectoryName(SafePathHelper.GetFullPathSafe(b)),
                StringComparison.OrdinalIgnoreCase);

    }
}
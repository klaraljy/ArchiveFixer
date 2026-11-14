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
using static ArchiveFixer.Extraction.VolumeNameRepair;
using static ArchiveFixer.Extraction.VolumeNameRestorer;

namespace ArchiveFixer.Extraction
{
    internal static partial class VolumeNamePlanner
    {

        /// <summary>
        /// **内容里带卷号**那一档（RAR 与跨盘 ZIP，用户 2026-09-29 任务）。
        ///
        /// <para>与 7z 那条最大的不同：这里**不靠猜、不用试开** —— 卷号是内容自己说的，
        /// 所以只要"整组连成 1..N"这一条自洽就够。认不出来（头截断 / CRC 对不上 / 绝对卷号连不成 1..N /
        /// RAR 1.5–4.x（RAR4）的基数两种解释都成立 / 跨盘 zip 片数 ≥ 3）一律拒绝，原样不动。</para>
        /// </summary>
        private static VolumeNameRepairPlan PlanByNumberedContent(
            string path,
            IEnumerable<VolumeCandidate>? filesInDirectory,
            VolumeNamingFamily family,
            Detection.VolumeContentFormat format)
        {
            var readings = new List<Detection.VolumeNumberReading>();

            foreach (VolumeCandidate candidate in filesInDirectory ?? Array.Empty<VolumeCandidate>())
            {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.Path))
                {
                    continue;
                }

                readings.Add(Detection.VolumeNumberFromContent.Read(candidate.Path));
            }

            Detection.VolumeGroupOrder order = Detection.VolumeNumberFromContent.ResolveGroup(
                path,
                readings,
                requireCurrentFirstVolume: false);

            if (!order.Confirmed || order.Count < 2)
            {
                return Cannot(path, DescribeNumberedContentFail(order.Fail, order.Detail));
            }

            /*
             * 基名从"这一组里最像标准名的那一卷"推：跨盘 zip 取末片（它的标准名就是 `X.zip`），
             * RAR 取第 1 卷（`X.part1.rar`）。⛔ 名字只影响"改完像不像人写的"，正确性由卷号与"绝不覆盖"钉着。
             */
            string stemSource = order.Slots[0].Path;
            string tailPath = order.Slots[^1].Path;
            string stem;

            if (family == VolumeNamingFamily.ZipSpanned)
            {
                if (!TryDeriveSpannedStem(
                        order.Slots.Select(slot => slot.Path).ToList(),
                        tailPath,
                        order.Count,
                        out stem))
                {
                    return Cannot(path, StatusText.VolumeRepairNoSuggestion);
                }
            }
            else if (!Detection.VolumeNumberFromContent.TryDeriveStem(stemSource, format, out stem))
            {
                return Cannot(path, StatusText.VolumeRepairNoSuggestion);
            }

            IReadOnlyList<string> targetNames = Detection.VolumeNumberFromContent.BuildStandardNames(stem, family, order.Count);

            if (targetNames.Count != order.Count)
            {
                return Cannot(path, StatusText.VolumeRepairNoSuggestion);
            }

            /*
             * 入口那一卷（引擎要打开的那一份，同时也是"计划里当前这一项"）：
             *   · 跨盘 zip = 末片（`X.zip`）—— 中央目录在它身上，7-Zip 从它开始拼这一组；
             *   · RAR = **调用方手上那一卷**（方案 §4 阶段 C）：族标记 `partN` 已经把"第 1 卷是谁"
             *     写死了，计划的目标名仍从第 1 卷推（上面那句），⛔ 不因为入口不是第 1 卷就少改一卷。
             *     ⚠ 老写法在这里传的是第 1 卷 ⇒ 手上是 `.part2.rar` 时计划里的"当前项"指向别人的名字，
             *     调用方（`TryApply` / 任务路径同步）拿它当入口就会指到一个"名字本来就对"的卷上。
             */
            string entryVolumePath = family == VolumeNamingFamily.ZipSpanned ? tailPath : path;

            return BuildPlanFromOrder(path, order, targetNames, entryVolumePath: entryVolumePath);
        }

        /// <summary>
        /// 7z：内容认第一卷 + 同目录尺寸规律（或**两卷形状**，用户 2026-09-29 放宽）
        /// + **起始头自述的整包字节数**（2026-10-03 阶段 B）+ 硬链接试开验证（决定性）。
        /// </summary>
        /// <param name="anchorPath">
        /// **锚点** = 带 7z 起始头的那一卷 = 第 1 卷。⛔ 基名只从它推（方案 §4 阶段 B 的 (b)）：
        /// 名字丢了的那一片（<c>111</c>）身上没有任何"这一组叫什么"的信息，拿它推基名等于把标准名建立在
        /// 一个名字已经丢了的东西上。⛔ 引擎也只从这里开（找兄弟卷只看入口旁边那一层）。
        /// </param>
        /// <param name="entryPath">
        /// 调用方**手上这一卷**（可能就等于锚点，也可能就是那一片名字丢了的中间卷 —— 真机形状）。
        /// 它只用于"这句话该怎么说"（计划的 <c>CurrentPath</c> 与日志里的名字），⛔ 不参与定名。
        /// </param>
        private static async Task<VolumeNameRepairPlan> PlanSevenZipByContentAsync(
            string anchorPath,
            string entryPath,
            IEnumerable<VolumeCandidate>? filesInDirectory,
            Engines.IArchiveEngine engine,
            string? workRootDirectory,
            CancellationToken cancellationToken)
        {
            Detection.VolumeContentFormat format = Detection.VolumeContentFormat.SevenZip;

            IReadOnlyList<VolumeCandidate> candidates =
                Detection.VolumeContentInference.BuildCandidates(anchorPath, filesInDirectory);

            /*
             * 「值不值得试开一次」的闸门 = **两张门票取或**（用户 2026-09-29 放宽，理由写在
             * `VolumeContentInference.HasTwoVolumeShapeEvidence` 的注释里）：
             *   ① 尺寸规律：有与第一卷等长的满片；
             *   ② 两卷形状：一个等长的都没有 —— 那正是"第一卷满片 + 末卷是余量"这一组
             *      （现场 `amb909.7.01` 2 GiB + `amb909.z.2` 1.89 GB）。老口径只认 ①，
             *      于是这一组报「同目录里也没有找到像后续卷的文件」，而兄弟卷就躺在同一个目录里。
             *
             * ⛔ 放宽的只是**"敢不敢试一次"**这一道：成不成立仍然只由下面的硬链接试开回答，
             * 试不出来就一个字节都不动（判据仍然只有一处，就是这两张门票 + 试开）。
             */
            if (!Detection.VolumeContentInference.HasVolumeSizePattern(anchorPath, candidates)
                && !Detection.VolumeContentInference.HasTwoVolumeShapeEvidence(anchorPath, candidates))
            {
                return Cannot(entryPath, StatusText.VolumeRepairContentNoSizePattern);
            }

            /*
             * **起始头那条硬证据**（2026-10-03 阶段 B 的 (d)，方案 §1 表格 ①）：
             * `32 + NextHeaderOffset + NextHeaderSize` = 整包应当有的字节数，**与顺序无关**。
             * 拿它与"手上这几卷加起来"比，三档各有各的用处（⛔ 全都只读，⛔ 不拿它猜"哪一卷是第几卷"）：
             *   · **小于** ⇒ 缺卷，如实报"还差多少字节"，一次引擎都不调；
             *   · **正好** ⇒ 字节数这一条证据成立（"整组齐了"），顺序仍由名字 / 试开回答；
             *   · **大于** ⇒ 候选池里混进了不属于这一组的文件 ⇒ **缩池重来**（只接受唯一说得通的那种缩法）。
             */
            Detection.SevenZipByteBudget budget = Detection.SevenZipStartHeader.Measure(
                anchorPath,
                new[] { anchorPath }.Concat(candidates.Select(c => c.Path)));

            IReadOnlyList<VolumeCandidate> pool = candidates;

            if (budget.Known && budget.Verdict == Detection.SevenZipByteBudgetVerdict.Excess)
            {
                IReadOnlyList<VolumeCandidate>? shrunk = TryShrinkToExactByteBudget(
                    anchorPath, candidates, budget.ExpectedBytes);

                if (shrunk == null)
                {
                    return Cannot(
                        entryPath,
                        string.Format(
                            StatusText.VolumeRepairContentBytesExcessFormat,
                            budget.ExpectedBytes,
                            budget.ActualBytes,
                            budget.DifferenceBytes),
                        byteBudgetMismatch: true);
                }

                pool = shrunk;

                budget = Detection.SevenZipStartHeader.Measure(
                    anchorPath,
                    new[] { anchorPath }.Concat(pool.Select(c => c.Path)));
            }

            if (budget.Known && budget.Verdict == Detection.SevenZipByteBudgetVerdict.Short)
            {
                return Cannot(
                    entryPath,
                    string.Format(
                        StatusText.VolumeRepairContentBytesMissingFormat,
                        budget.ExpectedBytes,
                        budget.ActualBytes,
                        budget.DifferenceBytes),
                    byteBudgetMismatch: true);
            }

            IReadOnlyList<IReadOnlyList<VolumeCandidate>> orderings =
                Detection.VolumeContentInference.BuildOrderings(anchorPath, pool);

            var verifier = new VolumeProbeVerifier(engine);
            VolumeProbeOutcome probe = await verifier
                .VerifyAsync(anchorPath, orderings, cancellationToken, workRootDirectory)
                .ConfigureAwait(false);

            if (!probe.Confirmed || probe.OrderedVolumes.Count < 2)
            {
                /*
                 * ⛔ "没试"与"试过不成立"必须分开说（用户 2026-09-30 红线：工作区只准设在解压的地方）：
                 * 这条路**没有目标目录** ⇒ 没有工作区根 ⇒ 一次都不试开（见 VolumeProbeVerifier）。
                 * 那一档只能如实报"无法确认"，⛔ 不许写成"试开没通过"；结论照旧是**不改名**。
                 * `TrialAttempted` 也跟着如实走 —— 它的唯一含义就是"试开真跑过"。
                 */
                return probe.Attempted
                    ? Cannot(
                        entryPath,
                        string.Format(StatusText.VolumeRepairContentProbeFailedFormat, probe.Reason),
                        trialAttempted: true)
                    : Cannot(
                        entryPath,
                        string.Format(StatusText.VolumeRepairNoProbeFormat, probe.Reason),
                        trialAttempted: false);
            }

            /*
             * 基名从**锚点**推（⛔ 不从"手上这一卷"推）：试开把顺序钉住了，第 1 卷就是锚点自己
             * （`VerifyAsync` 永远把锚点放在第一位），它的名字才是这一组标准名的来源。
             */
            string stemSource = probe.OrderedVolumes[0].Path;

            if (!Detection.VolumeNumberFromContent.TryDeriveStem(stemSource, format, out string stem))
            {
                return Cannot(entryPath, StatusText.VolumeRepairNoSuggestion, trialAttempted: true);
            }

            IReadOnlyList<string> targetNames = Detection.VolumeNumberFromContent.BuildStandardNames(
                stem, VolumeNamingFamily.SevenZipNumbered, probe.OrderedVolumes.Count);

            string directory = Path.GetDirectoryName(stemSource) ?? string.Empty;
            var items = new List<VolumeRepairItem>();
            int primary = -1;

            for (int i = 0; i < probe.OrderedVolumes.Count && i < targetNames.Count; i++)
            {
                string source = probe.OrderedVolumes[i].Path;
                string target = Path.Combine(directory, targetNames[i]);

                if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                {
                    /*
                     * 这一卷的名字**本来就是对的**（真机那一档的第 1 卷 `111.7z.001` 与末卷 `111.7z.003`）：
                     * 留在组里（调用方要按整份计划同步任务路径），但⛔ 不改它 —— 老写法在这里整份拒掉，
                     * 于是"只差中间那一卷名字"的组一个名字都改不成。
                     */
                    items.Add(new VolumeRepairItem
                    {
                        CurrentPath = source,
                        CurrentFileName = Path.GetFileName(source),
                        SuggestedFileName = targetNames[i],
                        TargetPath = target
                    });

                    continue;
                }

                // ⛔ 绝不覆盖：任何一个目标名被占，整组不改。
                if (File.Exists(target))
                {
                    return Cannot(
                        entryPath,
                        string.Format(StatusText.VolumeRepairTargetTakenFormat, targetNames[i]),
                        trialAttempted: true);
                }

                items.Add(new VolumeRepairItem
                {
                    CurrentPath = source,
                    CurrentFileName = Path.GetFileName(source),
                    SuggestedFileName = targetNames[i],
                    TargetPath = target
                });

                if (primary < 0 || SamePath(source, entryPath))
                {
                    primary = i;
                }
            }

            if (primary < 0)
            {
                // 组里没有一卷真的要改（名字本来就都对）⇒ 什么都不做。
                return Cannot(entryPath, StatusText.VolumeRepairAlreadyStandard, trialAttempted: true);
            }

            VolumeRepairItem self = items[primary];

            return new VolumeNameRepairPlan
            {
                CanRepair = true,
                CurrentPath = self.CurrentPath,
                CurrentFileName = self.CurrentFileName,
                SuggestedFileName = self.SuggestedFileName,
                TargetPath = self.TargetPath,
                Siblings = items.Select(i => i.CurrentFileName).ToList(),
                Items = items,
                TrialAttempted = true,
                ProbeNeedsPassword = probe.NeedsPassword
            };
        }

        /// <summary>
        /// 「手上的字节数**多于**整包自述的字节数」⇒ **缩池重来**（方案 §4 阶段 B 的 (d) 第三档）。
        ///
        /// <para>7z 的 <c>-v</c> 切法给了一个很硬的形状：**除末片外每一片彼此等大**，
        /// 所以"多出来的"无非两种：多算了那个更短的候选（它不是末片），或者多算了一个满片
        /// （它不是这一组的卷）。于是只试这两种**各去掉一份**的缩法，而且要求**只有一种**能对上整包字节数 ——
        /// 两种都对得上、或者一种都对不上 ⇒ 返回 null（判不出 ⇒ 上层如实报"多出多少字节"、一个字节不动）。</para>
        ///
        /// <para>⚠ 满片有 <b>两份以上</b>时"去掉哪一份"无从知道（它们一样大），所以那一档也返回 null ——
        /// ⛔ 不挑一个去试。</para>
        /// </summary>
        internal static IReadOnlyList<VolumeCandidate>? TryShrinkToExactByteBudget(
            string anchorPath,
            IReadOnlyList<VolumeCandidate> candidates,
            long expectedBytes)
        {
            long anchorSize = LengthOf(anchorPath);

            if (anchorSize <= 0)
            {
                return null;
            }

            List<VolumeCandidate> full = candidates.Where(c => c.Size == anchorSize).ToList();
            List<VolumeCandidate> rest = candidates.Where(c => c.Size != anchorSize).ToList();

            var exact = new List<List<VolumeCandidate>>();

            // ① 去掉"最像末片的那一个"（更短的那些里最大的那一个）。
            VolumeCandidate? partial = rest
                .Where(c => c.Size < anchorSize)
                .OrderByDescending(c => c.Size)
                .ThenBy(c => SafeFileName(c.Path), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (partial != null)
            {
                List<VolumeCandidate> withoutPartial = candidates
                    .Where(c => !SamePath(c.Path, partial.Path))
                    .ToList();

                if (SumBytes(anchorPath, withoutPartial) == expectedBytes)
                {
                    exact.Add(withoutPartial);
                }
            }

            // ② 去掉一个满片（只有"满片恰好一份"时才知道去掉的是哪一个）。
            if (full.Count == 1)
            {
                List<VolumeCandidate> withoutFull = candidates
                    .Where(c => !SamePath(c.Path, full[0].Path))
                    .ToList();

                if (withoutFull.Count > 0 && SumBytes(anchorPath, withoutFull) == expectedBytes)
                {
                    exact.Add(withoutFull);
                }
            }

            if (exact.Count != 1)
            {
                return null;
            }

            return exact[0];
        }

        /// <summary>锚点 + 这几份的字节数之和（读不到任何一份 ⇒ 0，调用方按"对不上"处理）。</summary>
        private static long SumBytes(string anchorPath, IReadOnlyList<VolumeCandidate> volumes)
        {
            long total = LengthOf(anchorPath);

            if (total <= 0)
            {
                return 0;
            }

            foreach (VolumeCandidate volume in volumes)
            {
                long size = LengthOf(volume.Path);

                if (size <= 0)
                {
                    return 0;
                }

                total += size;
            }

            return total;
        }

        /// <summary>
        /// 由"内容定好序的整组"造计划：卷号升序、逐卷算目标名，⛔ 目标名被占 → 整组不改。
        ///
        /// <para><b>散在两层目录里的组会被"收"到一起</b>（用户 2026-10-01：「绝大多数只会在一个父文件夹和
        /// 父文件夹的同级子文件夹当中」）：目标目录一律取**引擎要打开的那一卷所在的目录**
        /// （跨盘 zip 是末片 <c>X.zip</c>，RAR / 7z 是第 1 卷）——
        /// 引擎找兄弟卷**只看入口文件旁边那一层**，散着放即使名字都对也解不开（实测 7-Zip 就是这样）。
        /// ⛔ 只搬**同一卷**上的（跨盘搬不动，也绝不做跨盘复制）：任何一卷与目标目录不同卷 ⇒ 整组不改。</para>
        ///
        /// <para>要改的第一卷（<c>CurrentPath</c>）优先取**调用方手上那一卷**（它在组里时），
        /// 否则取第一个真要改名的 —— <see cref="TryApply"/> 拿这一卷当入口，指到一个"名字本来就对"的卷上
        /// 会直接判"问题不在名字上"，整组就白算了。</para>
        /// </summary>
        internal static VolumeNameRepairPlan BuildPlanFromOrder(
            string path,
            Detection.VolumeGroupOrder order,
            IReadOnlyList<string> targetNames,
            string entryVolumePath,
            bool trialAttempted = false,
            bool probeNeedsPassword = false)
        {
            // 目标目录 = 入口那一卷所在的那一层（引擎就在那儿找兄弟卷）。
            string targetDirectory = Path.GetDirectoryName(entryVolumePath)
                ?? Path.GetDirectoryName(path)
                ?? string.Empty;
            var items = new List<VolumeRepairItem>();
            int primary = -1;
            int gathered = 0;

            for (int i = 0; i < order.Count; i++)
            {
                string source = order.Slots[i].Path;

                if (!Detection.VolumeContentInference.IsSameVolumeRoot(source, targetDirectory))
                {
                    // 跨盘：搬不过去，也⛔ 绝不复制大文件 ⇒ 整组不改（判不出就不做）。
                    return Cannot(path, string.Format(
                        StatusText.VolumeRepairGatherAcrossVolumeFormat,
                        SafeFileName(source),
                        SafeFileName(entryVolumePath)));
                }

                string directory = Path.GetDirectoryName(source) ?? targetDirectory;

                if (!string.Equals(directory, targetDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    gathered++;
                }

                string target = Path.Combine(targetDirectory, targetNames[i]);

                if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                {
                    // 这一卷的名字本来就是对的：留在组里（调用方要按它同步任务路径），但不改。
                    items.Add(new VolumeRepairItem
                    {
                        CurrentPath = source,
                        CurrentFileName = Path.GetFileName(source),
                        SuggestedFileName = targetNames[i],
                        TargetPath = target
                    });

                    continue;
                }

                if (File.Exists(target))
                {
                    return Cannot(path, string.Format(StatusText.VolumeRepairTargetTakenFormat, targetNames[i]));
                }

                items.Add(new VolumeRepairItem
                {
                    CurrentPath = source,
                    CurrentFileName = Path.GetFileName(source),
                    SuggestedFileName = targetNames[i],
                    TargetPath = target
                });

                if (primary < 0 || string.Equals(source, path, StringComparison.OrdinalIgnoreCase))
                {
                    primary = i;
                }
            }

            if (primary < 0)
            {
                return Cannot(path, StatusText.VolumeRepairAlreadyStandard);
            }

            VolumeRepairItem self = items[primary];

            return new VolumeNameRepairPlan
            {
                CanRepair = true,
                CurrentPath = self.CurrentPath,
                CurrentFileName = self.CurrentFileName,
                SuggestedFileName = self.SuggestedFileName,
                TargetPath = self.TargetPath,
                Siblings = items.Select(i => i.CurrentFileName).ToList(),
                Items = items,
                TrialAttempted = trialAttempted,
                ProbeNeedsPassword = probeNeedsPassword,
                GatheredVolumes = gathered
            };
        }

        /// <summary>内容级认不出来时给用户的那句话（判据在 <see cref="Detection.VolumeNumberFromContent"/>，文案只有这一处）。</summary>
        private static string DescribeNumberedContentFail(Detection.VolumeNumberFail fail, int detail) => fail switch
        {
            Detection.VolumeNumberFail.RarOldNumbering => StatusText.VolumeRepairContentRarOldNumbering,
            Detection.VolumeNumberFail.RarFirstVolumeMismatch => StatusText.VolumeRepairContentRarFirstVolumeMismatch,
            Detection.VolumeNumberFail.GroupBaseAmbiguous => StatusText.VolumeRepairContentRarBaseAmbiguous,
            Detection.VolumeNumberFail.ZipSingleDisk => StatusText.VolumeRepairContentZipSingleDisk,
            Detection.VolumeNumberFail.ZipTooManyDisks =>
                string.Format(StatusText.VolumeRepairContentZipTooManyDisksFormat, detail),
            Detection.VolumeNumberFail.ZipPartsMissing =>
                string.Format(StatusText.VolumeRepairContentZipPartsMissingFormat, detail),
            Detection.VolumeNumberFail.CurrentNotFirstVolume => StatusText.VolumeRepairNotFirstVolume,
            Detection.VolumeNumberFail.CurrentNotInGroup => StatusText.VolumeRepairContentCurrentNotInGroup,
            Detection.VolumeNumberFail.GroupIncomplete or Detection.VolumeNumberFail.GroupNotContiguous =>
                string.Format(StatusText.VolumeRepairContentGroupNotContiguousFormat, detail),
            _ => StatusText.VolumeRepairContentNotAVolumeMember
        };
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **第二大步的组事实服务**（2026-10-24 重建，用户口径：一组一个不可变 Unit、唯一写者）。
    ///
    /// <list type="number">
    /// <item><description>唯一创建点：<see cref="TryCommit"/> —— 只有族、源层、入口、片清单都齐的
    /// **可证完整**组才建 Unit；族判不出/证据不足 ⇒ 返回 null，一个字节都不动。</description></item>
    /// <item><description>唯一 owner：建 Unit 时写一次 <see cref="VolumeGroupUnit.OwnerTaskFileName"/>，
    /// 之后任何人再改都会由 Unit 自己抛异常。</description></item>
    /// <item><description>源层冻结：<see cref="VolumeGroupUnit.SourceLayer"/> 只在建组那一刻写入，
    /// 其余物/工作区永远不能当入口层（构造前这里先挡一次）。</description></item>
    /// <item><description>生命周期：批首 <see cref="Reset"/>；同一条一键链跨轮保留。</description></item>
    /// </list>
    ///
    /// <para>⛔ 这里不读文件、不猜名字、不建链接；链接/解压/收片由调用方按 Unit 执行。</para>
    /// </summary>
    public sealed class VolumeGroupService
    {
        private readonly Dictionary<string, VolumeGroupUnit> _units =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>已接线的族算法（P1 先接 RAR；其它族未接 ⇒ 保持 P0 行为，不判、不命名）。</summary>
        private readonly Dictionary<VolumeNamingFamily, IVolumeFamilyAlgorithm> _algorithms =
            new()
            {
                [VolumeNamingFamily.RarPart] = new RarFamilyAlgorithm(),
                [VolumeNamingFamily.ZipSpanned] = new ZipSpannedFamilyAlgorithm(),
                [VolumeNamingFamily.ZipNumbered] = new ZipNumberedFamilyAlgorithm(),
                [VolumeNamingFamily.SevenZipNumbered] = new SevenZipFamilyAlgorithm()
            };

        /// <summary>本批/本链已经登记的组（只读视图）。</summary>
        public IReadOnlyCollection<VolumeGroupUnit> Units => _units.Values;

        /// <summary>批首清账（与交接账同一处调用）。</summary>
        public void Reset()
        {
            _units.Clear();
        }

        /// <summary>按“基名 + 族”找已经登记的组（同基名多组时返回第一个；调用方本来就不用它区分）。</summary>
        public VolumeGroupUnit? Find(string baseName, VolumeNamingFamily? family)
        {
            if (string.IsNullOrWhiteSpace(baseName) || family == null)
            {
                return null;
            }

            return _units.Values.FirstOrDefault(unit =>
                string.Equals(unit.BaseName, baseName, StringComparison.OrdinalIgnoreCase)
                && unit.Family == family.Value);
        }

        /// <summary>按唯一 owner 任务文件名找回这一组（收尾用；⛔ 不用“再算一遍”。）</summary>
        public VolumeGroupUnit? FindByOwner(string? ownerTaskFileName)
        {
            if (string.IsNullOrWhiteSpace(ownerTaskFileName))
            {
                return null;
            }

            return _units.Values.FirstOrDefault(unit =>
                string.Equals(unit.OwnerTaskFileName, ownerTaskFileName, StringComparison.Ordinal));
        }

        /// <summary>
        /// **按任务全路径找回它所属的组**（同名文件在不同目录时唯一可靠的入口）。
        ///
        /// <para>⛔ 2026-11-01 真机：2916/3100 各有一个 `风景01.mp4`；只按文件名找组，
        /// 第二单会命中第一组的单元 ⇒ 第一组链接被删两次、第二组链接永远不清。
        /// 先比入口/片的**全路径**，找不到才退回文件名（兼容入口改名/链接名）。</para>
        /// </summary>
        public VolumeGroupUnit? FindForTask(string? taskCurrentPath, string? ownerTaskFileName)
        {
            if (!string.IsNullOrWhiteSpace(taskCurrentPath))
            {
                VolumeGroupUnit? byPath = _units.Values.FirstOrDefault(unit =>
                    unit != null
                    && ((!string.IsNullOrWhiteSpace(unit.EntryPath)
                         && ArchiveFixer.Helpers.SafePathHelper.PathEquals(unit.EntryPath, taskCurrentPath))
                        || unit.Pieces.Any(piece =>
                            !string.IsNullOrWhiteSpace(piece.Path)
                            && ArchiveFixer.Helpers.SafePathHelper.PathEquals(piece.Path, taskCurrentPath))));

                if (byPath != null)
                {
                    return byPath;
                }
            }

            return FindByOwner(ownerTaskFileName);
        }

        /// <summary>链接完成后回写（唯一写者；只追加本次真建出来的链接名）。</summary>
        public void MarkLinked(VolumeGroupUnit? unit, IReadOnlyList<string>? linkedNames)
        {
            if (unit == null)
            {
                return;
            }

            unit.LinkedNames = linkedNames ?? Array.Empty<string>();
            unit.State = VolumeGroupState.Linked;
        }

        /// <summary>
        /// **唯一建组出口**：证据齐、可证完整、族已定 ⇒ 建一次 Unit 并返回；否则 null（什么都不做）。
        /// 同一组重复调用只返回同一份 Unit，⛔ 不覆盖、不改 owner、不重置状态。
        /// </summary>
        public VolumeGroupUnit? TryCommit(
            string baseName,
            VolumeNamingFamily? family,
            string sourceLayer,
            string entryPath,
            string? ownerTaskFileName,
            IReadOnlyList<VolumeFacts>? pieces,
            IReadOnlyList<int>? missingNumbers,
            string evidence,
            int? declaredPieceCount,
            long? declaredTotalBytes,
            string outputLayer,
            IReadOnlyList<string>? confirmedOrder = null,
            bool standardNameFastPath = false)
        {
            if (string.IsNullOrWhiteSpace(baseName)
                || family == null
                || string.IsNullOrWhiteSpace(sourceLayer)
                || string.IsNullOrWhiteSpace(entryPath)
                || pieces == null
                || pieces.Count == 0)
            {
                return null;
            }

            /*
             * 源层红线（用户六条之二）：链产出/暂存的片不能把其余物 / 工作区当入口层。
             * 但用户**明确从其余物导入**的原件，那一层就是用户的源层；不能因为目录名带
             * “其余物”就把整组拒掉（2026-10-26 真机：9953 就在 其余物 下被拒）。
             */
            bool allUserImported = pieces.All(piece =>
                piece.FromHandoff
                && piece.Source == ArchiveFixer.Detection.VolumeHandoffSource.UserImported);

            if (IsForbiddenLayer(sourceLayer) && !allUserImported)
            {
                return null;
            }

            string key = BuildKey(baseName, family.Value, sourceLayer, entryPath);

            if (_units.TryGetValue(key, out VolumeGroupUnit? existing))
            {
                /*
                 * Unknown / Missing 不是终态；Complete / OwnerAssigned 在**还没链接/解压之前**
                 * 也不是终态 —— 候选事实会变（改名、补片、首卷标记后到）。
                 * 真机 21212121：第一轮事实把伪装片认成第 1 卷，建成 Complete；第二轮真首卷的
                 * 首卷标记才到，缓存挡住 ⇒ owner 永远指着错的那一片。
                 * ⛔ 已经 Linked/Extracting/Succeeded/Failed 的组不重算（那才是定局）。
                 */
                bool factsChanged = !SamePieces(existing.Pieces, pieces);
                bool beforeLink = existing.State == VolumeGroupState.Complete
                    || existing.State == VolumeGroupState.OwnerAssigned
                    || existing.State == VolumeGroupState.Linked;

                if (existing.State == VolumeGroupState.Unknown
                    || existing.State == VolumeGroupState.Missing
                    || (beforeLink && factsChanged))
                {
                    _units.Remove(key);
                }
                else
                {
                    /*
                     * 先建组、后建任务那一档：任务出现后只**补写一次** owner（⛔ 不换 owner、不重置状态）。
                     * 组事实/规范名计划早就定了，不因为任务晚到而重算。
                     */
                    if (!string.IsNullOrWhiteSpace(ownerTaskFileName)
                        && string.IsNullOrWhiteSpace(existing.OwnerTaskFileName)
                        && (existing.State == VolumeGroupState.Complete
                            || existing.State == VolumeGroupState.OwnerAssigned))
                    {
                        existing.OwnerTaskFileName = ownerTaskFileName;
                        existing.State = VolumeGroupState.OwnerAssigned;
                    }

                    return existing;
                }
            }

            var unit = new VolumeGroupUnit(baseName, family, sourceLayer, entryPath)
            {
                State = VolumeGroupState.Complete,
                Pieces = pieces,
                MissingNumbers = missingNumbers ?? Array.Empty<int>(),
                DeclaredPieceCount = declaredPieceCount,
                DeclaredTotalBytes = declaredTotalBytes,
                ConfirmedOrder = confirmedOrder,
                Verdict = evidence,
                OutputLayer = outputLayer
            };

            /*
             * 族已接线 ⇒ 由族算法回答“顺序 / 片数 / 完整性 / 规范名计划”。
             * ⛔ 只有 Complete 才继续；Missing/Unknown 把 Unit 留在账上（判不出就不动），
             * 由调用方负责“不点名、不链接、不解压”。
             */
            if (standardNameFastPath)
            {
                unit.OrderedPieces = pieces.Select(piece => piece.Path).ToList();
                unit.NamePlan = null;
                unit.MissingNumbers = Array.Empty<int>();
                unit.Verdict = "标准名快通道：按入口直接试解，不判内容完整性";
                unit.State = VolumeGroupState.Complete;
            }
            else if (_algorithms.TryGetValue(family.Value, out IVolumeFamilyAlgorithm? algorithm))
            {
                FamilyResult result = algorithm.Evaluate(unit);

                unit.OrderedPieces = result.OrderedPieces;
                unit.NamePlan = result.NamePlan;
                unit.MissingNumbers = result.MissingNumbers;
                unit.Verdict = result.Evidence;
                unit.State = result.Verdict switch
                {
                    VolumeCandidateDiscovery.VolumeCandidateVerdict.Complete => VolumeGroupState.Complete,
                    VolumeCandidateDiscovery.VolumeCandidateVerdict.Missing => VolumeGroupState.Missing,
                    _ => VolumeGroupState.Unknown
                };

                if (unit.State != VolumeGroupState.Complete)
                {
                    _units[key] = unit;
                    return unit;
                }
            }

            if (!string.IsNullOrWhiteSpace(ownerTaskFileName))
            {
                unit.OwnerTaskFileName = ownerTaskFileName;
                unit.State = VolumeGroupState.OwnerAssigned;
            }

            _units[key] = unit;
            return unit;
        }

        /// <summary>
        /// **prepared 组的建组入口**（共用骨架）：边界 resolver 已经给出顺序/标准名/结论，
        /// 这里只把结论登记成 Unit；⛔ 不再调用族算法重排、⛔ 不再读盘。
        /// </summary>
        public VolumeGroupUnit? TryCommitPrepared(
            PreparedVolumeGroup? prepared,
            string? ownerTaskFileName,
            string outputLayer)
        {
            if (prepared == null
                || string.IsNullOrWhiteSpace(prepared.BaseName)
                || prepared.Family == null
                || prepared.Pieces.Count == 0
                || string.IsNullOrWhiteSpace(prepared.OriginalSourceLayer))
            {
                return null;
            }

            string key = BuildKey(prepared.BaseName, prepared.Family.Value, prepared.OriginalSourceLayer, prepared.OriginalEntryPath);

            List<VolumeFacts> pieces = prepared.Pieces
                .Select(piece => new VolumeFacts(
                        piece.OriginalPath,
                        piece.Size,
                        FormatForFamily(prepared.Family),
                        ArchiveBodyKind.Unknown,
                        null,
                        false,
                        VolumeNumberFromContent.VolumeMembershipVerdict.Unknown,
                        VolumeNumberFail.NotAnArchive,
                        0,
                        0,
                        null,
                        prepared.OriginalSourceLayer,
                        piece.Source,
                        true,
                        CanonicalVolumeName: piece.CanonicalName,
                        ArchiveBaseName: prepared.BaseName))
                .ToList();

            if (_units.TryGetValue(key, out VolumeGroupUnit? existing))
            {
                /*
                 * ⚠ 2026-11-01（真机 901000018 / 901000027）：第一轮账本只认出 2 片 ⇒ 登记成 Unknown；
                 * 第二轮又产出两片、prepared 已经是 **Complete**，可老写法一律 `return existing`
                 * ⇒ 永远拿回第一轮那本"判不出"的旧账，组一次都不点名、一次引擎都不调。
                 *
                 * 判据：**没进入不可逆阶段**（没链接 / 没解 / 没成功 / 没失败）的旧 Unit，
                 * 只要新事实不同、或者旧结论本来就是"判不出/缺片/结构异常"，就按新 prepared 重建；
                 * 已经 Linked/Extracting/Succeeded/Failed 的是定局，⛔ 不推翻。
                 */
                bool locked = existing.State is VolumeGroupState.Linked
                    or VolumeGroupState.Extracting
                    or VolumeGroupState.Succeeded
                    or VolumeGroupState.Failed;

                if (locked)
                {
                    return existing;
                }

                bool sameFacts = SamePieces(existing.Pieces, pieces);
                bool undecided = existing.State is VolumeGroupState.Unknown
                    or VolumeGroupState.Missing
                    or VolumeGroupState.SizeAnomaly
                    or VolumeGroupState.Candidate;

                if (!sameFacts || undecided)
                {
                    _units.Remove(key);
                }
                else
                {
                    // 先建组、后建任务那一档：任务出现后只**补写一次** owner（⛔ 不换 owner、不重置状态）。
                    if (!string.IsNullOrWhiteSpace(ownerTaskFileName)
                        && string.IsNullOrWhiteSpace(existing.OwnerTaskFileName)
                        && existing.State == VolumeGroupState.Complete)
                    {
                        existing.OwnerTaskFileName = ownerTaskFileName;
                        existing.State = VolumeGroupState.OwnerAssigned;
                    }

                    return existing;
                }
            }

            var unit = new VolumeGroupUnit(
                prepared.BaseName,
                prepared.Family,
                prepared.OriginalSourceLayer,
                string.IsNullOrWhiteSpace(prepared.CanonicalEntryPath)
                    ? prepared.OriginalEntryPath
                    : prepared.CanonicalEntryPath)
            {
                State = MapPreparedState(prepared.State),
                Pieces = pieces,
                OrderedPieces = prepared.Pieces
                    .Select(piece => string.IsNullOrWhiteSpace(prepared.EntryLayer)
                        ? piece.OriginalPath
                        : System.IO.Path.Combine(prepared.EntryLayer, piece.CanonicalName))
                    .ToList(),
                ConfirmedOrder = prepared.Pieces
                    .Select(piece => string.IsNullOrWhiteSpace(prepared.EntryLayer)
                        ? piece.OriginalPath
                        : System.IO.Path.Combine(prepared.EntryLayer, piece.CanonicalName))
                    .ToList(),
                MissingNumbers = prepared.MissingNumbers,
                Verdict = prepared.Describe(),
                OutputLayer = outputLayer
            };

            if (!string.IsNullOrWhiteSpace(ownerTaskFileName)
                && unit.State == VolumeGroupState.Complete)
            {
                unit.OwnerTaskFileName = ownerTaskFileName;
                unit.State = VolumeGroupState.OwnerAssigned;
            }

            _units[key] = unit;
            return unit;
        }

        /// <summary>prepared 结论是结构异常时，登记一次可见状态（不做任何盘上动作）。</summary>
        public void MarkSizeAnomaly(VolumeGroupUnit? unit, string detail)
        {
            if (unit == null)
            {
                return;
            }

            unit.State = VolumeGroupState.SizeAnomaly;
            unit.Verdict = detail ?? string.Empty;
        }

        private static VolumeContentFormat FormatForFamily(VolumeNamingFamily? family)
        {
            return family switch
            {
                VolumeNamingFamily.SevenZipNumbered => VolumeContentFormat.SevenZip,
                VolumeNamingFamily.RarPart => VolumeContentFormat.Rar,
                VolumeNamingFamily.ZipSpanned or VolumeNamingFamily.ZipNumbered => VolumeContentFormat.Zip,
                _ => VolumeContentFormat.Unknown
            };
        }

        private static VolumeGroupState MapPreparedState(PreparedGroupState state)
        {
            return state switch
            {
                PreparedGroupState.Complete => VolumeGroupState.Complete,
                PreparedGroupState.Missing => VolumeGroupState.Missing,
                PreparedGroupState.SizeAnomaly => VolumeGroupState.SizeAnomaly,
                _ => VolumeGroupState.Unknown
            };
        }

        /// <summary>按 Unit 回写状态（唯一写者；⛔ 调用方不许直接改 Unit 的 State）。</summary>
        public void MarkState(VolumeGroupUnit? unit, VolumeGroupState state, string? verdict = null)
        {
            if (unit == null)
            {
                return;
            }

            unit.State = state;

            if (!string.IsNullOrWhiteSpace(verdict))
            {
                unit.Verdict = verdict;
            }
        }

        /// <summary>两组候选的“原始事实”是否逐片相同（路径/卷号/首卷/内容身份/ZIP 首末片证据）。</summary>
        private static bool SamePieces(
            IReadOnlyList<VolumeFacts> left,
            IReadOnlyList<VolumeFacts> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            List<VolumeFacts> orderedLeft = left
                .OrderBy(piece => piece.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<VolumeFacts> orderedRight = right
                .OrderBy(piece => piece.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int index = 0; index < orderedLeft.Count; index++)
            {
                VolumeFacts a = orderedLeft[index];
                VolumeFacts b = orderedRight[index];

                if (!string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase)
                    || a.VolumeNumber != b.VolumeNumber
                    || a.FirstVolumeFlag != b.FirstVolumeFlag
                    || a.BodyKind != b.BodyKind
                    || a.HasZipEndRecord != b.HasZipEndRecord
                    || a.ZipEndRecordMeansSplit != b.ZipEndRecordMeansSplit
                    || a.HasZipLocalHeader != b.HasZipLocalHeader)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 单元键 = 基名 + 族 + **源层**。
        ///
        /// <para>⛔ 2026-11-01：同基名的两组可以各自独立存在于不同目录（真机 2916/3100 的
        /// `风景01.mp4`）；只按“基名+族”做键会把后一组顶掉/并掉。源层是组的物理出处，
        /// 同一逻辑组只会来自同一入口层，故用它做区分不会拆散正常的跨目录组。</para>
        /// </summary>
        private static string BuildKey(string baseName, VolumeNamingFamily family, string? sourceLayer, string? entryPath) =>
            baseName + "|" + family.ToString()
            + "|" + (string.IsNullOrWhiteSpace(sourceLayer) ? string.Empty : sourceLayer)
            + "|" + (string.IsNullOrWhiteSpace(entryPath) ? string.Empty : entryPath);

        /// <summary>其余物 / 工作区 / 暂存目录不能当组的入口层（只读路径判断，不碰盘）。</summary>
        private static bool IsForbiddenLayer(string layer)
        {
            string normalized = layer.Replace('/', '\\');

            return normalized.IndexOf("\\.ArchiveFixer.work", StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf("\\其余物", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}

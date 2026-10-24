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
                [VolumeNamingFamily.RarPart] = new RarFamilyAlgorithm()
            };

        /// <summary>本批/本链已经登记的组（只读视图）。</summary>
        public IReadOnlyCollection<VolumeGroupUnit> Units => _units.Values;

        /// <summary>批首清账（与交接账同一处调用）。</summary>
        public void Reset()
        {
            _units.Clear();
        }

        /// <summary>按“基名 + 族”找已经登记的组。</summary>
        public VolumeGroupUnit? Find(string baseName, VolumeNamingFamily? family)
        {
            if (string.IsNullOrWhiteSpace(baseName) || family == null)
            {
                return null;
            }

            return _units.TryGetValue(BuildKey(baseName, family.Value), out VolumeGroupUnit? unit)
                ? unit
                : null;
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
            IReadOnlyList<GroupPieceFact>? pieces,
            IReadOnlyList<int>? missingNumbers,
            string evidence,
            int? declaredPieceCount,
            long? declaredTotalBytes,
            string outputLayer)
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
             * 源层红线（用户六条之二）：入口层只能是“该开这一组的那一层”，
             * 其余物 / 工作区 / 暂存目录永远不能当入口层。判不出 ⇒ 不建组。
             */
            if (IsForbiddenLayer(sourceLayer))
            {
                return null;
            }

            string key = BuildKey(baseName, family.Value);

            if (_units.TryGetValue(key, out VolumeGroupUnit? existing))
            {
                return existing;
            }

            var unit = new VolumeGroupUnit(baseName, family, sourceLayer, entryPath)
            {
                State = VolumeGroupState.Complete,
                Pieces = pieces,
                MissingNumbers = missingNumbers ?? Array.Empty<int>(),
                DeclaredPieceCount = declaredPieceCount,
                DeclaredTotalBytes = declaredTotalBytes,
                Verdict = evidence,
                OutputLayer = outputLayer
            };

            /*
             * 族已接线 ⇒ 由族算法回答“顺序 / 片数 / 完整性 / 规范名计划”。
             * ⛔ 只有 Complete 才继续；Missing/Unknown 把 Unit 留在账上（判不出就不动），
             * 由调用方负责“不点名、不链接、不解压”。
             */
            if (_algorithms.TryGetValue(family.Value, out IVolumeFamilyAlgorithm? algorithm))
            {
                FamilyResult result = algorithm.Evaluate(unit);

                unit.OrderedPieces = result.OrderedPieces;
                unit.NamePlan = result.NamePlan;
                unit.MissingNumbers = result.MissingNumbers;
                unit.Verdict = result.Evidence;
                unit.State = result.Verdict switch
                {
                    GroupAnalysis.GroupAnalysisVerdict.Complete => VolumeGroupState.Complete,
                    GroupAnalysis.GroupAnalysisVerdict.Missing => VolumeGroupState.Missing,
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

        private static string BuildKey(string baseName, VolumeNamingFamily family) =>
            baseName + "|" + family.ToString();

        /// <summary>其余物 / 工作区 / 暂存目录不能当组的入口层（只读路径判断，不碰盘）。</summary>
        private static bool IsForbiddenLayer(string layer)
        {
            string normalized = layer.Replace('/', '\\');

            return normalized.IndexOf("\\.ArchiveFixer.work", StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf("\\其余物", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}

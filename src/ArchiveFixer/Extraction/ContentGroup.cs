using System;
using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction
{
    /// <summary>最终内容分卷组的失败种类；只由组事实层写入。</summary>
    public enum FinalGroupFailureKind
    {
        None = 0,

        /// <summary>组不完整（缺片）：链失败，源包不删。</summary>
        Incomplete = 1,

        /// <summary>组完整但数据损坏：链失败，源包不删。</summary>
        Corrupted = 2
    }

    /// <summary>最终层输入片的直接产出者（用户源包与程序过程物分开）。</summary>
    public sealed class ContentGroupFinalLayerProducer
    {
        public string TaskFileName { get; init; } = string.Empty;

        public string Path { get; init; } = string.Empty;

        public bool IsSource { get; init; }
    }

    /// <summary>内容组的最终内容结论；⛔ 任务级“解压成功”不能替代它。</summary>
    public enum FinalContentState
    {
        Unknown = 0,

        /// <summary>有产物但证据只到非空/未逐条核对，不能当最终成功。</summary>
        Partial = 1,

        /// <summary>产物校验通过且逐条核对过，可当最终成功。</summary>
        Verified = 2,

        /// <summary>没有最终内容。</summary>
        Missing = 3
    }

    /// <summary>
    /// **交界处的内容组**：把“哪些片、来自哪些源根、最终内容落在哪”收成一条组级事实。
    ///
    /// <para>它只由交界屏障登记；⛔ 不在导入时预建、⛔ 不按任务名猜、⛔ 不碰盘。
    /// 组状态与入口来自 <see cref="PreparedVolumeGroup"/>；来源根来自第一大步账本。</para>
    /// </summary>
    public sealed class ContentGroup
    {
        private readonly HashSet<string> _sourceRootIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _memberPaths = new();
        private readonly List<string> _missingNumbers = new();

        public string GroupId { get; init; } = string.Empty;

        public string BaseName { get; init; } = string.Empty;

        public VolumeNamingFamily? Family { get; init; }

        public string SourceLayer { get; init; } = string.Empty;

        public string PlacementKey { get; private set; } = string.Empty;

        /// <summary>指定位置时的目标根（从 PlacementKey 的落点域取）；Original 档为空。</summary>
        public string PlacementRoot { get; private set; } = string.Empty;

        public bool HasPlacementRoot => !string.IsNullOrWhiteSpace(PlacementRoot);

        public string EntryPath { get; private set; } = string.Empty;

        public PreparedGroupState State { get; private set; } = PreparedGroupState.Unknown;

        public string Evidence { get; private set; } = string.Empty;

        public string OwnerTaskFileName { get; private set; } = string.Empty;

        public string OwnerTaskPath { get; private set; } = string.Empty;

        /// <summary>组唯一最终输出根；空 = 还没定，⛔ 不拿任务 OutputPath 猜。</summary>
        public string FinalOutputRoot { get; private set; } = string.Empty;

        public string FinalContentPath { get; private set; } = string.Empty;

        public FinalContentState FinalState { get; private set; } = FinalContentState.Unknown;

        public bool FinalContentVerified => FinalState == FinalContentState.Verified;

        public int FinalFileCount { get; private set; }

        public long FinalBytes { get; private set; }

        public string FinalContentEvidence { get; private set; } = string.Empty;

        /// <summary>最终组失败种类；None = 还没判出/不是最终组失败。</summary>
        public FinalGroupFailureKind FailureKind { get; private set; } = FinalGroupFailureKind.None;

        public string FailureEvidence { get; private set; } = string.Empty;

        private readonly List<ContentGroupFinalLayerProducer> _finalLayerProducers = new();

        /// <summary>最终层输入片的直接产出者；源包与过程物分开由 IsSource 标记。</summary>
        public IReadOnlyList<ContentGroupFinalLayerProducer> FinalLayerProducers => _finalLayerProducers;

        public IReadOnlyCollection<string> SourceRootIds => _sourceRootIds;

        public IReadOnlyList<string> MemberPaths => _memberPaths;

        public IReadOnlyList<string> MissingNumbers => _missingNumbers;

        public bool HasPlacementConflict { get; private set; }

        public bool HasPlacementKey => !string.IsNullOrWhiteSpace(PlacementKey);

        public void MergePrepared(PreparedVolumeGroup prepared, FirstStepFileLedger? ledger)
        {
            if (prepared == null)
            {
                throw new ArgumentNullException(nameof(prepared));
            }

            /*
             * 后到分析可能只看得到剩余片（成功组收片后 .002/.003 已经不在了）。
             * ⛔ 不能用“后到的少片分析”把已经可证完整的组降级/缩成员：
             * 组的事实一旦由 5 片证成 Complete，就保留这 5 片的成员账，后到结果只作补充。
             */
            bool keepStrongerState = State == PreparedGroupState.Complete
                && prepared.State != PreparedGroupState.Complete;

            if (!keepStrongerState)
            {
                State = prepared.State;
                Evidence = prepared.Describe();

                _missingNumbers.Clear();

                foreach (int number in prepared.MissingNumbers)
                {
                    _missingNumbers.Add(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            else
            {
                Evidence = prepared.Describe();
            }

            if (!string.IsNullOrWhiteSpace(prepared.OriginalEntryPath))
            {
                EntryPath = prepared.OriginalEntryPath;
            }

            foreach (PreparedVolumePiece piece in prepared.Pieces)
            {
                if (!string.IsNullOrWhiteSpace(piece.OriginalPath)
                    && !_memberPaths.Contains(piece.OriginalPath, StringComparer.OrdinalIgnoreCase))
                {
                    _memberPaths.Add(piece.OriginalPath);
                }
            }

            foreach (PreparedVolumePiece piece in prepared.Pieces)
            {
                if (string.IsNullOrWhiteSpace(piece.OriginalPath)
                    || ledger == null
                    || !ledger.TryGetByPath(piece.OriginalPath, out FirstStepFileLedgerEntry entry))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(entry.OriginRootId))
                {
                    _sourceRootIds.Add(entry.OriginRootId);
                }

                if (string.IsNullOrWhiteSpace(entry.PlacementKey))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(PlacementKey))
                {
                    PlacementKey = entry.PlacementKey;
                }
                else if (!string.Equals(PlacementKey, entry.PlacementKey, StringComparison.OrdinalIgnoreCase))
                {
                    HasPlacementConflict = true;
                }

                /*
                 * PlacementKey 形状：模式|自定义根|选中范围（BuildSourcePlacementKey 唯一出口）。
                 * 指定位置档第二段就是目标根；Original 档第二段为空。
                 */
                string[] keyParts = entry.PlacementKey.Split('|');
                if (keyParts.Length >= 2
                    && string.IsNullOrWhiteSpace(PlacementRoot)
                    && !string.IsNullOrWhiteSpace(keyParts[1]))
                {
                    PlacementRoot = keyParts[1];
                }
            }
        }

        /// <summary>
        /// 组事实层落最终组失败。只能在不完整/完整但损坏时调用；
        /// 已 Verified 的组绝不降级。这里不读盘、不删源包。
        /// </summary>
        public bool MarkFinalGroupFailure(FinalGroupFailureKind kind, string? evidence)
        {
            if (kind == FinalGroupFailureKind.None || FinalState == FinalContentState.Verified)
            {
                return false;
            }

            bool changed = FailureKind != kind || FinalState != FinalContentState.Missing;
            FailureKind = kind;
            FailureEvidence = evidence ?? string.Empty;
            FinalState = FinalContentState.Missing;
            FinalFileCount = 0;
            FinalBytes = 0;
            FinalContentPath = string.Empty;
            FinalContentEvidence = FailureEvidence;
            return changed;
        }

        public void RecordFinalLayerProducer(
            string? taskFileName,
            string? path,
            bool isSource)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (_finalLayerProducers.Any(item =>
                    string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _finalLayerProducers.Add(new ContentGroupFinalLayerProducer
            {
                TaskFileName = taskFileName ?? string.Empty,
                Path = path,
                IsSource = isSource
            });
        }

        public void AttachOwner(string? ownerTaskFileName, string? ownerTaskPath = null)
        {
            if (!string.IsNullOrWhiteSpace(ownerTaskFileName))
            {
                OwnerTaskFileName = ownerTaskFileName!;
            }

            if (!string.IsNullOrWhiteSpace(ownerTaskPath))
            {
                OwnerTaskPath = ownerTaskPath!;
            }
        }

        /// <summary>
        /// 写最终内容结论；返回状态是否发生变化（用于只记录一次）。⛔ 不允许用 Partial 冒充 Verified。
        /// </summary>
        public bool SetFinalOutput(
            string? outputRoot,
            string? finalContentPath,
            FinalContentState state,
            int fileCount,
            long bytes,
            string? evidence)
        {
            bool changed = FinalState != state
                || !string.Equals(FinalOutputRoot, outputRoot ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(FinalContentPath, finalContentPath ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            FinalOutputRoot = outputRoot ?? string.Empty;
            FinalContentPath = finalContentPath ?? string.Empty;
            FinalState = state;
            FinalFileCount = fileCount;
            FinalBytes = bytes;
            FinalContentEvidence = evidence ?? string.Empty;

            return changed;
        }

        public string Describe()
        {
            string roots = _sourceRootIds.Count == 0
                ? "来源根判不出"
                : string.Join(",", _sourceRootIds.Select(System.IO.Path.GetFileName).OrderBy(name => name));

            string placement = HasPlacementConflict
                ? "落点域冲突"
                : string.IsNullOrWhiteSpace(PlacementKey) ? "落点域判不出" : PlacementKey;

            return $"{BaseName} ｜ 族={Family} ｜ 状态={State} ｜ 片={_memberPaths.Count}"
                + $" ｜ 来源根={roots} ｜ {placement}"
                + (string.IsNullOrWhiteSpace(EntryPath) ? string.Empty : $" ｜ 入口={System.IO.Path.GetFileName(EntryPath)}")
                + (_missingNumbers.Count == 0 ? string.Empty : $" ｜ 缺={string.Join(",", _missingNumbers)}")
                + (string.IsNullOrWhiteSpace(FinalContentPath) ? string.Empty : $" ｜ 最终内容={FinalContentPath}")
                + (FinalContentVerified ? " ｜ 最终内容已证" : string.Empty);
        }
    }
}

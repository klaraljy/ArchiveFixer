using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 账本项在“源根 → 产出物 → 内容组 → 最终内容”链路里的角色。
    /// <para>⛔ 这是归属事实，不是“处理状态”；判不出就保持 <see cref="Unknown"/>。</para>
    /// </summary>
    public enum ArtifactRole
    {
        Unknown = 0,

        /// <summary>用户导入的源文件（第一大步的一个源根）。</summary>
        SourceRoot = 1,

        /// <summary>可能是某一组分卷、交界前不许回收的候选片。</summary>
        CandidateVolumePiece = 2,

        /// <summary>解出来的内层归档；可能继续递归，也可能成为内容组入口。</summary>
        InnerArchive = 3,

        /// <summary>已确认只用于中间处理、不会成为最终内容的过程物。</summary>
        ProcessArtifact = 4,

        /// <summary>可进入最终内容审判的内容物。</summary>
        Content = 5
    }

    /// <summary>
    /// **第一大步通过闸门的文件账本项**（共用骨架）。
    ///
    /// <para>它把"这一份文件底层的全部事实"一次性记下来，第二大步的族专属 resolver
    /// 只读账本、不再回盘上重新拼判据。判不出的字段留空/0，⛔ 不猜。</para>
    ///
    /// <para>⛔ 账本项不是"待处理队列"：文件本身必须原地不动。</para>
    /// </summary>
    public sealed class FirstStepFileLedgerEntry
    {
        public string Path { get; init; } = string.Empty;

        public string BaseName { get; init; } = string.Empty;

        public long Size { get; init; }

        public VolumeContentFormat Format { get; init; }

        public VolumeNumberFromContent.VolumeMembershipVerdict Membership { get; init; }

        public ArchiveBodyKind BodyKind { get; init; }

        public int? VolumeNumber { get; init; }

        public bool FirstVolumeFlag { get; init; }

        public int? RawVolumeField { get; init; }

        public long DeclaredTotalBytes { get; init; }

        public int DeclaredVolumeCount { get; init; }

        public bool HasZipEndRecord { get; init; }

        public bool ZipEndRecordMeansSplit { get; init; }

        public bool HasZipLocalHeader { get; init; }

        public RarVariant RarVariant { get; init; }

        public ZipVariant ZipVariant { get; init; }

        public ArchiveEncryptionState Encryption { get; init; }

        public int? NameSlot { get; init; }

        public string NameEvidence { get; init; } = string.Empty;

        public VolumeHandoffSource Source { get; init; }

        public string SourceLayer { get; init; } = string.Empty;

        public bool HasIdentity { get; init; }

        public ulong VolumeSerial { get; init; }

        public ulong FileIndex { get; init; }

        public string TaskLogName { get; init; } = string.Empty;

        /// <summary>
        /// 这一份归哪一条源根链。用户导入的根任务 = 根任务原始路径的规范化值；
        /// 递归产出物 = 它所属根任务的同一个值。空 = 归属不明，⛔ 不猜。
        /// </summary>
        public string OriginRootId { get; init; } = string.Empty;

        /// <summary>真正产出这一份的任务身份；用户导入项 = 根任务身份。</summary>
        public string ProducedByTaskId { get; init; } = string.Empty;

        /// <summary>这一份在源根/产出物/内容组链路里的角色。</summary>
        public ArtifactRole Role { get; init; } = ArtifactRole.Unknown;

        /// <summary>
        /// 交界前保护候选片的租约；空 = 没有租约。
        /// <para>⛔ 有租约的片，逐层回收/临时清理/失败清理都不得删除。</para>
        /// </summary>
        public string CandidateLeaseId { get; init; } = string.Empty;

        /// <summary>这一份所属的落点域；同组必须同域，空 = 归属不明。</summary>
        public string PlacementKey { get; init; } = string.Empty;

        public bool HasCandidateLease => !string.IsNullOrWhiteSpace(CandidateLeaseId);

        /// <summary>这一份是不是通过了第一大步闸门（账本只收通过项；保留字段供日志复核）。</summary>
        public bool IsGatePassed { get; init; }

        /// <summary>从底层内容卡生成账本项（唯一转换出口之一）。</summary>
        public static FirstStepFileLedgerEntry FromCard(
            FileNatureCard card,
            string taskLogName,
            VolumeHandoffSource source,
            bool gatePassed = true,
            RarVariant rarVariant = RarVariant.Unknown,
            ZipVariant zipVariant = ZipVariant.Unknown,
            int? nameSlot = null,
            string nameEvidence = "",
            string originRootId = "",
            string producedByTaskId = "",
            ArtifactRole role = ArtifactRole.Unknown,
            string candidateLeaseId = "",
            string placementKey = "")
        {
            if (card == null)
            {
                throw new ArgumentNullException(nameof(card));
            }

            string directory;

            try
            {
                directory = System.IO.Path.GetDirectoryName(card.Path) ?? string.Empty;
            }
            catch
            {
                directory = string.Empty;
            }

            return new FirstStepFileLedgerEntry
            {
                Path = card.Path,
                BaseName = FileNameHelper.SecondStepBaseName(card.Path),
                Size = card.Size,
                Format = card.Format,
                Membership = card.Membership,
                BodyKind = card.BodyKind,
                VolumeNumber = card.VolumeNumber,
                FirstVolumeFlag = card.FirstVolumeFlag,
                RawVolumeField = card.RawVolumeField,
                DeclaredTotalBytes = card.DeclaredTotalBytes,
                DeclaredVolumeCount = card.DeclaredVolumeCount,
                HasZipEndRecord = card.HasZipEndRecord,
                ZipEndRecordMeansSplit = card.ZipEndRecordMeansSplit,
                HasZipLocalHeader = card.HasZipLocalHeader,
                RarVariant = rarVariant != RarVariant.Unknown ? rarVariant : card.RarVariant,
                ZipVariant = zipVariant,
                Encryption = card.Encryption,
                NameSlot = nameSlot,
                NameEvidence = nameEvidence,
                Source = source,
                SourceLayer = directory,
                HasIdentity = card.HasIdentity,
                VolumeSerial = card.VolumeSerial,
                FileIndex = card.FileIndex,
                TaskLogName = taskLogName,
                OriginRootId = originRootId,
                ProducedByTaskId = producedByTaskId,
                Role = role,
                CandidateLeaseId = candidateLeaseId,
                PlacementKey = placementKey,
                IsGatePassed = gatePassed
            };
        }

        /// <summary>任务改名后只换路径/基名/源层，其它底层事实原样保留。</summary>
        public FirstStepFileLedgerEntry WithPath(string newPath)
        {
            string directory;
            try
            {
                directory = System.IO.Path.GetDirectoryName(newPath) ?? SourceLayer;
            }
            catch
            {
                directory = SourceLayer;
            }

            return new FirstStepFileLedgerEntry
            {
                Path = newPath,
                BaseName = FileNameHelper.SecondStepBaseName(newPath),
                Size = Size,
                Format = Format,
                Membership = Membership,
                BodyKind = BodyKind,
                VolumeNumber = VolumeNumber,
                FirstVolumeFlag = FirstVolumeFlag,
                RawVolumeField = RawVolumeField,
                DeclaredTotalBytes = DeclaredTotalBytes,
                DeclaredVolumeCount = DeclaredVolumeCount,
                HasZipEndRecord = HasZipEndRecord,
                ZipEndRecordMeansSplit = ZipEndRecordMeansSplit,
                HasZipLocalHeader = HasZipLocalHeader,
                RarVariant = RarVariant,
                ZipVariant = ZipVariant,
                Encryption = Encryption,
                NameSlot = NameSlot,
                NameEvidence = NameEvidence,
                Source = Source,
                SourceLayer = directory,
                HasIdentity = HasIdentity,
                VolumeSerial = VolumeSerial,
                FileIndex = FileIndex,
                TaskLogName = TaskLogName,
                OriginRootId = OriginRootId,
                ProducedByTaskId = ProducedByTaskId,
                Role = Role,
                CandidateLeaseId = CandidateLeaseId,
                PlacementKey = PlacementKey,
                IsGatePassed = IsGatePassed
            };
        }

        public string Describe()
        {
            string number = VolumeNumber.HasValue
                ? VolumeNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "判不出";

            return $"{System.IO.Path.GetFileName(Path)} ｜ 基名={BaseName} ｜ {Size} 字节 ｜ 格式={Format}"
                + $" ｜ 内容={BodyKind} ｜ 成员={Membership} ｜ 卷号={number}"
                + $" ｜ 首卷={(FirstVolumeFlag ? "是" : "否")} ｜ RAR变体={RarVariant}"
                + $" ｜ ZIP变体={ZipVariant} ｜ 加密={Encryption}"
                + $" ｜ 名字槽={(NameSlot?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "判不出")}"
                + $" ｜ 层={SourceLayer} ｜ 来源={Source}";
        }
    }

    /// <summary>
    /// **第一大步文件账本**（共用骨架的唯一收口）。
    ///
    /// <para>它回答"这一批里有哪些文件过了第一大步闸门、各自是什么"，供第二大步的
    /// 边界 resolver 按基名/族/大小收片。⛔ 它不做任何分组结论、不碰盘。</para>
    /// </summary>
    public sealed class FirstStepFileLedger
    {
        private readonly List<FirstStepFileLedgerEntry> _entries = new();
        private readonly Dictionary<string, int> _indexByPath = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<FirstStepFileLedgerEntry> Entries => _entries;

        public int Count => _entries.Count;

        /// <summary>批首清空（与其它批内账本同一处调用）。</summary>
        public void Clear()
        {
            _entries.Clear();
            _indexByPath.Clear();
        }

        /// <summary>
        /// 登记一项；同一路径重复登记时**保留后到的那一项**（内容卡可能因改名/补片而变化），
        /// 但绝不因为"路径相同"就把两份不同文件合成一份。
        /// </summary>
        public FirstStepFileLedgerEntry AddOrReplace(FirstStepFileLedgerEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (string.IsNullOrWhiteSpace(entry.Path))
            {
                return entry;
            }

            string fullPath;

            try
            {
                fullPath = System.IO.Path.GetFullPath(entry.Path);
            }
            catch
            {
                fullPath = entry.Path;
            }

            if (_indexByPath.TryGetValue(fullPath, out int index))
            {
                _entries[index] = entry;
                return entry;
            }

            _indexByPath[fullPath] = _entries.Count;
            _entries.Add(entry);
            return entry;
        }

        /// <summary>
        /// 任务改名/修后缀后，把旧路径那一条改成新路径。
        /// 只换路径与派生字段，底层身份/卷证据原样保留；旧路径不存在时不新增。
        /// </summary>
        public FirstStepFileLedgerEntry? RetargetPath(string? oldPath, string? newPath)
        {
            if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath))
            {
                return null;
            }

            string oldFull;
            string newFull;

            try
            {
                oldFull = System.IO.Path.GetFullPath(oldPath);
                newFull = System.IO.Path.GetFullPath(newPath);
            }
            catch
            {
                return null;
            }

            if (!_indexByPath.TryGetValue(oldFull, out int index))
            {
                return null;
            }

            FirstStepFileLedgerEntry retargeted = _entries[index].WithPath(newFull);
            _entries[index] = retargeted;
            _indexByPath.Remove(oldFull);
            _indexByPath[newFull] = index;
            return retargeted;
        }

        /// <summary>按路径取账本项（路径规范化后比较）；没有返回 false。</summary>
        public bool TryGetByPath(string? path, out FirstStepFileLedgerEntry entry)
        {
            entry = null!;

            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string fullPath;

            try
            {
                fullPath = System.IO.Path.GetFullPath(path);
            }
            catch
            {
                return false;
            }

            if (!_indexByPath.TryGetValue(fullPath, out int index))
            {
                return false;
            }

            entry = _entries[index];
            return true;
        }

        /// <summary>同基名（第二大步基名）的项；⛔ 不做族判定。</summary>
        public IReadOnlyList<FirstStepFileLedgerEntry> SameBase(string? baseName)
        {
            if (string.IsNullOrWhiteSpace(baseName))
            {
                return Array.Empty<FirstStepFileLedgerEntry>();
            }

            return _entries
                .Where(entry => string.Equals(entry.BaseName, baseName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>某一条源根链下的全部账本项；空根身份返回空表，⛔ 不退化成“全部”。</summary>
        public IReadOnlyList<FirstStepFileLedgerEntry> ByOriginRoot(string? originRootId)
        {
            if (string.IsNullOrWhiteSpace(originRootId))
            {
                return Array.Empty<FirstStepFileLedgerEntry>();
            }

            return _entries
                .Where(entry => string.Equals(entry.OriginRootId, originRootId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>当前持有租约、交界前不得回收的候选片。</summary>
        public IReadOnlyList<FirstStepFileLedgerEntry> LeasedEntries()
        {
            return _entries.Where(entry => entry.HasCandidateLease).ToList();
        }

        /// <summary>本批出现过的基名（去重、稳定顺序）。</summary>
        public IReadOnlyList<string> BaseNames()
        {
            return _entries
                .Select(entry => entry.BaseName)
                .Where(baseName => !string.IsNullOrWhiteSpace(baseName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(baseName => baseName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}

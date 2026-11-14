using ArchiveFixer.Helpers;
using System;

namespace ArchiveFixer.Detection
{
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

        public VolumeNumberFromContent.VolumeMembershipVerdict Membership { get; init; } =
            VolumeNumberFromContent.VolumeMembershipVerdict.Unknown;

        public ArchiveBodyKind BodyKind { get; init; }

        /// <summary>内容物事实（普通媒体 = 否定证据；见 <see cref="FileContentKind"/>）。</summary>
        public FileContentKind ContentKind { get; init; }

        /// <summary>内容物/归档分类（唯一出口 <see cref="FileContentProbe"/>）。</summary>
        public FileContentClass ContentClass { get; init; }

        /// <summary>内容格式名（PNG/WebP/PDF/OOXML…）。</summary>
        public string ContentFormat { get; init; } = string.Empty;

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
        /// 这个路径是**已经解成功并定稿的源包**（路径事实，与后缀/基名/内容格式无关）。
        ///
        /// <para>⛔ 已消费的源包不许再进第二大步分卷候选：真机 2026-11-10（
        /// `D:\ArchiveFixer\logs\ArchiveFixer_20261110_161401.log` 794/801/852/853 行）外层 tar
        /// 已在 16:42:06 定稿，第二大步仍把同基名的它当 7z 中间片收进组 ⇒ 整包字节和翻倍 ⇒
        /// `SizeAnomaly` ⇒ 组建组被拒 ⇒ 5 片永不却解。</para>
        /// </summary>
        public bool IsConsumedSource { get; init; }

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
                ContentKind = card.ContentKind,
                ContentClass = card.ContentClass,
                ContentFormat = card.ContentFormat,
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

        /// <summary>
        /// 同一路径再次登记时，保留**已有但后到项缺失**的事实。
        ///
        /// <para>2026-11-06 真机 `_2.7z`：首卷登记时会把同目录兄弟片补进账本，但补录项不传
        /// <see cref="NameSlot"/>；随后兄弟片自己的任务再登记时，若又碰上另一轮补录，
        /// 后到的“低信息项”会把已有名字槽/租约/角色覆盖掉。于是全场七片，组分析只看到
        /// 1、3、7 有槽（日志逐字：<c>找到 4 片…有槽 1,3,7</c>），下一次甚至只剩 1 片。
        /// 账本事实必须**单调**：后到项可以补新事实，但⛔ 不许把已确认的事实抹回“判不出”。</para>
        /// </summary>
        public FirstStepFileLedgerEntry PreserveFactsFrom(FirstStepFileLedgerEntry existing)
        {
            if (existing == null)
            {
                return this;
            }

            bool incomingHasIdentity = HasIdentity;
            bool existingHasIdentity = existing.HasIdentity;

            return new FirstStepFileLedgerEntry
            {
                Path = Path,
                BaseName = string.IsNullOrWhiteSpace(BaseName) ? existing.BaseName : BaseName,
                Size = Size > 0 ? Size : existing.Size,
                Format = Format != VolumeContentFormat.Unknown ? Format : existing.Format,
                Membership = Membership != VolumeNumberFromContent.VolumeMembershipVerdict.Unknown
                    ? Membership
                    : existing.Membership,
                BodyKind = BodyKind != ArchiveBodyKind.Unknown ? BodyKind : existing.BodyKind,
                ContentKind = ContentKind != FileContentKind.Unknown ? ContentKind : existing.ContentKind,
                ContentClass = ContentClass != FileContentClass.Unknown ? ContentClass : existing.ContentClass,
                ContentFormat = !string.IsNullOrWhiteSpace(ContentFormat) ? ContentFormat : existing.ContentFormat,
                VolumeNumber = VolumeNumber ?? existing.VolumeNumber,
                FirstVolumeFlag = FirstVolumeFlag || existing.FirstVolumeFlag,
                RawVolumeField = RawVolumeField ?? existing.RawVolumeField,
                DeclaredTotalBytes = DeclaredTotalBytes > 0 ? DeclaredTotalBytes : existing.DeclaredTotalBytes,
                DeclaredVolumeCount = DeclaredVolumeCount > 0 ? DeclaredVolumeCount : existing.DeclaredVolumeCount,
                HasZipEndRecord = HasZipEndRecord || existing.HasZipEndRecord,
                ZipEndRecordMeansSplit = ZipEndRecordMeansSplit || existing.ZipEndRecordMeansSplit,
                HasZipLocalHeader = HasZipLocalHeader || existing.HasZipLocalHeader,
                RarVariant = RarVariant != RarVariant.Unknown ? RarVariant : existing.RarVariant,
                ZipVariant = ZipVariant != ZipVariant.Unknown ? ZipVariant : existing.ZipVariant,
                Encryption = Encryption != ArchiveEncryptionState.Unknown ? Encryption : existing.Encryption,
                NameSlot = NameSlot ?? existing.NameSlot,
                NameEvidence = !string.IsNullOrWhiteSpace(NameEvidence) ? NameEvidence : existing.NameEvidence,
                Source = Source == VolumeHandoffSource.UserImported
                         || existing.Source != VolumeHandoffSource.UserImported
                    ? Source
                    : existing.Source,
                SourceLayer = !string.IsNullOrWhiteSpace(SourceLayer) ? SourceLayer : existing.SourceLayer,
                HasIdentity = incomingHasIdentity || existingHasIdentity,
                VolumeSerial = incomingHasIdentity ? VolumeSerial : existing.VolumeSerial,
                FileIndex = incomingHasIdentity ? FileIndex : existing.FileIndex,
                TaskLogName = !string.IsNullOrWhiteSpace(TaskLogName) ? TaskLogName : existing.TaskLogName,
                OriginRootId = !string.IsNullOrWhiteSpace(OriginRootId) ? OriginRootId : existing.OriginRootId,
                ProducedByTaskId = !string.IsNullOrWhiteSpace(ProducedByTaskId)
                    ? ProducedByTaskId
                    : existing.ProducedByTaskId,
                Role = Role != ArtifactRole.Unknown ? Role : existing.Role,
                IsConsumedSource = IsConsumedSource || existing.IsConsumedSource,
                CandidateLeaseId = !string.IsNullOrWhiteSpace(CandidateLeaseId)
                    ? CandidateLeaseId
                    : existing.CandidateLeaseId,
                PlacementKey = !string.IsNullOrWhiteSpace(PlacementKey) ? PlacementKey : existing.PlacementKey,
                IsGatePassed = IsGatePassed || existing.IsGatePassed
            };
        }

        /// <summary>
        /// 这一项有没有资格作为**第二大步交接候选**（`OneClickCoordinator` 的 `ledgerHandoff` 读它）。
        ///
        /// <para>账本会收所有已发布产物（含 MP4/TXT/PNG 等内容物，见
        /// <c>RegisterProducedOutputsToLedger</c>），而组分析不能把每个内容物都当成“1 片候选”。
        /// 这里只认底层分卷事实：内容身份、卷号/首卷、名字槽、ZIP split 证据、交界前租约或候选角色。
        /// 判不出且没有这些事实的内容物由 <c>extraGroupSeeds</c> 的脏名扫描另行补；⛔ 不在这里猜。</para>
        ///
        /// <para>⚠ 它**不是** `<see cref="SecondStepCandidates(string?)"/>` 的过滤器：候选池只排除
        /// 已消费、0 字节与普通媒体，事实稀疏的分卷片仍要交给族 resolver 定序。
        /// 内容物事实（<see cref="FileContentKind.NormalMedia"/>）在这里也会否决。</para>
        /// </summary>
        public bool HasSecondStepEvidence =>
            !IsConsumedSource
            && ContentKind != FileContentKind.NormalMedia
            && ContentClass != FileContentClass.KnownContent
            && (BodyKind is ArchiveBodyKind.VolumeFirstPiece or ArchiveBodyKind.VolumePiece
                || Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                || FirstVolumeFlag
                || NameSlot.HasValue
                || ZipEndRecordMeansSplit
                || HasZipLocalHeader
                || HasCandidateLease
                || Role == ArtifactRole.CandidateVolumePiece);

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
                ContentKind = ContentKind,
                ContentClass = ContentClass,
                ContentFormat = ContentFormat,
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
                IsConsumedSource = IsConsumedSource,
                CandidateLeaseId = CandidateLeaseId,
                PlacementKey = PlacementKey,
                IsGatePassed = IsGatePassed
            };
        }

        /// <summary>把这一项标成「已解成功并定稿的源包」（只加这一条路径事实，其它字段原样保留）。</summary>
        public FirstStepFileLedgerEntry WithConsumedSource()
        {
            return new FirstStepFileLedgerEntry
            {
                Path = Path,
                BaseName = BaseName,
                Size = Size,
                Format = Format,
                Membership = Membership,
                BodyKind = BodyKind,
                ContentKind = ContentKind,
                ContentClass = ContentClass,
                ContentFormat = ContentFormat,
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
                SourceLayer = SourceLayer,
                HasIdentity = HasIdentity,
                VolumeSerial = VolumeSerial,
                FileIndex = FileIndex,
                TaskLogName = TaskLogName,
                OriginRootId = OriginRootId,
                ProducedByTaskId = ProducedByTaskId,
                Role = Role,
                IsConsumedSource = true,
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
}

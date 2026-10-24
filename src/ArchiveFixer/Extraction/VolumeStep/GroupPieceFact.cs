using System;
using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **第二大步拿到的一片原始事实**（用户 2026-10-24 口径：底层给事实，族算法下结论）。
    ///
    /// <para>这里只装**已经识别到的**内容事实 + 落在哪一层 + 来源 + 证据出处；
    /// 卷号/片数允许为空/0（判不出），⛔ 由族算法决定用不用、怎么用。</para>
    /// </summary>
    public sealed record GroupPieceFact(
        string Path,
        long Size,
        VolumeContentFormat Format,
        ArchiveBodyKind BodyKind,
        int? VolumeNumber,
        bool FirstVolumeFlag,
        VolumeNumberFromContent.VolumeMembershipVerdict Membership,
        VolumeNumberFail ContentFail,
        long DeclaredTotalBytes,
        int DeclaredVolumeCount,
        int? RawVolumeField,
        string SourceLayer,
        VolumeHandoffSource? Source,
        bool FromHandoff,
        bool HasZipEndRecord = false,
        bool ZipEndRecordMeansSplit = false,
        bool HasZipLocalHeader = false);
}

using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using System.IO;

namespace ArchiveFixer.Extraction;

/// <summary>
/// **同组判定**（第二大步发现层的第 2 段）：这一份文件**能不能**算某个分卷组的一片。
///
/// <para>只回答"是不是同一组的一员"，不回答"第几片、齐没齐"（定序在
/// <see cref="VolumeGroupPieceOrdering"/>，完整/缺片结论在 <see cref="VolumeCandidateVerdict"/>）。</para>
///
/// <para><b>否定证据（排除项）</b>：可证完整单卷/父包（`SingleComplete` + `ProvablyNotVolumeMember`
/// + 没有名字卷槽）⛔ 绝不能当别人分卷组的一片；普通媒体与已证明的内容物不是片；
/// 0 字节不是片；已证实的异族片不并进同组。</para>
///
/// <para><b>正面证据（准入项）</b>：名字卷槽（`.001` / `.part1.rar` / `.r00`）、内容卷号、
/// 首片标记、ZIP split 证据；"同目录恰好只有一个内容首卷"时才允许用内容卡绕过同基名入场。</para>
///
/// <para><b>失败模型</b>：判不出就返回 false（不收/不排除由调用方的下一步定），
/// 文件读不出按"没有正面证据"处理，⛔ 不猜、不抛。</para>
///
/// <para><b>生命周期</b>：纯静态、无状态；输入是路径/交接单/内容卡/已建好的一片证据，
/// 输出是布尔判据。不写盘、不缓存、不改名。</para>
/// </summary>
internal static class VolumeGroupMembership
{
    /// <summary>
    /// 这一份是不是**自证完整单卷、且可证不是分卷成员**（父包 / 普通单卷 zip / 完整单卷 7z）。
    ///
    /// <para>⛔ 这类文件只能当自己的任务，绝不能当别人分卷组的一片 —— 真机 2026-11-01：
    /// `2.exe`（完整 RAR5）与它解出来的 `2.part1/2/3.rar` 基名同为 "2"，被收进同一组，
    /// 组永远判不出、过程物也删不掉。判据只认内容事实，不看名字像不像。</para>
    /// </summary>
    public static bool IsProvablySingleCompleteNonMember(
        ArchiveFixer.Detection.VolumeHandoffEntry? entry)
    {
        return entry != null
            && entry.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.SingleComplete
            && entry.Membership == ArchiveFixer.Detection.VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember
            /*
             * ⛔ 2026-11-01 真机：`TryGetVolumeIndex("2.rar")` 把裸 `.rar` 当成第 1 卷（slot=1），
             * 于是父包 `2.rar` 逃过"没有名字卷槽"这一条、被收进子分卷组。
             * 这里判的是"名字是不是分卷片"（.part1.rar / .7z.001 / .r00…），不是"能不能读出卷号"。
             */
            && !LegacyVolumeNames.IsVolumePart(FileNameHelper.GetFileName(entry.Path))
            && !entry.FirstVolumeFlag;
    }

    public static bool IsProvablySingleCompleteNonMember(
        IReadOnlyDictionary<string, ArchiveFixer.Detection.VolumeHandoffEntry>? handoffByPath,
        string path)
    {
        return handoffByPath != null
            && handoffByPath.TryGetValue(path, out ArchiveFixer.Detection.VolumeHandoffEntry? entry)
            && IsProvablySingleCompleteNonMember(entry);
    }

    public static bool IsProvablySingleCompleteNonMember(
        ArchiveFixer.Detection.FileNatureCard? card,
        string path)
    {
        return card != null
            && card.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.SingleComplete
            && card.Membership == ArchiveFixer.Detection.VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember
            && !LegacyVolumeNames.IsVolumePart(FileNameHelper.GetFileName(path));
    }

    public static bool IsProvablySingleCompleteNonMember(VolumeCandidatePieceFacts item)
    {
        if (item.Handoff != null)
        {
            return IsProvablySingleCompleteNonMember(item.Handoff);
        }

        return IsProvablySingleCompleteNonMember(item.Card, item.Path);
    }

    /// <summary>普通媒体内容物：只是**否定证据**，不参与"是不是分卷"的结论。</summary>
    public static bool IsKnownNormalMedia(ArchiveFixer.Detection.FileNatureCard? card)
        => card?.ContentKind == ArchiveFixer.Detection.FileContentKind.NormalMedia;

    public static bool IsKnownNormalMedia(ArchiveFixer.Detection.VolumeHandoffEntry? entry)
        => entry?.ContentKind == ArchiveFixer.Detection.FileContentKind.NormalMedia;

    public static bool IsKnownNormalMedia(
        IReadOnlyDictionary<string, ArchiveFixer.Detection.VolumeHandoffEntry>? handoffByPath,
        string path)
        => handoffByPath != null
            && handoffByPath.TryGetValue(path, out ArchiveFixer.Detection.VolumeHandoffEntry? entry)
            && IsKnownNormalMedia(entry);

    public static bool IsKnownNormalMedia(VolumeCandidatePieceFacts item) => IsKnownNormalMedia(item.Card);

    /// <summary>
    /// 内容物（图片/视频/音频/文档）事实：浅卡已经能证明就用浅卡；
    /// ZIP 家族需要深探测看条目（只在候选准入点调用，不在每个 FileNature.Inspect 里打开 ZIP）。
    /// </summary>
    public static bool IsKnownContent(VolumeCandidatePieceFacts item)
    {
        if (IsKnownNormalMedia(item))
        {
            return true;
        }

        if (item.Card.EmbeddedOffset > 0)
        {
            return false;
        }

        if (item.Card.ContentClass == FileContentClass.KnownContent
            || item.Handoff?.ContentClass == FileContentClass.KnownContent)
        {
            return true;
        }

        /*
         * 候选已经在同基名上下文里，才允许补一次内容事实：
         * 浅探测能定案就直接用；ZIP 家族才深探测条目。
         */
        if (ContentAdmission.Probe(
                item.Path,
                ContentAdmissionScope.VolumeCandidate,
                deep: false).Class == FileContentClass.KnownContent)
        {
            return true;
        }

        if (item.Card.Format == ArchiveFixer.Detection.VolumeContentFormat.Zip)
        {
            return ContentAdmission.Probe(
                item.Path,
                ContentAdmissionScope.VolumeCandidate,
                deep: true).Class == FileContentClass.KnownContent;
        }

        return false;
    }

    /// <summary>
    /// 盘上这一份是不是普通媒体 / 已证明的内容物（否定证据）。
    ///
    /// <para>按内容事实判：浅卡已定案就直接用；浅探测能定案用浅探测；ZIP 家族才深探测条目。
    /// 前导挂着一个完整归档（`EmbeddedOffset &gt; 0`）的不在这一档（媒体前缀的完整包仍按归档处理）。</para>
    /// </summary>
    public static bool IsKnownContentOnDisk(string path)
    {
        try
        {
            FileNatureCard card = FileNature.Inspect(path);

            if (card.EmbeddedOffset > 0)
            {
                return false;
            }

            if (card.IsKnownContent || card.IsKnownNormalMedia)
            {
                return true;
            }

            if (ContentAdmission.Probe(
                    path,
                    ContentAdmissionScope.VolumeCandidate,
                    deep: false).Class == FileContentClass.KnownContent)
            {
                return true;
            }

            return card.Format == ArchiveFixer.Detection.VolumeContentFormat.Zip
                && ContentAdmission.Probe(
                    path,
                    ContentAdmissionScope.VolumeCandidate,
                    deep: true).Class == FileContentClass.KnownContent;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 字节补齐配对里的尾片：账本里只能是"纯 Unknown"（没有卷号/名字槽/首卷标记/自述整包），
    /// 否则它属于别的组，⛔ 不许抢过来配对。
    /// </summary>
    public static bool IsByteFitTailCandidate(ArchiveFixer.Detection.VolumeHandoffEntry entry)
    {
        return entry.ContentKind != ArchiveFixer.Detection.FileContentKind.NormalMedia
            && entry.ContentClass != ArchiveFixer.Detection.FileContentClass.KnownContent
            && entry.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.Unknown
            && entry.Membership != ArchiveFixer.Detection.VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
            && !entry.NameVolumeIndex.HasValue
            && !entry.FirstVolumeFlag
            && entry.DeclaredTotalBytes == 0
            && !IsProvablySingleCompleteNonMember(entry);
    }

    /// <summary>
    /// 同目录里这一族是否**恰好只有一个**内容首卷。只有一个 ⇒ 可以用内容卡收兄弟片；
    /// 多个 ⇒ 名字仍是必要的歧义消解，不能绕基名。
    /// </summary>
    public static bool HasSingleContentFirstInDirectory(
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
    /// 这片有没有"能证明自己属于这一组"的正面证据：卷号 / 名字槽 / 首片标记 / 内容身份 / ZIP split。
    /// </summary>
    public static bool HasVolumeIdentityEvidence(VolumeCandidatePieceFacts item)
    {
        if (item.VolumeNumber.HasValue
            || item.IsFirstPiece
            || item.Card.FirstVolumeFlag
            || item.Card.BodyKind != ArchiveFixer.Detection.ArchiveBodyKind.Unknown
            || item.Card.Membership == ArchiveFixer.Detection.VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
            || item.Card.HasZipLocalHeader
            || item.Card.ZipEndRecordMeansSplit)
        {
            return true;
        }

        if (item.Handoff is { } handoff
            && (handoff.BodyKind != ArchiveFixer.Detection.ArchiveBodyKind.Unknown
                || handoff.Membership == ArchiveFixer.Detection.VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                || handoff.FirstVolumeFlag
                || handoff.NameVolumeIndex.HasValue))
        {
            return true;
        }

        return ArchiveFixer.Detection.LegacyVolumeNames.TryGetIndex(
            Path.GetFileName(item.Path)).HasValue;
    }

    /// <summary>
    /// 首片事实已知后的几何过滤：只有"能证明自己是这一组的一片"的候选才无条件留下。
    ///
    /// <para>判据（⛔ 不猜）：有卷号 / 名字槽 / 首片标记 / 内容身份 / ZIP split 证据 ⇒ 保留；
    /// 完全没有身份证据的候选，只有**全部无身份候选的字节和正好补齐自述整包**时才整批保留
    /// （那就是"首片 + 唯一尾片"那一档）；否则整批忽略并记日志。</para>
    ///
    /// <para>为什么必须在求和之前：真机 336 的 PNG 被算进已收字节，组报
    /// 636,790,140 &gt; 自述 636,736,722 ⇒ SizeAnomaly ⇒ 永不建组。</para>
    /// </summary>
    public static (List<VolumeCandidatePieceFacts> Kept, int Dropped) FilterPiecesByGeometry(
        List<VolumeCandidatePieceFacts> items,
        long declaredTotalBytes)
    {
        if (declaredTotalBytes <= 0 || items.Count <= 1)
        {
            return (items, 0);
        }

        var known = new List<VolumeCandidatePieceFacts>();
        var unknown = new List<VolumeCandidatePieceFacts>();

        foreach (VolumeCandidatePieceFacts item in items)
        {
            if (HasVolumeIdentityEvidence(item))
            {
                known.Add(item);
            }
            else
            {
                unknown.Add(item);
            }
        }

        if (unknown.Count == 0)
        {
            return (items, 0);
        }

        long knownBytes = known.Sum(item => item.Size);
        long unknownBytes = unknown.Sum(item => item.Size);
        long remainder = declaredTotalBytes - knownBytes;

        /*
         * 无身份候选整批正好补齐 ⇒ 保留（首片 + 唯一尾片；此时无法用名字区分它们，
         * 但字节自述对得上，属于既有"字节补齐配对"同一档证据）。
         * 其余情况（多出来、少一点、正好为 0）⇒ 它们不是这一组的一部分。
         */
        if (remainder > 0 && unknownBytes == remainder && unknown.Count > 0)
        {
            return (items, 0);
        }

        return (known, unknown.Count);
    }
}

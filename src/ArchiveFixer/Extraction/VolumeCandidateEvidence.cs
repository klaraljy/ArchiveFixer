using ArchiveFixer.Detection;
using System.IO;

namespace ArchiveFixer.Extraction;

/// <summary>
/// **证据建模**（第二大步发现层的第 4 段）：把"这一片到底是什么"折成统一事实。
///
/// <para>交接单里已经登记过的片**直接取交接单的底层事实**（⛔ 不再 `FileNature.Inspect`、
/// ⛔ 不重读盘）；只有盘上补进来的兄弟片才回盘上读一次。产出三种形态：
/// 内部证据 <see cref="VolumeCandidatePieceFacts"/>（判同组/定序/判完整性共用）、
/// 公开事实 <see cref="VolumeFacts"/>（第二大步拿得走）、底层内容卡 <see cref="FileNatureCard"/>（族判定用）。</para>
///
/// <para><b>不承载判据</b>：同组判定在 <see cref="VolumeGroupMembership"/>、定序在
/// <see cref="VolumeGroupPieceOrdering"/>、失败/完整结论在 <see cref="VolumeCandidateVerdict"/>；
/// 这里只回答"盘上/交接单给出的原始事实是什么"。</para>
///
/// <para><b>失败模型</b>：读不到内容/大小时，对应字段按"判不出"落（<c>Unknown</c> / <c>0</c>），
/// ⛔ 不抛、不编；"缺什么、为什么判不出"的措辞归 <see cref="VolumeCandidateVerdict"/>。</para>
///
/// <para><b>生命周期</b>：纯静态、无状态；输入是路径 + 交接单（可空），输出与该路径一一对应。
/// 同一路径多次调用结果一致（文件不变时），不缓存、不写盘。</para>
/// </summary>
internal static class VolumeCandidateEvidence
{
    /// <summary>
    /// 一片的证据：**已登记过交接单的片直接用交接单里的底层事实**（⛔ 不重读盘）；
    /// 只有盘上补进来的、没登记过的兄弟片才 `FileNature.Inspect` 一次。
    /// </summary>
    public static VolumeCandidatePieceFacts BuildFacts(
        string path,
        IReadOnlyDictionary<string, ArchiveFixer.Detection.VolumeHandoffEntry>? handoffByPath)
    {
        if (handoffByPath != null && handoffByPath.TryGetValue(path, out ArchiveFixer.Detection.VolumeHandoffEntry? handoff))
        {
            /*
             * ⛔ 不重读盘：交接单里的内容事实直接还原成一张卡（HasZip* 那几格交接单没有，
             * 保持默认 false；ZIP 族的判定在族算法里会结合名字/盘号，不靠这几格猜）。
             */
            var handoffCard = new ArchiveFixer.Detection.FileNatureCard
            {
                Path = handoff.Path,
                Size = handoff.Size,
                Format = handoff.Format,
                Membership = handoff.Membership,
                VolumeNumber = handoff.VolumeNumber,
                FirstVolumeFlag = handoff.FirstVolumeFlag,
                ContentFail = handoff.ContentFail,
                BodyKind = handoff.BodyKind,
                ContentKind = handoff.ContentKind,
                DeclaredTotalBytes = handoff.DeclaredTotalBytes,
                DeclaredVolumeCount = handoff.DeclaredVolumeCount,
                RawVolumeField = handoff.RawVolumeField,
                HasZipEndRecord = handoff.HasZipEndRecord,
                ZipEndRecordMeansSplit = handoff.ZipEndRecordMeansSplit,
                HasZipLocalHeader = handoff.HasZipLocalHeader
            };

            return new VolumeCandidatePieceFacts(
                path,
                handoff.Format,
                handoff.VolumeNumber,
                handoff.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.VolumeFirstPiece,
                handoff.Size,
                handoff.DeclaredVolumeCount,
                handoff.DeclaredTotalBytes,
                handoff,
                handoffCard);
        }

        ArchiveFixer.Detection.FileNatureCard card = ArchiveFixer.Detection.FileNature.Inspect(path);

        long size = card.Size;

        if (size <= 0)
        {
            try
            {
                size = new FileInfo(path).Length;
            }
            catch
            {
                // 读不到大小就按 0 计入（对账时会自然落到"缺片"或"判不出"）。
            }
        }

        return new VolumeCandidatePieceFacts(
            path,
            card.Format,
            card.VolumeNumber,
            card.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.VolumeFirstPiece,
            size,
            card.DeclaredVolumeCount,
            card.DeclaredTotalBytes,
            null,
            card);
    }

    /// <summary>把内部证据折成第二大步拿得走的原始事实（⛔ 不补判不出的字段）。</summary>
    public static VolumeFacts ToPublicFact(VolumeCandidatePieceFacts item) =>
        new(
            item.Path,
            item.Size,
            item.Card.Format,
            item.Card.BodyKind,
            item.Card.VolumeNumber,
            item.Card.FirstVolumeFlag,
            item.Card.Membership,
            item.Card.ContentFail,
            item.Card.DeclaredTotalBytes,
            item.Card.DeclaredVolumeCount,
            item.Card.RawVolumeField,
            item.Handoff?.SourceLayer ?? (Path.GetDirectoryName(item.Path) ?? string.Empty),
            item.Handoff?.Source,
            item.Handoff != null,
            item.Card.HasZipEndRecord,
            item.Card.ZipEndRecordMeansSplit,
            item.Card.HasZipLocalHeader,
            item.Card.CanonicalVolumeName,
            item.Card.FirstVolumeNameByName,
            item.Card.ArchiveBaseName,
            HasIdentity: item.Card.HasIdentity,
            VolumeSerial: item.Card.VolumeSerial,
            FileIndex: item.Card.FileIndex,
            Confidence: VolumeFactConfidence.Content,
            EvidenceSource: item.Handoff != null ? "handoff+content" : "content-card");

    /// <summary>
    /// 每片的底层内容卡（族判定唯一出口要用它；已登记的片由交接单还原，⛔ 不重读盘）。
    /// </summary>
    public static List<FileNatureCard> CardsOf(IEnumerable<VolumeCandidatePieceFacts> items) =>
        items.Select(item => item.Card).ToList();

    /// <summary>
    /// 候选收尾：挂上每片的公开事实/内容卡，并把"已忽略哪些候选"如实补进证据
    /// （⛔ 不改判据，只补证据文本）。
    /// </summary>
    public static VolumeCandidateDiscovery.VolumeCandidate Finish(
        VolumeCandidateDiscovery.VolumeCandidate candidate,
        IReadOnlyList<VolumeFacts> publicFacts,
        IReadOnlyList<FileNatureCard> cards,
        int dropped)
    {
        candidate.PieceFacts = publicFacts;
        candidate.Cards = cards;

        if (dropped > 0)
        {
            candidate.AppendEvidence($"；已忽略 {dropped} 份无法证明属于本组的候选（内容物/无关文件）");
        }

        return candidate;
    }

    /// <summary>文件字节数；读不到按 <c>0</c>（对账时自然落到"缺片 / 判不出"，⛔ 不抛）。</summary>
    public static long FileSizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }
}

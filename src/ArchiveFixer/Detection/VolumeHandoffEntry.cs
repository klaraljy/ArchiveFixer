using System;

namespace ArchiveFixer.Detection
{
    /// <summary>这一份文件是从哪来的（交接单要用它决定"搬还是只建链接"）。</summary>
    public enum VolumeHandoffSource
    {
        /// <summary>用户导入的原件（源目录只读：只许建零字节硬链接）。</summary>
        UserImported = 0,

        /// <summary>本链产出的片（同卷搬、跨卷建链接）。</summary>
        ChainProduced = 1
    }

    /// <summary>
    /// **第一大步的交接事实**（用户 2026-10-22 口径）：每一份"过了闸门"的文件登记一行，
    /// 交给第二大步当原包。⛔ 这不是"待处理队列"，文件本身必须原地不动。
    ///
    /// <para>字段只写**底层已经识别到的事实**（`FileNatureCard` 那一套）：
    /// 基名 / 族 / 大小 / 内容身份（BodyKind）/ 卷号 / 首卷标记 / 所在层 / 来源。
    /// 判不出的字段留空，⛔ 不猜。</para>
    /// </summary>
    public sealed class VolumeHandoffEntry
    {
        public string Path { get; set; } = string.Empty;

        /// <summary>基名（唯一出口 `FileNameHelper.GetArchiveBaseName`）。</summary>
        public string BaseName { get; init; } = string.Empty;

        public VolumeContentFormat Format { get; init; }

        public long Size { get; init; }

        /// <summary>内容身份：`VolumeFirstPiece` / `VolumePiece` / `Unknown`。</summary>
        public ArchiveBodyKind BodyKind { get; init; }

        /// <summary>内容卷号（判不出 = null，⛔ 不编）。</summary>
        public int? VolumeNumber { get; init; }

        /// <summary>首卷标记（RAR4 主头 / RAR5 卷号=1）。</summary>
        public bool FirstVolumeFlag { get; init; }

        /// <summary>
        /// 内容证据的三档结论（`VolumeMember` / `ProvablyNotVolumeMember` / `Unknown`）——
        /// 第二大步判断"要不要收、能不能证伪"的第一判据（用户 2026-10-24：底层识别事实要随交接单过来）。
        /// </summary>
        public VolumeNumberFromContent.VolumeMembershipVerdict Membership { get; init; }

        /// <summary>读不出卷号时是哪一档（给日志与第二大步的判据输入用；成功时 = None）。</summary>
        public VolumeNumberFail ContentFail { get; init; }

        /// <summary>**7z 起始头自述的整包字节数**；判不出 = 0（⛔ 不编）。</summary>
        public long DeclaredTotalBytes { get; init; }

        /// <summary>**按内容算出来的片数**（只有 VolumeFirstPiece 档算得出）；判不出 = 0。</summary>
        public int DeclaredVolumeCount { get; init; }

        /// <summary>RAR 那一族卷号字段的原始值（RAR4 基数锚点用）；判不出 = null。</summary>
        public int? RawVolumeField { get; init; }

        /// <summary>尾部有 zip 的 EOCD（通用分片族的末片证据）；判不出 = false。</summary>
        public bool HasZipEndRecord { get; init; }

        /// <summary>这个 EOCD 说明"我是分片的最后一片"（中央目录偏移 ≥ 本文件长度，或盘号 &gt; 0）。</summary>
        public bool ZipEndRecordMeansSplit { get; init; }

        /// <summary>开头是 zip 本地文件头（通用分片族第 1 片的证据）。</summary>
        public bool HasZipLocalHeader { get; init; }

        /// <summary>它在盘上哪一层（第二大步按这里定入口层）。</summary>
        public string SourceLayer { get; init; } = string.Empty;

        public VolumeHandoffSource Source { get; init; }

        /// <summary>
        /// 第二大步**取用**这一份时打点（谁消费的、什么时候）；没消费 = 空串 / null。
        /// 批末汇总读它算「已消费 N / 待处理 M」；⛔ 不是"已处理完"，只代表交接单已被接手。
        /// </summary>
        public string ConsumedBy { get; set; } = string.Empty;

        /// <summary>取用时间（批末汇总与日志用）。</summary>
        public DateTime? ConsumedAt { get; set; }

        /// <summary>
        /// 第二大步对这一组的**结论**（成功/失败原因/收片数）；还没结论 = 空串。
        /// 批末「第二大步·结论」那几行读它，⛔ 不再从日志里反推。
        /// </summary>
        public string ConsumedVerdict { get; set; } = string.Empty;

        /// <summary>给人看的一行（日志与判据输入共用；⛔ 不猜，判不出的字段如实写"判不出"）。</summary>
        public string Describe() =>
            $"{BaseName} ｜ 族={Format} ｜ {Size} 字节 ｜ 内容身份={BodyKind}"
            + $" ｜ 成员判定={Membership}"
            + $" ｜ 卷号={(VolumeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "判不出")}"
            + $"（原始字段={(RawVolumeField?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "判不出")}"
            + $"；读不出原因={ContentFail}）"
            + $" ｜ 首卷={(FirstVolumeFlag ? "是" : "否")}"
            + $" ｜ 自述整包={(DeclaredTotalBytes > 0 ? DeclaredTotalBytes + " 字节" : "判不出")}"
            + $" ｜ 自述片数={(DeclaredVolumeCount > 0 ? DeclaredVolumeCount + " 片" : "判不出")}"
            + $" ｜ 层={SourceLayer} ｜ 来源={Source}";
    }
}
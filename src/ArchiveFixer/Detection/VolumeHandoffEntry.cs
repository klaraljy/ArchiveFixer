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
        public string Path { get; init; } = string.Empty;

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

        /// <summary>它在盘上哪一层（第二大步按这里定入口层）。</summary>
        public string SourceLayer { get; init; } = string.Empty;

        public VolumeHandoffSource Source { get; init; }

        /// <summary>给人看的一行（日志与判据输入共用）。</summary>
        public string Describe() =>
            $"{BaseName} ｜ 族={Format} ｜ {Size} 字节 ｜ 内容身份={BodyKind}"
            + $" ｜ 卷号={(VolumeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "判不出")}"
            + $" ｜ 首卷={(FirstVolumeFlag ? "是" : "否")} ｜ 层={SourceLayer} ｜ 来源={Source}";
    }
}
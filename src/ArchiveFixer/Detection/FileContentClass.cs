namespace ArchiveFixer.Detection
{
    /// <summary>
    /// **非压缩文件事实**（用户 2026-11-11：非压缩文件我们不碰；不需要识别所有格式，
    /// 只要在“准备处理这一份”之前证明它是内容物，判不出就不动）。
    ///
    /// <para>与 <see cref="FileContentKind"/> 分开：`FileContentKind` 只描述普通媒体容器；
    /// 这里回答的是更上层的准入问题 —— `KnownContent`（非压缩文件）/ `Archive`（压缩包）/
    /// `Unknown`（判不出）。</para>
    /// </summary>
    public enum FileContentClass
    {
        /// <summary>判不出（⛔ 不猜；没有卷证据时按“不碰”处理）。</summary>
        Unknown = 0,

        /// <summary>已证明是非压缩文件（图片/视频/音频/文档/文本内容物）。</summary>
        KnownContent = 1,

        /// <summary>已证明是压缩包/归档容器（含普通 ZIP）。</summary>
        Archive = 2
    }
}

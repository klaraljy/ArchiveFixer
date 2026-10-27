namespace ArchiveFixer.Detection
{
    /// <summary>
    /// **RAR 族内部的细分**（第二大步专属算法用）。
    ///
    /// <para>RAR5 与 RAR4 的签名不同，RAR4 的新/老编号又决定"卷号从哪来"：
    /// RAR5 有绝对卷号；RAR4 新编号有卷尾原始字段；RAR4 老编号只能靠名字约定。
    /// 三类的判据完全不同，不能只用一个 <see cref="VolumeContentFormat.Rar"/> 混着办。</para>
    ///
    /// <para>⛔ 这是**内容事实的分类**，不是"名字像不像"；名字只用于后续槽位匹配。</para>
    /// </summary>
    public enum RarVariant
    {
        /// <summary>判不出。</summary>
        Unknown = 0,

        /// <summary>RAR5（签名 <c>52 61 72 21 1A 07 01 00</c>）。</summary>
        Rar5 = 1,

        /// <summary>RAR 1.5–4.x 新编号族（主头带 <c>MHD_NEWNUMBERING</c>）。</summary>
        Rar4New = 2,

        /// <summary>RAR 1.5–4.x 老编号族（<c>.rar</c> + <c>.r00/.r01…</c>）。</summary>
        Rar4Old = 3,

        /// <summary>RAR5 加密头（卷号读不出，只能按标准名快速通道或判不出）。</summary>
        Rar5HeadersEncrypted = 4,

        /// <summary>RAR4 加密头（卷号/首卷标志读不出）。</summary>
        Rar4HeadersEncrypted = 5
    }

    /// <summary>
    /// **ZIP 族内部的两种分卷**（命名、入口、内容证据都不同，⛔ 不能混）。
    /// </summary>
    public enum ZipVariant
    {
        /// <summary>判不出。</summary>
        Unknown = 0,

        /// <summary>跨盘 ZIP：<c>X.z01…X.zip</c>，入口是末片 <c>X.zip</c>。</summary>
        Spanned = 1,

        /// <summary>通用分片：<c>X.zip.001…X.zip.00N</c>，入口是第 1 片。</summary>
        Numbered = 2
    }
}

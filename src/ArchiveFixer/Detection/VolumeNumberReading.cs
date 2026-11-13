namespace ArchiveFixer.Detection
{
    /// <summary>**一卷的内容**读出来的东西（只读头部与尾部若干字节，不解析归档内容）。</summary>
    public sealed class VolumeNumberReading
    {
        /// <summary>这一卷的完整路径。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>内容格式（7z / RAR / ZIP / 认不出）。</summary>
        public VolumeContentFormat Format { get; init; }

        /// <summary>内容自述"我是分卷组的一员"。</summary>
        public bool IsVolumeMember { get; init; }

        /// <summary>
        /// 这一卷的字节数（读不到就是 0）。**只给"尺寸规律"那一条判据用** ——
        /// 分卷组里除最后一片外都是切分上限那么大的满片，所以"该接在末片前面"的那一片不可能比末片还小。
        /// </summary>
        public long Size { get; init; }

        /// <summary>卷号（**1 起**）。null = 内容里没有盘号（跨盘 zip 的非末片），或还没定（RAR 1.5–4.x 要看整组）。</summary>
        public int? Number { get; init; }

        /// <summary>能确定总片数时给出（跨盘 zip 的末片：盘号 + 1）。</summary>
        public int? Total { get; init; }

        /// <summary>
        /// **这一份的尾部有 zip 的 EOCD**（`PK\x05\x06`，且注释长度自洽）—— 设计文档 §6.6 A / 工单 #10。
        ///
        /// <para>通用分片族 `X.zip.001..00N` 里，**只有最后一片的尾部有 EOCD**，其余是裸切块；
        /// 7-Zip 用 `-v` 切出来的那种末片 EOCD 写的是"盘号 0"（不是 PKZIP 跨盘那套），
        /// 所以它算不上"跨盘成员"（`IsVolumeMember = false`），但"尾部有 EOCD"这条**内容事实**仍然成立
        /// ⇒ 认末片时用它，而不是只靠名字序号。</para>
        /// </summary>
        public bool HasZipEndRecord { get; init; }

        /// <summary>
        /// **这个 EOCD 说明"我是分片的最后一片"**（设计文档 §6.6 A 的"拼回合法性"那条证据）：
        /// 判据 = EOCD 里的**中央目录偏移 ≥ 本文件长度**（中央目录起在更早的片里），或盘号 &gt; 0。
        ///
        /// <para>⛔ 单卷 zip 的尾部也有 EOCD，但它的中央目录就在自己身上 ⇒ 这条为 <c>false</c>。
        /// 现场（新造 30 份的困难档）：造包器给**每一片单独套了 0–2 层归档**（`Add-Wraps`），
        /// 盘上每个文件其实是一个"装着一片的独立归档"（`Rar!` / 多个 `PK` 头就是这么来的）——
        /// 把它们当成"分片"硬拼，就永远解不出来；正确行为是**先各自按单卷解**，再收拢。</para>
        /// </summary>
        public bool ZipEndRecordMeansSplit { get; init; }

        /// <summary>
        /// **这一份的开头是 zip 的本地文件头**（`PK\x03\x04`）—— 通用分片族里"哪一片是第 1 片"的内容证据
        /// （设计文档 §6.6 A / 工单 #10）。7-Zip `-tzip -v` 切出来的第 1 片就以本地文件头开头，
        /// 中间片是从数据中间切开的（四种 PK 签名之一，但不是本地头）。
        /// </summary>
        public bool HasZipLocalHeader { get; init; }

        /// <summary>
        /// RAR **1.5–4.x**（WinRAR 里叫 RAR4；签名与 RAR3 是同一个，靠签名分不出 3 与 4）
        /// 卷尾归档结尾块里的**原始**卷号字段值。基数（0 起还是 1 起）在 RAR 的文档里没写，
        /// 只能靠"整组必须连成 1..N"来定 —— 所以这里保留原值，不在单卷上做加减。
        /// </summary>
        public int? RawField { get; init; }

        /// <summary>RAR 1.5–4.x 主头的 <c>MHD_FIRSTVOLUME</c>（"这一卷就是第 1 卷"）。</summary>
        public bool FirstVolumeFlag { get; init; }

        /// <summary>
        /// **这个族的"主头"已经成功读出来了**（RAR5 主归档头 / RAR4 主头且 CRC 通过）。
        ///
        /// <para>它把"读成功且证明不是分卷成员"与"结构就不对/不是归档开头"分开 ——
        /// `Fail` 那一位做不到这件事（两种都落 `NotVolumeMember`），而"单卷 ⇒ SingleComplete"
        /// 只允许认前者（2026-10-22，`24242424` 收口）。</para>
        /// </summary>
        public bool MainHeaderRead { get; init; }

        /// <summary>RAR 1.5–4.x 主头的编号族。</summary>
        public RarNumberingFamily RarNumbering { get; init; }

        /// <summary>RAR 族内细分（RAR5 / RAR4 新 / RAR4 老 / 加密头）；判不出 = Unknown。</summary>
        public RarVariant RarVariant { get; init; }

        /// <summary>读不出来 / 认不出来时是哪一档。</summary>
        public VolumeNumberFail Fail { get; init; }

        /// <summary>给文案用的数字（片数等；没有就是 0）。</summary>
        public int Detail { get; init; }
    }
}


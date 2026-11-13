using System.Globalization;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// **底层：一份文件"到底是什么"** —— 一张卡，四个来源的事实各就各位（用户 2026-10-11 口径：
    /// 「就是统筹到同一组上面去…我们就是先看看**底层能够识别到文件是什么东西**」）。
    ///
    /// <para><b>为什么要有这张卡</b>：分卷后缀那套专属算法（<see cref="ExtensionHelper"/> /
    /// <see cref="FileNameHelper"/> / <see cref="VolumeNameRepair"/> / <see cref="VolumeGroupDetector"/>）已经做了很多文章，
    /// 内容证据（<see cref="VolumeNumberFromContent"/>）也有一套；可**上面每一处判据都在自己重新拼**这四类事实 ——
    /// 于是同一个文件在不同地方能被认成不同的东西（第 125 轮那次误接、第 127 轮轮判定被名字带偏，都是这么来的）。
    /// ⇒ **"这一份是什么"只许有一个出口**，就是这里：上面只管取事实，⛔ 不许自己再判一遍。</para>
    ///
    /// <para><b>四类事实（按证据强度）</b>：</para>
    /// <list type="number">
    /// <item><description><b>内容 · 格式</b>：魔数（<see cref="VolumeContentInference.SniffFormat"/>）—— 不信后缀。</description></item>
    /// <item><description><b>内容 · 分卷身份</b>：<see cref="VolumeNumberFromContent"/> 读归档自己写的头
    /// （RAR5 主头 <c>MHFL_VOLUME 0x0001</c> / <c>MHFL_VOLNUMBER 0x0002</c>、RAR4 <c>MHD_FIRSTVOLUME</c>、
    /// 跨盘 ZIP 的 EOCD 盘号）⇒ 三档结论 + 卷号 + 首卷标志。**7z 那一族格式里没有卷号 ⇒ 只能是"判不出"**
    /// （判据在 <see cref="VolumeContentInference"/> + 试开那边）。</description></item>
    /// <item><description><b>名字 · 分卷后缀那套算法</b>：族 + 卷标记 + 规范卷名 + 本族第 1 卷名（入口包名）
    /// —— 脏名（`111.par删t1.ra除r`）也能算出来；它**只是线索**，⛔ 不是身份。</description></item>
    /// <item><description><b>盘上 · 物理身份</b>：卷序列号 + 文件索引（<see cref="FileIdentity"/>）——
    /// 同一卷里改名 / 移动之后**不变**（拷贝算新文件），所以"是不是同一份"用它，⛔ 不用路径。</description></item>
    /// </list>
    ///
    /// <para>⚠ 本类**不新造任何判据**：四个来源全部转调既有唯一出口；它只负责"把四份事实摆在一张卡上"。</para>
    /// </summary>
    public sealed class FileNatureCard
    {
        /// <summary>这一份的完整路径。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>字节数（读不到 = 0）。</summary>
        public long Size { get; init; }

        // ===== ① 内容 · 格式（魔数）=====

        /// <summary>魔数认出来的格式（⛔ 与后缀无关）。</summary>
        public VolumeContentFormat Format { get; init; }

        // ===== ② 内容 · 分卷身份（归档自己写的头）=====

        /// <summary>内容证据的三档结论。</summary>
        public VolumeNumberFromContent.VolumeMembershipVerdict Membership { get; init; }

        /// <summary>内容里读出来的卷号（1 起）；判不出 = null。</summary>
        public int? VolumeNumber { get; init; }

        /// <summary>内容自述"我就是第 1 卷"（RAR 1.5–4.x 的主头标记；RAR5 第 1 卷表现为"没有卷号字段"）。</summary>
        public bool FirstVolumeFlag { get; init; }

        /// <summary>读不出卷号时是哪一档（给日志与文案用；成功时 = <see cref="VolumeNumberFail.None"/>）。</summary>
        public VolumeNumberFail ContentFail { get; init; }

        // ===== ②b 内容 · 专属算法（单一 vs 分卷；用户 2026-10-13 口径）=====

        /// <summary>内容自证"完整的单卷 / 某组的第一片 / 某组的一片"（见 <see cref="ArchiveBodyKind"/>）。</summary>
        public ArchiveBodyKind BodyKind { get; init; }

        /// <summary>
        /// 内容物事实（见 <see cref="FileContentKind"/>）：只有"媒体结构正好占满整个文件、
        /// 且没有尾部归档"这一档才置 <see cref="FileContentKind.NormalMedia"/>；其余一律 Unknown。
        /// </summary>
        public FileContentKind ContentKind { get; init; }

        /// <summary>是不是普通媒体内容物（⛔ 否定证据，不许拿来当"它一定是分卷/不是分卷"）。</summary>
        public bool IsKnownNormalMedia => ContentKind == FileContentKind.NormalMedia;

        /// <summary>
        /// 内容物/归档的分类事实（唯一出口 <see cref="FileContentProbe"/>）：
        /// `KnownContent` = 非压缩文件，自动处理不碰；`Archive` = 已证明的归档；
        /// `Unknown` = 判不出（没有卷证据时也不碰）。
        /// </summary>
        public FileContentClass ContentClass { get; init; }

        /// <summary>内容格式名（PNG/WebP/MP4/PDF/OOXML…；判不出 = 空串）。</summary>
        public string ContentFormat { get; init; } = string.Empty;

        /// <summary>是不是已证明的非压缩文件。</summary>
        public bool IsKnownContent => ContentClass == FileContentClass.KnownContent;

        /// <summary>RAR 族内细分（RAR5 / RAR4 新 / RAR4 老 / 加密头）；判不出 = Unknown。</summary>
        public RarVariant RarVariant { get; init; }

        /// <summary>加密结论（RAR 读头；其它族保持 Unknown；判不出 = Unknown，⛔ 不猜）。</summary>
        public ArchiveEncryptionState Encryption { get; init; }

        /// <summary>
        /// **7z 起始头明文自述的整包字节数**（<c>32 + NextHeaderOffset + NextHeaderSize</c>）；判不出 = 0。
        ///
        /// <para>它是"整包应当有多少字节"这条硬证据（<see cref="SevenZipStartHeader"/>）在卡上的落点 ——
        /// 7z 的中间片内容里没有身份信息，唯一能自证的数就是这个。</para>
        /// </summary>
        public long DeclaredTotalBytes { get; init; }

        /// <summary>
        /// **按内容算出来的片数**（只有 <see cref="ArchiveBodyKind.VolumeFirstPiece"/> 那一档算得出：
        /// 整包自述字节数 ÷ 这一片自己的字节数，向上取整 —— 7z 分卷除末片外等大）；
        /// 判不出 = 0（⛔ 不编）。
        /// </summary>
        public int DeclaredVolumeCount { get; init; }

        /// <summary>
        /// **归档在文件里的起点**（&gt; 0 = 前面垫着媒体/别的数据，即"媒体前缀"那一档；0 = 从文件头就是归档）。
        /// 判不出 = 0。
        /// </summary>
        public long EmbeddedOffset { get; init; }

        /// <summary>媒体前缀那一档的**归档区间字节数**（判不出 = 0，⛔ 不编）。</summary>
        public long EmbeddedLength { get; init; }

        /// <summary>
        /// **内容里那个"卷号字段"的原始值**（⛔ 不是 1 起的卷号）：只有 RAR 一族给得出
        /// （RAR5 主头的 <c>MHFL_VOLUME_NUMBER</c>、RAR4 归档结尾块的 <c>EARC_VOLNUMBER</c>）；判不出 = null。
        ///
        /// <para>为什么要留原值：RAR4 老编号族那一档**基数待定**（见
        /// <see cref="VolumeNumberFromContent"/> 里 `ReadRar4Legacy` 的注释：「第 1 卷 0、第 2 卷 1」是
        /// 真样本的字节，但格式没写死基数）⇒ 单看一份读不出"它是第几卷"，
        /// 而**整组之间的相对大小**是可靠的：以第一片那个值为锚点，差值就是卷号差。
        /// 这条证据的用途 = 非 7z 族"按内容补齐槽位"那一档（用户 2026-10-13 口径：名字读不出卷号
        /// 是底层识别的事）—— 第一片由 <see cref="BodyKind"/> 认，其余几片靠这个原值定序。</para>
        /// </summary>
        public int? RawVolumeField { get; init; }

        /// <summary>
        /// 这一份的**尾部有 zip 的 EOCD**（设计文档 §6.6 A / 工单 #10）：通用分片族里它 = **末片**的证据。
        /// </summary>
        public bool HasZipEndRecord { get; init; }

        /// <summary>这个 EOCD 说明"我是分片的最后一片"（中央目录偏移 ≥ 本文件长度，或盘号 &gt; 0）。</summary>
        public bool ZipEndRecordMeansSplit { get; init; }

        /// <summary>
        /// 这一份的**开头是 zip 本地文件头** `PK\x03\x04`：通用分片族里它 = **第 1 片**的证据。
        /// </summary>
        public bool HasZipLocalHeader { get; init; }

        // ===== ③ 名字 · 分卷后缀那套算法（线索，不是身份）=====

        /// <summary>按名字算出来的卷号（脏名也算得出）；不是分卷样子的名字 = null。</summary>
        public int? VolumeIndexByName { get; init; }

        /// <summary>按名字推出来的**本族第 1 卷名**（跨盘 ZIP 族 = 末片 `X.zip`，也就是入口包名）；判不出 = null。</summary>
        public string? FirstVolumeNameByName { get; init; }

        /// <summary>按名字算出来的**规范卷名**（`111.part删2.ra除r` ⇒ `111.part2.rar`）；判不出 = 空串。</summary>
        public string CanonicalVolumeName { get; init; } = string.Empty;

        /// <summary>归档基名（唯一出口 <see cref="FileNameHelper.GetArchiveBaseName"/>）；判不出 = 空串。</summary>
        public string ArchiveBaseName { get; init; } = string.Empty;

        // ===== ④ 盘上 · 物理身份 =====

        /// <summary>拿得到物理身份吗（文件在盘上、能打开句柄）。</summary>
        public bool HasIdentity { get; init; }

        /// <summary>所在卷的序列号（与 <see cref="FileIndex"/> 合起来 = 这一份文件的身份）。</summary>
        public ulong VolumeSerial { get; init; }

        /// <summary>文件索引（同一卷内唯一；**改名 / 移动不变**，拷贝会变）。</summary>
        public ulong FileIndex { get; init; }

        /// <summary>
        /// **这一份要不要参与"按分卷组办"的动作**（接片 / 收卷 / 轮判定的分卷那一支）。
        ///
        /// <para>判据：内容证据**可证"不是分卷组的一员"** ⇒ **不参与**（它就是一份普通的单卷归档 / 别的文件）；
        /// 其余两种（是分卷 / 判不出）⇒ **参与**（判不出照旧按名字 —— ⛔ 不许把"判不出"当"不是"）。</para>
        /// </summary>
        public bool ParticipatesInVolumeHandling =>
            Membership != VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember;

        /// <summary>一行判据输入（⛔ 只写程序当场算出来的值）。</summary>
        public string Describe()
        {
            string number = VolumeNumber.HasValue
                ? VolumeNumber.Value.ToString(CultureInfo.InvariantCulture)
                : "判不出";
            string byName = VolumeIndexByName.HasValue
                ? VolumeIndexByName.Value.ToString(CultureInfo.InvariantCulture)
                : "判不出";
            string identity = HasIdentity
                ? string.Format(CultureInfo.InvariantCulture, "{0:X8}:{1:X}", VolumeSerial, FileIndex)
                : "判不出";

            return $"「{System.IO.Path.GetFileName(Path)}」｜内容：格式={Format} 分卷身份={Membership}"
                + $" 卷号={number} 首卷标志={FirstVolumeFlag} 原始卷号字段={(RawVolumeField.HasValue ? RawVolumeField.Value.ToString(CultureInfo.InvariantCulture) : "判不出")} 结论={ContentFail}"
                + $" 专属算法：单卷/分卷={BodyKind} 内容物={ContentKind} RAR变体={RarVariant} 加密={Encryption}"
                + (DeclaredTotalBytes > 0 ? $" 自述整包={DeclaredTotalBytes} 字节" : string.Empty)
                + (DeclaredVolumeCount > 0 ? $" 按内容算的片数={DeclaredVolumeCount}" : string.Empty)
                + $"｜名字：卷号={byName} 本族第1卷={FirstVolumeNameByName ?? "判不出"} 规范名={CanonicalVolumeName}"
                + $" 基名={ArchiveBaseName}"
                + $"｜盘上：身份={identity}"
                + $"｜⇒ 参与分卷那一套={ParticipatesInVolumeHandling}";
        }
    }
}

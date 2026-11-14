namespace ArchiveFixer.Detection;

/// <summary>
/// **7z 加密判据的唯一判定出口**：把 next header 走读到的结构事实（<see cref="SevenZipHeaderWalk"/>）
/// 落成三档结论 + 依据。结构解析不会自己下结论，结论只在这里出现。
///
/// <para><b>判据（与拆类前逐条一致，⛔ 一个字都不许改）</b>：</para>
/// <list type="bullet">
/// <item><description><c>kEncodedHeader</c> 且解码链里有 AES ⇒ <see cref="ArchiveEncryptionState.HeadersEncrypted"/>
/// （<c>-mhe</c> / <c>-hp</c>）；</description></item>
/// <item><description><c>kHeader</c> 且 MainStreamsInfo 的 coder 链里有 AES ⇒
/// <see cref="ArchiveEncryptionState.DataEncrypted"/>（<c>-p</c>）；</description></item>
/// <item><description><c>kHeader</c> 且链里没有 AES ⇒ <see cref="ArchiveEncryptionState.NotEncrypted"/>。</description></item>
/// </list>
///
/// <para><b>⚠ 那条必须记住的"不知道"</b>：<c>kEncodedHeader</c> 而解码链里没有 AES ⇒
/// <see cref="ArchiveEncryptionState.Unknown"/>，⛔ **不是"没加密"** —— 头被压缩时文件数据的
/// coder 链要解压之后才看得见。实测 <c>many-p.7z</c>（80 个文件 + <c>-p</c>）与
/// <c>many-plain.7z</c>（同样 80 个文件、不加密）的编码头逐字节同构（都只有 LZMA），
/// 前者却是真加密的 ⇒ 这一档只能说"不知道"。</para>
///
/// <para>依据文案（<see cref="ArchiveEncryptionReading.Basis"/>）只写结构化事实：命中哪个结构、
/// 哪一步读不下去；⛔ 不写中文判断，也不许下游拿文案当判据。</para>
/// </summary>
internal static class SevenZipEncryptionVerdict
{
    /// <summary>
    /// 把一次走读落成结论。⛔ 判据只看 <see cref="SevenZipHeaderShape"/>（+ 那两个细节字段），
    /// 不比较任何文案；<paramref name="bytesRead"/> 原样带进证据（读入量上限的证据口径）。
    /// </summary>
    internal static ArchiveEncryptionReading Conclude(SevenZipHeaderWalk walk, int bytesRead) => walk.Shape switch
    {
        SevenZipHeaderShape.EmptyHeader =>
            SevenZipEncryptionEvidence.Unknown(bytesRead, "next header 是空的"),

        SevenZipHeaderShape.NotA7zHeader =>
            SevenZipEncryptionEvidence.Unknown(
                bytesRead,
                "next header 的第一个字节既不是 kHeader(0x01) 也不是 kEncodedHeader(0x17)"),

        SevenZipHeaderShape.EncodedHeaderEncrypted =>
            SevenZipEncryptionEvidence.Encrypted(
                bytesRead,
                ArchiveEncryptionState.HeadersEncrypted,
                "7z 编码头（kEncodedHeader）的解码链里有 AES coder（0x06F10701）：头也加密（-mhe）"),

        SevenZipHeaderShape.EncodedHeaderWithoutAes =>
            SevenZipEncryptionEvidence.Unknown(
                bytesRead,
                "kEncodedHeader 的解码链里没有 AES，而文件数据的 coder 藏在编码流里（解压后才看得见）"),

        SevenZipHeaderShape.EncodedHeaderWithoutCoderInfo =>
            SevenZipEncryptionEvidence.Unknown(
                bytesRead,
                "kEncodedHeader 的 StreamsInfo 里没有 kUnPackInfo：读不出解码链"),

        SevenZipHeaderShape.EncodedHeaderUnreadable =>
            SevenZipEncryptionEvidence.Unknown(
                bytesRead,
                "kEncodedHeader 的解码链读不出来：" + walk.Failure),

        SevenZipHeaderShape.MainStreamsEncrypted =>
            SevenZipEncryptionEvidence.Encrypted(
                bytesRead,
                ArchiveEncryptionState.DataEncrypted,
                "7z 明文头 MainStreamsInfo 的 coder 链里有 AES coder（0x06F10701）：文件数据加密（-p）"),

        SevenZipHeaderShape.MainStreamsPlain =>
            SevenZipEncryptionEvidence.Plain(
                bytesRead,
                "7z 明文头 MainStreamsInfo 的 coder 链里没有 AES coder"),

        SevenZipHeaderShape.MainStreamsWithoutCoderInfo =>
            SevenZipEncryptionEvidence.Unknown(
                bytesRead,
                "MainStreamsInfo 里没有 kUnPackInfo：读不出 coder 链"),

        SevenZipHeaderShape.MainStreamsUnreadable =>
            SevenZipEncryptionEvidence.Unknown(
                bytesRead,
                "MainStreamsInfo 读不出来：" + walk.Failure),

        SevenZipHeaderShape.ReachedFilesInfo =>
            SevenZipEncryptionEvidence.Plain(
                bytesRead,
                "头走到 kFilesInfo 也没有 MainStreamsInfo：这一包没有数据流"),

        SevenZipHeaderShape.AdditionalStreamsInfo =>
            SevenZipEncryptionEvidence.Unknown(
                bytesRead,
                "头里有 kAdditionalStreamsInfo：这一档的结构读不出来"),

        SevenZipHeaderShape.UnexpectedProperty =>
            SevenZipEncryptionEvidence.Unknown(
                bytesRead,
                "头里出现了预期之外的属性 0x" + walk.PropertyId.ToString("X2")),

        SevenZipHeaderShape.EndedWithoutMainStreams =>
            SevenZipEncryptionEvidence.Plain(
                bytesRead,
                "头走到 kEnd 都没有 MainStreamsInfo：这一包没有数据流"),

        SevenZipHeaderShape.ArchivePropertiesUnreadable =>
            SevenZipEncryptionEvidence.Unknown(
                bytesRead,
                "ArchiveProperties 读不出来：" + walk.Failure),

        _ => SevenZipEncryptionEvidence.Unknown(
            bytesRead,
            "未登记的头形状：" + walk.Shape)
    };
}

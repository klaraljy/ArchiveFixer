namespace ArchiveFixer.Detection;

/// <summary>
/// **next header 走读到的结构形状** —— 7z 头解析的唯一产物词表。
///
/// <para>每个值都对应"走到哪一步停下的"，而**不是**"加没加密"：结论由
/// <see cref="SevenZipEncryptionVerdict"/> 按形状（外加 <see cref="SevenZipHeaderWalk.Failure"/> /
/// <see cref="SevenZipHeaderWalk.PropertyId"/>）落成三档。这样拆开的好处是：
/// 结构解析只回答"我看到什么"，判据只回答"这算哪一档"，两边可以分别核对。</para>
/// </summary>
internal enum SevenZipHeaderShape
{
    /// <summary>next header 是空的（一个字节都读不出来）。</summary>
    EmptyHeader = 0,

    /// <summary>第一个字节既不是 <c>kHeader(0x01)</c> 也不是 <c>kEncodedHeader(0x17)</c>。</summary>
    NotA7zHeader = 1,

    /// <summary><c>kEncodedHeader</c> 的解码链里有 AES（<c>-mhe</c> / <c>-hp</c>）。</summary>
    EncodedHeaderEncrypted = 2,

    /// <summary><c>kEncodedHeader</c> 的解码链里没有 AES（头被压缩 ⇒ 文件数据的 coder 链解压后才看得见）。</summary>
    EncodedHeaderWithoutAes = 3,

    /// <summary><c>kEncodedHeader</c> 的 StreamsInfo 里没有 <c>kUnPackInfo</c>（读不出解码链）。</summary>
    EncodedHeaderWithoutCoderInfo = 4,

    /// <summary><c>kEncodedHeader</c> 的解码链读不出来（<see cref="SevenZipHeaderWalk.Failure"/> 带细节）。</summary>
    EncodedHeaderUnreadable = 5,

    /// <summary>明文头 MainStreamsInfo 的 coder 链里有 AES（<c>-p</c>）。</summary>
    MainStreamsEncrypted = 6,

    /// <summary>明文头 MainStreamsInfo 的 coder 链里没有 AES。</summary>
    MainStreamsPlain = 7,

    /// <summary>明文头 MainStreamsInfo 里没有 <c>kUnPackInfo</c>（读不出 coder 链）。</summary>
    MainStreamsWithoutCoderInfo = 8,

    /// <summary>明文头 MainStreamsInfo 读不出来（<see cref="SevenZipHeaderWalk.Failure"/> 带细节）。</summary>
    MainStreamsUnreadable = 9,

    /// <summary>头走到 <c>kFilesInfo</c> 也没有 MainStreamsInfo（这一包没有数据流）。</summary>
    ReachedFilesInfo = 10,

    /// <summary>头里有 <c>kAdditionalStreamsInfo</c>（这一档的结构读不出来）。</summary>
    AdditionalStreamsInfo = 11,

    /// <summary>头里出现了预期之外的属性（<see cref="SevenZipHeaderWalk.PropertyId"/> 是那个字节）。</summary>
    UnexpectedProperty = 12,

    /// <summary>头走到 <c>kEnd</c>（或读到缓冲区末尾）都没有 MainStreamsInfo（这一包没有数据流）。</summary>
    EndedWithoutMainStreams = 13,

    /// <summary><c>kArchiveProperties</c> 读不出来（<see cref="SevenZipHeaderWalk.Failure"/> 带细节）。</summary>
    ArchivePropertiesUnreadable = 14
}

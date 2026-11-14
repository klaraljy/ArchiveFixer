using System.Collections.Generic;

namespace ArchiveFixer.Detection;

/// <summary>
/// **只读头部的 7z 加密判读**（用户 2026-09-30 任务：识别阶段看得出 7z 加没加密，
/// ⛔ 不额外调引擎、不引入依赖、不把包体读进来）。
///
/// <para><b>为什么要有它</b>：识别阶段过去只看开头那 6 个魔数字节。而 7z 的加密信号写在
/// **coder 链**里（AES-256 的 method ID = <c>06 F1 07 01</c>），要么在明文主头里（<c>-p</c>），
/// 要么在"编码头"的解码链里（<c>-mhe</c>）—— 两种都只看开头 6 个字节是看不见的。
/// 后果与 RAR 那一档一样：批首那句"本批有 N 个包没有可用密码"的预判对 7z 整档漏报；
/// 而 <c>-mhe</c> 更狠 —— 引擎侧连条目名都列不出来（<c>7z l -slt</c> 一条都不报），
/// 是"程序现在完全不知道它加密"的那一档。</para>
///
/// <para><b>7z 的布局（官方 7zFormat.txt，逐字节核过真样本）</b>：</para>
/// <list type="number">
/// <item><description><b>起始头 32 字节</b> = 签名(6) + 版本(2) + StartHeaderCRC(4) +
/// <b>NextHeaderOffset</b>(LE64，相对偏移 32 起算) + <b>NextHeaderSize</b>(LE64) + NextHeaderCRC(4)。
/// 四个单文件样本都满足 <c>32 + NextHeaderOffset + NextHeaderSize == 文件长度</c> ——
/// 这条自洽式就是我们的布局闸门（不成立一律"不知道"）。</description></item>
/// <item><description><b>next header</b> 的第一字节：<c>0x01</c> = <c>kHeader</c>（明文头）、
/// <c>0x17</c> = <c>kEncodedHeader</c>（头本身被编码 —— 压缩或加密）。</description></item>
/// <item><description><b>StreamsInfo</b> = <c>[kPackInfo(0x06)] [kUnPackInfo(0x07)] [kSubStreamsInfo(0x08)] kEnd(0x00)</c>；
/// <c>kUnPackInfo</c> 里 <c>kFolder(0x0B)</c> 给出每个文件夹的 coder 链，
/// <b>coder</b> = flags（低 4 位 = ID 字节数 / <c>0x10</c> = 复杂（带入出流个数）/ <c>0x20</c> = 带属性）
/// → ID 字节 → 可选入/出流个数 → 可选属性。数字是 7z 变长编码（首字节高位连续 1 的个数 = 后续字节数，
/// **低位在前**，不是 RAR5 那种 7 位一组的 vint）。</description></item>
/// </list>
///
/// <para><b>三档结论是怎么定的</b>（其余一律"不知道"）：</para>
/// <list type="bullet">
/// <item><description><c>kEncodedHeader</c> 且解码链里有 AES ⇒ <see cref="ArchiveEncryptionState.HeadersEncrypted"/>
/// （<c>-mhe</c> / <c>-hp</c>）；</description></item>
/// <item><description><c>kHeader</c> 且 MainStreamsInfo 的 coder 链里有 AES ⇒
/// <see cref="ArchiveEncryptionState.DataEncrypted"/>（<c>-p</c>）；</description></item>
/// <item><description><c>kHeader</c> 且链里没有 AES ⇒ <see cref="ArchiveEncryptionState.NotEncrypted"/>。</description></item>
/// </list>
///
/// <para><b>⚠ 一条必须记住的"不知道"</b>：<c>kEncodedHeader</c> 而**解码链里没有 AES** 时，
/// 结论是 <see cref="ArchiveEncryptionState.Unknown"/>，⛔ **不是"没加密"** ——
/// 头被压缩时文件数据的 coder 链要解压之后才看得见。实测 <c>many-p.7z</c>（80 个文件 + <c>-p</c>）
/// 的编码头里只有 LZMA、看不见任何 AES，可它是**真加密**的（<c>7z t -pWrongPassword</c> 报
/// "Wrong password?"）。而普通包 <c>many-plain.7z</c> 的编码头里同样只有 LZMA ——
/// 两者在这 64 KiB 里**逐字节同构**，谁也没法区分 ⇒ 这一档只能如实说"不知道"。
/// （引擎侧：<c>7z l -slt</c> 对 <c>-p</c> 包报 80 条 <c>Encrypted = +</c>，
/// 对普通包报 81 条 <c>Encrypted = -</c> —— 也就是说这一档我们确实比引擎弱，如实写在
/// <c>docs/真机事故复盘.md</c> 里。）</para>
///
/// <para><b>多卷 7z</b>：7z 分卷是**原样切**的（<c>-v100k</c> 切出来的每一卷就是整包的一段连续字节），
/// 元数据在**最后一卷**，所以"只看第 1 卷"读不出 7z 的加密。判据是
/// <c>32 + NextHeaderOffset</c> 落在整个卷组的**拼接坐标**里 → 卷长用 <see cref="FileInfo"/> 求和 →
/// 只读覆盖该范围的那一卷（跨两卷就读两段）。单文件判读就是"只有一卷"的特例，同一条代码路径
/// （真样本实测：<c>32 + offset + size == 三卷长度之和</c>，逐字节核过）。</para>
///
/// <para><b>实现分工</b>（公开 API 只此一处；判据一个字都不在这里）：</para>
/// <list type="bullet">
/// <item><description>一次判读的编排 / 起始头布局闸门 → <see cref="SevenZipEncryptionScan"/>；</description></item>
/// <item><description>"字节从哪来、读多少" → <see cref="SevenZipVolumeRangeReader"/>；</description></item>
/// <item><description>next header 的结构走读 → <see cref="SevenZipHeaderWalker"/>；</description></item>
/// <item><description>加密判据（AES coder 的精确匹配）→ <see cref="SevenZipAesCoder"/> / <see cref="SevenZipCoderReader"/>；</description></item>
/// <item><description>三档结论与依据文案 → <see cref="SevenZipEncryptionVerdict"/> / <see cref="SevenZipEncryptionEvidence"/>。</description></item>
/// </list>
/// </summary>
public static class SevenZipEncryptionReader
{
    /// <summary>
    /// **读入量硬上限**（64 KiB）：整个判读过程从文件里读进内存的字节数不许超过它
    /// （起始头 32 字节 + next header 最多这么多）。真实包的头远小于它，一旦超过就说明
    /// "这不是我们认识的形状"，按"不知道"返回 —— 宁可漏报也不猜。
    /// </summary>
    public const int MaxHeaderBytes = 64 * 1024;

    /// <summary>起始头长度（签名 6 + 版本 2 + CRC 4 + 两个 LE64 + CRC 4）。</summary>
    public const int StartHeaderLength = 32;

    /// <summary>最多认多少个文件夹（防畸形头把预算耗在无穷无尽的 coder 上）。</summary>
    public const int MaxFolders = 4096;

    /// <summary>一个文件夹里最多认多少个 coder。</summary>
    public const int MaxCodersPerFolder = 256;

    /// <summary>整份头里最多认多少个 coder（跨文件夹累计）。</summary>
    public const int MaxCodersTotal = 16384;

    /// <summary>PackInfo 里最多认多少条 pack stream。</summary>
    public const int MaxPackStreams = 1 << 20;

    /// <summary>
    /// 判读一个 7z（或内嵌在别的文件里、从 <paramref name="offset"/> 开始的一段 7z）有没有加密。
    ///
    /// <para><paramref name="volumePaths"/> 只在 <paramref name="offset"/> 为 0 且给了**两卷以上**时生效
    /// （= 一组 7z 分卷；内嵌段里不可能再套一组分卷，所以那时一律忽略它）。
    /// 不给卷组信息时按单文件判读 —— 多卷包会落到"不知道"（元数据在最后一卷，单看第 1 卷本来就看不出来）。</para>
    ///
    /// <para>任何 IO 意外（不存在 / 被占用 / 权限 / 半路被删 / 尺寸对不上）都落成
    /// <see cref="ArchiveEncryptionState.Unknown"/>，**绝不抛** —— 识别阶段不许因为它失败。</para>
    /// </summary>
    public static ArchiveEncryptionReading Read(
        string? filePath,
        long offset = 0,
        IReadOnlyList<string>? volumePaths = null)
    {
        return SevenZipEncryptionScan.Scan(filePath, offset, volumePaths);
    }
}

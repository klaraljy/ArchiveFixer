namespace ArchiveFixer.Extraction;

/// <summary>
/// 内嵌 ZIP **直读**的格式常量与签名（APPNOTE 6.2 的线上布局）。
///
/// <para><b>为什么单独一个类</b>：扫描（尾部找 EOCD）、解析（中央目录 / 本地头 / ZIP64 / AES 扩展字段）、
/// 解压（本地头长度、数据起点）三处必须用同一批长度与签名。各处抄一份数字，只要有一处差一个字节，
/// 就是"结构看着自洽、字节取错位置"的典型事故 —— 所以只留这一份。</para>
///
/// <para>⛔ 这里只有常量，不含判断逻辑：判据分别在
/// <see cref="EmbeddedZipCentralDirectoryScanner"/>（找 EOCD + 结构自洽）与
/// <see cref="EmbeddedZipCentralDirectoryParser"/>（把中央目录翻成条目清单）里。</para>
/// </summary>
internal static class EmbeddedZipStreamFormat
{
    /// <summary>EOCD 记录长度（22 字节）。</summary>
    internal const int EndOfCentralDirectoryLength = 22;

    /// <summary>ZIP64 收尾定位器长度（20 字节）。</summary>
    internal const int Zip64LocatorLength = 20;

    /// <summary>ZIP64 收尾记录长度（56 字节）。</summary>
    internal const int Zip64EndOfCentralDirectoryLength = 56;

    /// <summary>ZIP64 收尾两段（记录 + 定位器）的合计长度。</summary>
    internal const int Zip64FooterLength = Zip64EndOfCentralDirectoryLength + Zip64LocatorLength;

    /// <summary>中央目录头长度（46 字节，不含名字 / 扩展字段 / 注释）。</summary>
    internal const int CentralDirectoryHeaderLength = 46;

    /// <summary>本地文件头长度（30 字节，不含名字 / 扩展字段）。</summary>
    internal const int LocalFileHeaderLength = 30;

    /// <summary>
    /// 直读的拷贝缓冲（4 MiB）。直读是"读一块 → 写一块"交替进行的，而 U 盘 / 机械盘每换一次方向
    /// 就是一次寻道；4 MiB 一块把换向次数降到 1/50（用户 2026-09-25 第 38 条："不换盘也要省"）。
    /// 条目写入、AES 认证、源文件打开三处共用这一个值 —— 改它等于同时改三处的吞吐与内存。
    /// </summary>
    internal const int CopyBufferSize = 4 * 1024 * 1024;

    internal static readonly byte[] EndOfCentralDirectorySignature = { 0x50, 0x4B, 0x05, 0x06 };
    internal static readonly byte[] CentralDirectorySignature = { 0x50, 0x4B, 0x01, 0x02 };
    internal static readonly byte[] LocalFileHeaderSignature = { 0x50, 0x4B, 0x03, 0x04 };
    internal static readonly byte[] Zip64EndOfCentralDirectorySignature = { 0x50, 0x4B, 0x06, 0x06 };
    internal static readonly byte[] Zip64EndOfCentralDirectoryLocatorSignature = { 0x50, 0x4B, 0x06, 0x07 };

    /// <summary>内置读取器的版本 = 本程序集版本（它随程序一起发布，这就是它的真实版本）。</summary>
    internal static string ReaderVersion =>
        typeof(EmbeddedZipStreamFormat).Assembly.GetName().Version?.ToString() ?? string.Empty;
}

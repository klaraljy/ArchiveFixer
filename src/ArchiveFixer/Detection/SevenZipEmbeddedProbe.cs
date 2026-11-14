using System;
using System.Buffers.Binary;

namespace ArchiveFixer.Detection;

/// <summary>
/// **7z 候选起点的自洽校验**：只回答"从 <paramref name="offset"/> 起是不是一份完整的单卷 7z"。
///
/// <para>四条判据，任一条不过 ⇒ false（判不出，⛔ 不猜）：</para>
///
/// <list type="number">
/// <item><description>起始头 32 字节里的签名 <c>37 7A BC AF 27 1C</c> 对得上；</description></item>
/// <item><description><c>StartHeaderCRC</c> 覆盖起始头 12..32 字节；</description></item>
/// <item><description><c>32 + NextHeaderOffset + NextHeaderSize</c> 与文件剩余长度对账 ——
/// 自述字节超出剩余长度 ⇒ 不是完整单卷（可能是分卷首片）；</description></item>
/// <item><description><c>NextHeaderCRC</c> 覆盖下一个头。</description></item>
/// </list>
///
/// <para>⚠ 这里**不写第二份 CRC**：校验复用 <see cref="VolumeNumberFromContent.CrcMatches"/>，
/// 与 RAR5 头解析是同一份实现（两份 CRC 表必然漂移，后果是"同一个头，一处认得出、一处认不出"）。</para>
/// </summary>
internal static class SevenZipEmbeddedProbe
{
    /// <summary>7z 签名。<c>37 7A BC AF 27 1C</c>。</summary>
    private static readonly byte[] Signature = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };

    /// <summary>下一个头的硬上限：超过就不读进内存（正常 7z 的 next header 远小于此）。</summary>
    private const int MaxNextHeaderBytes = 8 << 20;

    /// <summary>
    /// 在窗口里找**第一个能通过自洽校验**的 7z 起点。偏移 0 跳过（那是普通单卷，由既有那条路回答）。
    /// </summary>
    internal static bool TryLocate(string path, long fileLength, bool deep, out EmbeddedArchiveHit hit)
    {
        hit = default;

        foreach (long candidate in SignatureWindowScan.EnumerateOffsets(path, Signature, fileLength, deep))
        {
            if (candidate <= 0)
            {
                continue;
            }

            if (!TryValidateAt(path, candidate, fileLength, out long archiveLength, out string evidence))
            {
                continue;
            }

            hit = new EmbeddedArchiveHit(candidate, archiveLength, VolumeContentFormat.SevenZip, evidence);

            return true;
        }

        return false;
    }

    /// <summary>
    /// 在**已知偏移**上做两段 CRC 自洽校验（媒体锚点那条路直接用它，⛔ 不再扫一遍）。
    /// </summary>
    internal static bool TryValidateAt(
        string path,
        long offset,
        long fileLength,
        out long archiveLength,
        out string evidence)
    {
        archiveLength = 0;
        evidence = string.Empty;

        Span<byte> header = stackalloc byte[32];

        if (!FileRangeReader.TryReadExactly(path, offset, header))
        {
            return false;
        }

        if (!header[..Signature.Length].SequenceEqual(Signature))
        {
            return false;
        }

        uint storedStartCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);

        if (!VolumeNumberFromContent.CrcMatches(storedStartCrc, header[12..32]))
        {
            return false;
        }

        ulong nextHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[12..20]);
        ulong nextHeaderSize = BinaryPrimitives.ReadUInt64LittleEndian(header[20..28]);
        uint storedNextCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[28..32]);

        if (nextHeaderSize == 0
            || nextHeaderSize > MaxNextHeaderBytes
            || nextHeaderOffset > long.MaxValue - 32
            || nextHeaderSize > long.MaxValue)
        {
            return false;
        }

        long total = 32 + (long)nextHeaderOffset + (long)nextHeaderSize;

        if (total <= 0 || offset + total > fileLength)
        {
            // 自述字节超出文件剩余长度 ⇒ 它不是一份完整的单卷（可能是分卷首片）。
            return false;
        }

        var nextHeader = new byte[(int)nextHeaderSize];

        if (!FileRangeReader.TryReadExactly(path, offset + 32 + (long)nextHeaderOffset, nextHeader))
        {
            return false;
        }

        if (!VolumeNumberFromContent.CrcMatches(storedNextCrc, nextHeader))
        {
            return false;
        }

        archiveLength = total;
        evidence = $"7z 起始头自洽：起点 {offset}，NextHeaderCRC 通过，整包 {total} 字节";

        return true;
    }
}

using System;
using System.Buffers.Binary;

namespace ArchiveFixer.Detection;

/// <summary>
/// **RAR 1.5–4.x 候选起点的自洽校验**：只回答"从 <paramref name="offset"/> 起是不是一份完整单卷"。
///
/// <para>判据：签名 <c>52 61 72 21 1A 07 00</c> + 随后 13 字节主头 ——
/// 主头类型是 <c>0x73</c>（main）、16 位头 CRC 对得上、且 <c>MHD_VOLUME = 0</c>。</para>
///
/// <para>⚠ <b>分卷片一律不认</b>（含首片）：这里回答的是"最后是不是一份完整的单卷"，
/// 分卷交第二大步。<c>MHD_VOLUME</c> 只有"我是分卷组的一员"这一条含义，⛔ 不拿它猜卷号。</para>
///
/// <para>⚠ RAR5 不在本类：它的头是 vint 编码，校验在
/// <see cref="VolumeNumberFromContent.TryValidateRar5At"/>（与卷号解析同一份实现）。</para>
/// </summary>
internal static class Rar4EmbeddedProbe
{
    /// <summary>RAR 1.5–4.x 签名。<c>52 61 72 21 1A 07 00</c>。</summary>
    private static readonly byte[] Signature = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 };

    /// <summary>RAR 1.5–4.x 主头 flags 里的"这是分卷组的一员"（<c>MHD_VOLUME 0x0001</c>）。</summary>
    private const ushort VolumeFlag = 0x0001;

    /// <summary>
    /// 在窗口里找**第一个能通过自洽校验**的 RAR4 起点。偏移 0 跳过（那是普通单卷）。
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

            if (!TryValidateSingleVolume(path, candidate, fileLength, out string evidence))
            {
                continue;
            }

            hit = new EmbeddedArchiveHit(candidate, 0, VolumeContentFormat.Rar, evidence);

            return true;
        }

        return false;
    }

    /// <summary>
    /// RAR4 主头 13 字节：CRC16 通过、<c>MHD_VOLUME = 0</c> ⇒ 完整单卷。
    /// 媒体锚点那条路也直接用它（⛔ 不再扫一遍）。
    /// </summary>
    internal static bool TryValidateSingleVolume(
        string path,
        long offset,
        long fileLength,
        out string evidence)
    {
        evidence = string.Empty;

        if (offset + 7 + 13 > fileLength)
        {
            return false;
        }

        Span<byte> main = stackalloc byte[13];

        if (!FileRangeReader.TryReadExactly(path, offset + 7, main))
        {
            return false;
        }

        if (main[2] != 0x73)
        {
            return false;
        }

        ushort storedCrc = BinaryPrimitives.ReadUInt16LittleEndian(main[..2]);

        if (!VolumeNumberFromContent.CrcMatches16(storedCrc, main[2..]))
        {
            return false;
        }

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(main[3..5]);

        if ((flags & VolumeFlag) != 0)
        {
            // 分卷成员（含首片）：这里回答的是"最后是不是一份完整单卷"，⛔ 不认。
            return false;
        }

        evidence = $"RAR4 主头自洽：起点 {offset}，MHD_VOLUME = 0（完整单卷）";

        return true;
    }
}

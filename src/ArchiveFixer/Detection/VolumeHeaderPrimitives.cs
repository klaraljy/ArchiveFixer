using System;

namespace ArchiveFixer.Detection;

/// <summary>
/// **归档头部字节原语**：RAR5 风格的 vint 读法，以及 CRC32 / RAR 1.5–4.x 的 16 位头 CRC 校验。
///
/// <para>⚠ <c>internal</c> 而不是 <c>private</c>：RAR 头的 CRC 与 vint 读法在本仓库里**只留一份**
/// （<c>RarEncryptionReader</c> 解析 RAR4/RAR5 头、<c>Rar4EmbeddedProbe</c> 认嵌入的 RAR4、
/// <c>SevenZipEmbeddedProbe</c> 校验 7z 头 CRC，走的都是同一份实现）——
/// 两份 CRC 表/两套 vint 约定必然漂移，而漂移的后果是"同一个头，一处认得出、一处认不出"。</para>
///
/// <para><b>取补与不取补两种约定都收</b>：文档只写"CRC32"，而参考实现里这个值取不取补在历史上并不统一，
/// 本机又造不出 RAR 样本可验。收宽一点才不会把真样本挡在门外 —— 真正的闸门是
/// "签名 + 字段自洽 + 整组卷号连成 1..N"，CRC 只用来挡随机字节。</para>
/// </summary>
internal static class VolumeHeaderPrimitives
{
    /// <summary>RAR5 风格的 vint：低 7 位是数据、最高位是"还有下一字节"。</summary>
    public static bool TryReadVint(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0;
        int shift = 0;

        while (offset < data.Length && shift <= 63)
        {
            byte current = data[offset++];
            value |= (ulong)(current & 0x7F) << shift;

            if ((current & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }

    /// <summary>头 CRC 对得上（取补与不取补两种约定都收，理由见类注释）。</summary>
    public static bool CrcMatches(uint stored, ReadOnlySpan<byte> data)
    {
        uint raw = Crc32Raw(data);

        return stored == raw || stored == (raw ^ 0xFFFFFFFF);
    }

    /// <summary>RAR 1.5–4.x 的 16 位头 CRC（= 同一个 CRC32 的低 16 位；取补与不取补两种约定都收）。</summary>
    public static bool CrcMatches16(ushort stored, ReadOnlySpan<byte> data)
    {
        uint raw = Crc32Raw(data);

        return stored == (ushort)(raw & 0xFFFF) || stored == (ushort)((raw ^ 0xFFFFFFFF) & 0xFFFF);
    }

    private static uint Crc32Raw(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;

        foreach (byte current in data)
        {
            crc = (crc >> 8) ^ CrcTable[(crc ^ current) & 0xFF];
        }

        return crc;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];

        for (uint index = 0; index < 256; index++)
        {
            uint value = index;

            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}

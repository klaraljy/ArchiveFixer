using System;

namespace ArchiveFixer.Detection;

/// <summary>
/// 在一个**已经读进内存的** next header 上游走的游标（⛔ 绝不越界读，走不动就是"读不出来"）。
///
/// <para><b>为什么独立成类</b>：7z 的数字编码与 RAR5 的 vint 完全不同 —— 首字节里**高位连续 1 的个数**
/// = 后面还有几个字节，剩下的那几位是最高位部分，**低位在前**。抄错一个就整份头都读歪，
/// 所以这套低层读法只留一处（结构解析 <see cref="SevenZipHeaderWalker"/> 只按字段语义调用它）。</para>
/// </summary>
internal sealed class SevenZipHeaderCursor
{
    private readonly byte[] _data;

    internal SevenZipHeaderCursor(byte[] data)
    {
        _data = data;
    }

    private int Position { get; set; }

    internal bool TryReadByte(out byte value)
    {
        value = 0;

        if (Position >= _data.Length)
        {
            return false;
        }

        value = _data[Position];
        Position++;

        return true;
    }

    internal bool TryReadBytes(int count, out byte[] data)
    {
        data = Array.Empty<byte>();

        if (count < 0 || Position + count > _data.Length)
        {
            return false;
        }

        data = new byte[count];
        Array.Copy(_data, Position, data, 0, count);
        Position += count;

        return true;
    }

    /// <summary>
    /// 7z 的变长数字：首字节里**高位连续的 1 的个数** = 后面还有几个字节（低位在前，
    /// 剩下的那几位是最高位部分）。⚠ 它与 RAR5 那种"每字节 7 位一组"的 vint **不是一回事**，
    /// 抄错一个就整份头都读歪。
    /// </summary>
    internal bool TryReadNumber(out ulong value)
    {
        value = 0;

        if (!TryReadByte(out byte first))
        {
            return false;
        }

        byte mask = 0x80;

        for (int index = 0; index < 8; index++)
        {
            if ((first & mask) == 0)
            {
                value |= (ulong)(first & (byte)(mask - 1)) << (index * 8);

                return true;
            }

            if (!TryReadByte(out byte next))
            {
                return false;
            }

            value |= (ulong)next << (index * 8);
            mask >>= 1;
        }

        // 8 个高位全是 1：7-Zip 自己不会产出这种数字 ⇒ 不是我们认识的形状。
        return false;
    }

    internal bool TrySkip(long count)
    {
        if (count < 0 || count > _data.Length - Position)
        {
            return false;
        }

        Position += (int)count;

        return true;
    }
}

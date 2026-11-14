using System;
using System.IO;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 在源文件 / 内存缓冲上**按偏移取字节**的最底层原语：读满一段、比签名、从后往前找签名、小端解 64 位。
///
/// <para><b>为什么单独一个类</b>：扫描（EOCD / ZIP64 收尾）、解析（中央目录 / 本地头）、
/// 解压（AES 数据布局）三处都要"在任意偏移上读几个字节"，而失败口径必须完全一样 ——
/// 读不满就返回实际读到的字节，由各处判据决定"这是不是截断"。</para>
///
/// <para>⛔ 这里不认格式、不判偏移对不对、不抛业务异常：只负责把字节取回来。</para>
/// </summary>
internal static class EmbeddedZipByteReader
{
    /// <summary>从指定偏移读一段字节；读不满就返回实际读到的部分（由调用方判失败）。</summary>
    internal static byte[] ReadAt(FileStream stream, long offset, int count)
    {
        if (offset < 0 || count <= 0)
        {
            return Array.Empty<byte>();
        }

        stream.Seek(offset, SeekOrigin.Begin);

        byte[] buffer = new byte[count];
        int total = 0;

        while (total < count)
        {
            int read = stream.Read(buffer, total, count - total);

            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        if (total == count)
        {
            return buffer;
        }

        byte[] partial = new byte[total];
        Array.Copy(buffer, partial, total);

        return partial;
    }

    /// <summary>在缓冲的 <paramref name="index"/> 处比一段签名；越界或不匹配都是 false。</summary>
    internal static bool Matches(byte[] buffer, int index, byte[] signature)
    {
        if (buffer == null || index < 0 || index + signature.Length > buffer.Length)
        {
            return false;
        }

        for (int i = 0; i < signature.Length; i++)
        {
            if (buffer[index + i] != signature[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>从 <paramref name="startIndex"/> 往前找最后一个签名的位置；找不到返回 -1。</summary>
    internal static int LastIndexOfSignature(byte[] buffer, byte[] signature, int startIndex)
    {
        int start = Math.Min(startIndex, buffer.Length - signature.Length);

        for (int i = start; i >= 0; i--)
        {
            if (Matches(buffer, i, signature))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 从 8 字节小端读一个 64 位无符号数；超过 <c>long.MaxValue</c> 或正好是全 1 占位符时返回 -1
    /// （= "这个字段拿不到真实值"），与 <c>EmbeddedArchiveDetector</c> 同一口径。
    /// </summary>
    internal static long ReadUInt64AsLong(byte[] buffer, int index)
    {
        if (index < 0 || index + 8 > buffer.Length)
        {
            return -1;
        }

        ulong value = BitConverter.ToUInt64(buffer, index);

        if (value == ulong.MaxValue || value > long.MaxValue)
        {
            return -1;
        }

        return (long)value;
    }

    /// <summary>
    /// 清单字节数累加：任何一边为负按 0 计，和饱和到 <c>long.MaxValue</c>
    /// （伪造的巨值不许把合计溢出成负数）。
    /// </summary>
    internal static long SaturatingSum(long left, long right)
    {
        long a = left > 0 ? left : 0L;
        long b = right > 0 ? right : 0L;

        return a > long.MaxValue - b ? long.MaxValue : a + b;
    }
}

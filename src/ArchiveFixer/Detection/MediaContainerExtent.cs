using System;
using System.Buffers.Binary;
using System.IO;

namespace ArchiveFixer.Detection;

/// <summary>
/// **媒体容器结构量到哪为止**（偏移解析：媒体段的终点就是尾部归档的起点）。
///
/// <para>只读事实、不猜：按容器自己的结构走（PNG 块链走到 IEND / JPEG 的 EOI / RIFF 的 File Size /
/// MP4 的顶层 box），走不通（脏媒体 / 截断 / 结构不符）⇒ false，
/// 调用方退回"媒体后缀 + 盲扫"那条兜底。</para>
///
/// <para>⚠ 容器身份**先按文件头魔数、后缀只是兜底**：`336.png` 实际是 RIFF/WEBP，
/// 按 `.png` 交给 PNG 解析器会直接失败，整份文件就被当成"认不出"的内容物。</para>
///
/// <para>⚠ 后缀白名单（<see cref="MatchesFileName"/>）只决定"要不要按媒体结构去量"，
/// ⛔ 不是内容结论 —— 内容身份统一走 <see cref="FileContentProbe"/>。</para>
/// </summary>
internal static class MediaContainerExtent
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>媒体容器的后缀白名单（用户 2026-10-22：「把加媒体前缀的情况就锁定在 MP4 和 png、jpg 等」）。</summary>
    internal static bool MatchesFileName(string path)
    {
        string extension = Path.GetExtension(path);

        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 读出"媒体段到哪里结束"（媒体按自己的容器结构遍历，⛔ 不猜）。
    /// 判不出（脏媒体 / 截断 / 结构不符 / 打不开）⇒ false。
    /// </summary>
    internal static bool TryFindEnd(string path, out long end)
    {
        end = 0;

        using FileStream? stream = FileRangeReader.TryOpen(path);

        if (stream is null)
        {
            return false;
        }

        try
        {
            long length = stream.Length;
            string extension = Path.GetExtension(path);

            /*
             * 先按文件头认容器，再按后缀兜底。这样 `.png` 实际是 WEBP 也能量出媒体终点；
             * 反过来，后缀是媒体但文件头已经被截断/伪装时，仍退回原来的后缀解析器。
             */
            if (HasPngSignature(stream))
            {
                return TryFindPngEnd(stream, length, out end);
            }

            if (HasWebpSignature(stream))
            {
                return TryFindWebpEnd(stream, length, out end);
            }

            if (HasJpegSignature(stream))
            {
                return TryFindJpegEnd(stream, length, out end);
            }

            if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                return TryFindPngEnd(stream, length, out end);
            }

            if (extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            {
                return TryFindWebpEnd(stream, length, out end);
            }

            if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                return TryFindJpegEnd(stream, length, out end);
            }

            if (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase))
            {
                return TryFindMp4End(stream, length, out end);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasPngSignature(FileStream stream)
    {
        Span<byte> signature = stackalloc byte[8];

        return FileRangeReader.TryReadExactly(stream, 0, signature)
            && signature.SequenceEqual(PngSignature);
    }

    private static bool HasJpegSignature(FileStream stream)
    {
        Span<byte> soi = stackalloc byte[2];

        return FileRangeReader.TryReadExactly(stream, 0, soi)
            && soi[0] == 0xFF
            && soi[1] == 0xD8;
    }

    /// <summary>RIFF 头 + 第 8–11 字节的 `WEBP`（见 RFC 9649：文件大小字段从偏移 8 起算）。</summary>
    private static bool HasWebpSignature(FileStream stream)
    {
        Span<byte> header = stackalloc byte[12];

        return FileRangeReader.TryReadExactly(stream, 0, header)
            && header[0] == (byte)'R'
            && header[1] == (byte)'I'
            && header[2] == (byte)'F'
            && header[3] == (byte)'F'
            && header[8] == (byte)'W'
            && header[9] == (byte)'E'
            && header[10] == (byte)'B'
            && header[11] == (byte)'P';
    }

    /// <summary>
    /// WebP：`RIFF` + 小端 32 位 File Size + `WEBP`；媒体结束 = 8 + File Size。
    /// 末尾允许有非规范数据（RFC 9649 允许读入方忽略），但这里只报告到 RIFF 声明的终点。
    /// </summary>
    private static bool TryFindWebpEnd(FileStream stream, long length, out long end)
    {
        end = 0;

        Span<byte> header = stackalloc byte[12];

        if (!FileRangeReader.TryReadExactly(stream, 0, header)
            || header[0] != (byte)'R'
            || header[1] != (byte)'I'
            || header[2] != (byte)'F'
            || header[3] != (byte)'F'
            || header[8] != (byte)'W'
            || header[9] != (byte)'E'
            || header[10] != (byte)'B'
            || header[11] != (byte)'P')
        {
            return false;
        }

        uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);

        if (fileSize < 4)
        {
            return false;
        }

        long total = 8L + fileSize;

        if (total < 12 || total > length)
        {
            return false;
        }

        end = total;

        return true;
    }

    /// <summary>PNG：8 字节签名 + 块链（长度 4 + 类型 4 + 数据 + CRC 4），走到 IEND 块末尾。</summary>
    private static bool TryFindPngEnd(FileStream stream, long length, out long end)
    {
        end = 0;

        Span<byte> signature = stackalloc byte[8];

        if (!FileRangeReader.TryReadExactly(stream, 0, signature)
            || !signature.SequenceEqual(PngSignature))
        {
            return false;
        }

        long position = 8;
        Span<byte> head = stackalloc byte[8];

        while (position + 12 <= length)
        {
            if (!FileRangeReader.TryReadExactly(stream, position, head))
            {
                return false;
            }

            uint chunkLength = BinaryPrimitives.ReadUInt32BigEndian(head[..4]);

            if (chunkLength > int.MaxValue || position + 12 + chunkLength > length)
            {
                return false;
            }

            bool isEnd = head[4] == (byte)'I' && head[5] == (byte)'E'
                && head[6] == (byte)'N' && head[7] == (byte)'D';

            position += 12 + chunkLength;

            if (isEnd)
            {
                end = position;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// JPEG：字节流里第一个 <c>FF D9</c>（EOI）之后就是媒体结束。
    /// 熵编码里的 0xFF 一定被转义成 <c>FF 00</c>，所以裸的 <c>FF D9</c> 只会是标记。
    /// </summary>
    private static bool TryFindJpegEnd(FileStream stream, long length, out long end)
    {
        end = 0;

        Span<byte> soi = stackalloc byte[2];

        if (!FileRangeReader.TryReadExactly(stream, 0, soi) || soi[0] != 0xFF || soi[1] != 0xD8)
        {
            return false;
        }

        byte[] buffer = new byte[FileRangeReader.ChunkBytes];
        long position = 2;

        while (position < length)
        {
            int want = (int)Math.Min(FileRangeReader.ChunkBytes, length - position);

            if (!FileRangeReader.TryReadExactly(stream, position, buffer.AsSpan(0, want)))
            {
                return false;
            }

            for (int index = 0; index + 1 < want; index++)
            {
                if (buffer[index] == 0xFF && buffer[index + 1] == 0xD9)
                {
                    end = position + index + 2;

                    return true;
                }
            }

            position += want;
        }

        return false;
    }

    /// <summary>
    /// MP4/MOV：从 0 起按 <c>size(4) + type(4)</c> 走顶层 box。
    /// 第一个不合法 box（size &lt; 8 或 type 不是可打印 ASCII）的位置 = 媒体结束。
    /// size = 0 表示"一直到文件尾"，size = 1 表示后面 8 字节是 64 位长度。
    /// </summary>
    private static bool TryFindMp4End(FileStream stream, long length, out long end)
    {
        end = 0;

        long position = 0;
        Span<byte> box = stackalloc byte[16];

        while (position + 8 <= length)
        {
            if (!FileRangeReader.TryReadExactly(stream, position, box[..8]))
            {
                return false;
            }

            uint size = BinaryPrimitives.ReadUInt32BigEndian(box[..4]);

            if (!IsPrintableBoxType(box[4..8]))
            {
                end = position;

                return end > 0;
            }

            long boxEnd;

            if (size == 0)
            {
                boxEnd = length;
            }
            else if (size == 1)
            {
                if (!FileRangeReader.TryReadExactly(stream, position + 8, box[8..16]))
                {
                    return false;
                }

                ulong largeSize = BinaryPrimitives.ReadUInt64BigEndian(box[8..16]);

                if (largeSize < 16 || largeSize > long.MaxValue)
                {
                    return false;
                }

                boxEnd = position + (long)largeSize;
            }
            else
            {
                if (size < 8)
                {
                    end = position;

                    return end > 0;
                }

                boxEnd = position + size;
            }

            if (boxEnd > length)
            {
                return false;
            }

            position = boxEnd;
        }

        end = position;

        return end > 0;
    }

    private static bool IsPrintableBoxType(ReadOnlySpan<byte> type)
    {
        for (int index = 0; index < type.Length; index++)
        {
            if (type[index] < 0x20 || type[index] > 0x7E)
            {
                return false;
            }
        }

        return true;
    }
}

using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Detection;

/// <summary>
/// **卷组拼接坐标里的区间读取**：先把"要读哪几个文件、各自多长"整理成段，再按拼接坐标
/// <c>[position, position + count)</c> 只打开真正覆盖这一段的那几卷（跨两卷就是两次打开、各读一段）。
///
/// <para><b>为什么独立成类</b>：单文件判读 = **只有一段**（同一条读取路径的特例），
/// 所以"跨卷读两段"这条边界不会成为一套只在分卷上才跑得到的代码。把这套坐标算术与
/// "头里写了什么"分开，7z 头解析就只在"已经读进内存的字节"上工作。</para>
///
/// <para><b>⛔ 只读</b>：不建目录、不改名字、不调引擎；任何一卷读不满 / 长度与刚才量到的不一致
/// 都返回 <c>null</c>（源文件在两次 stat 之间变过 ⇒ 如实"不知道"，不猜）。</para>
/// </summary>
internal sealed class SevenZipVolumeRangeReader
{
    private readonly List<Segment> _segments;

    private SevenZipVolumeRangeReader(List<Segment> segments)
    {
        _segments = segments;

        long total = 0;

        foreach (Segment segment in segments)
        {
            total += segment.Length;
        }

        TotalLength = total;
    }

    /// <summary>整个卷组拼接起来有多少字节（= 各卷长度之和）。</summary>
    internal long TotalLength { get; }

    /// <summary>
    /// 把"要读哪几个文件、各自多长"整理成段列表。
    ///
    /// <para><paramref name="volumePaths"/> 只在 <paramref name="offset"/> 为 0 且给了**两卷以上**时生效
    /// （= 一组 7z 分卷；内嵌段里不可能再套一组分卷，所以那时一律忽略它）。
    /// 有卷读不到大小 ⇒ <c>null</c>（= 卷组信息不完整）。</para>
    /// </summary>
    internal static SevenZipVolumeRangeReader? TryOpen(
        string filePath,
        long offset,
        IReadOnlyList<string>? volumePaths)
    {
        var paths = new List<string>();

        if (offset == 0 && volumePaths != null && volumePaths.Count > 1)
        {
            foreach (string? path in volumePaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return null;
                }

                paths.Add(path!);
            }
        }
        else
        {
            paths.Add(filePath);
        }

        var segments = new List<Segment>(paths.Count);

        foreach (string path in paths)
        {
            long length;

            try
            {
                length = new FileInfo(path).Length;
            }
            catch
            {
                return null;
            }

            if (length < 0)
            {
                return null;
            }

            segments.Add(new Segment(path, length));
        }

        return new SevenZipVolumeRangeReader(segments);
    }

    /// <summary>
    /// 在**卷组拼接坐标**里读 <c>[position, position + count)</c>：只打开真正覆盖这一段的那几卷
    /// （跨两卷就是两次打开、各读一段）。任何一卷读不满 / 长度与刚才量到的不一致都返回 null
    /// （源文件在两次 stat 之间变过 ⇒ 如实"不知道"，不猜）。
    ///
    /// <para><paramref name="bytesRead"/> 是这次判读累计读进内存的字节数：只有**整段读满**才累加，
    /// 读不满的段一个字节都不算 —— 与"读入量上限"的证据口径一致。</para>
    /// </summary>
    internal byte[]? ReadRange(long position, int count, ref int bytesRead)
    {
        if (count <= 0)
        {
            return Array.Empty<byte>();
        }

        var buffer = new byte[count];
        int filled = 0;
        long cursor = 0;

        foreach (Segment segment in _segments)
        {
            long segmentStart = cursor;
            long segmentEnd = cursor + segment.Length;

            cursor = segmentEnd;

            if (segmentEnd <= position || filled >= count)
            {
                continue;
            }

            long from = Math.Max(position, segmentStart);
            int skip = (int)(from - segmentStart);
            int want = (int)Math.Min(count - filled, segment.Length - skip);

            if (want <= 0)
            {
                continue;
            }

            using var stream = new FileStream(
                segment.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length != segment.Length)
            {
                return null;
            }

            stream.Seek(skip, SeekOrigin.Begin);

            int total = 0;

            while (total < want)
            {
                int read = stream.Read(buffer, filled + total, want - total);

                if (read <= 0)
                {
                    return null;
                }

                total += read;
            }

            filled += total;
            bytesRead += total;
        }

        return filled == count ? buffer : null;
    }

    /// <summary>卷组里的一段：<paramref name="path"/> 有 <paramref name="length"/> 个字节。</summary>
    private readonly struct Segment
    {
        internal Segment(string path, long length)
        {
            Path = path;
            Length = length;
        }

        internal string Path { get; }

        internal long Length { get; }
    }
}

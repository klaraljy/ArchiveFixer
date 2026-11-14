using System;
using System.IO;

namespace ArchiveFixer.Detection;

/// <summary>
/// **格式探测与字节入口**：一份文件的**头**属于哪一族（RAR / ZIP / 认不出），以及这一族该交给
/// 哪个解码器；同时提供"只读一小段字节、读不到就少给几个字节"的唯一实现。
///
/// <para><b>为什么单独成层</b>：卷号解码分两族（<see cref="RarVolumeNumberDecoder"/> /
/// <see cref="ZipVolumeNumberDecoder"/>），而"这一族是哪个"必须**只在一处分派**（用户 2026-10-11 口径：
/// 「不仅仅是 rar，我们能做的识别文件逻辑要有程序性…换一个 7z 或者用户新接的引擎怎么办」）。
/// 族判别由 <see cref="VolumeContentInference.SniffFormat"/> 给（它只看魔数），本层负责：</para>
/// <list type="bullet">
/// <item><description>按族分派到 RAR / ZIP 解码器；</description></item>
/// <item><description>头认不出时补一次**尾部 EOCD** 判断（跨盘 zip 的中间片/末片头都是从数据中间切开的）；</description></item>
/// <item><description>把"读不到"落成"没认出来 + 哪一档原因"，**绝不抛**。</description></item>
/// </list>
///
/// <para>生命周期：纯静态、无状态（只有诊断计数）；不建目录、不改名字、不调引擎。</para>
/// </summary>
internal static class VolumeContentFormatProbe
{
    private static long _readCalls;

    /// <summary>诊断计数：<c>Read</c> 实际进入次数；测试可重置（经主类转发）。</summary>
    public static long ReadCalls => System.Threading.Interlocked.Read(ref _readCalls);

    /// <summary>把诊断计数清零（只给测试用；经主类转发）。</summary>
    public static void ResetReadCallsForTests() => System.Threading.Interlocked.Exchange(ref _readCalls, 0);

    /// <summary>
    /// 读**一卷**的内容，得出"它说自己是不是分卷组的一员、是第几卷"。
    ///
    /// <para>任何 IO 意外都落成"没认出来 + 哪一档原因"，**绝不抛**。</para>
    /// </summary>
    public static VolumeNumberReading Read(string? filePath)
    {
        System.Threading.Interlocked.Increment(ref _readCalls);

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return VolumeContentEvidence.NotAVolume(string.Empty, VolumeContentFormat.Unknown, VolumeNumberFail.NotAnArchive);
        }

        VolumeContentFormat format = VolumeContentInference.SniffFormat(filePath);

        try
        {
            if (format == VolumeContentFormat.Rar)
            {
                return RarVolumeNumberDecoder.Read(filePath);
            }

            if (format == VolumeContentFormat.Zip)
            {
                return ZipVolumeNumberDecoder.Read(filePath);
            }

            /*
             * 头不是 PK 也可能是跨盘 zip 的**末片**：分片是从数据中间切开的，末片的开头是上一片剩下来的数据，
             * 根本没有本地文件头（7z 的 -v 切出来就是这样）。这种文件认不认由 EOCD 说了算 ——
             * 所以这里补一次内容判断，只有它自述"是跨盘组的一员"时才改口径。
             */
            VolumeNumberReading fallback = ZipVolumeNumberDecoder.Read(filePath);

            /*
             * 通用分片 `X.zip.001..00N`（设计文档 §6.6 A / 工单 #10）：**末片的头也是切开的**
             * （不是 PK 签名）⇒ `SniffFormat` 给 `Unknown`，但它的**尾部有 EOCD** ——
             * 这条内容事实必须留下来（`HasZipEndRecord`），否则整族只剩名字一条证据。
             */
            return fallback.IsVolumeMember || fallback.HasZipEndRecord
                ? fallback
                : VolumeContentEvidence.NotAVolume(filePath, format, VolumeNumberFail.NotAnArchive);
        }
        catch (Exception)
        {
            // 读不到（被占 / 权限 / 半路被删）就是"看不出来"：调用方会因此一个字节都不动。
            return VolumeContentEvidence.NotAVolume(filePath, format, VolumeNumberFail.HeadersUnreadable);
        }
    }

    /// <summary>读一小段字节（读不到就少给几个字节，**绝不抛**）。</summary>
    public static byte[] ReadBytes(string filePath, long offset, int count)
    {
        try
        {
            using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (offset < 0 || count <= 0)
            {
                return Array.Empty<byte>();
            }

            long available = Math.Min(count, Math.Max(0, stream.Length - offset));

            if (available <= 0)
            {
                return Array.Empty<byte>();
            }

            stream.Position = offset;

            var buffer = new byte[available];
            int total = 0;

            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);

                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total == buffer.Length ? buffer : buffer[..total];
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }
}

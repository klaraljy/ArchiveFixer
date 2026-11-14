using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Detection;

/// <summary>
/// **在一个有界窗口里顺序扫签名**，产出候选起点（分块读 + 块内逐字节比对）。
///
/// <para>两种档位共用这一个入口（⛔ 只留一份扫描实现）：</para>
///
/// <list type="bullet">
/// <item><description><b>快扫</b>（<c>deep</c> = false）：只看文件尾 <see cref="TailWindowBytes"/> 字节，从后往前 ——
/// 覆盖"归档贴着文件尾"的绝大多数；</description></item>
/// <item><description><b>深扫</b>（<c>deep</c> = true）：从文件头顺序扫到 <see cref="DeepWindowBytes"/>
/// （前缀最多 512 MiB），与 ZIP 那条"顺序扫找 EOCD"同一口径 —— 覆盖大前缀、归档本身很大的情形。</description></item>
/// </list>
///
/// <para>⛔ 两个窗口都是**上限**，不是文件大小上限：这类扫描是 O(窗口) 的，而它会在识别阶段
/// 被每个"认不出格式"的文件触发一次（200 MB 的媒体扫一遍就白烧 200 MB 读）。
/// 这里只产出**候选**，是不是真归档由各格式探测类做自洽校验 —— 判断一个字节都不放宽。</para>
/// </summary>
internal static class SignatureWindowScan
{
    /// <summary>快扫窗口：文件尾 4 MiB（媒体前缀那一档：归档通常贴着文件尾）。</summary>
    internal const long TailWindowBytes = 4L << 20;

    /// <summary>深扫窗口：从文件头最多扫 512 MiB（窗口上限，不是文件大小上限）。</summary>
    internal const long DeepWindowBytes = 512L << 20;

    /// <summary>
    /// 在窗口里列出 <paramref name="signature"/> 的每一个候选起点。
    /// 参数不成立 / 读不到 ⇒ 不产出（⛔ 不抛 —— 识别兜底分支不许把扫描炸掉）。
    /// </summary>
    internal static IEnumerable<long> EnumerateOffsets(
        string path,
        byte[] signature,
        long fileLength,
        bool deep)
    {
        if (signature.Length == 0 || fileLength < signature.Length)
        {
            yield break;
        }

        long scanStart;
        long scanEnd;

        if (deep)
        {
            scanStart = 0;
            scanEnd = Math.Min(fileLength, DeepWindowBytes);
        }
        else
        {
            scanStart = Math.Max(0, fileLength - TailWindowBytes);
            scanEnd = fileLength;
        }

        byte[] buffer = new byte[FileRangeReader.ChunkBytes + signature.Length];

        long position = scanStart;

        while (position < scanEnd)
        {
            int want = (int)Math.Min(FileRangeReader.ChunkBytes, scanEnd - position);
            int read;

            using (FileStream? stream = FileRangeReader.TryOpen(path))
            {
                if (stream is null)
                {
                    yield break;
                }

                try
                {
                    stream.Seek(position, SeekOrigin.Begin);
                    read = stream.Read(buffer, 0, want);
                }
                catch
                {
                    yield break;
                }
            }

            if (read <= 0)
            {
                yield break;
            }

            int limit = read - signature.Length;

            // 快扫从后往前（归档贴在尾部时最先命中）；深扫从前往后（前缀在头上，第一个命中就是归档起点）。
            for (int step = 0; step <= limit; step++)
            {
                int index = deep ? step : limit - step;
                bool matched = true;

                for (int s = 0; s < signature.Length; s++)
                {
                    if (buffer[index + s] != signature[s])
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    yield return position + index;
                }
            }

            position += read;
        }
    }
}

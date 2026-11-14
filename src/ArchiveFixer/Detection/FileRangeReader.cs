using System;
using System.IO;

namespace ArchiveFixer.Detection;

/// <summary>
/// **按偏移读一段字节**的底层原语（打开 / 定位 / 读满）。
///
/// <para>为什么单独一个类：签名扫描与媒体容器遍历都要"在文件的任意偏移上读几个字节"，
/// 而这两处的失败口径必须完全一样 —— 读不到就是 <c>false</c>（判不出 ⇒ 上层什么都不做，⛔ 不抛）。
/// 打开时的共享口径也只留一处：<see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/> ——
/// 识别发生在用户目录里，同一份文件可能正被杀毒 / 索引 / 另一个任务开着。</para>
///
/// <para>⛔ 这里只做"读"，不认格式、不判偏移对不对 —— 那是各格式探测类的事。</para>
/// </summary>
internal static class FileRangeReader
{
    /// <summary>一次读多少字节（1 MiB，与既有扫描口径一致）。</summary>
    internal const int ChunkBytes = 1 << 20;

    /// <summary>
    /// 打开只读流并允许并发读写 / 删除；打不开（不在 / 被占 / 权限）返回 null。
    /// 调用方用 <c>using</c> 接管释放。
    /// </summary>
    internal static FileStream? TryOpen(string path)
    {
        try
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 在已打开的流上从 <paramref name="offset"/> 起读满 <paramref name="destination"/>；
    /// 读不满（截断 / 读失败）⇒ false。
    /// </summary>
    internal static bool TryReadExactly(FileStream stream, long offset, Span<byte> destination)
    {
        try
        {
            stream.Seek(offset, SeekOrigin.Begin);

            int filled = 0;

            while (filled < destination.Length)
            {
                int read = stream.Read(destination[filled..]);

                if (read <= 0)
                {
                    return false;
                }

                filled += read;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按路径打开、读满一段、随即关闭；打不开 / 读不满 ⇒ false。</summary>
    internal static bool TryReadExactly(string path, long offset, Span<byte> destination)
    {
        using FileStream? stream = TryOpen(path);

        return stream is not null && TryReadExactly(stream, offset, destination);
    }
}

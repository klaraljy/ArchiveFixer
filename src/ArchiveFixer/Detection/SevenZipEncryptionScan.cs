using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace ArchiveFixer.Detection;

/// <summary>
/// **一次 7z 加密判读的编排**：把「卷组 → 起始头布局闸门 → next header → 结构走读 → 三档结论」
/// 串起来，全项目只此一处（<see cref="SevenZipEncryptionReader.Read"/> 是它的薄转发）。
///
/// <para><b>为什么独立成类</b>：起判的每一步都会产生"读进内存多少字节"这一证据位，而它进
/// <see cref="ArchiveEncryptionReading.BytesRead"/>；读取、布局校验、结论三条路各写一遍这个计数，
/// 就会出现"某个失败档忘了带 <c>BytesRead</c>"的漂移。所以这里只做一件事：按顺序把事实取齐，
/// 再把事实交给判据（<see cref="SevenZipEncryptionVerdict"/>），自己不下结论。</para>
///
/// <para><b>⛔ 不改变判据</b>：起始头 32 字节的布局闸门（<c>32 + NextHeaderOffset + NextHeaderSize</c>
/// 必须落在这一份 / 整个卷组的拼接长度之内）原样留在这里；它拦住的正好是"多卷包的第 1 卷"，
/// 那时 <c>NextHeaderOffset</c> 指向整包尾部，落在后面的卷里 ⇒ 如实"不知道"。</para>
///
/// <para>任何 IO 意外（不存在 / 被占用 / 权限 / 半路被删 / 尺寸对不上）都落成
/// <see cref="ArchiveEncryptionState.Unknown"/>，**绝不抛** —— 识别阶段不许因为它失败。</para>
/// </summary>
internal static class SevenZipEncryptionScan
{
    /// <summary>7z 魔数：<c>37 7A BC AF 27 1C</c>。</summary>
    private static readonly byte[] Signature = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };

    /// <summary>
    /// 判读一个 7z（或内嵌在别的文件里、从 <paramref name="offset"/> 开始的一段 7z）有没有加密。
    /// 参数语义与 <see cref="SevenZipEncryptionReader.Read"/> 完全一致。
    /// </summary>
    internal static ArchiveEncryptionReading Scan(
        string? filePath,
        long offset,
        IReadOnlyList<string>? volumePaths)
    {
        if (string.IsNullOrWhiteSpace(filePath) || offset < 0)
        {
            return SevenZipEncryptionEvidence.NoReadablePath();
        }

        try
        {
            SevenZipVolumeRangeReader? volumes = SevenZipVolumeRangeReader.TryOpen(filePath!, offset, volumePaths);

            if (volumes == null)
            {
                return SevenZipEncryptionEvidence.Unknown(0, "卷组信息不完整（有卷读不到大小）");
            }

            int bytesRead = 0;

            if (offset > volumes.TotalLength - SevenZipEncryptionReader.StartHeaderLength)
            {
                return SevenZipEncryptionEvidence.Unknown(bytesRead, "起始头读不到（文件比 32 字节还短）");
            }

            byte[]? startHeader = volumes.ReadRange(
                offset,
                SevenZipEncryptionReader.StartHeaderLength,
                ref bytesRead);

            if (startHeader == null)
            {
                return SevenZipEncryptionEvidence.Unknown(bytesRead, "起始头读不出来（被截断 / 读到一半文件变了）");
            }

            if (!StartsWithSignature(startHeader))
            {
                return SevenZipEncryptionEvidence.Unknown(bytesRead, "这一段的开头不是 7z 签名");
            }

            ulong nextHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(startHeader.AsSpan(12, 8));
            ulong nextHeaderSize = BinaryPrimitives.ReadUInt64LittleEndian(startHeader.AsSpan(20, 8));

            if (nextHeaderOffset > long.MaxValue - SevenZipEncryptionReader.StartHeaderLength
                || nextHeaderSize > long.MaxValue)
            {
                return SevenZipEncryptionEvidence.Unknown(
                    bytesRead,
                    "NextHeaderOffset / NextHeaderSize 超出可表示范围（布局不符）");
            }

            if (nextHeaderSize == 0)
            {
                return SevenZipEncryptionEvidence.Unknown(bytesRead, "NextHeaderSize 为 0（布局不符）");
            }

            long nextHeaderStart = offset + SevenZipEncryptionReader.StartHeaderLength + (long)nextHeaderOffset;

            // 溢出要显式挡掉：先比下界（溢出会变成负数），再用"总长 − 起点"避免加法再溢出一次。
            if (nextHeaderStart < offset
                || nextHeaderStart > volumes.TotalLength
                || (long)nextHeaderSize > volumes.TotalLength - nextHeaderStart)
            {
                /*
                 * 这是**布局闸门**：32 + NextHeaderOffset + NextHeaderSize 必须落在这一份
                 * 文件（或整个卷组的拼接）之内。它拦住的正好是"多卷包的第 1 卷"
                 * —— 那时 NextHeaderOffset 指向的是整包的尾部，落在后面的卷里。
                 */
                return SevenZipEncryptionEvidence.Unknown(
                    bytesRead,
                    "32 + NextHeaderOffset + NextHeaderSize 超出文件长度（截断 / 不是一个完整的 7z）");
            }

            int want = (int)Math.Min((long)nextHeaderSize, SevenZipEncryptionReader.MaxHeaderBytes);
            byte[]? header = volumes.ReadRange(nextHeaderStart, want, ref bytesRead);

            if (header == null)
            {
                return SevenZipEncryptionEvidence.Unknown(
                    bytesRead,
                    "next header 读不出来（被截断 / 读到一半文件变了）");
            }

            SevenZipHeaderWalk walk = SevenZipHeaderWalker.Walk(header);

            return SevenZipEncryptionVerdict.Conclude(walk, bytesRead);
        }
        catch (Exception)
        {
            // 读不到（被占 / 权限 / 半路被删）就是"看不出来"：调用方照旧按"不报加密"处理。
            return SevenZipEncryptionEvidence.NoReadablePath();
        }
    }

    private static bool StartsWithSignature(byte[] data)
    {
        if (data.Length < Signature.Length)
        {
            return false;
        }

        for (int index = 0; index < Signature.Length; index++)
        {
            if (data[index] != Signature[index])
            {
                return false;
            }
        }

        return true;
    }
}

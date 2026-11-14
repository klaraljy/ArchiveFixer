using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;

namespace ArchiveFixer.Services;

/// <summary>
/// **格式探测**：只凭文件头的字节回答"这是哪种容器"（识别服务里的格式探测那一层）。
///
/// <para>这里做的全部事情：</para>
/// <list type="bullet">
/// <item><description>魔数表（7Z / RAR4 / RAR5 / ZIP 三态 / GZIP / BZIP2 / XZ / CAB / ARJ / LZH / Z / ZSTD / LZ4 / RPM）；</description></item>
/// <item><description>两条**结构**判据：TAR 的 <c>ustar</c> 标记在偏移 257、ISO 9660 的 <c>CD001</c> 在偏移 0x8001
/// （所以文件头要读 34 KB —— 见 <see cref="ArchiveContentIdentifier"/> 的读入量）；</description></item>
/// <item><description>媒体前缀识别 —— ⛔ **只用来限制顺序扫的窗口**，不用于判定"不是归档"；
/// 媒体前缀 + 尾部归档仍由尾部那条路先回答；</description></item>
/// <item><description>ZIP 家族判定（<c>ZIP</c> / <c>ZIP_EMPTY</c> / <c>ZIP_SPANNED</c>）——
/// 加密判读与后缀状态共用这一把尺子，⛔ 别处不许再写一套。</description></item>
/// </list>
///
/// <para>⛔ 这里**不读文件**（文件头由 <see cref="ArchiveContentIdentifier"/> 读好传进来），
/// 也不碰缓存、不判加密 —— 加密判据的唯一出口是 <see cref="ArchiveEncryptionVerdict"/>。</para>
/// </summary>
internal static class ArchiveFormatProbe
{
    /// <summary>
    /// 头部像常见媒体容器（MP4/JPEG/PNG/GIF/RIFF/EBML/FLV/MPEG/ID3）。
    /// ⛔ 只用于**限制深度顺序扫的窗口**，不用于判定"不是归档"；
    /// 媒体前缀 + 尾部归档仍由尾部路先回答。
    /// </summary>
    internal static bool LooksLikeMediaHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 12
            && header[4] == (byte)'f'
            && header[5] == (byte)'t'
            && header[6] == (byte)'y'
            && header[7] == (byte)'p')
        {
            return true;
        }

        if (header.Length >= 3
            && header[0] == 0xFF
            && header[1] == 0xD8
            && header[2] == 0xFF)
        {
            return true;
        }

        if (header.Length >= 8
            && header[0] == 0x89
            && header[1] == 0x50
            && header[2] == 0x4E
            && header[3] == 0x47)
        {
            return true;
        }

        if (header.Length >= 6
            && (header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F')
            || (header.Length >= 4
                && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'))
        {
            return true;
        }

        if (header.Length >= 4
            && header[0] == 0x1A
            && header[1] == 0x45
            && header[2] == 0xDF
            && header[3] == 0xA3)
        {
            return true;
        }

        if (header.Length >= 4
            && header[0] == (byte)'F'
            && header[1] == (byte)'L'
            && header[2] == (byte)'V')
        {
            return true;
        }

        if (header.Length >= 4
            && header[0] == 0x00
            && header[1] == 0x00
            && header[2] == 0x01
            && header[3] == 0xBA)
        {
            return true;
        }

        return header.Length >= 3
            && header[0] == (byte)'I'
            && header[1] == (byte)'D'
            && header[2] == (byte)'3';
    }

    /// <summary>ZIP 家族（含空 ZIP 与分卷 ZIP）—— 它们的容器都是 ZIP 结构。</summary>
    internal static bool IsZipFamily(string? format)
    {
        return format is "ZIP" or "ZIP_EMPTY" or "ZIP_SPANNED";
    }

    /// <summary>
    /// 按文件头识别格式（原文的 <c>DetectByHeader</c>：判据、次序、文案一条没动）。
    /// </summary>
    internal static DetectResult ProbeHeader(byte[] header)
    {
        if (header == null || header.Length == 0)
        {
            return DetectResultFactory.CreateUnknown("文件头为空");
        }

        string headerHex = SafePathHelper.ToHexString(header, 64);

        // 7Z:
        // 37 7A BC AF 27 1C
        if (StartsWith(header, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C))
        {
            return DetectResultFactory.CreateHeaderMatch("7Z", ".7z", "识别为 7Z 压缩包", headerHex, 100);
        }

        // RAR4:
        // 52 61 72 21 1A 07 00
        if (StartsWith(header, 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00))
        {
            return DetectResultFactory.CreateHeaderMatch("RAR4", ".rar", "识别为 RAR4 压缩包", headerHex, 100);
        }

        // RAR5:
        // 52 61 72 21 1A 07 01 00
        if (StartsWith(header, 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00))
        {
            return DetectResultFactory.CreateHeaderMatch("RAR5", ".rar", "识别为 RAR5 压缩包", headerHex, 100);
        }

        // ZIP:
        // PK 03 04 普通 ZIP
        // PK 05 06 空 ZIP
        // PK 07 08 分卷或数据描述符相关
        if (StartsWith(header, 0x50, 0x4B, 0x03, 0x04))
        {
            return DetectResultFactory.CreateHeaderMatch("ZIP", ".zip", "识别为 ZIP 压缩包", headerHex, 100);
        }

        if (StartsWith(header, 0x50, 0x4B, 0x05, 0x06))
        {
            return DetectResultFactory.CreateHeaderMatch("ZIP_EMPTY", ".zip", "识别为空 ZIP 压缩包", headerHex, 100);
        }

        if (StartsWith(header, 0x50, 0x4B, 0x07, 0x08))
        {
            return DetectResultFactory.CreateHeaderMatch("ZIP_SPANNED", ".zip", "识别为 ZIP 分卷或特殊 ZIP", headerHex, 90);
        }

        // GZIP:
        // 1F 8B
        if (StartsWith(header, 0x1F, 0x8B))
        {
            return DetectResultFactory.CreateHeaderMatch("GZIP", ".gz", "识别为 GZIP 压缩包", headerHex, 100);
        }

        // BZIP2:
        // 42 5A 68 = BZh
        if (StartsWith(header, 0x42, 0x5A, 0x68))
        {
            return DetectResultFactory.CreateHeaderMatch("BZIP2", ".bz2", "识别为 BZIP2 压缩包", headerHex, 100);
        }

        // XZ:
        // FD 37 7A 58 5A 00
        if (StartsWith(header, 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00))
        {
            return DetectResultFactory.CreateHeaderMatch("XZ", ".xz", "识别为 XZ 压缩包", headerHex, 100);
        }

        // TAR:
        // ustar 出现在偏移 257。
        if (IsTar(header))
        {
            return DetectResultFactory.CreateHeaderMatch("TAR", ".tar", "识别为 TAR 归档文件", headerHex, 90);
        }

        // CAB:
        // 4D 53 43 46 = MSCF
        if (StartsWith(header, 0x4D, 0x53, 0x43, 0x46))
        {
            return DetectResultFactory.CreateHeaderMatch("CAB", ".cab", "识别为 CAB 压缩包", headerHex, 100);
        }

        // ARJ:
        // 60 EA
        if (StartsWith(header, 0x60, 0xEA))
        {
            return DetectResultFactory.CreateHeaderMatch("ARJ", ".arj", "识别为 ARJ 压缩包", headerHex, 100);
        }

        // LZH:
        // 2D 6C 68 = -lh
        if (StartsWith(header, 0x2D, 0x6C, 0x68))
        {
            return DetectResultFactory.CreateHeaderMatch("LZH", ".lzh", "识别为 LZH 压缩包", headerHex, 100);
        }

        // Z（Unix compress）:
        // 1F 9D
        if (StartsWith(header, 0x1F, 0x9D))
        {
            return DetectResultFactory.CreateHeaderMatch("Z", ".z", "识别为 Z 压缩包", headerHex, 100);
        }

        // Zstandard:
        // 28 B5 2F FD
        if (StartsWith(header, 0x28, 0xB5, 0x2F, 0xFD))
        {
            return DetectResultFactory.CreateHeaderMatch("ZSTD", ".zst", "识别为 Zstandard 压缩包", headerHex, 100);
        }

        // LZ4:
        // 04 22 4D 18
        if (StartsWith(header, 0x04, 0x22, 0x4D, 0x18))
        {
            return DetectResultFactory.CreateHeaderMatch("LZ4", ".lz4", "识别为 LZ4 压缩包", headerHex, 100);
        }

        // RPM:
        // ED AB EE DB
        if (StartsWith(header, 0xED, 0xAB, 0xEE, 0xDB))
        {
            return DetectResultFactory.CreateHeaderMatch("RPM", ".rpm", "识别为 RPM 软件包", headerHex, 100);
        }

        // ISO 9660:
        // 卷描述符 "CD001" 位于偏移 0x8001（扇区 16 起始处）。
        if (IsIso9660(header))
        {
            return DetectResultFactory.CreateHeaderMatch("ISO", ".iso", "识别为 ISO 光盘镜像", headerHex, 90);
        }

        return DetectResultFactory.CreateUnknown("未识别为支持的压缩格式", headerHex);
    }

    /// <summary>
    /// 判断文件头是否以指定字节开头。
    /// </summary>
    private static bool StartsWith(byte[] data, params byte[] signature)
    {
        if (data == null || signature == null)
        {
            return false;
        }

        if (data.Length < signature.Length)
        {
            return false;
        }

        for (int i = 0; i < signature.Length; i++)
        {
            if (data[i] != signature[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 判断 TAR。
    /// TAR 的 ustar 标记通常位于偏移 257。
    /// </summary>
    private static bool IsTar(byte[] header)
    {
        if (header == null || header.Length < 262)
        {
            return false;
        }

        return header[257] == (byte)'u' &&
               header[258] == (byte)'s' &&
               header[259] == (byte)'t' &&
               header[260] == (byte)'a' &&
               header[261] == (byte)'r';
    }

    /// <summary>
    /// 判断是否为 ISO 9660 镜像。
    /// 主卷描述符的 "CD001" 标记位于文件偏移 0x8001。
    /// </summary>
    private static bool IsIso9660(byte[] header)
    {
        if (header == null || header.Length < 0x8001 + 5)
        {
            return false;
        }

        return header[0x8001] == (byte)'C' &&
               header[0x8002] == (byte)'D' &&
               header[0x8003] == (byte)'0' &&
               header[0x8004] == (byte)'0' &&
               header[0x8005] == (byte)'1';
    }
}

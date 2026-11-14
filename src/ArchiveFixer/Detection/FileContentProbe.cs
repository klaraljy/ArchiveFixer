using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// **内容物识别的唯一出口**（只读，不碰盘）：
    /// 魔数优先；ZIP 家族只有深探测才看条目；文本才用 BOM/可打印比例。
    /// ⛔ 后缀只作弱提示，绝不单独决定“这是内容物”。
    /// </summary>
    public static class FileContentProbe
    {
        private sealed record CacheEntry(long Length, long Ticks, bool Deep, FileContentProbeResult Result);

        private const int MaxCacheEntries = 4096;

        private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentQueue<string> CacheOrder = new();

        public static FileContentProbeResult Probe(string? path, bool deep = false)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return FileContentProbeResult.Unknown;
            }

            FileInfo info;

            try
            {
                info = new FileInfo(path);
            }
            catch
            {
                return FileContentProbeResult.Unknown;
            }

            /*
             * 浅探测已经能定案（KnownContent / Archive）时，深探测不会改结论：
             * 只有 ZIP 这种浅层 Unknown 才必须再进条目看一次。
             * 这样 4,000 个普通内容文件不会因为外层调了 deep 再读一遍头。
             */
            if (Cache.TryGetValue(path, out CacheEntry? cached)
                && cached.Length == info.Length
                && cached.Ticks == info.LastWriteTimeUtc.Ticks
                && (cached.Deep || !deep || cached.Result.Class != FileContentClass.Unknown))
            {
                return cached.Result;
            }

            FileContentProbeResult result = ProbeCore(path, deep, info.Length);

            var entry = new CacheEntry(info.Length, info.LastWriteTimeUtc.Ticks, deep, result);
            bool firstTime = !Cache.ContainsKey(path);
            Cache[path] = entry;

            if (firstTime)
            {
                CacheOrder.Enqueue(path);
            }

            TrimCache();

            return result;
        }

        /// <summary>
        /// 有界淘汰：超过上限只丢最旧的一批，⛔ 不再 `Clear()` 整表 ——
        /// 4,000 个产物一轮扫下来，整表清空会让前面刚探测过的文件在下一轮重新读。
        /// </summary>
        private static void TrimCache()
        {
            while ((Cache.Count > MaxCacheEntries || CacheOrder.Count > MaxCacheEntries * 2)
                && CacheOrder.TryDequeue(out string? old))
            {
                Cache.TryRemove(old, out _);
            }
        }

        private static FileContentProbeResult ProbeCore(string path, bool deep, long length)
        {
            byte[] header = ReadHeader(path);

            if (header.Length == 0)
            {
                return FileContentProbeResult.Unknown;
            }

            if (StartsWith(header, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A))
            {
                return new FileContentProbeResult(FileContentClass.KnownContent, "PNG", "PNG 签名");
            }

            if (StartsWith(header, 0xFF, 0xD8, 0xFF))
            {
                return new FileContentProbeResult(FileContentClass.KnownContent, "JPEG", "JPEG SOI");
            }

            if (HasRiffType(header, "WEBP"))
            {
                return new FileContentProbeResult(
                    FileContentClass.KnownContent,
                    "WebP",
                    "RIFF/WEBP 签名",
                    MediaEnd: TryWebpEnd(header, length));
            }

            if (HasAscii(header, "GIF87a") || HasAscii(header, "GIF89a"))
            {
                return new FileContentProbeResult(FileContentClass.KnownContent, "GIF", "GIF 签名");
            }

            if (StartsWith(header, 0x42, 0x4D))
            {
                return new FileContentProbeResult(FileContentClass.KnownContent, "BMP", "BMP 签名");
            }

            if (StartsWith(header, 0x49, 0x49, 0x2A, 0x00)
                || StartsWith(header, 0x4D, 0x4D, 0x00, 0x2A))
            {
                return new FileContentProbeResult(FileContentClass.KnownContent, "TIFF", "TIFF 头");
            }

            if (HasFtypBox(header))
            {
                return new FileContentProbeResult(
                    FileContentClass.KnownContent,
                    "MP4/MOV",
                    "ISO BMFF ftyp box");
            }

            if (HasRiffType(header, "AVI ") || HasRiffType(header, "WAVE"))
            {
                return new FileContentProbeResult(
                    FileContentClass.KnownContent,
                    HasRiffType(header, "AVI ") ? "AVI" : "WAV",
                    "RIFF 容器");
            }

            if (StartsWith(header, 0x1A, 0x45, 0xDF, 0xA3))
            {
                return new FileContentProbeResult(FileContentClass.KnownContent, "MKV/WebM", "EBML 签名");
            }

            if (HasAscii(header, "FLV"))
            {
                return new FileContentProbeResult(FileContentClass.KnownContent, "FLV", "FLV 签名");
            }

            if (HasAscii(header, "fLaC"))
            {
                return new FileContentProbeResult(FileContentClass.KnownContent, "FLAC", "FLAC 签名");
            }

            if (HasAscii(header, "OggS"))
            {
                return new FileContentProbeResult(FileContentClass.KnownContent, "OGG", "OggS 签名");
            }

            if (HasAscii(header, "ID3"))
            {
                return new FileContentProbeResult(FileContentClass.KnownContent, "MP3", "ID3 标签");
            }

            string audioExtension = Path.GetExtension(path);

            if ((audioExtension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
                    || audioExtension.Equals(".aac", StringComparison.OrdinalIgnoreCase)
                    || audioExtension.Equals(".m4a", StringComparison.OrdinalIgnoreCase))
                && header.Length >= 2
                && header[0] == 0xFF
                && (header[1] & 0xE0) == 0xE0)
            {
                return new FileContentProbeResult(FileContentClass.KnownContent, "MP3/AAC", "MPEG 帧同步");
            }

            if (HasAscii(header, "#!AMR"))
            {
                return new FileContentProbeResult(FileContentClass.KnownContent, "AMR", "AMR 签名");
            }

            if (HasAscii(header, "MThd"))
            {
                return new FileContentProbeResult(FileContentClass.KnownContent, "MIDI", "MThd 签名");
            }

            if (HasAscii(header, "MAC "))
            {
                return new FileContentProbeResult(FileContentClass.KnownContent, "APE", "MAC 签名");
            }

            if (StartsWith(header, 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11))
            {
                return new FileContentProbeResult(FileContentClass.KnownContent, "ASF/WMA/WMV", "ASF GUID");
            }

            if (HasAscii(header, "%PDF-"))
            {
                return new FileContentProbeResult(FileContentClass.KnownContent, "PDF", "PDF 签名");
            }

            if (HasAscii(header, "{\\rtf"))
            {
                return new FileContentProbeResult(FileContentClass.KnownContent, "RTF", "RTF 头");
            }

            if (StartsWith(header, 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1))
            {
                return new FileContentProbeResult(
                    FileContentClass.KnownContent,
                    "OLE (DOC/XLS/PPT)",
                    "OLE 复合文档头");
            }

            if (header.Length >= 68 && HasAsciiAt(header, 60, "BOOKMOBI"))
            {
                return new FileContentProbeResult(FileContentClass.KnownContent, "MOBI", "BOOKMOBI 头");
            }

            if (StartsWith(header, 0x50, 0x4B, 0x03, 0x04)
                || StartsWith(header, 0x50, 0x4B, 0x05, 0x06)
                || StartsWith(header, 0x50, 0x4B, 0x07, 0x08))
            {
                /*
                 * ZIP 家族：浅探测**不能**回答“内容物还是压缩包”。
                 * 只有进入准入点、调用 deep=true 时才看 ZIP 条目；否则返回 Unknown，
                 * 避免 FileNature 的 93 个调用点都去打开 ZIP。
                 */
                if (!deep)
                {
                    return FileContentProbeResult.Unknown;
                }

                return ProbeZip(path);
            }

            if (StartsWith(header, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)
                || StartsWith(header, 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07)
                || StartsWith(header, 0x1F, 0x8B)
                || StartsWith(header, 0x42, 0x5A, 0x68)
                || StartsWith(header, 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00))
            {
                return new FileContentProbeResult(FileContentClass.Archive, "Archive", "归档魔数");
            }

            if (LooksLikeText(path, header))
            {
                return new FileContentProbeResult(FileContentClass.KnownContent, "Text", "BOM/可打印文本");
            }

            return FileContentProbeResult.Unknown;
        }

        private static FileContentProbeResult ProbeZip(string path)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

                bool hasContentTypes = false;
                bool hasWord = false;
                bool hasExcel = false;
                bool hasPowerPoint = false;
                bool hasMimetype = false;
                bool hasAndroidManifest = false;
                bool hasClassesDex = false;
                bool hasJarManifest = false;

                int seen = 0;

                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (++seen > 4096)
                    {
                        break;
                    }

                    string name = entry.FullName.Replace('\\', '/');

                    hasContentTypes |= string.Equals(name, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase);
                    hasWord |= name.StartsWith("word/", StringComparison.OrdinalIgnoreCase);
                    hasExcel |= name.StartsWith("xl/", StringComparison.OrdinalIgnoreCase);
                    hasPowerPoint |= name.StartsWith("ppt/", StringComparison.OrdinalIgnoreCase);
                    hasMimetype |= string.Equals(name, "mimetype", StringComparison.OrdinalIgnoreCase);
                    hasAndroidManifest |= string.Equals(name, "AndroidManifest.xml", StringComparison.OrdinalIgnoreCase);
                    hasClassesDex |= string.Equals(name, "classes.dex", StringComparison.OrdinalIgnoreCase);
                    hasJarManifest |= string.Equals(name, "META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase);
                }

                if (hasAndroidManifest && hasClassesDex)
                {
                    return new FileContentProbeResult(
                        FileContentClass.KnownContent,
                        "APK",
                        "ZIP 条目：AndroidManifest.xml + classes.dex",
                        IsContentContainer: true);
                }

                if (hasContentTypes && (hasWord || hasExcel || hasPowerPoint))
                {
                    return new FileContentProbeResult(
                        FileContentClass.KnownContent,
                        "OOXML",
                        "ZIP 条目：[Content_Types].xml + Office 目录",
                        IsContentContainer: true);
                }

                if (hasMimetype)
                {
                    return new FileContentProbeResult(
                        FileContentClass.KnownContent,
                        "ODF/EPUB",
                        "ZIP 条目：mimetype",
                        IsContentContainer: true);
                }

                if (hasJarManifest)
                {
                    return new FileContentProbeResult(
                        FileContentClass.KnownContent,
                        "JAR",
                        "ZIP 条目：META-INF/MANIFEST.MF",
                        IsContentContainer: true);
                }

                if (ArchiveFixer.Security.ContentContainerProtection.ShouldProtect(path))
                {
                    return new FileContentProbeResult(
                        FileContentClass.KnownContent,
                        "ContentContainer",
                        "ZIP 容器 + 内容容器后缀（ZIP 条目不可判）",
                        IsContentContainer: true);
                }

                return new FileContentProbeResult(FileContentClass.Archive, "ZIP", "普通 ZIP 容器");
            }
            catch
            {
                /*
                 * ZIP 读不开：后缀是内容容器时按“不碰”处理（用户明确要保护这些文件）；
                 * 其余无法证明是内容物，落 Unknown，交给归档/分卷证据。
                 */
                if (ArchiveFixer.Security.ContentContainerProtection.ShouldProtect(path))
                {
                    return new FileContentProbeResult(
                        FileContentClass.KnownContent,
                        "ContentContainer",
                        "ZIP 不可读 + 内容容器后缀",
                        IsContentContainer: true);
                }

                return FileContentProbeResult.Unknown;
            }
        }

        private static byte[] ReadHeader(string path)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[68];
                int read = stream.Read(buffer, 0, buffer.Length);

                return read <= 0 ? Array.Empty<byte>() : buffer.AsSpan(0, read).ToArray();
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        private static bool LooksLikeText(string path, ReadOnlySpan<byte> header)
        {
            string extension = Path.GetExtension(path);

            bool textExtension = extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".srt", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".ass", StringComparison.OrdinalIgnoreCase);

            if (!textExtension)
            {
                return false;
            }

            if (header.Length >= 3
                && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
            {
                return true;
            }

            if (header.Length >= 2
                && (header[0] == 0xFF && header[1] == 0xFE
                    || header[0] == 0xFE && header[1] == 0xFF))
            {
                return true;
            }

            int printable = 0;

            foreach (byte value in header)
            {
                if (value == 0)
                {
                    return false;
                }

                if (value == (byte)'\r' || value == (byte)'\n' || value == (byte)'\t'
                    || (value >= 0x20 && value <= 0x7E)
                    || value >= 0x80)
                {
                    printable++;
                }
            }

            return printable * 10 >= header.Length * 9;
        }

        private static bool StartsWith(ReadOnlySpan<byte> data, params byte[] prefix)
        {
            return data.Length >= prefix.Length
                && data[..prefix.Length].SequenceEqual(prefix);
        }

        private static bool HasAscii(ReadOnlySpan<byte> data, string value)
        {
            return data.Length >= value.Length
                && Encoding.ASCII.GetString(data[..value.Length]) == value;
        }

        private static bool HasAsciiAt(ReadOnlySpan<byte> data, int offset, string value)
        {
            return data.Length >= offset + value.Length
                && Encoding.ASCII.GetString(data.Slice(offset, value.Length)) == value;
        }

        private static bool HasFtypBox(ReadOnlySpan<byte> data)
        {
            return data.Length >= 8
                && data[4] == (byte)'f'
                && data[5] == (byte)'t'
                && data[6] == (byte)'y'
                && data[7] == (byte)'p';
        }

        private static bool HasRiffType(ReadOnlySpan<byte> data, string type)
        {
            return data.Length >= 12
                && HasAscii(data[..4], "RIFF")
                && HasAscii(data[8..12], type);
        }

        private static long TryWebpEnd(ReadOnlySpan<byte> header, long fileLength)
        {
            if (header.Length < 12)
            {
                return 0;
            }

            uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
            long end = 8L + size;

            return end >= 12 && end <= fileLength ? end : 0;
        }
    }
}

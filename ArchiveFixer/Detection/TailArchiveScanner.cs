using System;
using System.IO;
using System.Threading;

namespace ArchiveFixer.Detection
{
    /// <summary>尾部扫出来的归档签名。</summary>
    public sealed class TailArchiveSignature
    {
        /// <summary>归档在文件里的起始偏移（&gt; 0）。</summary>
        public long Offset { get; init; }

        /// <summary>格式名（与 <c>DetectResult.Format</c> 同一套写法：<c>RAR4</c> / <c>RAR5</c> / <c>7Z</c>）。</summary>
        public string Format { get; init; } = string.Empty;

        /// <summary>建议后缀（<c>.rar</c> / <c>.7z</c>）。</summary>
        public string SuggestedExtension { get; init; } = string.Empty;

        /// <summary>给人看的一句话。</summary>
        public string Describe() =>
            $"尾部内嵌归档（{Format}，偏移 {Offset:N0}）—— 需要先抠出 [偏移, 文件末尾) 再交给引擎解";
    }

    /// <summary>
    /// 在文件里**从前往后扫**归档签名（用户 2026-09-25 第 38 条追加：尾部挂 RAR/7z 的识别）。
    ///
    /// <para><b>为什么 ZIP 那条路不够</b>：ZIP 能从**尾部**的 EOCD 反推出起点（读几十 KB 就够），
    /// 所以 <c>EmbeddedArchiveDetector</c> 只做 ZIP。RAR / 7z 的目录结构不支持这种反推 ——
    /// 只能**从头扫签名**（<c>Rar!\x1a\x07</c> / <c>37 7A BC AF 27 1C</c>）。
    /// 实测：`封面.jpg + 资料.rar` 这种"图片尾部挂包"，前缀 1 MB 时 UnRAR 还能当 SFX 打开，
    /// **前缀 20 MB 时 7-Zip 与 UnRAR 都报"不是归档"** —— 所以必须自己定位起点、抠出来再解。</para>
    ///
    /// <para><b>代价</b>：一遍顺序读（在最后一个签名命中处停下）。真实伪装里前缀是"一张封面 / 一小段视频"，
    /// 几 MB 到几百 MB；这里给一个硬上限（<see cref="MaxScanBytes"/> = 512 MiB），
    /// 超过就不再往后扫（那种形状已不是"图片/视频伪装"的常见做法，扫描成本也不划算）。</para>
    /// </summary>
    public static class TailArchiveScanner
    {
        /// <summary>最多扫多少字节（512 MiB）。</summary>
        public const long MaxScanBytes = 512L * 1024 * 1024;

        /// <summary>每次读多大（4 MiB，与直读/抠取同一口径）。</summary>
        private const int ScanChunkBytes = 4 * 1024 * 1024;

        private static readonly byte[] Rar4Signature = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 };

        private static readonly byte[] Rar5Signature = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 };

        private static readonly byte[] SevenZipSignature = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };

        /// <summary>
        /// 从头扫，返回**第一个**命中的归档签名（偏移必须 &gt; 0：偏移 0 就是普通归档，不走这条路）。
        /// 找不到 / 读不了返回 null（绝不抛 —— 识别失败不该让整单失败）。
        /// </summary>
        public static TailArchiveSignature? Find(string? filePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return null;
            }

            try
            {
                using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    ScanChunkBytes,
                    FileOptions.SequentialScan);

                long fileLength = stream.Length;
                long limit = Math.Min(fileLength, MaxScanBytes);

                byte[] buffer = new byte[ScanChunkBytes + Rar5Signature.Length];
                long position = 0;
                int carried = 0;

                while (position < limit)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int want = (int)Math.Min(ScanChunkBytes, limit - position);
                    int read = stream.Read(buffer, carried, want);

                    if (read <= 0)
                    {
                        break;
                    }

                    int total = carried + read;

                    // 从 1 开始找：偏移 0 上的是"文件本身的头"，不属于"尾部内嵌"。
                    for (int i = 1; i <= total; i++)
                    {
                        TailArchiveSignature? hit = MatchAt(buffer, i, total, position - carried);

                        if (hit != null)
                        {
                            return hit;
                        }
                    }

                    /*
                     * 保留"签名长度 − 1"个字节到下一轮：签名可能正好跨在两块之间。
                     * 偏移基准跟着回退 carried 个字节（MatchAt 用它算绝对偏移）。
                     */
                    carried = Rar5Signature.Length - 1;
                    Array.Copy(buffer, total - carried, buffer, 0, carried);
                    position += read;
                }

                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>缓冲区里 <paramref name="index"/> 处是不是某个归档签名。</summary>
        private static TailArchiveSignature? MatchAt(byte[] buffer, int index, int length, long chunkStart)
        {
            long absolute = chunkStart + index;

            if (absolute <= 0)
            {
                return null;
            }

            if (Matches(buffer, index, length, Rar5Signature))
            {
                return new TailArchiveSignature { Offset = absolute, Format = "RAR5", SuggestedExtension = ".rar" };
            }

            if (Matches(buffer, index, length, Rar4Signature))
            {
                return new TailArchiveSignature { Offset = absolute, Format = "RAR4", SuggestedExtension = ".rar" };
            }

            if (Matches(buffer, index, length, SevenZipSignature))
            {
                return new TailArchiveSignature { Offset = absolute, Format = "7Z", SuggestedExtension = ".7z" };
            }

            return null;
        }

        private static bool Matches(byte[] buffer, int index, int length, byte[] signature)
        {
            if (index + signature.Length > length)
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
    }
}

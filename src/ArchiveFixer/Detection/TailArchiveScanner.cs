using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ArchiveFixer.Detection
{
    /// <summary>扫出来的归档签名（RAR / 7z 这类只能凭魔数定位起点的格式）。</summary>
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

    /// <summary>一遍扫描的全部收获（用户 2026-09-25 第 40 条）。</summary>
    public sealed class TailArchiveScanResult
    {
        /// <summary>第一个 RAR / 7z 魔数命中（没有就是 null）。</summary>
        public TailArchiveSignature? Archive { get; init; }

        /// <summary>
        /// 扫到的 <c>PK\x05\x06</c>（EOCD）候选偏移，**从后往前**逐个校验用的（最新的排在最后）。
        /// 命中列表不等于结论 —— 每个候选都要过 <c>EmbeddedArchiveDetector</c> 的自洽校验。
        /// </summary>
        public IReadOnlyList<long> ZipEocdOffsets { get; init; } = Array.Empty<long>();

        /// <summary>
        /// 魔数命中之前有没有见过 <c>PK\x03\x04</c>（ZIP 局部文件头）。
        /// 只用来决定"要不要为了 ZIP 再多读一段" —— 见 <see cref="TailArchiveScanner.Scan"/> 里的取舍说明。
        /// </summary>
        public bool SawZipLocalHeader { get; init; }
    }

    /// <summary>
    /// 在文件里**从前往后扫**归档签名。
    ///
    /// <para><b>为什么 ZIP 那条路不够</b>（用户 2026-09-25 第 38 条追加）：ZIP 能从**尾部**的 EOCD
    /// 反推出起点（读几十 KB 就够），所以 <c>EmbeddedArchiveDetector</c> 只做 ZIP。RAR / 7z 的目录结构
    /// 不支持这种反推 —— 只能**从头扫签名**（<c>Rar!\x1a\x07</c> / <c>37 7A BC AF 27 1C</c>）。
    /// 实测：`封面.jpg + 资料.rar` 这种"图片尾部挂包"，前缀 1 MB 时 UnRAR 还能当 SFX 打开，
    /// **前缀 20 MB 时 7-Zip 与 UnRAR 都报"不是归档"** —— 所以必须自己定位起点、抠出来再解。</para>
    ///
    /// <para><b>第 40 条又补了一种形状</b>：ZIP **不在末尾**（前面垫了图片、后面还垫了别的数据）。
    /// 那时尾部那 256 KB 里根本没有 EOCD，"从尾部反推"这条路整条失效 —— 只能靠这一遍顺序扫
    /// 顺手把 <c>PK\x05\x06</c> 候选位置记下来，再逐个做自洽校验（校验仍然在
    /// <c>EmbeddedArchiveDetector</c> 里，判据一个字没放宽）。</para>
    ///
    /// <para><b>代价</b>：一遍顺序读。真实伪装里前缀是"一张封面 / 一小段视频"，几 MB 到几百 MB；
    /// 这里给一个硬上限（<see cref="MaxScanBytes"/> = 512 MiB），超过就不再往后扫。
    /// ⛔ **命中 RAR / 7z 魔数就立刻收工**（第 39 条那批"图片后缀挂 RAR"靠这一条保住速度）——
    /// 唯一的例外是"魔数之前已经见过 <c>PK\x03\x04</c>"：那说明这可能是一个**整体被顶偏的 ZIP**
    /// （ZIP 里还装着 RAR 文件时，那个内层 RAR 的魔数也会被扫到），这时才继续把 EOCD 候选收完，
    /// 让"自洽校验通过的 ZIP"优先于一个孤零零的魔数。多读的那一段只在这种真有 ZIP 的场合发生。</para>
    /// </summary>
    public static class TailArchiveScanner
    {
        /// <summary>最多扫多少字节（512 MiB）。</summary>
        public const long MaxScanBytes = 512L * 1024 * 1024;

        /// <summary>每次读多大（4 MiB，与直读/抠取同一口径）。</summary>
        private const int ScanChunkBytes = 4 * 1024 * 1024;

        /// <summary>最多记住多少个 EOCD 候选（从后往前校验；真实文件里通常就一两个）。</summary>
        private const int MaxEocdCandidates = 32;

        private static readonly byte[] Rar4Signature = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 };

        private static readonly byte[] Rar5Signature = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 };

        private static readonly byte[] SevenZipSignature = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };

        /// <summary>ZIP 局部文件头（<c>PK\x03\x04</c>）—— 用来判断"这一片里像不像有个 ZIP"。</summary>
        private static readonly byte[] ZipLocalHeaderSignature = { 0x50, 0x4B, 0x03, 0x04 };

        /// <summary>ZIP 中央目录结束记录（<c>PK\x05\x06</c>）—— 中部 ZIP 唯一的抓手。</summary>
        private static readonly byte[] ZipEocdSignature = { 0x50, 0x4B, 0x05, 0x06 };

        /// <summary>
        /// 从头扫，返回**第一个**命中的归档签名（偏移必须 &gt; 0：偏移 0 就是普通归档，不走这条路）。
        /// 找不到 / 读不了返回 null（绝不抛 —— 识别失败不该让整单失败）。
        /// </summary>
        public static TailArchiveSignature? Find(string? filePath, CancellationToken cancellationToken = default) =>
            Scan(filePath, cancellationToken).Archive;

        /// <summary>
        /// 一遍顺序读，同时找**两类**东西：RAR / 7z 的魔数（第一个即结论）、ZIP 的 EOCD 候选
        /// （列表，交给 <c>EmbeddedArchiveDetector</c> 逐个做自洽校验）。
        ///
        /// <para>读不了 / 出错一律返回空结果（识别兜底分支不允许把扫描炸掉）。</para>
        /// </summary>
        public static TailArchiveScanResult Scan(string? filePath, CancellationToken cancellationToken = default)
        {
            TailArchiveSignature? archive = null;
            var eocdOffsets = new List<long>();
            bool sawZipLocalHeader = false;

            TailArchiveScanResult Finish() => new()
            {
                Archive = archive,
                ZipEocdOffsets = eocdOffsets,
                SawZipLocalHeader = sawZipLocalHeader
            };

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return Finish();
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
                    long chunkStart = position - carried;

                    // 从 1 开始找：偏移 0 上的是"文件本身的头"，不属于"尾部内嵌"。
                    for (int i = 1; i <= total; i++)
                    {
                        long absolute = chunkStart + i;

                        if (absolute <= 0)
                        {
                            continue;
                        }

                        /*
                         * 按**首字节**分派，不逐个签名去比：这一遍是拿 IO 换命中的核心循环，
                         * 每字节做六次签名比较（ZIP 两条 + 魔数三条）纯属白烧 CPU。
                         */
                        byte first = buffer[i];

                        if (first == 0x50)
                        {
                            // ZIP 的两种签名都以 "PK" 开头。
                            if (Matches(buffer, i, total, ZipEocdSignature))
                            {
                                RememberEocdCandidate(eocdOffsets, absolute);
                            }
                            else if (Matches(buffer, i, total, ZipLocalHeaderSignature))
                            {
                                sawZipLocalHeader = true;
                            }

                            continue;
                        }

                        if (first != 0x52 && first != 0x37)
                        {
                            // 'R' = Rar!…，'7' = 7z 的 37 7A BC AF 27 1C。
                            continue;
                        }

                        TailArchiveSignature? hit = MatchAt(buffer, i, total, chunkStart);

                        if (hit == null || archive != null)
                        {
                            continue;
                        }

                        archive = hit;

                        /*
                         * 两个终止条件（见类文档里的取舍）：
                         * ①这一片扫描里没见过 PK\x03\x04 → 这就是"图片挂个 RAR"那种最常见形状，
                         *   当场收工，后面的字节一个都不读（第 39 条那批的速度就靠这一条）；
                         * ②见过 → 可能是"整体被顶偏的 ZIP + 里面的 RAR"，继续把 EOCD 候选收完，
                         *   让自洽校验通过的 ZIP 优先。
                         */
                        if (!sawZipLocalHeader)
                        {
                            return Finish();
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

                return Finish();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return Finish();
            }
        }

        /// <summary>缓冲区里 <paramref name="index"/> 处是不是某个归档魔数。</summary>
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

        /// <summary>
        /// 记下一个 EOCD 候选。列表满了之后**丢掉最早的那个**（保留靠后的 —— 真正的 EOCD 更可能靠后，
        /// 而且校验本来就是从后往前做的，靠前的候选价值更低）。
        /// </summary>
        private static void RememberEocdCandidate(List<long> offsets, long offset)
        {
            if (offsets.Count >= MaxEocdCandidates)
            {
                offsets.RemoveAt(0);
            }

            offsets.Add(offset);
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

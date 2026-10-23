using System;
using System.Buffers.Binary;
using System.IO;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 一次"媒体前缀 + 尾部归档"的定位结果（**只描述事实**，不含任何动作）。
    /// </summary>
    /// <param name="Offset">归档在文件里的起点（&gt; 0 才叫"前面垫了东西"）。</param>
    /// <param name="Length">归档区间字节数（判不出时为 0，⛔ 不编）。</param>
    /// <param name="Format">归档格式。</param>
    /// <param name="Evidence">判据输入的一句话（给人看，⛔ 不许拿来当判据）。</param>
    public readonly record struct EmbeddedArchiveHit(
        long Offset,
        long Length,
        VolumeContentFormat Format,
        string Evidence);

    /// <summary>
    /// **单一完整压缩包的专属算法：媒体前缀 + 尾部归档**（2026-10-22，用户口径：
    /// 「简单的后缀伪装和媒体前缀，底层能不能靠信息识别」）。
    ///
    /// <para>要回答的问题只有一个：**这份文件里、从某个偏移开始，是不是一份完整的单卷归档**。
    /// 三种格式各自有一条**自洽校验**，任一条不过就返回 null（判不出 ⇒ 什么都不做，⛔ 不猜）：</para>
    ///
    /// <list type="bullet">
    /// <item><description><b>ZIP</b>：转调既有 <see cref="EmbeddedArchiveDetector"/> ——
    /// 它用 EOCD 声明的中央目录偏移与实际位置的差值做自洽校验，并已区分"跨盘末片"。⚠ 这一族**不在这里重写第二份**。</description></item>
    /// <item><description><b>7z</b>：搜 <c>37 7A BC AF 27 1C</c> 签名，校验
    /// <c>StartHeaderCRC</c>（覆盖起始头 12..32 字节）与 <c>NextHeaderCRC</c>（覆盖下一个头），
    /// 再用 <c>32 + NextHeaderOffset + NextHeaderSize</c> 与文件剩余长度对账 ⇒ 自洽。</description></item>
    /// <item><description><b>RAR4</b>：搜 <c>52 61 72 21 1A 07 00</c> 签名，读随后的 13 字节主头，
    /// 校验 16 位 CRC 且 <c>MHD_VOLUME = 0</c> ⇒ 完整单卷（分卷片一律不认，交第二大步）。</description></item>
    /// </list>
    ///
    /// <para>⚠ RAR5 的加密/分卷头用 vint 编码、解析成本高，这一版**不做**（返回 null = 判不出）——
    /// 宁可判不出，也不拿"差不多"当结论。</para>
    /// </summary>
    public static class EmbeddedArchiveLocator
    {
        /// <summary>7z 签名。<c>37 7A BC AF 27 1C</c>。</summary>
        private static readonly byte[] SevenZipSignature = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };

        /// <summary>RAR 1.5–4.x 签名。<c>52 61 72 21 1A 07 00</c>。</summary>
        private static readonly byte[] Rar4Signature = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 };

        private const int ScanBufferSize = 1 << 20;
        /// <summary>从文件末尾往前最多扫这么多字节（媒体前缀那一档：归档通常贴着文件尾）。</summary>
        private const long MaxScanBytes = 4L << 20;
        /// <summary>深扫窗口：前缀最多 512 MiB（与 ZIP 的顺序扫同一条口径：这是窗口上限，不是文件大小上限）。</summary>
        private const long DeepScanBytes = 512L << 20;
        private const int MaxSevenZipNextHeaderBytes = 8 << 20;

        /// <summary>
        /// 试着在文件里定位一份**尾部单卷归档**。找到 ⇒ 返回命中（<c>Offset &gt; 0</c>）；
        /// 找不到 / 判不出 / 偏移为 0（那就是普通单卷，由既有那条路回答）⇒ null。
        /// </summary>
        public static EmbeddedArchiveHit? TryLocate(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            /*
             * 这份扫描是 O(文件大小) 的（要搜签名），而 FileNature.Inspect 会在一批里被问很多次
             * ⇒ 按「路径 + 字节数 + 修改时间」缓存结论（与 ArchiveDetectService 的缓存键同源）。
             */
            string cacheKey;

            try
            {
                var info = new FileInfo(path);

                cacheKey = path + "|"
                    + info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
                    + info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }

            if (Cache.TryGetValue(cacheKey, out EmbeddedArchiveHit? cached))
            {
                return cached;
            }

            EmbeddedArchiveHit? located = TryLocateUncached(path);

            if (Cache.Count > 512)
            {
                Cache.Clear();
            }

            Cache[cacheKey] = located;

            return located;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, EmbeddedArchiveHit?> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        private static EmbeddedArchiveHit? TryLocateUncached(string path)
        {
            long fileLength;

            try
            {
                fileLength = new FileInfo(path).Length;
            }
            catch
            {
                return null;
            }

            if (fileLength <= 0)
            {
                return null;
            }

            // ① ZIP：既有那份唯一实现（EOCD 偏移自洽），⛔ 不重写第二份。
            if (TryLocateZip(path, out EmbeddedArchiveHit zipHit))
            {
                return zipHit;
            }

            /*
             * ② **媒体容器锚点**（2026-10-22 用户口径：「可以把加媒体前缀的情况就锁定在 MP4 和 png、jpg 等」）：
             * 先按媒体容器自己的结构把"媒体段"读完，媒体结束处就是归档起点；再在那个偏移上做格式自洽校验。
             * 锚点解析成功且媒体没占满整个文件 ⇒ 归档起点**已知**，⛔ 不再盲扫（省掉对普通大文件的整段扫描）。
             */
            bool mediaName = IsMediaFileName(path);

            if (mediaName
                && TryFindMediaEnd(path, out long mediaEnd)
                && mediaEnd > 0)
            {
                /*
                 * 媒体段自己就占满整个文件 ⇒ 后面根本没有归档（普通 mp4/jpg/png）⇒ 当场判"没有"，
                 * ⛔ 不退回深扫（否则一个正常的 60 MB 视频每次识别都要被整段读一遍）。
                 */
                if (mediaEnd >= fileLength)
                {
                    return null;
                }

                if (TryValidateSevenZip(path, mediaEnd, fileLength, out long anchoredLength, out string anchoredEvidence))
                {
                    return new EmbeddedArchiveHit(
                        mediaEnd, anchoredLength, VolumeContentFormat.SevenZip, anchoredEvidence);
                }

                if (TryValidateRar4SingleVolume(path, mediaEnd, fileLength, out string rarAnchoredEvidence))
                {
                    return new EmbeddedArchiveHit(
                        mediaEnd, 0, VolumeContentFormat.Rar, rarAnchoredEvidence);
                }

                // 前缀确实是媒体，但它后面既不是 7z 单卷也不是 RAR4 单卷 ⇒ 判不出（⛔ 不猜、不盲扫）。
                return null;
            }

            // ③ 7z / RAR4：快扫（文件尾 4 MB）。
            if (TryLocateSevenZip(path, fileLength, deep: false, out EmbeddedArchiveHit sevenZipHit))
            {
                return sevenZipHit;
            }

            if (TryLocateRar4(path, fileLength, deep: false, out EmbeddedArchiveHit rarHit))
            {
                return rarHit;
            }

            /*
             * ④ 深扫（≤512 MiB）**只对媒体后缀**：媒体锚点解析失败（脏媒体 / 截断）时才退回盲扫；
             * 非媒体后缀一律不做深扫 —— 否则内容物里的大文件（60 MB 级）每次识别都要被整段读一遍。
             */
            if (mediaName)
            {
                if (TryLocateSevenZip(path, fileLength, deep: true, out sevenZipHit))
                {
                    return sevenZipHit;
                }

                if (TryLocateRar4(path, fileLength, deep: true, out rarHit))
                {
                    return rarHit;
                }
            }

            return null;
        }

        private static bool TryLocateZip(string path, out EmbeddedArchiveHit hit)
        {
            hit = default;

            EmbeddedArchiveInfo info;

            try
            {
                info = EmbeddedArchiveDetector.Detect(path);
            }
            catch
            {
                return false;
            }

            if (!info.Found || info.Offset <= 0 || info.IsVolumePart)
            {
                // 偏移 0 = 普通单卷（不归这里管）；跨盘片 = 分卷，交第二大步。
                return false;
            }

            hit = new EmbeddedArchiveHit(
                info.Offset,
                info.ArchiveLength,
                VolumeContentFormat.Zip,
                $"ZIP 尾部自洽：归档起点 {info.Offset}，区间 {info.ArchiveLength} 字节");

            return true;
        }

        private static bool TryLocateSevenZip(string path, long fileLength, bool deep, out EmbeddedArchiveHit hit)
        {
            hit = default;

            foreach (long candidate in EnumerateSignatureOffsets(path, SevenZipSignature, fileLength, deep))
            {
                if (candidate <= 0)
                {
                    continue;
                }

                if (!TryValidateSevenZip(path, candidate, fileLength, out long archiveLength, out string evidence))
                {
                    continue;
                }

                hit = new EmbeddedArchiveHit(candidate, archiveLength, VolumeContentFormat.SevenZip, evidence);

                return true;
            }

            return false;
        }

        /// <summary>
        /// 7z 起始头的两段 CRC 自洽校验（判据见类注释）。任一层不过 ⇒ false（判不出）。
        /// </summary>
        private static bool TryValidateSevenZip(
            string path,
            long offset,
            long fileLength,
            out long archiveLength,
            out string evidence)
        {
            archiveLength = 0;
            evidence = string.Empty;

            Span<byte> header = stackalloc byte[32];

            if (!TryReadExactly(path, offset, header))
            {
                return false;
            }

            if (!header[..SevenZipSignature.Length].SequenceEqual(SevenZipSignature))
            {
                return false;
            }

            uint storedStartCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);

            if (!VolumeNumberFromContent.CrcMatches(storedStartCrc, header[12..32]))
            {
                return false;
            }

            ulong nextHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[12..20]);
            ulong nextHeaderSize = BinaryPrimitives.ReadUInt64LittleEndian(header[20..28]);
            uint storedNextCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[28..32]);

            if (nextHeaderSize == 0
                || nextHeaderSize > MaxSevenZipNextHeaderBytes
                || nextHeaderOffset > long.MaxValue - 32
                || nextHeaderSize > long.MaxValue)
            {
                return false;
            }

            long total = 32 + (long)nextHeaderOffset + (long)nextHeaderSize;

            if (total <= 0 || offset + total > fileLength)
            {
                // 自述字节超出文件剩余长度 ⇒ 它不是一份完整的单卷（可能是分卷首片）。
                return false;
            }

            var nextHeader = new byte[(int)nextHeaderSize];

            if (!TryReadExactly(path, offset + 32 + (long)nextHeaderOffset, nextHeader))
            {
                return false;
            }

            if (!VolumeNumberFromContent.CrcMatches(storedNextCrc, nextHeader))
            {
                return false;
            }

            archiveLength = total;
            evidence = $"7z 起始头自洽：起点 {offset}，NextHeaderCRC 通过，整包 {total} 字节";

            return true;
        }

        private static bool TryLocateRar4(string path, long fileLength, bool deep, out EmbeddedArchiveHit hit)
        {
            hit = default;

            foreach (long candidate in EnumerateSignatureOffsets(path, Rar4Signature, fileLength, deep))
            {
                if (candidate <= 0)
                {
                    continue;
                }

                if (!TryValidateRar4SingleVolume(path, candidate, fileLength, out string evidence))
                {
                    continue;
                }

                hit = new EmbeddedArchiveHit(candidate, 0, VolumeContentFormat.Rar, evidence);

                return true;
            }

            return false;
        }

        /// <summary>RAR4 主头 13 字节：CRC16 通过、MHD_VOLUME = 0 ⇒ 完整单卷。</summary>
        private static bool TryValidateRar4SingleVolume(
            string path,
            long offset,
            long fileLength,
            out string evidence)
        {
            evidence = string.Empty;

            if (offset + 7 + 13 > fileLength)
            {
                return false;
            }

            Span<byte> main = stackalloc byte[13];

            if (!TryReadExactly(path, offset + 7, main))
            {
                return false;
            }

            if (main[2] != 0x73)
            {
                return false;
            }

            ushort storedCrc = BinaryPrimitives.ReadUInt16LittleEndian(main[..2]);

            if (!VolumeNumberFromContent.CrcMatches16(storedCrc, main[2..]))
            {
                return false;
            }

            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(main[3..5]);

            if ((flags & Rar4VolumeFlag) != 0)
            {
                // 分卷成员（含首片）：这里回答的是"最后是不是一份完整单卷"，⛔ 不认。
                return false;
            }

            evidence = $"RAR4 主头自洽：起点 {offset}，MHD_VOLUME = 0（完整单卷）";

            return true;
        }

        /// <summary>RAR 1.5–4.x 主头 flags 里的"这是分卷组的一员"（<c>MHD_VOLUME 0x0001</c>）。</summary>
        private const ushort Rar4VolumeFlag = 0x0001;

        /// <summary>
        /// 在文件里顺序扫一个签名，产出**候选起点**（分块读 + 块间重叠，避免跨块漏检）。
        /// 判不出/读不到 ⇒ 不产出（⛔ 不抛）。
        /// </summary>
        /// <summary>
        /// **从文件末尾往前**扫签名（媒体前缀那一档：归档通常贴着文件尾，前缀在头上）。
        ///
        /// <para>⛔ 有上限 <see cref="MaxScanBytes"/>：这类扫描是 O(文件大小) 的，而它会在识别阶段
        /// 被每个"认不出格式"的文件触发一次（200 MB 的媒体文件扫一遍就白烧 200 MB 读）。
        /// 超过上限 ⇒ 判不出（保持 Unknown），⛔ 不猜、也不把整块盘读一遍。</para>
        /// </summary>
        /// <summary>媒体容器的后缀白名单（用户 2026-10-22：「把加媒体前缀的情况就锁定在 MP4 和 png、jpg 等」）。</summary>
        private static bool IsMediaFileName(string path)
        {
            string extension = Path.GetExtension(path);

            return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
        }

        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>
        /// 读出"媒体段到哪里结束"（媒体按自己的容器结构遍历，⛔ 不猜）。
        /// 判不出（脏媒体 / 截断 / 结构不符）⇒ false，调用方退回"媒体后缀 + 深扫"那条兜底。
        /// </summary>
        private static bool TryFindMediaEnd(string path, out long end)
        {
            end = 0;

            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                long length = stream.Length;
                string extension = Path.GetExtension(path);

                if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                {
                    return TryFindPngEnd(stream, length, out end);
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

        /// <summary>PNG：8 字节签名 + 块链（长度 4 + 类型 4 + 数据 + CRC 4），走到 IEND 块末尾。</summary>
        private static bool TryFindPngEnd(FileStream stream, long length, out long end)
        {
            end = 0;

            Span<byte> signature = stackalloc byte[8];

            if (!TryReadFromStream(stream, 0, signature)
                || !signature.SequenceEqual(PngSignature))
            {
                return false;
            }

            long position = 8;
            Span<byte> head = stackalloc byte[8];

            while (position + 12 <= length)
            {
                if (!TryReadFromStream(stream, position, head))
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

            if (!TryReadFromStream(stream, 0, soi) || soi[0] != 0xFF || soi[1] != 0xD8)
            {
                return false;
            }

            byte[] buffer = new byte[ScanBufferSize];
            long position = 2;

            while (position < length)
            {
                int want = (int)Math.Min(ScanBufferSize, length - position);

                if (!TryReadFromStream(stream, position, buffer.AsSpan(0, want)))
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
                if (!TryReadFromStream(stream, position, box[..8]))
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
                    if (!TryReadFromStream(stream, position + 8, box[8..16]))
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

        private static bool TryReadFromStream(FileStream stream, long offset, Span<byte> destination)
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
        /// <summary>
        /// 扫签名，两种档位共用这一个入口（⛔ 只留一份扫描实现）：
        /// <list type="bullet">
        /// <item><description><b>快扫</b>（<paramref name="deep"/> = false）：只看文件尾 <see cref="MaxScanBytes"/> 字节，从后往前 ——
        /// 覆盖"归档贴着文件尾"的绝大多数；</description></item>
        /// <item><description><b>深扫</b>（<paramref name="deep"/> = true）：从文件头顺序扫到 <see cref="DeepScanBytes"/>
        /// （前缀最多 512 MiB），与 ZIP 那条"顺序扫找 EOCD"同一口径 —— 覆盖大前缀、归档本身很大的情形。</description></item>
        /// </list>
        /// </summary>
        private static System.Collections.Generic.IEnumerable<long> EnumerateSignatureOffsets(
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
                scanEnd = Math.Min(fileLength, DeepScanBytes);
            }
            else
            {
                scanStart = Math.Max(0, fileLength - MaxScanBytes);
                scanEnd = fileLength;
            }

            byte[] buffer = new byte[ScanBufferSize + signature.Length];

            long position = scanStart;

            while (position < scanEnd)
            {
                int want = (int)Math.Min(ScanBufferSize, scanEnd - position);
                int read;

                try
                {
                    using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);

                    stream.Seek(position, SeekOrigin.Begin);
                    read = stream.Read(buffer, 0, want);
                }
                catch
                {
                    yield break;
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
        private static bool TryReadExactly(string path, long offset, Span<byte> destination)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

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
    }
}

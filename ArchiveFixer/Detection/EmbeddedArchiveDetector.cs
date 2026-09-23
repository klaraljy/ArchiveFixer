using System;
using System.IO;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// "内嵌归档"的识别结果：文件尾部藏着一个完整的 ZIP，ZIP 之前还有别的数据。
    /// </summary>
    public sealed class EmbeddedArchiveInfo
    {
        /// <summary>是否命中。</summary>
        public bool Found { get; init; }

        /// <summary>内嵌归档在文件中的起始偏移（前面那 N 字节"别的东西"的长度）。</summary>
        public long Offset { get; init; }

        /// <summary>
        /// 内嵌归档的**结束位置（不含）**：EOCD 结束 + ZIP 注释长度。
        ///
        /// 为什么不直接用文件末尾：真实资源包是"视频 + 完整 ZIP **+ EOCD 之后还有十几 KB 正常数据**"
        /// （2026-09-24 用户机器上 5 个真文件实测：EOCD 距 EOF 14,350–17,424 字节，
        /// 那 14–17 KB 是打包格式本身的一部分，不是垃圾）。抠取必须按这个位置**截断**，
        /// 否则 EOCD 后面那些字节会被当成归档内容带出去 —— 多数读取器能忍，但并不保证。
        ///
        /// 0 表示"没有内嵌归档"；<see cref="Offset"/> == 0 时调用方一律按 EOF 处理（旧行为）。
        /// </summary>
        public long ArchiveEnd { get; init; }

        /// <summary>内嵌归档的格式。目前只会是 "ZIP"（7z / RAR 的目录结构不支持这种"偏移错位"的自洽校验）。</summary>
        public string Format { get; init; } = string.Empty;

        /// <summary>建议后缀。</summary>
        public string SuggestedExtension { get; init; } = string.Empty;

        /// <summary>ZIP 中央目录里声明的条目数。</summary>
        public int EntryCount { get; init; }

        /// <summary>
        /// 归档区间的字节数（<c>ArchiveEnd - Offset</c>）—— 也就是抠出来会得到多大的文件。
        /// 不是"从 Offset 到文件末尾"：见 <see cref="ArchiveEnd"/>。
        /// </summary>
        public long ArchiveLength { get; init; }

        /// <summary>给人看的中文说明，直接写进任务/日志。</summary>
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// 识别"双面文件"：前面是视频等正常数据，尾部却拼了一个完整的 ZIP。
    ///
    /// 为什么必须单独有这么一个检测器（真实案例，2026-09-21 用户实测）：
    /// 网盘下载回来的 <c>.mp4</c> 长度 183,986,940 字节，偏移 0 是 MP4 的 <c>ftyp</c> 头（所以双击能播），
    /// 偏移 17,031,321 却是 <c>PK\x03\x04</c>，最后 22 字节是 <c>PK\x05\x06</c>。
    /// 关键在于 **ZIP 内部的偏移仍然相对它自己的起点**：EOCD 里声明的中央目录偏移是 166,955,403，
    /// 而它在文件里的实际位置是 183,986,724，差值正好等于 ZIP 的起始偏移。
    ///
    /// 于是出现了三种读取器三种结论：
    /// · 媒体播放器只读前面的视频 → 能播；
    /// · 资源管理器把后缀改成 <c>.zip</c> 能打开（它的 ZIP 读取器容忍这种整体偏移）；
    /// · **7z.exe 只在"垫的数据 ≤ 8 MiB"时才容忍整体偏移**（本机 7-Zip 26.01 实测边界：
    ///   8,388,608 字节能打开，8,388,609 起就报 "Cannot open the file as archive"）。
    ///   真实文件前面垫了 17,031,321 字节，落在拒绝区 —— 原文件、以及改名成 <c>.zip</c> 的副本都是打不开的；
    ///   把 <c>[17,031,321, EOF)</c> 原样抠出来另存，<c>7z l</c> 立刻列出两个条目。
    ///
    /// 这也是"为什么不能只读文件头"的答案：<c>ArchiveDetectService</c> 只读前 34 KB
    /// （<c>HeaderReadLength</c>，为了让 ISO 9660 的卷描述符也能被看到），而 ZIP 在 17 MB 之后 ——
    /// 只看文件头永远看不到它，这种文件会被判成"格式未知"，用户也就永远不知道该把哪一段抠出来。
    ///
    /// <para>
    /// <b>2026-09-24 放宽判据（用户报的第二批真缺陷）</b>：用户机器上另外 5 个真实 <c>.mp4</c>
    /// （487 MB–2.4 GB，形态完全一致）全都没被识别出来。字节级取证结论：
    /// </para>
    /// <list type="bullet">
    /// <item><description>头部是真的 MP4 <c>ftypisom</c>，尾部是完整 ZIP —— 但
    /// **EOCD 之后还有 14,350–17,424 字节正常数据**（1 个文件正好在 EOF），旧判据
    /// "EOCD 必须正好是文件最后 22 + 注释长度字节"把它们全否掉了；</description></item>
    /// <item><description>其中 2.4 GB 那个还带 **ZIP64**：EOCD 前面紧挨着 20 字节 ZIP64 locator
    /// （<c>PK\x06\x07</c>）+ 56 字节 ZIP64 EOCD（<c>PK\x06\x06</c>），所以"中央目录实际起点"
    /// = <c>EOCD位置 − 76 − cdSize</c>。</description></item>
    /// </list>
    /// <para>
    /// 改法：不再要求"EOCD 就是文件收尾"，改成**逐个 EOCD 候选做自洽校验**（从后往前枚举），
    /// 任一个候选通过就成立。放宽后为什么仍然安全 —— 见 <see cref="Detect"/> 里的说明。
    /// </para>
    /// </summary>
    public static class EmbeddedArchiveDetector
    {
        /// <summary>EOCD 固定长度（不含注释）。</summary>
        private const int EndOfCentralDirectoryLength = 22;

        /// <summary>ZIP64 locator 固定长度（APPNOTE 4.3.15）。</summary>
        private const int Zip64LocatorLength = 20;

        /// <summary>ZIP64 EOCD 记录固定长度（APPNOTE 4.3.14）。</summary>
        private const int Zip64EndOfCentralDirectoryLength = 56;

        /// <summary>ZIP64 收尾（EOCD64 + locator）总长度：76 字节，正好夹在中央目录与 EOCD 之间。</summary>
        private const int Zip64FooterLength = Zip64EndOfCentralDirectoryLength + Zip64LocatorLength;

        /// <summary>
        /// 默认只读文件尾部这么多字节：256 KB。
        ///
        /// 来由：22 + 注释上限 65535 只够"EOCD 就是文件收尾"那种形态；放宽之后要一起放得下
        /// ZIP64 收尾（76 字节）**和** EOCD 之后那十几 KB 正常数据（实测 14,350–17,424 字节），
        /// 而且候选是从后往前逐个试的，窗口越大能覆盖的形态越多。256 KB 相对几百 MB 的源文件可以忽略。
        /// </summary>
        public const int DefaultTailBytes = 262144;

        private static readonly byte[] EndOfCentralDirectorySignature = { 0x50, 0x4B, 0x05, 0x06 };

        private static readonly byte[] CentralDirectorySignature = { 0x50, 0x4B, 0x01, 0x02 };

        private static readonly byte[] LocalFileHeaderSignature = { 0x50, 0x4B, 0x03, 0x04 };

        private static readonly byte[] Zip64EndOfCentralDirectorySignature = { 0x50, 0x4B, 0x06, 0x06 };

        private static readonly byte[] Zip64EndOfCentralDirectoryLocatorSignature = { 0x50, 0x4B, 0x06, 0x07 };

        /// <summary>
        /// 读文件尾部、**从后往前**枚举所有 <c>PK\x05\x06</c>（EOCD）候选，逐个做自洽校验；
        /// 第一个通过的候选就是结论。
        ///
        /// <para>每个候选要过三关（缺一不可）：</para>
        /// <list type="number">
        /// <item><description><b>中央目录签名正好在算出来的位置</b>：
        /// <c>actualCd = eocdAbs − (有 ZIP64 收尾 ? 76 : 0) − cdSize</c>，该处必须是 <c>PK\x01\x02</c>；</description></item>
        /// <item><description><b>归档起点正好是局部文件头签名</b>：<c>delta = actualCd − cdOffset</c> 必须 &gt; 0，
        /// 且 delta 处正好是 <c>PK\x03\x04</c>；</description></item>
        /// <item><description><b>ZIP64 占位符必须能解析</b>：EOCD 的 32 位字段写成 <c>0xFFFFFFFF</c> / <c>0xFFFF</c>
        /// 时，得能从 ZIP64 EOCD 记录里拿到真实值；拿不到就跳过这个候选（宁可漏报也不猜）。</description></item>
        /// </list>
        ///
        /// <para>
        /// 放宽"EOCD 必须正好是文件最后 22 + 注释长度字节"之后，为什么仍然不会误报：
        /// 结论要求**两条签名同时落在由文件里的数字算出来的位置上** —— 一条是"中央目录头"，
        /// 一条是"第一个局部文件头"，而且两者之间还夹着"delta 必须 &gt; 0"。
        /// 随机文件里凑出一个 <c>PK\x05\x06</c> 不难，但要让它同时满足这两条（还要 delta 为正）是不可能的；
        /// 自解压安装器（PE + 尾部数据）之类的形态也一样过不了这两关。
        /// 换句话说：**放宽的只是"EOCD 在文件里的位置"，没有放宽"自洽"本身。**
        /// </para>
        ///
        /// 任何 IO 异常 / 越界 / 数据不合理一律吞掉并返回 <c>Found = false</c>：
        /// 调用它的是"文件头不认识"这条兜底分支，兜底逻辑**不允许**把识别流程炸掉。
        /// </summary>
        /// <param name="filePath">待检测文件。</param>
        /// <param name="tailBytes">只读文件尾部这么多字节。</param>
        public static EmbeddedArchiveInfo Detect(string filePath, int tailBytes = DefaultTailBytes)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    return NotFound(string.Empty);
                }

                if (tailBytes < EndOfCentralDirectoryLength)
                {
                    tailBytes = EndOfCentralDirectoryLength;
                }

                long fileLength = new FileInfo(filePath).Length;

                // 连一个空 ZIP 的 EOCD 都放不下，谈不上"尾部藏着归档"。
                if (fileLength < EndOfCentralDirectoryLength)
                {
                    return NotFound(string.Empty);
                }

                using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    FileOptions.RandomAccess);

                long tailLength = Math.Min(fileLength, tailBytes);
                long tailStart = fileLength - tailLength;

                byte[] tail = ReadAt(stream, tailStart, (int)tailLength);

                if (tail.Length < EndOfCentralDirectoryLength)
                {
                    return NotFound(string.Empty);
                }

                /*
                 * 从后往前逐个试候选，而不是"只看最后一个，不成立就判死"。
                 *
                 * 为什么必须逐个试：EOCD 是 4 字节签名，它可能出现在 ZIP 注释里、也可能出现在
                 * EOCD 之后那段正常数据里；而**真正的** EOCD 未必是最后一个（真实现场里
                 * EOCD 之后还跟着 14–17 KB 数据，那些数据里完全可能再出现一次同样的字节序列）。
                 * 只看最后一个候选，正是这次用户 5 个文件里 4 个被否掉的直接原因。
                 */
                int eocdIndex = LastIndexOf(tail, EndOfCentralDirectorySignature, tail.Length - EndOfCentralDirectorySignature.Length);

                while (eocdIndex >= 0)
                {
                    EmbeddedArchiveInfo? hit = TryEvaluateCandidate(stream, tail, tailStart, eocdIndex, fileLength);

                    if (hit != null)
                    {
                        return hit;
                    }

                    eocdIndex = LastIndexOf(tail, EndOfCentralDirectorySignature, eocdIndex - 1);
                }

                return NotFound("文件尾部没有自洽的 ZIP 收尾（EOCD 与中央目录位置对不上），不是内嵌归档。");
            }
            catch (Exception)
            {
                /*
                 * 识别兜底分支里的任何意外（文件被占用、读一半被删、偏移越界、坏扇区…）
                 * 都只能得出"没找到内嵌归档"这一个结论：这里没有能力区分"文件坏了"和"没有内嵌归档"，
                 * 而向上抛异常会让整个扫描中断 —— 一个坏文件不该让一批任务全停。
                 */
                return NotFound(string.Empty);
            }
        }

        /// <summary>
        /// 校验一个 EOCD 候选。通过返回结论，不通过返回 null（调用方继续往前找下一个候选）。
        /// </summary>
        private static EmbeddedArchiveInfo? TryEvaluateCandidate(
            FileStream stream,
            byte[] tail,
            long tailStart,
            int eocdIndex,
            long fileLength)
        {
            // EOCD 必须完整落在读到的尾部里：签名在、字段被截断的情况在这里显式判掉（不拿异常当控制流）。
            if (eocdIndex + EndOfCentralDirectoryLength > tail.Length)
            {
                return null;
            }

            long eocdAbs = tailStart + eocdIndex;

            // EOCD 布局：偏移 10 = 条目总数(2)，12 = 中央目录大小(4)，16 = 中央目录偏移(4)，20 = 注释长度(2)。
            long entryCount16 = BitConverter.ToUInt16(tail, eocdIndex + 10);
            long cdSize32 = BitConverter.ToUInt32(tail, eocdIndex + 12);
            long cdOffset32 = BitConverter.ToUInt32(tail, eocdIndex + 16);
            int commentLength = BitConverter.ToUInt16(tail, eocdIndex + 20);

            /*
             * ZIP64 收尾判定：EOCD 前面 20 字节是 locator（PK 06 07，紧贴 EOCD），
             * locator 前面 56 字节是 ZIP64 EOCD 记录（PK 06 06）。顺序不能颠倒 ——
             * 这正是 ZIP64 规范的排列，写死它比"只要附近有这两个签名就算"安全得多。
             */
            bool hasZip64 = eocdIndex >= Zip64FooterLength
                && Matches(tail, eocdIndex - Zip64LocatorLength, Zip64EndOfCentralDirectoryLocatorSignature)
                && Matches(tail, eocdIndex - Zip64FooterLength, Zip64EndOfCentralDirectorySignature);

            long archiveEnd = eocdAbs + EndOfCentralDirectoryLength + commentLength;

            /*
             * 归档区间必须落在文件内：注释长度是从文件里读出来的，一个坏值就能把它顶到文件外面去。
             * 这一条同时保证抠取时"截断点"永远合法。
             */
            if (archiveEnd > fileLength)
            {
                return null;
            }

            if (hasZip64)
            {
                int recordIndex = eocdIndex - Zip64FooterLength;

                /*
                 * ZIP64 EOCD 记录布局（相对记录起点，APPNOTE 4.3.14）：
                 * 签名 4 + 记录大小 8 + 版本 2+2 + 盘号 4+4 + 本盘条目数 8 + 总条目数 8 + CD 大小 8 + CD 偏移 8。
                 */
                long zip64EntryCount = ReadUInt64AsLong(tail, recordIndex + 32);
                long zip64CdSize = ReadUInt64AsLong(tail, recordIndex + 40);
                long zip64CdOffset = ReadUInt64AsLong(tail, recordIndex + 48);

                // 解释一（规范做法）：有 ZIP64 记录时以它的 64 位字段为准。
                if (zip64CdSize >= 0 && zip64CdOffset >= 0)
                {
                    EmbeddedArchiveInfo? hit = Validate(
                        stream,
                        eocdAbs,
                        hasZip64: true,
                        zip64CdSize,
                        zip64CdOffset,
                        zip64EntryCount < 0 ? entryCount16 : zip64EntryCount,
                        archiveEnd,
                        fileLength);

                    if (hit != null)
                    {
                        return hit;
                    }
                }
            }

            /*
             * 解释二：普通 EOCD 的 32 位字段。
             *
             * 什么时候会走到这里：① 根本没有 ZIP64 收尾；② 有 ZIP64 收尾但 64 位字段写的是占位符
             * （有些工具即便不需要 ZIP64 也会写上这两个记录，字段却没填全）；
             * ③ ZIP64 那套值算出来的位置对不上（工具写歪了），而 32 位字段是对的。
             * 三种情况都要求同一组签名校验通过 —— 多试一种解释只会多救回真文件，不会放大误报。
             */
            if (cdSize32 != uint.MaxValue && cdOffset32 != uint.MaxValue && entryCount16 != ushort.MaxValue)
            {
                return Validate(
                    stream,
                    eocdAbs,
                    hasZip64,
                    cdSize32,
                    cdOffset32,
                    entryCount16,
                    archiveEnd,
                    fileLength);
            }

            /*
             * 占位符且拿不到 ZIP64 真实值：这个候选没法算，跳过。
             * 这正是"宁可漏报也不误报"的落点 —— 绝不用 0xFFFFFFFF 当数字去算一个乱指的偏移。
             */
            return null;
        }

        /// <summary>
        /// 拿一组（cdSize / cdOffset / 条目数）去验证候选：算位置 → 验两条签名 → 给结论。
        /// </summary>
        private static EmbeddedArchiveInfo? Validate(
            FileStream stream,
            long eocdAbs,
            bool hasZip64,
            long cdSize,
            long cdOffset,
            long entryCount,
            long archiveEnd,
            long fileLength)
        {
            if (cdSize < 0 || cdOffset < 0)
            {
                return null;
            }

            /*
             * 核心一步：中央目录的**实际**位置 = 它的**声明**位置 + ZIP 自己的起点。
             * 声明位置要靠"从 EOCD 往回数 cdSize 字节"得到（有 ZIP64 收尾时还要再跨过那 76 字节），
             * 与声明的 cdOffset 相减得到的 delta 即"ZIP 前面垫了多少字节"。
             */
            long footer = hasZip64 ? Zip64FooterLength : 0;

            if (cdSize > eocdAbs - footer)
            {
                return null;
            }

            long actualCd = eocdAbs - footer - cdSize;
            long delta = actualCd - cdOffset;

            // delta == 0：这是普通 ZIP（内部偏移本来就相对文件起点），交给文件头识别，不该由本检测器报出来。
            // delta < 0：声明的中央目录位置比实际位置还靠后 —— 不是"前面垫了数据"，抠出来也解不开。
            if (delta <= 0)
            {
                return null;
            }

            long archiveLength = archiveEnd - delta;

            if (archiveLength <= 0 || archiveEnd > fileLength)
            {
                return null;
            }

            // 校验一：声明位置 + delta 处必须真的是中央目录头。
            if (!HasSignatureAt(stream, actualCd, CentralDirectorySignature))
            {
                return null;
            }

            // 校验二：delta 处必须真的是第一个本地文件头 —— 这是"ZIP 从这里开始"的直接证据。
            if (!HasSignatureAt(stream, delta, LocalFileHeaderSignature))
            {
                return null;
            }

            long tailAfterEocd = fileLength - archiveEnd;

            return new EmbeddedArchiveInfo
            {
                Found = true,
                Offset = delta,
                ArchiveEnd = archiveEnd,
                Format = "ZIP",
                SuggestedExtension = ".zip",
                EntryCount = (int)Math.Min(entryCount, int.MaxValue),
                ArchiveLength = archiveLength,
                Message =
                    $"文件尾部藏着一个 ZIP（前面 {delta} 字节是别的数据，例如视频），需要用偏移取出后才能解压。" +
                    $"ZIP 区间：{delta}–{archiveEnd}，条目数：{entryCount}，需要取出 {archiveLength} 字节" +
                    (tailAfterEocd > 0
                        ? $"；EOCD 之后还有 {tailAfterEocd} 字节尾部数据，不属于归档，抠取时按 {archiveEnd} 截断。"
                        : "。")
            };
        }

        /// <summary>
        /// 从 8 字节小端读一个 64 位无符号数；超过 <see cref="long.MaxValue"/>（现实里不存在）
        /// 或正好是全 1 占位符时返回 -1，表示"这个字段拿不到真实值"。
        /// </summary>
        private static long ReadUInt64AsLong(byte[] buffer, int index)
        {
            if (index < 0 || index + 8 > buffer.Length)
            {
                return -1;
            }

            ulong value = BitConverter.ToUInt64(buffer, index);

            if (value == ulong.MaxValue || value > long.MaxValue)
            {
                return -1;
            }

            return (long)value;
        }

        private static EmbeddedArchiveInfo NotFound(string message)
        {
            return new EmbeddedArchiveInfo { Message = message ?? string.Empty };
        }

        /// <summary>从指定偏移读一段字节；读不满就返回实际读到的部分（不足时由调用方判失败）。</summary>
        private static byte[] ReadAt(FileStream stream, long offset, int count)
        {
            if (offset < 0 || count <= 0)
            {
                return Array.Empty<byte>();
            }

            stream.Seek(offset, SeekOrigin.Begin);

            byte[] buffer = new byte[count];
            int total = 0;

            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);

                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            if (total == count)
            {
                return buffer;
            }

            byte[] partial = new byte[total];
            Array.Copy(buffer, partial, total);

            return partial;
        }

        /// <summary>指定绝对偏移处是否是指定签名。读不到（越界 / 文件被截断）一律 false。</summary>
        private static bool HasSignatureAt(FileStream stream, long offset, byte[] signature)
        {
            if (offset < 0)
            {
                return false;
            }

            byte[] actual = ReadAt(stream, offset, signature.Length);

            if (actual.Length != signature.Length)
            {
                return false;
            }

            for (int i = 0; i < signature.Length; i++)
            {
                if (actual[i] != signature[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>缓冲区里 <paramref name="index"/> 处是否正好是这段签名（越界一律 false）。</summary>
        private static bool Matches(byte[] buffer, int index, byte[] signature)
        {
            if (index < 0 || index + signature.Length > buffer.Length)
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

        /// <summary>在缓冲区里从 <paramref name="startIndex"/> 往前找签名，返回下标；找不到返回 -1。</summary>
        private static int LastIndexOf(byte[] buffer, byte[] signature, int startIndex)
        {
            int start = Math.Min(startIndex, buffer.Length - signature.Length);

            for (int i = start; i >= 0; i--)
            {
                if (Matches(buffer, i, signature))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}

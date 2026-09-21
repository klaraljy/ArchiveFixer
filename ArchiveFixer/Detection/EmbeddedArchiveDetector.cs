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

        /// <summary>内嵌归档的格式。目前只会是 "ZIP"（7z / RAR 的目录结构不支持这种"偏移错位"的自洽校验）。</summary>
        public string Format { get; init; } = string.Empty;

        /// <summary>建议后缀。</summary>
        public string SuggestedExtension { get; init; } = string.Empty;

        /// <summary>ZIP 中央目录里声明的条目数。</summary>
        public int EntryCount { get; init; }

        /// <summary>从 <see cref="Offset"/> 到文件末尾的字节数（也就是抠出来会得到多大的文件）。</summary>
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
    /// 判定手法（不猜内容，只做自洽性校验）：
    /// 尾部 EOCD 里"声明的中央目录偏移"和"中央目录的实际位置"之间必然差一个常量，
    /// 那个常量就是 ZIP 自己的起点；再要求该起点处确实是 <c>PK\x03\x04</c>、声明的中央目录处确实是
    /// <c>PK\x01\x02</c>，才敢下结论 —— 少了这两道校验，任何一个尾部凑巧带 EOCD 字节的文件都会被误报。
    /// </summary>
    public static class EmbeddedArchiveDetector
    {
        /// <summary>EOCD 固定长度（不含注释）。</summary>
        private const int EndOfCentralDirectoryLength = 22;

        /// <summary>默认只读文件尾部这么多字节：22 + 65535（ZIP 注释上限）足够，取 128 KB 留足余量。</summary>
        public const int DefaultTailBytes = 131072;

        private static readonly byte[] EndOfCentralDirectorySignature = { 0x50, 0x4B, 0x05, 0x06 };

        private static readonly byte[] CentralDirectorySignature = { 0x50, 0x4B, 0x01, 0x02 };

        private static readonly byte[] LocalFileHeaderSignature = { 0x50, 0x4B, 0x03, 0x04 };

        /// <summary>
        /// 读文件尾部找 ZIP 的 EOCD，用"声明的中央目录偏移 vs 实际位置"求差，
        /// 差值 &gt; 0 且落在合法位置时判定为"内嵌 ZIP"。
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
                    return NotFound();
                }

                if (tailBytes < EndOfCentralDirectoryLength)
                {
                    tailBytes = EndOfCentralDirectoryLength;
                }

                long fileLength = new FileInfo(filePath).Length;

                // 连一个空 ZIP 的 EOCD 都放不下，谈不上"尾部藏着归档"。
                if (fileLength < EndOfCentralDirectoryLength)
                {
                    return NotFound();
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
                    return NotFound();
                }

                int eocdIndex = LastIndexOf(tail, EndOfCentralDirectorySignature);

                if (eocdIndex < 0)
                {
                    return NotFound();
                }

                long eocdPosition = tailStart + eocdIndex;

                /*
                 * EOCD 必须完整落在读到的尾部里。
                 * 文件被截断时签名可能还在、后面的字段却已经没了 —— 靠下面的越界异常兜住也算"不命中"，
                 * 但那属于"用异常当控制流"，这里显式判掉，读起来才知道边界在哪。
                 */
                if (eocdIndex + EndOfCentralDirectoryLength > tail.Length)
                {
                    return NotFound();
                }

                // EOCD 布局：偏移 10 = 条目总数(2)，12 = 中央目录大小(4)，16 = 中央目录偏移(4)，20 = 注释长度(2)。
                int entryCount = BitConverter.ToUInt16(tail, eocdIndex + 10);
                long declaredCentralDirectorySize = BitConverter.ToUInt32(tail, eocdIndex + 12);
                long declaredCentralDirectoryOffset = BitConverter.ToUInt32(tail, eocdIndex + 16);
                int commentLength = BitConverter.ToUInt16(tail, eocdIndex + 20);

                /*
                 * EOCD 必须正好是文件最后 22 + 注释长度 字节 —— 这是"它就是本文件的收尾"的唯一凭据。
                 *
                 * 少了这一句，尾部之后的任何字节（例如另一次拼接、下载残留）都会让下面的算术整体偏移，
                 * 算出来的 delta 就成了"偏移量 + 尾部垃圾长度"，接着会去错误的位置找签名；
                 * 与其靠后面的签名校验去兜，不如在这里就把"算术前提不成立"的情况判掉。
                 *
                 * 代价是"ZIP 后面还跟着别的数据"的文件会被判成不命中 —— 那类文件本来就说不清
                 * 归档到哪儿结束，保守错过比误报一个偏移安全。
                 */
                long eocdFromTail = fileLength - EndOfCentralDirectoryLength - commentLength;

                if (eocdFromTail != eocdPosition)
                {
                    return NotFound();
                }

                /*
                 * 核心一步：中央目录的**实际**位置 = 它的**声明**位置 + ZIP 自己的起点。
                 * 这里的 eocdPosition - declaredCentralDirectorySize 就是中央目录实际从哪开始，
                 * 与声明值相减得到的 delta 即"ZIP 前面垫了多少字节"。
                 *
                 * 已知边界：ZIP64 归档（单条 > 4 GB 或条目数 > 65535）在 EOCD 里放的是 0xFFFFFFFF 占位符，
                 * 真实数值在它前面的 ZIP64 EOCD 里 —— 那种情况下这里算出来的 delta 会变成负数，
                 * 于是判成"不命中"。这是**有意保守**：宁可漏报（结果还是"格式未知"），
                 * 也不要拿占位符算出一个乱指的偏移去抠一段没用的字节出来。
                 */
                long delta = eocdPosition - declaredCentralDirectorySize - declaredCentralDirectoryOffset;

                // delta == 0：这是普通 ZIP（内部偏移本来就相对文件起点），交给文件头识别，不该由本检测器报出来。
                if (delta == 0)
                {
                    return NotFound();
                }

                /*
                 * delta < 0：声明的中央目录位置比实际位置还靠后。
                 * 这类文件不是"前面垫了数据"，而是 EOCD 与真实布局对不上（损坏、或根本不是 ZIP 的尾巴），
                 * 抠出来也解不开，所以不命中。
                 */
                if (delta < 0)
                {
                    return NotFound();
                }

                long archiveLength = fileLength - delta;

                /*
                 * 校验三：抠出来的这一段必须整个落在文件内。
                 *
                 * archiveLength 定义为 fileLength - delta，所以这条在数学上恒成立；仍然显式写出来，
                 * 是因为 delta 完全由文件里的字节算出来，不能假设它温和 —— 这条同时挡住溢出，
                 * 也保证以后有人改动上面的定义时这里会立刻失败，而不是悄悄放过。
                 */
                if (archiveLength <= 0 || delta > fileLength || archiveLength > fileLength - delta)
                {
                    return NotFound();
                }

                // 校验一：声明位置 + delta 处必须真的是中央目录头。
                if (!HasSignatureAt(stream, delta + declaredCentralDirectoryOffset, CentralDirectorySignature))
                {
                    return NotFound();
                }

                // 校验二：delta 处必须真的是第一个本地文件头 —— 这是"ZIP 从这里开始"的直接证据。
                if (!HasSignatureAt(stream, delta, LocalFileHeaderSignature))
                {
                    return NotFound();
                }

                return new EmbeddedArchiveInfo
                {
                    Found = true,
                    Offset = delta,
                    Format = "ZIP",
                    SuggestedExtension = ".zip",
                    EntryCount = entryCount,
                    ArchiveLength = archiveLength,
                    Message =
                        $"文件尾部藏着一个 ZIP（前面 {delta} 字节是别的数据，例如视频），需要用偏移取出后才能解压。" +
                        $"ZIP 起点：{delta}，条目数：{entryCount}，需要取出 {archiveLength} 字节。"
                };
            }
            catch (Exception)
            {
                /*
                 * 识别兜底分支里的任何意外（文件被占用、读一半被删、偏移越界、坏扇区…）
                 * 都只能得出"没找到内嵌归档"这一个结论：这里没有能力区分"文件坏了"和"没有内嵌归档"，
                 * 而向上抛异常会让整个扫描中断 —— 一个坏文件不该让一批任务全停。
                 */
                return NotFound();
            }
        }

        private static EmbeddedArchiveInfo NotFound()
        {
            return new EmbeddedArchiveInfo();
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

        /// <summary>在缓冲区里从后往前找签名，返回最后一个匹配的下标；找不到返回 -1。</summary>
        private static int LastIndexOf(byte[] buffer, byte[] signature)
        {
            for (int i = buffer.Length - signature.Length; i >= 0; i--)
            {
                bool matched = true;

                for (int j = 0; j < signature.Length; j++)
                {
                    if (buffer[i + j] != signature[j])
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// **只读头部的 7z 加密判读**（用户 2026-09-30 任务：识别阶段看得出 7z 加没加密，
    /// ⛔ 不额外调引擎、不引入依赖、不把包体读进来）。
    ///
    /// <para><b>为什么要有它</b>：识别阶段过去只看开头那 6 个魔数字节。而 7z 的加密信号写在
    /// **coder 链**里（AES-256 的 method ID = <c>06 F1 07 01</c>），要么在明文主头里（<c>-p</c>），
    /// 要么在"编码头"的解码链里（<c>-mhe</c>）—— 两种都只看开头 6 个字节是看不见的。
    /// 后果与 RAR 那一档一样：批首那句"本批有 N 个包没有可用密码"的预判对 7z 整档漏报；
    /// 而 <c>-mhe</c> 更狠 —— 引擎侧连条目名都列不出来（<c>7z l -slt</c> 一条都不报），
    /// 是"程序现在完全不知道它加密"的那一档。</para>
    ///
    /// <para><b>7z 的布局（官方 7zFormat.txt，逐字节核过真样本）</b>：</para>
    /// <list type="number">
    /// <item><description><b>起始头 32 字节</b> = 签名(6) + 版本(2) + StartHeaderCRC(4) +
    /// <b>NextHeaderOffset</b>(LE64，相对偏移 32 起算) + <b>NextHeaderSize</b>(LE64) + NextHeaderCRC(4)。
    /// 四个单文件样本都满足 <c>32 + NextHeaderOffset + NextHeaderSize == 文件长度</c> ——
    /// 这条自洽式就是我们的布局闸门（不成立一律"不知道"）。</description></item>
    /// <item><description><b>next header</b> 的第一字节：<c>0x01</c> = <c>kHeader</c>（明文头）、
    /// <c>0x17</c> = <c>kEncodedHeader</c>（头本身被编码 —— 压缩或加密）。</description></item>
    /// <item><description><b>StreamsInfo</b> = <c>[kPackInfo(0x06)] [kUnPackInfo(0x07)] [kSubStreamsInfo(0x08)] kEnd(0x00)</c>；
    /// <c>kUnPackInfo</c> 里 <c>kFolder(0x0B)</c> 给出每个文件夹的 coder 链，
    /// <b>coder</b> = flags（低 4 位 = ID 字节数 / <c>0x10</c> = 复杂（带入出流个数）/ <c>0x20</c> = 带属性）
    /// → ID 字节 → 可选入/出流个数 → 可选属性。数字是 7z 变长编码（首字节高位连续 1 的个数 = 后续字节数，
    /// **低位在前**，不是 RAR5 那种 7 位一组的 vint）。</description></item>
    /// </list>
    ///
    /// <para><b>三档结论是怎么定的</b>（其余一律"不知道"）：</para>
    /// <list type="bullet">
    /// <item><description><c>kEncodedHeader</c> 且解码链里有 AES ⇒ <see cref="ArchiveEncryptionState.HeadersEncrypted"/>
    /// （<c>-mhe</c> / <c>-hp</c>）；</description></item>
    /// <item><description><c>kHeader</c> 且 MainStreamsInfo 的 coder 链里有 AES ⇒
    /// <see cref="ArchiveEncryptionState.DataEncrypted"/>（<c>-p</c>）；</description></item>
    /// <item><description><c>kHeader</c> 且链里没有 AES ⇒ <see cref="ArchiveEncryptionState.NotEncrypted"/>。</description></item>
    /// </list>
    ///
    /// <para><b>⚠ 一条必须记住的"不知道"</b>：<c>kEncodedHeader</c> 而**解码链里没有 AES** 时，
    /// 结论是 <see cref="ArchiveEncryptionState.Unknown"/>，⛔ **不是"没加密"** ——
    /// 头被压缩时文件数据的 coder 链要解压之后才看得见。实测 <c>many-p.7z</c>（80 个文件 + <c>-p</c>）
    /// 的编码头里只有 LZMA、看不见任何 AES，可它是**真加密**的（<c>7z t -pWrongPassword</c> 报
    /// "Wrong password?"）。而普通包 <c>many-plain.7z</c> 的编码头里同样只有 LZMA ——
    /// 两者在这 64 KiB 里**逐字节同构**，谁也没法区分 ⇒ 这一档只能如实说"不知道"。
    /// （引擎侧：<c>7z l -slt</c> 对 <c>-p</c> 包报 80 条 <c>Encrypted = +</c>，
    /// 对普通包报 81 条 <c>Encrypted = -</c> —— 也就是说这一档我们确实比引擎弱，如实写在
    /// <c>docs/真机事故复盘.md</c> 里。）</para>
    ///
    /// <para><b>多卷 7z</b>：7z 分卷是**原样切**的（<c>-v100k</c> 切出来的每一卷就是整包的一段连续字节），
    /// 元数据在**最后一卷**，所以"只看第 1 卷"读不出 7z 的加密。判据是
    /// <c>32 + NextHeaderOffset</c> 落在整个卷组的**拼接坐标**里 → 卷长用 <see cref="FileInfo"/> 求和 →
    /// 只读覆盖该范围的那一卷（跨两卷就读两段）。单文件判读就是"只有一卷"的特例，同一条代码路径
    /// （真样本实测：<c>32 + offset + size == 三卷长度之和</c>，逐字节核过）。</para>
    /// </summary>
    public static class SevenZipEncryptionReader
    {
        /// <summary>
        /// **读入量硬上限**（64 KiB）：整个判读过程从文件里读进内存的字节数不许超过它
        /// （起始头 32 字节 + next header 最多这么多）。真实包的头远小于它，一旦超过就说明
        /// "这不是我们认识的形状"，按"不知道"返回 —— 宁可漏报也不猜。
        /// </summary>
        public const int MaxHeaderBytes = 64 * 1024;

        /// <summary>起始头长度（签名 6 + 版本 2 + CRC 4 + 两个 LE64 + CRC 4）。</summary>
        public const int StartHeaderLength = 32;

        /// <summary>最多认多少个文件夹（防畸形头把预算耗在无穷无尽的 coder 上）。</summary>
        public const int MaxFolders = 4096;

        /// <summary>一个文件夹里最多认多少个 coder。</summary>
        public const int MaxCodersPerFolder = 256;

        /// <summary>整份头里最多认多少个 coder（跨文件夹累计）。</summary>
        public const int MaxCodersTotal = 16384;

        /// <summary>PackInfo 里最多认多少条 pack stream。</summary>
        public const int MaxPackStreams = 1 << 20;

        /// <summary>7z 魔数：<c>37 7A BC AF 27 1C</c>。</summary>
        private static readonly byte[] Signature = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };

        /// <summary>
        /// **AES-256 + SHA-256 的 coder ID**（<c>06 F1 07 01</c>，大端写在头里）。
        ///
        /// <para>7-Zip 源码 <c>CPP/7zip/Crypto/7zAes.h</c> 里只注册了这一个 AES 变体
        /// （<c>k_AES = 0x06F10701</c>），所以这里是**精确匹配**，⛔ 不做前缀匹配 ——
        /// "看着像 AES 就算"正是这个项目最不许的猜。</para>
        /// </summary>
        private const uint AesCoderId = 0x06F10701;

        // ===== 7z 头属性 ID（官方 7zFormat.txt / 7zIn.cpp 的 kXxx 常量）=====
        private const byte KEnd = 0x00;
        private const byte KHeader = 0x01;
        private const byte KArchiveProperties = 0x02;
        private const byte KAdditionalStreamsInfo = 0x03;
        private const byte KMainStreamsInfo = 0x04;
        private const byte KFilesInfo = 0x05;
        private const byte KPackInfo = 0x06;
        private const byte KUnPackInfo = 0x07;
        private const byte KSize = 0x09;
        private const byte KCRC = 0x0A;
        private const byte KFolder = 0x0B;
        private const byte KEncodedHeader = 0x17;

        /// <summary>coder flags：低 4 位 = ID 的字节数。</summary>
        private const byte CoderIdSizeMask = 0x0F;

        /// <summary>coder flags：复杂 coder（后面还有"入流个数 + 出流个数"两个数字）。</summary>
        private const byte CoderComplex = 0x10;

        /// <summary>coder flags：带属性（后面还有"属性长度 + 属性字节"）。</summary>
        private const byte CoderHasProperties = 0x20;

        /// <summary>
        /// 判读一个 7z（或内嵌在别的文件里、从 <paramref name="offset"/> 开始的一段 7z）有没有加密。
        ///
        /// <para><paramref name="volumePaths"/> 只在 <paramref name="offset"/> 为 0 且给了**两卷以上**时生效
        /// （= 一组 7z 分卷；内嵌段里不可能再套一组分卷，所以那时一律忽略它）。
        /// 不给卷组信息时按单文件判读 —— 多卷包会落到"不知道"（元数据在最后一卷，单看第 1 卷本来就看不出来）。</para>
        ///
        /// <para>任何 IO 意外（不存在 / 被占用 / 权限 / 半路被删 / 尺寸对不上）都落成
        /// <see cref="ArchiveEncryptionState.Unknown"/>，**绝不抛** —— 识别阶段不许因为它失败。</para>
        /// </summary>
        public static ArchiveEncryptionReading Read(
            string? filePath,
            long offset = 0,
            IReadOnlyList<string>? volumePaths = null)
        {
            if (string.IsNullOrWhiteSpace(filePath) || offset < 0)
            {
                return NoPath();
            }

            try
            {
                List<Segment>? segments = BuildSegments(filePath!, offset, volumePaths);

                if (segments == null)
                {
                    return Unknown(0, "卷组信息不完整（有卷读不到大小）");
                }

                long total = 0;

                foreach (Segment segment in segments)
                {
                    total += segment.Length;
                }

                int bytesRead = 0;

                if (offset > total - StartHeaderLength)
                {
                    return Unknown(bytesRead, "起始头读不到（文件比 32 字节还短）");
                }

                byte[]? startHeader = ReadRange(segments, offset, StartHeaderLength, ref bytesRead);

                if (startHeader == null)
                {
                    return Unknown(bytesRead, "起始头读不出来（被截断 / 读到一半文件变了）");
                }

                if (!StartsWithSignature(startHeader))
                {
                    return Unknown(bytesRead, "这一段的开头不是 7z 签名");
                }

                ulong nextHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(startHeader.AsSpan(12, 8));
                ulong nextHeaderSize = BinaryPrimitives.ReadUInt64LittleEndian(startHeader.AsSpan(20, 8));

                if (nextHeaderOffset > long.MaxValue - StartHeaderLength || nextHeaderSize > long.MaxValue)
                {
                    return Unknown(bytesRead, "NextHeaderOffset / NextHeaderSize 超出可表示范围（布局不符）");
                }

                if (nextHeaderSize == 0)
                {
                    return Unknown(bytesRead, "NextHeaderSize 为 0（布局不符）");
                }

                long nextHeaderStart = offset + StartHeaderLength + (long)nextHeaderOffset;

                // 溢出要显式挡掉：先比下界（溢出会变成负数），再用"总长 − 起点"避免加法再溢出一次。
                if (nextHeaderStart < offset
                    || nextHeaderStart > total
                    || (long)nextHeaderSize > total - nextHeaderStart)
                {
                    /*
                     * 这是**布局闸门**：32 + NextHeaderOffset + NextHeaderSize 必须落在这一份
                     * 文件（或整个卷组的拼接）之内。它拦住的正好是"多卷包的第 1 卷"
                     * —— 那时 NextHeaderOffset 指向的是整包的尾部，落在后面的卷里。
                     */
                    return Unknown(bytesRead, "32 + NextHeaderOffset + NextHeaderSize 超出文件长度（截断 / 不是一个完整的 7z）");
                }

                int want = (int)Math.Min((long)nextHeaderSize, MaxHeaderBytes);
                byte[]? header = ReadRange(segments, nextHeaderStart, want, ref bytesRead);

                if (header == null)
                {
                    return Unknown(bytesRead, "next header 读不出来（被截断 / 读到一半文件变了）");
                }

                return Parse(header, bytesRead);
            }
            catch (Exception)
            {
                // 读不到（被占 / 权限 / 半路被删）就是"看不出来"：调用方照旧按"不报加密"处理。
                return NoPath();
            }
        }

        /// <summary>没有路径 / 读不出来时的"不知道"（不带依据 —— 没有可说的结构化事实）。</summary>
        private static ArchiveEncryptionReading NoPath() => new()
        {
            State = ArchiveEncryptionState.Unknown,
            Basis = "没有可读的文件路径"
        };

        /// <summary>
        /// 把"要读哪几个文件、各自多长"整理成段列表。
        ///
        /// <para>单文件判读 = **只有一段**（同一条读取路径的特例），
        /// 所以"跨卷读两段"这条边界不会成为一套只在分卷上才跑得到的代码。</para>
        /// </summary>
        private static List<Segment>? BuildSegments(string filePath, long offset, IReadOnlyList<string>? volumePaths)
        {
            var paths = new List<string>();

            if (offset == 0 && volumePaths != null && volumePaths.Count > 1)
            {
                foreach (string? path in volumePaths)
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        return null;
                    }

                    paths.Add(path!);
                }
            }
            else
            {
                paths.Add(filePath);
            }

            var segments = new List<Segment>(paths.Count);

            foreach (string path in paths)
            {
                long length;

                try
                {
                    length = new FileInfo(path).Length;
                }
                catch
                {
                    return null;
                }

                if (length < 0)
                {
                    return null;
                }

                segments.Add(new Segment(path, length));
            }

            return segments;
        }

        /// <summary>
        /// 在**卷组拼接坐标**里读 <c>[position, position + count)</c>：只打开真正覆盖这一段的那几卷
        /// （跨两卷就是两次打开、各读一段）。任何一卷读不满 / 长度与刚才量到的不一致都返回 null
        /// （源文件在两次 stat 之间变过 ⇒ 如实"不知道"，不猜）。
        /// </summary>
        private static byte[]? ReadRange(List<Segment> segments, long position, int count, ref int bytesRead)
        {
            if (count <= 0)
            {
                return Array.Empty<byte>();
            }

            var buffer = new byte[count];
            int filled = 0;
            long cursor = 0;

            foreach (Segment segment in segments)
            {
                long segmentStart = cursor;
                long segmentEnd = cursor + segment.Length;

                cursor = segmentEnd;

                if (segmentEnd <= position || filled >= count)
                {
                    continue;
                }

                long from = Math.Max(position, segmentStart);
                int skip = (int)(from - segmentStart);
                int want = (int)Math.Min(count - filled, segment.Length - skip);

                if (want <= 0)
                {
                    continue;
                }

                using var stream = new FileStream(
                    segment.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                if (stream.Length != segment.Length)
                {
                    return null;
                }

                stream.Seek(skip, SeekOrigin.Begin);

                int total = 0;

                while (total < want)
                {
                    int read = stream.Read(buffer, filled + total, want - total);

                    if (read <= 0)
                    {
                        return null;
                    }

                    total += read;
                }

                filled += total;
                bytesRead += total;
            }

            return filled == count ? buffer : null;
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

        /// <summary>
        /// 解析 next header 的**开头那一段**，得出结论。
        ///
        /// <para>⛔ 刻意**只解析到能判定 coder 就停**（kUnPackInfo 的文件夹链读完就收工）：
        /// 读得越少，越不会被与"加没加密"无关的结构（SubStreamsInfo、FilesInfo…）绊倒。</para>
        /// </summary>
        private static ArchiveEncryptionReading Parse(byte[] header, int bytesRead)
        {
            var cursor = new HeaderCursor(header);

            if (!cursor.TryReadByte(out byte first))
            {
                return Unknown(bytesRead, "next header 是空的");
            }

            if (first == KEncodedHeader)
            {
                if (!TryReadStreamsInfo(cursor, out bool hasAes, out bool sawCoderInfo, out string failure))
                {
                    return Unknown(bytesRead, "kEncodedHeader 的解码链读不出来：" + failure);
                }

                if (hasAes)
                {
                    return Encrypted(
                        bytesRead,
                        ArchiveEncryptionState.HeadersEncrypted,
                        "7z 编码头（kEncodedHeader）的解码链里有 AES coder（0x06F10701）：头也加密（-mhe）");
                }

                if (!sawCoderInfo)
                {
                    return Unknown(bytesRead, "kEncodedHeader 的 StreamsInfo 里没有 kUnPackInfo：读不出解码链");
                }

                /*
                 * ⛔ 这一档**必须**是"不知道"：头被压缩时，文件数据的 coder 链在解压之后才看得见。
                 * 实测 many-p.7z（80 个文件 + -p）与 many-plain.7z（同样 80 个文件、不加密）
                 * 的编码头在这里**逐字节同构**（都只有 LZMA），而前者是真加密的
                 * ⇒ 说"没加密"就是误报，"不知道"才是唯一诚实的答案。
                 */
                return Unknown(
                    bytesRead,
                    "kEncodedHeader 的解码链里没有 AES，而文件数据的 coder 藏在编码流里（解压后才看得见）");
            }

            if (first != KHeader)
            {
                return Unknown(bytesRead, "next header 的第一个字节既不是 kHeader(0x01) 也不是 kEncodedHeader(0x17)");
            }

            while (cursor.TryReadByte(out byte propertyId))
            {
                if (propertyId == KEnd)
                {
                    break;
                }

                if (propertyId == KArchiveProperties)
                {
                    if (!TrySkipArchiveProperties(cursor, out string propertiesFailure))
                    {
                        return Unknown(bytesRead, "ArchiveProperties 读不出来：" + propertiesFailure);
                    }

                    continue;
                }

                if (propertyId == KMainStreamsInfo)
                {
                    if (!TryReadStreamsInfo(cursor, out bool hasAes, out bool sawCoderInfo, out string failure))
                    {
                        return Unknown(bytesRead, "MainStreamsInfo 读不出来：" + failure);
                    }

                    if (hasAes)
                    {
                        return Encrypted(
                            bytesRead,
                            ArchiveEncryptionState.DataEncrypted,
                            "7z 明文头 MainStreamsInfo 的 coder 链里有 AES coder（0x06F10701）：文件数据加密（-p）");
                    }

                    if (!sawCoderInfo)
                    {
                        return Unknown(bytesRead, "MainStreamsInfo 里没有 kUnPackInfo：读不出 coder 链");
                    }

                    return Plain(bytesRead, "7z 明文头 MainStreamsInfo 的 coder 链里没有 AES coder");
                }

                if (propertyId == KFilesInfo)
                {
                    // 走到 FilesInfo 都还没见过 MainStreamsInfo ⇒ 这一包里没有数据流（只有目录 / 空文件）。
                    return Plain(bytesRead, "头走到 kFilesInfo 也没有 MainStreamsInfo：这一包没有数据流");
                }

                if (propertyId == KAdditionalStreamsInfo)
                {
                    /*
                     * ⛔ 刻意不做这一档：AdditionalStreamsInfo 的边界在格式说明的不同版本里写法不一致
                     * （它内嵌的 StreamsInfo 后面到底还有没有一个 kEnd），而读错一步就会把后面的字节
                     * 当成头属性继续解析 —— 那正是"猜"。它只出现在外置属性 / 编码数据的包上，
                     * 内置 7z.exe 对普通归档从不产出。
                     */
                    return Unknown(bytesRead, "头里有 kAdditionalStreamsInfo：这一档的结构读不出来");
                }

                return Unknown(bytesRead, "头里出现了预期之外的属性 0x" + propertyId.ToString("X2"));
            }

            return Plain(bytesRead, "头走到 kEnd 都没有 MainStreamsInfo：这一包没有数据流");
        }

        /// <summary>
        /// 读一段 <c>StreamsInfo</c>：<c>[kPackInfo] [kUnPackInfo] [kSubStreamsInfo] kEnd</c>。
        ///
        /// <para>⚠ 读到 <c>kUnPackInfo</c> 就**直接返回**（coder 链全在那里，判定已经做完了）；
        /// 只有一整段 StreamsInfo 走完都没有 kUnPackInfo 时才会继续遇到 kEnd，
        /// 那时 <paramref name="sawCoderInfo"/> 为 false —— 调用方按"读不出 coder 链"处理，⛔ 不当成"没加密"。</para>
        /// </summary>
        private static bool TryReadStreamsInfo(
            HeaderCursor cursor,
            out bool hasAes,
            out bool sawCoderInfo,
            out string failure)
        {
            hasAes = false;
            sawCoderInfo = false;
            failure = string.Empty;

            while (cursor.TryReadByte(out byte propertyId))
            {
                if (propertyId == KEnd)
                {
                    return true;
                }

                if (propertyId == KPackInfo)
                {
                    if (!TrySkipPackInfo(cursor, out failure))
                    {
                        return false;
                    }

                    continue;
                }

                if (propertyId == KUnPackInfo)
                {
                    sawCoderInfo = true;

                    return TryReadUnPackInfo(cursor, out hasAes, out failure);
                }

                failure = "StreamsInfo 里出现了预期之外的属性 0x" + propertyId.ToString("X2");

                return false;
            }

            failure = "StreamsInfo 读到缓冲区末尾（头被截断）";

            return false;
        }

        /// <summary>
        /// 跳过 <c>kPackInfo</c>：<c>packPos NumPackStreams [kSize …] [kCRC …] kEnd</c>。
        ///
        /// <para>它与"加没加密"无关，但**必须正确跳过**才能走到后面的 kUnPackInfo。</para>
        /// </summary>
        private static bool TrySkipPackInfo(HeaderCursor cursor, out string failure)
        {
            failure = string.Empty;

            if (!cursor.TryReadNumber(out _))
            {
                failure = "PackInfo 的 packPos 读不出来";

                return false;
            }

            if (!cursor.TryReadNumber(out ulong packStreams) || packStreams > MaxPackStreams)
            {
                failure = "PackInfo 的流数读不出来 / 越界";

                return false;
            }

            while (cursor.TryReadByte(out byte propertyId))
            {
                if (propertyId == KEnd)
                {
                    return true;
                }

                if (propertyId == KSize)
                {
                    for (ulong index = 0; index < packStreams; index++)
                    {
                        if (!cursor.TryReadNumber(out _))
                        {
                            failure = "PackInfo 的 kSize 读不出来";

                            return false;
                        }
                    }

                    continue;
                }

                if (propertyId == KCRC)
                {
                    if (!TrySkipDigests(cursor, packStreams, out failure))
                    {
                        return false;
                    }

                    continue;
                }

                failure = "PackInfo 里出现了预期之外的属性 0x" + propertyId.ToString("X2");

                return false;
            }

            failure = "PackInfo 读到缓冲区末尾（头被截断）";

            return false;
        }

        /// <summary>
        /// 跳过一组 CRC：<c>AllAreDefined(1)</c>；为 1 时后面是 <c>count × 4</c> 字节，
        /// 为 0 时先是一张 <c>count</c> 位的位图，再按置位个数各 4 字节（7-Zip 的 <c>ReadHashDigests</c>）。
        /// </summary>
        private static bool TrySkipDigests(HeaderCursor cursor, ulong count, out string failure)
        {
            failure = string.Empty;

            if (!cursor.TryReadByte(out byte allAreDefined))
            {
                failure = "kCRC 的 allAreDefined 读不出来";

                return false;
            }

            if (allAreDefined != 0)
            {
                if (!cursor.TrySkip((long)count * 4))
                {
                    failure = "kCRC 的长度越界";
                    return false;
                }

                return true;
            }

            int bitmapLength = (int)((count + 7) / 8);

            if (!cursor.TryReadBytes(bitmapLength, out byte[] defined))
            {
                failure = "kCRC 的位图越界";

                return false;
            }

            long definedCount = 0;

            for (ulong index = 0; index < count; index++)
            {
                if ((defined[(int)(index / 8)] & (1 << (int)(index % 8))) != 0)
                {
                    definedCount++;
                }
            }

            if (!cursor.TrySkip(definedCount * 4))
            {
                failure = "kCRC 的定义值越界";

                return false;
            }

            return true;
        }

        /// <summary>读 <c>kUnPackInfo</c> 的 <c>kFolder(0x0B)</c> 段，把每个文件夹的 coder 链走完。</summary>
        private static bool TryReadUnPackInfo(HeaderCursor cursor, out bool hasAes, out string failure)
        {
            hasAes = false;
            failure = string.Empty;

            if (!cursor.TryReadByte(out byte propertyId) || propertyId != KFolder)
            {
                failure = "UnPackInfo 的第一个属性不是 kFolder";

                return false;
            }

            if (!cursor.TryReadNumber(out ulong folders) || folders == 0 || folders > MaxFolders)
            {
                failure = "文件夹数读不出来 / 越界";

                return false;
            }

            if (!cursor.TryReadByte(out byte external))
            {
                failure = "文件夹数据是否外置的那一字节读不出来";

                return false;
            }

            if (external != 0)
            {
                // 外置时文件夹定义在 AdditionalStreamsInfo 里 —— 这一档我们读不出来，⛔ 不猜。
                failure = "文件夹数据是外置的（External = 1）";

                return false;
            }

            ulong totalCoders = 0;

            for (ulong folder = 0; folder < folders; folder++)
            {
                if (!cursor.TryReadNumber(out ulong coders) || coders == 0 || coders > MaxCodersPerFolder)
                {
                    failure = "coder 个数读不出来 / 越界";

                    return false;
                }

                totalCoders += coders;

                if (totalCoders > MaxCodersTotal)
                {
                    failure = "coder 太多，超出一次判读的预算";

                    return false;
                }

                for (ulong coder = 0; coder < coders; coder++)
                {
                    if (!TryReadCoder(cursor, ref hasAes, out failure))
                    {
                        return false;
                    }
                }
            }

            /*
             * coder 链已经全部读到 ⇒ 判定做完了。⛔ 刻意不再往下读 kCodersUnPackSize / kCRC：
             * 那两个属性的长度依赖"每个文件夹有几条出流"，多读一步就多一处可能与格式说明理解不一致的地方。
             */
            return true;
        }

        /// <summary>读一个 coder：flags → ID → 可选入/出流个数 → 可选属性。</summary>
        private static bool TryReadCoder(HeaderCursor cursor, ref bool hasAes, out string failure)
        {
            failure = string.Empty;

            if (!cursor.TryReadByte(out byte flags))
            {
                failure = "coder 的 flags 读不出来";

                return false;
            }

            int idSize = flags & CoderIdSizeMask;

            if (idSize == 0)
            {
                failure = "coder 的 ID 长度是 0（布局不符）";

                return false;
            }

            if (!cursor.TryReadBytes(idSize, out byte[] methodId))
            {
                failure = "coder 的 ID 读不出来";

                return false;
            }

            if (IsAesMethodId(methodId))
            {
                hasAes = true;
            }

            if ((flags & CoderComplex) != 0
                && (!cursor.TryReadNumber(out _) || !cursor.TryReadNumber(out _)))
            {
                failure = "复杂 coder 的入 / 出流个数读不出来";

                return false;
            }

            if ((flags & CoderHasProperties) != 0)
            {
                if (!cursor.TryReadNumber(out ulong propertiesSize)
                    || propertiesSize > int.MaxValue
                    || !cursor.TrySkip((long)propertiesSize))
                {
                    failure = "coder 的属性读不出来 / 越界";

                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 这个 coder ID 是不是 7-Zip 的 AES-256（<c>06 F1 07 01</c>，头里按**大端**写）。
        ///
        /// <para>精确匹配 4 字节，⛔ 不做前缀 / 模糊匹配 —— "看着像 AES 就算"就是猜。</para>
        /// </summary>
        private static bool IsAesMethodId(ReadOnlySpan<byte> methodId)
        {
            if (methodId.Length != 4)
            {
                return false;
            }

            uint value = ((uint)methodId[0] << 24)
                         | ((uint)methodId[1] << 16)
                         | ((uint)methodId[2] << 8)
                         | methodId[3];

            return value == AesCoderId;
        }

        /// <summary>
        /// 跳过 <c>kArchiveProperties</c>：<c>while(有属性) { 属性类型 属性长度 属性数据 } kEnd</c>
        /// （7-Zip 的 <c>ReadHeader</c>：类型读到 kEnd(0) 就结束，别的都是"长度 + 数据"）。
        /// </summary>
        private static bool TrySkipArchiveProperties(HeaderCursor cursor, out string failure)
        {
            failure = string.Empty;

            while (cursor.TryReadNumber(out ulong propertyType))
            {
                if (propertyType == 0)
                {
                    return true;
                }

                if (!cursor.TryReadNumber(out ulong propertySize)
                    || propertySize > int.MaxValue
                    || !cursor.TrySkip((long)propertySize))
                {
                    failure = "属性长度读不出来 / 越界";

                    return false;
                }
            }

            failure = "ArchiveProperties 读到缓冲区末尾（头被截断）";

            return false;
        }

        private static ArchiveEncryptionReading Unknown(int bytesRead, string basis) => new()
        {
            State = ArchiveEncryptionState.Unknown,
            Basis = basis,
            BytesRead = bytesRead
        };

        private static ArchiveEncryptionReading Plain(int bytesRead, string basis) => new()
        {
            State = ArchiveEncryptionState.NotEncrypted,
            Basis = basis,
            BytesRead = bytesRead
        };

        private static ArchiveEncryptionReading Encrypted(int bytesRead, ArchiveEncryptionState state, string basis) => new()
        {
            State = state,
            Basis = basis,
            BytesRead = bytesRead
        };

        /// <summary>卷组里的一段：<paramref name="Path"/> 有 <paramref name="Length"/> 个字节。</summary>
        private readonly struct Segment
        {
            public Segment(string path, long length)
            {
                Path = path;
                Length = length;
            }

            public string Path { get; }

            public long Length { get; }
        }

        /// <summary>
        /// 在一个**已经读进内存的** next header 上游走的游标（⛔ 绝不越界读，走不动就是"读不出来"）。
        /// </summary>
        private sealed class HeaderCursor
        {
            private readonly byte[] _data;

            public HeaderCursor(byte[] data)
            {
                _data = data;
            }

            private int Position { get; set; }

            public bool TryReadByte(out byte value)
            {
                value = 0;

                if (Position >= _data.Length)
                {
                    return false;
                }

                value = _data[Position];
                Position++;

                return true;
            }

            public bool TryReadBytes(int count, out byte[] data)
            {
                data = Array.Empty<byte>();

                if (count < 0 || Position + count > _data.Length)
                {
                    return false;
                }

                data = new byte[count];
                Array.Copy(_data, Position, data, 0, count);
                Position += count;

                return true;
            }

            /// <summary>
            /// 7z 的变长数字：首字节里**高位连续的 1 的个数** = 后面还有几个字节（低位在前，
            /// 剩下的那几位是最高位部分）。⚠ 它与 RAR5 那种"每字节 7 位一组"的 vint **不是一回事**，
            /// 抄错一个就整份头都读歪。
            /// </summary>
            public bool TryReadNumber(out ulong value)
            {
                value = 0;

                if (!TryReadByte(out byte first))
                {
                    return false;
                }

                byte mask = 0x80;

                for (int index = 0; index < 8; index++)
                {
                    if ((first & mask) == 0)
                    {
                        value |= (ulong)(first & (byte)(mask - 1)) << (index * 8);

                        return true;
                    }

                    if (!TryReadByte(out byte next))
                    {
                        return false;
                    }

                    value |= (ulong)next << (index * 8);
                    mask >>= 1;
                }

                // 8 个高位全是 1：7-Zip 自己不会产出这种数字 ⇒ 不是我们认识的形状。
                return false;
            }

            public bool TrySkip(long count)
            {
                if (count < 0 || count > _data.Length - Position)
                {
                    return false;
                }

                Position += (int)count;

                return true;
            }
        }
    }
}

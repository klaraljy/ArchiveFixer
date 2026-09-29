using System;
using System.Buffers.Binary;
using System.IO;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// **只读头部的 RAR 加密判读**（用户 2026-09-29 任务 A：识别阶段就要看得出 RAR 加密，
    /// ⛔ 不额外调引擎、不引入依赖、不把大文件读进来）。
    ///
    /// <para><b>为什么要有它</b>：识别阶段过去只看开头那几个魔数字节，RAR 的块结构一个都不解析，
    /// 于是 <c>-p</c>（只加密数据、文件名可见）这一档看不出加密，<c>-hp</c>（连文件名加密）更是什么都读不到。
    /// 后果写在 AGENTS.md §11：批首那句"本批有 N 个包没有可用密码"的预判对 RAR **整档漏报**。</para>
    ///
    /// <para><b>判据出处（⛔ 值不许凭记忆写，下面是逐条的来源）</b>：</para>
    /// <list type="number">
    /// <item><description><b>RAR5</b>（签名 <c>52 61 72 21 1A 07 01 00</c>）：官方格式说明
    /// <see href="https://www.rarlab.com/technote.htm"/>（RAR 5.0 archive format）——
    /// ① 头类型 <b>4</b> = <i>Archive encryption header</i>，原文明写 "This header is present only in
    /// archives with encrypted headers"，所以"签名之后第一个头是 4"= **头加密**；
    /// ② 文件/服务头（类型 2 / 3）的**可选扩展区**里，记录类型 <b>0x01</b> = <i>File encryption</i>，
    /// 原文 "This record is present if file data is encrypted" = **数据加密**；
    /// ③ ⚠ 主归档头（类型 1）的 <c>Archive flags</c> 里 <b>0x0004 是 Solid（固实归档），不是"有密码"</b>
    /// —— 官方那五条是 0x0001 Volume / 0x0002 Volume number present / 0x0004 Solid /
    /// 0x0008 Recovery record present / 0x0010 Locked。任务书里"archiveFlags 的 0x0004 有密码"这一句
    /// **与官方说明不符**，本实现按官方说明走（⛔ 拿 Solid 当密码会把一大批普通固实包误报成加密）。</description></item>
    /// <item><description><b>RAR 1.5–4.x</b>（WinRAR 里叫 <b>RAR4</b>，签名 <c>52 61 72 21 1A 07 00</c>；
    /// RAR3 与 RAR4 共用同一签名，所以这里一律不写"这是 RAR3"）：flags 常量取
    /// <see href="https://raw.githubusercontent.com/edmund-wagner/junrar/refs/heads/master/unrar/src/main/java/com/github/junrar/rarfile/BaseBlock.java">unrar 的移植版 junrar 的 BaseBlock</see>
    /// （RAR5 的官方说明本身就写着"更细的数据结构请用 UnRAR 源码"）——
    /// 主头（type <c>0x73</c>）的 <c>MHD_PASSWORD = 0x0080</c>：置位表示**归档头被加密**，
    /// 该头之后的块全是密文（= <c>-hp</c>，"连文件名都加密"）；
    /// 文件头 / 服务头（type <c>0x74</c> / <c>0x7A</c>）的 <c>LHD_PASSWORD = 0x0004</c>：
    /// 置位表示**这个条目的数据被加密**（= <c>-p</c>，文件名仍然可见）。
    /// 两条判据都用真样本核过（本机 WinRAR 的 Rar.exe 现造，见 <c>RarEncryptionReaderTests</c>）。</description></item>
    /// </list>
    ///
    /// <para><b>三条纪律</b>：</para>
    /// <list type="bullet">
    /// <item><description><b>只读头</b>：整个判读过程**读进内存的字节数 ≤ <see cref="MaxHeaderBytes"/>（64 KiB）**，
    /// 数据区一律用 seek 跳过去（绝不把包体读进来）；</description></item>
    /// <item><description><b>绝不猜</b>：头截断 / 块尺寸越界 / 头部 CRC 对不上 / 布局与官方说明不符
    /// → 一律 <see cref="ArchiveEncryptionState.Unknown"/>（宁可漏报，也不误报）；</description></item>
    /// <item><description><b>不调引擎、不引依赖</b>：纯字节解析，用的 CRC32 与 vint 读法就是仓库里
    /// 既有的那一份（<see cref="VolumeNumberFromContent"/>，⛔ 不另写一份必然漂移的表）。</description></item>
    /// </list>
    /// </summary>
    public static class RarEncryptionReader
    {
        /// <summary>
        /// **单文件读入量硬上限**（64 KiB）：整个判读过程从文件里读进内存的字节数不许超过它。
        /// 真实包的头链远小于它（一个文件头百来字节），一旦超过就说明"这不是我们认识的形状"，
        /// 按"不知道"返回 —— 宁可漏报也不猜。
        /// </summary>
        public const int MaxHeaderBytes = 64 * 1024;

        /// <summary>最多走多少个头（防止一个畸形文件把预算耗在无穷无尽的块上）。</summary>
        public const int MaxHeaders = 512;

        /// <summary>RAR 1.5–4.x 主头 TYPE（<c>MHD_HEAD</c>）。</summary>
        private const byte Rar4MainHeaderType = 0x73;

        /// <summary>RAR 1.5–4.x 文件头 TYPE（<c>LHD_HEAD</c>）。</summary>
        private const byte Rar4FileHeaderType = 0x74;

        /// <summary>RAR 1.5–4.x 服务头 TYPE（<c>SHC_HEAD</c>；结构与文件头相同，同样能带 LHD_PASSWORD）。</summary>
        private const byte Rar4ServiceHeaderType = 0x7A;

        /// <summary>RAR 1.5–4.x 归档结尾块 TYPE（<c>ENDARC_HEAD</c>）。</summary>
        private const byte Rar4EndBlockType = 0x7B;

        /// <summary>RAR 1.5–4.x 主头 flags：<c>MHD_PASSWORD</c> = 归档头被加密（整个 <c>-hp</c> 档）。</summary>
        private const ushort Rar4MainPassword = 0x0080;

        /// <summary>RAR 1.5–4.x 文件/服务头 flags：<c>LHD_PASSWORD</c> = 该条目数据被加密（<c>-p</c>）。</summary>
        private const ushort Rar4FilePassword = 0x0004;

        /// <summary>RAR 1.5–4.x 文件头 flags：<c>LHD_LARGE</c> = 头尾还有高 32 位尺寸字段。</summary>
        private const ushort Rar4FileLarge = 0x0100;

        /// <summary>RAR 1.5–4.x 通用 flags：<c>LONG_BLOCK</c> = 头后面跟着数据区（<c>ADD_SIZE</c>）。</summary>
        private const ushort Rar4LongBlock = 0x8000;

        /// <summary>RAR 1.5–4.x 块头固定长度：HEAD_CRC(2) + HEAD_TYPE(1) + HEAD_FLAGS(2) + HEAD_SIZE(2)。</summary>
        private const int Rar4BaseHeaderSize = 7;

        /// <summary>RAR5 主归档头 TYPE。</summary>
        private const ulong Rar5MainHeaderType = 1;

        /// <summary>RAR5 文件头 TYPE。</summary>
        private const ulong Rar5FileHeaderType = 2;

        /// <summary>RAR5 服务头 TYPE（与文件头同构，扩展区记录类型也共用）。</summary>
        private const ulong Rar5ServiceHeaderType = 3;

        /// <summary>RAR5 归档加密头 TYPE —— 官方说明："只出现在头加密的归档里"。</summary>
        private const ulong Rar5ArchiveEncryptionHeaderType = 4;

        /// <summary>RAR5 归档结尾头 TYPE。</summary>
        private const ulong Rar5EndOfArchiveHeaderType = 5;

        /// <summary>RAR5 通用头 flags：头尾带可选扩展区（扩展区大小字段存在）。</summary>
        private const ulong Rar5HeaderExtraArea = 0x0001;

        /// <summary>RAR5 通用头 flags：头后带数据区（数据区大小字段存在）。</summary>
        private const ulong Rar5HeaderDataArea = 0x0002;

        /// <summary>RAR5 文件/服务头扩展区记录类型：<c>File encryption</c>（"数据加密时才有这条"）。</summary>
        private const ulong Rar5FileEncryptionRecord = 0x01;

        /// <summary>RAR5 头大小上限（格式自己规定"Header size 最多 3 字节 vint"，即 2 MB）。</summary>
        private const int Rar5MaxHeaderSize = 2 * 1024 * 1024;

        /// <summary>
        /// 判读一个文件（或内嵌在别的文件里的、从 <paramref name="offset"/> 开始的一段 RAR）有没有加密。
        ///
        /// <para>任何 IO 意外（不存在 / 被占用 / 权限 / 半路被删）都落成
        /// <see cref="ArchiveEncryptionState.Unknown"/>，**绝不抛** —— 识别阶段不许因为它失败。</para>
        /// </summary>
        public static ArchiveEncryptionReading Read(string? filePath, long offset = 0)
        {
            if (string.IsNullOrWhiteSpace(filePath) || offset < 0)
            {
                return NoPath();
            }

            try
            {
                using var reader = new BoundedReader(filePath, offset, MaxHeaderBytes);

                /*
                 * ⚠ 签名**必须按各自的长度读**：RAR4 是 7 字节、RAR5 是 8 字节。
                 * 一上来就读 8 字节会把 RAR4 主头的第一个字节（HEAD_CRC 低字节）吃掉，
                 * 于是"块头 CRC 对不上"→ 一律返回"不知道"（实测踩过：RAR5 三条全绿、RAR4 三条全红）。
                 */
                if (!reader.TryRead(7, out byte[] signature))
                {
                    return reader.Unknown("头都读不出来（空文件 / 被截断）");
                }

                if (!IsRarSignature(signature, out bool isRar5))
                {
                    return reader.Unknown("这一段的开头不是 RAR 签名");
                }

                // RAR5 多出来的第 8 个字节（必须是 0x00）也要读掉：头链从它后面开始。
                if (isRar5 && (!reader.TryRead(1, out byte[] eighth) || eighth[0] != 0x00))
                {
                    return reader.Unknown("RAR5 签名不完整（第 8 字节不是 0x00）");
                }

                return isRar5 ? ReadRar5(reader) : ReadRar4(reader);
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

        /// <summary>开头 7 个字节是不是 RAR 签名（RAR5 还要看第 8 个字节：0x00）。</summary>
        private static bool IsRarSignature(ReadOnlySpan<byte> head, out bool isRar5)
        {
            isRar5 = false;

            if (head.Length >= 7
                && head[0] == (byte)'R' && head[1] == (byte)'a' && head[2] == (byte)'r'
                && head[3] == (byte)'!' && head[4] == 0x1A && head[5] == 0x07)
            {
                // 第 7 字节：0x01 0x00 = RAR5；0x00 = RAR 1.5–4.x（同一个签名，见类注释）。
                isRar5 = head[6] == 0x01;

                return head[6] == 0x00 || isRar5;
            }

            return false;
        }

        /// <summary>
        /// RAR 1.5–4.x：主头看 <c>MHD_PASSWORD</c>（头加密），文件 / 服务头看 <c>LHD_PASSWORD</c>（数据加密）。
        ///
        /// <para>块之间靠 <c>HEAD_SIZE</c> + 数据区长度（<c>LONG_BLOCK</c> 时头尾的 PACK_SIZE）跳过去，
        /// 所以**包体一个字节都不读**。</para>
        /// </summary>
        private static ArchiveEncryptionReading ReadRar4(BoundedReader reader)
        {
            for (int index = 0; index < MaxHeaders; index++)
            {
                long blockStart = reader.Position;

                if (!reader.TryRead(Rar4BaseHeaderSize, out byte[] baseHeader))
                {
                    return reader.Unknown("块头读不到（文件到此为止）");
                }

                ushort storedCrc = BinaryPrimitives.ReadUInt16LittleEndian(baseHeader.AsSpan(0, 2));
                byte headerType = baseHeader[2];
                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(baseHeader.AsSpan(3, 2));
                ushort headerSize = BinaryPrimitives.ReadUInt16LittleEndian(baseHeader.AsSpan(5, 2));

                if (headerSize < Rar4BaseHeaderSize)
                {
                    return reader.Unknown("HEAD_SIZE 小于块头长度（布局不符）");
                }

                if (!reader.TryRead(headerSize - Rar4BaseHeaderSize, out byte[] body))
                {
                    return reader.Unknown("块体读不到（截断 / 超出读取预算）");
                }

                /*
                 * 头部 CRC：官方口径是 [HEAD_TYPE, 块末尾) 的 CRC32 低 16 位。
                 * 取不取补两种约定都收 —— 与 VolumeNumberFromContent 的既有写法同一口径
                 * （那一处是按真样本核过的），⛔ 别在这里收得比它更松或者更紧。
                 */
                byte[] block = new byte[headerSize];
                Array.Copy(baseHeader, block, Rar4BaseHeaderSize);
                Array.Copy(body, 0, block, Rar4BaseHeaderSize, body.Length);

                if (!VolumeNumberFromContent.CrcMatches16(storedCrc, block.AsSpan(2)))
                {
                    return reader.Unknown("块头 CRC 对不上（不是我们认识的块）");
                }

                if (headerType == Rar4MainHeaderType)
                {
                    if ((flags & Rar4MainPassword) != 0)
                    {
                        return reader.Encrypted(
                            ArchiveEncryptionState.HeadersEncrypted,
                            "RAR4 主头 MHD_PASSWORD(0x0080) 置位：该头之后的块都是密文");
                    }
                }
                else if (headerType == Rar4FileHeaderType || headerType == Rar4ServiceHeaderType)
                {
                    if ((flags & Rar4FilePassword) != 0)
                    {
                        /*
                         * 只有数据加密：主头是明文（否则上面那条已经返回了），文件名照样看得见。
                         * 这里**立刻返回**是安全的：主头永远是第一个块，头加密那一档不可能在后面才出现。
                         */
                        return reader.Encrypted(
                            ArchiveEncryptionState.DataEncrypted,
                            "RAR4 文件/服务头 LHD_PASSWORD(0x0004) 置位：该条目数据加密");
                    }
                }
                else if (headerType == Rar4EndBlockType)
                {
                    // 走到归档结尾块 = 整条头链看完了，且一路没有任何加密标志。
                    return reader.Plain("读到了 RAR4 归档结尾块（0x7B），头链上没有加密标志");
                }

                long dataSize = Rar4DataSize(body, flags, headerType);

                if (dataSize < 0)
                {
                    return reader.Unknown("数据区长度字段读不出来（布局不符）");
                }

                if (!reader.TrySkip(dataSize))
                {
                    return reader.Unknown("数据区长度越界（布局不符）");
                }

                if (reader.Position <= blockStart)
                {
                    return reader.Unknown("块没有前进（布局不符）");
                }
            }

            return reader.Unknown("头太多，超出一次判读的预算（" + MaxHeaders + " 个块）");
        }

        /// <summary>
        /// RAR 1.5–4.x 一个块后面的数据区长度（= 下一块的起点要往前跳多少）。
        ///
        /// <para>文件 / 服务头的 <c>PACK_SIZE</c> 在块体开头 4 字节，带 <c>LHD_LARGE</c> 时头尾还有 4 字节高位
        /// （布局：PACK_SIZE(4) UNP_SIZE(4) HOST_OS(1) FILE_CRC(4) FTIME(4) UNP_VER(1) METHOD(1)
        /// NAME_SIZE(2) ATTR(4) → 25 字节之后是两个高 32 位）；其它带 <c>LONG_BLOCK</c> 的块
        /// <c>ADD_SIZE</c> 就是块体开头 4 字节。读不出来（长度不够 / 溢出）返回 −1。</para>
        /// </summary>
        private static long Rar4DataSize(byte[] body, ushort flags, byte headerType)
        {
            bool isFileLike = headerType == Rar4FileHeaderType || headerType == Rar4ServiceHeaderType;

            if (!isFileLike && (flags & Rar4LongBlock) == 0)
            {
                return 0;
            }

            if (body.Length < 4)
            {
                return -1;
            }

            long low = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0, 4));

            if (!isFileLike || (flags & Rar4FileLarge) == 0)
            {
                return low;
            }

            const int highFieldOffset = 25;

            if (body.Length < highFieldOffset + 4)
            {
                return -1;
            }

            long high = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(highFieldOffset, 4));

            // 高 32 位一旦超出 int 范围，拼出来的偏移必然是畸形的 —— 如实说"读不出来"，不猜。
            return high > int.MaxValue ? -1 : (high << 32) | low;
        }

        /// <summary>
        /// RAR5：签名之后第一个头是 <b>类型 4</b>（归档加密头）= 头加密；文件 / 服务头的**扩展区**里
        /// 出现记录类型 <b>0x01</b>（File encryption）= 数据加密。
        ///
        /// <para>⚠ 主归档头（类型 1）的 <c>Archive flags</c> 里**没有**"有密码"这一位
        /// （0x0004 是 Solid，官方说明那五条见类注释），所以这里一个字都不读它 ——
        /// 拿 Solid 当密码会把一大批普通固实包误报成"加密"。</para>
        /// </summary>
        private static ArchiveEncryptionReading ReadRar5(BoundedReader reader)
        {
            for (int index = 0; index < MaxHeaders; index++)
            {
                long headerStart = reader.Position;

                if (!reader.TryRead(4, out byte[] crcBytes))
                {
                    return reader.Unknown("头 CRC 字段读不到（文件到此为止）");
                }

                uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcBytes);

                if (!reader.TryReadVint(out byte[] sizeBytes, out ulong headerSize))
                {
                    return reader.Unknown("Header size 读不出来（布局不符）");
                }

                if (headerSize == 0 || headerSize > Rar5MaxHeaderSize)
                {
                    return reader.Unknown("Header size 越界（布局不符）");
                }

                if (!reader.TryRead((int)headerSize, out byte[] body))
                {
                    return reader.Unknown("块体读不到（截断 / 超出读取预算）");
                }

                // CRC32 覆盖 [Header size 字段, 头末尾]（官方说明），所以要把它自己那几个字节也算进去。
                byte[] crcRange = new byte[sizeBytes.Length + body.Length];
                Array.Copy(sizeBytes, crcRange, sizeBytes.Length);
                Array.Copy(body, 0, crcRange, sizeBytes.Length, body.Length);

                if (!VolumeNumberFromContent.CrcMatches(storedCrc, crcRange))
                {
                    return reader.Unknown("头 CRC 对不上（不是我们认识的块）");
                }

                int cursor = 0;

                if (!VolumeNumberFromContent.TryReadVint(body, ref cursor, out ulong headerType)
                    || !VolumeNumberFromContent.TryReadVint(body, ref cursor, out ulong headerFlags))
                {
                    return reader.Unknown("头类型 / 头标志读不出来（布局不符）");
                }

                ulong extraAreaSize = 0;

                if ((headerFlags & Rar5HeaderExtraArea) != 0
                    && !VolumeNumberFromContent.TryReadVint(body, ref cursor, out extraAreaSize))
                {
                    return reader.Unknown("扩展区大小读不出来（布局不符）");
                }

                ulong dataAreaSize = 0;

                if ((headerFlags & Rar5HeaderDataArea) != 0
                    && !VolumeNumberFromContent.TryReadVint(body, ref cursor, out dataAreaSize))
                {
                    return reader.Unknown("数据区大小读不出来（布局不符）");
                }

                if (headerType == Rar5ArchiveEncryptionHeaderType)
                {
                    return reader.Encrypted(
                        ArchiveEncryptionState.HeadersEncrypted,
                        "RAR5 第一个头是归档加密头（类型 4）：官方说明里它只出现在头加密的归档中");
                }

                if (headerType == Rar5EndOfArchiveHeaderType)
                {
                    return reader.Plain("读到了 RAR5 归档结尾头（类型 5），头链上没有加密标志");
                }

                if ((headerType == Rar5FileHeaderType || headerType == Rar5ServiceHeaderType)
                    && HasFileEncryptionRecord(body, extraAreaSize))
                {
                    return reader.Encrypted(
                        ArchiveEncryptionState.DataEncrypted,
                        "RAR5 文件/服务头的扩展区里有 File encryption 记录（类型 0x01）：该条目数据加密");
                }

                if (!reader.TrySkipTo(headerStart + 4 + sizeBytes.Length + (long)headerSize + (long)dataAreaSize))
                {
                    return reader.Unknown("下一个头的偏移越界（布局不符）");
                }
            }

            return reader.Unknown("头太多，超出一次判读的预算（" + MaxHeaders + " 个块）");
        }

        /// <summary>
        /// 文件 / 服务头的**扩展区**里有没有"数据加密"那条记录（类型 <c>0x01</c>）。
        ///
        /// <para>扩展区按官方布局排在**头的末尾**（头 = 定长字段 + 扩展区），长度由头标志 0x0001 给出，
        /// 所以起点 = <c>头长 − 扩展区大小</c> —— 定长字段一个都不用解析，布局变化也打不着这里。
        /// 记录之间靠记录自己的 size vint（"从 Type 字段起算"）串起来。</para>
        ///
        /// <para>扩展区越界 / 记录尺寸自相矛盾 → 返回 false（= 没看到那条记录）。
        /// 这一档**不影响"不报加密"的安全性**：我们绝不因为"读不懂扩展区"就去报加密；
        /// 反过来，真正加密的包里那条记录就在扩展区，读不懂时最坏也只是漏报（可接受）。</para>
        /// </summary>
        private static bool HasFileEncryptionRecord(byte[] body, ulong extraAreaSize)
        {
            if (extraAreaSize == 0 || extraAreaSize > (ulong)body.Length)
            {
                return false;
            }

            int start = body.Length - (int)extraAreaSize;
            int cursor = start;

            while (cursor < body.Length)
            {
                int recordStart = cursor;

                if (!VolumeNumberFromContent.TryReadVint(body, ref cursor, out ulong recordSize)
                    || recordSize == 0
                    || !VolumeNumberFromContent.TryReadVint(body, ref cursor, out ulong recordType))
                {
                    return false;
                }

                // "Size of record data starting from Type" —— 从 Type 字段起算，所以整条记录 = size 字节。
                long recordEnd = recordStart + (long)recordSize;

                if (recordEnd > body.Length || recordEnd <= cursor)
                {
                    return false;
                }

                if (recordType == Rar5FileEncryptionRecord)
                {
                    return true;
                }

                cursor = (int)recordEnd;
            }

            return false;
        }

        private static ArchiveEncryptionReading Unknown(string path, string basis) => new()
        {
            State = ArchiveEncryptionState.Unknown,
            Basis = basis
        };

        /// <summary>
        /// 读文件头的**有预算游标**：读进内存的字节数**记着账**，超过预算 / 越界一律失败。
        ///
        /// <para>数据区不用读，<see cref="TrySkip"/> 直接 seek 过去 —— 所以一个 40 GB 的包与一个 40 KB 的包
        /// 在这里的代价是一样的（都是几十字节的头）。</para>
        /// </summary>
        private sealed class BoundedReader : IDisposable
        {
            private readonly FileStream _stream;

            private int _readBudget;
            private int _bytesRead;
            private long _length;

            public BoundedReader(string path, long start, int budget)
            {
                _stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                _length = _stream.Length;

                long position = Math.Min(Math.Max(start, 0), _length);

                _stream.Seek(position, SeekOrigin.Begin);

                Position = position;
                _readBudget = budget;
            }

            public long Position { get; private set; }

            /// <summary>读 <paramref name="count"/> 个字节（读不满就算失败）。</summary>
            public bool TryRead(int count, out byte[] data)
            {
                data = Array.Empty<byte>();

                if (count < 0 || count > _readBudget || Position + count > _length)
                {
                    return false;
                }

                var buffer = new byte[count];
                int total = 0;

                while (total < count)
                {
                    int read = _stream.Read(buffer, total, count - total);

                    if (read <= 0)
                    {
                        return false;
                    }

                    total += read;
                }

                _readBudget -= count;
                _bytesRead += count;
                Position += count;
                data = buffer;

                return true;
            }

            /// <summary>读一个 RAR5 的 vint（同时把它的原始字节交出去 —— 头 CRC 要把这几个字节算进去）。</summary>
            public bool TryReadVint(out byte[] rawBytes, out ulong value)
            {
                rawBytes = Array.Empty<byte>();
                value = 0;

                var collected = new byte[10];

                for (int index = 0; index < collected.Length; index++)
                {
                    if (!TryRead(1, out byte[] one))
                    {
                        return false;
                    }

                    collected[index] = one[0];
                    value |= (ulong)(one[0] & 0x7F) << (index * 7);

                    if ((one[0] & 0x80) == 0)
                    {
                        rawBytes = collected[..(index + 1)];
                        return true;
                    }
                }

                return false;
            }

            /// <summary>跳过 <paramref name="count"/> 个字节（不读、不占预算；越界算失败）。</summary>
            public bool TrySkip(long count)
            {
                return TrySkipTo(Position + count);
            }

            /// <summary>跳到绝对偏移（只许往前走；越界 / 倒着走算失败）。</summary>
            public bool TrySkipTo(long target)
            {
                if (target < Position || target > _length)
                {
                    return false;
                }

                _stream.Seek(target, SeekOrigin.Begin);
                Position = target;

                return true;
            }

            public ArchiveEncryptionReading Unknown(string basis) => new()
            {
                State = ArchiveEncryptionState.Unknown,
                Basis = basis,
                BytesRead = _bytesRead
            };

            public ArchiveEncryptionReading Plain(string basis) => new()
            {
                State = ArchiveEncryptionState.NotEncrypted,
                Basis = basis,
                BytesRead = _bytesRead
            };

            public ArchiveEncryptionReading Encrypted(ArchiveEncryptionState state, string basis) => new()
            {
                State = state,
                Basis = basis,
                BytesRead = _bytesRead
            };

            public void Dispose() => _stream.Dispose();
        }
    }
}

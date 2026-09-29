using System;
using System.Buffers.Binary;
using System.IO;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// **只读头尾的 ZIP 加密判读**（用户 2026-09-30 任务：识别阶段看得出 ZIP 加没加密，
    /// ⛔ 不额外调引擎、不引入依赖、不把中央目录整个读进来）。
    ///
    /// <para><b>为什么要有它（这是一条真缺陷）</b>：旧实现（<c>ArchiveDetectService.IsZipProbablyEncrypted</c>）
    /// 只读**第一个本地头** offset 6-7 的通用位标志 bit0。实测 <c>dirfirst.zip</c> /
    /// <c>dirfirst2.zip</c>（<c>7z a -tzip -p&lt;pw&gt; dirfirst.zip sub</c>）的第一个本地头是**目录条目** <c>sub/</c>、
    /// flags = <c>0x0000</c> ⇒ 判成"没加密"；而中央目录两条记录的 flags 是 <c>0x0000 0x0001</c> ——
    /// 它就是加密包。也就是说"第一个条目不是加密的"被当成了"整包没加密"，
    /// 表现是**漏报**（批首那句"本批有 N 个包没有可用密码"少算一个）。</para>
    ///
    /// <para><b>判据（ZIP 的字段偏移，逐条核过真样本）</b>：</para>
    /// <list type="number">
    /// <item><description><b>EOCD</b> <c>PK\x05\x06</c> 在文件尾（22 字节 + 注释长度）：条目总数 +10(LE16)、
    /// 中央目录**大小** +12(LE32)、中央目录**偏移** +16(LE32)、注释长度 +20(LE16)，偏移都**相对 zip 起点**；</description></item>
    /// <item><description><b>中央目录记录</b> <c>PK\x01\x02</c>：通用位标志在记录内 **+8**（LE16，bit0 = 加密），
    /// 记录长度 = 46 + 名字(+28) + 扩展区(+30) + 注释(+32)；</description></item>
    /// <item><description><b>ZIP64</b>：EOCD 前面紧贴 <c>PK\x06\x07</c> 定位器（20 字节，zip64 EOCD 偏移在 +8 LE64），
    /// 定位器指的 <c>PK\x06\x06</c> 记录里中央目录大小在 +40、偏移在 +48（均 LE64）；</description></item>
    /// <item><description><b>本地头</b> <c>PK\x03\x04</c>：通用位标志在 **+6**（LE16，bit0 = 加密）。
    /// ⚠ 跨盘 zip 的**第 1 卷**以 <c>PK\x07\x08</c> + 4 字节盘号开头，本地头在 **+4** 处 ——
    /// 旧实现把盘号那 4 字节当 flags 读（于是"看着像没加密"），这里只在 <c>+4</c> 处**确实是** <c>PK\x03\x04</c>
    /// 时才认，认不出就"不知道"。</description></item>
    /// </list>
    ///
    /// <para><b>三档结论</b>：中央目录里**见到第一个 bit0 就停** ⇒ <see cref="ArchiveEncryptionState.DataEncrypted"/>；
    /// 逐条扫完整个中央目录都没有 ⇒ <see cref="ArchiveEncryptionState.NotEncrypted"/>；
    /// 其余（EOCD / 中央目录读不出、截断、跨盘首卷、超出扫描上限）⇒ 退到"读第一个本地头的 flags"这一档 ——
    /// 那一档**只可能给出"加密"或"不知道"**（⛔ 一个本地头说不出别的条目加没加密，见
    /// <see cref="ReadFirstLocalHeaderFlags"/>）。</para>
    /// </summary>
    public static class ZipEncryptionReader
    {
        /// <summary>中央目录一次最多扫多少字节（⛔ 不把整个中央目录读进内存；超了就"不知道"）。</summary>
        public const int MaxCentralDirectoryBytes = 1024 * 1024;

        /// <summary>EOCD 检索窗口：EOCD(22) + 注释长度的上限(65535)。</summary>
        public const int EocdSearchWindow = 22 + 65535;

        /// <summary>本地头的探针长度（签名 4 + 版本 2 + flags 2 —— 再往后就读不到了）。</summary>
        public const int LocalHeaderProbeLength = 8;

        private const int EocdLength = 22;
        private const int Zip64LocatorLength = 20;
        private const int Zip64EocdLength = 56;
        private const int CentralHeaderFixedLength = 46;

        /// <summary>通用位标志：bit0 = 这个条目的数据被加密（ZipCrypto 与 AES 都置这一位）。</summary>
        private const ushort EncryptedFlag = 0x0001;

        private static readonly byte[] LocalHeaderSignature = { 0x50, 0x4B, 0x03, 0x04 };
        private static readonly byte[] SpannedMarkerSignature = { 0x50, 0x4B, 0x07, 0x08 };
        private static readonly byte[] CentralHeaderSignature = { 0x50, 0x4B, 0x01, 0x02 };
        private static readonly byte[] DigitalSignatureSignature = { 0x50, 0x4B, 0x05, 0x05 };
        private static readonly byte[] EocdSignature = { 0x50, 0x4B, 0x05, 0x06 };
        private static readonly byte[] Zip64EocdSignature = { 0x50, 0x4B, 0x06, 0x06 };
        private static readonly byte[] Zip64LocatorSignature = { 0x50, 0x4B, 0x06, 0x07 };

        /// <summary>
        /// 判读一个 ZIP（或内嵌在别的文件里、从 <paramref name="startOffset"/> 开始的那一个 ZIP）
        /// 有没有加密。
        ///
        /// <para>任何 IO 意外（不存在 / 被占用 / 权限 / 半路被删）都落成
        /// <see cref="ArchiveEncryptionState.Unknown"/>，**绝不抛** —— 识别阶段不许因为它失败。</para>
        /// </summary>
        public static ArchiveEncryptionReading Read(string? filePath, long startOffset = 0)
        {
            if (string.IsNullOrWhiteSpace(filePath) || startOffset < 0)
            {
                return NoPath();
            }

            try
            {
                using var stream = new FileStream(
                    filePath!,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                long length = stream.Length;
                int bytesRead = 0;

                if (TryFindCentralDirectory(stream, startOffset, length, out long cdStart, out long cdSize, ref bytesRead))
                {
                    CentralDirectoryScan scan = ScanCentralDirectory(stream, cdStart, cdSize, ref bytesRead);

                    if (scan == CentralDirectoryScan.Encrypted)
                    {
                        return Encrypted(
                            bytesRead,
                            ArchiveEncryptionState.DataEncrypted,
                            "中央目录里有通用位标志 bit0 置位的条目（+8 处 LE16 的 bit0）");
                    }

                    if (scan == CentralDirectoryScan.Clean)
                    {
                        return Plain(bytesRead, "中央目录逐条扫完，没有任何条目的通用位标志 bit0 置位");
                    }
                }

                /*
                 * 走到这一档说明"中央目录说不出结论"：EOCD 不在（截断 / 跨盘 zip 的第 1 卷 ——
                 * 跨盘时 EOCD 在**最后一片**上）、中央目录越界、或者扫到了 1 MiB 上限。
                 * 唯一还能看的是**第一个本地头**。
                 */
                return ReadFirstLocalHeaderFlags(stream, startOffset, length, ref bytesRead);
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
        /// 从文件尾往前找 EOCD（窗口 ≤ 64 KiB + EOCD 本身），再到 ZIP64 记录，最后定位中央目录。
        ///
        /// <para>候选逐个往前试而不是"只看最后一个"：EOCD 是 4 字节签名，它可能出现在注释里；
        /// 判据是"EOCD 完整落在文件内 + 注释长度不把结尾顶出文件 + 中央目录能定位"。
        /// 与 <c>Detection/EmbeddedArchiveDetector</c> 那条自洽口径一致（那一处回答的是"内嵌归档的边界"，
        /// 这一处回答"加没加密" —— 两个问题，不共用一份实现）。</para>
        /// </summary>
        private static bool TryFindCentralDirectory(
            FileStream stream,
            long startOffset,
            long length,
            out long cdStart,
            out long cdSize,
            ref int bytesRead)
        {
            cdStart = 0;
            cdSize = 0;

            if (length <= startOffset || length - startOffset < EocdLength)
            {
                return false;
            }

            long windowLength = Math.Min(length - startOffset, (long)EocdSearchWindow);
            long windowStart = length - windowLength;

            byte[]? window = ReadAt(stream, windowStart, (int)windowLength, ref bytesRead);

            if (window == null)
            {
                return false;
            }

            int index = LastIndexOf(window, EocdSignature, window.Length - EocdSignature.Length);

            while (index >= 0)
            {
                if (TryResolveCentralDirectory(
                        stream,
                        startOffset,
                        length,
                        window,
                        windowStart,
                        index,
                        out cdStart,
                        out cdSize,
                        ref bytesRead))
                {
                    return true;
                }

                index = LastIndexOf(window, EocdSignature, index - 1);
            }

            return false;
        }

        /// <summary>校验一个 EOCD 候选，并把它（或 ZIP64 记录）里的中央目录位置算出来。</summary>
        private static bool TryResolveCentralDirectory(
            FileStream stream,
            long startOffset,
            long length,
            byte[] window,
            long windowStart,
            int index,
            out long cdStart,
            out long cdSize,
            ref int bytesRead)
        {
            cdStart = 0;
            cdSize = 0;

            if (index + EocdLength > window.Length)
            {
                return false;
            }

            long eocdAbsolute = windowStart + index;

            if (eocdAbsolute < startOffset)
            {
                return false;
            }

            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(window.AsSpan(index + 20, 2));

            // 注释长度是从文件里读出来的，一个坏值就能把归档结尾顶到文件外面去 —— 直接不要这个候选。
            if (eocdAbsolute + EocdLength + commentLength > length)
            {
                return false;
            }

            uint size32 = BinaryPrimitives.ReadUInt32LittleEndian(window.AsSpan(index + 12, 4));
            uint offset32 = BinaryPrimitives.ReadUInt32LittleEndian(window.AsSpan(index + 16, 4));
            ushort entries16 = BinaryPrimitives.ReadUInt16LittleEndian(window.AsSpan(index + 10, 2));

            long size = size32;
            long offset = offset32;

            /*
             * ZIP64：三个字段里任何一个取了"哨兵值"就得去读 ZIP64 记录。
             * 位置是**紧贴 EOCD 之前**的 20 字节 locator（规范的排列），
             * 不按"附近有 PK 06 06 就算"猜。
             */
            if (size32 == uint.MaxValue || offset32 == uint.MaxValue || entries16 == ushort.MaxValue)
            {
                long locatorAbsolute = eocdAbsolute - Zip64LocatorLength;

                if (locatorAbsolute < startOffset)
                {
                    return false;
                }

                byte[]? locator = ReadAt(stream, locatorAbsolute, Zip64LocatorLength, ref bytesRead);

                if (locator == null || !Matches(locator, 0, Zip64LocatorSignature))
                {
                    return false;
                }

                ulong zip64Offset = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8, 8));

                if (zip64Offset > long.MaxValue)
                {
                    return false;
                }

                long zip64Absolute = startOffset + (long)zip64Offset;

                if (zip64Absolute < startOffset || zip64Absolute > length - Zip64EocdLength)
                {
                    return false;
                }

                byte[]? zip64 = ReadAt(stream, zip64Absolute, Zip64EocdLength, ref bytesRead);

                if (zip64 == null || !Matches(zip64, 0, Zip64EocdSignature))
                {
                    return false;
                }

                ulong size64 = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(40, 8));
                ulong offset64 = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(48, 8));

                if (size64 > long.MaxValue || offset64 > long.MaxValue)
                {
                    return false;
                }

                size = (long)size64;
                offset = (long)offset64;
            }

            long absolute = startOffset + offset;

            if (absolute < startOffset || absolute > length || size < 0 || size > length - absolute)
            {
                return false;
            }

            cdStart = absolute;
            cdSize = size;

            return true;
        }

        /// <summary>
        /// 逐条扫中央目录：**见到第一个 bit0 就返回 <see cref="CentralDirectoryScan.Encrypted"/>**，
        /// 扫完整个目录都没有则 <see cref="CentralDirectoryScan.Clean"/>，中途读不动 / 撞上扫描上限则
        /// <see cref="CentralDirectoryScan.Unreadable"/>。
        ///
        /// <para>⛔ 分块读（4 KiB 缓冲 + 记录之间 seek 跳过变长部分），内存占用与中央目录大小无关；
        /// 扫过的字节数硬上限 <see cref="MaxCentralDirectoryBytes"/>。</para>
        /// </summary>
        private static CentralDirectoryScan ScanCentralDirectory(
            FileStream stream,
            long cdStart,
            long cdSize,
            ref int bytesRead)
        {
            long limit = Math.Min(cdSize, MaxCentralDirectoryBytes);
            long consumed = 0;
            var record = new byte[CentralHeaderFixedLength];
            var tail = new byte[CentralHeaderFixedLength - 4];

            using var cursor = new BufferedCursor(stream, cdStart, cdStart + limit);

            CentralDirectoryScan result = CentralDirectoryScan.Unreadable;

            while (consumed < limit)
            {
                if (!cursor.TryRead(record, 0, 4))
                {
                    break;
                }

                if (!Matches(record, 0, CentralHeaderSignature))
                {
                    /*
                     * 中央目录末尾可以跟一条"数字签名"记录（PK\x05\x05 + 2 字节长度 + 数据）。
                     * 它不描述任何条目 —— 跳过它继续扫；别的签名就说明结构断了。
                     */
                    if (Matches(record, 0, DigitalSignatureSignature))
                    {
                        if (!cursor.TryRead(record, 0, 2) || !cursor.TrySkip(BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0, 2))))
                        {
                            break;
                        }

                        consumed = cursor.Position - cdStart;

                        continue;
                    }

                    break;
                }

                if (!cursor.TryRead(tail, 0, tail.Length))
                {
                    break;
                }

                Array.Copy(tail, 0, record, 4, tail.Length);

                // 中央目录记录布局：+8 flags(2) +28 名字长度(2) +30 扩展区长度(2) +32 注释长度(2)。
                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(8, 2));

                if ((flags & EncryptedFlag) != 0)
                {
                    result = CentralDirectoryScan.Encrypted;

                    break;
                }

                long variableLength = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(28, 2))
                                      + BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(30, 2))
                                      + BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(32, 2));

                if (!cursor.TrySkip(variableLength))
                {
                    break;
                }

                consumed = cursor.Position - cdStart;
            }

            bytesRead += cursor.BytesRead;

            if (result == CentralDirectoryScan.Encrypted)
            {
                return result;
            }

            /*
             * 扫到上限就停：中央目录比 1 MiB 还长时，我们**没有**把整份目录看完
             * ⇒ 说不出"一条加密的都没有"，只能交给兜底那档（"不知道"）。
             */
            return consumed >= cdSize ? CentralDirectoryScan.Clean : CentralDirectoryScan.Unreadable;
        }

        /// <summary>
        /// 兜底那一档：读第一个本地头的通用位标志（跨盘 zip 的第 1 卷在 <c>+4</c> 处）。
        ///
        /// <para><b>⛔ 这一档只可能给出"加密"或"不知道"，绝不说"没加密"</b> ——
        /// 一个本地头只能代表它自己那一个条目，而现存实现正是栽在这一点上：
        /// <c>dirfirst.zip</c> 的第一个条目是目录（flags = <c>0x0000</c>）、第二个条目才是加密的，
        /// "第一个不是 ⇒ 整包不是"就是那次漏报的根因。放宽成"没加密"等于把同一个坑换个地方再挖一遍。</para>
        /// </summary>
        private static ArchiveEncryptionReading ReadFirstLocalHeaderFlags(
            FileStream stream,
            long startOffset,
            long length,
            ref int bytesRead)
        {
            byte[]? probe = ReadAt(stream, startOffset, LocalHeaderProbeLength, ref bytesRead);

            if (probe == null)
            {
                return Unknown(bytesRead, "第一个本地头读不出来（截断 / 不是本地头）");
            }

            if (Matches(probe, 0, SpannedMarkerSignature))
            {
                // 跨盘 zip 的第 1 卷：PK\x07\x08 + 4 字节盘号，本地头在 +4 处。
                if (startOffset + 4 + LocalHeaderProbeLength > length)
                {
                    return Unknown(bytesRead, "跨盘 zip 的第 1 卷：+4 处的本地头读不出来（截断）");
                }

                probe = ReadAt(stream, startOffset + 4, LocalHeaderProbeLength, ref bytesRead);

                if (probe == null)
                {
                    return Unknown(bytesRead, "跨盘 zip 的第 1 卷：+4 处的本地头读不出来");
                }
            }

            if (!Matches(probe, 0, LocalHeaderSignature))
            {
                return Unknown(bytesRead, "第一个本地头的签名不是 PK 03 04（认不出这一档）");
            }

            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(probe.AsSpan(6, 2));

            if ((flags & EncryptedFlag) != 0)
            {
                return Encrypted(
                    bytesRead,
                    ArchiveEncryptionState.DataEncrypted,
                    "第一个本地头的通用位标志 bit0 置位（+6 处 LE16）");
            }

            return Unknown(
                bytesRead,
                "中央目录读不出来，只读到第一个本地头的通用位标志（bit0 = 0）：它说不出其它条目加没加密");
        }

        /// <summary>从 <paramref name="position"/> 读 <paramref name="count"/> 个字节；读不满返回 null。</summary>
        private static byte[]? ReadAt(FileStream stream, long position, int count, ref int bytesRead)
        {
            if (position < 0 || count < 0 || position > stream.Length - count)
            {
                return null;
            }

            stream.Seek(position, SeekOrigin.Begin);

            var buffer = new byte[count];
            int total = 0;

            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);

                if (read <= 0)
                {
                    return null;
                }

                total += read;
            }

            bytesRead += total;

            return buffer;
        }

        private static int LastIndexOf(byte[] data, byte[] pattern, int from)
        {
            for (int index = Math.Min(from, data.Length - pattern.Length); index >= 0; index--)
            {
                if (Matches(data, index, pattern))
                {
                    return index;
                }
            }

            return -1;
        }

        private static bool Matches(byte[] data, int offset, byte[] pattern)
        {
            if (offset < 0 || offset + pattern.Length > data.Length)
            {
                return false;
            }

            for (int index = 0; index < pattern.Length; index++)
            {
                if (data[offset + index] != pattern[index])
                {
                    return false;
                }
            }

            return true;
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

        /// <summary>中央目录扫出来的三档（加密 / 干净 / 读不动）。</summary>
        private enum CentralDirectoryScan
        {
            /// <summary>见到第一个 bit0 置位的条目。</summary>
            Encrypted = 0,

            /// <summary>整个中央目录扫完，没有任何条目置位。</summary>
            Clean = 1,

            /// <summary>结构断了 / 撞上扫描上限 —— 说不出结论。</summary>
            Unreadable = 2
        }

        /// <summary>
        /// 中央目录上的**分块游标**：定长部分按 4 KiB 缓冲读，变长部分（名字 / 扩展区 / 注释）直接 seek 跳过。
        /// 内存占用与中央目录大小无关，读进内存的字节数记在 <see cref="BytesRead"/> 上。
        /// </summary>
        private sealed class BufferedCursor : IDisposable
        {
            private const int BufferSize = 4096;

            private readonly FileStream _stream;
            private readonly byte[] _buffer = new byte[BufferSize];
            private readonly long _length;

            private int _bufferOffset;
            private int _bufferCount;

            public BufferedCursor(FileStream stream, long start, long limit)
            {
                _stream = stream;
                _length = Math.Min(limit, stream.Length);
                _stream.Seek(start, SeekOrigin.Begin);
                Position = start;
            }

            public long Position { get; private set; }

            public int BytesRead { get; private set; }

            public bool TryRead(byte[] destination, int offset, int count)
            {
                if (count < 0 || Position + count > _length)
                {
                    return false;
                }

                int filled = 0;

                while (filled < count)
                {
                    if (_bufferOffset >= _bufferCount && !Refill())
                    {
                        return false;
                    }

                    int take = Math.Min(count - filled, _bufferCount - _bufferOffset);

                    Array.Copy(_buffer, _bufferOffset, destination, offset + filled, take);

                    _bufferOffset += take;
                    filled += take;
                    Position += take;
                }

                return true;
            }

            public bool TrySkip(long count)
            {
                if (count < 0 || Position + count > _length)
                {
                    return false;
                }

                // 没跨出当前缓冲就在缓冲里挪（省一次系统调用）；跨出去了才真的 seek。
                if (count <= _bufferCount - _bufferOffset)
                {
                    _bufferOffset += (int)count;
                    Position += count;

                    return true;
                }

                Position += count;
                _stream.Seek(Position, SeekOrigin.Begin);
                _bufferOffset = 0;
                _bufferCount = 0;

                return true;
            }

            private bool Refill()
            {
                if (Position >= _length)
                {
                    return false;
                }

                _bufferOffset = 0;
                _bufferCount = _stream.Read(_buffer, 0, _buffer.Length);

                if (_bufferCount <= 0)
                {
                    _bufferCount = 0;

                    return false;
                }

                BytesRead += _bufferCount;

                return true;
            }

            public void Dispose()
            {
                // 基流由调用方持有（它还要读本地头），这里没有要释放的东西。
            }
        }
    }
}

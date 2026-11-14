using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction;

/// <summary>
/// **条目解压**：把一条数据（stored / deflate / AES 解密后再 stored 或 deflate）流式写进目标流。
///
/// <para>三条路各自的读法都是刻意的：</para>
/// <list type="bullet">
/// <item><description><b>stored</b>：读写下重叠（双缓冲），U 盘 / 机械盘上让"下一次读"与"这一次写"同时在飞；
/// deflate / AES 两条路的读要经过解压/解密流，提前读没有意义，所以只对原样拷贝做这件事。</description></item>
/// <item><description><b>deflate</b>：数据区用 <see cref="BoundedReadStream"/> 界住 —— 压缩后大小是中央目录给的，
/// 不套一层界，DeflateStream 会一路读到下一个条目里去。</description></item>
/// <item><description><b>AES</b>：先按 CTR 解密，再按真方法落盘，最后用认证码确认整段密文没被动过
/// （AES 的目的就是"被改过必须发现"；少了它，一个被篡改的包会看着成功）。</description></item>
/// </list>
///
/// <para>⛔ 只写调用方给的 <c>target</c> 流：改名落位、CRC 比对、回滚都在
/// <see cref="EmbeddedZipExtractSession"/> 里。</para>
/// </summary>
internal static class EmbeddedZipEntryWriter
{
    /// <summary>
    /// 写一个条目：stored 原样拷、deflate 解压流、AES 先解密再按真方法处理。返回实际写出的字节数。
    /// </summary>
    internal static long WriteEntry(
        FileStream source,
        long offset,
        EmbeddedZipEntry entry,
        string resolvedPassword,
        ZipAesPasswordEncoding passwordEncoding,
        Stream target,
        EmbeddedZipProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        long entryWritten = 0;

        if (entry.AesStrength != 0)
        {
            return WriteAesEntry(
                source,
                offset,
                entry,
                resolvedPassword,
                passwordEncoding,
                target,
                reporter,
                cancellationToken);
        }

        /*
         * 数据起点 = 本地头偏移 + 30 + 本地头名字长 + 本地头扩展区长。
         * 这里再读一次本地头（而不是把探查处算好的值传下来）：
         * 探查与解压是两次独立的读取，中间源文件可能被换掉（不变量 11 的快照只挡时间戳级的变化），
         * 当场重算才能保证"解的字节"与"认的位置"是同一份文件。
         */
        long localHeader = offset + entry.LocalHeaderOffset;

        byte[] header = EmbeddedZipByteReader.ReadAt(
            source,
            localHeader,
            EmbeddedZipStreamFormat.LocalFileHeaderLength);

        if (header.Length != EmbeddedZipStreamFormat.LocalFileHeaderLength ||
            !EmbeddedZipByteReader.Matches(header, 0, EmbeddedZipStreamFormat.LocalFileHeaderSignature))
        {
            throw new IOException($"条目 {entry.Name} 的本地文件头不存在或不是 PK\\x03\\x04");
        }

        int localNameLength = BitConverter.ToUInt16(header, 26);
        int localExtraLength = BitConverter.ToUInt16(header, 28);
        long dataOffset = localHeader + EmbeddedZipStreamFormat.LocalFileHeaderLength + localNameLength + localExtraLength;

        source.Seek(dataOffset, SeekOrigin.Begin);

        byte[] buffer = new byte[EmbeddedZipStreamFormat.CopyBufferSize];

        if (entry.Method == 0)
        {
            /*
             * 存（Copy）条目：**读写下重叠**地拷（用户 2026-09-25 第 38 条追加："做"）。
             *
             * 为什么值得：老的写法是"读一块 → 写一块 → 读一块…"严格串行，而在 U 盘 / 机械盘上
             * 每一次"读→写"换向都要等一次寻道与旋转延迟，**两边的等待时间会叠加**。
             * 双缓冲让"下一次读"和"这一次写"同时在飞，USB 上通常能把有效吞吐往"两者较大者"推。
             * ⛔ 只对**原样拷贝**这条路做：deflate / AES 那两条路的读要经过解压/解密流，
             * 提前读没有意义（CPU 才是那两条路的主导）。
             */
            byte[] bufferA = new byte[EmbeddedZipStreamFormat.CopyBufferSize];
            byte[] bufferB = new byte[EmbeddedZipStreamFormat.CopyBufferSize];

            long remaining = entry.CompressedSize;
            int length = ReadAtMost(source, bufferA, remaining);

            long copied = 0;

            while (length > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long remainingAfter = remaining - length;
                Task<int>? pendingRead = remainingAfter > 0
                    ? ReadAtMostAsync(source, bufferB, remainingAfter, cancellationToken)
                    : null;

                target.Write(bufferA, 0, length);

                remaining -= length;
                copied += length;
                entryWritten += length;
                reporter.Record(length);

                if (pendingRead == null)
                {
                    break;
                }

                length = pendingRead.GetAwaiter().GetResult();

                // 双缓冲换手：刚读完的那块成为下一轮要写的那块。
                (bufferA, bufferB) = (bufferB, bufferA);
            }

            if (remaining > 0)
            {
                throw new IOException($"条目 {entry.Name} 的数据在归档区间里就读完了（文件可能被截断）");
            }

            _ = copied;
            return entryWritten;
        }

        using var bounded = new BoundedReadStream(source, entry.CompressedSize);
        using var inflater = new DeflateStream(bounded, CompressionMode.Decompress, leaveOpen: true);

        int decompressed;

        while ((decompressed = inflater.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            target.Write(buffer, 0, decompressed);

            entryWritten += decompressed;
            reporter.Record(decompressed);
        }

        return entryWritten;
    }

    /// <summary>
    /// AES 条目的写法：<c>盐(16) + 校验值(2) + 密文 + 认证码(10)</c> ——
    /// 先按 CTR 解密（读到的是明文），再按**真方法**（stored 原样拷 / deflate 解压）落盘，
    /// 最后用认证码确认整段密文没被动过。
    ///
    /// <para>⚠ 认证码这一关不能省：AES 的目的就是"被改过必须发现"。少了它，
    /// 一个被篡改的包会看着成功（不变量 6 的同一口径）。所以顺序是
    /// **先读完所有密文 → 再验认证码 → 才让调用方改名落位**。</para>
    /// </summary>
    private static long WriteAesEntry(
        FileStream source,
        long offset,
        EmbeddedZipEntry entry,
        string resolvedPassword,
        ZipAesPasswordEncoding passwordEncoding,
        Stream target,
        EmbeddedZipProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        if (!EmbeddedZipAesAccess.TryGetAesDataLayout(
                source,
                offset,
                entry,
                out EmbeddedZipAesAccess.AesDataLayout layout,
                out string layoutError))
        {
            throw new IOException(layoutError);
        }

        if (!ZipAesCrypto.TryDeriveKeys(
                resolvedPassword,
                passwordEncoding,
                layout.Salt,
                entry.AesStrength,
                out ZipAesKeys? keys,
                out string keyError))
        {
            throw new IOException($"条目 {entry.Name} 的 AES 密钥派生失败：{keyError}");
        }

        long entryWritten = 0;
        byte[] buffer = new byte[EmbeddedZipStreamFormat.CopyBufferSize];

        using var decrypt = new ZipAesDecryptStream(source, layout.CipherStart, layout.CipherLength, keys!);

        if (entry.Method == 0)
        {
            long remaining = layout.CipherLength;

            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int read = decrypt.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));

                target.Write(buffer, 0, read);

                remaining -= read;
                entryWritten += read;
                reporter.Record(read);
            }
        }
        else
        {
            using (var inflater = new DeflateStream(decrypt, CompressionMode.Decompress, leaveOpen: true))
            {
                int decompressed;

                while ((decompressed = inflater.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    target.Write(buffer, 0, decompressed);

                    entryWritten += decompressed;
                    reporter.Record(decompressed);
                }
            }

            /*
             * deflate 流读到结尾**不等于**密文读完（DeflateStream 会在结束标记处停下）。
             * 认证码是覆盖整段密文的，所以剩下的字节必须也过一遍 —— 否则等于"只认证了一半"。
             */
            Drain(decrypt, buffer, cancellationToken);
        }

        if (!decrypt.VerifyAuthenticationCode(layout.AuthenticationCode))
        {
            throw new IOException(
                $"条目 {entry.Name} 的 AES 认证码对不上：密文可能被改过、或源文件在解压过程中变过");
        }

        return entryWritten;
    }

    /// <summary>把流里剩下的字节读完（只为让认证覆盖整段，内容丢弃）。</summary>
    private static void Drain(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (stream.Read(buffer, 0, buffer.Length) <= 0)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 读一块（最多 <paramref name="maxBytes"/>，最多一个缓冲那么多）；读到 0 表示数据提前结束
    /// —— 调用方按"文件被截断"处理（与老的串行写法同一个判据）。
    /// </summary>
    private static int ReadAtMost(FileStream source, byte[] buffer, long maxBytes)
    {
        int want = (int)Math.Min(buffer.Length, maxBytes);

        return want <= 0 ? 0 : source.Read(buffer, 0, want);
    }

    /// <summary>
    /// 异步版的 <see cref="ReadAtMost(FileStream, byte[], long)"/>：让"下一次读"与"这一次写"重叠
    /// （用户 2026-09-25 第 38 条追加：读写下重叠，做）。
    ///
    /// <para>源流是按 <c>RandomAccess</c> 打开的（直读的读法本身是随机的），没开 <c>useAsync</c>，
    /// 于是 <c>ReadAsync</c> 走线程池 —— 对"与主线程的写重叠"来说够了，且不改变语义：
    /// 它仍然只是把这一块读进缓冲，什么时候真正完成由 <c>GetResult()</c> 那一步决定。</para>
    /// </summary>
    private static Task<int> ReadAtMostAsync(
        FileStream source,
        byte[] buffer,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        int want = (int)Math.Min(buffer.Length, maxBytes);

        return want <= 0
            ? Task.FromResult(0)
            : source.ReadAsync(buffer.AsMemory(0, want), cancellationToken).AsTask();
    }

    /// <summary>
    /// 只让读指针前进 <c>length</c> 个字节的包装流：deflate 数据流的"压缩后大小"是中央目录给的，
    /// 不套一层界，DeflateStream 会一路读到下一个条目里去（读到的字节会被当成压缩数据解，得到垃圾或异常）。
    /// </summary>
    private sealed class BoundedReadStream : Stream
    {
        private readonly FileStream _source;
        private long _remaining;

        public BoundedReadStream(FileStream source, long length)
        {
            _source = source;
            _remaining = length > 0 ? length : 0;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            int allowed = (int)Math.Min(count, _remaining);
            int read = _source.Read(buffer, offset, allowed);

            _remaining -= read;

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

using System;
using System.IO;
using System.Security.Cryptography;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 只读的"解密 + 认证"流：包住源文件里 <c>[cipherStart, cipherStart + cipherLength)</c> 这一段密文，
    /// 读出来就是明文，同时把读过的**密文**喂给 HMAC。
    ///
    /// <para><b>为什么要做成流</b>：条目可能是 deflate（先压缩后加密），解压侧只需要一个
    /// "读到的是明文"的流就能复用既有的 <see cref="System.IO.Compression.DeflateStream"/> 通路；
    /// 顺便让 HMAC 与解密看的是同一份字节，不会出现"校验过了但解密用的是另一段"这种自欺。</para>
    ///
    /// <para><b>认证在读完时才成立</b>：<see cref="VerifyAuthenticationCode"/> 必须在整段密文都被读完之后调用。
    /// 少了这一步会变成"篡改过的包也报成功"（不变量 6 的同一口径）。</para>
    /// </summary>
    internal sealed class ZipAesDecryptStream : Stream
    {
        private readonly Stream _source;
        private readonly long _cipherLength;
        private readonly ZipAesCtrTransform _ctr;
        private readonly IncrementalHash _hmac;
        private long _cipherRead;

        public ZipAesDecryptStream(Stream source, long cipherStart, long cipherLength, ZipAesKeys keys)
        {
            _source = source;
            _cipherLength = cipherLength;
            _ctr = new ZipAesCtrTransform(keys.EncryptionKey);
            _hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, keys.AuthenticationKey);

            source.Seek(cipherStart, SeekOrigin.Begin);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _cipherLength;

        public override long Position
        {
            get => _cipherRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = _cipherLength - _cipherRead;

            if (remaining <= 0 || count <= 0)
            {
                return 0;
            }

            int want = (int)Math.Min(count, remaining);
            int read = _source.Read(buffer, offset, want);

            if (read <= 0)
            {
                throw new EndOfStreamException(
                    $"密文没读完就读到了文件末尾：还差 {remaining} 字节（文件可能被截断）");
            }

            // 先喂 HMAC（认证明明是对**密文**算的），再就地解密。
            _hmac.AppendData(buffer, offset, read);
            _ctr.Transform(buffer, offset, read);

            _cipherRead += read;
            return read;
        }

        /// <summary>整段密文读完之后调用：对不上就是"密文被动过 / 密码不对"，必须报错。</summary>
        public bool VerifyAuthenticationCode(byte[] expected)
        {
            byte[] actual = _hmac.GetHashAndReset();
            var trimmed = new byte[ZipAesCrypto.AuthenticationCodeLength];
            Buffer.BlockCopy(actual, 0, trimmed, 0, ZipAesCrypto.AuthenticationCodeLength);

            return ZipAesCrypto.FixedTimeEquals(trimmed, expected);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _ctr.Dispose();
                _hmac.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

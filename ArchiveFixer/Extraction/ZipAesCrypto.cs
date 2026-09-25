using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 用哪种字节编码把密码字符串变成 <b>密钥派生的输入字节</b>。
    ///
    /// <para><b>为什么这不是"可选优化"而是必须有的两档</b>（2026-09-25 真机取证，见 <c>修改日志.md</c> 第 29 条）：
    /// ZIP 的 AES 加密没有"密码字符串"这个概念，只有<b>字节</b> —— 同一个中文密码，
    /// 打包方写进去的字节不同，派生出来的密钥就完全不同。实测：</para>
    /// <list type="bullet">
    /// <item><description>WinRAR（以及 7-Zip 的解码侧）用 **ANSI 代码页（中文 Windows = 936/GBK）** 的字节：
    /// 本机造的 AES-256 包用 <c>中文密码ABC</c>，按 GBK 字节派生出的校验值 <c>0x0B81</c> 与包里存的一致，
    /// 按 UTF-8 派生的是 <c>0x391E</c>（对不上）。</description></item>
    /// <item><description>百度网盘分享打包出来的 ZIP 用的是 **UTF-8** 字节（它的通用位标志 bit11 就写着
    /// <c>Encrypt UTF8</c>，7-Zip 列表里能直接看到）。</description></item>
    /// </list>
    ///
    /// <para>而 7-Zip <b>不管那个 bit11</b>：实测把 WinRAR 的包手工置上 bit11，7-Zip 照样能解开
    /// （说明它始终按 ANSI 派生）。结论：**UTF-8 打包的加密包，7-Zip 永远打不开，密码再对也报"密码错误"** ——
    /// 这正是用户 2026-09-25 报的"密码还是对不上，我那个密码是中文的就没有办法了吗"。
    /// 所以本读取器两档都试，谁通过校验用谁。</para>
    /// </summary>
    public enum ZipAesPasswordEncoding
    {
        /// <summary>UTF-8 字节（百度网盘分享包、7-Zip 自己造的 7z 包这套习惯）。</summary>
        Utf8,

        /// <summary>系统 ANSI 代码页字节（中文 Windows = 936/GBK；WinRAR、老式 ZIP 工具这套习惯）。</summary>
        AnsiCodePage
    }

    /// <summary>
    /// 一套 AES 密钥：加密密钥、认证密钥、两字节密码校验值。
    /// ⛔ 只在内存里流转（AGENTS.md §8）：不进日志、不进报告、不进异常文本。
    /// </summary>
    public sealed class ZipAesKeys
    {
        public byte[] EncryptionKey { get; init; } = Array.Empty<byte>();

        public byte[] AuthenticationKey { get; init; } = Array.Empty<byte>();

        /// <summary>两字节密码校验值（WinZip AES 存在密文开头，用来便宜地判"密码对不对"）。</summary>
        public ushort PasswordVerifier { get; init; }
    }

    /// <summary>
    /// WinZip AES（ZIP 里的 <c>method = 99</c>，扩展字段 <c>0x9901</c>）的密码学实现。
    ///
    /// <para><b>为什么自己写这一块</b>：7-Zip 命令行没有"指定密码字节编码"的开关（实测它无视 bit11），
    /// 于是"UTF-8 打包的中文密码 AES 包"在它那里是死路；而解压产物、路径预检、资源预算这些
    /// 都已有唯一实现，缺的只是这一段密码学。</para>
    ///
    /// <para><b>按规范实现的三件事</b>（WinZip AES / APPNOTE 6.2 §7.2）：</para>
    /// <list type="number">
    /// <item><description><b>密钥派生</b>：PBKDF2-HMAC-SHA1，1000 轮，盐来自密文开头（AES-256 是 16 字节），
    /// 输出 <c>2×密钥长 + 2</c> 字节 = 加密密钥 + 认证密钥 + 2 字节校验值。</description></item>
    /// <item><description><b>加密</b>：AES-CTR（WinZip 变体：16 字节计数器，<b>低 4 字节小端</b>记块号、从 1 开始，
    /// 其余 12 字节为 0；ECB 加密计数器块得到密钥流）。</description></item>
    /// <item><description><b>认证</b>：HMAC-SHA1 覆盖**密文**，取前 10 字节。</description></item>
    /// </list>
    ///
    /// <para>本类不引用 WPF，也不碰界面（AGENTS.md §4 分层铁律）。</para>
    /// </summary>
    public static class ZipAesCrypto
    {
        /// <summary>PBKDF2 轮数：规范固定 1000（不是"我们选的"，改了就解不开别人的包）。</summary>
        public const int KeyDerivationIterations = 1000;

        /// <summary>AES-128/192/256 的盐长度按规范都是 16 字节（只有老式 8 字节盐的变体才不是）。</summary>
        public const int SaltLength = 16;

        /// <summary>密码校验值长度。</summary>
        public const int PasswordVerifierLength = 2;

        /// <summary>认证码长度（HMAC-SHA1 取前 10 字节）。</summary>
        public const int AuthenticationCodeLength = 10;

        /// <summary>ZIP 通用位标志里"加密"那一位。</summary>
        public const int EncryptedFlag = 0x0001;

        /// <summary>ZIP 通用位标志里"名字与密码按 UTF-8"那一位（打包方声明，7-Zip 解码时不认）。</summary>
        public const int Utf8Flag = 0x0800;

        /// <summary>AES 条目的"压缩方法"字段值（真方法在扩展字段里）。</summary>
        public const int AesMethod = 99;

        /// <summary>本类总开销：盐 + 校验值 + 认证码（判断"压缩后大小"是否自洽时要用）。</summary>
        public const int OverheadLength = SaltLength + PasswordVerifierLength + AuthenticationCodeLength;

        /// <summary>按强度字节（1/2/3）给出密钥长度；认不出的强度返回 0。</summary>
        public static int KeyLengthForStrength(int strength)
        {
            return strength switch
            {
                1 => 16,
                2 => 24,
                3 => 32,
                _ => 0
            };
        }

        /// <summary>强度字节的中文说明（报告与日志里用；认不出就如实写数字）。</summary>
        public static string DescribeStrength(int strength)
        {
            return KeyLengthForStrength(strength) switch
            {
                16 => "AES-128",
                24 => "AES-192",
                32 => "AES-256",
                _ => $"未知强度({strength})"
            };
        }

        /// <summary>编码的中文说明（日志里说清"这次是按哪种字节解开的"）。</summary>
        public static string DescribeEncoding(ZipAesPasswordEncoding encoding)
        {
            return encoding == ZipAesPasswordEncoding.Utf8 ? "UTF-8 字节" : "ANSI(936) 字节";
        }

        /// <summary>
        /// 把一个密码字符串按指定编码变成字节。
        ///
        /// <para>ANSI 那一档取**系统 ANSI 代码页**（中文 Windows = 936）。取不到代码页时退回 GB18030
        /// （GBK 的超集，常用汉字字节完全相同），并**不静默**：错误信息里写明退回了什么。</para>
        /// </summary>
        public static bool TryGetPasswordBytes(
            string password,
            ZipAesPasswordEncoding encoding,
            out byte[] bytes,
            out string error)
        {
            bytes = Array.Empty<byte>();
            error = string.Empty;

            if (password == null)
            {
                error = "密码为空引用";
                return false;
            }

            if (encoding == ZipAesPasswordEncoding.Utf8)
            {
                bytes = Encoding.UTF8.GetBytes(password);
                return true;
            }

            CodePageEncodingBootstrap.EnsureRegistered();

            int codePage;

            try
            {
                codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                codePage = 936;
            }

            if (codePage <= 0)
            {
                codePage = 936;
            }

            Encoding? ansi = null;

            try
            {
                ansi = Encoding.GetEncoding(codePage);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // 取不到 ANSI 代码页不是崩溃点：GB18030 与 GBK 对常用汉字是同一套字节。
                try
                {
                    ansi = Encoding.GetEncoding("GB18030");
                }
                catch (Exception inner) when (inner is ArgumentException or NotSupportedException)
                {
                    error = $"取不到 ANSI 代码页（{codePage}）也取不到 GB18030，无法按 ANSI 字节派生密钥";
                    return false;
                }
            }

            /*
             * 不能在这里做"取不到就换成 UTF-8"这种兜底：
             * 那样会变成"悄悄换了密码字节" —— 结果就是报"密码错误"，而用户手上的密码其实是对的。
             */
            bytes = ansi.GetBytes(password);
            return true;
        }

        /// <summary>
        /// 派生一套密钥。失败（编码取不到 / 强度不认识 / 盐长度不对）返回 false + 原因。
        /// </summary>
        public static bool TryDeriveKeys(
            string password,
            ZipAesPasswordEncoding encoding,
            byte[] salt,
            int strength,
            out ZipAesKeys? keys,
            out string error)
        {
            keys = null;
            error = string.Empty;

            int keyLength = KeyLengthForStrength(strength);

            if (keyLength == 0)
            {
                error = $"认不出的 AES 强度字节：{strength}";
                return false;
            }

            if (salt == null || salt.Length < 8)
            {
                error = "盐太短（连 8 字节都没有），不像 WinZip AES 的密文开头";
                return false;
            }

            if (!TryGetPasswordBytes(password, encoding, out byte[] passwordBytes, out string byteError))
            {
                error = byteError;
                return false;
            }

            try
            {
                /*
                 * PBKDF2-HMAC-SHA1：规范写死了 1000 轮。
                 * 用 Rfc2898DeriveBytes（.NET 自带）而不是自己搓 HMAC 循环：
                 * 这一步是"密码对不对"的唯一判据，自己实现只会多一份出错的地方。
                 */
                using var kdf = new Rfc2898DeriveBytes(
                    passwordBytes,
                    salt,
                    KeyDerivationIterations,
                    HashAlgorithmName.SHA1);

                byte[] material = kdf.GetBytes((keyLength * 2) + PasswordVerifierLength);

                var encryptionKey = new byte[keyLength];
                var authenticationKey = new byte[keyLength];

                Buffer.BlockCopy(material, 0, encryptionKey, 0, keyLength);
                Buffer.BlockCopy(material, keyLength, authenticationKey, 0, keyLength);

                keys = new ZipAesKeys
                {
                    EncryptionKey = encryptionKey,
                    AuthenticationKey = authenticationKey,
                    PasswordVerifier = BitConverter.ToUInt16(material, keyLength * 2)
                };

                return true;
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                error = "密钥派生失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 算一个条目的认证码：HMAC-SHA1 覆盖**密文**，取前 10 字节。
        ///
        /// <para>为什么要单独暴露它：校验"整份密文有没有被动过"必须与解密用同一份字节，
        /// 因此调用方在读出密文时要同时喂给它（见 <see cref="ZipAesDecryptStream"/>）。</para>
        /// </summary>
        public static byte[] ComputeAuthenticationCode(byte[] authenticationKey, byte[] cipherText)
        {
            using var hmac = new HMACSHA1(authenticationKey);
            byte[] full = hmac.ComputeHash(cipherText);
            var code = new byte[AuthenticationCodeLength];
            Buffer.BlockCopy(full, 0, code, 0, AuthenticationCodeLength);
            return code;
        }

        /// <summary>定长比较（认证码）：不做提前返回，避免把"前几位对"这种信息漏出去。</summary>
        public static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            int diff = 0;

            for (int i = 0; i < left.Length; i++)
            {
                diff |= left[i] ^ right[i];
            }

            return diff == 0;
        }
    }

    /// <summary>
    /// WinZip AES 的 AES-CTR 密钥流（一次一个块，块号从 1 开始）。
    ///
    /// <para>为什么不用 <c>Aes</c> 的 CBC/CFB 模式凑：WinZip 的 CTR 是**自定义计数器布局**
    /// （低 4 字节小端块号 + 12 字节 0），只有 ECB 加密计数器块这一条路能原样实现它。</para>
    /// </summary>
    internal sealed class ZipAesCtrTransform : IDisposable
    {
        private readonly Aes _aes;
        private readonly ICryptoTransform _ecb;
        private readonly byte[] _counter = new byte[16];
        private readonly byte[] _keystream = new byte[16];
        private int _keystreamOffset = 16;
        private uint _blockIndex;

        public ZipAesCtrTransform(byte[] encryptionKey)
        {
            _aes = Aes.Create();
            _aes.Mode = CipherMode.ECB;
            _aes.Padding = PaddingMode.None;
            _aes.Key = encryptionKey;
            _ecb = _aes.CreateEncryptor();
        }

        /// <summary>把 <paramref name="count"/> 个字节就地异或掉（输入 = 密文，输出 = 明文）。</summary>
        public void Transform(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (_keystreamOffset >= 16)
                {
                    NextKeystreamBlock();
                }

                buffer[offset + i] ^= _keystream[_keystreamOffset++];
            }
        }

        private void NextKeystreamBlock()
        {
            _blockIndex++;

            // 低 4 字节小端记块号，其余 12 字节保持 0（规范布局，见类注释）。
            _counter[0] = (byte)(_blockIndex & 0xFF);
            _counter[1] = (byte)((_blockIndex >> 8) & 0xFF);
            _counter[2] = (byte)((_blockIndex >> 16) & 0xFF);
            _counter[3] = (byte)((_blockIndex >> 24) & 0xFF);

            _ecb.TransformBlock(_counter, 0, 16, _keystream, 0);
            _keystreamOffset = 0;
        }

        public void Dispose()
        {
            _ecb.Dispose();
            _aes.Dispose();
        }
    }

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

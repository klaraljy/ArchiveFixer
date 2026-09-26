using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ArchiveFixer.Extraction;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 测试用的 **AES ZIP 写入器**（WinZip AES，AE-1）。
    ///
    /// <para><b>为什么测试需要自己写一个</b>：要钉住的正是"打包方用的是哪种密码字节编码"，
    /// 而现成工具里没有一个能按我们的意愿选编码 —— 7-Zip **拒绝**用非 ASCII 密码造 ZIP
    /// （实测 <c>7z a -tzip -p中文密码</c> 直接报"参数错误"），WinRAR 只按 ANSI 字节（实测 pv 对得上），
    /// 百度网盘那种按 UTF-8 字节打包的包则没有命令行工具能造。所以这个写入器是本组测试的**实验器材**：
    /// 它自己按 PBKDF2 / AES-CTR / HMAC-SHA1 规范写（与产品实现相互独立），
    /// 并且用**真 7z 反向校验**（见 <c>ZipAesDirectReadTests</c>：按 ANSI 字节造出来的包，7z 必须能解开）——
    /// 器材本身错了，那一条会红。</para>
    ///
    /// <para>只实现测试用得到的最小集合：单条目、stored / deflate、AES-128/192/256、可选 UTF-8 名字标志。</para>
    /// </summary>
    internal static class ZipAesTestWriter
    {
        private const int SaltLength = 16;
        private const int AuthenticationCodeLength = 10;
        private const int AesMethodField = 99;

        /// <summary>
        /// 造一个（可加密的）ZIP 字节数组。
        /// </summary>
        /// <param name="entryName">条目名（按 UTF-8 写入）。</param>
        /// <param name="plaintext">条目内容。</param>
        /// <param name="password">密码；<c>null</c> = 不加密。</param>
        /// <param name="passwordEncoding">密码字符串按哪种字节参与密钥派生（UTF-8 / ANSI）。</param>
        /// <param name="deflate">true = 先 deflate 再（加密）；false = stored。</param>
        /// <param name="utf8NameFlag">是否在通用位标志里声明 bit11（UTF-8 名字 / 密码）。
        /// 百度网盘那种包会置上它，WinRAR 不置 —— 而 7-Zip 解码时**不看**这一位（实测），这正是坑所在。</param>
        public static byte[] Build(
            string entryName,
            byte[] plaintext,
            string? password = null,
            ZipAesPasswordEncoding passwordEncoding = ZipAesPasswordEncoding.Utf8,
            bool deflate = false,
            bool utf8NameFlag = false,
            int strength = 3)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(entryName);
            byte[] compressed = deflate ? Deflate(plaintext) : plaintext;
            int actualMethod = deflate ? 8 : 0;

            ushort flags = utf8NameFlag ? (ushort)0x0800 : (ushort)0;
            int method = actualMethod;
            byte[] entryData = compressed;
            byte[] extra;

            if (password == null)
            {
                extra = Array.Empty<byte>();
            }
            else
            {
                flags |= 0x0001;
                method = AesMethodField;

                int keyLength = strength switch { 1 => 16, 2 => 24, _ => 32 };

                byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
                byte[] keyMaterial = DeriveKeyMaterial(password, passwordEncoding, salt, keyLength);

                var encryptionKey = new byte[keyLength];
                var authenticationKey = new byte[keyLength];

                Buffer.BlockCopy(keyMaterial, 0, encryptionKey, 0, keyLength);
                Buffer.BlockCopy(keyMaterial, keyLength, authenticationKey, 0, keyLength);

                byte[] cipher = AesCtr(compressed, encryptionKey);
                byte[] authCode = ComputeAuthenticationCode(authenticationKey, cipher);

                using var data = new MemoryStream();
                data.Write(salt);
                data.Write(keyMaterial, keyLength * 2, 2);   // 两字节密码校验值（规范放在密文开头）
                data.Write(cipher);
                data.Write(authCode);

                entryData = data.ToArray();
                extra = BuildAesExtra(strength, actualMethod);
            }

            uint crc = Crc32(plaintext);
            ushort versionNeeded = password == null ? (ushort)20 : (ushort)51;

            using var output = new MemoryStream();

            // 本地文件头。
            output.Write(Encoding.ASCII.GetBytes("PK\x03\x04"));
            WriteUInt16(output, versionNeeded);
            WriteUInt16(output, flags);
            WriteUInt16(output, (ushort)method);
            WriteUInt16(output, 0);                       // 时间
            WriteUInt16(output, 0);                       // 日期
            WriteUInt32(output, crc);
            WriteUInt32(output, (uint)entryData.Length);
            WriteUInt32(output, (uint)plaintext.Length);
            WriteUInt16(output, (ushort)nameBytes.Length);
            WriteUInt16(output, (ushort)extra.Length);
            output.Write(nameBytes);
            output.Write(extra);
            output.Write(entryData);

            long centralOffset = output.Position;

            // 中央目录条目。
            output.Write(Encoding.ASCII.GetBytes("PK\x01\x02"));
            WriteUInt16(output, 20);                      // 造它的"工具版本"：与真实现场同量级
            WriteUInt16(output, versionNeeded);
            WriteUInt16(output, flags);
            WriteUInt16(output, (ushort)method);
            WriteUInt16(output, 0);
            WriteUInt16(output, 0);
            WriteUInt32(output, crc);
            WriteUInt32(output, (uint)entryData.Length);
            WriteUInt32(output, (uint)plaintext.Length);
            WriteUInt16(output, (ushort)nameBytes.Length);
            WriteUInt16(output, (ushort)extra.Length);
            WriteUInt16(output, 0);                       // 条目注释长度
            WriteUInt16(output, 0);                       // 起始盘
            WriteUInt16(output, 0);                       // 内部属性
            WriteUInt32(output, 0);                       // 外部属性
            WriteUInt32(output, 0);                       // 本地头偏移（本写入器只有一条，恒为 0）
            output.Write(nameBytes);
            output.Write(extra);

            long centralSize = output.Position - centralOffset;

            // EOCD。
            output.Write(Encoding.ASCII.GetBytes("PK\x05\x06"));
            WriteUInt16(output, 0);
            WriteUInt16(output, 0);
            WriteUInt16(output, 1);
            WriteUInt16(output, 1);
            WriteUInt32(output, (uint)centralSize);
            WriteUInt32(output, (uint)centralOffset);
            WriteUInt16(output, 0);

            return output.ToArray();
        }

        /// <summary>
        /// 把 ZIP 字节拼成"双面文件"：<c>[前缀][ZIP][尾巴]</c>，写进 <paramref name="path"/>。
        /// 返回归档终点（= 前缀长度 + ZIP 长度），调用方拿它当 <c>ArchiveEnd</c>。
        /// </summary>
        public static long BuildPolyglot(
            string path,
            byte[] zip,
            int prefixLength,
            int tailLength,
            byte[]? tail = null)
        {
            byte[] prefix = new byte[prefixLength];

            new Random(20260925).NextBytes(prefix);

            // 前 12 字节做成真格式的 ftyp box：识别阶段才认得出这是"视频 + 尾部归档"。
            Array.Copy(new byte[] { 0x00, 0x00, 0x00, 0x20 }, 0, prefix, 0, 4);
            Array.Copy(Encoding.ASCII.GetBytes("ftypisom"), 0, prefix, 4, 8);

            byte[] realTail = tail ?? new byte[tailLength];

            for (int i = 0; tail == null && i < realTail.Length; i++)
            {
                realTail[i] = (byte)(i % 251);
            }

            using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

            output.Write(prefix);
            output.Write(zip);
            output.Write(realTail);

            return prefixLength + zip.Length;
        }

        /// <summary>按规范派生密钥材料：PBKDF2-HMAC-SHA1，1000 轮，输出 <c>2×密钥长 + 2</c> 字节。</summary>
        private static byte[] DeriveKeyMaterial(string password, ZipAesPasswordEncoding encoding, byte[] salt, int keyLength)
        {
            byte[] passwordBytes = encoding == ZipAesPasswordEncoding.Utf8
                ? Encoding.UTF8.GetBytes(password)
                : GetAnsiBytes(password);

            using var kdf = new Rfc2898DeriveBytes(passwordBytes, salt, 1000, HashAlgorithmName.SHA1);

            return kdf.GetBytes((keyLength * 2) + 2);
        }

        /// <summary>
        /// 系统 ANSI 代码页（中文 Windows = 936）的字节。
        /// ⚠ 这里刻意**不调产品的** <c>ZipAesCrypto.TryGetPasswordBytes</c>：器材要独立于被测物。
        /// </summary>
        private static byte[] GetAnsiBytes(string password)
        {
            try
            {
                return Encoding.GetEncoding(936).GetBytes(password);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // 拿不到 936（非中文系统）就按 GB18030 走：常用汉字字节与 GBK 相同。
                return Encoding.GetEncoding("GB18030").GetBytes(password);
            }
        }

        /// <summary>AES-CTR（WinZip 变体：低 4 字节小端记块号、从 1 开始，其余 12 字节为 0）。</summary>
        private static byte[] AesCtr(byte[] input, byte[] key)
        {
            using Aes aes = Aes.Create();

            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = key;

            using ICryptoTransform ecb = aes.CreateEncryptor();

            var output = new byte[input.Length];
            var counter = new byte[16];
            var keystream = new byte[16];
            uint block = 0;

            for (int offset = 0; offset < input.Length; offset += 16)
            {
                block++;
                counter[0] = (byte)(block & 0xFF);
                counter[1] = (byte)((block >> 8) & 0xFF);
                counter[2] = (byte)((block >> 16) & 0xFF);
                counter[3] = (byte)((block >> 24) & 0xFF);

                ecb.TransformBlock(counter, 0, 16, keystream, 0);

                int chunk = Math.Min(16, input.Length - offset);

                for (int i = 0; i < chunk; i++)
                {
                    output[offset + i] = (byte)(input[offset + i] ^ keystream[i]);
                }
            }

            return output;
        }

        /// <summary>认证码：HMAC-SHA1 覆盖密文，取前 10 字节。</summary>
        private static byte[] ComputeAuthenticationCode(byte[] authenticationKey, byte[] cipher)
        {
            using var hmac = new HMACSHA1(authenticationKey);

            byte[] full = hmac.ComputeHash(cipher);
            var code = new byte[AuthenticationCodeLength];

            Buffer.BlockCopy(full, 0, code, 0, AuthenticationCodeLength);

            return code;
        }

        /// <summary>AES 扩展字段 0x9901（7 字节）：版本(2) + "AE"(2) + 强度(1) + 真压缩方法(2)。</summary>
        private static byte[] BuildAesExtra(int strength, int actualMethod)
        {
            using var extra = new MemoryStream();
            var writer = new BinaryWriter(extra);

            writer.Write((ushort)0x9901);
            writer.Write((ushort)7);
            writer.Write((ushort)1);
            writer.Write((byte)'A');
            writer.Write((byte)'E');
            writer.Write((byte)strength);
            writer.Write((ushort)actualMethod);

            return extra.ToArray();
        }

        private static byte[] Deflate(byte[] input)
        {
            using var output = new MemoryStream();

            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(input, 0, input.Length);
            }

            return output.ToArray();
        }

        private static void WriteUInt16(Stream stream, ushort value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >> 24) & 0xFF));
        }

        /// <summary>标准 CRC-32（AE-1 要写它；AE-2 才是 0）。</summary>
        private static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;

            foreach (byte value in data)
            {
                crc ^= value;

                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
            }

            return ~crc;
        }
    }
}

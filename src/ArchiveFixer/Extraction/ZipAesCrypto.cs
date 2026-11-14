using ArchiveFixer.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ArchiveFixer.Extraction;

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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ArchiveFixer.Extraction;

/// <summary>
/// AES 条目的**密码与数据布局**：盐 / 校验值 / 密文 / 认证码在源文件里的位置，候选密码逐个试开，
/// 以及"命中之后用认证码确认整段密文"。
///
/// <para><b>为什么这件事必须自己走</b>（2026-09-25 第 29 条）：7-Zip 对 AES 包只按 ANSI 字节派生
/// 密码、无视归档里的 <c>Encrypt UTF8</c> 声明，百度网盘那种"UTF-8 打包"的中文密码在它那里
/// **永远**报密码错误。这里按规范先用两字节校验值筛（PBKDF2 1000 轮，约 0.5 ms 一次），
/// 再用 HMAC-SHA1 认证码确认（覆盖整段密文），两档字节编码都试 —— 谁通过校验用谁。</para>
///
/// <para>⛔ 密码明文只活在调用栈里：这里只回"第几个候选 + 哪种编码"，绝不进日志、报告与异常文本
/// （AGENTS.md §8）。⛔ 只读：这里不落任何字节，真正的解密写在
/// <see cref="EmbeddedZipEntryWriter"/> 里。</para>
/// </summary>
internal static class EmbeddedZipAesAccess
{
    /// <summary>AES 条目的数据布局：盐 / 校验值 / 密文 / 认证码在源文件里的位置。</summary>
    internal readonly struct AesDataLayout
    {
        internal byte[] Salt { get; init; }

        internal ushort PasswordVerifier { get; init; }

        internal long CipherStart { get; init; }

        internal long CipherLength { get; init; }

        internal byte[] AuthenticationCode { get; init; }
    }

    /// <summary>
    /// 逐个候选密码 × 两种字节编码试开第一条 AES 条目：
    /// 先用两字节校验值筛（PBKDF2 1000 轮，约 0.5 ms 一次，够便宜），
    /// 再用**认证码**确认（HMAC-SHA1 覆盖整段密文 —— 校验值只有 2 字节，1/65536 的巧合必须由它兜住）。
    ///
    /// <para>顺序上先试 UTF-8：百度网盘那种分享包在归档里就声明着 <c>Encrypt UTF8</c>，
    /// 而它恰恰是 7-Zip 打不开的那一类；WinRAR 那类按 ANSI 打包的包会在第二档命中。</para>
    /// </summary>
    internal static bool TryResolvePassword(
        FileStream stream,
        long offset,
        List<EmbeddedZipEntry> entries,
        IReadOnlyList<string> passwords,
        CancellationToken cancellationToken,
        bool confirmResolvedPassword,
        out int resolvedIndex,
        out ZipAesPasswordEncoding resolvedEncoding,
        out string reason)
    {
        resolvedIndex = 0;
        resolvedEncoding = ZipAesPasswordEncoding.Utf8;
        reason = string.Empty;

        EmbeddedZipEntry? probeEntry = entries.FirstOrDefault(e => !e.IsDirectory && e.AesStrength != 0);

        if (probeEntry == null)
        {
            return true;
        }

        if (!TryGetAesDataLayout(stream, offset, probeEntry, out AesDataLayout layout, out string layoutError))
        {
            reason = layoutError;
            return false;
        }

        int pvMatched = 0;

        for (int i = 0; i < passwords.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (ZipAesPasswordEncoding encoding in new[]
                     {
                         ZipAesPasswordEncoding.Utf8,
                         ZipAesPasswordEncoding.AnsiCodePage
                     })
            {
                if (!ZipAesCrypto.TryDeriveKeys(
                        passwords[i] ?? string.Empty,
                        encoding,
                        layout.Salt,
                        probeEntry.AesStrength,
                        out ZipAesKeys? keys,
                        out _))
                {
                    continue;
                }

                if (keys!.PasswordVerifier != layout.PasswordVerifier)
                {
                    continue;
                }

                pvMatched++;

                /*
                 * 校验值撞上了：用认证码确认。
                 * 这一步要顺序读完整段密文（几百 MB 的条目约 1 秒），所以只在"校验值对了"时才做 ——
                 * 一个候选都不对时，整轮尝试仍是毫秒级。
                 */
                if (!confirmResolvedPassword || ConfirmByAuthenticationCode(stream, layout, keys, cancellationToken))
                {
                    resolvedIndex = i + 1;
                    resolvedEncoding = encoding;
                    return true;
                }
            }
        }

        reason = pvMatched > 0
            ? $"这个内嵌 ZIP 是 AES 加密的（{ZipAesCrypto.DescribeStrength(probeEntry.AesStrength)}），" +
              $"{passwords.Count} 个候选密码里有一个通过了校验值，但认证码对不上（密文可能被改过 / 源文件变过）——" +
              "没有解开，也没有生成临时副本。"
            : $"这个内嵌 ZIP 是 AES 加密的（{ZipAesCrypto.DescribeStrength(probeEntry.AesStrength)}），" +
              $"{passwords.Count} 个候选密码逐个按 UTF-8 与 ANSI(936) 两种字节各试了一遍，都不对。" +
              "（7-Zip 对 AES 包只按 ANSI 字节派生密码，所以这类包交给它也一样报密码错误。）";

        return false;
    }

    /// <summary>
    /// 按本地头算出 AES 条目的数据布局（本地头长度每次都重算，理由同
    /// <see cref="EmbeddedZipEntryWriter.WriteEntry"/>：探查与解压是两次独立的读取，中间源文件可能被换掉）。
    /// </summary>
    internal static bool TryGetAesDataLayout(
        FileStream stream,
        long offset,
        EmbeddedZipEntry entry,
        out AesDataLayout layout,
        out string error)
    {
        layout = default;
        error = string.Empty;

        long localHeader = offset + entry.LocalHeaderOffset;
        byte[] header = EmbeddedZipByteReader.ReadAt(
            stream,
            localHeader,
            EmbeddedZipStreamFormat.LocalFileHeaderLength);

        if (header.Length != EmbeddedZipStreamFormat.LocalFileHeaderLength ||
            !EmbeddedZipByteReader.Matches(header, 0, EmbeddedZipStreamFormat.LocalFileHeaderSignature))
        {
            error = $"条目 {entry.Name} 的本地文件头不存在或不是 PK\\x03\\x04";
            return false;
        }

        int localNameLength = BitConverter.ToUInt16(header, 26);
        int localExtraLength = BitConverter.ToUInt16(header, 28);
        long dataOffset = localHeader + EmbeddedZipStreamFormat.LocalFileHeaderLength + localNameLength + localExtraLength;
        long cipherStart = dataOffset + ZipAesCrypto.SaltLength + ZipAesCrypto.PasswordVerifierLength;
        long cipherLength = entry.CompressedSize - ZipAesCrypto.OverheadLength;

        if (cipherLength < 0)
        {
            error = $"条目 {entry.Name} 的压缩后大小 {entry.CompressedSize} 放不下 AES 的盐与认证码";
            return false;
        }

        byte[] prefix = EmbeddedZipByteReader.ReadAt(
            stream,
            dataOffset,
            ZipAesCrypto.SaltLength + ZipAesCrypto.PasswordVerifierLength);

        if (prefix.Length != ZipAesCrypto.SaltLength + ZipAesCrypto.PasswordVerifierLength)
        {
            error = $"条目 {entry.Name} 的 AES 盐/校验值读不满（文件可能被截断）";
            return false;
        }

        var salt = new byte[ZipAesCrypto.SaltLength];
        Buffer.BlockCopy(prefix, 0, salt, 0, ZipAesCrypto.SaltLength);

        byte[] authCode = EmbeddedZipByteReader.ReadAt(
            stream,
            cipherStart + cipherLength,
            ZipAesCrypto.AuthenticationCodeLength);

        if (authCode.Length != ZipAesCrypto.AuthenticationCodeLength)
        {
            error = $"条目 {entry.Name} 的 AES 认证码读不满（文件可能被截断）";
            return false;
        }

        layout = new AesDataLayout
        {
            Salt = salt,
            PasswordVerifier = BitConverter.ToUInt16(prefix, ZipAesCrypto.SaltLength),
            CipherStart = cipherStart,
            CipherLength = cipherLength,
            AuthenticationCode = authCode
        };

        return true;
    }

    /// <summary>把整段密文按"解密 + 认证"读一遍，只判认证码对不对（不落任何字节）。</summary>
    private static bool ConfirmByAuthenticationCode(
        FileStream stream,
        AesDataLayout layout,
        ZipAesKeys keys,
        CancellationToken cancellationToken)
    {
        using var decrypt = new ZipAesDecryptStream(stream, layout.CipherStart, layout.CipherLength, keys);
        byte[] buffer = new byte[EmbeddedZipStreamFormat.CopyBufferSize];
        long total = 0;

        while (total < layout.CipherLength)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int read = decrypt.Read(buffer, 0, buffer.Length);

            if (read <= 0)
            {
                return false;
            }

            total += read;
        }

        return decrypt.VerifyAuthenticationCode(layout.AuthenticationCode);
    }
}

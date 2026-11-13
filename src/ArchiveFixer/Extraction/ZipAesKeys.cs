using System;

namespace ArchiveFixer.Extraction
{
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
}

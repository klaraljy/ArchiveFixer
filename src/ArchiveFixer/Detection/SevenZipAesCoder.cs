using System;

namespace ArchiveFixer.Detection;

/// <summary>
/// **7z 加密判据的唯一出口**：AES-256 + SHA-256 的 coder ID（<c>06 F1 07 01</c>，头里按**大端**写）。
///
/// <para>7-Zip 源码 <c>CPP/7zip/Crypto/7zAes.h</c> 里只注册了这一个 AES 变体
/// （<c>k_AES = 0x06F10701</c>），所以这里是**精确匹配 4 字节**，⛔ 不做前缀 / 模糊匹配 ——
/// "看着像 AES 就算"正是这个项目最不许的猜。</para>
///
/// <para>整个 7z 判读里"这算不算加密"只问这一处；<see cref="SevenZipCoderReader"/> 负责把
/// coder 链读出来并调用它，⛔ 别处不许再写一遍 ID 比较。</para>
/// </summary>
internal static class SevenZipAesCoder
{
    /// <summary>**AES-256 + SHA-256 的 coder ID**（<c>06 F1 07 01</c>，大端写在头里）。</summary>
    internal const uint AesMethodId = 0x06F10701;

    /// <summary>
    /// 这个 coder ID 是不是 7-Zip 的 AES-256（<c>06 F1 07 01</c>，头里按**大端**写）。
    ///
    /// <para>精确匹配 4 字节，⛔ 不做前缀 / 模糊匹配 —— "看着像 AES 就算"就是猜。</para>
    /// </summary>
    internal static bool IsAesMethodId(ReadOnlySpan<byte> methodId)
    {
        if (methodId.Length != 4)
        {
            return false;
        }

        uint value = ((uint)methodId[0] << 24)
                     | ((uint)methodId[1] << 16)
                     | ((uint)methodId[2] << 8)
                     | methodId[3];

        return value == AesMethodId;
    }
}

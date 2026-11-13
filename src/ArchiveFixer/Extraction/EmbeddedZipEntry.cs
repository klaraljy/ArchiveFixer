using ArchiveFixer.Helpers;
using ArchiveFixer.Security;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 直接从源文件里读出来的一个条目。
    /// </summary>
    public sealed class EmbeddedZipEntry
    {
        /// <summary>归档里的原始名字（未做任何改写，方便用户回原始包里去核对）。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>清洗之后的相对路径（正斜杠换成平台分隔符，非法段已按 <see cref="FileNameHelper"/> 处理）。</summary>
        public string RelativePath { get; init; } = string.Empty;

        /// <summary>落盘路径。**调用方必须用这个值**，不要自己按名字拼（同一条目可能已被改名）。</summary>
        public string OutputPath { get; init; } = string.Empty;

        /// <summary>条目解压后的原始大小（以中央目录为准，不信本地头）。</summary>
        public long Size { get; init; }

        /// <summary>压缩后大小（写出进度的分母用的是 <see cref="Size"/>，这个只用于报告与自洽校验）。</summary>
        public long CompressedSize { get; init; }

        public bool IsDirectory { get; init; }

        /// <summary>压缩方法（0 = stored，8 = deflate）。AES 条目这里是**真方法**（解密之后再按它解压）。</summary>
        public int Method { get; init; }

        /// <summary>本地文件头相对 ZIP 起点的偏移（数据从哪里开始要从它往后数）。</summary>
        public long LocalHeaderOffset { get; init; }

        /// <summary>
        /// 中央目录声明的 CRC32（<c>PK\x01\x02</c> 偏移 +16）。
        /// 直读写入时逐条目计算并比对；AES 条目由认证码负责，不拿这个字段判损坏。
        /// </summary>
        public uint Crc32 { get; init; }

        /// <summary>
        /// AES 强度字节（1/2/3 = AES-128/192/256）；0 = 不是 AES 条目。
        ///
        /// <para>为什么把"是不是 AES"记在条目上：读数据时要用它决定
        /// "密文从哪里开始、有多长" —— 那份开销（盐 16 + 校验值 2 + 认证码 10 = 28 字节）
        /// 已经算在中央目录声明的压缩后大小里了。</para>
        /// </summary>
        public int AesStrength { get; init; }

        /// <summary>这条是不是加密条目（AES 或老式 ZipCrypto 都算）。</summary>
        public bool IsEncrypted { get; init; }

        /// <summary>AES 条目：密文总长（= 压缩后大小 − 28）。不是 AES 条目时为 0。</summary>
        public long CipherLength => AesStrength == 0 ? 0 : CompressedSize - ZipAesCrypto.OverheadLength;
    }
}

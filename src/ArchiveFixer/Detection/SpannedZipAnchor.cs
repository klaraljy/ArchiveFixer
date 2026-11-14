using System;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 分卷索引里的一条**位置锚点**：某一盘上、相对该盘起点的某个偏移处，**应当**有一个本地文件头，
    /// 而且它的文件名就是 <see cref="NameBytes"/>。
    ///
    /// <para>它从哪来：跨盘 zip 的**末片**里那份中央目录。中央目录的每一条记录都写着
    /// "这个文件的本地头在**第几盘**、离那一盘开头多少字节"（<c>disk number start</c> +
    /// <c>relative offset of local header</c>）—— 那是**归档自己**说的话，不是我们猜的。</para>
    /// </summary>
    public sealed class SpannedZipAnchor
    {
        /// <summary>盘号（**0 起**，与 EOCD 里的盘号同一个坐标系）。</summary>
        public int Disk { get; init; }

        /// <summary>相对**该盘起点**的偏移。</summary>
        public long RelativeOffset { get; init; }

        /// <summary>本地头里的文件名（**原始字节**：比字节，不比解码后的字符串 —— 免得编码差异把真锚点判掉）。</summary>
        public byte[] NameBytes { get; init; } = Array.Empty<byte>();

        /// <summary>给日志用的名字（尽力解码，仅供人读）。</summary>
        public string Name { get; init; } = string.Empty;
    }
}

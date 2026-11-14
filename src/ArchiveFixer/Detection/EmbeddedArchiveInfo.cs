namespace ArchiveFixer.Detection;

/// <summary>
/// "内嵌归档"的识别结果：文件尾部藏着一个完整的 ZIP，ZIP 之前还有别的数据。
/// </summary>
public sealed class EmbeddedArchiveInfo
{
    /// <summary>是否命中。</summary>
    public bool Found { get; init; }

    /// <summary>内嵌归档在文件中的起始偏移（前面那 N 字节"别的东西"的长度）。</summary>
    public long Offset { get; init; }

    /// <summary>
    /// 内嵌归档的**结束位置（不含）**：EOCD 结束 + ZIP 注释长度。
    ///
    /// 为什么不直接用文件末尾：真实资源包是"视频 + 完整 ZIP + EOCD 之后还有十几 KB 正常数据"
    /// （2026-09-24 用户机器上 5 个真文件实测：EOCD 距 EOF 14,350–17,424 字节，
    /// 那 14–17 KB 是打包格式本身的一部分，不是垃圾）。抠取必须按这个位置**截断**，
    /// 否则 EOCD 后面那些字节会被当成归档内容带出去 —— 多数读取器能忍，但并不保证。
    ///
    /// 0 表示"没有内嵌归档"；<see cref="Offset"/> == 0 时调用方一律按 EOF 处理（旧行为）。
    /// </summary>
    public long ArchiveEnd { get; init; }

    /// <summary>内嵌归档的格式。目前只会是 "ZIP"（7z / RAR 的目录结构不支持这种"偏移错位"的自洽校验）。</summary>
    public string Format { get; init; } = string.Empty;

    /// <summary>建议后缀。</summary>
    public string SuggestedExtension { get; init; } = string.Empty;

    /// <summary>ZIP 中央目录里声明的条目数。</summary>
    public int EntryCount { get; init; }

    /// <summary>
    /// 归档区间的字节数（<c>ArchiveEnd - Offset</c>）—— 也就是抠出来会得到多大的文件。
    /// 不是"从 Offset 到文件末尾"：见 <see cref="ArchiveEnd"/>。
    /// </summary>
    public long ArchiveLength { get; init; }

    /// <summary>给人看的中文说明，直接写进任务/日志。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// 这一片是**跨盘 ZIP 的最后一片**（用户 2026-10-01 真机：`222.zi删除p`）。
    ///
    /// <para>它与"内嵌归档"是**两种东西**，判据也完全不同：内嵌归档要求"ZIP 前面垫着别的数据"
    /// （所以必须有本地文件头、`delta &gt; 0`）；跨盘末片**没有本地文件头**（本地头都在第 1 片里），
    /// 它只有中央目录 + EOCD，靠 EOCD 的**盘号字段**说明"我是一片分卷"。</para>
    ///
    /// <para>⛔ 为 true 时 <see cref="Offset"/> 恒为 0（**不是**内嵌，不许按偏移抠）；
    /// 调用方据此落 `ZIP_SPANNED` 而不是"内嵌归档"。</para>
    /// </summary>
    public bool IsVolumePart { get; init; }
}

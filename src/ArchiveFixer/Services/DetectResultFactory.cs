using ArchiveFixer.Models;

namespace ArchiveFixer.Services;

/// <summary>
/// **识别结果模型的唯一构造出口**：<see cref="DetectResult"/> 的每一种形状只在这里组装。
///
/// <para>为什么值得单独一层：识别结论的字段集合与置信度口径是**约定**（见
/// <see cref="DetectResult"/> 上各属性的说明）—— 文件头直接命中给 100（TAR / ISO 这类弱特征给 90）、
/// 尾部推断出来的内嵌归档给 80、跨盘 ZIP 的一片给 90、认不出来给 0。把这些数字散在四五个方法里，
/// 迟早会出现"同一档结论在两条路上置信度不一样"，而下游（空间核算 / 后缀状态 / 汇总）读的正是它们。</para>
///
/// <para>⛔ 这里只组装结果，**不做任何判据**：是不是归档、加没加密、从哪个偏移抠，
/// 全部由调用方（<see cref="ArchiveFormatProbe"/> / <see cref="ArchiveContentIdentifier"/> /
/// <see cref="ArchiveEncryptionVerdict"/>）决定后作为参数传进来。</para>
/// </summary>
internal static class DetectResultFactory
{
    /// <summary>
    /// 认不出来 / 读不了时的那一份（置信度 0、不是归档、没有建议后缀）。
    ///
    /// <para><paramref name="message"/> 为空时用 <see cref="StatusText.UnknownFormat"/> 兜底
    /// （原文 <c>CreateUnknownResult</c> 的口径，一个字没改）。</para>
    ///
    /// <para><paramref name="headerHex"/>：**读到了文件头但认不出来**时要把那截证据带上
    /// （排查格式识别问题靠它）；连文件头都没读到的那几档传空串（默认）。</para>
    /// </summary>
    internal static DetectResult CreateUnknown(string? message, string headerHex = "") => new()
    {
        Format = "Unknown",
        SuggestedExtension = string.Empty,
        IsArchive = false,
        IsKnownFormat = false,
        IsProbablyEncrypted = false,
        Message = message ?? StatusText.UnknownFormat,
        HeaderHex = headerHex,
        Confidence = 0
    };

    /// <summary>
    /// 文件头魔数**直接命中**的那一份：已知格式 + 归档 + 置信度由探测方按特征强弱给
    /// （明确魔数 100，TAR / ISO 这类弱特征 90）。
    /// </summary>
    internal static DetectResult CreateHeaderMatch(
        string format,
        string suggestedExtension,
        string message,
        string headerHex,
        int confidence) => new()
        {
            Format = format,
            SuggestedExtension = suggestedExtension,
            IsArchive = true,
            IsKnownFormat = true,
            IsProbablyEncrypted = false,
            Message = message,
            HeaderHex = headerHex,
            Confidence = confidence
        };

    /// <summary>
    /// **跨盘 ZIP 的一片**（不是内嵌归档，置信度 90）。
    ///
    /// <para>偏移**恒为 0**：这一片没有可抠出来的独立区间，设了反而会让下游去抠一个不存在的区间
    /// （分叉的理由见 <see cref="ArchiveContentIdentifier"/> 里那段"跨盘 ZIP 的最后一片"）。</para>
    /// </summary>
    internal static DetectResult CreateSpannedZipPart(string message, string headerHex) => new()
    {
        Format = "ZIP_SPANNED",
        SuggestedExtension = ".zip",
        IsArchive = true,
        IsKnownFormat = true,
        IsProbablyEncrypted = false,
        Message = message,
        HeaderHex = headerHex,
        Confidence = 90,
        EmbeddedArchiveOffset = 0,
        EmbeddedArchiveEnd = 0,
        EmbeddedDirectReadSupported = false,
        EmbeddedDirectReadReason = "这一片是跨盘 ZIP 的一片，不是内嵌归档：没有可抠出来的独立区间"
    };

    /// <summary>
    /// 尾部推出来的**内嵌 ZIP**（"双面文件"，置信度 80）：带抠取区间与直读探查结论。
    ///
    /// <para>加密标志由调用方给（判据在 <see cref="ArchiveEncryptionVerdict"/> 与直读探针那边），
    /// ⛔ 这里**不**写死 false —— 写死就是"误报没加密"。</para>
    /// </summary>
    internal static DetectResult CreateEmbeddedZip(
        string message,
        string headerHex,
        long offset,
        long end,
        bool isProbablyEncrypted,
        bool directReadSupported,
        string directReadReason) => new()
        {
            Format = "ZIP",
            SuggestedExtension = ".zip",
            IsArchive = true,
            IsKnownFormat = true,
            IsProbablyEncrypted = isProbablyEncrypted,
            Message = message,
            HeaderHex = headerHex,
            Confidence = 80,
            EmbeddedArchiveOffset = offset,
            EmbeddedArchiveEnd = end,
            EmbeddedDirectReadSupported = directReadSupported,
            EmbeddedDirectReadReason = directReadReason
        };

    /// <summary>
    /// 尾部 / 顺序扫定位到的 **RAR / 7z 内嵌段**（置信度 80）。
    ///
    /// <para>两点如实说明：①终点取文件末尾 —— RAR / 7z 没有 ZIP 那种能算出精确终点的结构；
    /// ②直读**一定不支持** —— 内置直读器只会解 ZIP，RAR / 7z 必须走"抠取 + 引擎"。</para>
    /// </summary>
    internal static DetectResult CreateTailArchive(
        string format,
        string suggestedExtension,
        string message,
        string headerHex,
        long offset,
        long end) => new()
        {
            Format = format,
            SuggestedExtension = suggestedExtension,
            IsArchive = true,
            IsKnownFormat = true,
            IsProbablyEncrypted = false,
            Message = message,
            HeaderHex = headerHex,
            Confidence = 80,
            EmbeddedArchiveOffset = offset,
            EmbeddedArchiveEnd = end,
            EmbeddedDirectReadSupported = false,
            EmbeddedDirectReadReason = "内置直读器只解 ZIP；RAR / 7z 这条走「抠取 + 引擎」"
        };
}

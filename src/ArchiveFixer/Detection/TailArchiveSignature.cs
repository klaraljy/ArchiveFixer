namespace ArchiveFixer.Detection;

/// <summary>扫出来的归档签名（RAR / 7z 这类只能凭魔数定位起点的格式）。</summary>
public sealed class TailArchiveSignature
{
    /// <summary>归档在文件里的起始偏移（&gt; 0）。</summary>
    public long Offset { get; init; }

    /// <summary>格式名（与 <c>DetectResult.Format</c> 同一套写法：<c>RAR4</c> / <c>RAR5</c> / <c>7Z</c>）。</summary>
    public string Format { get; init; } = string.Empty;

    /// <summary>建议后缀（<c>.rar</c> / <c>.7z</c>）。</summary>
    public string SuggestedExtension { get; init; } = string.Empty;

    /// <summary>给人看的一句话。</summary>
    public string Describe() =>
        $"尾部内嵌归档（{Format}，偏移 {Offset:N0}）—— 需要先抠出 [偏移, 文件末尾) 再交给引擎解";
}

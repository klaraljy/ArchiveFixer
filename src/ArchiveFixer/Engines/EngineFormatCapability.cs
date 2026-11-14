namespace ArchiveFixer.Engines;

/// <summary>
/// 某个引擎对某个格式的**细粒度**能力。
///
/// 为什么不能只写"支持 ZIP"（设计.md §十八）：
/// 同一个格式不同引擎、甚至同一引擎不同版本，能力都不一样 ——
/// 比如"能列出但解不开 AES 加密"、"支持大文件但不支持分卷"。
/// 只写一个 bool 会让引擎选择变成一个说不清的猜谜。
/// </summary>
public sealed class EngineFormatCapability
{
    /// <summary>格式名，与 <c>DetectResult.Format</c> 同一套口径：ZIP / 7Z / RAR4 / RAR5 / TAR ...</summary>
    public string Format { get; init; } = string.Empty;

    public bool CanList { get; init; } = true;

    public bool CanTest { get; init; } = true;

    public bool CanExtract { get; init; } = true;

    public bool SupportsPassword { get; init; }

    public bool SupportsAes { get; init; }

    public bool SupportsMultiVolume { get; init; }

    public bool SupportsLargeFiles { get; init; } = true;

    public bool PreservesUnicodeNames { get; init; } = true;

    public bool SupportsStreaming { get; init; }
}

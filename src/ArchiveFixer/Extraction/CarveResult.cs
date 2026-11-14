namespace ArchiveFixer.Extraction;

/// <summary>
/// 抠出内嵌归档的结果。
/// </summary>
public sealed class CarveResult
{
    /// <summary>是否成功。</summary>
    public bool Success { get; init; }

    /// <summary>实际写出的文件路径。目标已存在时会改名，所以**必须**用这个值，不要用调用时传进来的路径。</summary>
    public string OutputPath { get; init; } = string.Empty;

    /// <summary>写出的字节数。</summary>
    public long BytesWritten { get; init; }

    /// <summary>给人看的中文说明（失败时就是失败原因）。</summary>
    public string Message { get; init; } = string.Empty;
}

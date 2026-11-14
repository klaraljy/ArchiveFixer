namespace ArchiveFixer.Extraction;

/// <summary>最终层输入片的直接产出者（用户源包与程序过程物分开）。</summary>
public sealed class ContentGroupFinalLayerProducer
{
    public string TaskFileName { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public bool IsSource { get; init; }
}

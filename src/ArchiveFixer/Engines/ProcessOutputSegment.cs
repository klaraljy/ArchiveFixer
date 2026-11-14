namespace ArchiveFixer.Engines;

/// <summary>从外部进程输出流里切出来的一段文本。</summary>
public readonly struct ProcessOutputSegment
{
    public ProcessOutputSegment(string text, ProcessOutputSegmentKind kind)
    {
        Text = text ?? string.Empty;
        Kind = kind;
    }

    public string Text { get; }

    public ProcessOutputSegmentKind Kind { get; }

    public override string ToString()
    {
        return Kind == ProcessOutputSegmentKind.Line ? $"[行]{Text}" : $"[片段]{Text}";
    }
}

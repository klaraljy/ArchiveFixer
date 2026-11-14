namespace ArchiveFixer.Packing;

/// <summary>一个打包步骤的进度（只带"到哪了"）。</summary>
public sealed class PackStepProgress
{
    /// <summary>百分比未知（还在扫描 / 这一行没给百分比）。</summary>
    public const int UnknownPercent = -1;

    /// <summary>0–100；未知时是 <see cref="UnknownPercent"/>。</summary>
    public int Percent { get; init; } = UnknownPercent;

    /// <summary>当前条目 / 状态行（引擎给什么就是什么）。</summary>
    public string Detail { get; init; } = string.Empty;
}

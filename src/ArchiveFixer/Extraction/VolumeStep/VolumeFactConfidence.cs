namespace ArchiveFixer.Extraction.VolumeStep;

/// <summary>这条片事实有多硬：内容读出 &gt; 位置/名字推断 &gt; 判不出。</summary>
public enum VolumeFactConfidence
{
    Unknown = 0,
    Inferred = 1,
    Content = 2
}


namespace ArchiveFixer.Extraction
{
    /// <summary>链尾清理步骤；顺序就是唯一删除顺序。</summary>
    public enum ChainCleanupStep
    {
        CollectProcessArtifacts,
        ApplyRestHandling,
        SweepPassThrough,
        ProcessUserSource
    }
}


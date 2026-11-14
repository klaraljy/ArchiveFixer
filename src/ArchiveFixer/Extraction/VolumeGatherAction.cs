namespace ArchiveFixer.Extraction;

/// <summary>收拢决策：这一次要不要把这一组搬到入口包那一层。</summary>
public enum VolumeGatherAction
{
    /// <summary>不搬（一个字节都不动）。</summary>
    Leave,

    /// <summary>整组搬到入口包那一层。</summary>
    Gather
}

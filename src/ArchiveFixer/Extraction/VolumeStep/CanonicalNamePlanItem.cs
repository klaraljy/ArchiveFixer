namespace ArchiveFixer.Extraction.VolumeStep
{
    /// <summary>一条“片 → 规范名”。<see cref="Conflict"/> 非空 = 目标名被占/不可用（只跳过，不覆盖）。</summary>
    public sealed record CanonicalNamePlanItem(
        string PiecePath,
        string TargetName,
        bool IsEntry,
        string Conflict = "");
}

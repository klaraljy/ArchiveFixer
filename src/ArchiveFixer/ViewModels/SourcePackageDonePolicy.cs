namespace ArchiveFixer.ViewModels
{
    /// <summary>删源包完成后的处理档（从 ExtractionCoordinator 搬出，供调用方与独立类共用）。</summary>
    internal enum SourcePackageDonePolicy
    {
        Respect = 0,
        AllowRecovery = 1
    }
}


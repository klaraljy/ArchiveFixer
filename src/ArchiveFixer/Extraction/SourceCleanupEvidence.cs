namespace ArchiveFixer.Extraction
{
    /// <summary>删源裁决所依据的证据种类（⛔ 不再用 bool verified 绕过）。</summary>
    public enum SourceCleanupEvidence
    {
        None = 0,
        ManifestCrossChecked = 1,
        LayerCompleteness = 2
    }
}


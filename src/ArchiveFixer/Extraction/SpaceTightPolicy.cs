namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 批首钉死的空间策略。所有与空间不足相关的分支只读这一份事实，
    /// 不再各自解释 _spaceTightThisBatch 的零散布尔值。
    /// </summary>
    public sealed class SpaceTightPolicy
    {
        public bool Enabled { get; init; }

        public bool KeepSource { get; init; }

        public bool SourceOnLayerSuccessDeletes { get; init; }

        public bool LastProcessOnFailureKeeps => true;

        public bool PartialContentOnFailureDrops => Enabled;

        public bool RestOnFailureKeepsLastProcess => true;

        public bool SkipUnfitAtStart => Enabled;

        public bool CrossVolumeEntryCopies => false;

        public static SpaceTightPolicy Normal { get; } = new();

        public static SpaceTightPolicy ForBatch(bool spaceTight, bool keepSource)
        {
            return new SpaceTightPolicy
            {
                Enabled = spaceTight,
                KeepSource = spaceTight && keepSource,
                SourceOnLayerSuccessDeletes = spaceTight && !keepSource
            };
        }
    }
}


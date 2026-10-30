namespace ArchiveFixer.Models
{
    /// <summary>
    /// 手动操作弹窗里“目录分布”的一行。只读展示，不参与任何删除/解压目标判定。
    /// </summary>
    public sealed class ManualDistributionEntry
    {
        public string Name { get; init; } = string.Empty;

        public string Kind { get; init; } = string.Empty;

        public string SizeText { get; init; } = string.Empty;

        public string ModifiedText { get; init; } = string.Empty;

        public string FullPath { get; init; } = string.Empty;
    }
}

namespace ArchiveFixer.Extraction
{
    /// <summary>最终内容分卷组的失败种类；只由组事实层写入。</summary>
    public enum FinalGroupFailureKind
    {
        None = 0,

        /// <summary>组不完整（缺片）：链失败，源包不删。</summary>
        Incomplete = 1,

        /// <summary>组完整但数据损坏：链失败，源包不删。</summary>
        Corrupted = 2
    }
}

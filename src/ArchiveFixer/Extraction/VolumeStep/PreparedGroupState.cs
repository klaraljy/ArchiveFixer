namespace ArchiveFixer.Extraction
{
    /// <summary>**一组已经由族专属 resolver 解析完的结论**（共用骨架）。</summary>
    public enum PreparedGroupState
    {
        /// <summary>可证完整（标准名已建好）。</summary>
        Complete = 0,

        /// <summary>缺片（只有内容/名字能点名时才允许）。</summary>
        Missing = 1,

        /// <summary>判不出（证据不足/冲突）。</summary>
        Unknown = 2,

        /// <summary>结构矛盾（数量/槽位/大小/混族）。</summary>
        SizeAnomaly = 3
    }
}

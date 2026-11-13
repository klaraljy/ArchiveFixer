namespace ArchiveFixer.Storage
{
    /// <summary>空间判断的机器三态；Unknown 绝不等于够。</summary>
    public enum SpaceDecisionState
    {
        Unknown = 0,
        Enough = 1,
        NotEnough = 2
    }
}

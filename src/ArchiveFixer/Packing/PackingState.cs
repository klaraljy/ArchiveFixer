namespace ArchiveFixer.Packing
{
    /// <summary>打包的结论状态。</summary>
    public enum PackingState
    {
        /// <summary>失败（包括"产物校验对不上"）。</summary>
        Failed = 0,

        /// <summary>用户取消。</summary>
        Cancelled = 1,

        /// <summary>成功（外层容器做成了，或者按要求"不做外层"只出了 7z 分卷）。</summary>
        Succeeded = 2
    }
}

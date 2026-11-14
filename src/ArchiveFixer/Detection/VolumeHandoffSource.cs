namespace ArchiveFixer.Detection
{
    /// <summary>这一份文件是从哪来的（交接单要用它决定"搬还是只建链接"）。</summary>
    public enum VolumeHandoffSource
    {
        /// <summary>用户导入的原件（源目录只读：只许建零字节硬链接）。</summary>
        UserImported = 0,

        /// <summary>本链产出的片（同卷搬、跨卷建链接）。</summary>
        ChainProduced = 1
    }
}
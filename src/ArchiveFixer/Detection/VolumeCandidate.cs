namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 输入：一个候选文件。
    ///
    /// 只带"路径 + 大小"两个事实：分卷组识别在本模块里是**纯命名**判定，
    /// 格式探测、卷内结构之类的证据由调用方（扫描/识别服务）在别处提供。
    /// </summary>
    public sealed class VolumeCandidate
    {
        /// <summary>完整路径（调用方保证非空）。也允许只给文件名，本模块不做全路径规范化的假设。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>
        /// 字节数，未知填 -1。
        ///
        /// 目前**不参与任何判定**，只是原样带进 <see cref="VolumeGroup.Volumes"/>。
        /// 留它的原因是 AGENTS.md §9.3 / 设计.md §十六 要求"不要只凭 .001/.002 就认定是一组"，
        /// 将来补"大小是否一致、是否首卷带主头"的复核时要用到，但不该在这个纯命名模块里实现。
        /// </summary>
        public long Size { get; init; } = -1;
    }
}

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 暂存区里的一个条目（文件或目录），路径**相对暂存根**。
    ///
    /// <para>
    /// 这是定稿规划唯一的输入形状：规划是纯函数，**不遍历磁盘** —— 暂存树由刚跑完解压的
    /// 调用方描述出来，规划只做结构与字符串推理。这样它才能在真搬家之前就把"最终长什么样"算清楚
    /// （用户要的"从尾巴倒着想"）。
    /// </para>
    /// </summary>
    public sealed class StagedEntry
    {
        /// <summary>相对暂存根的路径，<c>\</c> 或 <c>/</c> 都认（例：<c>out\666\a.mp4</c>）。</summary>
        public string RelativePath { get; init; } = string.Empty;

        /// <summary>文件字节数；目录填 0（只用来统计"其余物总大小"，契约 §3.2）。</summary>
        public long Size { get; init; }

        /// <summary>是不是目录。</summary>
        public bool IsDirectory { get; init; }

        /// <summary>
        /// 是不是"其余物"（内层归档、分卷、抠出来的过程物……）。
        ///
        /// <para>
        /// **必须由调用方显式标**：只有跑过暂存阶段的人知道哪些是中间产物。
        /// 规划器按后缀猜是不行的 —— 内容物里本来就可能有一个用户要的 <c>.zip</c>，
        /// 猜错就把用户的东西扔进了 <c>其余物</c>。
        /// </para>
        /// <para>
        /// 另一种等价办法是不标、改用 <c>Plan(..., contentRoot: "out")</c>：
        /// 内容物根之外的一切都算其余物。
        /// </para>
        /// </summary>
        public bool IsProcessArtifact { get; init; }
    }
}


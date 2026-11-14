namespace ArchiveFixer.Security
{
    /// <summary>一次"解压前路径长度预检"的结论。全部字段都是"算出来的事实"，不预测成败。</summary>
    public sealed class PathLengthPreflightResult
    {
        /// <summary>是否存在"可能因为路径过长而失败"的风险。</summary>
        public bool HasWarning { get; init; }

        /// <summary>中文提示，可直接进日志 / 任务详情（没有风险时为空字符串）。</summary>
        public string Warning { get; init; } = string.Empty;

        /// <summary>最长的那一条归档内相对路径（原样，便于用户回包里核对）。</summary>
        public string LongestRelativePath { get; init; } = string.Empty;

        /// <summary>最长相对路径的字符数。</summary>
        public int LongestRelativePathLength { get; init; }

        /// <summary>目标根目录的字符数（没给目标根时为 0）。</summary>
        public int TargetRootLength { get; init; }

        /// <summary>最长条目拼上目标根之后的完整路径字符数。</summary>
        public int LongestFullPathLength { get; init; }

        /// <summary>判定为"过长"的条目数（目录条目也算：目录建不出来时它下面的文件全落不了盘）。</summary>
        public int OverLimitEntryCount { get; init; }
    }
}

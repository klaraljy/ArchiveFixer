namespace ArchiveFixer.Security
{
    /// <summary>一次解压前预算检查的结论。</summary>
    public sealed class BudgetCheckResult
    {
        public bool Allowed { get; init; }

        /// <summary>中文说明，直接给用户看（拒绝原因里一定带具体数字）。</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>本次判断用的解压后总大小估算（拿不到清单时为 0）。</summary>
        public long EstimatedTotalSize { get; init; }

        /// <summary>展开比；压缩包体积未知或 &lt;= 0 时为 0（表示"这项没判"）。</summary>
        public double ExpansionRatio { get; init; }

        /// <summary>目标盘可用字节数；没给目标目录或取不到时为 null。</summary>
        public long? FreeSpaceBytes { get; init; }

        /// <summary>
        /// 路径长度预警（没有风险时为空字符串）。
        ///
        /// 它是**提示**、不参与 <see cref="Allowed"/> 的判断：路径过长时 7-Zip 可能只解出一部分，
        /// 但"要不要继续"仍然由解压结果决定（部分成功会落到 <c>PartiallyCompleted</c>，不会显示成成功）。
        /// 由 <see cref="ResourceBudget.CheckBeforeExtract"/> 从**同一份 list** 算出 —— 不额外跑 7z。
        /// </summary>
        public string PathLengthWarning { get; init; } = string.Empty;

        /// <summary>最长条目的完整路径字符数；没有清单时为 0。</summary>
        public int LongestPathLength { get; init; }

        /// <summary>
        /// 这次拒绝是不是**因为目标盘空间不足**（而不是单文件 / 文件数 / 总量 / 展开比超限）。
        ///
        /// <para>
        /// 为什么要单独标出来：两者对用户的含义完全不同 —— 空间不足是"清一下盘 / 换个盘就能继续"，
        /// 而"超出预算上限"是"这个包本身太大 / 疑似压缩炸弹"。调用方据此落成不同的状态
        /// （<c>StatusText.DiskSpaceInsufficient</c> 而不是「解压失败」），用户才知道该去动什么。
        /// </para>
        /// </summary>
        public bool IsSpaceShortage { get; init; }
    }
}

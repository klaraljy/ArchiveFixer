namespace ArchiveFixer.Models
{    /// <summary>
     /// 任务的**机器可判终态**（见 <see cref="ArchiveTask.Outcome"/>）。
     ///
     /// <para>
     /// 只有"删源 / 搬源 / 续解下一层 / 危险模式删其余物"这几条**不可逆或会放大**的裁决读它。
     /// 中文文案（<c>StatusText</c>）继续只做显示，两套东西各管一头，谁也不许拿另一头当判据。
     /// </para>
     /// </summary>
    public enum TaskOutcome
    {
        /// <summary>还没有结论（含"在定稿之前就结束"的那些分支）。所有门都不放行这一档。</summary>
        Pending = 0,

        /// <summary>产物已定稿、输出校验通过、源包处理也没出问题：**唯一**允许继续/删除的档。</summary>
        Succeeded = 1,

        /// <summary>做了一部分（源包没能搬进其余物 / 递归停在需要用户决定的层）。不得当成成功。</summary>
        PartiallyCompleted = 2,

        /// <summary>失败（含校验未通过 / 越界 / 超预算 / 密码试完）。</summary>
        Failed = 3,

        /// <summary>用户取消。</summary>
        Cancelled = 4,

        /// <summary>跳过（不是压缩包 / 同名冲突按用户选择跳过）。</summary>
        Skipped = 5
    }
}

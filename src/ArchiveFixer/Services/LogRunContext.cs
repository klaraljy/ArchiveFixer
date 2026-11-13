namespace ArchiveFixer.Services
{
    /// <summary>这一行的来源上下文（批次 / 轮次 / 样本 / 阶段 / 任务 / 层号）。</summary>
    public sealed record LogRunContext
    {
        public string Batch { get; init; } = string.Empty;

        /// <summary>第几轮（0 = 还没进轮次循环）。</summary>
        public int Round { get; init; }

        /// <summary>大测试的第几份原包（`1111` / `2222` …）。</summary>
        public string Sample { get; init; } = string.Empty;

        /// <summary>阶段：第一大步 / 第二大步 / 接片 / 收卷 / 落点 / 删除 / 引擎调用。</summary>
        public string Stage { get; init; } = string.Empty;

        public string Task { get; init; } = string.Empty;

        /// <summary>层号（-1 = 不适用）。</summary>
        public int Layer { get; init; } = -1;
    }
}


namespace ArchiveFixer.Storage
{
    /// <summary>
    /// 记忆里的一条密码（值 / 来源 / 启用状态 / 备注）。**顺序即列表顺序**。
    ///
    /// <para>⚠ <see cref="Value"/> 是明文，只允许存在于内存与**加密后的**记忆文件里：
    /// 绝不进日志、绝不进报告（AGENTS.md §8 隐私红线）。</para>
    /// </summary>
    public sealed class PasswordListEntry
    {
        public string Value { get; set; } = string.Empty;

        public string Source { get; set; } = "ManualList";

        public bool Enabled { get; set; } = true;

        public string Remark { get; set; } = string.Empty;
    }
}


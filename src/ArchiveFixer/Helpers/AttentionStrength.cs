namespace ArchiveFixer.Helpers
{
    /// <summary>需要引起注意的强度（决定响几声、闪多久）。</summary>
    public enum AttentionStrength
    {
        /// <summary>普通提示：窗口没到前台时闪任务栏 + 响一声。</summary>
        Normal,

        /// <summary>必须有人处理的提示（警告 / 错误 / 询问 / 密码）：窗口没到前台时闪任务栏 + 响一声。</summary>
        Strong
    }
}

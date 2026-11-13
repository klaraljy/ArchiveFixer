namespace ArchiveFixer.Extraction
{
    /// <summary>一个第一层子目录被跳过的原因。</summary>
    public enum EmptyFolderSkipReason
    {
        /// <summary>没有跳过。</summary>
        None = 0,

        /// <summary>没有传"用户已确认"标志 —— 默认不删（结果里会列出本来会删哪些）。</summary>
        NotConfirmed,

        /// <summary>任意层级存在文件（含空文件），必须保留。</summary>
        HasFiles,

        /// <summary>子树里有符号链接 / 联接点，不跟着删。</summary>
        IsReparsePoint,

        /// <summary>读不到目录内容（权限不足 / 被占用），无法确认"没有文件"，保守保留。</summary>
        NoPermission,

        /// <summary>越出了给定的根目录（容器内校验没过）。</summary>
        OutsideRoot,

        /// <summary>删除动作失败（被占用 / 回收站不可用 / 系统拒绝）。</summary>
        DeleteFailed
    }
}

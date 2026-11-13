namespace ArchiveFixer.Services
{
    /// <summary>一次同名冲突的处置方式（用户的答案，或由 <see cref="ConflictActions"/> 的档位直接推出）。</summary>
    public enum ConflictChoice
    {
        /// <summary>跳过这一项：已存在的文件一个字节都不动。</summary>
        Skip = 0,

        /// <summary>覆盖这一项：走"先挪到临时名 → 落位 → 再删"两阶段（不变量 3）。</summary>
        Overwrite = 1,

        /// <summary>自动重命名这一项：新产物落成 <c>名字(1)</c>，绝不覆盖。</summary>
        AutoRename = 2,

        /// <summary>取消本批（等价于既有的"停止后续 + 取消当前"取消语义）。</summary>
        CancelBatch = 3
    }
}


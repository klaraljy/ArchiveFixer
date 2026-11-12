namespace ArchiveFixer.Models
{    /// <summary>
     /// 终态的机器可判原因（只解释“为什么”，不替代 <see cref="TaskOutcome"/>）。
     /// </summary>
    public enum TaskReason
    {
        None = 0,
        VolumeMissing = 1,
        VerificationFailed = 2,
        EngineFailed = 3,
        PasswordExhausted = 4,
        UnsafePath = 5,
        SourceChanged = 6,
        Skipped = 7,
        Cancelled = 8,
        Other = 9,

        /// <summary>第二大步最终内容分卷组不完整；只用于最终组，不替代普通 VolumeMissing。</summary>
        FinalGroupIncomplete = 10,

        /// <summary>第二大步最终内容分卷组完整但数据损坏；与密码耗尽分开。</summary>
        FinalGroupCorrupted = 11
    }
}

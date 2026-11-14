namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 一次加密判读的结论（**三态 + 一档细分**）—— RAR / ZIP / 7z 三个判据器**共用这一套词表**。
    ///
    /// <para><b>为什么要有它</b>：三个格式各有各的读法（RAR 看块 flags、ZIP 看中央目录的通用位标志、
    /// 7z 看 coder 链里有没有 AES），但"这一包加没加密"对上层只允许有**一种说法** ——
    /// 各写一套枚举，出口就得来回映射，映射迟早漂移（AGENTS.md §9.5：同一件事的真值只允许有一个出口）。</para>
    ///
    /// <para>⚠ 刻意不用 bool：<see cref="Unknown"/> 与 <see cref="NotEncrypted"/> 在**报告口径**上
    /// 完全等价（都不报"加密"），但在**证据口径**上完全不同 —— 前者是"没读到 / 读不懂"，
    /// 后者是"走完了整条结构，没有任何加密标志"。把这个区分丢掉，以后排障时就没有办法回答
    /// "这一包到底是没加密、还是我们没看明白"（用户 2026-09-29 那次 RAR 分卷事故就是栽在
    /// "试开没成立"与"真的不是"混在一起上）。</para>
    /// </summary>
    public enum ArchiveEncryptionState
    {
        /// <summary>**不知道**：头被截断、布局与格式说明不符、超出读取预算 —— ⛔ 绝不猜成"没加密"。</summary>
        Unknown = 0,

        /// <summary>走完了结构，没有任何加密标志。</summary>
        NotEncrypted = 1,

        /// <summary>**只有文件数据加密**（WinRAR 的 <c>-p</c> / ZIP 的通用位标志 bit0 / 7z 的 AES coder）。</summary>
        DataEncrypted = 2,

        /// <summary>**连头都加密**（WinRAR 的 <c>-hp</c> / 7z 的 <c>-mhe</c>）：后续头都是密文，连条目名都读不出来。</summary>
        HeadersEncrypted = 3
    }
}

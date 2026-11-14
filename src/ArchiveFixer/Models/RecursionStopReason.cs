namespace ArchiveFixer.Models;

/// <summary>为什么会停。<b>不要</b>把它压成"成功/失败"两态：这里的每一项都对应不同的用户动作。</summary>
public enum RecursionStopReason
{
    None,

    /// <summary>一路解到底（或按用户选择的模式正常收尾）。</summary>
    Completed,

    /// <summary>出现多个内层归档，等用户拍板。</summary>
    NeedsDecision,

    /// <summary>
    /// 第 1 层起出现多分支：不再问第二遍（问过一次了），停在这一层。
    ///
    /// 与 <see cref="NeedsDecision"/> 分开、而不复用它的理由：两者的用户动作不同 ——
    /// NeedsDecision 是"等你回答"，这一条是"已经按你的选择做完了，剩下的分支没展开"。
    /// 混成一个会让"已完成"和"等你决定"分不清，用户会以为拿到的是最终数据。
    /// </summary>
    BranchNotExpanded,

    MaxDepthReached,

    MaxTotalFilesReached,

    MaxTotalSizeReached,

    /// <summary>
    /// 本层清单里最大的那个条目超过单文件上限（不变量 8 的"单文件"那一条）。
    ///
    /// <para>与 <see cref="MaxTotalSizeReached"/> 同一档处置：**不是"包坏了"**，而是撞上了程序的安全上限，
    /// 用户要动的是⑥设置「安全上限」（或先看看这个包本身是不是有问题）。</para>
    /// </summary>
    MaxSingleFileSizeReached,

    /// <summary>本层展开比超限，疑似压缩炸弹。</summary>
    ExpansionRatioExceeded,

    TooManyInnerArchives,

    /// <summary>候选还有，但已经试满 MaxPasswordAttemptsPerLayer。**不是**密码错误。</summary>
    PasswordAttemptsExceeded,

    /// <summary>
    /// 解压途中**磁盘写满**（引擎报 <see cref="ArchiveFixer.Engines.EngineErrorTypes.NoDiskSpace"/>）。
    ///
    /// <para>为什么要单列一条（用户 2026-09-27 真机：递归内层包撞空间不足，批末诊断却归到"其他"）：
    /// 归档本身没有任何问题，用户要做的是**清空间 / 换盘**，而不是去怀疑包坏了或改权限 ——
    /// 把它压在 <see cref="EngineFailed"/> 里，用户就看不到正确方向。</para>
    /// </summary>
    DiskSpaceInsufficient,

    /// <summary>候选全试完了都不对。</summary>
    WrongPassword,

    /// <summary>
    /// **文件名已加密**（7z `-mhe` / RAR `-hp`）：这一层从头到尾**就没列出过清单**，
    /// 而引擎在列目录那一步说的是"加密头"（用户 2026-10-05 拍板：递归路也要与单层路同结论）。
    ///
    /// <para>为什么必须与 <see cref="WrongPassword"/> 分开：用户要做的动作完全不同 ——
    /// 密码错误是"去核对 / 补密码本"，而这一档是"**先给它一个密码，它才肯把内容清单给你看**"。
    /// 判据与单层路径同一套（列目录失败 + 引擎给的结构化错误类型是
    /// <see cref="ArchiveFixer.Engines.EngineErrorTypes.EncryptedHeaders"/>，⛔ 不比中文）；
    /// ⛔ 只要**任何一个候选成功列出过清单**，这一档就不成立（那时失败在解压阶段，
    /// 结论照旧走密码错误 / 两义 / 损坏）。</para>
    /// </summary>
    EncryptedHeaders,

    Corrupted,

    /// <summary>
    /// **两义**：引擎同一句话里既说"密码不对"又说"数据坏了"，谁也单独定不了原因（用户 2026-09-30 真机）。
    ///
    /// <para>它必须与 <see cref="Corrupted"/> 和 <see cref="WrongPassword"/> 都分开：
    /// 折成 Corrupted ⇒ 结论断言"文件损坏"（用户被指去重新下载，而可能只是密码没试对）；
    /// 折成 WrongPassword ⇒ 把真损坏说成密码错（用户去反复核对没写错的密码本）。
    /// 与 7-Zip 侧同一口径：那句 <c>CRC Failed in encrypted file. Wrong password?</c>
    /// 从不当成"已损坏"（见 <c>Item37SafetyTests</c>）。</para>
    /// </summary>
    PasswordOrCorrupted,

    /// <summary>条目路径不安全（预检拦下），本层不落盘。</summary>
    UnsafeEntry,

    /// <summary>
    /// 源文件发生了变化（不变量 11）：本层**一个字节都没解**，本批到此为止。
    ///
    /// <para>与 <see cref="EngineFailed"/> 分开：引擎压根没被调用，这不是"引擎解不开"，
    /// 而是"手上这份识别结果已经不对应这个文件了"。用户要做的事也不同 ——
    /// 重新扫描后再处理，而不是去怀疑包坏了或换个引擎。</para>
    /// </summary>
    SourceChanged,

    /// <summary>
    /// **分卷缺失（免试那一档）**：这一组凑不齐，所以**一次引擎调用都没做**（用户 2026-10-05 拍板：
    /// 「要不然你在分开了你还会继续解压单独的001」）。
    ///
    /// <para>与 <see cref="EngineFailed"/> 分开的理由：用户要做的动作完全不同 ——
    /// EngineFailed 指向"查包 / 换引擎"，而这一档是"把同一组分卷凑到同一个目录里再来"。
    /// 判据只有一条硬证据（7z 起始头自述的整包字节数对不上），见
    /// <c>VolumeNameRepair.ResolveCrossLayerVolumeGather</c>；⛔ 其他族本轮不做这一档。</para>
    /// </summary>
    MissingVolume,

    UserCancelled,

    EngineFailed
}

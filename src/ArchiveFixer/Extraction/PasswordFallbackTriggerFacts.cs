namespace ArchiveFixer.Extraction;

/// <summary>
/// **换引擎再试一遍**的触发判据（用户 2026-10-05 拍板"补"；真机现场见 AGENTS.md §11.5 最后一条）。
///
/// <para><b>真机事实</b>：同一个包（真视频 + 尾部一个 WinZip AES / 压缩法 99 的 ZIP）、同一条密码 ——
/// WinRAR 6.11 退出码 0、两卷原样解出；内置 7-Zip 26.03 退出码 2、<c>Wrong password</c>、一个字节都没出来。
/// 而候选循环**只用 7-Zip**，<c>WrongPassword</c> 又落在"不换引擎"那一档 ⇒ 程序把
/// "这个引擎吃不下这个 ZIP"如实报成了「达到密码尝试上限」。用户自己用 WinRAR 十秒就能打开。</para>
///
/// <para><b>本判据管什么</b>：候选循环已经**跑完**（候选全试完 / 撞上每层上限）、失败是**密码类**、
/// 这一层的归档**确实加密**、本机**有** <c>WinRAR.exe</c> ⇒ 换 WinRAR 把**同一批候选**再试一遍。
/// ⛔ 正常路径一个字不改：循环**内部**遇到 <c>WrongPassword</c> 照旧换下一个候选、照旧不换引擎（省时间）。</para>
///
/// <para>⛔ 纯判据（本类前半部分）与执行体（<see cref="PasswordEngineFallback.TryAsync"/>）分开：
/// 判据能被单测直接喂事实，不必真装一个 WinRAR。</para>
/// </summary>
public sealed class PasswordFallbackTriggerFacts
{
    /// <summary>这一批已经解开了（有候选成功）⇒ ⛔ 不兜底。</summary>
    public bool AlreadySucceeded { get; init; }

    /// <summary>
    /// 这次失败是**密码类**：结构化错误类型是 <c>WrongPassword</c> / <c>NeedPassword</c> /
    /// <c>PasswordOrCorrupted</c>（⛔ 不比中文、⛔ 不靠"引擎说密码不对就认了"）。
    /// </summary>
    public bool PasswordClassFailure { get; init; }

    /// <summary>这一层的归档**确实加密**（引擎清单说有条目加密 / 报过加密头 / 识别阶段判过加密）。</summary>
    public bool ArchiveEncrypted { get; init; }

    /// <summary>候选循环**跑完了**：候选全试完，或撞上每层上限（⛔ 不是"被别的停因提前收手"）。</summary>
    public bool CandidatesFinished { get; init; }

    /// <summary>用户点了「停止后续」/ 取消 ⇒ 一个候选都不再试（⛔ 不许拿兜底绕过用户的停手）。</summary>
    public bool UserAskedToStop { get; init; }

    /// <summary>手上还有候选可试（一条都没有 ⇒ 兜底无事可做）。</summary>
    public bool HasCandidates { get; init; }

    /// <summary>本机有没有可用的 <c>WinRAR.exe</c>（用户自装；⛔ 我们不打包、不复制）。</summary>
    public bool EngineAvailable { get; init; }

    /// <summary>
    /// **主引擎在这些候选上已经写出过字节**（= 它进得去这一份的数据流）⇒ ⛔ 不认 WinRAR 的命中。
    ///
    /// <para><b>为什么这是命中的一票否决（2026-10-05 实测逮到的假成功）</b>：WinRAR **用错密码**解一个
    /// 普通 <c>-p</c> 加密的 7z 时，照样回**退出码 0**、并且**先按原大小把解密出来的乱码写进产物目录**
    /// （实测 23 字节：**文件名、大小、时间戳**与真产物逐字一样 —— 时间戳还是它按归档里存的值还原的），
    /// 只有 CRC 校验那一步在它自己肚子里失败。于是"退出码 0 且产物目录里多出了东西"这条判据
    /// 会把**错密码**认成命中 ⇒ 整条链把乱码当产物发布、结论从「密码错误」翻成「解压成功」
    /// （不变量 6 的红线：假的成功）。</para>
    ///
    /// <para>而"主引擎已经写出过字节"正是**这一档分不出证据**的标志：主引擎进得去这一份的数据流、
    /// 只是校验/密码不过 ⇒ 两个引擎给出的产物在**名字 / 大小 / 时间戳**上完全一致，现有任何判据
    /// （<c>OutputVerifier</c> 只比文件数与总字节数）都分辨不出"真解开了"与"写的是乱码"。
    /// ⇒ 按项目口径**判不出 ⇒ 什么都不做**：不换引擎、不改结论（调用方照旧用原来那条密码类结论），
    /// 只写一行如实说明的日志。</para>
    ///
    /// <para>反过来，"主引擎一个字节都没写出来"正是这一档要修的**真机形状**（同一个包 7-Zip 报
    /// <c>Wrong password</c>、0 个字节出来，换 WinRAR 十秒就解开）—— 那一档判据成立、照旧允许换引擎。</para>
    ///
    /// <para>判据只读**字节数**这一个事实（0 字节桩文件不算"写出过字节"：
    /// <c>ProducedContentGate</c> / <c>RecursiveExtractor.ProducedBytesInLayerOutput</c> 都按这个口径数），
    /// ⛔ 不比任何引擎文案、⛔ 不看文件名。默认 false = 与加这条之前逐字相同。</para>
    /// </summary>
    public bool MainEngineWroteBytes { get; init; }
}

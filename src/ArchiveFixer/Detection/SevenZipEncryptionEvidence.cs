namespace ArchiveFixer.Detection;

/// <summary>
/// **判读证据的唯一装配处**：把三档结论 + 依据 + 读入量装成 <see cref="ArchiveEncryptionReading"/>。
///
/// <para><b>为什么独立成类</b>：同 <c>EmbeddedZipOutcomes</c> 的先例 —— 证据对象的构造只留一处，
/// 免得出现"某个失败档忘了带 <see cref="ArchiveEncryptionReading.BytesRead"/> / 依据"这类漂移。
/// ⛔ 这里**不做任何判断**：结论由 <see cref="SevenZipEncryptionVerdict"/> 算好原样带过来。</para>
/// </summary>
internal static class SevenZipEncryptionEvidence
{
    /// <summary>没有路径 / 读不出来时的"不知道"（不带依据 —— 没有可说的结构化事实）。</summary>
    internal static ArchiveEncryptionReading NoReadablePath() => new()
    {
        State = ArchiveEncryptionState.Unknown,
        Basis = "没有可读的文件路径"
    };

    /// <summary>"不知道"：读不到 / 读不懂 / 超出预算 —— ⛔ 绝不猜成"没加密"。</summary>
    internal static ArchiveEncryptionReading Unknown(int bytesRead, string basis) => new()
    {
        State = ArchiveEncryptionState.Unknown,
        Basis = basis,
        BytesRead = bytesRead
    };

    /// <summary>走完结构、没有任何加密标志。</summary>
    internal static ArchiveEncryptionReading Plain(int bytesRead, string basis) => new()
    {
        State = ArchiveEncryptionState.NotEncrypted,
        Basis = basis,
        BytesRead = bytesRead
    };

    /// <summary>有证据的加密档（数据加密 / 头加密两档之一）。</summary>
    internal static ArchiveEncryptionReading Encrypted(int bytesRead, ArchiveEncryptionState state, string basis) => new()
    {
        State = state,
        Basis = basis,
        BytesRead = bytesRead
    };
}

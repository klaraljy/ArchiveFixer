namespace ArchiveFixer.Detection;

/// <summary>
/// **一次 next header 走读的结果**：只装"看到了什么结构事实"，⛔ 不装结论、不装给人看的话。
///
/// <para><see cref="Shape"/> 说走到哪一步停下；<see cref="Failure"/> 是那一步读不下去的结构原因
/// （只有"读不出来"那几档才非空）；<see cref="PropertyId"/> 只在
/// <see cref="SevenZipHeaderShape.UnexpectedProperty"/> 时有意义。</para>
///
/// <para>结论与依据文案全部在 <see cref="SevenZipEncryptionVerdict"/> —— 结构与判据分开，
/// 才能把"解析器看到什么"和"这算哪一档"各自单独比对。</para>
/// </summary>
internal sealed class SevenZipHeaderWalk
{
    /// <summary>走读停在哪一步（见 <see cref="SevenZipHeaderShape"/>）。</summary>
    internal SevenZipHeaderShape Shape { get; init; }

    /// <summary>读不下去的结构原因（原样来自解析层，⛔ 不含结论）。</summary>
    internal string Failure { get; init; } = string.Empty;

    /// <summary>预期之外的那个属性字节（只在 <see cref="SevenZipHeaderShape.UnexpectedProperty"/> 时有意义）。</summary>
    internal byte PropertyId { get; init; }
}

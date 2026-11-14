namespace ArchiveFixer.Models;
/// <summary>
/// 空间门拦下这一单时**已经算好的**那三个数（用户 2026-09-30 第 1 条：批末清单要"点名差多少"）。
///
/// <para><b>为什么要把它们记在任务上</b>：这三个数在空间门那一刻就算出来了
/// （<c>SpaceGate.Check</c> / <c>ScheduledExtractionItem.RequiredBytes</c>），
/// 但它们当时只进了日志与 <c>ExtractionCoordinator</c> 的一个私有列表
/// （<c>_spaceBlockedTasks</c>，批首会被清掉、批末只写日志）——
/// 批末那个汇总框要"点名差多少"时，手上只有任务清单，于是只能重算一遍。
/// 重算就是第二套口径（可用空间还是不是当时那个数？预算一样吗？），
/// 所以这里把**已经算出来的**三个数原样记下来，诊断清单只读它，一个数都不重算。</para>
///
/// <para>类型放在 Models 层：它只装数字，没有任何盘上动作（AGENTS.md §4 的分层铁律）。</para>
/// </summary>
public sealed class SpaceBlockedFacts
{
    /// <summary>当时的**需求**（<c>TaskSpaceEstimate.FreeSpaceDemandBytes</c>：内容物 + 过程物）。</summary>
    public long RequiredBytes { get; init; }

    /// <summary>当时的可用空间；<c>-1</c> = 取不到（与 <c>SpaceReservationLedger.UnknownAvailable</c> 同一口径）。</summary>
    public long AvailableBytes { get; init; } = -1L;

    /// <summary>差多少字节（<c>SpaceGateDecision.ShortfallBytes</c>）。</summary>
    public long ShortfallBytes { get; init; }
}

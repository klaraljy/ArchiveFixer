using System;
using System.Collections.Generic;

namespace ArchiveFixer.Detection;

/// <summary>
/// 一次候选池扫描的**完整输出** —— 判定流水线要的"候选事实"全在这一个对象里。
///
/// <para><b>职责</b>：把"同目录里哪些文件算这一组的候选"这件事的结果**只交一次**：
/// 在位的卷（带卷号）、哪些槽位是空的、哪一个空槽是"边界槽"（<c>.z01</c>/<c>.r00</c> 族那个
/// 名字不带卷号的本体位）、同一卷的名字变体、以及按体积 + 位置推定出来的候选与说明。</para>
///
/// <para><b>为什么是一个对象</b>：这些字段是同一份扫描的**同一次快照**（槽位、变体、推定互相依赖）。
/// 拆成多个方法各自重算，就会出现"槽位算了两遍、两遍不一致"的漂移；拿到快照之后再落证据，
/// 判定那一段就只剩"读事实 + 拼文案"。</para>
///
/// <para><b>失败模型</b>：扫描判不出（组里一卷可解析卷号的都没有）⇒ <see cref="Located"/> 为空，
/// 其余字段保持默认空集合；⛔ 消费方据此报"判不出"，不许拿空集合推"完整"。</para>
///
/// <para>生命周期：一次判定一份，创建后只读（<c>init</c>）；不持句柄、不缓存、可丢弃。</para>
/// </summary>
internal sealed class VolumeCandidateScan
{
    /// <summary>组内**已定位**的卷（带可解析卷号，按卷序升序）。</summary>
    public IReadOnlyList<ResolvedVolume> Located { get; init; } = Array.Empty<ResolvedVolume>();

    /// <summary>已定位卷里的最大卷号（<see cref="Located"/> 为空时无意义）。</summary>
    public int MaxFound { get; init; }

    /// <summary>1..<see cref="MaxFound"/> 之间没有卷的槽位（升序）。</summary>
    public IReadOnlyList<int> OpenSlots { get; init; } = Array.Empty<int>();

    /// <summary>
    /// 边界槽（<c>.z01</c>/<c>.r00</c> 族那个没卷号的本体位，即第 1 卷）是否空着 ——
    /// 这一档的候选只算**弱证据**（真案 ②：末卷被改成 <c>一只顶美.z删除ip</c>）。
    /// </summary>
    public bool BoundarySlotOpen { get; init; }

    /// <summary>**同一卷的名字变体**（<c>222.zip</c> 与 <c>222.zi删除p</c>）：算组成员，但不占独立卷号。</summary>
    public IReadOnlyList<string> VariantPaths { get; init; } = Array.Empty<string>();

    /// <summary>变体里有没有名字不标准的（有 ⇒ 整组不许进可删的其余物）。</summary>
    public bool VariantHasDisguisedName { get; init; }

    /// <summary>基名段相同、体积对得上、但既不是已定位的卷、也不是名字变体的候选（按槽位分配）。</summary>
    public IReadOnlyList<VolumeGroupEntry> Unrecognized { get; init; } = Array.Empty<VolumeGroupEntry>();

    /// <summary>按体积 + 位置**推定**出来的卷（弱证据，只报不采信）。</summary>
    public IReadOnlyList<ResolvedVolume> Inferred { get; init; } = Array.Empty<ResolvedVolume>();

    /// <summary>与 <see cref="Inferred"/> 一一对应的说明（写进证据表与结论文案）。</summary>
    public IReadOnlyList<string> InferredNotes { get; init; } = Array.Empty<string>();

    /// <summary>推定之后还剩几个槽没填上（"还有卷没认出来"的弱证据之一）。</summary>
    public IReadOnlyList<int> RemainingSlots { get; init; } = Array.Empty<int>();
}

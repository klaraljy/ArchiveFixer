using ArchiveFixer.Detection;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>试开一次分卷组的结论。</summary>
public sealed class VolumeProbeOutcome
{
    /// <summary>顺序成立（引擎真的读到了里面的条目）。</summary>
    public bool Confirmed { get; init; }

    /// <summary>
    /// 成立，但引擎**读不出清单**：这一组是"文件名也加密"的归档（7z <c>-mhe</c> / RAR <c>-hp</c>），
    /// 要正确密码才列得出条目。
    ///
    /// <para>它不影响"成立"这个结论（引擎按第一张卷声明的偏移真读到了那一份加密头），
    /// 只影响**怎么说这句话** —— 用户 2026-09-29 真机就是这一类包。</para>
    /// </summary>
    public bool NeedsPassword { get; init; }

    /// <summary>成立时的完整卷序（第 1 卷在第一位）。</summary>
    public IReadOnlyList<VolumeCandidate> OrderedVolumes { get; init; } = Array.Empty<VolumeCandidate>();

    /// <summary>
    /// **真的试过**。false = 没试成（没有可用引擎 / 拿不到卷根 / 建不出试开目录 / **跨盘**）——
    /// 这一档是"没试"，⛔ 不是"不成立"；消费方（<c>VolumeGroupResolver</c>）必须分开算。
    /// </summary>
    public bool Attempted { get; init; }

    /// <summary>
    /// 没试的**结构化**原因（⛔ 消费方不许去比 <see cref="Reason"/> 的中文）。
    /// 只有 <see cref="VolumeTrialSkipReason.CrossVolume"/> 会改变结论：跨盘 ⇒ 判不出。
    /// </summary>
    public VolumeTrialSkipReason SkipReason { get; init; } = VolumeTrialSkipReason.None;

    /// <summary>试了几种排列。</summary>
    public int Attempts { get; init; }

    /// <summary>给人看的一句话（成立 / 不成立都说清为什么）。</summary>
    public string Reason { get; init; } = string.Empty;
}

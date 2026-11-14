namespace ArchiveFixer.Detection;

/// <summary>
/// 失败模型 ——「这一次能不能下结论 / 能不能进可删的其余物 / 这一档没试开是不是原理上做不了」
/// 的唯一出口。
///
/// <para><b>为什么独立成层</b>：这三件事的兜底方向全都必须是"什么都不做"（判不出 ⇒ 不删、不移动、不改名），
/// 而"可删资格"还有第二个调用方 —— 试开那一条决定性证据（<see cref="VolumeGroupTrialGate"/>）也会落结论。
/// 两处各写一遍 `verdict == Complete &amp;&amp; !hasRenamedVolume` 迟早走岔，所以只留这一处。</para>
///
/// <para><b>判断准则</b>（⛔ 判据只读枚举，不许比中文）：</para>
/// <list type="bullet">
/// <item><description>可删 = 结论"完整" **且** 组里没有名字不标准的卷（缺一条都不行）；</description></item>
/// <item><description>"没试开"里只有跨盘 / 没有工作区根这两档会**改变结论**（降成判不出），其余档只是"没取到证据"；</description></item>
/// <item><description>其余一切情形一律 <see cref="VolumeGroupVerdict.Undetermined"/> + 不可删。</description></item>
/// </list>
///
/// <para>生命周期：纯静态、无状态；只读结论对象与枚举，不碰文件系统、不调引擎。</para>
/// </summary>
internal static class VolumeGroupFailureRules
{
    public static VolumeGroupResolution Undetermined(string anchor, string reason) => new()
    {
        Verdict = VolumeGroupVerdict.Undetermined,
        AnchorPath = anchor,
        Reason = reason,
        CanEnterDeletableRestItems = false
    };

    /// <summary>
    /// **"这一组可不可以进可删的其余物"的唯一算法**（AGENTS §9.5：同一件事只有一个出口）。
    ///
    /// <para>只有两条都成立才放行：① 结论是 <see cref="VolumeGroupVerdict.Complete"/>；
    /// ② 组里没有名字不标准的卷。⛔ 其余一切情形（含"判不出"、含弱证据）一律 false ——
    /// 兜底落在"什么都不做"那一档。</para>
    ///
    /// <para>放在这里当唯一出口，是因为试开那一档（<see cref="VolumeGroupTrialGate.ApplyTrialOutcome"/>）也会落结论；
    /// 两处各写一遍 `verdict == Complete &amp;&amp; !hasRenamedVolume` 迟早会走岔。</para>
    /// </summary>
    public static bool Deletable(VolumeGroupVerdict verdict, bool hasRenamedVolume) =>
        verdict == VolumeGroupVerdict.Complete && !hasRenamedVolume;

    /// <summary>
    /// 这一档"没试开"是不是**原理上做不了**（不是我这次没取到证据）。
    ///
    /// <para>做不了 ⇒ 只能如实报"无法确认" ⇒ 结论降成 <see cref="VolumeGroupVerdict.Undetermined"/>
    /// （⛔ 不许拿名字/体积那几条去顶"完整"，那正是 25 GB 那次的形状）。两档都源自用户红线
    /// 「工作区就设在解压的地方，这就完全不存在跨盘的操作」：</para>
    /// <list type="bullet">
    /// <item><description><b>没有工作区根</b>（<see cref="VolumeTrialSkipReason.NoProbeRoot"/>）——
    /// 拿不到目标目录，而工作区只准设在解压的地方，⛔ 不许另找地方开一个；</description></item>
    /// <item><description><b>跨盘</b>（<see cref="VolumeTrialSkipReason.CrossVolume"/>）——
    /// 硬链接不能跨卷，⛔ 不许退到源卷根去试。这一条是**原理限制**，不是偷懒。</description></item>
    /// </list>
    ///
    /// <para>其余几档（没引擎 / 凑不出顺序）照旧"这一次没取到证据，结论不变" —— 没试 ≠ 不成立。</para>
    /// </summary>
    public static bool IsUnconfirmable(VolumeTrialSkipReason reason) =>
        reason is VolumeTrialSkipReason.CrossVolume or VolumeTrialSkipReason.NoProbeRoot;

    /// <summary>"没试开"的档说成给人看的一句话（⛔ 判据不许比这句话，判据只读枚举）。</summary>
    public static string DescribeSkip(VolumeTrialSkipReason reason) => reason switch
    {
        VolumeTrialSkipReason.CrossVolume => "跨盘无法试开，整卷是否齐全无法确认",
        VolumeTrialSkipReason.NoProbeRoot => "没有工作区根（拿不到目标目录），不许另找地方试开，整卷是否齐全无法确认",
        _ => "试开这一条证据取不到，整卷是否齐全无法确认"
    };
}

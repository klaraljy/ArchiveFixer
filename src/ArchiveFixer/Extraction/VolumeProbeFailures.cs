using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction;

/// <summary>
/// **失败模型**：试开这一层"拒绝结论怎么造、最后一句话怎么说"的唯一出口。
///
/// <para><b>为什么要独立成层</b>：试开的三条入口（整组试开 / 跨盘顺序试拼 / 通用分片顺序试开）
/// 共用同一套"没试成"与"试过但不成立"的语义 —— <see cref="VolumeProbeOutcome.Attempted"/> 为 false
/// 是「没试」而不是「不成立」，<see cref="VolumeProbeOutcome.SkipReason"/> 只读枚举、⛔ 消费方不比中文。
/// 三处各拼一遍理由迟早走岔，所以拒绝结论只从这里出。</para>
///
/// <para>准入理由的措辞只差动词（试开 / 试拼）与"证不出什么"（整组是否齐 / 顺序），
/// 由调用方按入口传进来，⛔ 不在这里另造第二套判据。</para>
///
/// <para>生命周期：纯静态、无状态；不碰文件系统、不调引擎，只造结论与文案。</para>
/// </summary>
internal static class VolumeProbeFailures
{
    /// <summary>顺序试拼做不出硬链接时的原话（跨盘 zip 那一路）。</summary>
    internal const string LinkNotPossibleForSpannedOrder =
        "做不了硬链接 —— 源目录与试拼目录必须在同一个卷上，且文件系统要支持硬链接";

    /// <summary>通用分片顺序试开做不出硬链接时的原话。</summary>
    internal const string LinkNotPossibleForNumberedOrder =
        "做不了硬链接 —— 源目录与试开目录必须在同一个卷上，且文件系统要支持硬链接";

    /// <summary>顺序试拼：一组密码都报密码错 ⇒ 顺序没法验证（与"顺序不对"是同一句话）。</summary>
    internal const string PasswordBlockedForSpannedOrder =
        "这一组是加密的，而手上没有能用的密码 ⇒ 顺序没法验证（顺序错了与密码错了在引擎那里是同一句话）";

    /// <summary>通用分片顺序试开：引擎报密码错 ⇒ 顺序没法验证（与"顺序不对"是同一句话）。</summary>
    internal const string PasswordBlockedForNumberedOrder =
        "这一组是加密的，而这条试开定序没有可用密码 ⇒ 顺序没法验证（顺序错了与密码错了在引擎那里是同一句话）";

    /// <summary>一次"试过但不成立"的结论（<paramref name="attempted"/> = false 时是"没试"）。</summary>
    internal static VolumeProbeOutcome Refuse(
        int attempts,
        string reason,
        bool attempted,
        VolumeTrialSkipReason skipReason = VolumeTrialSkipReason.None,
        bool needsPassword = false) => new()
        {
            Confirmed = false,
            Attempted = attempted,
            NeedsPassword = needsPassword,
            SkipReason = skipReason,
            Attempts = attempts,
            Reason = reason
        };

    /// <summary>补一个"真的试过"的标记（单独一卷的结论是确定的，但它自己不知道试没试）。</summary>
    internal static VolumeProbeOutcome WithAttempted(VolumeProbeOutcome outcome, bool attempted) => new()
    {
        Confirmed = outcome.Confirmed,
        Attempted = attempted,
        SkipReason = outcome.SkipReason,
        NeedsPassword = outcome.NeedsPassword,
        OrderedVolumes = outcome.OrderedVolumes,
        Attempts = outcome.Attempts,
        Reason = outcome.Reason
    };

    /// <summary>引擎没给原话时保留上一次的失败原因（只为把话说清，不参与判定）。</summary>
    internal static string PreferLastFailure(string lastFailure, string? engineMessage) =>
        string.IsNullOrWhiteSpace(engineMessage) ? lastFailure : engineMessage;

    /// <summary>准入：第一卷不在了。</summary>
    internal static VolumeProbeOutcome FirstVolumeGone() =>
        Refuse(0, "第一卷不在了", attempted: false, VolumeTrialSkipReason.Other);

    /// <summary>准入：整组试开没有可当后续卷的候选。</summary>
    internal static VolumeProbeOutcome NoGroupOrderings() =>
        Refuse(
            0,
            "同目录里没有可当后续卷的候选文件",
            attempted: false,
            VolumeTrialSkipReason.NoOrderings);

    /// <summary>准入：顺序试开 / 试拼没有可试的顺序。</summary>
    internal static VolumeProbeOutcome NoCandidateOrders() =>
        Refuse(0, "没有可试的顺序", attempted: false, VolumeTrialSkipReason.NoOrderings);

    /// <summary>准入：整组还没凑齐（少于 2 片）。<paramref name="action"/> = 那句话的动词（试拼 / 按顺序试开）。</summary>
    internal static VolumeProbeOutcome IncompleteOrder(string action) =>
        Refuse(0, $"整组还没凑齐（少于 2 片），没法{action}", attempted: false, VolumeTrialSkipReason.Other);

    /// <summary>准入：候选顺序的片数彼此对不上。</summary>
    internal static VolumeProbeOutcome OrderCountMismatch(string verb) =>
        Refuse(0, $"候选顺序的片数对不上，没法{verb}", attempted: false, VolumeTrialSkipReason.Other);

    /// <summary>准入：顺序里有一片不在了。</summary>
    internal static VolumeProbeOutcome OrderPieceMissing(string verb) =>
        Refuse(0, $"有一片不在了，没法{verb}", attempted: false, VolumeTrialSkipReason.Other);

    /// <summary>准入：没有可用的解压引擎。</summary>
    internal static VolumeProbeOutcome EngineUnavailable(string verb) =>
        Refuse(
            0,
            $"当前没有可用的解压引擎，没法{verb}验证（宁可不改，也不猜）",
            attempted: false,
            VolumeTrialSkipReason.NoEngine);

    /// <summary>
    /// 准入：没有工作区根 ⇒ **一次都不试**（工作区只准设在解压的地方，⛔ 不另找落点）。
    /// <paramref name="unprovable"/> = 这一档证不出什么（"整组是齐的" / "顺序"）。
    /// </summary>
    internal static VolumeProbeOutcome NoWorkRoot(string verb, string unprovable) =>
        Refuse(
            0,
            $"这条路没有工作区根（拿不到这一单的目标目录）⇒ 不许{verb}：工作区只准设在解压的地方，"
            + $"⛔ 不在源卷根 / 程序目录 / 临时目录另开一个 —— 证不出{unprovable}，只能如实报「无法确认」",
            attempted: false,
            VolumeTrialSkipReason.NoProbeRoot);

    /// <summary>
    /// 准入：跨卷 ⇒ **一次都不试**（硬链接不能跨卷；⛔ 不复制、⛔ 不在源盘开工作区）。
    /// <paramref name="subject"/> = 这一档证不出什么（"整卷是否齐全" / "顺序"）。
    /// </summary>
    internal static VolumeProbeOutcome CrossVolume(string verb, string subject) =>
        Refuse(
            0,
            $"跨盘无法{verb}，{subject}无法确认 —— 硬链接不能跨卷（源文件与目标工作区不在同一个卷上），"
            + "⛔ 不复制大文件、也不在源盘上开工作区，所以这一档只能判「无法确认」",
            attempted: false,
            VolumeTrialSkipReason.CrossVolume);

    /// <summary>准入：拿不到可用的试开目录（工作区根给得不合法）。</summary>
    internal static VolumeProbeOutcome NoProbeRoot(string verb) =>
        Refuse(
            0,
            $"拿不到可用的{verb}目录（工作区根给得不合法）—— 不复制大文件，所以不试",
            attempted: false,
            VolumeTrialSkipReason.NoProbeRoot);

    /// <summary>试开 / 试拼目录建不出来（<paramref name="message"/> = 原始异常消息）。</summary>
    internal static string ProbeDirectoryCreateFailed(string verb, string message) =>
        $"{verb}目录建不出来（{message}）";

    /// <summary>整组试开：所有排列都不成立。</summary>
    internal static string AllOrderingsFailed(int attempts, string lastFailure) =>
        $"试了 {attempts} 种排列都不成立（最后一次：{lastFailure}）";

    /// <summary>顺序试开 / 试拼：这些顺序都不成立（带引擎原话）。</summary>
    internal static string OrdersNotConfirmed(string lastFailure) =>
        $"这些顺序都不成立（引擎原话：{lastFailure}）";

    /// <summary>试开 / 试拼过程中抛了异常（<paramref name="verb"/> 也允许是"试开定序"）。</summary>
    internal static string TrialFailed(string verb, string message) =>
        $"{verb}过程中出错：{message}";
}

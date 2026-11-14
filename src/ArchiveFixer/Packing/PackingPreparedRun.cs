namespace ArchiveFixer.Packing;

/// <summary>
/// 计划阶段的结论（<see cref="PackingPreparation"/> 的产出）。
///
/// <para><b><see cref="Plan"/> 为 null ⇔ 没准备好</b>：此时 <see cref="FailureReason"/> 与
/// <see cref="FailureMessage"/> 就是失败结果要用的那两句（与 <see cref="PackingResult.FailureReason"/> /
/// <see cref="PackingResult.Message"/> 一一对应）。调用方不需要再判第二个标志位 ——
/// 两个来源一旦各说各话，"规划成功却没有计划"这种半状态就会漏到执行那一步。</para>
/// </summary>
internal sealed class PackingPreparedRun
{
    /// <summary>算好的规划；null = 计划阶段就被拒了（请求不合法 / 空间不够 / 落点建不出来 …）。</summary>
    internal PackingPlan? Plan { get; init; }

    /// <summary>
    /// **实际生效的**外层容器（本机没有 Rar.exe 时是降级之后的 7z）。
    /// 它只有一处计算（<see cref="PackingOuterContainerResolver"/>），这里只是把它带到执行与校验那两步，
    /// ⛔ 不许在执行那一步再按请求里那一档重算一遍。
    /// </summary>
    internal PackOuterContainer EffectiveContainer { get; init; }

    /// <summary>失败原因（给日志与 <see cref="PackingResult.FailureReason"/> 用）。</summary>
    internal string FailureReason { get; init; } = string.Empty;

    /// <summary>失败结论那一句（<c>StatusText.PackFailed</c> 或 <c>StatusText.DiskSpaceInsufficient</c>）。</summary>
    internal string FailureMessage { get; init; } = string.Empty;
}

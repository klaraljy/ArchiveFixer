using System;
using System.Collections.Generic;

namespace ArchiveFixer.Storage;

/// <summary>空间门（<see cref="SpaceGate"/>）的结论。</summary>
public sealed class SpaceGateDecision
{
    /// <summary>空间判断的机器三态；调用方不得把 Unknown 当 Enough。</summary>
    public SpaceDecisionState State =>
        ProbeFailed ? SpaceDecisionState.Unknown
        : Allowed ? SpaceDecisionState.Enough
        : SpaceDecisionState.NotEnough;

    /// <summary>放不放行。false = **不启动**这个任务（用户 2026-09-22 需求：空间不足时不启动）。</summary>
    public bool Allowed { get; init; }

    /// <summary>本次判断用的需求字节数（含已经在跑的其它任务的预留）。</summary>
    public long RequiredBytes { get; init; }

    /// <summary>目标盘可用字节数；取不到时为 -1（<see cref="ProbeFailed"/> 为 true）。</summary>
    public long AvailableBytes { get; init; }

    /// <summary>要求保留的余量字节数。</summary>
    public long ReserveBytes { get; init; }

    /// <summary>差多少字节（放行时为 0）。</summary>
    public long ShortfallBytes { get; init; }

    /// <summary>中文结论，**一定带具体数字**（需要 X、可用 Y、差 Z）。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>建议动作（清理 `其余物` / 换输出盘 / 开「空间不足」模式 / 降并发）。</summary>
    public IReadOnlyList<string> Suggestions { get; init; } = Array.Empty<string>();

    /// <summary>可用空间**取不到**（盘未就绪 / 路径非法 / 权限）。</summary>
    public bool ProbeFailed => AvailableBytes < 0;

    /// <summary>是不是"空间不足"被拦下（区别于"探测失败"）。</summary>
    public bool ShortOfSpace => !Allowed && !ProbeFailed;

    /// <summary>一行日志形态：把结论与建议拼成一句话（建议之间用分号）。</summary>
    public string ToLogLine()
    {
        return Suggestions.Count == 0
            ? Reason
            : Reason + " 建议：" + string.Join("；", Suggestions) + "。";
    }
}

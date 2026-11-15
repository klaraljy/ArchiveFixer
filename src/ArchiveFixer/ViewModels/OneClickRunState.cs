using ArchiveFixer.BindingModels;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 一次一键处理运行的状态账：**跨轮/跨阶段**共享的那几个值。
///
/// <para>为什么要有它：这些值过去是 <c>RunPipelineAsync</c> 的局部量，而轮前 setup、轮循环、
/// 轮尾结论分别在三个位置读写它们 —— 只要想搬动其中任何一段，就得让整条链看见同一份账。
/// 这里只搬**跨阶段**的那 14 个，⛔ 轮内临时量（这一轮扫到哪些组、哪几张准备单）不进这里。</para>
///
/// <para>⚠ 生命周期 = 一次 <c>RunPipelineAsync</c>：每次运行新建一个，不复用。</para>
/// </summary>
internal sealed class OneClickRunState
{
    /// <summary>轮前 setup 定下来的这一批任务（已按"还在列表里"过滤）。</summary>
    internal List<ArchiveTask> RootTargets { get; set; } = new();

    /// <summary>本次选项快照（null = 按设置走；<c>Prepare</c> 之后必定非空）。</summary>
    internal OneClickRunOptions? SnapshotOptions { get; set; }

    /// <summary>本次运行的停止令牌（UI 停止只翻它；轮尾/结论也要读它）。</summary>
    internal RunStopToken StopToken { get; } = new();

    /// <summary>本次运行冻结的快照（阶段 4 影子）。</summary>
    internal RunSnapshot? Snapshot { get; set; }

    /// <summary>处理过的任务（按轮累加；汇总要算"本次一共处理了多少个"）。</summary>
    internal List<ArchiveTask> Processed { get; set; } = new();

    /// <summary>源文件（含分卷各卷）不算内层包：不能把用户最初给的那个 .mp4 / .7z.001 又加一遍。</summary>
    internal HashSet<string> SourcePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>这一轮要处理的任务（第 1 轮 = <see cref="RootTargets"/>，之后逐轮换成新内层包）。</summary>
    internal List<ArchiveTask> RoundTargets { get; set; } = new();

    /// <summary>已经跑完几轮（0 = 还没开始）。</summary>
    internal int Round { get; set; }

    /// <summary>自动续解了几层（正常跑完时 = 轮数 - 1）。</summary>
    internal int ContinuationLayers { get; set; }

    /// <summary>这一批是否被停止（用户点停止 / 有任务没轮到）。</summary>
    internal bool Stopped { get; set; }

    /// <summary>停在哪一阶段（写进汇总；空 = 没停）。</summary>
    internal string StoppedPhase { get; set; } = string.Empty;

    /// <summary>是否撞到轮数上限。</summary>
    internal bool HitRoundLimit { get; set; }

    /// <summary>撞到轮数上限时"已加进列表、这一批没解"的内层包。</summary>
    internal List<ArchiveTask> PendingContinuation { get; set; } = new();

    /// <summary>
    /// 「用户按过停止后续」这件事必须单独记一笔（只看 <c>_vm.IsStopping</c> 不行：
    /// 协调器在批次开始和收尾时都会把它复位成 false）。
    /// </summary>
    internal bool StopRequested { get; set; }
}

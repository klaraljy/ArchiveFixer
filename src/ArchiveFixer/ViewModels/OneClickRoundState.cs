using ArchiveFixer.BindingModels;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System.Collections.Generic;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// **一轮**的状态：这一轮从产物里认出来的内层包、逐条扫描日志、轮判定结论，
/// 以及第二大步点名的入口任务。
///
/// <para>为什么要有它：轮前扫描（A 段）、第二大步（B 段）、轮尾续解规划（C 段）过去靠
/// <c>RunPipelineAsync</c> 的局部量直接传递；A/C 拆成阶段方法之后，必须有一个**只活一轮**的
/// 载体把这三段的交集带过去（⛔ 不放进跨轮状态 <see cref="OneClickRunState"/>，
/// 免得上一轮的内层包被下一轮误读）。</para>
/// </summary>
internal sealed class OneClickRoundState
{
    /// <summary>本轮按内容认出来的内层包（A 段产出；B/C 段消费）。</summary>
    internal List<InnerArchiveCandidate> InnerArchives { get; set; } = new();

    /// <summary>本轮产物扫描的逐条日志（A 段产出；B 段末尾统一落日志）。</summary>
    internal List<string> ScanLog { get; set; } = new();

    /// <summary>轮判定结论（A 段产出；只用于日志与诊断）。</summary>
    internal RoundProductClassifier.Summary? RoundSummary { get; set; }

    /// <summary>第二大步点名的入口任务（B 段产出；C 段排进下一轮）。</summary>
    internal List<ArchiveTask> VolumeEntryTasks { get; set; } = new();

    /// <summary>第二大步点名时顺带补建的入口候选（B 段产出；B 段尾消费）。</summary>
    internal List<InnerArchiveCandidate> SecondStepEntryCandidates { get; set; } = new();

    /// <summary>第二大步里每个组的 Unit（与候选同序；B 段尾建链接时用）。</summary>
    internal List<(VolumeCandidateDiscovery.VolumeCandidate Group, VolumeGroupUnit Unit)> SecondStepEntryGroups { get; set; } = new();
}

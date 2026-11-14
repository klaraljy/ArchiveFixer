using System;

namespace ArchiveFixer.Engines;

/// <summary>
/// "这个引擎已经很久没有任何输出了"的通知（**只提示，绝不杀进程**）。
///
/// <para>
/// 对应的正是用户反复抱怨的"卡死"：界面只有"处理中/完成"两态，
/// 而一个 780MB 的固实包在解压中途可能几十秒不产生任何输出 —— 看起来就是死了。
/// 这里不做任何"自动处理"，只把事实告诉上层，由上层写日志 + 在任务上提示，
/// 用户自己决定要不要点「取消当前」（取消语义只归用户，见不变量 9）。
/// </para>
/// </summary>
public sealed class ArchiveStallNotice
{
    /// <summary>距离最后一次引擎输出过去了多久。</summary>
    public TimeSpan Idle { get; init; }

    /// <summary>触发这条提示的阈值（默认 90 秒）。</summary>
    public TimeSpan Threshold { get; init; }

    /// <summary>触发时进程是否仍在运行（恒为 true —— 进程已退出时不会报这个）。</summary>
    public bool ProcessStillRunning { get; init; } = true;

    public override string ToString()
    {
        return $"已 {Idle.TotalSeconds:F0} 秒没有任何引擎输出（阈值 {Threshold.TotalSeconds:F0} 秒）";
    }
}

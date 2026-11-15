using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>源文件快照与不变量 11 的检查口（从 ExtractionCoordinator.Source.cs 搬出）。</summary>
internal sealed class ExtractionSourceSnapshot
{
    private readonly ExtractionCoordinator _host;

    internal ExtractionSourceSnapshot(ExtractionCoordinator host)
    {
        _host = host;
    }



    /// <summary>
    /// **开工前**保证手上有一份基准：没有就现在补拍一次。
    ///
    /// <para>
    /// 为什么是补拍而不是拦下（"判据缺失 ≠ 判据不通过"）：老任务（这个功能落地之前建的任务）、
    /// 测试直接 <c>new</c> 出来的任务、以及用户手改过列表的情形，都可能没有快照。
    /// 把"我们没记录"说成"你的文件变了"，会把一个本来能正常跑的任务拦死，
    /// 而用户完全无从理解（他什么都没做过）。正确做法是把基准补上，从这一刻起开始保护。
    /// </para>
    /// </summary>
    public void EnsureSourceSnapshot(ArchiveTask task)
    {
        if (task == null || task.HasSourceSnapshot)
        {
            return;
        }

        task.CaptureSourceSnapshot();

        _host.AppendLog(
            "INFO",
            $"{task.LogName}：开工前补拍源文件快照（这份任务过去没有基准），"
            + "从这一刻起按源文件大小 / 修改时间判断它有没有被改动。");
    }

    /// <summary>
    /// **真正调引擎之前**比一次：变了就当场停下（不变量 11）。
    ///
    /// <para><b>为什么落在这里</b>：<see cref="ExtractSingleTaskAsync"/> 是**所有**解压入口的
    /// 唯一收口 —— 一键处理、手动「只解压」、单包重试、危险模式自测走的都是它；
    /// 而"引擎"这条路只有它下游会走。放在这里，等于任何一个入口都不可能绕过去。</para>
    ///
    /// <para><b>停下来意味着什么（不变量 1 的红线照旧）</b>：引擎**一次都不会被调用**、
    /// 源包一个字节都不动、<c>其余物</c> 不生成、不发布任何产物、不留任何中间品。
    /// 状态落「源文件已变化」，<c>ErrorMessage</c> 里点名"哪一个文件、哪一项变了"并给出出路。</para>
    /// </summary>
    /// <returns>true = 已经拦下（调用方必须立刻 return，什么都不许做）。</returns>
    internal bool StopIfSourceChanged(ArchiveTask task) => StopIfSourceChanged(task, out _);

    /// <summary>
    /// 与上面同一个判据，只是把"为什么"也交回给调用方（递归核心要拿它当停止理由）。
    /// <paramref name="reason"/> 为空串 = 没有变化，任务照常继续。
    /// </summary>
    internal bool StopIfSourceChanged(ArchiveTask task, out string reason)
    {
        reason = string.Empty;

        if (task == null || !task.HasSourceSnapshot)
        {
            // 没有基准：不拦（调用方会先 EnsureSourceSnapshot 补上）。
            return false;
        }

        SourceChangeResult? comparison = task.CompareWithSourceSnapshot();

        if (comparison == null || !comparison.Changed)
        {
            return false;
        }

        reason = SourceFileSnapshot.DescribeChange(comparison);

        /*
         * 状态 / 进度 / 原因 / 结束时间四件事一起落（不变量 6 的反面同样成立：
         * 一件没做成的事不许看起来像做成了）。这里刻意**不碰** task.OutputPath：
         * 它可能还停在上一次的值，但对一个"压根没开工"的任务来说没有意义，
         * 而失败清单里的"位置"一行读的是 CurrentPath（源包在哪），那才是用户要去找的东西。
         */
        _host.MarkStoppedBeforeExtract(task, StatusText.SourceChanged, reason);

        _host.AppendLog("ERROR", $"源文件已变化，未开始解压：{task.FileName} —— {reason}");

        return true;
    }

    /// <summary>
    /// 递归核心那一侧的检查口（不变量 11 覆盖到每一层）。
    ///
    /// <para><b>只认第 0 层</b>，也就是用户给的源包：第 1 层起解的是**我们自己**从内层抠出来的
    /// 过程物（工作区里的文件），它们既不在源文件快照里、也不该被源文件的变化牵连 ——
    /// 拿源包的快照去比内层包，只会得出一句必然错误的"源文件已变化"。</para>
    ///
    /// <para>返回非 null = 已经拦下，递归核心会停止展开并把这句话当成本次的结论。</para>
    /// </summary>
    internal Task<string?> CheckRootSourceUnchangedAsync(ArchiveTask task)
    {
        return Task.FromResult(StopIfSourceChanged(task, out string reason) ? reason : null);
    }
}


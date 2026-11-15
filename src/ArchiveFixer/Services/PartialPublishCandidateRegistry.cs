using ArchiveFixer.BindingModels;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Services;

/// <summary>
/// 本批「部分完成」候选的登记处：**失败路径把"盘上还剩什么"记进来，收尾那一刻取走**
/// （见 <see cref="PartialPublishStep.TryPublishPartialProductsAsync"/>）。
///
/// <para>为什么用"候选"而不是就地发布：发布要读到三样只有收尾那一刻才齐的东西 ——
/// 机器终态（失败 / 部分完成才算数）、最终输出目录（同名冲突可能刚改过名）、
/// 以及"这一单到底还剩哪些产物"（候选循环里每个候选都会清一次产物目录）。
/// 所以失败那一刻只记**事实与引用**（没有额外的磁盘开销），收尾时再判。</para>
///
/// <para>键用任务对象本身；条目在收尾时由 <see cref="Take"/> 移除 —— 不清掉的话同一个任务对象
/// 重跑时会把上一轮的清单当成这一轮的（那是"拿旧识别结果解新文件"的翻版）。</para>
///
/// <para>⛔ 本类不判"该不该发"（那是 <see cref="PartialPublishAdmission"/> 的事），也不动盘：
/// 它只负责**在失败那一刻把事实记准**。</para>
/// </summary>
internal sealed class PartialPublishCandidateRegistry
{
    /// <summary>
    /// 部分完成发布的候选（**失败路径**填、收尾处发布）。
    /// 字典本身对协调器可见：收尾之前要读走那一单的 <c>ChainProducedContent</c> 做终态分类，
    /// 而收尾那次 <see cref="Take"/> 会把它从表里摘掉。
    /// </summary>
    internal ConcurrentDictionary<ArchiveTask, PartialPublishStep.PartialPublishCandidate> Candidates { get; } = new();

    /// <summary>
    /// 记下"这一单失败时盘上还剩什么"（部分完成发布的输入）。**失败路径调用**，成功路径一次都不调。
    ///
    /// <para>⛔ 这里刻意**不看③页那个开关**：开关在收尾那一刻才读（见
    /// <see cref="PartialPublishAdmission.IsActive"/>），否则"开着空间不足模式时本开关不生效"这句说明
    /// 就没机会写出来了（一个候选都没记下来 ⇒ 收尾时连"为什么没发布"都答不上）。
    /// 记的都是引用，没有额外的磁盘开销。</para>
    /// </summary>
    /// <param name="stagingRoot">已解出来的东西在哪（暂存目录 / 这一层的产物目录）。</param>
    /// <param name="manifestEntries">逐条清单（递归那条路直接带过来；没有就传 null，收尾时懒取一次）。</param>
    internal void Record(
        ArchiveTask task,
        string stagingRoot,
        string archivePath,
        string password,
        IReadOnlyList<(string Path, long Size)>? manifestEntries,
        IReadOnlyList<string>? engineFailedEntries,
        int engineReportedErrorCount,
        bool engineReportedFailure,
        string? detectedErrorType = null,
        bool chainProducedContent = false)
    {
        if (task == null || string.IsNullOrWhiteSpace(stagingRoot))
        {
            return;
        }

        Candidates[task] = new PartialPublishStep.PartialPublishCandidate
        {
            StagingRoot = stagingRoot,
            ArchivePath = archivePath ?? string.Empty,
            Password = password ?? string.Empty,
            ManifestEntries = manifestEntries ?? Array.Empty<(string, long)>(),
            EngineFailedEntries = engineFailedEntries ?? Array.Empty<string>(),
            EngineReportedErrorCount = engineReportedErrorCount,
            EngineReportedFailure = engineReportedFailure,
            Damaged = PartialPublishFacts.IsDamagedArchive(detectedErrorType),
            DetectedErrorType = detectedErrorType ?? string.Empty,
            ChainProducedContent = chainProducedContent
        };
    }

    /// <summary>
    /// 递归那条路的登记（递归收尾时调用，见 <c>ExtractionCoordinator.Recursion.cs</c>）。
    ///
    /// <para>只认"**最深的那一层失败了**"这一档：那一层的产物才是"解了一半的现场"。
    /// 层成功、只是整条链按上限 / 多分支停住的那些情形**不登记** —— 它们的产物该走既有的
    /// 定稿与链尾口径（那是另一条路，⛔ 不许被这一档抢过来当成"部分完成"发布）。</para>
    /// </summary>
    internal void RecordFromRecursion(ArchiveTask task, RecursionResult? result)
    {
        if (task == null || result == null || string.IsNullOrWhiteSpace(task.CurrentPath))
        {
            return;
        }

        RecursionLayerReport? failedLayer = PartialPublishFacts.ResolveStoppedLayer(result);

        if (failedLayer == null || string.IsNullOrWhiteSpace(failedLayer.OutputPath))
        {
            return;
        }

        /*
         * 密码传空串：这一层的候选是递归核心自己挑的，它只把**脱敏**的占位符交出来
         * （报告里绝不出现明文密码）。所以这一档要么拿现成的逐条清单（正常情形：
         * 解压前列目录那一次就是拿对的密码列的），要么什么都不发布 —— ⛔ 不去猜密码。
         */
        /*
         * 链上**任何一层**产出过文件 ⇒ "整体已有内容物解出"（用户 2026-10-16 口径）。
         *
         * ⚠ 还必须加上**停下那一层自己的产物目录**：失败层的报告里 `OutputFileCount` 按定义是 0
         * （`BuildLayerReport` 写的就是 0 —— 那一层没跑成），可它的产物目录里**可能真有东西**。
         * 实测（假引擎 + 5 个产物文件 + 密码错误）：第一版只读报告 ⇒ 判成"没有内容物"⇒ 终态落 `Failed`，
         * 与用户口径相反。判据仍走**唯一出口** `ProducedContentGate`，⛔ 不另定阈值。
         */
        bool chainProducedContent =
            result.Layers.Any(layer => layer.OutputFileCount > 0)
            || (ProducedContentGate.TryMeasure(
                    failedLayer.OutputPath,
                    failedLayer.ReportedSubItemErrors,
                    failedLayer.FailedEntryNames.Count,
                    out int failedLayerFiles,
                    out _)
                && failedLayerFiles >= ProducedContentGate.MinimumFiles);

        Record(
            task,
            failedLayer.OutputPath,
            failedLayer.ArchivePath,
            string.Empty,
            failedLayer.ManifestEntries,
            failedLayer.FailedEntryNames,
            failedLayer.ReportedSubItemErrors,
            engineReportedFailure: true,
            detectedErrorType: failedLayer.DetectedErrorType,
            chainProducedContent: chainProducedContent);
    }

    /// <summary>
    /// 取走这一单的候选（收尾处**唯一**读取口）。返回 null = 这一单没登记过（或已经被取走）。
    ///
    /// <para>⚠ 取走发生在"要不要发"之前：取消也照样取走 —— 候选只是失败那一刻的快照，
    /// 留着它只会让同一个任务对象重跑时读到上一轮的事实。</para>
    /// </summary>
    internal PartialPublishStep.PartialPublishCandidate? Take(ArchiveTask? task)
    {
        if (task == null)
        {
            return null;
        }

        return Candidates.TryRemove(task, out PartialPublishStep.PartialPublishCandidate? candidate)
            ? candidate
            : null;
    }
}

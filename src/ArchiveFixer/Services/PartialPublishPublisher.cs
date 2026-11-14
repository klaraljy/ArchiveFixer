using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Services;

/// <summary>
/// 收尾那一刻的**真发布与结算**：懒取清单 → 对账／二次空间体检／搬运（唯一执行体
/// <see cref="PartialPublishRunner"/>）→ 把"救回来多少"记在任务上 → 机器终态跟着走 →
/// 决定其余物与工作区能不能收。
///
/// <para>为什么"搬运"和"结算"必须在同一处（⛔ 不许再把记账甩给批末）：</para>
/// <list type="number">
/// <item><description>"这一单救回来多少"只有写在**产生它的那一刻**才是事实；批末绕一圈去推必然推歪。</description></item>
/// <item><description>置位机器终态（<c>Failed → PartiallyCompleted</c>）只能在**真发布成功之后**
/// ⇒ 前面每一条 <c>return false</c> 都保持原终态（"损坏但一个字节都没救回来"照旧落 <c>Failed</c>，那是对的）。</description></item>
/// <item><description>其余物／工作区能不能动，取决于"计划里的文件是不是全部搬成"
/// （<see cref="PartialPublisher.Result.AllMoved"/>）—— 没搬成时它们**只**在工作区里，删掉等于丢用户的产物。</description></item>
/// </list>
///
/// <para>⛔ 它自己不判"该不该发"（<see cref="PartialPublishAdmission"/>）、不写一句文案
/// （<see cref="PartialPublishLog"/>）、不碰源包（清理的口径在其余物那一侧）。</para>
/// </summary>
internal sealed class PartialPublishPublisher
{
    private readonly IPartialPublishStepHost _host;
    private readonly PartialPublishLog _log;
    private readonly PartialPublishCandidateRegistry _candidates;
    private readonly PartialPublishAdmission _admission;

    internal PartialPublishPublisher(
        IPartialPublishStepHost host,
        PartialPublishLog log,
        PartialPublishCandidateRegistry candidates,
        PartialPublishAdmission admission)
    {
        _host = host;
        _log = log;
        _candidates = candidates;
        _admission = admission;
    }

    /// <summary>
    /// **部分完成发布的唯一入口**（收尾处调用）：取走候选 → 过准入 → 懒取清单 → 真搬 → 记账/终态/收尾。
    /// </summary>
    /// <returns>
    /// true = 这一单的工作区**不能清**（有内容没能发布，它们只在那里）；false = 按既有口径清。
    /// </returns>
    internal async Task<bool> TryPublishAsync(ArchiveTask task, bool cancelled)
    {
        PartialPublishStep.PartialPublishCandidate? candidate = _candidates.Take(task);

        if (candidate == null)
        {
            return false;
        }

        if (!_admission.TryAdmit(task, candidate, cancelled, out string packageOutputRoot))
        {
            return false;
        }

        IReadOnlyList<(string Path, long Size)> manifestEntries = candidate.ManifestEntries;

        if (manifestEntries.Count == 0 && !string.IsNullOrWhiteSpace(candidate.ArchivePath))
        {
            /*
             * 手上没有清单，现问一次引擎（**只在这一档**、只问一次）。
             *
             * 为什么必须补这一次：真机 `giu910` 那一单是 7z `-mhe`（文件名也加密）——
             * 前面几个候选连目录都列不出来，而**把包解到 98% 的那个候选**从没列过目录
             * （解压成功才会走校验、校验里才列目录，而它恰恰没成功）⇒ 不补这一问，
             * 这一档在最需要它的那个场景上永远拿不到清单、永远不发布。
             *
             * 密码用的是**这一趟最后那个候选**的：密码不对时它照样列不出来（返回失败），
             * 于是下游按"没有清单"办 —— ⛔ 绝不因为"列不出来"就放松任何一道闸门。
             */
            manifestEntries = await TryListAsync(task, candidate);
        }

        PartialPublishOutcome outcome = PartialPublishRunner.Run(
            candidate.StagingRoot,
            packageOutputRoot,
            manifestEntries,
            candidate.EngineFailedEntries,
            candidate.EngineReportedErrorCount,
            candidate.EngineReportedFailure);

        if (!outcome.Published)
        {
            _log.NotPublished(task, outcome);

            return false;
        }

        _log.Published(task, outcome);
        _log.DamagedArchive(task, outcome);

        if (outcome.Publish.RenamedCount > 0)
        {
            _log.Renamed(task, outcome);
        }

        _log.SourceKept(task);

        /*
         * 把"这一单救回来多少"记在任务上（批末诊断要用它说清"不是全丢"）。
         * 判据只有写在产生它的那一刻才是事实 —— 批末那一刻发布早就做完了，绕一圈去推必然推歪。
         */
        task.PartialPublishedCount = outcome.PublishedCount;
        task.PartialPublishDirectoryPath = outcome.DestinationDirectory;

        /*
         * ⛔ **机器终态跟着走：损坏 + 已经救回一部分 = 「部分完成」**（用户 2026-10-14 口径逐字：
         * 「这个损坏的压缩包……不完整的内容物就留下来，如果用户选择删除，那就删除……
         * 留下就留下，然后**最后的汇总肯定是报部分完成，橙色**」）。
         *
         * 位置：只能在**真发布成功之后**改（前面每一条 return false 都保持原终态）
         * ⇒ "损坏但一个字节都没救回来"照旧落 `Failed`（红），那是对的。
         * 色带与计数不用另写判据：`BatchSummarySeverity.FromOutcomes` / `BatchOutcomeTally`
         * 读的就是这一位（橙 = 有部分完成）。⚠ 需要先过 `FinalizeOutcomeIfPending`（它在收尾更早处，
         * 已经把 `Pending` 兜底成 `Failed`）—— 这里改的是**已经定下来的失败**，不是绕过兜底。
         */
        if (task.Outcome == TaskOutcome.Failed)
        {
            task.Outcome = TaskOutcome.PartiallyCompleted;
        }

        /*
         * 全部搬成之后才动其余物与工作区（「宁可少删」）：
         * 有文件没能发布时它们还在工作区里，那是这一份唯一的线索 —— 删掉等于丢用户的产物。
         */
        if (!outcome.Publish.AllMoved)
        {
            _log.RestKept(task, outcome);

            return true;
        }

        _host.PurgeRestItemsExceptChainRootSource(task);

        return false;
    }

    /// <summary>
    /// 懒取清单（**只服务部分完成发布**）：拿最后那个候选的密码问一次引擎。
    /// 列不出来（密码不对 / 加密头 / 引擎不可用）时返回空表 ⇒ 下游按"没有清单"办、什么都不发布。
    /// </summary>
    private async Task<IReadOnlyList<(string Path, long Size)>> TryListAsync(
        ArchiveTask task,
        PartialPublishStep.PartialPublishCandidate candidate)
    {
        try
        {
            ArchiveListResult list = await _host.Engine.ListAsync(
                ArchiveRequest.For(candidate.ArchivePath, candidate.Password),
                CancellationToken.None);

            return PartialPublishPlanner.ToManifestEntries(list);
        }
        catch (Exception ex)
        {
            _log.ListFailed(task, ex);

            return Array.Empty<(string, long)>();
        }
    }
}

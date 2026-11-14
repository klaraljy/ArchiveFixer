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
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RestHandlingTrigger = ArchiveFixer.Services.RestHandlingStep.RestHandlingTrigger;

namespace ArchiveFixer.Services;

/// <summary>
/// 「删除操作」这一档的**执行体**（生命周期：任务收尾 / 链尾 / 成功链清空 / 部分完成收尾；
/// 失败模型：删不成就落成结论，绝不把已经成功的解压拖成异常）。
///
/// <para>⛔ 删除门槛一条都不在这里放宽：全部由 <see cref="RestItemPurger"/> 的六道门槛与
/// <see cref="CleanupExecutor"/> 的执行语义钉着；本类只负责"什么时候试"与"怎么记账"。
/// 失败 / 部分完成 / 取消的任务一个字节都不会被删（不变量 1 红线）。</para>
///
/// <para>三个入口分属三个生命周期站点：<see cref="RunAsync"/>（任务/链尾按档处理）、
/// <see cref="PurgeRestAfterSuccess"/>（整条链全部成功 ⇒ 清空其余物）、
/// <see cref="PurgeItemsExceptChainRootSource"/>（部分完成 ⇒ 只留最外层源包）。</para>
/// </summary>
internal sealed class RestPurgeStep
{
    private readonly IRestHandlingStepHost _host;
    private readonly RestHandlingBatchPolicy _policy;

    internal RestPurgeStep(
        IRestHandlingStepHost host,
        RestHandlingBatchPolicy policy)
    {
        _host = host;
        _policy = policy;
    }

    /// <summary>
    /// 「删除操作」那一档的**唯一触发点**：任务收尾之后，按用户选的档处理它自己的其余物。
    ///
    /// <para>两档（用户 2026-09-25 第 32 条；2026-10-15 回收站档下线）：不动（Keep）/ 彻底删除。
    /// 删除档的门槛全在 <see cref="RestItemPurger"/> 里（终态必须是「完成」+ 校验通过 + 未取消 +
    /// 路径是定稿那一刻记下来的那一条 + 落在自己的输出根之内）——这里只负责"什么时候试"和"怎么记账"。
    /// **失败 / 部分完成 / 取消的任务一个字节都不会被删**（不变量 1 的红线）。</para>
    /// </summary>
    internal async Task RunAsync(
        ArchiveTask task,
        ScheduledTaskRuntime runtime,
        bool oneClickRun,
        RestHandlingTrigger trigger = RestHandlingTrigger.TaskEnd)
    {
        string mode = RestHandlingModes.Normalize(_policy.Mode);

        /*
         * ⚠ 一键处理里**不能在这里动手**（2026-09-25 第 32 条实现删除操作时被测试逮住的顺序陷阱）：
         * 任务收尾这一刻，其余物里躺着的正是**下一层要解的内层包** —— 当场删掉，续解就没粮草了
         * （实测：选了「彻底删除」之后 ContinuationLayers 直接变成 0，第二层再也解不出来）。
         * 所以一键档统一记成"留到链尾"，由
         * CompleteRootSourcePackagesAfterChainAsync → ChainRestSettlement.ApplyAfterChainAsync 处理；
         * 手动「只解压」是单层路径，没有链可等，当场按档处理。
         */
        if (oneClickRun && trigger == RestHandlingTrigger.TaskEnd)
        {
            if (!string.Equals(mode, RestHandlingModes.Keep, StringComparison.OrdinalIgnoreCase))
            {
                /*
                 * ⚠ 2026-10-03（普通档「逐层回收」）：续解层的过程物**已经当场回收掉了**
                 * （`PostProcessSuccessAsync` 里那一支），链尾那一档从今天起只处理最外层源包 ——
                 * 老文案"整条续解链跑完后再按「删除操作」处理"在这一档下就是**假话**
                 * （用户照它去等，什么都不会再发生）。
                 * ⛔ 只改这一支的措辞；最外层那一单照旧（它的其余物确实要等链尾）。
                 *
                 * ⚠ 2026-10-04（同一天的真机）：这一句以前是**无条件**的 —— 续解层「定稿搬运失败」
                 * 时它照样写着"已当场回收"，而那一刻回收判据一条都没过（`_host.PurgeLayerSourcePackage`
                 * 要么被 `commit.FailedCount > 0` 挡下、要么根本走不到），过程物**原封不动躺在盘上**。
                 * 现在**按事实说**（用户要求：真删了才说已回收，被拒就如实说没回收 + 原因）。
                 *
                 * 事实位 = `task.SourcePackageMove == Done`：对续解任务它**只可能**由
                 * `_host.PurgeLayerSourcePackage` 写（`源包移入其余物旧入口` 那一支对续解任务走不到，
                 * 上面那个 `else if (task.IsContinuationTask)` 先接住了），而它只在
                 * "真的删掉了至少一个文件、且一个都没失败"时才落 —— 正是"真删了"。
                 */
                if (_host.LayerReclaimThisBatch && task.IsContinuationTask)
                {
                    _host.AppendLog(
                        "INFO",
                        task.SourcePackageMove == SourcePackageMoveState.Done
                            ? string.Format(
                                CultureInfo.CurrentCulture,
                                StatusText.LayerProcessReclaimedFormat,
                                task.FileName)
                            : string.Format(
                                CultureInfo.CurrentCulture,
                                StatusText.LayerProcessNotReclaimedFormat,
                                task.FileName,
                                RestHandlingLog.DescribeLayerReclaimBlocker(task)));

                    return;
                }

                /*
                 * ⚠ 措辞要**如实**（第 35 条）：这一轮**没有**产生其余物时（内容物里就是那个内层包），
                 * 老文案"其余物先留着（里面还有要接着解的内层包）"会让人以为目录已经建好了 ——
                 * 用户按这句话去找其余物，只会更糊涂。有其余物才说"留着"，没有就说清"这一轮没有其余物"。
                 */
                _host.AppendLog(
                    "INFO",
                    string.IsNullOrWhiteSpace(task.RestDirectoryPath)
                        ? $"{task.LogName}：这一轮没有产生其余物（过程物就是内容物里的那个内层包）；"
                          + "整条续解链跑完后按「删除操作」处理内层包。"
                        : $"{task.LogName}：其余物先留着（里面还有要接着解的内层包），"
                          + "整条续解链跑完后再按「删除操作」处理。");
            }

            return;
        }

        if (string.Equals(mode, RestHandlingModes.Keep, StringComparison.OrdinalIgnoreCase))
        {
            // 不动其余物：也要**说一句**（用户 2026-09-25 真机反馈："源包确实没有了，但是其余物还在"）——
            // 静默正是问题本身。只对真的产出了其余物的任务写，免得整批刷屏。
            if (!string.IsNullOrWhiteSpace(task.RestDirectoryPath))
            {
                /*
                 * 一行说清（第 44 条：一次导出 713 KB，"每次不需要汇报得那么详细"）。
                 * "去哪儿改档位"这句话**批首已经说过一遍**（`其余物：本批保留（③页「删除操作」= 不动其余物）…`），
                 * 逐任务再念一遍 68 次只是把日志撑大。
                 */
                _host.AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.RestKeptCompactFormat,
                        task.FileName,
                        LogLineText.DescribeShortDestination(task.RestDirectoryPath, task.RestDirectoryPath),
                        LogLineText.CountRestEntries(task.RestDirectoryPath)));
            }

            return;
        }

        bool cancelled = _host.IsStopping || _host.OperationCts?.IsCancellationRequested == true;

        var plan = new DeletionPlan
        {
            Targets = string.IsNullOrWhiteSpace(task.RestDirectoryPath)
                ? Array.Empty<string>()
                : new[] { task.RestDirectoryPath },
            AllowedRoot = string.IsNullOrWhiteSpace(task.RestDirectoryPath)
                ? string.Empty
                : Path.GetDirectoryName(task.RestDirectoryPath) ?? string.Empty,
            DeletesEverything = true,
            Cancelled = cancelled,
            Reason = "任务收尾：按「删除操作」处理本任务其余物"
        };

        RestPurgeOutcome outcome;

        try
        {
            // 磁盘活（量大小 + 删目录）→ 放后台，别占住 UI 线程。
            outcome = await Task.Run(
                () =>
                {
                    var purger = new RestItemPurger(
                        _host.RestDeleteExecutor,
                        null,
                        ContentKeepRules.FromSettings(_host.Settings));

                    /*
                     * 空间不足：最后一层过程物是这条链**唯一要留下**的过程物。
                     * 递归核心已经把它复制到 task.OutputPath\其余物；这里按项保留，
                     * 其余过程物照旧永久删。⛔ 只认盘上确实存在的那一份，判不出就退回整体删除。
                     */
                    string? keepLast = task.LastProcessArtifactPath;

                    return _host.SpaceTightThisBatch
                        && !string.IsNullOrWhiteSpace(keepLast)
                        && File.Exists(keepLast)
                            ? purger.PurgeExcept(task, new[] { keepLast }, plan)
                            : purger.Purge(task, plan);
                });
        }
        catch (Exception ex)
        {
            // 这一层不该抛（RestItemPurger 内部全部收敛成结论），但"删东西"这件事
            // 绝不允许把已经成功的解压拖成异常：结论落成"没删成"，内容物不受影响。
            outcome = new RestPurgeOutcome
            {
                Message = $"{task.LogName}：处理其余物时出现意外错误：{ex.Message}"
            };
        }

        /*
         * 逐条 LogLines（`[彻底删除] 成功 路径=…；理由=…；条目数=…；总大小=…；说明=…`）
         * **只在没删成时写**（用户 2026-09-25 第 44 条：成功那一支一行就够，见下面）。
         */
        if (!outcome.Succeeded)
        {
            foreach (string line in outcome.LogLines)
            {
                _host.AppendLog("INFO", line);
            }
        }

        if (outcome.Succeeded)
        {
            runtime.RestPurged = true;
            runtime.PurgedBytes = outcome.FreedBytes;

            /*
             * 一行说清（用户 2026-09-25 第 44 条："最后删除文件你还弄得这么多的文字"）。
             * 以前这里是两句长文案（含理由、条目数、总大小、说明、完整路径、不可逆提示），
             * 68 个包就是 400 多行、7 万多字符。理由与"不可逆"已经在批首那条 + ③页常驻红提示里
             * 说过一次了，逐任务再念一遍只是把日志撑大；**失败时仍然全量**（下面那一支）。
             */
            _host.NoteBatchPurge(outcome.FreedBytes, permanent: true);

            _host.AppendLog("INFO", RestHandlingLog.DescribeRestPurgedLine(outcome));

            return;
        }

        runtime.PurgeNote = outcome.Message;

        // 其余物没删成也是"其余物还在"的一个答案 ⇒ 同一句话落到①页 / 批末诊断 / 失败清单。
        RestHandlingLog.RecordRestKept(task, outcome.Message);

        _host.AppendLog(outcome.Attempted ? "ERROR" : "INFO", outcome.Message);
    }

    /// <summary>
    /// 整条链成功后的其余物清空。**不检查最外层源包是否还在盘上** ——
    /// 成功链按用户档位删源包是允许的，过程物此时已不再承担恢复职责。
    /// 失败 / 部分完成仍然只能走 <see cref="PurgeRestItemsExceptChainRootSource"/>，
    /// 那条路继续要求源包在盘上。
    /// </summary>
    internal void PurgeRestAfterSuccess(
        ArchiveTask task,
        string? planAllowedRoot = null,
        CancellationToken cancellationToken = default)
    {
        if (task == null || string.IsNullOrWhiteSpace(task.RestDirectoryPath))
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            _host.AppendLog(
                "WARN",
                $"{task.FileName}：用户已请求停止，成功链的其余物清空已取消（一个字节都不删）");
            return;
        }

        DeletionPlan plan = new()
        {
            Targets = new[] { task.RestDirectoryPath },
            AllowedRoot = planAllowedRoot ?? string.Empty,
            Cancelled = cancellationToken.IsCancellationRequested,
            Reason = "成功链清空其余物"
        };

        RestPurgeOutcome purge = CleanupExecutor.ExecuteDeletion(
            plan,
            p => new RestItemPurger(
                    null,
                    null,
                    ContentKeepRules.FromSettings(_host.Settings))
                .Purge(task, p));

        _host.AppendLog(
            purge.Succeeded ? "INFO" : "WARN",
            $"{task.FileName}：整条链成功，其余物清空"
            + (purge.Succeeded ? "完成" : "失败")
            + $"：{purge.Message}");
    }

    /// <summary>
    /// 部分完成收尾：把其余物里**除这条链最外层源包以外**的项删掉（用户 2026-10-02 改的口径）。
    ///
    /// <para><b>为什么可以删</b>：其余物里装的都是过程物（内层包、抠出来的内嵌归档副本），
    /// 全都能从最外层源包重新解出来；用户原话是「我建议源包 + 已解出的内容物留着，其他的都删掉」——
    /// 这样盘上正好是"两份"，与③页小字那句承诺对得上。</para>
    ///
    /// <para><b>⛔ 安全闸门（这一条我来守）</b>：只有"这条链的最外层源包**此刻还在盘上**"时才动手；
    /// 它不在了（已被删 / 路径失效）⇒ **一个字节都不删**（判不出 ⇒ 什么都不做）——
    /// 源包是唯一能重建整条链的东西，它都没了还清过程物，等于把最后一份可恢复路径也删掉。</para>
    /// </summary>
    internal void PurgeRestItemsExceptChainRootSource(ArchiveTask task)
    {
        string rootSource = task.ChainRootIdentity;

        if (string.IsNullOrWhiteSpace(rootSource))
        {
            _host.AppendLog(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.PartialPublishRestKeptFormat,
                    task.FileName,
                    "算不出这条链的最外层源包是谁"));

            return;
        }

        string rootSourceFull = SafePathHelper.GetFullPathSafe(rootSource);

        if (rootSourceFull.Length == 0 || !SafePathHelper.FileExists(rootSourceFull))
        {
            _host.AppendLog(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.PartialPublishRestKeptFormat,
                    task.FileName,
                    $"这条链的最外层源包不在盘上了（{rootSource}）—— 它是唯一能把整条链重新解出来的东西，"
                    + "所以其余物里的过程物一个都不删"));

            return;
        }

        if (string.IsNullOrWhiteSpace(task.RestDirectoryPath))
        {
            // 这一单压根没建过其余物（部分完成这一档不生成其余物）⇒ 没事可做，不写日志。
            return;
        }

        var plan = new DeletionPlan
        {
            Targets = new[] { task.RestDirectoryPath },
            AllowedRoot = Path.GetDirectoryName(task.RestDirectoryPath) ?? string.Empty,
            DeletesEverything = false,
            Reason = "部分完成收尾：删其余物里除最外层源包以外的过程物"
        };

        RestPurgeOutcome purge = new RestItemPurger(
                null,
                null,
                ContentKeepRules.FromSettings(_host.Settings))
            .PurgeExcept(
            task,
            new[] { rootSourceFull },
            plan);

        _host.AppendLog(
            purge.Succeeded ? "INFO" : "WARN",
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                purge.Succeeded
                    ? StatusText.PartialPublishRestPurgedFormat
                    : StatusText.PartialPublishRestKeptFormat,
                task.FileName,
                purge.EntryCount,
                purge.Message));
    }
}

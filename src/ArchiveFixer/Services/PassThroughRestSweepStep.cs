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
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Services;

/// <summary>
/// 「过路层」的其余物按档清掉（生命周期：分卷组接手结算 / 链尾清扫；
/// 失败模型：只要有一份文件判不出归属 ⇒ 什么都不做）。
///
/// <para>两个入口同一条判据：<see cref="PurgeSettledProducer"/> 是"某一单只出过程物、该组后来由
/// 另一个可证完整的消费方接手"那一刻的结算；<see cref="SweepAsync"/> 是链尾把根任务那棵树里
/// 同类形状的其余物扫一遍。⛔ 只碰根任务这棵树、只碰"每一个文件都已被下游接手"的那些；
/// 失败 / 取消的那一单原样留着（那是现场凭证，红线）。</para>
/// </summary>
internal sealed class PassThroughRestSweepStep
{
    private readonly IRestHandlingStepHost _host;
    private readonly RestHandlingBatchPolicy _policy;
    private readonly ChainRestSettlement _settlement;

    internal PassThroughRestSweepStep(
        IRestHandlingStepHost host,
        RestHandlingBatchPolicy policy,
        ChainRestSettlement settlement)
    {
        _host = host;
        _policy = policy;
        _settlement = settlement;
    }

    /// <summary>
    /// **过路层那一单的其余物按档处理**（用户 2026-10-06 拍板：「其余物会在每层的过程中会删掉，
    /// 我无法理解为什么还会有其余物留着」；他两次追问的 `111\111\其余物\111.zip`（48.35 MB）就是这一格）。
    ///
    /// <para><b>形状</b>：某一单只出**过程物**（它解出来的入口包 / 分卷片就是下一层的输入），
    /// 自己因此停在中途；那一组后来由**另一个当场核过清单、可证完整**的消费方解开
    /// ⇒ 它自己那份其余物不再有用，按「删除操作」处理。</para>
    ///
    /// <para>⛔ **六道门槛一条不绕过**（机器终态 / 完整性证据 / 其余物目录 / 形状 / 输出范围 /
    /// 半套分卷），唯一差别是**证据来源**：这一单自己停在中途 ⇒ 它自己的完整性判不出来，
    /// 该按证据的是**接手它的那一单**（`completenessEvidence`）。⛔ 那条证据只用于这一次删除，
    /// 不写回它自己的账。</para>
    ///
    /// <para>⛔ 放行之前还要逐条确认：其余物里**每一个文件的名字都属于这一组**
    /// （= 都是被那次成功解压接手的过程物）；只要有一个不属于 ⇒ 什么都不做（判不出就不动）。</para>
    ///
    /// <para><b>⚠ 2026-10-11 环台实测（取证，⛔ 不是推断）</b>：把这一档与
    /// <see cref="SweepAsync"/> **两条都整条关掉**
    /// （各加一句 early return），整个 <c>SiblingFolderVolumeGatherTests</c>（19 条）
    /// **照旧 18 通过 / 1 跳过 / 0 失败**，`彻底删除：111\其余物（8 项 / 7.0 MB）`照旧执行 ⇒
    /// 现在这几条用例真正覆盖的是**链尾那一遍**「这一单自己的其余物按档处理」
    /// （关掉它 ⇒ `真机形状_过路层那一单的其余物按档删掉_48MB那处` 当场红）。
    /// ⇒ 本档在现有夹具里**没有一次真的删成功**（19 次调用全部停在闸门上，逐条见日志里那句
    /// `过路层的其余物这次没删 —— …`），⛔ **不许把"用例绿"读成"这一档验过了"**；
    /// 哪一天真机日志里出现 `过路层的其余物已按「删除操作」处理`，那才是它第一次真干活。</para>
    /// </summary>
    internal void PurgeSettledProducer(
        ArchiveTask producer,
        ArchiveTask consumer,
        string groupBaseName)
    {
        if (producer == null || !_host.PassThroughRestHandled.Add(producer))
        {
            return;
        }

        string restDirectory = producer.RestDirectoryPath;

        if (string.IsNullOrWhiteSpace(restDirectory) || !Directory.Exists(restDirectory))
        {
            // 「判不出 ⇒ 什么都不做」：把判据输入写进日志（⛔ 判据本身一个字不动）。
            _host.LogPassThroughRestCriterionInputs(
                producer,
                consumer,
                groupBaseName,
                restDirectory,
                "没记下这一单定稿实际用的其余物目录（或它已经不在盘上）");

            return;
        }

        string mode = RestHandlingModes.Normalize(_policy.Mode);

        if (string.Equals(mode, RestHandlingModes.Keep, StringComparison.OrdinalIgnoreCase))
        {
            // 「不动其余物」这一档：一个字节都不删（不变量 1 的默认档）。
            return;
        }

        try
        {
            /*
             * ⛔ 前置闸门的引用名走**唯一出口**（`_host.AllRestFilesBelongToGroup` → `ResolveGroupReferenceName`）：
             * 原来写死 `<组基名>.zip` ⇒ 非 ZIP 族这一层永远判"里面有别人的东西" ⇒ 永远不删
             * （用户选了「彻底删除」也不动）；ZIP 族却删 —— 同一个设置两种结果（2026-10-11 统一）。
             */
            if (!_host.AllRestFilesBelongToGroup(
                    consumer,
                    groupBaseName,
                    Directory.EnumerateFiles(restDirectory, "*", SearchOption.AllDirectories)))
            {
                // 里面还有不属于这一组的东西 / 读不到 ⇒ 判不出 ⇒ 什么都不做（原样留着）。
                _host.LogPassThroughRestCriterionInputs(
                    producer,
                    consumer,
                    groupBaseName,
                    restDirectory,
                    "其余物里有不属于这一组的文件（判不出它是不是被接手的过程物）");

                return;
            }
        }
        catch
        {
            return;
        }

        ResultCompletenessVerdict evidence = ResultCompletenessClassifier.Classify(consumer);

        if (!evidence.AllowsSourceRemoval)
        {
            _host.LogPassThroughRestCriterionInputs(
                producer,
                consumer,
                groupBaseName,
                restDirectory,
                "接手方那一单的完整性证据不允许删源（不是「可证完整 + 校验通过」）");

            return;
        }

        bool cancelled = _host.IsStopping || _host.OperationCts?.IsCancellationRequested == true;

        var plan = new DeletionPlan
        {
            Targets = new[] { restDirectory },
            AllowedRoot = Path.GetDirectoryName(restDirectory) ?? string.Empty,
            DeletesEverything = false,
            Cancelled = cancelled,
            Reason = "过路层那一单的其余物：本组已由消费方可证完整解开"
        };

        RestPurgeOutcome outcome;

        try
        {
            outcome = CleanupExecutor.ExecuteDeletion(
                plan,
                p => new RestItemPurger(
                        _host.RestDeleteExecutor,
                        null,
                        ContentKeepRules.FromSettings(_host.Settings))
                    .Purge(producer, p, evidence, completenessEvidenceTask: consumer));
        }
        catch (Exception ex)
        {
            // 删东西绝不许把已经成立的结论拖成异常：落成"没删成"，别的什么都不变。
            _host.AppendLog("WARN", $"{producer.LogName}：处理过路层的其余物时出现意外错误：{ex.Message}");
            return;
        }

        _host.AppendLog(
            outcome.Succeeded ? "INFO" : "WARN",
            outcome.Succeeded
                ? $"{producer.LogName}：过路层的其余物已按「删除操作」处理（{outcome.EntryCount} 项 / {outcome.FreedBytes} 字节）"
                  + $"—— 这一组的内容已由「{consumer.FileName}」解开。"
                : $"{producer.LogName}：过路层的其余物这次没删成 —— {outcome.Message}");
    }

    /// <summary>
    /// **"过路层的其余物"该不该放行**（唯一判据，纯函数；真机 CCCC 2026-10-06）。
    ///
    /// <para><b>为什么不能只看终态"成功"</b>：真机那个现场的过路层是 `111.rar` —— 它只出过程物
    /// （解出来的入口包 `111.zip` 被**采纳进别的层**），于是它自己那条链停在中途、终态是
    /// **「部分完成」** ⇒ 按"只放行成功"那条判据它**永远被挡在外面**，那份其余物
    /// （`111\111\其余物\111.zip`，48.35 MB）就一直留在盘上（用户报的"其余物还有"）。</para>
    ///
    /// <para><b>但也不许改成"部分完成就删"</b> —— 那是失败现场的凭证（红线：
    /// 失败 / 部分完成 / 取消 ⇒ 其余物一个字节都不动）。⇒ 放行条件换成**可证"这份其余物里的东西
    /// 已经全被下游接手了"**（三条同时成立）：</para>
    /// <list type="number">
    /// <item><description>这一单**没失败**、也没被取消（`Failed` / `Cancelled` 一律不动）；</description></item>
    /// <item><description>其余物目录**在盘上**且**不是空的**（空壳由既有那一档处理，这里不抢它的活）；</description></item>
    /// <item><description>其余物里**每一个文件**都出现在那份"借来的源片"账
    /// <c>_host.ConsumedVolumeSources</c> 里（= 它已经被下游某一单接手用过了）——
    /// 只要有一个文件没进账 ⇒ 判不出 ⇒ **什么都不做**。</description></item>
    /// </list>
    /// </summary>
    internal static bool CanSweep(
        ArchiveTask? task,
        IReadOnlyDictionary<string, string>? consumedVolumeSources)
    {
        if (task == null || string.IsNullOrWhiteSpace(task.RestDirectoryPath))
        {
            return false;
        }

        // ① 失败 / 取消 ⇒ 红线：其余物一个字节都不动（那是失败现场的凭证）。
        if (task.Outcome is TaskOutcome.Failed or TaskOutcome.Cancelled)
        {
            return false;
        }

        if (task.OutputVerification == OutputVerificationOutcome.Failed)
        {
            return false;
        }

        string restDirectory = task.RestDirectoryPath;

        if (!Directory.Exists(restDirectory))
        {
            return false;
        }

        if (consumedVolumeSources == null || consumedVolumeSources.Count == 0)
        {
            return false;
        }

        // ② 其余物里每一个文件都必须在"借来的源片"账里（= 已被下游接手）。
        bool any = false;

        foreach (string file in Directory.EnumerateFiles(restDirectory, "*", SearchOption.AllDirectories))
        {
            any = true;

            bool adopted = false;

            foreach (string key in consumedVolumeSources.Keys)
            {
                if (SafePathHelper.PathEquals(key, file))
                {
                    adopted = true;
                    break;
                }
            }

            if (!adopted)
            {
                // 判不出 / 还有一个没被接手的 ⇒ 什么都不做。
                return false;
            }
        }

        return any;
    }

    /// <summary>
    /// 把**这一个根任务那条链里"过路层"的其余物**按档处理掉（含 `111\111\其余物` 这种中间层）。
    ///
    /// <para>只处理"落在根任务那棵树里、且放行判据成立"的那几单 —— 这是与"对全表扫一遍"的关键差别：
    /// 后者会碰到别的链、也会碰到链中途失败要留现场的那几单（实测把三条红线用例打红）。</para>
    /// </summary>
    internal async Task SweepAsync(ArchiveTask rootTask, CancellationToken cancellationToken)
    {
        string? rootDirectory = Path.GetDirectoryName(rootTask.OutputPath);

        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            string contentRoot = rootTask.ContentDirectoryPath;

            rootDirectory = string.IsNullOrWhiteSpace(contentRoot)
                ? null
                : Path.GetDirectoryName(contentRoot);
        }

        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return;
        }

        foreach (ArchiveTask restOwner in _host.SnapshotTaskTable(_host.Tasks))
        {
            if (restOwner == null
                || ReferenceEquals(restOwner, rootTask)
                || string.IsNullOrWhiteSpace(restOwner.RestDirectoryPath))
            {
                continue;
            }

            // ⛔ 只碰**这个根任务那棵树里**的其余物（别的链、别的包一律不动）。
            if (!IsRestInsideRootTree(rootDirectory, restOwner.RestDirectoryPath))
            {
                continue;
            }

            if (!CanSweep(restOwner, _host.ConsumedVolumeSources))
            {
                continue;
            }

            await _settlement.ApplyAfterChainAsync(restOwner, null, cancellationToken);
        }
    }

    /// <summary>
    /// **这一份其余物是不是落在"根任务那棵树"里**（⛔ 只碰这一棵树：别的链、别的包一律不动）。
    ///
    /// <para>⚠ 参数顺序按 <see cref="ArchivePathGuard.IsInsideRoot"/> 的定义来：**第一个是根、第二个是待查路径**。
    /// 真机 CCCC 2026-10-06 18:13 那一批：这里两个参数写反了 ⇒ 每一份过路层其余物都被判成"不在树里"
    /// ⇒ <see cref="SweepAsync"/> 整段成了死代码 ⇒
    /// `111\111\其余物\111.zip`（48.35 MB）一直留到用户看见（他原话：「**还有 48MB 的问题**」）。</para>
    /// </summary>
    internal static bool IsRestInsideRootTree(string? rootDirectory, string? restDirectory)
        => !string.IsNullOrWhiteSpace(rootDirectory)
           && !string.IsNullOrWhiteSpace(restDirectory)
           && ArchivePathGuard.IsInsideRoot(rootDirectory, restDirectory, out _);
}

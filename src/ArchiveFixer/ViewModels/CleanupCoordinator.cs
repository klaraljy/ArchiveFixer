using ArchiveFixer.BindingModels;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 清理执行：预览 → 确认 → 删除 → 汇总；UI 通过回调/对话框服务，不直接碰窗口。
/// </summary>
internal sealed class CleanupCoordinator
{
    private readonly Func<IReadOnlyList<ArchiveTask>> _tasks;
    private readonly Func<string, string> _promptNoCheckedTask;
    private readonly Action<string, string> _log;
    private readonly Action _enterBusy;
    private readonly Action _exitBusy;
    private readonly Action<string, ArchiveTask, CleanupPreview, CleanupOutcome> _writeOutcome;
    private readonly DialogService _dialogService;
    private readonly MaintenanceCleanupService _cleanup;

    public CleanupCoordinator(
        Func<IReadOnlyList<ArchiveTask>> tasks,
        Func<string, string> promptNoCheckedTask,
        Action<string, string> log,
        Action enterBusy,
        Action exitBusy,
        Action<string, ArchiveTask, CleanupPreview, CleanupOutcome> writeOutcome,
        DialogService dialogService,
        MaintenanceCleanupService cleanup)
    {
        _tasks = tasks;
        _promptNoCheckedTask = promptNoCheckedTask;
        _log = log;
        _enterBusy = enterBusy;
        _exitBusy = exitBusy;
        _writeOutcome = writeOutcome;
        _dialogService = dialogService;
        _cleanup = cleanup;
    }

    /// <summary>
    /// 其余物/空文件夹的删除流程（③「清理与删除」页那三个按钮）：
    /// **判定作用于谁 → 预览 → 红色确认（默认回收站，勾选框切激进档）→（激进档）二次确认 → 后台执行 → 写日志**。
    ///
    /// <para>
    /// <b>选中口径（2026-09-24 用户第 12 条亲自拍板：一律只认勾选）</b>：这三个入口统一走
    /// <see cref="ResolveCleanupTargets"/> —— 只作用于**勾选**的任务；一个都没勾就只提示
    /// （<see cref="StatusText.NoCheckedTaskPromptFormat"/>），一个字节都不动。
    /// ⛔ 曾经那条"一个都没勾时退回当前点中的那一行"的兜底**已删除**：用户原话
    /// "你只需要操作我选中的文件，其他的不用管"，兜底等于替他决定要动哪些文件。
    /// （早期还有一个反向缺陷：这三个入口只看当前行，勾了任务的用户反而收到"请先选中一个任务"。）
    /// </para>
    /// <para>
    /// 多任务时**合并成一次确认**（用户 2026-09-22 拍板，原话："要合并成 1 次确认，不要有冗余操作"）：
    /// 先把**全部**目标各预览一遍（后台），把"每个任务的作用范围 + 合计条目数/字节数 +
    /// 哪些任务含源包"合成一个确认框，用户点一次就算数；确认之后逐个执行，
    /// 单个任务失败不中断整批（不变量 9），最后给一次汇总（成功 N / 失败 M + 原因 + 释放字节数）。
    /// 以前是"勾了 5 个就弹 5 次预览 + 5 次确认"，那正是用户说的冗余。
    /// </para>
    /// <para>
    /// 「删除本目录全部其余物」保持整目录语义：同目录只算一次（<see cref="ExpandCleanupTargets"/>），
    /// 所以它天然只会有一次确认，且确认框里点名"影响该目录下 N 个包"。
    /// </para>
    /// <para>
    /// 线程纪律（本项目历史上因 UI 线程干重活卡死过）：
    /// 预览统计与删除执行全部在 <see cref="Task.Run(System.Action)"/> 里；
    /// 界面线程只做两件事 —— 弹确认框、把结论写进日志/提示。
    /// </para>
    /// <para>
    /// 取消的语义：任何一步没确认，那一个任务就**什么都不做**（一个字节都不动），只留一条日志。
    /// </para>
    /// </summary>
    /// <param name="scope">作用域类型（其余物 / 空文件夹）。</param>
    /// <param name="deleteScope">
    /// 作用域大小：只删每个目标任务自己那一份（默认），还是删整个共享目录下的全部。
    /// 后者只由「删除本目录全部其余物…」这个显式入口传进来。
    /// </param>
    internal async Task RunAsync(
        CleanupScope scope,
        ArtifactDeleteScope deleteScope,
        IReadOnlyList<ArchiveTask>? selectedTargets = null,
        DeleteMode? requestedMode = null)
    {
        bool everything = deleteScope == ArtifactDeleteScope.EverythingInDirectory;

        string title = selectedTargets != null
            ? scope == CleanupScope.EmptyFolders
                ? "清理当前任务空文件夹"
                : requestedMode == DeleteMode.RecycleBin
                    ? "把当前任务产物移到回收站"
                    : "彻底删除当前任务产物"
            : scope == CleanupScope.EmptyFolders
                ? "清理空文件夹"
                : everything ? "删除本目录全部其余物" : "删除其余物";

        CleanupTargetSelection selection = CleanupSelection.Resolve(_tasks(), selectedTargets);

        _log(selection.HasTarget ? "INFO" : "WARN", CleanupSelection.Describe(title, selection));

        if (!selection.HasTarget)
        {
            // 一律只认勾选：一个都没勾就只提示、什么都不做（用户 2026-09-24 第 12 条）。
            _dialogService.ShowWarning(_promptNoCheckedTask(title));
            return;
        }

        List<ArchiveTask> targets = CleanupSelection.Expand(selection, everything);

        _enterBusy();

        try
        {
            // ① 预览**全部**目标（每个任务各一条日志，便于事后核对"这次动了哪些目录"）。
            var plans = new List<CleanupTaskPlan>(targets.Count);

            foreach (ArchiveTask task in targets)
            {
                plans.Add(await BuildPlanAsync(scope, deleteScope, task));
            }

            List<CleanupTaskPlan> actionable = plans.Where(plan => plan.IsActionable).ToList();

            if (actionable.Count == 0)
            {
                // 没有可删的：一次提示说清每个任务为什么没得删（以前是每个任务一个提示框）。
                string nothing = BuildNothingToCleanText(title, plans);

                _log("WARN", nothing.Replace(Environment.NewLine, " "));
                _dialogService.ShowInfo(nothing);
                return;
            }

            CleanupPreview merged = MergeCleanupPreviews(scope, actionable);
            string scopeNote = BuildBatchScopeNote(scope, actionable);

            /*
             * ② **一次**确认（红色）。档位语义：⛔ 2026-10-15 起手工清理**只剩一个动作**
             * （彻底删除 —— 回收站那一档整条下线），所以这里不再有"默认档 / 另一档"之说，
             * 档位构造仍然只有 DeriveCleanupDecision.CleanupDecision 一处实现。
             */
            CleanupDecisionPlanner.CleanupDecision decision = requestedMode is { } forcedMode
                ? new CleanupDecisionPlanner.CleanupDecision
                {
                    DefaultMode = forcedMode,
                    OptionMode = forcedMode,
                    ConfirmText = CleanupDecisionPlanner.BuildCleanupConfirmText(title, actionable, merged, forcedMode, scopeNote),
                    ConfirmButtonText = forcedMode == DeleteMode.Permanent ? "彻底删除" : "移入回收站",
                    OptionText = string.Empty,
                    OptionCheckedByDefault = false
                }
                : CleanupDecisionPlanner.DeriveCleanupDecision(
                    merged,
                    title,
                    scopeNote,
                    actionable);

            bool confirmed = _dialogService.ShowDestructiveConfirmWithOption(
                decision.ConfirmText,
                decision.ConfirmButtonText,
                decision.OptionText,
                optionCheckedByDefault: decision.OptionCheckedByDefault,
                out bool optionChecked);

            if (!confirmed)
            {
                _log(
                    "INFO",
                    $"{title}：用户取消，{actionable.Count} 个任务一个字节都没动"
                    + "（本次只弹这一次确认，取消即整批不做）。");
                return;
            }

            DeleteMode mode = decision.Resolve(optionChecked);

            if (mode == DeleteMode.Permanent)
            {
                /*
                 * ③ 激进档的**二次确认**：整批只再来这一次（红色 + 必须勾"我知道不可恢复"）。
                 * 批里每删一个都问一遍不是更安全，只是更烦；真正的红线是"不可逆操作必须显式确认"。
                 */
                bool acknowledged = _dialogService.ShowDestructiveConfirm(
                    CleanupDecisionPlanner.BuildCleanupConfirmText(title, actionable, merged, DeleteMode.Permanent, scopeNote),
                    "彻底删除",
                    "我知道彻底删除不可恢复，这些内容不会进回收站",
                    out bool irreversibleAcknowledged);

                if (!acknowledged || !irreversibleAcknowledged)
                {
                    _log(
                        "INFO",
                        $"{title}：没有通过彻底删除的二次确认，{actionable.Count} 个任务一个字节都没动。");
                    return;
                }
            }

            // ④ 逐个执行（后台）。**单个任务失败不中断整批**：这一条记 ERROR 之后继续下一个。
            var tally = new CleanupDecisionPlanner.BatchCleanupTally();

            foreach (CleanupTaskPlan plan in actionable)
            {
                ArchiveTask task = plan.Task!;

                try
                {
                    CleanupPreview preview = plan.Preview!;

                    CleanupOutcome outcome = await Task.Run(() => scope == CleanupScope.EmptyFolders
                        ? _cleanup.CleanEmptyFolders(task.OutputPath, mode)
                        : _cleanup.CleanProcessArtifacts(task, mode, preview, deleteScope));

                    _writeOutcome(title, task, preview, outcome);
                    tally.Record(task, outcome);
                }
                catch (Exception ex)
                {
                    tally.RecordException(task, ex.Message);
                    _log("ERROR", $"{title}（{task.FileName}）失败：" + ex.Message);
                }
            }

            // ⑤ 汇总一次：成功 N / 失败 M（各自原因）/ 释放字节数。
            string summary = CleanupDecisionPlanner.BuildBatchCleanupSummary(title, mode, actionable.Count, tally);

            /*
             * 有任何一项没删掉（或整批一项都没成功）就绝不能显示成"全部成功"
             * （不变量 6 的同一口径）：用警告框把原因列出来。
             */
            if (tally.HasAnyFailure)
            {
                _dialogService.ShowWarning(summary);
            }
            else
            {
                _dialogService.ShowInfo(summary);
            }
        }
        finally
        {
            _exitBusy();
        }
    }

    /// <summary>
    /// 一个清理目标"这次到底会删什么"的预览结论（多任务合并确认的输入）。
    ///
    /// <para>
    /// <see cref="Preview"/> 为 null 时 <see cref="SkipReason"/> 说明为什么这个任务没得删
    /// （例如"还没有输出目录（先解压一次）"）—— 那种任务不参与确认框，但要在
    /// "没有可删的"提示里点名，免得用户以为程序把它漏了。
    /// </para>
    /// </summary>


    /// <summary>预览一个清理目标（后台线程干活；界面线程只拿结论）。</summary>
    private async Task<CleanupTaskPlan> BuildPlanAsync(
        CleanupScope scope,
        ArtifactDeleteScope deleteScope,
        ArchiveTask task)
    {
        if (string.IsNullOrWhiteSpace(task.OutputPath))
        {
            const string reason = "还没有输出目录（先解压一次）";

            _log("WARN", $"清理：任务「{task.FileName}」{reason}，已跳过。");

            return new CleanupTaskPlan { Task = task, SkipReason = reason };
        }

        CleanupPreview preview = await Task.Run(() => scope == CleanupScope.EmptyFolders
            ? _cleanup.PreviewEmptyFolders(task.OutputPath)
            : _cleanup.PreviewProcessArtifacts(task, deleteScope));

        _log(preview.HasTarget ? "INFO" : "WARN", $"清理（{task.FileName}）：{preview.Message}");

        return new CleanupTaskPlan
        {
            Task = task,
            Preview = preview,
            SkipReason = preview.HasTarget ? string.Empty : preview.Message,
            ScopeNote = CleanupDecisionPlanner.BuildSharedScopeNote(scope, task, preview)
        };
    }

    /// <summary>
    /// 把多个目标的预览合成一份（确认框上的"合计"就取它）。
    ///
    /// 合口径：条目数 / 字节数 / 顶层项数**相加**；"读不全"只要有一个不完整就整体标注；
    /// 作用范围同目录时写那一个目录，否则写"N 个任务（下面逐个列出）"。
    /// </summary>
    internal static CleanupPreview MergeCleanupPreviews(CleanupScope scope, IReadOnlyList<CleanupTaskPlan> plans)
    {
        var actionable = plans.Where(plan => plan.IsActionable).ToList();

        int itemCount = 0;
        int entryCount = 0;
        long totalBytes = 0;
        bool determined = true;
        int sourcePackageCount = 0;
        int affectedPackageCount = 0;

        var items = new List<string>();
        var sourceNames = new List<string>();
        var affectedNames = new List<string>();
        var scopePaths = new List<string>();

        ArtifactCleanupScope? resolvedScope = null;

        foreach (CleanupTaskPlan plan in actionable)
        {
            CleanupPreview preview = plan.Preview!;

            itemCount += preview.ItemCount;
            entryCount += preview.EntryCount;
            totalBytes += preview.TotalBytes;
            determined &= preview.Determined;
            sourcePackageCount += preview.SourcePackageCount;
            affectedPackageCount += preview.AffectedPackageCount;

            foreach (string item in preview.Items)
            {
                if (items.Count < CleanupPreview.MaxListedItems)
                {
                    items.Add(item);
                }
            }

            foreach (string name in preview.SourcePackageNames)
            {
                if (sourceNames.Count < CleanupPreview.MaxListedItems * 2)
                {
                    sourceNames.Add(name);
                }
            }

            foreach (string name in preview.AffectedPackageNames)
            {
                if (affectedNames.Count < CleanupPreview.MaxListedItems * 2)
                {
                    affectedNames.Add(name);
                }
            }

            if (!string.IsNullOrWhiteSpace(preview.ScopePath) && !scopePaths.Contains(preview.ScopePath))
            {
                scopePaths.Add(preview.ScopePath);
            }

            if (preview.ResolvedScope?.DeletesEverythingInDirectory == true)
            {
                resolvedScope = preview.ResolvedScope;
            }
        }

        string scopePath = scopePaths.Count == 1
            ? scopePaths[0]
            : $"{actionable.Count} 个任务各自的目录（下面逐个列出）";

        string message = actionable.Count == 1
            ? actionable[0].Preview!.Message
            : $"{actionable.Count} 个任务：合计顶层 {itemCount} 项 / {entryCount} 个条目 / {totalBytes} 字节。";

        return new CleanupPreview
        {
            Scope = scope,
            ScopePath = scopePath,
            AllowedRoot = actionable.Count == 1 ? actionable[0].Preview!.AllowedRoot : string.Empty,
            ResolvedScope = resolvedScope,
            HasTarget = actionable.Count > 0,
            ItemCount = itemCount,
            EntryCount = entryCount,
            TotalBytes = totalBytes,
            Determined = determined,
            Items = items,
            SourcePackageNames = sourceNames,
            SourcePackageCount = sourcePackageCount,
            AffectedPackageCount = affectedPackageCount,
            AffectedPackageNames = affectedNames,
            Message = message
        };
    }

    /// <summary>
    /// 多任务时的作用域提醒：把各任务那句"只删自己那一份 / 会影响整个目录"合并成一句。
    ///
    /// <para>
    /// 为什么要合并：单任务那句提醒（<see cref="BuildSharedScopeNote"/>）在批里会出现 N 次，
    /// 而确认框只有一次 —— 直接拼 N 遍就是复读。去重之后保留**不同**的说法：
    /// "只删自己那一份"和"会影响同目录的其它包"是两种完全不同的后果，都得留下。
    /// </para>
    /// </summary>
    internal static string BuildBatchScopeNote(CleanupScope scope, IReadOnlyList<CleanupTaskPlan> plans)
    {
        if (plans.Count <= 1)
        {
            return plans.Count == 1 ? plans[0].ScopeNote : string.Empty;
        }

        var notes = new List<string>();

        foreach (CleanupTaskPlan plan in plans)
        {
            if (!string.IsNullOrWhiteSpace(plan.ScopeNote) && !notes.Contains(plan.ScopeNote))
            {
                notes.Add(plan.ScopeNote);
            }
        }

        string shared = $"本次一次确认、覆盖 {plans.Count} 个任务："
                        + (scope == CleanupScope.EmptyFolders
                            ? "每个任务的输出根都会清掉其中“任意层级都没有文件”的子目录。"
                            : "每个任务各删自己那一份其余物（下面逐个列出各自的目录）。");

        return notes.Count == 0 ? shared : shared + Environment.NewLine + string.Join(Environment.NewLine, notes);
    }

    /// <summary>"一个都没得删"时的合并提示（点名每个任务的原因，而不是弹出 N 个提示框）。</summary>
    internal static string BuildNothingToCleanText(string title, IReadOnlyList<CleanupTaskPlan> plans)
    {
        if (plans.Count == 0)
        {
            return $"{title}：没有可处理的任务。";
        }

        if (plans.Count == 1)
        {
            return $"{title}（{plans[0].Task?.FileName}）：{plans[0].SkipReason}";
        }

        var builder = new StringBuilder();

        builder.AppendLine($"{title}：{plans.Count} 个任务都没有可删的东西，一个字节都没动。");

        foreach (CleanupTaskPlan plan in plans)
        {
            builder.AppendLine("· " + (plan.Task?.FileName ?? "（未知任务）") + "：" + plan.SkipReason);
        }

        return builder.ToString().TrimEnd();
    }
}


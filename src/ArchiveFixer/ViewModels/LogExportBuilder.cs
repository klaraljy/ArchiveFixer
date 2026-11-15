using ArchiveFixer.BindingModels;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 日志导出文本的纯构造：不弹窗、不写盘、不改设置；窗口/落盘仍由 MainViewModel 命令负责。
/// </summary>
internal sealed class LogExportBuilder
{
    private readonly Func<IReadOnlyList<ArchiveTask>> _tasks;
    private readonly Func<IReadOnlyList<OperationLogItem>> _logs;
    private readonly Func<string> _outputLocationForLog;
    private readonly ExtractionCoordinator _extraction;
    private readonly TaskSummaryService _summary;
    private readonly AppSettings _settings;

    public LogExportBuilder(
        Func<IReadOnlyList<ArchiveTask>> tasks,
        Func<IReadOnlyList<OperationLogItem>> logs,
        Func<string> outputLocationForLog,
        ExtractionCoordinator extraction,
        TaskSummaryService summary,
        AppSettings settings)
    {
        _tasks = tasks;
        _logs = logs;
        _outputLocationForLog = outputLocationForLog;
        _extraction = extraction;
        _summary = summary;
        _settings = settings;
    }

    private IReadOnlyList<ArchiveTask> Tasks => _tasks();

    private IReadOnlyList<OperationLogItem> Logs => _logs();

    internal IReadOnlyList<string> BuildHeader()
    {
        var lines = new List<string>
    {
        "================ ArchiveFixer 日志导出 ================"
    };

        DateTime? first = Tasks.Where(task => task.StartTime.HasValue).Min(task => task.StartTime);
        DateTime? last = Tasks.Where(task => task.EndTime.HasValue).Max(task => task.EndTime);

        /*
         * ⛔ 2026-11-01 真机：抬头写 14:49–15:57，正文最后事件却是 16:07 ——
         * 因为 `Tasks.EndTime` 只到最后一单解压结束，批末的第二大步/收尾事件没有任务时间。
         * 导出的正文就是"本次操作"那一段日志 ⇒ 抬头直接用那段日志的首末时间（有就用它）。
         */
        if (Logs.Count > 0)
        {
            int markerIndex = -1;

            for (int index = 0; index < Logs.Count; index++)
            {
                if (Logs[index].Message.Contains("本次操作开始", StringComparison.Ordinal))
                {
                    markerIndex = index;
                }
            }

            if (markerIndex >= 0)
            {
                first = Logs[markerIndex].Time;
                last = Logs[^1].Time;
            }
        }

        if (first.HasValue && last.HasValue)
        {
            lines.Add($"时间范围：{first:yyyy-MM-dd HH:mm:ss} → {last:yyyy-MM-dd HH:mm:ss}"
                + (last.Value > first.Value ? $"（用时 {(last.Value - first.Value).ToString(@"hh\:mm\:ss")}）" : string.Empty));
        }
        else
        {
            lines.Add($"导出时间：{ArchiveFixer.Helpers.Clock.Now:yyyy-MM-dd HH:mm:ss}");
        }

        /*
         * 数法只有一份（`Models/BatchOutcomeTally`）：批末「本批汇总」那一行、一键汇总那一行、
         * 以及这里三处共用，⛔ 别在这里再写一遍 `Count(task => …)`。
         *
         * 旧写法（2026-10-02 真机日志里读出来的）漏了两件事：
         * ① 跟班卷（同一分卷组的后续卷）被算进"跳过" —— 同一份日志里批末说"跳过 0"、
         *    头部却会把每一卷都数成一次"跳过"；
         * ② 只报成功 / 失败 / 跳过三档 —— "既没成功、也没失败、也没跳过"的那些任务
         *    （例如导入完还没跑）一个数都不占，于是头部写着
         *    「任务数：10（成功 0 / 失败 0 / 跳过 0）」，读的人当场对不上账。
         */
        /*
         * ⛔ 2026-11-01：导出正文是"本次操作"（可能跨多批），不能用**最后一批**的计数
         * （真机抬头写任务数 1、明细 13 行）。按当前任务表算，与任务明细同口径。
         */
        BatchOutcomeTally tally = BatchOutcomeTally.Count(Tasks);

        /*
         * 任务数要分两层说（用户 2026-09-26 第 45 条的真机现场）：他跑 38 个包，头部却写"任务数：76" ——
         * 因为续解出来的内层包**也是任务**。不分开说，读的人会以为程序多跑了 38 个包。
         */
        int continuation = Tasks.Count(task => task.IsContinuationTask);

        lines.Add(
            $"任务数：{tally.Total}（{string.Join(" / ", tally.BuildParts())}）"
            + (continuation > 0 ? $"；其中续解出来的内层包 {continuation} 个" : string.Empty));

        /*
         * 跟班卷那一句与两个汇总行**共用同一句**（`DescribeFollowerNote`）：
         * 数字从"跳过"里剔出去之后，它必须在头部里出现一次，否则三个分项加起来又对不上任务数。
         */
        if (tally.FollowerSkipped > 0)
        {
            lines.Add(tally.DescribeFollowerNote());
        }

        if (tally.ContentGroupProcessSkipped > 0)
        {
            lines.Add(tally.DescribeContentGroupProcessNote());
        }

        string engine = DescribeEngineIdentity();

        if (!string.IsNullOrWhiteSpace(engine))
        {
            lines.Add("引擎：" + engine);
        }

        /*
         * ⛔ 读的是①页那一格的**同一份判据**（`ResolveOutputLocationText`），
         * 不是 `SelectedOutputDirectory` 这个字段本身：字段会被"记住的目录"填上，
         * 而这一批的落点可能压根不用它 —— 老写法因此在真机日志里写出上一批的
         * <c>…\测试\BBB</c>，与正文每一行"本次实际输出目录 …\222\…"互相打脸。
         */
        string outputRoot = _outputLocationForLog();

        if (!string.IsNullOrWhiteSpace(outputRoot))
        {
            lines.Add(outputRoot);
        }

        lines.Add(
            "细节在哪看：先看下面的「失败 / 取消 / 部分完成」「分卷组」「源包处理」三个专区；"
            + "完整的逐层过程在日志正文里；末尾只保留本次操作的关键诊断事件（不再倒整份 trace）。"
            + "如果专区里仍判不出，再让用户补 data\\logs 的详细文件。"
            + "⑧设置的「详细日志（排查用）」仍控制屏幕与普通/详细导出。");

        /*
         * 导出文件必须和①页看到的一致：至少把每一单的名字/状态/机器终态列出来。
         * 具体过程（逐层、密码候选、引擎原话）仍看正文与 trace，不在这里复述。
         */
        lines.Add("任务明细（与①页一致）：");

        foreach (ArchiveTask task in Tasks)
        {
            string error = (task.ErrorMessage ?? string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();

            lines.Add(
                $"{task.LogName} ｜ {task.Status} ｜ {task.Outcome}"
                + (error.Length > 0 ? $" ｜ {CompactReportLine(error, 500)}" : string.Empty));
        }

        return lines;
    }

    /// <summary>
    /// 单文件报告的**问题专区**（用户 2026-10-26：只给一个 txt 也要能定位）。
    /// 顺序固定：失败/取消/部分完成 → 分卷组 → 源包处理。
    /// </summary>
    internal IReadOnlyList<string> BuildSections()
    {
        var sections = new List<string>();

        sections.Add("================ 一、失败 / 取消 / 部分完成 ================");

        /*
         * 失败专区只列**真正的根任务问题**：跟班卷（同一分卷组后续卷）即使状态是"跳过"、
         * 甚至机器终态 Succeeded，也不是这一批的失败；把它们列进来会让用户误以为
         * B250135/151 这些完整组也失败了。
         */
        List<ArchiveTask> problemTasks = Tasks
            .Where(task => !task.IsVolumeGroupFollower
                && task.CountsTowardBatchOutcome
                && task.Outcome is TaskOutcome.Failed
                    or TaskOutcome.Cancelled
                    or TaskOutcome.PartiallyCompleted)
            .ToList();

        if (problemTasks.Count == 0)
        {
            sections.Add("本批没有失败 / 取消 / 部分完成的根任务。");
        }
        else
        {
            foreach (ArchiveTask task in problemTasks)
            {
                sections.Add(
                    $"[{task.Outcome}] {task.LogName} ｜ {task.Status}"
                    + $"｜格式={task.DetectedFormat}"
                    + (task.IsContinuationTask ? "｜续解内层包" : "｜根任务"));

                if (!string.IsNullOrWhiteSpace(task.ParentTaskName))
                {
                    sections.Add($"  父包：{task.ParentTaskName}");
                }

                if (!string.IsNullOrWhiteSpace(task.OutputPath))
                {
                    sections.Add($"  输出：{task.OutputPath}");
                }

                if (!string.IsNullOrWhiteSpace(task.VolumeInfoText))
                {
                    sections.Add($"  分卷：{CompactReportLine(task.VolumeInfoText, 500)}");
                }

                if (!string.IsNullOrWhiteSpace(task.ErrorMessage))
                {
                    sections.Add($"  原因：{CompactReportLine(task.ErrorMessage, 900)}");
                }

                if (!string.IsNullOrWhiteSpace(task.EngineVerdict))
                {
                    sections.Add($"  引擎：{CompactReportLine(task.EngineVerdict, 500)}");
                }
            }
        }

        sections.Add(string.Empty);
        sections.Add("================ 二、分卷组 ================");

        /*
         * ⛔ 2026-11-01：优先读第二大步的**组单元**（VolumeGroupUnit）——
         * 旧写法按任务表拼，真机上 `以宇_2/yanje_2/183` 明明是 OwnerAssigned/可证完整，
         * 导出却写「已登记片=0｜完整=False｜状态=未登记」。任务表只是组的成员视图，不是结论。
         */
        IReadOnlyCollection<ArchiveFixer.Extraction.VolumeGroupUnit> volumeUnits =
            _extraction.VolumeGroupUnits;

        var volumeTasks = Tasks
            .Where(task => task.IsVolumeGroup
                || task.IsVolumeGroupFollower
                || task.VolumePaths.Count > 0)
            .ToList();

        if (volumeUnits.Count > 0)
        {
            foreach (ArchiveFixer.Extraction.VolumeGroupUnit unit in volumeUnits)
            {
                string entryName = string.IsNullOrWhiteSpace(unit.EntryPath)
                    ? "判不出"
                    : System.IO.Path.GetFileName(unit.EntryPath);
                string missing = unit.MissingNumbers.Count == 0
                    ? "无"
                    : string.Join(",", unit.MissingNumbers);

                sections.Add(
                    $"组「{unit.BaseName}」｜族={unit.Family?.ToString() ?? "判不出"}"
                    + $"｜状态={unit.State}｜完整={unit.State is ArchiveFixer.Extraction.VolumeStep.VolumeGroupState.Complete
                        or ArchiveFixer.Extraction.VolumeStep.VolumeGroupState.OwnerAssigned
                        or ArchiveFixer.Extraction.VolumeStep.VolumeGroupState.Linked
                        or ArchiveFixer.Extraction.VolumeStep.VolumeGroupState.Extracting
                        or ArchiveFixer.Extraction.VolumeStep.VolumeGroupState.Succeeded}"
                    + $"｜片={unit.Pieces.Count}｜入口={entryName}"
                    + $"｜owner={unit.OwnerTaskFileName ?? "未定"}"
                    + $"｜缺片={missing}"
                    + $"｜{CompactReportLine(unit.Verdict, 300)}");
            }
        }
        else if (volumeTasks.Count == 0)
        {
            sections.Add("本批没有识别到分卷组。");
        }
        else
        {
            foreach (IGrouping<string, ArchiveTask> group in volumeTasks.GroupBy(DescribeVolumeGroupKey))
            {
                List<ArchiveTask> members = group.ToList();
                string state = members
                    .Select(task => task.VolumeInfoText)
                    .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? "未登记";

                int declared = members
                    .Select(task => task.VolumePaths.Count)
                    .Max();
                List<string> missing = members
                    .SelectMany(task => task.MissingVolumeNames)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .ToList();
                bool complete = members.Any(task => task.IsVolumeGroup
                    && task.IsVolumeComplete);

                sections.Add(
                    $"组「{group.Key}」｜成员={members.Count}｜已登记片={Math.Max(0, declared)}"
                    + $"｜完整={complete}"
                    + $"｜状态={CompactReportLine(state, 300)}"
                    + $"｜缺片={(missing.Count > 0 ? string.Join("、", missing) : "无")}");

                foreach (ArchiveTask task in members)
                {
                    sections.Add(
                        $"  - {task.LogName} ｜ {task.Status} ｜ {task.Outcome}"
                        + (task.VolumePaths.Count > 0 ? $"｜片={task.VolumePaths.Count}" : string.Empty)
                        + (task.MissingVolumeNames.Count > 0 ? $"｜缺={task.MissingVolumeNames.Count}" : string.Empty));
                }
            }
        }

        sections.Add(string.Empty);
        sections.Add("================ 三、源包处理 ================");

        var roots = Tasks.Where(task => !task.IsContinuationTask).ToList();

        if (roots.Count == 0)
        {
            sections.Add("本批没有根任务。");
        }
        else
        {
            foreach (ArchiveTask task in roots)
            {
                bool exists = false;

                try
                {
                    exists = !string.IsNullOrWhiteSpace(task.CurrentPath) && File.Exists(task.CurrentPath);
                }
                catch
                {
                    // 源包状态只是报告信息，不参与任何结论。
                }

                string handling = task.SourcePackageMove switch
                {
                    SourcePackageMoveState.DeferredToChainEnd => "延期到链尾",
                    SourcePackageMoveState.Done => "已处理",
                    _ => "未处理"
                };

                sections.Add(
                    $"{task.LogName} ｜ {task.Outcome} ｜ 源包={(exists ? "仍在原处" : "当前不存在")}"
                    + $"｜处理状态={handling}"
                    + (string.IsNullOrWhiteSpace(task.ErrorMessage) ? string.Empty : $"｜{CompactReportLine(task.ErrorMessage, 600)}"));

                if (task.SourcePackageMove == SourcePackageMoveState.DeferredToChainEnd)
                {
                    sections.Add(
                        "  源包保留原因：这一单所在的链/分卷组尚未证明完整，"
                        + "按「整条链全部成功才删原包」规则不动它");
                }
            }
        }

        return sections;
    }

    private static string DescribeVolumeGroupKey(ArchiveTask task)
    {
        string baseName = FileNameHelper.GetArchiveBaseName(task.CurrentPath);

        if (!string.IsNullOrWhiteSpace(baseName))
        {
            return baseName;
        }

        if (!string.IsNullOrWhiteSpace(task.LogName))
        {
            return task.LogName;
        }

        return "未命名组";
    }

    /// <summary>报告里的长行截断：完整事实仍留在 data/log，txt 只保留可读的一段。</summary>
    private static string CompactReportLine(string? text, int maxLength)
    {
        string value = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();

        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }

    /// <summary>
    /// 当前生效的引擎标识（名字 + 版本）—— 导出头部与任务详情共用同一份口径。
    ///
    /// ⛔ 问的是 <c>TaskSummaryService.EngineIdentity</c>（报告层那一份），
    /// 不自己 new 引擎、也不自己拼 7z 路径（AGENTS.md §3.1：引擎选择只允许注册表一个来源）；
    /// 版本取不到时它自己只说名字（见 <see cref="EngineIdentity.Describe"/>），这里不补编。
    /// </summary>
    private string DescribeEngineIdentity()
    {
        try
        {
            string engine = _summary.EngineIdentity.Describe();

            if (string.IsNullOrWhiteSpace(engine))
            {
                return string.Empty;
            }

            /*
             * ⚠ 优先级那一格是 List<string>：直接插进字符串会写成
             * `System.Collections.Generic.List`1[System.String]`（2026-09-26 真机导出的头部里就是这么写的）。
             * 这里必须自己用 `→` 连起来（与②页引擎那一栏同一口径）。
             */
            string priority = _settings.EnginePriority is { Count: > 0 }
                ? string.Join(" → ", _settings.EnginePriority.Where(value => !string.IsNullOrWhiteSpace(value)))
                : string.Empty;

            return string.IsNullOrWhiteSpace(priority)
                ? engine
                : $"{engine}（优先级：{priority}）";
        }
        catch
        {
            // 报告层不该因为"引擎信息取不到"整个失败：头部少一行，日志照旧导得出来。
            return string.Empty;
        }
    }
}

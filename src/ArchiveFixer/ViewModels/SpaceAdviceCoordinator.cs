using ArchiveFixer.BindingModels;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 并发建议/空间体检；属性留在 MainViewModel 便于 XAML 绑定。
/// </summary>
internal sealed class SpaceAdviceCoordinator
{
    private readonly MainViewModel _vm;

    public SpaceAdviceCoordinator(MainViewModel vm)
    {
        _vm = vm;
    }

    internal async Task RefreshParallelAdviceAsync()
    {
        List<ArchiveTask> selected = _vm.Tasks.Where(task => task.IsSelected).ToList();

        if (selected.Count == 0)
        {
            _vm.SetParallelAdviceText(_vm.Tasks.Count == 0
                ? "任务列表是空的：先添加文件，再算并行建议"
                : "一个任务都没勾：先勾选要处理的任务，再算并行建议");

            _vm.AppendLog("WARN", _vm.ParallelAdviceText);
            return;
        }

        try
        {
            ExtractionSchedulePlan plan = await Task.Run(
                () => _vm.ExtractionCoordinator.BuildSpaceAdvice(selected));

            /*
             * 建议只显示设备侧结论，不自动应用：
             * 源/落点是什么盘、建议几个（HDD/未知/移动盘 1，SSD 2）。
             * 空间侧仍算一遍并写日志，但不再混进这句建议，避免“建议 5”和用户上限 2 打架。
             */
            ConcurrencySuggestion suggestion = ConcurrencyAdvisor.Suggest(
                selected,
                _vm.Settings?.CustomOutputDirectory,
                _vm.Settings?.MaxParallelExtractCount ?? 2);

            _vm.SetParallelAdviceText(suggestion.Text);

            _vm.AppendLog("INFO", $"并发建议：{suggestion.Text}");
            _vm.AppendLog("INFO", "空间依据：" + plan.Basis);

            foreach (ScheduledExtractionItem blocked in plan.BlockedAtPlanTime)
            {
                _vm.AppendLog(
                    "WARN",
                    $"按当前可用空间，这个任务排不上：{blocked.Estimate.DisplayName} —— "
                    + $"需要 {TaskSpaceEstimate.FormatSize(blocked.RequiredBytes)}，"
                    + $"差 {TaskSpaceEstimate.FormatSize(blocked.ShortfallBytes)}");
            }
        }
        catch (Exception ex)
        {
            _vm.SetParallelAdviceText("算并行建议失败：" + ex.Message);
            _vm.AppendLog("ERROR", "算并行建议失败：" + ex.Message);
        }
    }

    internal async Task CheckSpaceForTasksAsync(IReadOnlyList<ArchiveTask>? tasks, string trigger)
    {
        if (tasks == null || tasks.Count == 0)
        {
            return;
        }

        try
        {
            ExtractionSchedulePlan plan = await Task.Run(() => _vm.ExtractionCoordinator.BuildSpaceAdvice(tasks))
                .ConfigureAwait(true);

            ReportSpaceCheck(plan, tasks.Count, trigger);
        }
        catch (Exception ex)
        {
            // 体检失败不影响任何事（它只是提醒）：写一行日志就够了。
            _vm.AppendLog("WARN", $"空间体检没能完成（不影响导入与解压）：{ex.Message}");
        }
    }

    private void ReportSpaceCheck(ExtractionSchedulePlan plan, int taskCount, string trigger)
    {
        long sourceTotal = 0;

        foreach (ScheduledExtractionItem item in plan.Ordered)
        {
            sourceTotal = TaskSpaceEstimate.SaturatingSum(sourceTotal, item.Estimate.SourceBytes);
        }

        if (plan.AvailableBytes < 0)
        {
            // 取不到就**说取不到**：不谎报足够，也不谎报不足。
            _vm.AppendLog(
                "WARN",
                $"空间体检（{trigger}）：没能取到目标盘的可用空间，无法预判这 {taskCount} 个包放不放得下。"
                + "开跑时每个任务启动前仍会再判一次空间。");

            return;
        }

        string summary =
            $"空间体检（{trigger}）：{taskCount} 个源包共 {TaskSpaceEstimate.FormatSize(sourceTotal)}，"
            + $"目标盘可用 {TaskSpaceEstimate.FormatSize(plan.AvailableBytes)}，"
            + $"其中要保留 {TaskSpaceEstimate.FormatSize(plan.ReserveBytes)} 余量。";

        if (plan.BlockedAtPlanTime.Count == 0)
        {
            /*
             * 够的时候也要留一句 INFO（用户要的是"能回答当时的判断"，不是只有坏消息才说话）——
             * 但**不弹窗**：弹一个"没问题"的框只是训练他闭眼点确定。
             */
            _vm.AppendLog("INFO", summary + "按当前可用空间，这一批没有整盘都放不下的包。");

            return;
        }

        var names = new List<string>();
        var detail = new StringBuilder();

        detail.AppendLine(summary);
        detail.AppendLine();
        detail.AppendLine("按当前可用空间，下面这些包整盘都放不下（连单独跑都不够）：");

        foreach (ScheduledExtractionItem blocked in plan.BlockedAtPlanTime)
        {
            /*
             * "需要"说的是**这次要从可用空间里新写多少**（内容物 + 过程物），不是源包占多少 ——
             * 源包已经在盘上、不在可用空间里（2026-09-29 真机就是因为这里口径错而被误拦）。
             * 括号里把那句话说清，用户才能自己复核这个数字。
             */
            string line =
                $"{blocked.Estimate.DisplayName} —— 需要 {TaskSpaceEstimate.FormatSize(blocked.RequiredBytes)}"
                + $"（内容物 + 过程物，源包已经在盘上不算在内），"
                + $"差 {TaskSpaceEstimate.FormatSize(blocked.ShortfallBytes)}";

            names.Add(blocked.Estimate.DisplayName);
            detail.AppendLine("  " + line);
        }

        const int MaxNamedInPopup = 10;

        string named = string.Join("、", names.Take(MaxNamedInPopup));

        if (names.Count > MaxNamedInPopup)
        {
            named += $"等 {names.Count} 个";
        }

        /*
         * 黄色 WARN + 红色 ERROR 两条都写（用户 2026-09-27 明确要求"日志里两个都要"）：
         * WARN 是"这件事要注意"，ERROR 是"这一批一定会跳过它"—— 级别不同，事后翻日志的用途也不同。
         */
        _vm.AppendLog("WARN", summary + $"其中 {plan.BlockedAtPlanTime.Count} 个包整盘都放不下：{named}。");
        _vm.AppendLog(
            "ERROR",
            $"空间不足，这一批会跳过 {plan.BlockedAtPlanTime.Count} 个包（一个字节都不会动它们）：{named}。"
            + "处理办法：清理「其余物」腾空间 / 换一个更大的输出盘 / 开①页的「空间不足」模式"
            + "（每个包校验通过后立刻删源包，边解边回收；⚠ 会永久删除源包）。");

        /*
         * 那个弹窗（用户原话："还可以弄一个弹窗，因为这个比较危险"）。
         * 无界面宿主（单元测试 / 控制台宿主）由 DialogService 自己降级成一条记录，不弹、不死等。
         */
        _vm.DialogService.ShowSpaceShortageWarning(
            $"有 {plan.BlockedAtPlanTime.Count} 个包放不下，处理时会跳过它们",
            detail.ToString().TrimEnd());
    }
}





using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.Views;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ArchiveFixer.ViewModels;

public partial class MainViewModel
{
    private bool CanRunNormalCommand()
    {
        return !IsBusy;
    }

    private bool CanStartExtract()
    {
        return !IsBusy && Tasks.Any(x => x.IsSelected);
    }

    private bool CanRunCurrentTaskManual()
    {
        return !IsBusy && SelectedTask != null && Tasks.Contains(SelectedTask);
    }

    /// <summary>
    /// 手动解压永远从入口包启动：选中第 2 卷时，找列表里对应的入口包；
    /// 找不到就停下来提示，不拿后续卷去启动一条注定失败的解压。
    /// </summary>
    private ArchiveTask? ResolveManualExtractionTarget()
    {
        ArchiveTask? selected = SelectedTask;

        if (selected == null)
        {
            return null;
        }

        if (selected.VolumePaths.Count == 0)
        {
            return selected;
        }

        string entryPath = selected.VolumePaths[0];

        if (string.Equals(selected.CurrentPath, entryPath, StringComparison.OrdinalIgnoreCase))
        {
            return selected;
        }

        ArchiveTask? entryTask = Tasks.FirstOrDefault(task =>
            string.Equals(task.CurrentPath, entryPath, StringComparison.OrdinalIgnoreCase)
            || task.VolumePaths.Any(path =>
                string.Equals(path, entryPath, StringComparison.OrdinalIgnoreCase)));

        if (entryTask == null)
        {
            _dialogService.ShowInfo(
                "当前选中的是分卷组的后续卷，但任务列表里没有入口包。"
                + Environment.NewLine
                + "请先把入口包加入列表，再手动解压；程序不会拿后续卷直接启动。");
        }

        return entryTask;
    }

    /// <summary>三个批量勾选命令的可用性：闲着 + 列表里确实有东西可勾。</summary>
    private bool CanChangeTaskSelection()
    {
        return !IsBusy && Tasks.Count > 0;
    }

    /// <summary>
    /// 「移除勾选的」（①页那颗按钮）与「移除勾选中的任务」（列表右键菜单）**共用**的可用性：
    /// **列表里真有一条勾着的**（空列表 / 一个都没勾 → 灰）。
    ///
    /// <para>这个判据是用户**两轮反馈各管一半**合成的，两半都留在这里，⛔ 只照一半改就是错的：</para>
    ///
    /// <list type="number">
    /// <item><description><b>2026-09-27 否掉的那一半</b>：这里以前绑着 <c>!IsBusy</c>，跑批中途按钮是灰的。
    /// 用户原话：<i>"我就是修改列表删除东西，和正在处理有什么关系"</i>（他当天正是在跑批中间点的它）。
    /// 这话是对的：它是**纯列表操作**（只动内存里那几行，磁盘一个字节都不碰），
    /// **与跑批没有关系** —— 所以跑批中途必须能点。</description></item>
    /// <item><description><b>2026-09-29 补上的那一半</b>：去掉 <c>!IsBusy</c> 之后它变成"永远可点"，
    /// 于是空列表 / 一个都没勾时也亮着、点下去什么都不发生。用户原话：
    /// <i>"这个勾选行里面，移除勾选的按钮一直亮着没用啊，我都没有导入文件你亮着干什么"</i>
    /// —— 可作用的对象就是"勾选"，一个都没有就不该亮（与「只解压」等命令同一条精神）。</description></item>
    /// </list>
    ///
    /// <para>合成口径 = **只问"有没有勾选的"，忙闲根本不进判据**。
    /// ⛔ **不许再加 <c>!IsBusy</c>**：那会推翻用户 2026-09-27 的明确原话，等于把两轮反馈又砍回一半。</para>
    ///
    /// <para>⛔ 两条命令共用这一个判据（2026-09-29）：同一件事只允许有一个出口，
    /// 各写一套必然漂移成"按钮是灰的、右键菜单却能点"。</para>
    /// </summary>
    private bool CanRemoveSelectedTasks()
    {
        return Tasks.Any(task => task?.IsSelected == true);
    }

    /*
     * ============================ 勾选跟随汇总（缺陷修正） ============================
     *
     * 以前这里**没有任何人**监听 ArchiveTask.IsSelected：
     * 用户点一下勾选框，勾选确实变了、命令执行时也确实按新的勾选走，
     * 但汇总区那个「选中：N」（Summary.SelectedCount）以及依赖勾选的命令可用性
     * （例如「只解压」要求"至少勾了一个"）**一直停在旧值上** —— 界面在撒谎：
     * 屏幕上写着"选中：3"，实际勾了 5 个；或者一个都没勾时「只解压」还是亮着的。
     *
     * 修法是"只在勾选变化时重算"，**不监听全部属性**：Status / ProgressText / ElapsedText
     * 在一次解压里会被刷新成百上千次，每次都重算汇总（还要遍历整张任务表）纯属自伤。
     */

    /// <summary>批量改勾选时挂起逐条刷新（50–200 个任务的"全选"不该触发 200 次汇总）。</summary>
    private bool _bulkSelectionUpdate;

    private void HookTaskSelectionTracking()
    {
        Tasks.CollectionChanged += Tasks_CollectionChanged;

        foreach (ArchiveTask task in Tasks)
        {
            task.PropertyChanged -= Task_SelectionPropertyChanged;
            task.PropertyChanged += Task_SelectionPropertyChanged;
        }
    }

    /// <summary>
    /// 集合变化只负责**换挂**逐条的监听，不在这里刷汇总：
    /// 批量导入会逐个 <c>Add</c>（用户场景是 50–200+ 个包），逐个刷就是 O(n²)；
    /// 而所有 Add / Remove / Clear 的调用点本来就会在收尾时各刷一次（见各自的 UpdateSummary）。
    /// </summary>
    private void Tasks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        /*
         * Reset（批量替换 / 清空）**不带** OldItems / NewItems —— 逐项挂监听的那两条路都拿不到东西。
         * 批量导入走的正是 Reset（见 RangeObservableCollection），所以必须在这里按当前列表整批重挂，
         * 否则导入进来的任务勾选一下，汇总里的「选中：N」和「只解压」的可用性就不会跟着变
         *（那正是 2026-09-22 修过的那个"界面在撒谎"的缺陷，换个入口复活）。
         */
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (ArchiveTask task in Tasks)
            {
                task.PropertyChanged -= Task_SelectionPropertyChanged;
                task.PropertyChanged += Task_SelectionPropertyChanged;
            }

            return;
        }

        if (e.OldItems != null)
        {
            foreach (ArchiveTask task in e.OldItems.OfType<ArchiveTask>())
            {
                task.PropertyChanged -= Task_SelectionPropertyChanged;
            }
        }

        if (e.NewItems != null)
        {
            foreach (ArchiveTask task in e.NewItems.OfType<ArchiveTask>())
            {
                task.PropertyChanged -= Task_SelectionPropertyChanged;
                task.PropertyChanged += Task_SelectionPropertyChanged;
            }
        }
    }

    private void Task_SelectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_bulkSelectionUpdate || e.PropertyName != nameof(ArchiveTask.IsSelected))
        {
            return;
        }

        RefreshSummaryAfterSelectionChange();
    }

    /// <summary>
    /// 勾选变化之后重算汇总（汇总里带着「选中：N」，同时负责刷新命令可用性）。
    ///
    /// <para>
    /// 为什么要挑线程：勾选通常发生在界面线程（用户点的），但**续解流程也会改 IsSelected**
    /// （<c>OneClickCoordinator</c> 把新加进来的内层包显式勾上，那段代码可能跑在后台线程上）。
    /// 后台线程直接碰绑定会有跨线程风险，所以有 UI 宿主时统一回界面线程刷。
    /// </para>
    /// </summary>
    public void RefreshSummaryAfterSelectionChange()
    {
        if (UiThread.IsAvailable?.Invoke() == true && UiThread.CheckAccess?.Invoke() == false)
        {
            UiThread.BeginInvoke?.Invoke(UpdateSummary);
            return;
        }

        UpdateSummary();
    }

    /// <summary>
    /// 配合 <see cref="_bulkSelectionUpdate"/>：批量动作结束后只刷一次汇总。
    ///
    /// <para><b>凡是"循环改一批任务的勾选"的代码都必须走它</b>（用户 2026-09-24 第 12 条"卡死"
    /// 的修法之一）：逐项改勾选会逐项触发全表汇总重算 + 38 条命令可用性重查 ——
    /// 几百项的任务列表就是几百次 O(N) 全表扫描（O(N²)）。跨类调用的例子见
    /// <c>OneClickCoordinator</c>（续解把已处理的任务取消勾选、把新内层包勾上）
    /// 与 <c>MainWindow.SmartRenameCurrentTaskMenuItem_Click</c>（只留当前这一个）。</para>
    /// </summary>
    internal void RunBulkSelectionUpdate(Action changeSelection)
    {
        _bulkSelectionUpdate = true;

        try
        {
            changeSelection();
        }
        finally
        {
            _bulkSelectionUpdate = false;
        }

        RefreshSummaryAfterSelectionChange();
    }

    /// <summary>全选：整个列表都参与处理。</summary>
    public void SelectAllTasks()
    {
        if (Tasks.Count == 0)
        {
            AppendLog("INFO", "「全选」：任务列表是空的，没有可勾选的任务。");
            return;
        }

        RunBulkSelectionUpdate(() =>
        {
            foreach (ArchiveTask task in Tasks)
            {
                task.IsSelected = true;
            }
        });

        AppendLog("INFO", $"已全选：{Tasks.Count} 个任务都会参与处理（「只解压 / 一键处理」按勾选执行）。");
    }

    /// <summary>全不选：一个都不勾，方便"只处理其中某一个"。</summary>
    public void SelectNoneTasks()
    {
        if (Tasks.Count == 0)
        {
            AppendLog("INFO", "「全不选」：任务列表是空的。");
            return;
        }

        int before = Tasks.Count(x => x.IsSelected);

        RunBulkSelectionUpdate(() =>
        {
            foreach (ArchiveTask task in Tasks)
            {
                task.IsSelected = false;
            }
        });

        /*
         * 行高亮这里**刻意保留**：右键菜单、复制路径这类"这一行"的操作仍然用它。
         * 但命令型入口（一键处理 / 只解压 / 清理类）**一律只认勾选** ——
         * 一个都没勾时它们只提示、什么都不做（用户 2026-09-24 第 12 条），
         * 所以"清掉行高亮会拆掉兜底"这个老顾虑已经不存在了。
         */
        AppendLog(
            "INFO",
            $"已取消全部勾选（原有 {before} 个勾选被清掉）：现在一个都没勾。" +
            "接着点某一行左边的勾选框，或者用右键「只勾选这一个」。");
    }

    /// <summary>反选：勾上的取消、没勾的勾上。</summary>
    public void InvertTaskSelection()
    {
        if (Tasks.Count == 0)
        {
            AppendLog("INFO", "「反选」：任务列表是空的。");
            return;
        }

        RunBulkSelectionUpdate(() =>
        {
            foreach (ArchiveTask task in Tasks)
            {
                task.IsSelected = !task.IsSelected;
            }
        });

        AppendLog("INFO", $"已反选：现在有 {Tasks.Count(x => x.IsSelected)} 个任务被勾选（共 {Tasks.Count} 个）。");
    }

    /// <summary>
    /// 只勾选指定的那一个，其余全部取消勾选。
    ///
    /// 这是"识别完一个文件夹、几十上百个包全被勾上，但这次只想解其中一个"最短的一条路：
    /// 右键那一行 → 点一下，不必先"全不选"再去找那一个。
    /// </summary>
    private void SelectSoleTask(object? parameter)
    {
        if (parameter is not ArchiveTask target || !Tasks.Contains(target))
        {
            // ⛔ 不许静默（用户 2026-10-10：「什么东西都是没有用的」）：命令那层已有 IsRowInList 判据，这里补一句。
            ExplainNoRow();
            return;
        }

        RunBulkSelectionUpdate(() =>
        {
            foreach (ArchiveTask task in Tasks)
            {
                task.IsSelected = ReferenceEquals(task, target);
            }
        });

        AppendLog(
            "INFO",
            $"只勾选了「{target.FileName}」：其余 {Math.Max(0, Tasks.Count - 1)} 个任务的勾选已取消。");
    }

    /// <summary>
    /// 「清空列表」：**整表操作**，与勾选无关（名字本身就是这个意思）。
    ///
    /// <para>
    /// 缺陷 1 顺手核对到这里时，查出来的不是"代码看错了选中含义"，而是**界面在自相矛盾**：
    /// 列表上方那条蓝字把它和「移除选中」列在一起，说"都只作用于已勾选的任务"，
    /// 而它从第一版起就是清整张表。
    /// </para>
    /// <para>
    /// 处置：**没有**把行为改成"只清勾选的" —— 那样"清空列表"这个名字就失去了唯一的出口
    /// （勾了 3 个时点它，清 3 个还是清全部？），而是把话说明白：
    /// 蓝字里单独写明它是整表操作，确认框里点名总数、勾选数、"只移除勾选的请用「移除选中」"。
    /// 于是"界面说的"和"代码做的"重新对齐，用户点之前就知道会发生什么。
    /// </para>
    /// </summary>
    public void ClearTasks()
    {
        if (Tasks.Count == 0)
        {
            return;
        }

        int totalCount = Tasks.Count;
        int checkedCount = Tasks.Count(x => x.IsSelected);

        string confirmText =
            $"确定要清空整个任务列表吗？{Environment.NewLine}{Environment.NewLine}"
            + $"列表里的 {totalCount} 个任务会全部移除 —— 这是整表操作，与勾选无关"
            + (checkedCount > 0
                ? $"（当前勾选的 {checkedCount} 个也会一起移除）。"
                : "。")
            + $"{Environment.NewLine}{Environment.NewLine}"
            + $"只移除勾选的任务请用「移除选中」；这里只清空列表，磁盘上的文件一个都不会动。";

        bool confirm = _dialogService.ShowConfirm(confirmText);

        if (!confirm)
        {
            AppendLog("INFO", "用户取消了「清空列表」，任务列表没有变化。");
            return;
        }

        Tasks.Clear();
        LogLeftoverWorkspaces();
        AutoLoadPasswordBook();

        UpdateSummary();
        AppendLog(
            "INFO",
            $"已清空任务列表（整表操作，与勾选无关）：共移除 {totalCount} 个任务（其中勾选 {checkedCount} 个）；磁盘上的文件没有动。");
    }

    /// <summary>
    /// 「移除选中」：与清理类命令**同一套选中口径**（勾选为准 → 退化为当前行 → 都没有才提示）。
    ///
    /// <para>
    /// 以前一个都没勾时它静默什么也不做（只在日志里留一句"已移除选中任务 0 个"），
    /// 用户点完只会以为程序坏了 —— 那正是缺陷 1 的同一类问题：**界面说作用于勾选，
    /// 实际却要求另一个东西**。现在两者都被 <see cref="ResolveCleanupTargets"/> 管着。
    /// </para>
    /// </summary>
    public void RemoveSelectedTasks()
    {
        const string title = "移除选中";

        CleanupTargetSelection selection = ResolveCleanupTargets(Tasks);

        AppendLog(selection.HasTarget ? "INFO" : "WARN", DescribeCleanupTargets(title, selection));

        if (!selection.HasTarget)
        {
            // 只提示、什么都不做（用户 2026-09-24 第 12 条：一律只认勾选）。
            _dialogService.ShowWarning(PromptNoCheckedTask(title));
            return;
        }

        List<ArchiveTask> targets = selection.Tasks.ToList();

        foreach (ArchiveTask task in targets)
        {
            Tasks.Remove(task);
        }

        RebuildTaskIndex();
        LogLeftoverWorkspaces();
        AutoLoadPasswordBook();

        UpdateSummary();
        AppendLog("INFO", $"{title}：以勾选为准，已移除 {targets.Count} 个任务。");
    }

    /// <summary>
    /// "一个都没勾"时的**唯一**提示句（用户 2026-09-24 第 12 条定的口径：只提示、不动作）。
    ///
    /// <para>措辞来自 <see cref="StatusText.NoCheckedTaskPromptFormat"/> —— 界面蓝字、命令提示、
    /// 日志三处共用同一句话，谁也不能再各写一套（§7）。</para>
    /// </summary>
    private string PromptNoCheckedTask(string title)
    {
        return string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            StatusText.NoCheckedTaskPromptFormat,
            title,
            Tasks.Count);
    }

}

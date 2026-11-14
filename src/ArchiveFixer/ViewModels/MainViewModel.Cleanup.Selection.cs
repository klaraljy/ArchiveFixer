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
{        /// <summary>一次清理/列表命令"作用于谁"的来源。</summary>
    internal enum CleanupTargetSource
    {
        /// <summary>一个都没勾 —— 什么都不做（提示用户先勾选）。</summary>
        None = 0,

        /// <summary>以**勾选**为准（批量清理的入口）。</summary>
        Checked = 1,

        /// <summary>只作用于当前选中的一行（右侧任务检查器）。</summary>
        Selected = 2
    }

    /// <summary>
    /// 一次清理命令的目标解析结果。
    ///
    /// <para>
    /// 抽成对象 + <see cref="ResolveCleanupTargets"/> 这一个静态判定处，是为了让
    /// "这次用的是哪一种选中含义"能被**直接断言**（测试不必去猜弹了什么框、写了哪行日志）。
    /// </para>
    /// </summary>
    internal sealed class CleanupTargetSelection
    {
        /// <summary>本次要处理的任务（按列表顺序；只看勾选）。</summary>
        public IReadOnlyList<ArchiveTask> Tasks { get; init; } = Array.Empty<ArchiveTask>();

        /// <summary>作用来源：勾选 / 一个都没勾。</summary>
        public CleanupTargetSource Source { get; init; }

        /// <summary>有没有可处理的目标。</summary>
        public bool HasTarget => Tasks.Count > 0;
    }

    /// <summary>
    /// 解析一次命令作用于谁（**唯一判定处**）。
    ///
    /// <para><b>一律只认勾选</b>（用户 2026-09-24 第 12 条亲自拍板，原话：
    /// "你只需要操作我选中的文件，其他的不用管"）：</para>
    /// <list type="number">
    /// <item><description>有勾选任务 → 就作用于勾选的那些；</description></item>
    /// <item><description>一个都没勾 → 返回空，调用方按
    /// <see cref="StatusText.NoCheckedTaskPromptFormat"/> **只提示、什么都不做**。</description></item>
    /// </list>
    ///
    /// <para>⛔ <b>这里曾经有一条"一个都没勾时退回当前点中的那一行"的兜底 —— 已经删掉，
    /// 不要再加回来</b>。用户原话："我即使没有特地的没有去选中，你为什么还要去操作，
    /// 这个操作导致我彻底的卡死了"。两套"选中"（行高亮 / 勾选框）并存时，兜底等于
    /// **替他决定要动哪些文件**：他以为"我没勾就是不动"，程序却拿高亮那一行开了工。
    /// 代价（用户点一行但没勾 → 收到提示）远小于代价的另一面（动了没打算动的文件）。</para>
    /// </summary>
    internal static CleanupTargetSelection ResolveCleanupTargets(
        IEnumerable<ArchiveTask>? tasks,
        IReadOnlyList<ArchiveTask>? selectedTargets = null)
    {
        if (selectedTargets != null)
        {
            List<ArchiveTask> selected = selectedTargets
                .Where(task => task != null && (tasks == null || tasks.Contains(task)))
                .Distinct()
                .ToList();

            return selected.Count > 0
                ? new CleanupTargetSelection
                {
                    Tasks = selected,
                    Source = CleanupTargetSource.Selected
                }
                : new CleanupTargetSelection { Source = CleanupTargetSource.None };
        }

        List<ArchiveTask> checkedTasks = tasks?
            .Where(task => task != null && task.IsSelected)
            .ToList()
            ?? new List<ArchiveTask>();

        if (checkedTasks.Count > 0)
        {
            return new CleanupTargetSelection
            {
                Tasks = checkedTasks,
                Source = CleanupTargetSource.Checked
            };
        }

        return new CleanupTargetSelection { Source = CleanupTargetSource.None };
    }

    /// <summary>
    /// 把"这次用的是哪一种选中含义"写进日志（**用了哪一种必须留痕**）。
    ///
    /// 用户事后要能回答"刚才为什么只动了这一个 / 为什么动了这 7 个" —— 只写"已取消"
    /// 或什么都不写，排查就只能靠猜（那次真机验收就是这么被误导的）。
    /// </summary>
    internal static string DescribeCleanupTargets(string title, CleanupTargetSelection selection)
    {
        if (selection.Source == CleanupTargetSource.Selected)
        {
            return selection.Tasks.Count == 1
                ? $"{title}：只作用于当前任务「{selection.Tasks[0].FileName}」。"
                : $"{title}：只作用于当前选中的 {selection.Tasks.Count} 个任务。";
        }

        if (selection.Source == CleanupTargetSource.Checked)
        {
            return selection.Tasks.Count == 1
                ? $"{title}：以勾选为准，本次作用于勾选的 1 个任务「{selection.Tasks[0].FileName}」。"
                : $"{title}：以勾选为准，本次作用于勾选的 {selection.Tasks.Count} 个任务"
                  + "（逐个处理，每个任务都会先给你看预览与确认框）。";
        }

        return $"{title}：没有勾选任何任务，已取消（一个文件、一个字节都没动）。";
    }

    /// <summary>
    /// 把解析出来的目标展开成"真正要跑几次"。
    ///
    /// <para>
    /// 「删除其余物」「清理空文件夹」是**每个任务各删自己那一份**，所以逐个任务各跑一次；
    /// 「删除本目录全部其余物」的作用域本来就是**整个输出目录**，
    /// 于是同一个输出目录只算一次 —— 勾了同一个源目录下的 5 个包时，
    /// 不该把"整个目录都删掉"这件事问 5 遍（那 5 遍的答案还必然是一样的）。
    /// </para>
    /// </summary>
    internal static List<ArchiveTask> ExpandCleanupTargets(
        CleanupTargetSelection selection,
        bool everythingInDirectory)
    {
        if (!everythingInDirectory)
        {
            return selection.Tasks.ToList();
        }

        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expanded = new List<ArchiveTask>();

        foreach (ArchiveTask task in selection.Tasks)
        {
            string directory = string.IsNullOrWhiteSpace(task.OutputPath)
                ? string.Empty
                : SafePathHelper.GetFullPathSafe(task.OutputPath);

            // 路径认不出来时也要留一条：下面会明确报"这个任务还没有输出目录"，比静默跳过好。
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = task.OutputPath ?? string.Empty;
            }

            if (seenDirectories.Add(directory))
            {
                expanded.Add(task);
            }
        }

        return expanded;
    }
}

using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
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
{        // ================================================================ 空间规划 + 其余物处理

    /// <summary>
    /// "按当前可用空间，最多能并行几个"那句话（用户 2026-09-22 需求第 2 条：界面上算出并显示）。
    ///
    /// <para>数字来自 <see cref="ExtractionScheduleHandler.BuildSpaceAdvice"/> —— 与真正开跑时的调度
    /// 同一段实现，所以界面上显示的建议与日志里那一刻的实际口径永远不会分叉。</para>
    /// </summary>
    public string ParallelAdviceText
    {
        get => _parallelAdviceText;
        private set => SetProperty(ref _parallelAdviceText, value ?? string.Empty);
    }

    /// <summary>并发档位的候选（界面的下拉框直接绑它；1 / 2 / 3 / 4）。</summary>
    public IReadOnlyList<int> MaxParallelChoices => ExtractionScheduler.AllowedParallelCounts;

    /// <summary>当前档位的一句话说明（源包怎么处理 + 其余物怎么处理），显示在并发那一组旁边。</summary>
    public string SpaceModeText
    {
        get => _spaceModeText;
        private set => SetProperty(ref _spaceModeText, value ?? string.Empty);
    }

    /// <summary>
    /// 算一次并行建议（后台 stat 每个源包，界面不卡）。
    /// 没勾任务时给出提示而不是算一个空结论。
    /// </summary>

    /// <summary>把"当前档位"那句话刷新一遍（界面绑定它）。</summary>

    /// <summary>
    /// ②③页那条**本次被「空间不足」模式覆盖**的提示（模式关着时为空 = 整行收起）。
    ///
    /// <para>为什么这两页也要显示：②页是"源包 / 其余物怎么处理、并发几档"的那一页，
    /// ③页是"清理与删除"的那一页 —— 正是用户来核对这件事的两个地方。
    /// 只在①页说一句，他翻到②③页看到的还是设置里的档位，就会读成"程序没按我看到的跑"
    /// （2026-09-27 真机上他就是这么对照日志的）。</para>
    /// </summary>
    public string SpaceTightOverrideText
    {
        get => _spaceTightOverrideText;
        private set => SetProperty(ref _spaceTightOverrideText, value ?? string.Empty);
    }

    private string _spaceTightOverrideText = string.Empty;

    /// <summary>那条覆盖提示要不要显示（界面绑定它；与文本**同时**通知，见 §9.5 的值 + 通知）。</summary>
    public bool HasSpaceTightOverride => !string.IsNullOrWhiteSpace(SpaceTightOverrideText);

    /// <summary>把那条覆盖提示按当前开关刷一遍（开关一动、进页面时都要刷）。</summary>

    /// <summary>
    /// ①页那个**黄色「空间不足」开关**（用户 2026-09-27 拍板）。
    ///
    /// <para><b>它刻意不是设置项</b>：⛔ 不写 <c>appsettings.json</c>、不记忆、重启后是关着的
    /// （用户原话："只有当出现空间不足的情况才需要"）—— 所以这里是一个纯运行期字段，
    /// 与 <see cref="RunAtFullSpeed"/>（那个是设置项、用户明确要求要记住）刚好相反。
    /// 一旦开跑，它由 <c>ExtractionCoordinator</c> 在**批首钉死**（同一批口径一致）。</para>
    ///
    /// <para>开关本身只改一件事：那两页的覆盖提示与这句话的显示。真正的行为在解压管线里
    /// （并发档 / 排序 / 源包删除 / 其余物删除四处，见 <c>ExtractionCoordinator._spaceTightThisBatch</c>）。</para>
    /// </summary>
    public bool SpaceTightMode
    {
        get => _spaceTightMode;
        set
        {
            if (!SetProperty(ref _spaceTightMode, value))
            {
                return;
            }

            RefreshSpaceTightOverrideText();

            AppendLog(
                "WARN",
                value
                    ? "已开「空间不足」模式（只对本次运行有效，不写设置）：下一批会按空间自己定并发，并且每个包定稿 + 校验通过后立刻永久删除它的源包。"
                    : "已关「空间不足」模式：恢复按设置里的源包 / 其余物 / 并发档执行。");
        }
    }

    private bool _spaceTightMode;

    /*
     * ===== 解压前的**空间体检**（用户 2026-09-27 第 2 条）=====
     *
     * 他原话的意思是："导入完就把源包大小跟目标盘可用空间对一遍，不够就说一声、
     * 还要弹个窗（因为这个比较危险），单个放不下的包要**点名**"。
     *
     * 三条纪律：
     * ① **自动**（默认就做，不需要他记得点哪个按钮）—— 触发点只有两个：
     *    导入结束（批量任务刚进列表）与**换输出位置**（换了盘就是换了另一块空间）；
     * ② 判据**一处都不新写**：直接调解压管线用的同一个 `BuildSpaceAdvice`
     *    （与开跑时的调度、界面上的「按空间算建议」是三处共用一段实现）；
     * ③ 取不到可用空间时**如实说取不到**，绝不猜"够"或"不够"。
     *
     * ⚠ 它只是**提醒**：不许顺手把任务取消勾选、不许改设置、不许拦着不让跑 ——
     * 真正拦人的仍然是每个任务启动前那道空间门（不变量：空间不足绝不开跑）。
     */

    /// <summary>
    /// 给一批任务做空间体检（导入完成 / 换了输出位置时调）。
    ///
    /// <para>它跑在**后台**（要 stat 每个源包），界面只收结论；任何异常都只写日志，
    /// 绝不让一次"提醒"把导入或选区变成失败。</para>
    /// </summary>
    /// <param name="tasks">要体检的任务（空 / null = 什么都不做）。</param>
    /// <param name="trigger">触发来源（进日志，用户事后要能回答"这句是什么时候说的"）。</param>

    public TaskSummary Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value);
    }

    public ArchiveTask? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (SetProperty(ref _selectedTask, value))
            {
                OnPropertyChanged(nameof(HasSelectedTask));
                OnPropertyChanged(nameof(CanOpenManualOperation));
                OnPropertyChanged(nameof(ManualGuideText));
                SubscribeManualDistributionTask(value);
                RefreshManualDistribution();
                RaiseAllCommandCanExecuteChanged();
            }
        }
    }
    internal void SetParallelAdviceText(string value) => ParallelAdviceText = value;

    internal Task RefreshParallelAdviceAsync() => Space.RefreshParallelAdviceAsync();

    internal Task CheckSpaceForTasksAsync(IReadOnlyList<ArchiveTask>? tasks, string trigger)
        => Space.CheckSpaceForTasksAsync(tasks, trigger);

    internal void SetSpaceModeText(string value) => SpaceModeText = value;

    internal void SetSpaceTightOverrideText(string value) => SpaceTightOverrideText = value;

    internal void NotifySpaceTightOverrideChanged() => OnPropertyChanged(nameof(HasSpaceTightOverride));

    internal void RefreshSpaceModeText() => SpaceText.RefreshSpaceModeText();

    internal void RefreshSpaceTightOverrideText() => SpaceText.RefreshSpaceTightOverrideText();
}


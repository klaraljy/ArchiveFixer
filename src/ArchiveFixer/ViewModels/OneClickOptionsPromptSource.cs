using ArchiveFixer.BindingModels;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 一键处理「本次选项」的**唯一询问入口**：决定这一次问不问、问的时候走真面板还是注入替身。
///
/// <para>从 <c>OneClickCoordinator</c> 按职责搬出来（选项 ≠ 确认）：本类只管"选项从哪来"，
/// 不判断"这次跑不跑"，也不写任何一条日志与设置 —— 取消 / 确认 / 无面板三支的收尾
/// 在 <see cref="OneClickConfirmFlow"/>。</para>
///
/// <para>⛔ 判据一个字不改：可注入的面板入口仍然是**整条一键处理流水线上唯一**的询问点
/// （一次一键处理只问一次，规格 §9.2 硬要求③）。</para>
/// </summary>
internal sealed class OneClickOptionsPromptSource
{
    private readonly MainViewModel _vm;
    private readonly ExtractionCoordinator _extractionCoordinator;

    /// <summary>
    /// 本次运行里用户勾过「以后不再询问」——之后的一键处理直接按设置走，不再弹确认框。
    ///
    /// <para>⚠ 2026-09-24 第 17 条之后它还有一个**落盘**的孪生兄弟
    /// （<see cref="AppSettings.SkipOneClickConfirm"/>，用户在框里勾了就由
    /// <c>MainViewModel.SaveSkipOneClickConfirm</c> 写进设置，跨重启有效）。
    /// 这个内存字段仍然要留着：本次运行内**立刻**生效，不用等下次读设置。</para>
    /// </summary>
    private bool _suppressOptionsPanel;

    internal OneClickOptionsPromptSource(
        MainViewModel vm,
        ExtractionCoordinator extractionCoordinator)
    {
        _vm = vm;
        _extractionCoordinator = extractionCoordinator;
    }

    private AppSettings Settings => _vm.Settings;

    /// <summary>
    /// 「本次选项 / 确认框」的询问入口（**可注入**；默认 = 真窗口 <c>Views/OneClickOptionsWindow</c>）。
    ///
    /// <para>
    /// 为什么必须留这个注入点：无 UI 宿主（单元测试 / 控制台宿主）下面板根本不弹，
    /// 于是"不勾「存为默认」时设置文件一个字节都不改"（规格 §9.2 硬要求②）
    /// 这条**永远走不到**，也就永远无法被证明。注入一个假面板之后，
    /// 两条分支（勾 / 不勾）都能在测试里跑到，并且能对着真实的设置文件断言。
    /// </para>
    /// </summary>
    internal Func<OneClickRunOptions, OneClickOptionsPrompt>? OptionsPromptOverride { get; set; }

    /// <summary>
    /// 真窗口入口（生产里**唯一**的确认框在 <c>Views/OneClickOptionsWindow</c>，由宿主注入）。
    ///
    /// <para>⛔ 本类**不许**直接引用 <c>ArchiveFixer.Views</c>：Views 侧要绑定本层类型
    /// （窗口的 <c>OneClickRunOptions</c> / <c>OneClickConfirmFacts</c>），那条边删不掉；
    /// 本层再反向 <c>using ArchiveFixer.Views</c>，两条边合起来就是目录级依赖环
    /// （<c>CodeStructureGuardTests.目录级依赖图不能有环</c>）。所以这里只留一个**端口**，
    /// 真实实现由 <c>App.OnStartup</c> 设成 <c>OneClickOptionsWindow.Show</c>
    /// （与 <c>DialogWindowFactories</c> / <c>RenamePreviewWindowFactory</c> 同一套做法）。</para>
    ///
    /// <para>没注入 = 无 UI 宿主（单元测试 / 控制台宿主）：直接
    /// <see cref="OneClickOptionsPrompt.NotShown"/>，与窗口在无宿主时自己返回的结论逐字相同
    /// （§9.2 硬要求④：不弹窗、不死等）。</para>
    /// </summary>
    internal static Func<
        OneClickRunOptions,
        AppSettings?,
        OneClickConfirmFacts?,
        Func<OneClickRunOptions, Task<string>>?,
        OneClickOptionsPrompt>? DefaultPromptFactory
    { get; set; }

    /// <summary>本次选项的种子（按设置 + 界面上选中的落点；改落点前的正文第一行就用它）。</summary>
    internal OneClickRunOptions BuildSeed() =>
        OneClickRunOptions.FromSettings(Settings, _vm.SelectedOutputDirectory);

    /// <summary>
    /// 这一次要不要问：可注入的替身永远问（测试要跑两条分支）；否则看内存标志与落盘设置
    /// （勾过「以后不再询问」= 一个框都不弹）。
    /// </summary>
    internal bool ShouldAsk => OptionsPromptOverride != null || (!_suppressOptionsPanel && !Settings.SkipOneClickConfirm);

    /// <summary>用户在框里勾了「以后不再询问」：本次运行立刻生效（落盘由确认流程负责）。</summary>
    internal void SuppressPanel() => _suppressOptionsPanel = true;

    /// <summary>
    /// 问一次本次选项（**一次一键处理只问一次**，规格 §9.2 硬要求③）。
    ///
    /// <para>
    /// 之所以能"只问一次"，是因为这个方法是**整条一键处理流水线上唯一**的询问点：
    /// 续解的第 2/3 轮复用同一个快照，不重新问（50–200 个包的场景下逐个问等于不可用，决策 D-4）。
    /// </para>
    /// </summary>
    /// <param name="facts">
    /// 正文那几行（内容物落点 / 其余物 / 提醒）；无 UI 宿主或注入替身时用不到它。
    /// </param>
    /// <param name="targets">这一批要处理的任务（用来在用户改了落点时重算正文第一行）。</param>
    internal OneClickOptionsPrompt AskOnce(
        OneClickRunOptions seed,
        OneClickConfirmFacts? facts = null,
        IReadOnlyList<ArchiveTask>? targets = null)
    {
        if (OptionsPromptOverride != null)
        {
            return OptionsPromptOverride(seed);
        }

        if (_suppressOptionsPanel)
        {
            return OneClickOptionsPrompt.NotShown();
        }

        Func<
            OneClickRunOptions,
            AppSettings?,
            OneClickConfirmFacts?,
            Func<OneClickRunOptions, Task<string>>?,
            OneClickOptionsPrompt>? promptFactory = DefaultPromptFactory;

        if (promptFactory == null)
        {
            // 没有宿主注入真窗口（无 UI 宿主）：不弹窗、不死等，调用方按设置值继续。
            return OneClickOptionsPrompt.NotShown();
        }

        return promptFactory(seed, Settings, facts, BuildDestinationEchoFactory(targets));
    }

    /// <summary>
    /// 确认框折叠区里改了落点之后，正文第一行要跟着重算。
    ///
    /// <para>重算这件事交给解压协调器（它手里有 <c>PathService</c> 与真实任务），
    /// 本类与窗口都**不拼任何路径** —— 落点永远只有一处实现。</para>
    /// </summary>
    private Func<OneClickRunOptions, Task<string>>? BuildDestinationEchoFactory(
        IReadOnlyList<ArchiveTask>? targets)
    {
        if (targets == null || targets.Count == 0)
        {
            return null;
        }

        return options => _extractionCoordinator.BatchReminders.DescribePlannedDestinationAsync(targets, options);
    }
}

using ArchiveFixer.BindingModels;
using ArchiveFixer.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 一键处理的**确认流程**（用户 2026-09-24 第 17 条）：整条流水线只在这里弹一次框，
/// 一次问清本次选项与"无用物 / 没有可用密码"那两条提醒，然后把用户的回答落到设置与界面。
///
/// <para>从 <c>OneClickCoordinator</c> 按职责搬出来（选项 ≠ 确认）：选项来源在
/// <see cref="OneClickOptionsPromptSource"/>，本类只做"问 → 取消 / 确认 / 无面板"三支的收尾。
/// ⛔ 判据与文案一个字不改：同一个框、同一批日志、同一次设置写入。</para>
///
/// <para>⚠ 调用位置**必须在 <c>EnterBusy</c> 之前**：主界面上那个进度条绑的正是 IsBusy，
/// 换到后面去就是用户点名的那条 bug（见 <see cref="OneClickBatchDriver"/> 里的说明）。</para>
/// </summary>
internal sealed class OneClickConfirmFlow
{
    private readonly MainViewModel _vm;
    private readonly ExtractionCoordinator _extractionCoordinator;
    private readonly OneClickOptionsPromptSource _options;

    internal OneClickConfirmFlow(
        MainViewModel vm,
        ExtractionCoordinator extractionCoordinator,
        OneClickOptionsPromptSource options)
    {
        _vm = vm;
        _extractionCoordinator = extractionCoordinator;
        _options = options;
    }

    private AppSettings Settings => _vm.Settings;

    private void AppendLog(string level, string message) => _vm.AppendLog(level, message);

    /// <summary>
    /// 问一次本次选项并落下收尾副作用。
    ///
    /// <para>返回 <c>Proceed = false</c> 的唯一含义是「用户在这个框上点了取消 / 这一次不跑」：
    /// 调用方必须**只写日志、什么都不做**（进程状态上也是真的"没发生"：IsBusy 还是 false、
    /// 引擎一次都没被调用、输出目录一个字节都没写）。</para>
    /// </summary>
    /// <returns>（是否继续, 本次选项快照；null = 按设置走）。</returns>
    internal async Task<(bool Proceed, OneClickRunOptions? Options)> AskAsync(
        IReadOnlyList<ArchiveTask> targets)
    {
        OneClickRunOptions? runOptions = null;
        OneClickRunOptions seed = _options.BuildSeed();
        RunSnapshot confirmSnapshot = RunSnapshot.Capture(
            targets,
            seed,
            _vm.SpaceTightMode,
            Settings.UseSpecificExtraction,
            _vm.SelectedOutputDirectory,
            _extractionCoordinator.SpecificExtractionPasswords);

        if (_options.ShouldAsk)
        {
            /*
             * 提醒（无用物 / 没有可用密码）只在设置开关打开时才扫：与 §9.7 共用同一个开关
             * （<see cref="AppSettings.RemindBeforeExtract"/>，用户 2026-09-24 第 15 条要求可关）。
             * 关掉它的人不该在确认框里再被念一遍，也不该为那次扫描（最坏十万条目录项）付代价。
             */
            BatchReminderHandler.BatchReminderFacts? reminders = null;

            if (Settings.RemindBeforeExtract)
            {
                reminders = await _extractionCoordinator.BatchReminders
                    .PrepareBatchReminderForCallerAsync(targets)
                    .ConfigureAwait(true);
            }
            else
            {
                _extractionCoordinator.BatchReminders.SuppressBatchReminderDialogForThisRun();
                AppendLog("INFO", StatusText.RemindBeforeExtractDisabledLog);
            }

            OneClickConfirmFacts facts = await _extractionCoordinator.BatchReminders
                .BuildConfirmFactsAsync(targets, seed, reminders, confirmSnapshot)
                .ConfigureAwait(true);

            OneClickOptionsPrompt prompt = _options.AskOnce(seed, facts, targets);

            if (prompt.Outcome == OneClickOptionsOutcome.Cancelled)
            {
                /*
                 * 确认框上点「取消」= 这一次不跑。
                 * 只写日志、**不再弹一个"已取消"的提示框**：用户刚刚才点过取消，
                 * 再弹一次等于把"取消"变成"两步操作"。
                 */
                AppendLog("INFO", "一键处理已取消（确认框没有确认，什么都没做）。");
                return (false, null);
            }

            if (prompt.Outcome == OneClickOptionsOutcome.Confirmed && prompt.Options != null)
            {
                runOptions = prompt.Options;

                if (runOptions.SuppressPanelNextTime)
                {
                    /*
                     * 用户 2026-09-24 第 17 条："而且这个可以选中以后不弹出"。
                     * 他说的"以后"不止这一次运行，所以写进设置（界面上 ② 解压方式 页
                     * 留着一个开关可以再打开）；同时记在内存里，本次运行立刻生效。
                     */
                    _options.SuppressPanel();
                    _vm.SaveSkipOneClickConfirm(true);
                    AppendLog("INFO", StatusText.OneClickConfirmSuppressSavedLog);
                }

                /*
                 * **把这次的选择记住**（用户 2026-10-01：「这不就相当于设置记忆吗，这么简单的问题不要再问了」）。
                 *
                 * <para>老口径是"只有勾了「把本次选择存为默认」才写设置"（硬要求②）。用户实测下来的感受是
                 * 每次一键处理都要重新点一次落点（"未选择指定位置"），而**设置记忆本来就是他要的** ——
                 * 一个要每天用的工具，不该让他每次都重选一遍。</para>
                 *
                 * <para>⛔ 记忆的**范围**只有弹窗上他看得见的那几档：落点（含指定位置那一格）、
                 * 源包处理、删除操作。⛔ 不碰其它任何设置；⛔ 取消 / 没弹框这两条路一个字节都不写。</para>
                 */
                _vm.SaveOneClickOptionsAsDefaults(runOptions);

                AppendLog(
                    "INFO",
                    $"本次选项已记住（写入设置，下次不用再选）：{runOptions.Describe()}"
                    + (runOptions.SaveAsDefault ? string.Empty : "（勾选框留作显式确认，行为已统一为记住）"));
            }
            else
            {
                // 没弹框（无 UI 宿主）：行为与加确认框之前**完全一致**（硬要求⑤）。
                AppendLog(
                    "INFO",
                    "一键处理：没有本次选项面板（无界面宿主），本次按设置走：" + seed.Describe());
            }

            /*
             * 弹窗收尾后把①③页那几个档位控件的显示**拉回真值**（2026-09-25 第 34 条）。
             *
             * 为什么必须有这一句：弹窗自己有一组同名的单选按钮，用户在里面看过/改过之后
             * 回到③页，界面上的黑点可能与设置里的真值不一致（"关掉弹窗回来，两个黑点都没了"
             * 正是他报的那个现象）。只做重新求值、不写任何设置 —— 与第 31 条"进页面就对齐"
             * 是同一个套路，这里是"关掉弹窗就对齐"。
             */
            _vm.SettingsEditor.NotifyProcessingOptionsChanged();
            _vm.SettingsEditor.NotifyOutputPlacementChanged();
        }
        else
        {
            /*
             * 勾过「以后不再询问」：一个框都不弹。
             *
             * 这里同时把解压前那个提醒也压掉（SuppressBatchReminderDialogForThisRun）——
             * 用户勾的是"直接开始"，只留日志。判定与日志一条都没少，少的只是弹窗。
             */
            _extractionCoordinator.BatchReminders.SuppressBatchReminderDialogForThisRun();

            AppendLog("INFO", "一键处理：已勾「以后不再询问」，本次直接按设置开始：" + seed.Describe());
        }

        return (true, runOptions);
    }
}

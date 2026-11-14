using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;


/// <summary>
/// 「一个包一次搞定」：把 识别 → 修正伪装后缀 → 按密码本试密码解压 → 一行汇总 串成一次操作
/// （AGENTS.md §10 的 M2 验收）。
///
/// 关于"一键"的准确含义（2026-09-25 用户改口径）：
/// **一键 = 点一次，剩下全自动** —— 需要改后缀的包**自动改名、不弹预览**（用户原话："这个一键处理
/// 自己会自动改名自动解压，为什么遇到这种改名的压缩包还要我两次确认……那种情况只有手动档才会有"）。
/// 手动档（②页那几条改名命令）照旧**先出预览**；一键档的每个改名照旧逐条写进日志与任务列表。
///
/// 关于自动续解（第 2 层起）：
/// 真实的"双面文件"（<c>xxx.mp4</c> = 视频头 + 尾部一个完整 ZIP）解出来往往**不是最终数据**，
/// 而是 <c>&lt;id&gt;.7z.001</c> + <c>.002</c> 这样的加密分卷，还要再解一层。
/// 以前一键处理只做第一层就停手（<see cref="AppSettings.RecursionMode"/> 默认 SingleLayer），
/// 用户看到的现象就是"只有第一层能解出来"。
///
/// 这里**刻意不碰** <c>Extraction/RecursiveExtractor</c>：那条递归路径在用户的真实文件上会卡死
/// （历史事故见 docs/需求变更.md），所以续解用的是**已经跑通的单层解压**重复跑 ——
/// 一轮就是一次正常的"识别 → 改名 → 解压"，只是把上一轮产出的内层包当成本轮输入，
/// 轮数卡死在 <see cref="RoundLimit"/> 轮（默认 7，天花板 <see cref="MaxRoundsCeiling"/> = 不变量 8）。RecursionMode 的默认值不动。
///
/// 刻意不做的事：
/// - 不自动删源包（那是 M3 的独立开关，默认关闭，且要校验通过才删）；
/// - 不改用户没勾选的任务（沿用既有"只处理选中项"的约定）——
///   续解只处理**本轮产物里新出现**的归档，产物目录里本来就有的包一个都不碰。
/// </summary>
internal sealed partial class OneClickCoordinator
{
    /// <summary>
    /// 轮数**硬上限**（不变量 8）：含第一层在内一共最多 **10 轮**。
    ///
    /// <para>⚠ 2026-09-24 第 16 条追加由用户拍板从 3 提到 10：他要的是**一键解到尽头**
    /// （原话："你为什么只弄了两层，我要的一键解压时多重解压……都到最后一步了还没成功"）。
    /// 但硬上限这件事本身不许取消（不变量 8）：真遇到"套了几十层"的包，继续无脑往下解
    /// 只会把时间和磁盘烧在一个可能失控的展开上。</para>
    ///
    /// <para>⚠ <b>2026-09-26：实际用几轮改由设置决定</b>（②页「最大嵌套层数」，见
    /// <see cref="RoundLimit"/>）—— 默认从 10 调到 **5**（他原话："现在将默认的最大的解压层数
    /// 从 10 改到 5 吧，用户有需要自己会改的"）。这个常量从此只当**天花板**用：
    /// 设置允许 1~10，绝不允许超过它。⛔ 界面写 5、程序跑到 10 这种不一致不许出现。</para>
    ///
    /// <para>到顶**不是静默停下**：剩下的内层包会被加进任务列表并勾好，汇总与弹窗里写明
    /// "还剩 N 个内层包没解"，界面给一个「继续解」按钮接着跑下一批
    /// （见 <see cref="OneClickOutcome.PendingContinuationCount"/>）。</para>
    /// </summary>
    internal const int MaxRoundsCeiling = 10;

    /// <summary>
    /// 这一批实际允许跑几轮 = ②页「最大嵌套层数」（默认 7），夹在 1~<see cref="MaxRoundsCeiling"/>。
    ///
    /// <para>为什么跟设置走：这一格的名字就叫"最大嵌套层数"，而一键处理是主流程 ——
    /// 两处各有一套上限只会让人以为"有一处没生效"（2026-09-24 把 3 与 10 统一成 10 就是这个原因，
    /// 2026-09-26 它跟着 10→5 一起下来，2026-10-14 用户拍板**默认再调到 7**，始终是同一个数字）。</para>
    /// </summary>
    internal int RoundLimit => Math.Clamp(
        Settings?.MaxRecursionDepth ?? 7,
        1,
        MaxRoundsCeiling);

    private readonly MainViewModel _vm;
    private readonly ScanCoordinator _scanCoordinator;
    private readonly RenameCoordinator _renameCoordinator;
    private readonly ExtractionCoordinator _extractionCoordinator;
    private readonly DialogService _dialogService;
    private readonly InnerLayerCollector _innerLayerCollector;
    private readonly OneClickBatchFinalizer _batchFinalizer;
    private readonly OneClickRoundRunner _roundRunner;
    private readonly OneClickSecondStepRunner _secondStepRunner;

    /// <summary>本次运行的不可变快照（阶段 4 影子：先冻结，尚未替换所有读取点）。</summary>
    internal RunSnapshot? CurrentRunSnapshot { get; private set; }



    /// <summary>
    /// 本次运行里用户勾过「以后不再询问」——之后的一键处理直接按设置走，不再弹确认框。
    ///
    /// <para>⚠ 2026-09-24 第 17 条之后它还有一个**落盘**的孪生兄弟
    /// （<see cref="AppSettings.SkipOneClickConfirm"/>，用户在框里勾了就由
    /// <c>MainViewModel.SaveSkipOneClickConfirm</c> 写进设置，跨重启有效）。
    /// 这个内存字段仍然要留着：本次运行内**立刻**生效，不用等下次读设置。</para>
    /// </summary>
    private bool _suppressOptionsPanel;

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

    public OneClickCoordinator(
        MainViewModel vm,
        ScanCoordinator scanCoordinator,
        RenameCoordinator renameCoordinator,
        ExtractionCoordinator extractionCoordinator,
        DialogService dialogService)
    {
        _vm = vm;
        _scanCoordinator = scanCoordinator;
        _renameCoordinator = renameCoordinator;
        _extractionCoordinator = extractionCoordinator;
        _dialogService = dialogService;
        _innerLayerCollector = new InnerLayerCollector(vm, extractionCoordinator);
        _batchFinalizer = new OneClickBatchFinalizer(vm, extractionCoordinator);
        _roundRunner = new OneClickRoundRunner(vm, extractionCoordinator, _batchFinalizer, _innerLayerCollector);
        _secondStepRunner = new OneClickSecondStepRunner(vm, extractionCoordinator, _batchFinalizer);
    }

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
    private OneClickOptionsPrompt AskRunOptionsOnce(
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

        return OneClickOptionsWindow.Show(seed, Settings, facts, BuildDestinationEchoFactory(targets));
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

        return options => _extractionCoordinator.DescribePlannedDestinationAsync(targets, options);
    }

    private ObservableCollection<ArchiveTask> Tasks => _vm.Tasks;
    private AppSettings Settings => _vm.Settings;
    private bool IsBusy => _vm.IsBusy;

    /*
     * ===== 续解落点规划（实现见 OneClickContinuationPlanner）=====
     *
     * 下面这些 internal 静态入口是既有用例与协调器的调用点（⛔ 判据只有一处，不许在这里再写一份）：
     * 本类只保留一行转发，行为与改动前逐字相同。
     */

    internal static string ResolveContinuationOutputDirectory(
        ArchiveTask parentTask,
        string? innerPackagePath = null,
        string? ownLayerName = null) =>
        OneClickContinuationPlanner.ResolveContinuationOutputDirectory(parentTask, innerPackagePath, ownLayerName);

    internal static bool IsOwnLayerOccupiedByPackageFile(
        string directory,
        string? ownLayerName,
        string? innerPackagePath) =>
        OneClickContinuationPlanner.IsOwnLayerOccupiedByPackageFile(directory, ownLayerName, innerPackagePath);

    internal static bool ShouldAddContinuationLevelLayer(
        bool parentProducedContent,
        int siblingCount) =>
        OneClickContinuationPlanner.ShouldAddContinuationLevelLayer(parentProducedContent, siblingCount);

    internal static string AppendOwnLayer(string directory, string? ownLayerName) =>
        OneClickContinuationPlanner.AppendOwnLayer(directory, ownLayerName);

    internal static string ResolveContinuationLayerName(string? innerPackagePath) =>
        OneClickContinuationPlanner.ResolveContinuationLayerName(innerPackagePath);

    internal static string ResolveContinuationBaseDirectory(
        ArchiveTask parentTask,
        string? innerPackagePath = null) =>
        OneClickContinuationPlanner.ResolveContinuationBaseDirectory(parentTask, innerPackagePath);

    /*
     * 忙碌标志必须**成对进出**（MainViewModel.EnterBusy/ExitBusy 是嵌套计数）。
     *
     * 旧写法是直接 `IsBusy = true` / `finally IsBusy = false`：一键处理跑到一半会去调
     * RenameCoordinator / ExtractionCoordinator，而它们各自的 finally 也会把标志置回 false ——
     * 于是外层还在跑的时候「停止后续 / 取消当前」按钮就变灰了（它们只看 IsBusy），
     * 守卫跟着失效，用户能在间隙里再点一次解压，同一个包被解两遍。
     * 计数之后只有最外层退出才真正变成"不忙"，见 MainViewModel.EnterBusy。
     */
    private void EnterBusy() => _vm.EnterBusy();
    private void ExitBusy() => _vm.ExitBusy();

    private void AppendLog(string level, string message) => _vm.AppendLog(level, message);
    internal void UpdateSummary() => _vm.UpdateSummary();

    public async Task RunAsync()
    {
        if (IsBusy)
        {
            return;
        }

        /*
         * 只处理**勾选**的任务。
         * 以前是"选了就处理选中的、没选就处理全部"，看起来贴心，实际是灾难：
         * 用户以为只动自己挑的那几个，结果整列表都被解压；日志里的任务数还会前后对不上。
         * 明确一点更好：没勾就提示去勾。
         */
        List<ArchiveTask> targets = Tasks.Where(t => t.IsSelected).ToList();

        if (targets.Count == 0)
        {
            /*
             * 一律只认勾选（用户 2026-09-24 第 12 条）：一个都没勾 → **只提示、什么都不做**。
             * ⛔ 这里永远不许再出现"没勾就退回当前点中的那一行"之类的兜底
             *（用户原话："你只需要操作我选中的文件，其他的不用管"）。
             *
             * 提示语来自 StatusText 的唯一来源，与「只解压」、清理类命令逐字相同 ——
             * 以前三处各写一套，用户看到三种说法，还以为是三个不同的问题。
             */
            _dialogService.ShowInfo(Tasks.Count == 0
                ? StatusText.TaskListEmptyPrompt
                : string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.NoCheckedTaskPromptFormat,
                    "一键处理",
                    Tasks.Count));

            AppendLog("WARN", StatusText.NoCheckedTaskLogFormat);

            return;
        }

        /*
         * ===== 确认框的位置：**EnterBusy 之前**（用户 2026-09-24 第 17 条）=====
         *
         * 旧写法是「先 EnterBusy，再弹面板」，而主界面上那个进度条绑的正是 IsBusy
         * （MainWindow.xaml 里的不确定进度条 + 「正在处理」那两行文字的可见性）——
         * 于是用户还没点确认，进度条就已经在动了。用户原话：
         * 「为什么我一键处理还没有开始确认，你的进度条就开始动了」。
         *
         * 换到 EnterBusy 之前以后，"没确认 = 什么都没发生"在**状态上**也是真的：
         * IsBusy 还是 false、引擎一次都没被调用、输出目录一个字节都没写。
         * ⛔ 别把它挪回 EnterBusy 后面 —— 那正是用户点名的那条 bug。
         *
         * 另外两件事也在这段里定下来：
         * ① 提醒（无用物 / 没有可用密码）**并进这一个框**（PrepareBatchReminderForCallerAsync），
         *    整条一键处理（含续解的第 2/3 轮）不再弹第二个框 —— 用户要的是"只能有一个弹窗"；
         * ② 勾了「以后不再询问」就**一个框都不弹**（含那个提醒），直接按设置开始。
         */
        OneClickRunOptions? runOptions = null;
        OneClickRunOptions seed = OneClickRunOptions.FromSettings(Settings, _vm.SelectedOutputDirectory);
        RunSnapshot confirmSnapshot = RunSnapshot.Capture(
            targets,
            seed,
            _vm.SpaceTightMode,
            _vm.SpaceTightKeepSource,
            Settings.UseSpecificExtraction,
            _vm.SelectedOutputDirectory,
            _extractionCoordinator.SpecificExtractionPasswords);

        try
        {
            bool askConfirm = OptionsPromptOverride != null
                              || (!_suppressOptionsPanel && !Settings.SkipOneClickConfirm);

            if (askConfirm)
            {
                /*
                 * 提醒（无用物 / 没有可用密码）只在设置开关打开时才扫：与 §9.7 共用同一个开关
                 * （<see cref="AppSettings.RemindBeforeExtract"/>，用户 2026-09-24 第 15 条要求可关）。
                 * 关掉它的人不该在确认框里再被念一遍，也不该为那次扫描（最坏十万条目录项）付代价。
                 */
                ExtractionCoordinator.BatchReminderFacts? reminders = null;

                if (Settings.RemindBeforeExtract)
                {
                    reminders = await _extractionCoordinator
                        .PrepareBatchReminderForCallerAsync(targets)
                        .ConfigureAwait(true);
                }
                else
                {
                    _extractionCoordinator.SuppressBatchReminderDialogForThisRun();
                    AppendLog("INFO", StatusText.RemindBeforeExtractDisabledLog);
                }

                OneClickConfirmFacts facts = await _extractionCoordinator
                    .BuildConfirmFactsAsync(targets, seed, reminders, confirmSnapshot)
                    .ConfigureAwait(true);

                OneClickOptionsPrompt prompt = AskRunOptionsOnce(seed, facts, targets);

                if (prompt.Outcome == OneClickOptionsOutcome.Cancelled)
                {
                    /*
                     * 确认框上点「取消」= 这一次不跑。
                     * 只写日志、**不再弹一个"已取消"的提示框**：用户刚刚才点过取消，
                     * 再弹一次等于把"取消"变成"两步操作"。
                     */
                    AppendLog("INFO", "一键处理已取消（确认框没有确认，什么都没做）。");
                    return;
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
                        _suppressOptionsPanel = true;
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
                _extractionCoordinator.SuppressBatchReminderDialogForThisRun();

                AppendLog("INFO", "一键处理：已勾「以后不再询问」，本次直接按设置开始：" + seed.Describe());
            }

            /*
             * ⛔ 2026-11-01（用户口径）：一键处理开工前**不再扫无用物**。
             * 无用物的自动剔除只发生在「选文件夹导入」那一次（见 ScanCoordinator.RemoveJunkTasksFromListAsync）：
             * 导入文件一般是用户点名的单个原包，解压过程中不应再动列表；手动清理走「移除勾选任务」。
             */

            EnterBusy();

            try
            {
                // 新的一批开始：上一次留下的"还剩 N 个内层包"提示先清掉（跑完按本次结论重设）。
                _vm.ReportPendingContinuation(0);

                OneClickOutcome outcome = await RunPipelineAsync(targets, runOptions);

                OneClickBatchFinalizer.CleanupSuccessfulProcessArtifacts(Tasks, message => AppendLog("INFO", message));

                _vm.ReportPendingContinuation(outcome.PendingContinuationCount);

                /*
                 * 批末的**最终汇总框**（用户 2026-09-27 第二次拍板："最终汇总框可以留、其他都删"）。
                 *
                 * 他上一次（同一天）说的是"一键处理期间一个弹窗都不弹"，因为那一批被 8–9 个
                 * 「确认」框打断（多分支递归各弹一次）—— 那些才是要删的。**批已经跑完**之后这一个
                 * 汇总是另一回事：那时他已经回来（不回来看不见"跑完了"），而且他点掉它就行，
                 * 不点也不会卡住任何任务（所有任务都已经结束、工作区都已收尾）。
                 *
                 * ⛔ 判定与日志一条都没少：下面那一行 `一键处理汇总：…` 照旧写进日志，
                 * 这个框只是把同一份 <see cref="OneClickOutcome.Summary"/> 摆到他眼前一次。
                 * ⛔ 也**只留这一个** —— 批中间那些"要用户回答"的框一律还是保守档 + 日志。
                 *
                 * ⚠ 2026-09-29 第 3 条：这个框按**机器终态**分三档配色（蓝 / 橙 / 红顶部色带，
                 * 正文白底黑字）。严重度由 <see cref="OneClickOutcome.Severity"/> 带过来 ——
                 * 窗口一个字都不判断（见 BatchSummarySeverityRules 的说明）。
                 *
                 * ⚠ 2026-09-30 第 1 条：正文不只是那一行数字了 —— 还要像编译器那样**点名说清错在哪**
                 * （空间不足差多少 / 哪些包可能是密码不对 / 111.7z.001 缺哪几卷…）。
                 * 那份清单与严重度出自同一次调用（见 RunPipelineAsync 里
                 * <c>BatchSummaryDiagnosticsRules.Build</c> 那一行），这里只是把它摆出来。
                 */
                _dialogService.ShowBatchSummary(outcome.DialogMessage, outcome.Severity);

                AppendLog("INFO", "一键处理汇总：" + outcome.Summary.Replace(Environment.NewLine, "；"));

                /*
                 * 那份清单同时逐组写进日志：用户不看日志，但**我们**要靠它排障 ——
                 * 而且真机反馈回来时，"框里写了什么"与"日志里写了什么"必须是同一份文字。
                 * 有问题才写（全成功那一批一个字都不加），组数与行数由清单本身封顶。
                 */
                foreach (string line in outcome.DiagnosticLines)
                {
                    AppendLog(
                        outcome.Severity == BatchSummarySeverity.Failed ? "WARN" : "INFO",
                        StatusText.BatchDiagnosticsLogPrefix + line);
                }
            }
            finally
            {
                ExitBusy();
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("WARN", "一键处理被取消。");
        }
        catch (Exception ex)
        {
            AppendLog("ERROR", "一键处理失败：" + ex.Message);
            _vm.Logger.Exception("一键处理失败", ex);
            _dialogService.ShowException(ex, "一键处理失败");
        }
        finally
        {
            // 「这一次由调用方负责弹框」只管这一次一键处理：跑完（含取消）就收回默认口径。
            _extractionCoordinator.EndCallerHandledReminderBatch();
        }
    }

    /// <summary>
    /// 真正干活的部分：**不带任何对话框**，返回结果对象。
    ///
    /// 为什么要单独一个入口：GUI 的 MessageBox 会真的弹出来（自动测试里没人点，就挂在那里了），
    /// 把"跑流程"和"弹窗"分开，流程本身才测得动。
    /// </summary>
    /// <param name="runOptions">
    /// 本次选项快照（规格 §9；null = 按设置走）。
    ///
    /// <para>
    /// ⚠ 它必须**一路传到最后一轮**：盘问只在 <see cref="RunAsync"/> 里做一次，
    /// 续解的第 2/3 轮、以及链结束后的源包补搬都用同一个快照 —— 中途重新问一次
    /// 或者中途退回设置值，都会让"这一次选的落点"在链的后半段静默变掉。
    /// </para>
    /// </param>
    internal async Task<OneClickOutcome> RunPipelineAsync(
        IReadOnlyList<ArchiveTask> firstRoundTargets,
        OneClickRunOptions? runOptions = null)
    {
        var state = new OneClickRunState();

        CurrentRunSnapshot = _roundRunner.Prepare(state, firstRoundTargets, runOptions);

        /*
         * 特定解压 = 独立整理模式，不再由旧规则清单决定是否生效。
         * 旧规则注册表里的“只留一层内容”已经不影响定稿；这里只看总开关。
         */
        if (CurrentRunSnapshot.UseSpecificExtraction)
        {
            return await RunSpecificBatchAsync(state.RootTargets, state.SnapshotOptions!, CurrentRunSnapshot).ConfigureAwait(true);
        }

        // 处理过的任务按轮累加：汇总要算"本次一共处理了多少个"，只算第一轮会和实际不符。
        state.Processed = new List<ArchiveTask>(state.RootTargets);

        // 源文件（含分卷各卷）不算内层包：不能把用户最初给的那个 .mp4 / .7z.001 又加一遍。
        state.SourcePaths = BuildSourcePathSet(state.RootTargets);

        state.RoundTargets = state.RootTargets.ToList();

        /*
         * 「用户按过停止后续」这件事必须单独记一笔。
         *
         * 只看 _vm.IsStopping 不行：ExtractionCoordinator 在批次开始和收尾时都会把它复位成 false，
         * 等我们拿回控制权时它已经是 false 了。只看"有没有任务没被处理"也不完全可靠：
         * 并发位被占着时批量循环还会再启动一个任务，未必留下未处理的任务。
         * 订阅属性变更拿到的是"用户确实按过"这个事实，不依赖上面两条时序。
         */
        void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsStopping) && _vm.IsStopping)
            {
                state.StopToken.RequestStop();
                state.StopRequested = true;
            }
        }

        _vm.PropertyChanged += OnViewModelPropertyChanged;

        // 这一趟的工作区跟踪从零开始：一键处理一条链会跨多轮，链尾统一收空壳。
        _extractionCoordinator.BeginWorkspaceRun();
        try
        {
            while (state.Round < RoundLimit)
            {
                state.Round++;

                var roundState = new OneClickRoundState();

                if (await _roundRunner.RunFirstStepAsync(this, state, roundState, runOptions).ConfigureAwait(true))
                {
                    break;
                }

                List<InnerArchiveCandidate> innerArchives = roundState.InnerArchives;
                List<string> scanLog = roundState.ScanLog;

                /*
                 * ⚠ **第二大步的轮边界：这一轮解开过的那几组卷，按删除档收掉**（2026-10-11，用户口径：
                 * 「**分卷记账是第二大步的事**」）。判据与执行体只有协调器那一个出口
                 * （`HarvestSolvedGroupVolumesForRound`：**入口包所在那一层** + 六道门槛 + 族尺子 + 用户源路径红线），
                 * ⛔ 这里不读任务表、⛔ 不看时序 —— 只把这一轮**判成是分卷**的那几个入口包交下去。
                 *
                 * ⚠ 位置必须在**轮判定这一行紧后面**：放到"有下一轮目标"那个块里 ⇒ 最后一轮（正是这一组解完的那一轮）
                 * 直接 `break` 跳过它（实测：EEE ③ 一点没变）。
                 */
                /*
                 * ===== 交界处消费端 · 第一段（只读）：用交接单做组分析 =====
                 *
                 * 用户 2026-10-24：交接处先只做「读」—— 交接单是事实来源（族/卷号/首卷/自述片数/自述整包），
                 * 这一遍只打 `[第二大步·组分析]` 事实行；⛔ 不点名、不搬片、不改名、不动盘。
                 * 组分析实现从 `SecondStepResidue/VolumeCandidateDiscovery.cs` 挑回来的（只读纯函数）。
                 */
                using SecondStepWorkScope secondStepWork = _extractionCoordinator.BeginSecondStepWork();

                if (await _secondStepRunner.RunAsync(this, state, roundState, innerArchives, scanLog, secondStepWork).ConfigureAwait(true))
                {
                    break;
                }

                if (await _roundRunner.RunRoundTailAsync(this, state, roundState, secondStepWork).ConfigureAwait(true))
                {
                    break;
                }
            }
        }
        finally
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            state.StopToken.Dispose();
        }

        return await _roundRunner.BuildOutcomeAsync(state).ConfigureAwait(true);
    }

    /// <summary>
    /// 这个产物文件要不要进这一轮「内层包扫描」的**统计与日志**（唯一出口）。
    ///
    /// <para>2026-10-05 真机：那一轮扫出 857 个文件，其中 2 个是 <c>其余物\HK.7z.001/.002</c>
    /// （**本批的源包**）—— 它们永远只会落一句"跳过"，却把"新文件"这个数字和日志行都撑大了
    /// （用户点名的"857 个文件、20 行日志"）。所以这里把"已经是我们自己的东西"挡在统计之外：
    /// 本批的源包、以及已经登记过路径的文件（在任务列表里 / 本轮已加进来的）。</para>
    ///
    /// <para>⛔ <b>只过滤"已知的"，绝不整棵排除「其余物」</b>：默认档（只解当前这一层 + 一键处理续解）下，
    /// 上一层产出的内层包正是**收进其余物之后**才被这一轮扫描认出来、当下一个任务接着解的
    /// （`InnerLayerContinuationTests` 那条 <c>outer.7z → inner.7z.001</c> 的链就是它）——
    /// 把整棵树排掉等于把续解链砍掉（2026-10-05 实测：18 条用例当场红）。</para>
    /// </summary>
    internal static bool ShouldScanProducedFile(
        string file,
        IReadOnlyCollection<string> sourcePaths,
        IReadOnlyCollection<string> knownTaskPaths)
    {
        /*
         * ⛔ **真正的 APK 不进"下一轮的内层包"名单**（用户 2026-10-10 拍板：有些 APK 是用户真的要
         * 转移到手机上安装的）：它按魔数就是 ZIP，所以"按内容认得出是归档"这条路一定会把它收进去，
         * 而我们要的是**一个字节都不碰它**。
         *
         * 判据转调唯一出口 `AndroidPackageDetector`（内容证据，⛔ 不看后缀；判不出 ⇒ 不拦）。
         * 三条路的第三处（另两处：单层入口 `ExtractSingleTaskAsync` 开头、
         * 递归层的内层包候选 `RecursiveExtractor`）。
         */
        if (Detection.AndroidPackageDetector.IsAndroidPackage(file))
        {
            return false;
        }

        return !sourcePaths.Contains(file) && !knownTaskPaths.Contains(file);
    }


    /// <summary>
    /// 一轮的"识别"步骤。
    /// 第 1 轮沿用原来的整体扫描；第 2 轮起**只扫本轮新加进来的包**。
    /// </summary>
    internal async Task ScanRoundAsync(int round, IReadOnlyList<ArchiveTask> roundTargets)
    {
        List<ArchiveTask> pending = roundTargets.Where(NeedsScan).ToList();

        if (pending.Count == 0)
        {
            return;
        }

        if (round == 1)
        {
            AppendLog("INFO", "一键处理：先识别格式。");
            await _scanCoordinator.ScanTasksAsync();
            return;
        }

        /*
         * 为什么不直接再调一次 ScanTasksAsync：
         * 它会遍历**整个任务列表**，把已经解压完的任务状态从"解压成功"冲回"已识别" ——
         * 用户会看到第 1 层的结果在列表里突然没了，汇总里的成功数也跟着归零。
         * 新加进来的包只需要识别它自己，按任务扫没有这个副作用。
         */
        foreach (ArchiveTask task in pending)
        {
            await _scanCoordinator.RescanTaskAsync(task);
        }
    }

    /// <summary>
    /// 把**已经处理过**的任务全部取消勾选（续解的每一步之前都要做，见调用处的说明）。
    ///
    /// <para><b>为什么收成一个出口</b>（2026-10-05 只读审计）：到顶停下那一支过去漏了这一步，
    /// 于是"继续解"会把上一批最后一轮已经解过的包再解一遍（产物同名 ⇒ <c>(1)</c> 垃圾副本）。
    /// 两处各写一遍的话，下次再有人加一支"停下"的分支照样会漏 —— 这里只留一份做法。</para>
    ///
    /// <para>必须走批量守卫（用户 2026-09-24 第 12 条"卡死"的修法之一）：逐项改勾选
    /// 会让**每一项**都触发一次全表汇总重算 + 38 条命令重查，
    /// 几百项的任务列表在这一步就是几百次 O(N) 扫描（O(N²)）。</para>
    /// </summary>
    internal void DeselectProcessedTasks(List<ArchiveTask> processed)
    {
        _vm.RunBulkSelectionUpdate(() =>
        {
            foreach (ArchiveTask task in processed)
            {
                task.IsSelected = false;
            }
        });
    }

    /// <summary>
    /// 把内层包加进任务列表，返回新加进来的任务（已显式勾选），并给每个内层任务挂上
    /// **父任务的最终目录**（<see cref="ArchiveTask.ParentOutputDirectory"/>）。
    ///
    /// 挂父目录这一步是"一个源包 = 一个最终目录"的接线点：
    /// <see cref="PathService.BuildOutputPath"/> 见到这个字段就直接返回它，
    /// 于是内层包不会再算出 <c>&lt;id&gt;.7z\内容物</c> 这种自己的目录。
    ///
    /// 为什么这一次导入要关掉"顺手整表重新识别"（<c>suppressAutoScan</c>）：
    /// AddPathsAsync 在「导入后自动识别」开启时会顺手对整个列表做一次重新识别，同样会把已经解压完的
    /// 任务状态冲回"已识别"（第 1 层的结果在界面上就没了）。这里只要"把新文件变成任务"，
    /// 识别由本轮自己按任务做（见 <see cref="ScanRoundAsync"/>）。
    ///
    /// ⚠ 2026-09-26 改：以前是"临时把 <c>Settings.AutoScanAfterDrop</c> 改成 false、用完还原" ——
    /// 那是**用户看得见、还会被自动保存写进盘**的一个设置项：万一在那段窗口里进程被杀（或断电报错），
    /// 用户的"导入后自动扫描"就永久变成关的，而他从没动过那个开关。
    /// 现在走 <c>AddPathsAsync</c> 的参数，一个设置项都不碰。
    /// </summary>
    internal async Task<List<ArchiveTask>> AddInnerTasksAsync(IReadOnlyList<InnerArchiveCandidate> candidates)
    {
        int before = Tasks.Count;

        /*
         * 同一基名 + 同一卷号如果用户已经导入过源片，链产出副本不再加成任务。
         * P53 真机：输入 P53-8 与成品 P53-8 各有一份 .001/.002，输出副本被加成一单
         * ⇒ 这一单先跑失败，第二大步入口反而选了它，而不是用户源片。
         * 判据只读“用户导入”与卷号，不按路径猜。
         */
        var userVolumeSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ArchiveTask existing in Tasks)
        {
            if (existing == null
                || existing.IsContinuationTask
                || string.IsNullOrWhiteSpace(existing.CurrentPath))
            {
                continue;
            }

            /*
             * ⛔ 2026-11-01 真机：`TryGetVolumeIndex("2.rar")` 会把裸 `.rar` 当成第 1 卷（slot=1），
             * 于是父包 `2.rar` 被记成"用户源片 slot 1"，把真正的 `2.part1.rar` 当成重复副本跳过
             * ⇒ 入口任务补建 0 个、链停住。只有**名字是分卷片**的才参与这个去重。
             */
            int? index = ArchiveFixer.Detection.LegacyVolumeNames.IsVolumePart(
                    FileNameHelper.GetFileName(existing.CurrentPath))
                ? LegacyVolumeNames.TryGetIndex(
                    FileNameHelper.GetFileName(existing.CurrentPath))
                : null;

            if (index.HasValue)
            {
                userVolumeSlots.Add(
                    ArchiveFixer.Helpers.FileNameHelper.SecondStepBaseName(existing.CurrentPath)
                    + "|" + index.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        List<InnerArchiveCandidate> tasksToAdd = candidates
            .Where(candidate =>
            {
                if (string.IsNullOrWhiteSpace(candidate.Path))
                {
                    return false;
                }

                int? index = ArchiveFixer.Detection.LegacyVolumeNames.IsVolumePart(
                        FileNameHelper.GetFileName(candidate.Path))
                    ? LegacyVolumeNames.TryGetIndex(
                        FileNameHelper.GetFileName(candidate.Path))
                    : null;

                return !index.HasValue
                    || !userVolumeSlots.Contains(
                        ArchiveFixer.Helpers.FileNameHelper.SecondStepBaseName(candidate.Path)
                        + "|" + index.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            })
            .ToList();

        if (tasksToAdd.Count < candidates.Count)
        {
            AppendLog(
                "INFO",
                $"一键处理：跳过 {candidates.Count - tasksToAdd.Count} 个内层包 —— "
                + "同基名同卷号的用户源片已在任务表里，链产出副本不重复当原包。");
        }

        /*
         * ⚠ 这里必须是**追加**（ImportMode.Append）。
         *
         * 「添加文件 / 添加文件夹」的默认语义是"清空整张表再加"（用户 2026-09-24 第 12 条），
         * 而这一步是解压途中往列表里补内层包 —— 用替换语义就会把用户的任务表
         * （连同刚刚解完的那些结果行）整张清掉。AddPathsAsync 刻意**不给默认值**，
         * 就是为了让这种调用点必须把意图写出来。
         */
        await _scanCoordinator.AddPathsAsync(
            tasksToAdd.Select(c => c.Path).ToList(),
            ImportMode.Append,
            suppressAutoScan: true);

        // 按完整路径找回"这个任务对应哪个内层候选"：AddPathsAsync 可能改写路径大小写，
        // 也可能因为重复而少加几个，所以用查表而不是按下标一一对应。
        var byPath = new Dictionary<string, InnerArchiveCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (InnerArchiveCandidate candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.Path))
            {
                byPath[NormalizePath(candidate.Path)] = candidate;
            }
        }

        var added = new List<ArchiveTask>();

        /*
         * 新内层包一律勾上，整批只刷一次汇总（批量守卫）——
         * 内层包可能有几十上百个，逐项勾选会逐项触发全表重算（O(N²)）。
         */
        _vm.RunBulkSelectionUpdate(() =>
        {
            for (int i = before; i < Tasks.Count; i++)
            {
                /*
                 * 新任务必须处于勾选状态才会被下一轮处理。
                 * FileScanService 建任务时就是 IsSelected = true（也确认过 ArchiveTask 的字段默认值），
                 * 但这条是"续解能不能继续"的命门，显式写一遍，免得以后有人改默认值时静默失效。
                 */
                Tasks[i].IsSelected = true;

                /*
                 * 按目录补跑一次分卷归组：已停用（用户 2026-10-24）。
                 * 实现 `ResolveVolumeGroupFromDirectory` 仍在原处（第二大步重建时可能要用），
                 * 但第一大步不再把同组卷并成一个任务。
                 */

                if (byPath.TryGetValue(NormalizePath(Tasks[i].CurrentPath), out InnerArchiveCandidate? candidate))
                {
                    Tasks[i].ParentOutputDirectory = candidate.ParentOutputDirectory;
                    Tasks[i].ParentTaskName = candidate.ParentTaskName;
                    Tasks[i].RootSourcePath = candidate.RootSourcePath;

                    /*
                     * 落点当场写一遍，别等界面刷新：续解下一轮要读 task.OutputPath 找内层包，
                     * 中间任何一次时序错位都会让第二层静默不跑（历史事故）。
                     */
                    if (!string.IsNullOrWhiteSpace(candidate.ParentOutputDirectory))
                    {
                        Tasks[i].OutputPath = candidate.ParentOutputDirectory;
                    }
                }

                added.Add(Tasks[i]);
            }
        });

        return added;
    }

    /// <summary>路径比较用的规范化形式（全路径 + 统一分隔符；大小写在 Windows 上不敏感）。</summary>
    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    /// <summary>
    /// **只算不写**：按一个任务**所在目录里的真实文件**算出它属于哪一组（算不出返回 <c>null</c>）。
    ///
    /// <para>为什么要把"算"和"写"分开（用户 2026-10-01）：调用方有两种 ——
    /// 一种要**把结论记到任务上**（续解层补组信息、开工前补整组清单），另一种只想**看一眼**
    /// （"我是不是这一组的后续卷"）。后者若顺手把账改了，就会把别处已经算好的清单**砍短**：
    /// 两卷分处两个目录时，按目录归组只会给出 1 卷，而任务账上那份（来自改名同步 /
    /// 扫描期归组）本来是完整的 —— 那会把"源包按整组搬"变成"只搬第一卷"。
    /// 判据只有这一份（<see cref="VolumeGroupDetector.Group"/>）。</para>
    /// </summary>
    internal static ArchiveFixer.Detection.VolumeGroup? ResolveVolumeGroupFromDirectory(ArchiveTask? task)
    {
        if (task == null)
        {
            return null;
        }

        string path = task.CurrentPath;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        var candidates = new List<ArchiveFixer.Detection.VolumeCandidate>();

        foreach (string sibling in Directory.EnumerateFiles(directory))
        {
            long size = -1;

            try
            {
                size = new FileInfo(sibling).Length;
            }
            catch
            {
                // 量不出大小不影响归组；"伪装名"那一档要尺寸佐证，量不出就不放行那一档。
            }

            candidates.Add(new ArchiveFixer.Detection.VolumeCandidate { Path = sibling, Size = size });
        }

        string fullPath = Path.GetFullPath(path);

        return ArchiveFixer.Detection.VolumeGroupDetector.Group(candidates)
            .FirstOrDefault(g => g.Volumes.Any(
                v => string.Equals(Path.GetFullPath(v.Path), fullPath, StringComparison.OrdinalIgnoreCase)));
    }
    /// <summary>
    /// **两个归组来源取更全的那一份**（用户 2026-10-01 第三 / 第四 / 第五次真机踩出来的唯一正确问法）。
    ///
    /// <para>现场（`222.zip` + `222.z01`，都是批首改好的标准名）：
    /// <see cref="ResolveVolumeGroupFromDirectory"/> 拿 `222.zip` **返回的不是 null，而是一个"只有 1 卷"的组**
    /// —— 因为按名字归组时 `.zip` 自己成一组、`222.z01` 只是它"没卷号的候选"；而
    /// <see cref="ResolveVolumeGroupFromDirectoryByName"/> 能看出两卷同基名。
    /// 前两轮白改的根因都是"只看能不能归出组"：第一次写成"自己的名字带卷标记"（`.zip` 不是 ⇒ 恒 false），
    /// 第二次写成"归不出组时才问按名字"（这里**归得出**，只是少一卷）⇒ 补清单永远轮不到。</para>
    ///
    /// <para>⛔ 判据只有一句：**谁卷数多就用谁**；一样多时保留按名字归的那份（它对"脏名兄弟"更敏感）。
    /// 调用方仍然走"只增不减"的闸门 —— 这个方法只回答"该用哪份清单"，不写任何账。</para>
    /// </summary>
    internal static ArchiveFixer.Detection.VolumeGroup? PickFullerGroup(
        ArchiveFixer.Detection.VolumeGroup? byDirectory,
        ArchiveFixer.Detection.VolumeGroup? byName)
    {
        if (byDirectory == null)
        {
            return byName;
        }

        if (byName == null)
        {
            return byDirectory;
        }

        return byName.Volumes.Count >= byDirectory.Volumes.Count ? byName : byDirectory;
    }

    /// <summary>
    /// **这一单一开工就必须按名字重算一次分卷清单吗**（用户 2026-10-01 第三 / 第四次真机）。
    ///
    /// <para>判据只有一条：**同目录里有没有和它同基名、名字带卷标记的兄弟**
    /// （`222.zip` 旁边躺着 `222.z01`）。⛔ 不能写成"自己的名字里带卷标记" ——
    /// 跨盘 zip 的**本体**叫 `222.zip`，`.zip` 不是卷标记 ⇒ 那条判断恒为 false，
    /// 补清单永远跑不到（第三报就是这样白改了一轮：400 MB 的 `222.z01` 照旧留在源目录）。</para>
    ///
    /// <para>抽成 internal 静态是为了让守门用例**直接钉这个判据**（不必构造整个 MainViewModel）。</para>
    /// </summary>
    internal static bool NeedsVolumeGroupRefreshByName(ArchiveTask? task, string? currentPath)
    {
        if (task == null || string.IsNullOrWhiteSpace(currentPath))
        {
            return false;
        }

        // 扫描期已经归过组的照旧要重算（账上那份可能只有一卷）。
        if (task.IsVolumeGroup)
        {
            return true;
        }

        // 标准卷名的续卷（`.001` / `.z01` / `.part2`）也要重算。
        if (LegacyVolumeNames.IsVolumePart(FileNameHelper.GetFileName(currentPath)))
        {
            return true;
        }

        // 剩下这一档就是真机那个形状：本体叫 `222.zip`，靠"同目录有没有卷兄弟"回答。
        return ResolveVolumeGroupFromDirectoryByName(task, currentPath) != null;
    }

    /// <summary>
    /// **按名字补整组清单**（用户 2026-10-01 第三报）：账上的分卷清单可能过期（改名 / 扫描期只认出一卷），
    /// 而 `222.zip` 单看又归不出组 ⇒ 这一档按"本目录里基名相同、名字里带卷标记"的文件重新支一组出来。
    ///
    /// <para><b>为什么需要它</b>：整组改名把 `222.zscip` → `222.zip`、`222.z删除01` → `222.z01` 都改对了，
    /// 可任务账上的清单还是扫描期那一份（1 卷）⇒ 源包搬运只搬了 `222.zip` 自己，
    /// 400 MB 的 `222.z01` 留在源目录里。</para>
    ///
    /// <para><b>判据只用事实</b>：基名相同（点号之前那一段）+ 名字里带卷标记（转调
    /// <see cref="ExtensionHelper.TrySplitVolumeSegmentTolerant"/> 这个唯一出口 —— `z01` 与 `z0sc1` 都认）
    /// + 文件真的在盘上。⛔ 目标名 / 卷序都不猜：这里只回答"本目录里哪些文件与它同属一组"，
    /// 整组搬不搬仍由调用方"只增不减"的闸门决定。</para>
    /// </summary>
    internal static ArchiveFixer.Detection.VolumeGroup? ResolveVolumeGroupFromDirectoryByName(
        ArchiveTask? task,
        string currentPath)
    {
        if (task == null || string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
        {
            return null;
        }

        string directory = Path.GetDirectoryName(currentPath) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        string fileName = Path.GetFileName(currentPath);

        /*
         * 基名 = **第二大步那一份**（唯一出口 `FileNameHelper.SecondStepBaseName`）。
         * 原来这里自己按第一个点切了一份 —— 结论与收口后的规则相同，但那是第二份实现
         * （用户 2026-10-23：「14 处以后统统删掉」）。
         * 没有点 / 以点开头 ⇒ 判不出（与收口前同一结论：这一档按名字归组，名字给不出基名就不动）。
         */
        string stem = FileNameHelper.SecondStepBaseName(fileName);

        if (stem.Length == 0 || stem.Length >= fileName.Length)
        {
            return null;
        }

        var volumes = new List<ArchiveFixer.Detection.VolumeCandidate>();
        int maxIndex = 0;

        foreach (string sibling in Directory.EnumerateFiles(directory))
        {
            string siblingName = Path.GetFileName(sibling);
            string siblingStem = FileNameHelper.SecondStepBaseName(siblingName);

            if (siblingStem.Length == 0
                || siblingStem.Length >= siblingName.Length
                || !string.Equals(siblingStem, stem, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string segment = siblingName[(siblingStem.Length + 1)..];
            int index;

            if (segment.Equals("zip", StringComparison.OrdinalIgnoreCase))
            {
                index = 1;
            }
            else if (ExtensionHelper.TrySplitVolumeSegmentTolerant(segment, out string mark, out _)
                     && mark.Length == 3
                     && (mark[0] == 'z' || mark[0] == 'Z')
                     && int.TryParse(mark.AsSpan(1), out int ordinal)
                     && ordinal >= 1)
            {
                index = ordinal + 1;
            }
            else
            {
                continue;
            }

            long size = -1;

            try
            {
                size = new FileInfo(sibling).Length;
            }
            catch
            {
                // 量不出大小不影响"这一卷在不在"这条事实。
            }

            volumes.Add(new ArchiveFixer.Detection.VolumeCandidate { Path = sibling, Size = size });
            maxIndex = Math.Max(maxIndex, index);
        }

        if (volumes.Count < 2)
        {
            return null;
        }

        /*
         * 组对象只填"清单"这三件事（基名 / 各卷 / 首卷 = 本体那一份）——
         * ⛔ 不编缺卷名、不编卷总数：调用方要的只是"这一组有哪些文件"。
         */
        string body = Path.Combine(directory, stem + ".zip");
        string firstVolumePath = File.Exists(body) ? body : volumes[0].Path;

        return new ArchiveFixer.Detection.VolumeGroup
        {
            GroupKey = directory + "|" + stem,
            DirectoryPath = directory,
            BaseName = stem,
            Volumes = volumes,
            FirstVolumePath = firstVolumePath,
            KnownVolumeCount = maxIndex,
            ExpectedVolumeCount = 0,
            IsComplete = true,
            MissingVolumeNames = Array.Empty<string>(),
            Note = $"按名字补出来的清单：同目录里 {volumes.Count} 个文件与「{fileName}」同基名（{stem}）且带卷标记"
        };
    }

    private static bool NeedsScan(ArchiveTask task)
    {
        return task.ExtensionStatus == StatusText.NotChecked ||
            string.IsNullOrWhiteSpace(task.ExtensionStatus) ||
            string.Equals(task.DetectedFormat, "Unknown", StringComparison.OrdinalIgnoreCase) ||
            task.Status == StatusText.WaitingScan;
    }

    /// <summary>
    /// 需要改名的三种后缀状态：缺失、不匹配、多重伪装。
    /// "后缀正常"不用动，"分卷后缀"**绝对不能动**（改了会切断分卷链），"格式未知"交给设置里的策略。
    /// </summary>

    /// <summary>源文件路径集合（当前路径 + 最初路径 + 该分卷组的每一卷）。</summary>
    internal static HashSet<string> BuildSourcePathSet(IEnumerable<ArchiveTask> tasks)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ArchiveTask task in tasks)
        {
            if (task == null)
            {
                continue;
            }

            foreach (string path in new[] { task.CurrentPath, task.OriginalPath }.Concat(task.VolumePaths))
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
            }
        }

        return paths;
    }

    /// <summary>
    /// 记录候选产物目录在**解压前**已有的文件，返回（已有文件集合, 解压前读不到内容的目录集合）。
    ///
    /// 返回"读不到内容的目录"的用途：这种目录里的老包和新产物混在一起、分不出新旧，
    /// 续解时整个不看它 —— 宁可漏掉一层，也不能把用户本来就放在那里的包当成新产物去解压。
    ///
    /// ⚠ 这里**刻意不再**返回"快照成功的目录集合"。
    /// 旧实现要求"当前目录必须在快照集合里"，于是漏掉了最要命的一种情况：
    /// 输出目录已存在且非空时，解压实际落到自动改名的 <c>xxx(1)</c>，而快照是解压**之前**做的，
    /// 里面只有原定的 <c>xxx</c> —— 内层包所在的 <c>xxx(1)</c> 被整段跳过，
    /// 第二层静默不跑，汇总仍然打印"一键处理完成：成功 N"。
    /// 换成"黑名单"之后，<c>xxx(1)</c> 这种本轮新建的目录天然被放行，
    /// 新旧之分仍然由 <c>existingFiles</c> 把关。
    ///
    /// 线程规则：目录集合与设置读取在调用线程（UI）上做，**枚举（可能几万个文件）放后台**。
    /// 一键处理整条流程跑在 UI 线程上，任何一次全目录遍历都会让窗口卡住。
    /// </summary>
    internal async Task<(HashSet<string> ExistingFiles, HashSet<string> UnreadableDirectories)> SnapshotCandidateDirectoriesAsync(
        IReadOnlyList<ArchiveTask> roundTargets)
    {
        // 没有输出目录就没法做"解压前后对比"。补算一次（与界面刷新输出目录用的是同一套规则）。
        if (roundTargets.Any(t => string.IsNullOrWhiteSpace(t.OutputPath)))
        {
            _vm.RefreshOutputPaths();
        }

        var directories = new List<string>();

        foreach (ArchiveTask task in roundTargets)
        {
            directories.AddRange(InnerLayerCandidateClassifier.CandidateDirectories(task));
        }

        // 归集目标目录在解压前就能确定：不先记下来，它里面的老文件会被当成这一轮的新产物。
        if (Settings.CollectResultsToDirectory && !string.IsNullOrWhiteSpace(Settings.CollectTargetDirectory))
        {
            directories.Add(Settings.CollectTargetDirectory);
        }

        List<string> targets = directories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        (HashSet<string> existingFiles, List<(string Directory, string Message)> unreadable) = await Task.Run(() =>
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failures = new List<(string, string)>();

            foreach (string directory in targets)
            {
                if (!Directory.Exists(directory))
                {
                    // 目录还不存在：解压之后出现在里面的都算新产物，不需要记任何东西。
                    continue;
                }

                try
                {
                    /*
                     * ⛔ 必须**排除工作区自己那棵树**（用户 2026-09-30）：工作区默认建在目标目录里面
                     * （<目标目录>\.ArchiveFixer.work），解压过程中它会被填满中间产物。
                     * 不排除的话，这些中间产物会被当成本轮"新出现的产物"，进而被当成内层包继续解 /
                     * 被当成内容物搬出去 —— 不可逆的事故。
                     * 判据唯一出口见 WorkspaceTree（名字 + 当前生效的根两条）。
                     */
                    foreach (string file in WorkspaceTree.EnumerateFiles(directory, _vm.CurrentWorkspaceRoot))
                    {
                        files.Add(file);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add((directory, ex.Message));
                }
            }

            return (files, failures);
        });

        var unreadableDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 日志回到 UI 线程再写（后台线程不许碰界面集合）。
        foreach ((string directory, string message) in unreadable)
        {
            unreadableDirectories.Add(directory);
            AppendLog("WARN", $"一键处理：读不了产物目录 {directory}（{message}），本轮不看这个目录。");
        }

        return (existingFiles, unreadableDirectories);
    }

    /// <summary>
    /// 从本轮**成功任务**的产物目录里找内层包，只取"归档的起点"，并带上**它属于哪个父任务**。
    ///
    /// 只取起点：把 <c>.002</c> / <c>.z01</c> / <c>.r00</c> / <c>part2</c> 这些后续段也加进任务列表，
    /// 只会造出一批假任务（它们自己不是完整归档，也不是组的开头）。
    ///
    /// 为什么每个候选都要带父任务：内层包不是独立任务，它的产物要归到**父任务那一个**最终目录里
    /// （用户诉求：一个源包 = 一个最终目录）。父目录由
    /// <see cref="ResolveContinuationOutputDirectory"/> 给出（归集开了就是归集目录）。
    ///
    /// 线程规则：目录枚举在后台（见 <see cref="SnapshotCandidateDirectoriesAsync"/> 的说明），
    /// 过滤是纯内存哈希查表，留在调用线程上做。
    /// </summary>
    /// <summary>
    /// 从本轮成功任务的产物里找"还能继续解"的内层包。
    ///
    /// <para><b>判据（2026-09-25 第 33 条改）：只认内容，⛔ 后缀不参与判决。</b>
    /// 老实现的第一道门是后缀白名单（旧后缀白名单）。真机现场：
    /// 第一层解出来的下一层叫 <c>222.ra删除r</c>（打包方在 <c>.rar</c> 中间插了「删除」两个字），
    /// 后缀 <c>.ra删除r</c> 不认识 → <b>在魔数体检之前就被扔掉</b> →
    /// 日志只写"第 1 层没有发现可继续解压的内层包"，用户看到的就是"只解了一层"。
    /// 而"不信后缀、只认魔数"恰恰是这个程序的前提 —— 这一步等于把它写反了，
    /// 于是**只对名字正常的内层包有效**（"换一种伪装就失效"的机制就在这里）。
    /// 现在：新产物逐个按内容判定 —— 先读文件头（<see cref="MagicArchiveProber"/>），
    /// 认不出再按"双面文件"读尾部（<see cref="EmbeddedArchiveDetector"/>，与识别阶段同一口径），
    /// 两条都不成立才不是包。</para>
    ///
    /// <para>仍然按名字排除的只有一种：<b>分卷的后续卷</b>
    /// （<c>.002</c>／<c>.z02</c>／<c>.r01</c>／<c>.part2+</c>）—— 那是"哪一卷是组的开头"这条
    /// <b>分组</b>信息，不是格式判断（见 <see cref="IsVolumeContinuationPart"/>）。</para>
    ///
    /// <para>代价有界：头部体检每个新文件一次；尾部体检只对"文件头认不出 + ≥ 64 KiB"的文件做，
    /// 每轮最多 <see cref="MaxTailProbesPerRound"/> 次，超了就写一条 WARN 说清还剩几个没体检
    /// （宁可说清"没看"，也不假装"没有"）。</para>
    ///
    /// <para>返回候选 + <b>一行一条的扫描日志</b>（认出了谁、跳过了谁、为什么跳）——
    /// 用户 2026-09-25 的要求是"出问题我看日志就能知道"，所以这一段的判据必须能自证。</para>
    /// </summary>
    /// <summary>
    /// **这一份是不是"某一组的第一卷，而且它的兄弟卷就在同目录里"**（轮尾扫描补的那一格判据）。
    ///
    /// <para>为什么是"第一卷"而不是"任意一片"：起点必须落在**入口包**上（用户口径「对哪个包输入密码，
    /// 就在哪个地方设工作区」）—— RAR 新式族 / 7z 数字族的入口就是第 1 卷；跨盘 ZIP 那一族的入口是
    /// **末片 `X.zip`**（它没有卷号），那一档由**按内容识别**那条路兜（它自己带 ZIP 头，引擎认得）。
    /// ⛔ 判据只读两条事实：卷号 == 1（既有那把尺子）、同目录里真有同一组的另一片 —— 判不出 ⇒ false。</para>
    /// </summary>
    /// <summary>「后续卷」判断的唯一入口（测试与既有调用点按这个名字指认；实现见 <see cref="InnerLayerCollector"/>）。</summary>
    internal static bool IsVolumeContinuationPart(string filePath) =>
        InnerLayerCandidateClassifier.IsVolumeContinuationPart(filePath);

    /// <summary>批末副作用（实现见 <see cref="OneClickBatchFinalizer"/>；本类只留既有入口）。</summary>
    internal void FinalizeBatch() => _batchFinalizer.FinalizeBatch();

    /// <summary>一行汇总（实现见 <see cref="OneClickBatchFinalizer"/>）。</summary>
    internal string BuildSummaryLine(
        IReadOnlyList<ArchiveTask> targets,
        bool stopped = false,
        bool stopRequestedByUser = false,
        int continuationLayers = 0,
        bool hitRoundLimit = false,
        int pendingContinuation = 0,
        string stoppedPhase = "") =>
        _batchFinalizer.BuildSummaryLine(
            targets, stopped, stopRequestedByUser, continuationLayers, hitRoundLimit, pendingContinuation, stoppedPhase);

    internal static bool IsSuccessStatus(ArchiveTask task) => OneClickBatchFinalizer.IsSuccessStatus(task);

    internal static bool IsHandled(ArchiveTask task) => OneClickBatchFinalizer.IsHandled(task);

    internal static bool IsFailureStatus(ArchiveTask task) => OneClickBatchFinalizer.IsFailureStatus(task);

    internal static int CleanupSuccessfulProcessArtifacts(
        IEnumerable<ArchiveTask>? tasks,
        Action<string>? log = null) =>
        OneClickBatchFinalizer.CleanupSuccessfulProcessArtifacts(tasks, log);

    internal static bool IsContentCanonicalName(string path) =>
        OneClickBatchFinalizer.IsContentCanonicalName(path);
}

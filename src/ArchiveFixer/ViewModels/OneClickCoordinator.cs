using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 一键处理的结果。
    ///
    /// 为什么要一个对象而不是只返回那行汇总：测试要能直接断言"跑了几轮、停在哪"，
    /// 拿中文汇总去 <c>Contains</c> 只是把文案当接口，文案一改测试就假红/假绿。
    /// </summary>
    internal sealed class OneClickOutcome
    {
        /// <summary>实际跑了几轮（1 = 只有第一层，也就是最常见的情况）。</summary>
        public int Rounds { get; init; }

        /// <summary>自动续解了几层（正常跑完时 = 轮数 - 1）。</summary>
        public int ContinuationLayers { get; init; }

        /// <summary>是不是被「停止后续」打断的（被打断就不许显示成成功）。</summary>
        public bool Stopped { get; init; }

        /// <summary>是不是撞到轮数硬上限、还有更深的内层包没解。</summary>
        public bool HitRoundLimit { get; init; }

        /// <summary>
        /// 撞上限时**还剩多少个内层包没解**（已经加进任务列表并勾好，等用户点「继续解」）。
        ///
        /// <para>用户 2026-09-24 第 16 条追加："你为什么只弄了两层，我要的一键解压时多重解压……都到最后一步了还没成功"。
        /// 到顶**必须提示还剩多少 + 给一键继续**，不许静默停下 —— 这个数字就是那句提示与那个按钮的依据。</para>
        /// </summary>
        public int PendingContinuationCount { get; init; }

        /// <summary>一行汇总（已经写进日志，GUI 用它弹提示）。</summary>
        public string Summary { get; init; } = string.Empty;

        /// <summary>
        /// 这一批的**汇总严重度**（用户 2026-09-29 第 3 条：批末那个框要按结果分颜色）。
        ///
        /// <para>判定只读机器终态（<see cref="ArchiveTask.Outcome"/> / 校验枚举），
        /// 唯一出口是 <see cref="BatchSummarySeverityRules.FromTasks"/>；
        /// 这里只是把它**带出来**，供 GUI 选蓝 / 橙 / 红那条色带 ——
        /// ⛔ 别在窗口里按 <see cref="Summary"/> 这段中文再判断一次。</para>
        /// </summary>
        public BatchSummarySeverity Severity { get; init; } = BatchSummarySeverity.Success;

        /// <summary>
        /// **出错在哪**那份清单（用户 2026-09-30 第 1 条）：按机器终态 / 错误分类分组，
        /// 逐组点名具体包名；空字符串 = 这一批没有任何"没做成"的事。
        ///
        /// <para>它与 <see cref="Severity"/> 出自**同一次**调用
        /// （<see cref="BatchSummaryDiagnosticsRules.Build"/>，见 <see cref="RunPipelineAsync"/> 里那一行）：
        /// 严重度管颜色、诊断管文字，两者是同一份任务终态算出来的，⛔ 不是各算一遍。</para>
        /// </summary>
        public string Diagnostics { get; init; } = string.Empty;

        /// <summary>
        /// 上面那份清单的**逐行**形态（组行 + 「下一步」那一行，不含标题）：日志一行一条写它。
        ///
        /// <para>与 <see cref="Diagnostics"/> 出自同一个 <see cref="BatchSummaryReport"/> ——
        /// 框里摆的那份文字与日志里写的那几行逐字一致（用户 2026-09-30 第 1 条的排障前提）。</para>
        /// </summary>
        public IReadOnlyList<string> DiagnosticLines { get; init; } = Array.Empty<string>();

        /// <summary>
        /// 批末那个框的**正文** = 一行汇总 + （有问题时）诊断清单。
        ///
        /// <para>日志仍用 <see cref="Summary"/> 那一行（一条一行好搜）；摆给用户看的用这一份 ——
        /// 他原话："你和我这总会仔细看日志，但是用户不会，他们只想看看错误出在哪里"。</para>
        /// </summary>
        public string DialogMessage => string.IsNullOrWhiteSpace(Diagnostics)
            ? Summary
            : Summary + Environment.NewLine + Environment.NewLine + Diagnostics;
    }

    /// <summary>
    /// 「本次选项」面板的询问结果（规格 <c>docs/输出与整理模型.md</c> §9）。
    ///
    /// <para>
    /// 为什么要区分"没弹"和"用户取消"：两者的正确行为**相反** ——
    /// 没弹（无 UI 宿主 / 勾过"不再询问"）要"按设置走、继续跑"；
    /// 用户明确取消则**这一次不许开跑**。把它们都压成 <c>null</c> 就会出现
    /// "用户在面板上点了取消，结果整批照跑"这种最不该有的形态。
    /// </para>
    /// </summary>
    internal enum OneClickOptionsOutcome
    {
        /// <summary>没弹面板：无 UI 宿主，或本次运行里已经勾过"以后不再询问"。**按设置走，继续跑**。</summary>
        NotShown = 0,

        /// <summary>用户确认了本次选项。</summary>
        Confirmed = 1,

        /// <summary>用户取消（点「取消」或直接关掉面板）。**这一次一键处理不执行**。</summary>
        Cancelled = 2
    }

    /// <summary>一次「本次选项」询问的结果：结论 + （确认时的）快照。</summary>
    internal readonly struct OneClickOptionsPrompt
    {
        private OneClickOptionsPrompt(OneClickOptionsOutcome outcome, OneClickRunOptions? options)
        {
            Outcome = outcome;
            Options = options;
        }

        public OneClickOptionsOutcome Outcome { get; }

        /// <summary>只有 <see cref="OneClickOptionsOutcome.Confirmed"/> 时非 null。</summary>
        public OneClickRunOptions? Options { get; }

        public static OneClickOptionsPrompt NotShown() =>
            new(OneClickOptionsOutcome.NotShown, null);

        public static OneClickOptionsPrompt Confirmed(OneClickRunOptions options) =>
            new(OneClickOptionsOutcome.Confirmed, options);

        public static OneClickOptionsPrompt Cancelled() =>
            new(OneClickOptionsOutcome.Cancelled, null);
    }

    /// <summary>
    /// 本轮找到的一个内层归档，以及**它属于哪个父任务**。
    ///
    /// 为什么要带父任务（本轮改造的核心之一）：内层包不再被当成"另一个独立的任务"，
    /// 而是父任务这条流水线的一部分 —— 它的产物最终必须归到**父任务那一个**最终目录里。
    /// 只传文件路径的话，内层包会按自己的路径算落点（<c>&lt;id&gt;.7z\内容物</c>），
    /// 源目录旁边就又多一个平级目录 —— 正是用户抱怨的那个现象。
    /// </summary>
    internal sealed class InnerArchiveCandidate
    {
        /// <summary>内层归档的起点（分卷组的第一卷，或者单文件归档）。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>父任务的最终目录（归集之后的落点）；内层任务的产物就落在这里。</summary>
        public string ParentOutputDirectory { get; init; } = string.Empty;

        /// <summary>父任务的名字（只用于日志与报告）。</summary>
        public string ParentTaskName { get; init; } = string.Empty;

        /// <summary>
        /// 这条续解链**根任务的身份**（= 根任务那一刻的规范化绝对路径）。
        /// <para>为什么必须按路径认链：链尾「删除操作」原先只看"是不是续解任务"，而调用方传进来的是
        /// **整批**任务清单 —— 于是 A 目录那个包的链尾，被 B 目录里**同名**的另一个包的失败挡住
        /// （真机 2026-09-30 日志原文：`333-Rar4.part1.rar：链尾的其余物不处理（链上的「111.part1.rar」没有成功）`）。
        /// 名字相同 ≠ 同一条链。</para>
        /// </summary>
        public string RootSourcePath { get; init; } = string.Empty;
    }

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
                    var handoffGroups = new List<VolumeCandidateDiscovery.VolumeCandidate>();

                    /*
                     * 第二大步"当前工作"句柄：解析 / 试开 / 命名 / 建入口根都归它。
                     * 暂停或「停止当前任务」取消它 ⇒ 试开里的引擎进程立刻被取消，且不会再进下一批。
                     */
                    using SecondStepWorkScope secondStepWork = _extractionCoordinator.BeginSecondStepWork();

                    try
                    {
                        /*
                         * ⛔ 第二大步不再物理改用户源名：`.001删 → .001` 由 StandardNameFastPath
                         * 的逻辑规范名 + 入口层硬链接承担。物理改名会拆散任务/账本/prepared 三份路径
                         * （真机 336 的 13:45/13:51 两次路径不一致就是这么来的）。
                         */

                        List<string> groupRoots = _extractionCoordinator.VolumeHandoffEntries
                            .Select(entry => entry.SourceLayer)
                            .Concat(_extractionCoordinator.VolumeHandoffEntries
                                .Select(entry => Path.GetDirectoryName(entry.Path ?? string.Empty) ?? string.Empty))
                            .Where(directory => !string.IsNullOrWhiteSpace(directory))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        /*
                         * 候选池的种子不能只来自交接单：脏名字的片（`.zip.@z002`、`.7z.=Z002`）
                         * 内容认不出、闸门不会登记，可它们确实是某一组的片（用户第 1 步：基名+大小）；
                         * 没有种子 ⇒ 整组永远不入候选池 ⇒ 源包/其余物那里红。
                         * ⇒ 在组根里补“名字/内容像分卷片”的种子，只做候选，判定仍交给族算法。
                         */
                        var extraGroupSeeds = new List<string>(innerArchives.Select(candidate => candidate.Path));

                        foreach (string root in groupRoots)
                        {
                            try
                            {
                                foreach (string file in Directory.EnumerateFiles(
                                             root,
                                             "*",
                                             SearchOption.TopDirectoryOnly))
                                {
                                    if (InnerLayerCandidateClassifier.IsVolumeCandidateSeed(file))
                                    {
                                        extraGroupSeeds.Add(file);
                                    }
                                }
                            }
                            catch
                            {
                                // 扫不到的目录只影响候选池，不影响任何破坏性动作。
                            }
                        }

                        List<string> analysisRoots = groupRoots
                            .Concat(_extractionCoordinator.FirstStepLedger.Entries
                                .Select(entry => entry.SourceLayer)
                                .Where(layer => !string.IsNullOrWhiteSpace(layer)))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        /*
                         * 第二大步候选的唯一正式来源 = FirstStepFileLedger。
                         * VolumeHandoffEntries 只是其中一部分，必须把账本里
                         * 未单独建交接单的 Unknown/内层片也补成候选。
                         */
                        List<ArchiveFixer.Detection.VolumeHandoffEntry> ledgerHandoff =
                            _extractionCoordinator.FirstStepLedger.Entries
                                /*
                                 * ⛔ 账本收所有已发布产物（MP4/TXT/PNG 也在内），但第二大步候选只认
                                 * 分卷事实：内容身份/卷号/名字槽/ZIP split 证据/交界前租约。
                                 * 不过这一道会把“内容文件”当成 1 片候选（真机 15:32/15:33 的
                                 * `SSNI-727-UC`、`promotion`、`点我安装`）。
                                 * 脏名的 Unknown 片仍由下面的 extraGroupSeeds 扫描补进来，不在这里放宽。
                                 */
                                .Where(entry => entry != null && entry.HasSecondStepEvidence)
                                .Select(entry => new ArchiveFixer.Detection.VolumeHandoffEntry
                                {
                                    Path = entry.Path,
                                    BaseName = entry.BaseName,
                                    Format = entry.Format,
                                    Size = entry.Size,
                                    BodyKind = entry.BodyKind,
                                    ContentKind = entry.ContentKind,
                                    ContentClass = entry.ContentClass,
                                    ContentFormat = entry.ContentFormat,
                                    Evidence = entry.BodyKind is ArchiveFixer.Detection.ArchiveBodyKind.VolumeFirstPiece
                                            or ArchiveFixer.Detection.ArchiveBodyKind.VolumePiece
                                        ? ArchiveFixer.Detection.VolumeHandoffEvidence.ContentProven
                                        : entry.NameSlot.HasValue
                                            ? ArchiveFixer.Detection.VolumeHandoffEvidence.NameSlot
                                            : ArchiveFixer.Detection.VolumeHandoffEvidence.Unknown,
                                    VolumeNumber = entry.VolumeNumber,
                                    NameVolumeIndex = entry.NameSlot,
                                    FirstVolumeFlag = entry.FirstVolumeFlag,
                                    Membership = entry.Membership,
                                    DeclaredTotalBytes = entry.DeclaredTotalBytes,
                                    DeclaredVolumeCount = entry.DeclaredVolumeCount,
                                    RawVolumeField = entry.RawVolumeField,
                                    HasZipEndRecord = entry.HasZipEndRecord,
                                    ZipEndRecordMeansSplit = entry.ZipEndRecordMeansSplit,
                                    HasZipLocalHeader = entry.HasZipLocalHeader,
                                    SourceLayer = entry.SourceLayer,
                                    Source = entry.Source
                                })
                                .Concat(_extractionCoordinator.VolumeHandoffEntries)
                                .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                                .Select(group => group.First())
                                .ToList();

                        /*
                         * ⛔ 2026-11-01 用户口径：「内容物保留关键词」命中的文件**碰都不要碰**，
                         * 自然也不许进第二大步的分卷候选（`上老王论坛当老王.zip` 曾被当 1 片分卷组）。
                         */
                        ArchiveFixer.Security.ContentKeepRules keepRules =
                            ArchiveFixer.Security.ContentKeepRules.FromSettings(Settings);

                        ledgerHandoff = ledgerHandoff
                            .Where(entry => entry != null && !keepRules.ShouldKeep(entry.Path))
                            .ToList();
                        extraGroupSeeds = extraGroupSeeds
                            .Where(seed => !keepRules.ShouldKeep(seed))
                            .ToList();

                        /*
                         * ⛔ 2026-11-01 真机（2.exe 与 2.part1/2/3.rar）：父包是**任务源包**，
                         * 内容卡是 Unknown（内嵌归档）⇒ 任何"内容自证 SingleComplete"的过滤都抓不到它。
                         * 判据放回**来源层**：根任务源包自身没有分卷证据（不是 Volume 片、没有名字卷槽）
                         * ⇒ 它绝不参与自己产物所组成的分卷组。
                         */
                        var nonVolumeRootSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var ledgerByPath = _extractionCoordinator.FirstStepLedger.Entries
                            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Path))
                            .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

                        foreach (ArchiveTask root in Tasks)
                        {
                            if (root == null || root.IsContinuationTask)
                            {
                                continue;
                            }

                            foreach (string? rootPath in new[] { root.CurrentPath, root.OriginalPath })
                            {
                                if (string.IsNullOrWhiteSpace(rootPath)
                                    || !ledgerByPath.TryGetValue(rootPath, out ArchiveFixer.Detection.FirstStepFileLedgerEntry? rootEntry))
                                {
                                    continue;
                                }

                                if (rootEntry.BodyKind != ArchiveFixer.Detection.ArchiveBodyKind.VolumeFirstPiece
                                    && rootEntry.BodyKind != ArchiveFixer.Detection.ArchiveBodyKind.VolumePiece
                                    && !rootEntry.NameSlot.HasValue
                                    && rootEntry.Membership != ArchiveFixer.Detection.VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember)
                                {
                                    nonVolumeRootSources.Add(rootPath);
                                }
                            }
                        }

                        /*
                         * 路径事实（2026-11-10 真机）：账本里标成「已消费源包」的路径 = 这一单自己的源包
                         * 已经解成功并定稿 ⇒ ⛔ 不论后缀、不论基名、不论名字里有几个数字，都不许再当分卷候选/种子。
                         * 真机现场：`D:\ArchiveFixer\logs\ArchiveFixer_20261110_161401.log` 719/794/801/852/853 行，
                         * 外层 tar 与它解出的 5 片同基名，tar 被当成"中间片"收进组 ⇒ 整包字节和翻倍 ⇒ SizeAnomaly。
                         */
                        foreach (ArchiveFixer.Detection.FirstStepFileLedgerEntry consumedEntry
                                 in ledgerByPath.Values.Where(entry => entry.IsConsumedSource))
                        {
                            nonVolumeRootSources.Add(consumedEntry.Path);
                        }

                        UiActivity.Mark("第二大步组分析", string.Empty, "扫描交接单候选");
                        List<VolumeCandidateDiscovery.VolumeCandidate> analyzedGroups = await Task.Run(
                            () => VolumeCandidateDiscovery.Analyze(
                                ledgerHandoff,
                                analysisRoots,
                                extraGroupSeeds.Distinct(StringComparer.OrdinalIgnoreCase),
                                nonVolumeRootSources).ToList());
                        UiActivity.Mark("空闲");

                        foreach (VolumeCandidateDiscovery.VolumeCandidate group in analyzedGroups)
                        {
                            handoffGroups.Add(group);
                            AppendLog("INFO", $"[第二大步·组分析] {group.Describe()}");
                        }

                        /*
                         * 共用骨架接线点：族 resolver 先出 prepared 组，再让 VolumeCandidateDiscovery 只吃标准名。
                         * 骨架阶段一个族都没注册 ⇒ 返回空，下面的过滤与追加都不会发生，既有行为不变。
                         */
                        foreach (ArchiveTask ledgerTask in Tasks)
                        {
                            if (!string.IsNullOrWhiteSpace(ledgerTask.CurrentPath)
                                && !string.Equals(
                                    ledgerTask.OriginalPath,
                                    ledgerTask.CurrentPath,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                _extractionCoordinator.FirstStepLedger.RetargetPath(
                                    ledgerTask.OriginalPath,
                                    ledgerTask.CurrentPath);
                            }
                        }

                        IReadOnlyList<PreparedVolumeGroup> preparedGroups =
                            await _extractionCoordinator.VolumeBoundaryPipeline.ResolveAllAsync(
                                    _extractionCoordinator.FirstStepLedger,
                                    baseName =>
                                    {
                                        long trialLastTicks = 0;
                                        int trialLastPercent = -1;

                                        return new VolumeBoundaryContext
                                        {
                                            Ledger = _extractionCoordinator.FirstStepLedger,
                                            BaseName = baseName,
                                            Engine = _extractionCoordinator.ArchiveEngine,
                                            WorkspaceRoot = ArchiveFixer.Extraction.RecursiveExtractor.ConfiguredWorkspaceRoot,

                                            /*
                                             * 「代表路径 / 代表层」只从候选片取（SecondStepCandidates）：
                                             * 同基名可能同时挂着外层包（X.tar 与 X.7z.001 都切成 X），
                                             * 用外层包那条的路径/层会让密码探测和快通道键全偏（真机 2026-11-10）。
                                             */
                                            PasswordCandidates = _extractionCoordinator.BuildVolumeProbePasswordCandidates(
                                                _extractionCoordinator.FirstStepLedger.CandidateRepresentativePath(baseName)),
                                            OriginalSourceLayer = _extractionCoordinator.FirstStepLedger
                                                .CandidateSourceLayer(baseName),
                                            Log = (level, message) => AppendLog(level, message),
                                            Progress = new Progress<ArchiveFixer.Engines.ArchiveProgress>(progress =>
                                            {
                                                long now = Environment.TickCount64;

                                                if (progress.Percent == trialLastPercent
                                                    && now - trialLastTicks < 5000)
                                                {
                                                    return;
                                                }

                                                trialLastPercent = progress.Percent;
                                                trialLastTicks = now;
                                                AppendLog(
                                                    "INFO",
                                                    progress.Percent >= 0
                                                        ? $"[第二大步·试开] {baseName}：{progress.Percent}%"
                                                        : $"[第二大步·试开] {baseName}：引擎正在扫描（还没有百分比）");
                                            }),
                                            Stalled = notice => AppendLog(
                                                "WARN",
                                                $"[第二大步·试开] {baseName}：{notice}")
                                        };
                                    },
                                    secondStepWork.Token)
                                .ConfigureAwait(true);

                        secondStepWork.Token.ThrowIfCancellationRequested();

                        /*
                         * ⛔ 2026-11-01 真机：字节补齐配对已在组分析层证完整（`风景01`），
                         * 但 prepared resolver 对同名跨目录首片只给 Unknown；它一旦参与建组，
                         * 会把 Complete 的结论顶掉。⇒ 同一基名已有字节补齐 Complete 时，
                         * **非 Complete 的 prepared 组不参与**这一轮的组建设。
                         */
                        var byteFitCompleteBases = new HashSet<string>(
                            analyzedGroups
                                .Where(group => group.ByteFitPaired)
                                .Select(group => group.BaseName),
                            StringComparer.OrdinalIgnoreCase);

                        if (byteFitCompleteBases.Count > 0)
                        {
                            preparedGroups = preparedGroups
                                .Where(group => group.IsComplete
                                    || !byteFitCompleteBases.Contains(group.BaseName))
                                .ToList();
                        }

                        if (preparedGroups.Count > 0)
                        {
                            /*
                             * 标准名硬链接必须在 VolumeCandidateDiscovery 之前建好：VolumeCandidateDiscovery 只吃临时入口层。
                             * 建不出来 ⇒ 这一组落 Unknown（⛔ 不退回脏名硬拼、⛔ 不猜）。
                             */
                            var materialized = new List<PreparedVolumeGroup>();

                            foreach (PreparedVolumeGroup prepared in preparedGroups)
                            {
                                secondStepWork.Token.ThrowIfCancellationRequested();

                                if (prepared.State != PreparedGroupState.Complete)
                                {
                                    materialized.Add(prepared);
                                    continue;
                                }

                                string? targetWorkRoot = _batchFinalizer.ResolvePreparedGroupTargetWorkRoot(prepared);
                                PreparedVolumeGroup? linked = VolumeGroupEntryDirectory.TryMaterialize(
                                    prepared,
                                    VolumeGroupEntryDirectory.TryResolveTargetEntryRoot(
                                        prepared,
                                        targetWorkRoot,
                                        allowSourceVolumeFallback: _vm.SpaceTightMode),
                                    (level, message) => AppendLog(level, $"[第二大步·标准名] {message}"),
                                    out string linkError,
                                    allowCrossVolumeCopy: !_vm.SpaceTightMode);

                                /*
                                 * ⛔ 不再为目标盘与源片不同卷回落源卷根/盘根：
                                 * 拿不到合法工作区入口就不建目录，由这一组落 Unknown。
                                 */
                                if (linked == null)
                                {
                                    materialized.Add(prepared.WithState(
                                        PreparedGroupState.Unknown,
                                        $"标准名硬链接没建好：{linkError}"));
                                }
                                else
                                {
                                    materialized.Add(linked);
                                }
                            }

                            preparedGroups = materialized;

                            /*
                             * 交界屏障：prepared 组此刻已经拿到族/片/入口，登记成内容组，
                             * 接上第一大步账本里的来源根与落点域。只登记事实，不点名、不动盘。
                             */
                            _extractionCoordinator.RegisterContentGroups(preparedGroups);

                            /*
                             * ⛔ 2026-11-01：旧行为"prepared 组一旦存在，同基名旧候选让位"必须保留
                             * （真机 904 批：让位被改成"只有 Complete 才让位"后，多出来的 Unknown 候选
                             * 干扰了正常组结算 ⇒ c4 过程物按档 6 条红）。
                             * 唯一例外：**字节补齐配对**已经在同基名给出 Complete，而 resolver 对该基名
                             * 只有 Unknown（2916/3100 的 `风景01`）—— 这一档保留配对组。
                             */
                            var preparedBaseNames = new HashSet<string>(
                                preparedGroups.Select(group => group.BaseName),
                                StringComparer.OrdinalIgnoreCase);

                            /*
                             * `SizeAnomaly` 的 prepared 组按设计没有 Pieces，不能提交；
                             * 同一基名的发现层组如果已经有完整候选，必须保留它作回退。
                             */
                            var zeroPieceSizeAnomalyBases = new HashSet<string>(
                                preparedGroups
                                    .Where(group => group.State == PreparedGroupState.SizeAnomaly
                                                    && group.Pieces.Count == 0)
                                    .Select(group => group.BaseName),
                                StringComparer.OrdinalIgnoreCase);

                            handoffGroups.RemoveAll(group =>
                                preparedBaseNames.Contains(group.BaseName)
                                && !zeroPieceSizeAnomalyBases.Contains(group.BaseName)
                                && group.PreparedGroup == null
                                && !group.ByteFitPaired);

                            var byteFitBaseNames = new HashSet<string>(
                                handoffGroups
                                    .Where(group => group.ByteFitPaired)
                                    .Select(group => group.BaseName),
                                StringComparer.OrdinalIgnoreCase);

                            List<VolumeCandidateDiscovery.VolumeCandidate> preparedCandidates =
                                VolumeCandidateDiscovery.AnalyzePrepared(preparedGroups);

                            foreach (VolumeCandidateDiscovery.VolumeCandidate group in preparedCandidates)
                            {
                                // 字节补齐配对已经给出可证完整的组 ⇒ 同一基名的 resolver Unknown 不再叠加。
                                if (byteFitBaseNames.Contains(group.BaseName))
                                {
                                    continue;
                                }

                                handoffGroups.Add(group);
                                AppendLog("INFO", $"[第二大步·prepared] {group.Describe()}");
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        state.Stopped = true;
                        state.StoppedPhase = "第二大步解析/试开";
                        break;
                    }
                    catch (Exception probeError)
                    {
                        AppendLog(
                            "WARN",
                            $"[第二大步·组分析] 跳过：{probeError.GetType().Name}：{probeError.Message}");
                    }

                    /*
                     * ===== 交界处消费端 · 第二段（B）：点名入口任务（只解一层，⛔ 不搬片）=====
                     *
                     * 可证完整的交接组 ⇒ 在任务表里找到入口那一份，清掉它上一轮「已跳过」的结论，
                     * 登记第二大步事实位（`_volumeStepTasks` ⇒ 只解一层），并把它排进下一轮。
                     * ⛔ 这一版不搬片、不改名、不动盘；片进不进入口层由 C 段再做。
                     */
                    List<ArchiveTask> volumeEntryTasks = roundState.VolumeEntryTasks;
                    var secondStepEntryCandidates = new List<InnerArchiveCandidate>();
                    var secondStepEntryGroups =
                        new List<(VolumeCandidateDiscovery.VolumeCandidate Group, ArchiveFixer.Extraction.VolumeGroupUnit Unit)>();

                    /*
                     * 标准名前置快通道的控制名单：只收“同目录全部标准名、且同基名没有
                     * 标准片散在别处”的组。Incomplete 不在这里，交回正常第二大步。
                     * ⛔ 只影响控制路径，不写内容卡事实。
                     */
                    var fastReadyEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var fastReadyFamilies = new Dictionary<string, VolumeNamingFamily?>(StringComparer.OrdinalIgnoreCase);

                    foreach (ArchiveFixer.Extraction.StandardNameFastPath.Decision decision
                             in ArchiveFixer.Extraction.StandardNameFastPath.Decide(
                                 _extractionCoordinator.FirstStepLedger
                                     .AllSecondStepCandidates()
                                     .Select(entry => entry.Path)))
                    {
                        if (decision.Kind == ArchiveFixer.Extraction.StandardNameFastPath.DecisionKind.Ready)
                        {
                            fastReadyEntries.Add(decision.Group.EntryPath);
                            fastReadyFamilies[decision.Group.EntryPath] =
                                ArchiveFixer.Extraction.StandardNameFastPath.ToNamingFamily(decision.Group.Family);
                            AppendLog(
                                "INFO",
                                $"[第二大步·标准名快通道] {decision.Group.BaseName}：{decision.Reason}"
                                + $"｜入口={FileNameHelper.GetFileName(decision.Group.EntryPath)}");
                        }
                        else
                        {
                            AppendLog(
                                "INFO",
                                $"[第二大步·标准名快通道] {decision.Group.BaseName}：{decision.Kind}（{decision.Reason}）⇒ 交回正常第二大步");
                        }
                    }

                    foreach (VolumeCandidateDiscovery.VolumeCandidate group in handoffGroups)
                    {
                        /*
                         * ⛔ 这里不再用候选 Verdict 当闸门（那只是交界处的“建议”）：
                         * 已接线的族由族算法判完整性；未接线的族仍要求候选 Complete（在下面兜住）。
                         */
                        if (string.IsNullOrWhiteSpace(group.EntryPath))
                        {
                            continue;
                        }

                        /*
                         * 族的**入口片**才是 owner。先按组事实建 Unit（owner 先空），
                         * 由族算法给出规范名计划里的 IsEntry 片（RAR=第 1 卷、跨盘 ZIP=末片 .zip、
                         * 通用分片/7z=第 1 片）；⛔ 不再“任务表里哪一片先出现就拿哪一片当 owner”
                         * （真机 6666：owner 被点成了第 2 片 `.=Z002`，引擎打不开）。
                         */
                        string entryLayer = Path.GetDirectoryName(group.EntryPath) ?? string.Empty;

                        if (entryLayer.Length == 0)
                        {
                            continue;
                        }

                        /*
                         * 入口包已经不在盘上，而同层同基名有一个成功任务（2222 批的三个「风景01」
                         * 都是这种：任务名是 .mp4，组入口是已经清掉的 .7z.001）⇒ 这一组已收口，
                         * 不再进 TryCommit（否则会先打一条 prepared 组建组被拒的 WARN）。
                         * 只在“入口确实不存在”时走，入口还在就不许猜。
                         */
                        string earlyEntryPath = group.PreparedGroup?.OriginalEntryPath
                            ?? group.OriginalEntryPath;

                        if (!string.IsNullOrWhiteSpace(earlyEntryPath)
                            && !File.Exists(earlyEntryPath)
                            && _batchFinalizer.FindSucceededTaskInLayer(entryLayer, group.BaseName) != null)
                        {
                            AppendLog(
                                "INFO",
                                $"[第二大步·接住] {group.BaseName}：入口包已不在盘上，但同层同基名任务已完成 ⇒ 已收口，不重复建组。");
                            continue;
                        }

                        bool standardNameFastPath = fastReadyEntries.Contains(group.EntryPath)
                            && ArchiveFixer.Extraction.StandardNameFastPath.Find(group.Pieces)
                                .Any(standard => standard.Pieces.Count == group.Pieces.Count);

                        VolumeNamingFamily? fastFamily = standardNameFastPath
                            && fastReadyFamilies.TryGetValue(group.EntryPath, out VolumeNamingFamily? familyFromName)
                                ? familyFromName
                                : null;

                        ArchiveFixer.Extraction.VolumeGroupUnit? preUnit =
                            _extractionCoordinator.TryCommitVolumeGroup(
                                group,
                                null,
                                entryLayer,
                                standardNameFastPath: standardNameFastPath,
                                standardFamily: fastFamily);

                        if (preUnit != null
                            && preUnit.State == ArchiveFixer.Extraction.VolumeGroupState.Missing)
                        {
                            /*
                             * 组事实层落最终组失败：不点名、不链接、不解压、不删源包。
                             * 缺片不是第一大步的交接跳过，而是第二大步最终组失败。
                             */
                            _extractionCoordinator.RecordFinalGroupFailure(
                                preUnit,
                                FinalGroupFailureKind.Incomplete,
                                $"最终内容分卷组不完整：{preUnit.Verdict}",
                                Tasks);

                            AppendLog(
                                "WARN",
                                $"[第二大步·组] {group.BaseName}：Missing ⇒ Failed / FinalGroupIncomplete（不链接、不解压，源包不动）。");
                            continue;
                        }

                        if (preUnit != null
                            && (preUnit.State == ArchiveFixer.Extraction.VolumeGroupState.Unknown
                                || preUnit.State == ArchiveFixer.Extraction.VolumeGroupState.SizeAnomaly))
                        {
                            if (preUnit.State == ArchiveFixer.Extraction.VolumeGroupState.SizeAnomaly
                                && preUnit.MissingNumbers.Count > 0)
                            {
                                _extractionCoordinator.RecordFinalGroupFailure(
                                    preUnit,
                                    FinalGroupFailureKind.Incomplete,
                                    $"最终内容分卷组结构异常且缺片：{preUnit.Verdict}",
                                    Tasks);
                            }

                            AppendLog(
                                "INFO",
                                $"[第二大步·组] {group.BaseName}：{preUnit.State} ⇒ 不点名、不链接（{preUnit.Verdict}）。");
                            continue;
                        }

                        // 引擎读临时标准名；任务表/落点/source 处理只认原始入口。
                        string entryPath = preUnit?.NamePlan?.Items.FirstOrDefault(item => item.IsEntry)?.PiecePath
                            ?? group.EntryPath;

                        string originalEntryPath = group.PreparedGroup?.OriginalEntryPath
                            ?? group.OriginalEntryPath;

                        if (string.IsNullOrWhiteSpace(originalEntryPath))
                        {
                            originalEntryPath = entryPath;
                        }

                        ArchiveTask? entryTask = null;

                        foreach (ArchiveTask candidate in Tasks)
                        {
                            if (candidate != null
                                && SafePathHelper.PathEquals(candidate.CurrentPath, originalEntryPath))
                            {
                                entryTask = candidate;
                                break;
                            }
                        }

                        if (entryTask == null)
                        {
                            /*
                             * 入口任务还没建（典型：这一组的三片是上一轮刚产出的，扫描只把它们当“分卷片”
                             * 记了交接事实，还没进任务表）。⇒ 用**组事实**把入口片按正常“加任务”那条路
                             * 补进表；下一轮由第二大步点名为 owner。
                             * ⛔ 不在这里解压、不搬片、不改名；⛔ owner 只补写一次。
                             */
                            if (preUnit != null
                                && (preUnit.State == ArchiveFixer.Extraction.VolumeGroupState.Complete
                                    || preUnit.State == ArchiveFixer.Extraction.VolumeGroupState.OwnerAssigned)
                                && File.Exists(originalEntryPath))
                            {
                                /*
                                 * ⛔ 父目录必须取**这一片自己所在的那一层**，不能取组分析入口那一层的目录：
                                 * 同一基名可能同时有“原包树那份”和“产出层那份”，组分析的入口可能是原包树那份，
                                 * 而这一片（如 `.0Bs401`/`111-L2.zip`）实际在产出层 —— 取错层就把这一单的
                                 * OutputPath 写进原包树（真机 8888：内容全部落到原包树、判据树里没有）。
                                 */
                                string entryTaskLayer =
                                    Path.GetDirectoryName(originalEntryPath) ?? entryLayer;

                                secondStepEntryCandidates.Add(new InnerArchiveCandidate
                                {
                                    Path = originalEntryPath,
                                    ParentOutputDirectory = entryTaskLayer,
                                    ParentTaskName = group.BaseName,
                                    RootSourcePath = originalEntryPath
                                });
                                secondStepEntryGroups.Add((group, preUnit));

                                AppendLog(
                                    "INFO",
                                    $"[第二大步·组] {group.BaseName}：入口任务还没建 ⇒ 用组事实补建任务"
                                    + "（下一轮点名，⛔ 不在边界解压）。");
                            }
                            else
                            {
                                /*
                                 * 同层、同基名已经有一个成功任务（真机 2222：三个「风景01」各自
                                 * 在不同层，入口是 `.7z.001`，而成功任务名是 `.mp4`）⇒ 这一组
                                 * 实际上已经由那个任务收口，不能再报"找不到入口"的 WARN。
                                 * 只认同一层 + 同基名 + 机器终态成功，禁跨层猜。
                                 */
                                ArchiveTask? settledSameBase = _batchFinalizer.FindSucceededTaskInLayer(entryLayer, group.BaseName);

                                if (settledSameBase != null)
                                {
                                    if (preUnit != null)
                                    {
                                        _extractionCoordinator.SettleExtractedVolumeGroup(preUnit);
                                    }

                                    AppendLog(
                                        "INFO",
                                        $"[第二大步·接住] {group.BaseName}：入口任务不在表里，但同层同基名任务"
                                        + $"「{settledSameBase.LogName}」已完成 ⇒ 视为已收口，不再点名。");
                                }
                                else
                                {
                                    AppendLog(
                                        "WARN",
                                        $"[第二大步·接住] {group.BaseName}：可证完整，但任务表里找不到入口那一份"
                                        + $"（{FileNameHelper.GetFileName(entryPath)}）⇒ 这一组不动。");
                                }
                            }

                            continue;
                        }

                        if (entryTask.Outcome == TaskOutcome.Succeeded || entryTask.IsOutputVerified)
                        {
                            /*
                             * 入口已经解成功过：这一组不会再点名，但它其余的片仍是本组的过程物
                             * ⇒ 按“已被消费”收掉（⛔ 用户原件不删；真机 8888 的 `.00!~2` 就留在这里）。
                             */
                            _extractionCoordinator.SettleExtractedVolumeGroup(preUnit);

                            AppendLog(
                                "INFO",
                                $"[第二大步·接住] {group.BaseName}：入口已经解成功过 ⇒ 不再点名（⛔ 不重跑）。");
                            continue;
                        }

                        /*
                         * ⛔ 上一轮已经拿到「失败 / 取消 / 部分完成」的终态 ⇒ **绝不重跑**：
                         * 重跑只会把终态重置成 Pending，一路撞轮数上限、最后连"失败"都没了
                         * （真机 V04：密码错 ⇒ 每次都被重新点名，7 轮后停在 Pending）。
                         * 片原样留着，结论由收片账那一行给。
                         *
                         * 例外（真机 21212121）：这一组的事实刚被**重算成新 Unit**（owner 还空着、
                         * 状态 Complete），说明上一轮的终态是“拿错片当入口”留下的 ⇒ 允许这一新版跑一次；
                         * 已经是 Failed/Linked/Succeeded 的 Unit 不走这里（⛔ 不形成重跑环）。
                         */
                        bool freshUnitAfterFactsChanged =
                            preUnit != null
                            && preUnit.State == ArchiveFixer.Extraction.VolumeGroupState.Complete
                            && string.IsNullOrWhiteSpace(preUnit.OwnerTaskFileName);

                        if (!freshUnitAfterFactsChanged
                            && (entryTask.Outcome == TaskOutcome.Failed
                                || entryTask.Outcome == TaskOutcome.Cancelled
                                || entryTask.Outcome == TaskOutcome.PartiallyCompleted))
                        {
                            AppendLog(
                                "INFO",
                                $"[第二大步·接住] {group.BaseName}：入口上一轮终态={entryTask.Outcome} ⇒ 不重跑，片原样留着。");
                            continue;
                        }

                        entryTask.Outcome = TaskOutcome.Pending;
                        entryTask.IsOutputVerified = false;
                        entryTask.EndTime = null;
                        entryTask.ErrorMessage = string.Empty;
                        entryTask.Status = StatusText.WaitingExtract;
                        entryTask.Operation = StatusText.OpWaiting;
                        entryTask.ProgressText = StatusText.ProgressWaiting;

                        /*
                         * ===== P1：提交成 Unit（族算法在这里回答完整性/顺序/规范名计划）=====
                         * ⛔ Missing/Unknown ⇒ 不点名、不链接、不解压（判不出就不动）。
                         * RAR 已接线；其它族未接线 ⇒ 保持 P0 行为（不判、不命名）。
                         */
                        ArchiveFixer.Extraction.VolumeGroupUnit? committedUnit =
                            await _extractionCoordinator.TryCommitVolumeGroupAsync(
                                group,
                                entryTask,
                                entryLayer,
                                standardNameFastPath,
                                standardFamily: fastFamily)
                                .ConfigureAwait(false);

                        if (committedUnit != null
                            && (committedUnit.State == ArchiveFixer.Extraction.VolumeGroupState.Missing
                                || committedUnit.State == ArchiveFixer.Extraction.VolumeGroupState.Unknown
                                || committedUnit.State == ArchiveFixer.Extraction.VolumeGroupState.SizeAnomaly))
                        {
                            AppendLog(
                                "INFO",
                                $"[第二大步·组] {group.BaseName}：{committedUnit.State} ⇒ 不点名、不链接（{committedUnit.Verdict}）。");
                            continue;
                        }

                        /*
                         * 最终结论只认 VolumeGroupService + 族算法；VolumeCandidateDiscovery 的 Verdict
                         * 只是候选建议，不能拿来给未建组的候选兜底放行。
                         */
                        if (committedUnit == null)
                        {
                            AppendLog(
                                "WARN",
                                $"[第二大步·组] {group.BaseName}：族算法没有建组"
                                + $"（候选建议={group.SuggestedVerdict}）⇒ 不点名、不链接。");
                            continue;
                        }

                        _extractionCoordinator.AttachContentGroupOwner(
                            group.PreparedGroup,
                            entryTask.FileName,
                            entryTask.CurrentPath);

                        _extractionCoordinator.RememberVolumeStepTaskForRound(entryTask);

                        IReadOnlyList<string> consumedPaths = group.PreparedGroup?.Pieces
                            .Select(piece => piece.OriginalPath)
                            .Where(path => !string.IsNullOrWhiteSpace(path))
                            .ToList()
                            ?? group.Pieces;

                        _extractionCoordinator.MarkVolumeHandoffConsumed(consumedPaths, "第二大步·点名");

                        /*
                         * ===== C 段：把片放进入口层，**一律只建硬链接** =====
                         * ⛔ 不搬、不复制、不删：用户原件与下一轮内层包都必须原地不动。
                         * ⛔ 规范卷名/改名是**第二大步自己的算法**，不在这里做（用户 2026-10-24 拍板：
                         * 交界处只负责把片送到入口层；"名字该怎么写"属第二大步）。
                         */
                        if (group.PreparedGroup is not null)
                        {
                            /*
                             * prepared 组的标准名已由边界 resolver 在临时入口层建好；
                             * 这里只把引擎入口指过去，⛔ 不再搬/链接一次、⛔ 不再造第二套名字。
                             */
                            entryTask.EngineEntryPathOverride = entryPath;

                            AppendLog(
                                "INFO",
                                $"[第二大步·标准名] {group.BaseName}：prepared 组入口 = "
                                + $"{FileNameHelper.GetFileName(entryPath)}（原始入口 {FileNameHelper.GetFileName(originalEntryPath)}；"
                                + "源文件未动）");

                            _extractionCoordinator.RememberVolumeStepGroupWork(
                                entryTask,
                                group.BaseName,
                                entryLayer,
                                group.PreparedGroup?.Pieces
                                    .Select(piece => piece.OriginalPath)
                                    .Where(path => !string.IsNullOrWhiteSpace(path))
                                    .ToList()
                                    ?? group.Pieces);
                        }
                        else if (entryLayer.Length > 0)
                        {
                            if (committedUnit?.NamePlan is { IsEmpty: false } namePlan)
                            {
                                /*
                                 * P1：族算法给了规范名计划 ⇒ 按计划建链接（只链接、不覆盖）。
                                 * ⛔ 不再按原文件名另建一套，避免同一组两套名字。
                                 */
                                ArchiveFixer.Extraction.VolumeGroupEntryLayer.PlanLinkResult linked =
                                    ArchiveFixer.Extraction.VolumeGroupEntryLayer.LinkByPlan(
                                        namePlan,
                                        entryLayer,
                                        (level, message) => AppendLog(level, $"[第二大步·搬进入口层] {message}"));

                                AppendLog("INFO", $"[第二大步·搬进入口层] {group.BaseName}：{linked.Describe()}（{namePlan.Evidence}）");
                                _extractionCoordinator.MarkVolumeGroupLinked(committedUnit, linked.LinkedNames);

                                DiagnosticLog.WriteGroup(
                                    _vm.Logger,
                                    "group.linked",
                                    group.BaseName,
                                    group.Format.ToString(),
                                    committedUnit.State.ToString(),
                                    committedUnit.Verdict,
                                    group.EntryPath,
                                    committedUnit.OrderedPieces,
                                    committedUnit.MissingNumbers,
                                    detail: namePlan.Evidence);

                                if (namePlan.Items.FirstOrDefault(item => item.IsEntry) is { } entryItem)
                                {
                                    entryTask.EngineEntryPathOverride =
                                        Path.Combine(entryLayer, entryItem.TargetName);
                                }
                            }
                            else
                            {
                                ArchiveFixer.Extraction.VolumeGroupEntryLayer.MoveResult moved =
                                    ArchiveFixer.Extraction.VolumeGroupEntryLayer.MovePiecesIntoEntryLayer(
                                        group.Pieces,
                                        entryLayer,
                                        _ => true,
                                        (level, message) => AppendLog(level, $"[第二大步·搬进入口层] {message}"),
                                        forceLinks: true);

                                AppendLog("INFO", $"[第二大步·搬进入口层] {group.BaseName}：{moved.Describe()}");
                            }

                            _extractionCoordinator.RememberVolumeStepGroupWork(
                                entryTask,
                                group.BaseName,
                                entryLayer,
                                group.PreparedGroup?.Pieces
                                    .Select(piece => piece.OriginalPath)
                                    .Where(path => !string.IsNullOrWhiteSpace(path))
                                    .ToList()
                                    ?? group.Pieces);
                        }

                        if (!volumeEntryTasks.Contains(entryTask))
                        {
                            volumeEntryTasks.Add(entryTask);
                        }

                        AppendLog(
                            "INFO",
                            $"[第二大步·接住] {group.BaseName}：可证完整（{group.Pieces.Count} 片，{group.Evidence}）"
                            + $"—— 入口={FileNameHelper.GetFileName(group.EntryPath)}，这一单只解一层"
                            + "（B 段：只点名、不搬片）。");
                    }

                    foreach (string line in scanLog)
                    {
                        AppendLog(line.Contains("[WARN]", StringComparison.Ordinal) ? "WARN" : "INFO", line);
                    }

                    /*
                     * **P3a 第二大步接住**：已整段停用（用户 2026-10-24）。
                     * 实现（`TryMarkHandoffGroupEntryTasks`、搬/链接片、记账）仍保留在
                     * `SecondStepResidue/ExtractionCoordinator.VolumeHandoffStep.cs`，重建第二大步时去那儿挑。
                     */
                    /*
                     * 第二大步的入口任务晚到（例如这一组的片是上一轮刚产出的）⇒ 走与续解同一条加任务路
                     * 补进表；下一轮由第二大步点名成 owner。⛔ 不绕过任务表自己造一条执行路径。
                     */
                    if (secondStepEntryCandidates.Count > 0)
                    {
                        try
                        {
                            List<ArchiveTask> createdSecondStepTasks =
                                await AddInnerTasksAsync(secondStepEntryCandidates);

                            foreach (ArchiveTask createdTask in createdSecondStepTasks)
                            {
                                if (!volumeEntryTasks.Contains(createdTask))
                                {
                                    volumeEntryTasks.Add(createdTask);
                                }
                            }

                            AppendLog(
                                "INFO",
                                $"[第二大步·组] 补建入口任务 {createdSecondStepTasks.Count} 个（下一轮点名）。");

                            /*
                             * 任务刚建出来就**当场点名并建好规范名链接**：
                             * 不能等下一轮边界再点名 —— 下一轮一开工就按原文件名解压，
                             * 7z 找不到 `.7z.001` 兄弟卷，直接把入口判成“无法识别”，
                             * 之后又因“上一轮 Failed”永不重跑（真机 15151515 现场）。
                             */
                            foreach ((VolumeCandidateDiscovery.VolumeCandidate group, ArchiveFixer.Extraction.VolumeGroupUnit unit)
                                     in secondStepEntryGroups)
                            {
                                ArchiveTask? createdTask = createdSecondStepTasks.FirstOrDefault(task =>
                                    SafePathHelper.PathEquals(
                                        task.CurrentPath,
                                        unit.NamePlan?.Items.FirstOrDefault(item => item.IsEntry)?.PiecePath
                                        ?? unit.EntryPath));

                                if (createdTask == null)
                                {
                                    continue;
                                }

                                string layer = Path.GetDirectoryName(
                                    unit.NamePlan?.Items.FirstOrDefault(item => item.IsEntry)?.PiecePath
                                    ?? unit.EntryPath) ?? string.Empty;

                                ArchiveFixer.Extraction.VolumeGroupUnit? ownedUnit =
                                    _extractionCoordinator.TryCommitVolumeGroup(group, createdTask, layer);

                                if (ownedUnit?.NamePlan is { IsEmpty: false } plan)
                                {
                                    ArchiveFixer.Extraction.VolumeGroupEntryLayer.PlanLinkResult linked =
                                        ArchiveFixer.Extraction.VolumeGroupEntryLayer.LinkByPlan(
                                            plan,
                                            layer,
                                            (level, message) => AppendLog(level, $"[第二大步·搬进入口层] {message}"));

                                    _extractionCoordinator.MarkVolumeGroupLinked(ownedUnit, linked.LinkedNames);
                                    AppendLog("INFO", $"[第二大步·搬进入口层] {group.BaseName}：{linked.Describe()}");

                                    DiagnosticLog.WriteGroup(
                                        _vm.Logger,
                                        "group.linked",
                                        group.BaseName,
                                        group.Format.ToString(),
                                        ownedUnit.State.ToString(),
                                        ownedUnit.Verdict,
                                        group.EntryPath,
                                        ownedUnit.OrderedPieces,
                                        ownedUnit.MissingNumbers,
                                        detail: plan.Evidence);

                                    if (plan.Items.FirstOrDefault(item => item.IsEntry) is { } entryItem)
                                    {
                                        createdTask.EngineEntryPathOverride =
                                            Path.Combine(layer, entryItem.TargetName);
                                    }
                                }

                                _extractionCoordinator.RememberVolumeStepTaskForRound(createdTask);
                                _extractionCoordinator.MarkVolumeHandoffConsumed(group.Pieces, "第二大步·点名（补建）");
                            }
                        }
                        catch (Exception ex)
                        {
                            AppendLog(
                                "ERROR",
                                $"一键处理：把第二大步入口任务补进列表失败 —— {ex.Message}（片在产物目录里，可以自己添加）");
                        }
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
}

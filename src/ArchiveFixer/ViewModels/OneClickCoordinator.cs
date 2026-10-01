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
    /// 轮数卡死在 <see cref="RoundLimit"/> 轮（默认 5，天花板 <see cref="MaxRoundsCeiling"/> = 不变量 8）。RecursionMode 的默认值不动。
    ///
    /// 刻意不做的事：
    /// - 不自动删源包（那是 M3 的独立开关，默认关闭，且要校验通过才删）；
    /// - 不改用户没勾选的任务（沿用既有"只处理选中项"的约定）——
    ///   续解只处理**本轮产物里新出现**的归档，产物目录里本来就有的包一个都不碰。
    /// </summary>
    internal sealed class OneClickCoordinator
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
        /// 这一批实际允许跑几轮 = ②页「最大嵌套层数」（默认 5），夹在 1~<see cref="MaxRoundsCeiling"/>。
        ///
        /// <para>为什么跟设置走：这一格的名字就叫"最大嵌套层数"，而一键处理是主流程 ——
        /// 两处各有一套上限只会让人以为"有一处没生效"（2026-09-24 把 3 与 10 统一成 10 就是这个原因，
        /// 2026-09-26 它跟着 10→5 一起下来，仍然是同一个数字）。</para>
        /// </summary>
        internal int RoundLimit => Math.Clamp(Settings?.MaxRecursionDepth ?? 5, 1, MaxRoundsCeiling);

        private readonly MainViewModel _vm;
        private readonly ScanCoordinator _scanCoordinator;
        private readonly RenameCoordinator _renameCoordinator;
        private readonly ExtractionCoordinator _extractionCoordinator;
        private readonly DialogService _dialogService;

        /// <summary>
        /// 内层包候选的"这真的是包吗"判据（用户 2026-09-24 要求加的下限；2026-09-25 第 33 条改成**只认内容**）。
        ///
        /// <para>走 <see cref="ArchiveDetectService"/> 的完整识别（文件头魔数 + 文件头认不出时的尾部
        /// "内嵌归档"检测）——与"扫描任务"那一步**同一个实现**，⛔ 不另造一套签名表，
        /// 也**不再**按后缀筛（真机上"名字被塞两个字就只解一层"就是那道筛子造成的）。</para>
        /// </summary>
        private readonly ArchiveDetectService _detectService = new();

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

        /// <summary>
        /// 内层包的落点 = **那个内层包自己所在的那一层目录**（第 35 条改正；归集开启时仍以归集目录为准）。
        ///
        /// <para><b>真机现场</b>（"添加文件夹 + 指定位置"，多个包共用同一个输出根）：外层包第一层的产物是
        /// 一个文件夹 <c>BBB\111\2222\</c>，里面躺着下一层的包 <c>222.ra删除r</c>。
        /// 老实现按**父任务的 OutputPath**（＝共用根 <c>BBB\111</c>）落下一层的产物，于是内容物与包名目录
        /// **平级**（<c>BBB\111\222</c> 与 <c>BBB\111\2222</c> 并排）。用户原话：
        /// "我觉得你应该是要将 `BBB\111\222` 文件夹放在 `BBB\111\2222` 这个里面"。</para>
        ///
        /// <para>判据用"内层包自己所在的目录"这条**事实**，不猜任何名字：下一层的内容物落在那个包**旁边**，
        /// 天然就是"一个源包 = 一个目录"。唯一要处理的是内层包**本身就在「其余物」里**的情况
        /// （过程物集中处）—— 那时要爬到「其余物」外面那一层，否则下一层的内容物会被放进其余物里、
        /// 跟着「彻底删除」一起没了。</para>
        ///
        /// <para>归集（<c>CollectResultsToDirectory</c>）是"把产物目录整个搬走"，那时旧路径已经不存在，
        /// 权威落点是 <see cref="ArchiveTask.CollectedPath"/>（既有行为，不变）。</para>
        /// </summary>
        /// <param name="parentTask">内层包所属的父任务。</param>
        /// <param name="innerPackagePath">内层包自己的路径（给 null 时退回"父任务的内容物层 / 输出目录"）。</param>
        /// <param name="ownLayerName">
        /// 给**这一层自己**再套的那一层文件夹名（2026-09-27 用户拍板的"首尾必留、中间看开关"）：
        /// 给了就在算出来的目录后面追加一层（<c>…\222</c> → <c>…\222\333</c>），
        /// 给 null / 空 = 不追加（这一层的产物直接落进父任务那一层）。
        ///
        /// <para>⛔ 名字**必须由调用方按 <see cref="ShouldAddContinuationLevelLayer"/> 算出来再传**：
        /// 这里只管"怎么拼、要不要防重复"，不管"该不该加"—— 判据只有一处（扫描那一侧），
        /// 免得同一件事在两个地方各判一遍、判出两个结果。</para>
        /// </param>
        internal static string ResolveContinuationOutputDirectory(
            ArchiveTask parentTask,
            string? innerPackagePath = null,
            string? ownLayerName = null)
        {
            string directory = ResolveContinuationBaseDirectory(parentTask, innerPackagePath);

            /*
             * ⚠ 这一层的位置**被内层包自己那个文件占着**时不能建（2026-09-27 真机测试逮到）：
             * Windows 同一条路径上不许既有文件又有目录。典型现场 = **名字被改坏、剥不出后缀**的包
             * （`user.jp删除g`、`222.ra删除r`）：它的包基名就等于文件名本身，于是
             * `<父层>\user.jp删除g` 既是那个文件、又要当这一层的目录 → 建目录必然失败，
             * 任务会落成"最终目录不存在且创建失败"。
             *
             * 处置：这一层**不另建**，内容落进父任务那一层里（内层包自己就躺在那里）——
             * 这是物理上唯一可行的选择，调用方会写一条日志说清为什么（见扫描那一侧）。
             */
            if (IsOwnLayerOccupiedByPackageFile(directory, ownLayerName, innerPackagePath))
            {
                return directory;
            }

            return AppendOwnLayer(directory, ownLayerName);
        }

        /// <summary>
        /// 这一层要建的位置是不是**被内层包自己那个文件占着**（同路径不许既有文件又有目录）。
        ///
        /// <para>只有"包基名 == 文件名"（名字被改坏、剥不出已知后缀那种）才会命中；
        /// 正常包（`333.7z` → 层名 `333`）永远不会命中。</para>
        /// </summary>
        internal static bool IsOwnLayerOccupiedByPackageFile(
            string directory,
            string? ownLayerName,
            string? innerPackagePath)
        {
            if (string.IsNullOrWhiteSpace(innerPackagePath) || string.IsNullOrWhiteSpace(ownLayerName))
            {
                return false;
            }

            string layered = AppendOwnLayer(directory, ownLayerName);

            if (string.Equals(layered, directory, StringComparison.OrdinalIgnoreCase))
            {
                // 没打算加层（名字为空 / 最后一段已经同名）——不关这一条的事。
                return false;
            }

            try
            {
                return File.Exists(layered) && !Directory.Exists(layered);
            }
            catch
            {
                // 路径形状怪异时按"没被占"处理：真建不出来会在任务级如实报出来。
                return false;
            }
        }

        /// <summary>
        /// 续解层"要不要给这一层套一个以包名命名的文件夹"（用户 2026-09-27：**首尾必留，中间看开关**）。
        ///
        /// <para>
        /// ⚠ 判据**只有一处**：<see cref="PackageLayerRules.ShouldAddInnerPackageLayer"/>。
        /// 这里只做转发（保留这个方法名是因为既有用例与文档都按它指认这一档规则），
        /// ⛔ 不许在这里再写第二份规则 —— 递归发布侧读的是同一个出口。
        /// </para>
        /// </summary>
        internal static bool ShouldAddContinuationLevelLayer(
            bool omitMiddleLayers,
            bool parentProducedContent,
            int siblingCount)
        {
            return PackageLayerRules.ShouldAddInnerPackageLayer(
                omitMiddleLayers,
                parentProducedContent,
                siblingCount);
        }

        /// <summary>
        /// 把"这一层自己的包名"追到目录后面（<c>…\222</c> → <c>…\222\333</c>）。
        ///
        /// <para>三条防重复/防空的判断，每条都对应一种真实形状：</para>
        /// <list type="bullet">
        /// <item><description>名字为空（取不出包名）→ 一个字都不加；</description></item>
        /// <item><description>目录最后一段**已经**叫这个名字（<c>…\333\333.rar</c> 那种"包在自名文件夹里"）
        /// → 不再加，否则会得到 <c>333\333</c> 这种重复层（用户明确抱怨过的形状）；</description></item>
        /// <item><description><b>过程物名不成层</b>（用户 2026-09-27 红线）：分卷组的基名（真机现场
        /// <c>59768866.001/.002</c> → <c>59768866</c>）**不是**用户认得出来的包名，
        /// 拿它当一层只会在成品里造出 <c>…\包名\59768866\真内容</c> 这种怪目录 →
        /// 这一档**不追加**，那一层的内容物归父任务自己那一层。</description></item>
        /// </list>
        /// </summary>
        internal static string AppendOwnLayer(string directory, string? ownLayerName)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(ownLayerName))
            {
                return directory;
            }

            string layer = FileNameHelper.SanitizeFileName(ownLayerName);

            if (layer.Length == 0)
            {
                return directory;
            }

            string trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (trimmed.Length == 0)
            {
                return directory;
            }

            if (string.Equals(Path.GetFileName(trimmed), layer, StringComparison.OrdinalIgnoreCase))
            {
                return directory;
            }

            return Path.Combine(trimmed, layer);
        }

        /// <summary>
        /// 这一层该叫什么（= 内层包**去掉假后缀的基名**：就地替换留下的那个文件夹名）；
        /// **分卷组返回空** —— 过程物名不成层。
        ///
        /// <para>
        /// ⚠ 判据**只有一处**：<see cref="PackageLayerRules.ResolveInPlaceLayerName"/>
        /// （递归发布侧留目录用的是同一个方法），这里只做转发。
        /// </para>
        /// </summary>
        internal static string ResolveContinuationLayerName(string? innerPackagePath)
        {
            return PackageLayerRules.ResolveInPlaceLayerName(innerPackagePath);
        }

        /// <summary>
        /// 内层包落点的**原有推导**（2026-09-25 第 35 条 + 第 43 条）：只算"内层包自己所在的那一层"，
        /// 不追加任何层 —— <see cref="ResolveContinuationOutputDirectory"/> 在外面套那一层。
        ///
        /// <para>⚠ <c>internal</c>（不是 private）只为一处：扫描那一侧要拿它判
        /// "这一层的位置是不是被内层包自己占着"，好在日志里说清为什么没建那一层。</para>
        /// </summary>
        internal static string ResolveContinuationBaseDirectory(ArchiveTask parentTask, string? innerPackagePath = null)
        {
            if (parentTask == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(parentTask.CollectedPath))
            {
                return parentTask.CollectedPath;
            }

            string fallback = string.IsNullOrWhiteSpace(parentTask.ContentDirectoryPath)
                ? parentTask.OutputPath
                : parentTask.ContentDirectoryPath;

            if (string.IsNullOrWhiteSpace(innerPackagePath))
            {
                return EnsureChainRootPackageLayer(parentTask, fallback);
            }

            string? directory = Path.GetDirectoryName(innerPackagePath);

            if (string.IsNullOrWhiteSpace(directory))
            {
                return EnsureChainRootPackageLayer(parentTask, fallback);
            }

            // 内层包在「其余物」（含旧名「过程物」）里 → 爬到那一层之外。
            string? outermostArtifact = null;
            string? cursor = directory;

            // 「其余物\<这一层>\…」里的 <这一层>：共用根那一档下它就是**外层包的包基名**（D-10 的布局）。
            string? artifactChild = null;
            string? childCursor = directory;

            while (!string.IsNullOrWhiteSpace(childCursor))
            {
                if (ProcessArtifactLayout.IsArtifactDirectoryName(Path.GetDirectoryName(childCursor)))
                {
                    artifactChild = Path.GetFileName(childCursor);
                }

                childCursor = Path.GetDirectoryName(childCursor);
            }

            while (!string.IsNullOrWhiteSpace(cursor))
            {
                if (ProcessArtifactLayout.IsArtifactDirectoryName(cursor))
                {
                    outermostArtifact = cursor;
                }

                cursor = Path.GetDirectoryName(cursor);
            }

            if (outermostArtifact != null)
            {
                directory = Path.GetDirectoryName(outermostArtifact);
            }

            if (string.IsNullOrWhiteSpace(directory))
            {
                return EnsureChainRootPackageLayer(parentTask, fallback, artifactChild);
            }

            /*
             * 不许越出根任务的输出范围（共用输出根模式下 root 就是那个根）：
             * 内层包如果被放在了范围之外（理论上不该发生），落点退回 fallback，绝不往外写。
             */
            string outputRoot = string.IsNullOrWhiteSpace(parentTask.OutputPath)
                ? parentTask.ContentDirectoryPath
                : parentTask.OutputPath;

            if (!string.IsNullOrWhiteSpace(outputRoot) &&
                !ArchivePathGuard.IsInsideRoot(outputRoot, directory, out _))
            {
                return EnsureChainRootPackageLayer(parentTask, fallback, artifactChild);
            }

            return EnsureChainRootPackageLayer(parentTask, directory, artifactChild);
        }

        /// <summary>
        /// **链根那一层包名目录必须存在**（用户 2026-09-25 第 43 条，真机：68 套图全平铺进共用根）。
        ///
        /// <para><b>现场</b>：用户"添加文件夹 + 指定位置"导入 68 个包，落点是**共用输出根**
        /// （`CCC\SJA佳爷…\`，最后一段是**导入文件夹名**）。每个外层包的清单里**只有内层包**
        /// （`00NN_auto.7z.001/.002/.003`）、**没有内容文件夹** —— 于是外层包这一轮什么都没定稿，
        /// 链根那一层"包名目录"从来没被建出来。续解任务按"内层包所在的那一层"算落点，
        /// 得到的就是那个**共用根**；再加上「每个包只留一层内容」把最后一层塌掉，
        /// 68 套图全倒进同一个目录（真机实测：4339 个文件平铺、几千个同名被自动改名成 `(1)…`）。</para>
        ///
        /// <para><b>该是什么样</b>：用户 2026-09-24 拍板的语义就是 `222\1111\内容物`
        /// （成品目录 \ **包名** \ 内容物）。所以这里补一层**链根任务的包基名**：
        /// `CCC\SJA佳爷…\055\…`——每个包一个目录，兄弟包互不干扰。</para>
        ///
        /// <para><b>判据只有两条事实</b>（⛔ 不猜、不看文案）：①父任务是**链根**（不是续解任务）；
        /// ②算出来的目录**最后一段不是它自己的包基名** —— 那说明这是"多个包共用的根"
        /// （包名那一层不在路径里），而不是"它自己的包名目录"。两条都成立才补。</para>
        ///
        /// <para>⚠ 第二条正是它**不会**动到第 35 条那两种情形的原因：内层包在内容文件夹里时
        /// （`…\111\2222\`），最后一段就是包名 `2222`；单个包 + 指定位置时目录本来就是
        /// `…\指定位置\包名\` —— 两种都不补，行为与从前逐字相同。</para>
        /// </summary>
        private static string EnsureChainRootPackageLayer(
            ArchiveTask parentTask,
            string directory,
            string? artifactChildLayer = null)
        {
            if (parentTask == null
                || parentTask.IsContinuationTask
                || string.IsNullOrWhiteSpace(directory))
            {
                return directory;
            }

            /*
             * ⚠ 只有"算出来的目录就是这个链根自己的输出根"时才补那一层（⛔ 不许用"最后一段名字不等于包名"
             * 之类的近似判据 —— 实测踩到：扫描把**兄弟包**内容目录里的内层包也算到这个父任务头上时，
             * 那个近似判据会把兄弟的那一层当成共用根，补出一层 `…\3333\2222` 的错目录，把第 35 条那条
             * "跟着内层包自己所在的目录走"给毁了）。
             */
            string ownRoot = string.IsNullOrWhiteSpace(parentTask.ContentDirectoryPath)
                ? parentTask.OutputPath
                : parentTask.ContentDirectoryPath;

            if (string.IsNullOrWhiteSpace(ownRoot) || !SafePathHelper.PathEquals(ownRoot, directory))
            {
                return directory;
            }

            /*
             * 层名优先取**内层包真正所在的那一层**（「其余物\055\…」里的 `055`）：
             * 共用根那一档下"其余物"按包基名分了子目录（ResultFinalizer 的 D-10），
             * 那个子目录名就是外层包的包基名，**比父任务的名字更可靠** ——
             * 实测：两个兄弟包都装着同名内层卷时，扫描会把另一个包的内层包也算到这个父任务头上
             * （`2222.7z：内层包移入其余物 —— …\3333\333.7z`），用父任务名就会把两个包的内容
             * 倒进同一个目录（真机上就是"混乱"）。
             */
            string packageLayer = string.IsNullOrWhiteSpace(artifactChildLayer)
                ? FileNameHelper.SanitizeFileName(FileNameHelper.GetArchiveBaseName(parentTask.CurrentPath))
                : FileNameHelper.SanitizeFileName(artifactChildLayer!);

            if (string.IsNullOrWhiteSpace(packageLayer))
            {
                return directory;
            }

            string trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(Path.GetFileName(trimmed), packageLayer, StringComparison.OrdinalIgnoreCase))
            {
                // 已经在自己那一层里（单个包 + 指定位置 / 内层包在内容文件夹里）→ 一个字都不动。
                return directory;
            }

            return Path.Combine(trimmed, packageLayer);
        }

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
        private void UpdateSummary() => _vm.UpdateSummary();

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
                        .BuildConfirmFactsAsync(targets, seed, reminders)
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
                         * 「把本次选择存为默认」是**唯一**允许写落点 / 终端落法 / 源包操作 / 删除操作
                         * 这四个设置的分支（硬要求②）。写设置这件事只发生在用户显式勾选之后 ——
                         * 没勾时这条路上没有写盘代码。
                         */
                        if (runOptions.SaveAsDefault)
                        {
                            _vm.SaveOneClickOptionsAsDefaults(runOptions);
                            AppendLog("INFO", $"本次选项已存为默认（写入设置）：{runOptions.Describe()}");
                        }
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
                 * 一键处理 = 按一下全搞定（用户 2026-09-28：「1.要」）：**无用物不要留在任务列表里**。
                 * ⛔ 只动列表，绝不删 / 移动文件；放在 EnterBusy 之前，列表先清干净、用户看得见。
                 * 出错也不影响这一批（只写一句 WARN）。
                 */
                try
                {
                    await _scanCoordinator.RemoveJunkTasksFromListAsync(targets);
                }
                catch (Exception ex)
                {
                    AppendLog("WARN", "一键处理：清理列表里的无用物失败（不影响这一批）：" + ex.Message);
                }

                EnterBusy();

                try
                {
                    // 新的一批开始：上一次留下的"还剩 N 个内层包"提示先清掉（跑完按本次结论重设）。
                    _vm.ReportPendingContinuation(0);

                    OneClickOutcome outcome = await RunPipelineAsync(targets, runOptions);

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
            /*
             * ⛔ 这一批只处理**还在任务列表里**的目标（2026-09-28 真机："只解一层就停"的真正成因）。
             *
             * 调用方拍下"这一批要处理谁"的清单之后，列表还可能变——一键处理开工前那一步就会把
             * 无用物从列表里移掉（ScanCoordinator.RemoveJunkTasksFromListAsync：只动列表，不动磁盘）。
             * 被移掉的那些**既不在列表里、也不会被解压循环碰到**（解压循环走的是列表），
             * 于是收尾守卫（下面那句 roundStartTargets.Any(t => !IsHandled(t))）会把它们读成
             * "这一轮有任务没轮到" → 一次正常的续解被误判成用户叫停：**只解一层就收工**，
             * 汇总还写着"已按「停止后续」中断"（用户根本没按过；真机症状就是这个）。
             *
             * ⚠ 判据是**引用**（那个任务对象还在不在列表里），不按路径比 ——
             * 同一个文件被重新导入会是一个新任务，路径一样但它不属于这一批。
             */
            var listedTasks = new HashSet<ArchiveTask>(Tasks);
            List<ArchiveTask> rootTargets = firstRoundTargets.Where(listedTasks.Contains).ToList();

            AppendLog("INFO", $"一键处理开始，共 {rootTargets.Count} 个任务。");

            // 处理过的任务按轮累加：汇总要算"本次一共处理了多少个"，只算第一轮会和实际不符。
            var processed = new List<ArchiveTask>(rootTargets);

            // 源文件（含分卷各卷）不算内层包：不能把用户最初给的那个 .mp4 / .7z.001 又加一遍。
            HashSet<string> sourcePaths = BuildSourcePathSet(rootTargets);

            List<ArchiveTask> roundTargets = rootTargets.ToList();

            int round = 0;
            int continuationLayers = 0;
            bool stopped = false;
            bool hitRoundLimit = false;

            // 撞到轮数上限时那几个"已经加进列表、这一批没解"的内层包（第 16 条追加：到顶要能一键继续）。
            var pendingContinuation = new List<ArchiveTask>();

            /*
             * 「用户按过停止后续」这件事必须单独记一笔。
             *
             * 只看 _vm.IsStopping 不行：ExtractionCoordinator 在批次开始和收尾时都会把它复位成 false，
             * 等我们拿回控制权时它已经是 false 了。只看"有没有任务没被处理"也不完全可靠：
             * 并发位被占着时批量循环还会再启动一个任务，未必留下未处理的任务。
             * 订阅属性变更拿到的是"用户确实按过"这个事实，不依赖上面两条时序。
             */
            bool stopRequested = false;

            void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(MainViewModel.IsStopping) && _vm.IsStopping)
                {
                    stopRequested = true;
                }
            }

            _vm.PropertyChanged += OnViewModelPropertyChanged;

            try
            {
                while (round < RoundLimit)
                {
                    round++;

                    // 第一步：识别（第 2 轮起只扫本轮新加进来的包）。
                    await ScanRoundAsync(round, roundTargets);

                    /*
                     * ⛔ 本轮"开跑时"的目标快照（2026-09-28 真机）。
                     *
                     * 收尾那个"有任务没轮到就停"的守卫**只看这份快照**：本轮解压途中会往列表里加
                     * **下一轮**才该解的内层包（AddInnerTasksAsync），拿实时清单去比，那些刚加进来、
                     * 本来就该下一轮跑的任务会被当成"这一轮没轮到" → 正常续解被误判成"用户叫停"，
                     * 于是**只解一层就收工**（真机现象；总结还会说"已按「停止后续」中断"，用户根本没按过）。
                     *
                     * ⚠ 2026-09-28 复检（补守门用例时当场量到的）：这份快照与 roundTargets 在同一个迭代里
                     * **内容逐项相同**（roundTargets 只在本轮末尾才被重新赋值，中途没有任何地方改它），
                     * 所以"改用快照"这一下本身不改变行为，真机上那个"只解一层就停"也不是它治好的 ——
                     * 真正的成因是"这一批的目标清单里混进了**已经从列表里移掉**的任务"（开工前清无用物那一步），
                     * 那个已经在 RunPipelineAsync 入口处过滤掉了（见那里的说明）。
                     * 保留这份快照的理由：它把"守卫只看本轮开跑时的目标"这条语义**写死**在这里，
                     * 以后谁要是把 roundTargets 改成实时清单（比如直接传 Tasks），守卫不会跟着一起错。
                     */
                    List<ArchiveTask> roundStartTargets = roundTargets.ToList();

                    /*
                     * 第二步：修正伪装后缀。只处理确实需要改的。
                     *
                     * **一键档不弹预览**（用户 2026-09-25 明确指示）："这个一键处理自己会自动改名自动解压，
                     * 为什么遇到这种改名的压缩包还要我两次确认……那种情况只有手动档才会有"。
                     * 每个改名照旧逐条写进日志与任务列表；手动档（②页/右键那几条命令）**照旧先出预览**。
                     */
                    int needRename = Tasks.Count(t => t.IsSelected && NeedsRename(t));

                    if (needRename > 0)
                    {
                        AppendLog("INFO", $"一键处理：{needRename} 个任务需要修正后缀，自动执行（不弹预览）。");
                        await _renameCoordinator.AutoFixExtensionsAsync();
                    }
                    else if (round == 1)
                    {
                        AppendLog("INFO", "一键处理：没有需要修正的后缀，跳过改名。");
                    }

                    /*
                     * 解压**之前**先记下候选目录里已有的文件。
                     *
                     * 续解只认"这一轮新出现的"归档起点，不能把目录里本来就有的压缩包当成产物：
                     * 输出目录可以就是源目录（设置里的"解压到原目录 + 不建包名文件夹"），
                     * 那样一来用户自己放在旁边的包会被当成内层包重新解一遍 —— 等于偷偷处理了没勾选的文件。
                     *
                     * 目录枚举（可能几万个文件）在后台线程上做：一键处理跑在 UI 线程上，
                     * 收尾阶段任何一次全目录遍历都会让窗口卡住（本项目最容易出卡死的一块）。
                     */
                    (HashSet<string> existingFiles, HashSet<string> unreadableDirectories) =
                        await SnapshotCandidateDirectoriesAsync(roundTargets);

                    // 第三步：解压（密码本、旁路说明文件、分卷守卫都在解压流程里生效）。
                    // 走**整理路径**的入口：定稿 + 校验通过 + 未取消之后按设置处理源包
                    // （默认移入其余物，决策 D-9/D-11/D-12）。手动「只解压」按钮走的
                    // StartExtractAsync 是地基路径，永远不动源包 —— 两者只在这一件事上不同。
                    AppendLog("INFO", round == 1 ? "一键处理：开始解压。" : $"一键处理：第 {round} 层开始解压。");
                    await _extractionCoordinator.StartExtractForOneClickAsync(runOptions);

                    UpdateSummary();

                    // 「停止后续」之后不许再续解：按下过停止，或者这一轮有任务根本没轮到。
                    // ⚠ 用 roundStartTargets（本轮开跑时的快照），不用实时 roundTargets ——
                    //    理由见上面那段注释：中途加进来的下一轮任务不该算"这一轮没轮到"。
                    if (stopRequested || roundStartTargets.Any(t => !IsHandled(t)))
                    {
                        stopped = true;
                        break;
                    }

                    /*
                     * 从本轮成功任务的产物里找内层包。找不到就结束，但**不许静默**：
                     * 扫描的每一条判据都会写进日志（认出谁 / 跳过谁 / 为什么），
                     * 于是"只解了一层"这类问题**看日志就能定位**（用户 2026-09-25 明确要求）。
                     */
                    (List<InnerArchiveCandidate> innerArchives, List<string> scanLog) =
                        await CollectInnerLayersAsync(round, roundTargets, existingFiles, unreadableDirectories, sourcePaths);

                    foreach (string line in scanLog)
                    {
                        AppendLog(line.Contains("[WARN]", StringComparison.Ordinal) ? "WARN" : "INFO", line);
                    }

                    if (innerArchives.Count == 0)
                    {
                        /*
                         * 这一句是必须的，不是噪音。
                         *
                         * 以前这里一声不吭地 break：用户完全分不清"本来就没有内层包"和"续解功能坏了、
                         * 或者内层包落在了没被看到的目录里"。真实踩坑就是后者 —— 输出目录已存在时
                         * 解压实际落到 xxx(1)，而续解只按解压**前**的目录快照找包，
                         * 结果第二层根本没跑，汇总却照样打印"一键处理完成：成功 N"。
                         * 有这一行日志（以及上面那串逐条明细），用户（和我们）才有能对照的线索。
                         */
                        AppendLog("INFO", $"一键处理：第 {round} 层没有发现可继续解压的内层包，到此结束。");
                        break;
                    }

                    if (round >= RoundLimit)
                    {
                        hitRoundLimit = true;

                        /*
                         * 用户 2026-09-24 第 16 条追加：到顶**不能静默停下**。
                         *
                         * 把这一层发现的内层包**照样加进任务列表并勾好**（走与正常续解同一条路），
                         * 只是这一批不再解它们：于是用户看到的列表里就摆着"还没解的那些包"，
                         * 汇总与弹窗写明还剩几个，界面给「继续解」接着跑下一批 10 轮。
                         *
                         * 为什么不直接继续解完：硬上限就是不变量 8 的那道闸（见 RoundLimit / MaxRoundsCeiling 的说明）。
                         */
                        try
                        {
                            pendingContinuation = await AddInnerTasksAsync(innerArchives);
                        }
                        catch (Exception ex)
                        {
                            AppendLog("ERROR", $"一键处理：把剩下的内层包加进任务列表失败 —— {ex.Message}（它们在产物目录里，可以自己添加）");
                        }

                        AppendLog(
                            "WARN",
                            $"一键处理：第 {round + 1} 层还有 {innerArchives.Count} 个内层包，"
                            + $"但已达到 {RoundLimit} 轮上限，本批先停在这里 —— "
                            + $"{pendingContinuation.Count} 个内层包已加进列表并勾好，点「继续解」接着解（每次最多再解 {RoundLimit} 轮）。");

                        break;
                    }

                    List<ArchiveTask> nextRound;

                    try
                    {
                        /*
                         * 已经处理过的任务全部取消勾选。
                         * 不这样做，下一轮会把它们**重新解压一遍**，产出 "(1)" 这样的垃圾副本 ——
                         * 解压流程只认勾选状态，这正是它该有的样子（不偷偷处理没勾的）。
                         *
                         * 必须走批量守卫（用户 2026-09-24 第 12 条"卡死"的修法之一）：逐项改勾选
                         * 会让**每一项**都触发一次全表汇总重算 + 38 条命令重查，
                         * 几百项的任务列表在这一步就是几百次 O(N) 扫描（O(N²)）。
                         */
                        _vm.RunBulkSelectionUpdate(() =>
                        {
                            foreach (ArchiveTask task in processed)
                            {
                                task.IsSelected = false;
                            }
                        });

                        nextRound = await AddInnerTasksAsync(innerArchives);
                    }
                    catch (Exception ex)
                    {
                        AppendLog("ERROR", $"一键处理：把内层包加进任务列表失败，停止续解 —— {ex.Message}");
                        break;
                    }

                    if (nextRound.Count == 0)
                    {
                        // 内层包全都已经在任务列表里了（路径重复）：再跑一轮只会空转。
                        AppendLog("WARN", $"一键处理：第 {round + 1} 层的 {innerArchives.Count} 个内层包没能加进任务列表，停止续解。");
                        break;
                    }

                    continuationLayers++;

                    /*
                     * 说清"第二层解到哪去"：内层包**不另建目录**，
                     * 它的产物与父任务归到同一个最终目录（用户诉求：一个源包 = 一个目录）。
                     */
                    string continuationTarget = innerArchives
                        .Select(candidate => candidate.ParentOutputDirectory)
                        .FirstOrDefault(directory => !string.IsNullOrWhiteSpace(directory)) ?? string.Empty;

                    AppendLog(
                        "INFO",
                        $"一键处理：第 {round + 1} 层发现 {innerArchives.Count} 个内层包，继续解" +
                        (string.IsNullOrWhiteSpace(continuationTarget)
                            ? "。"
                            : $"（产物归入同一个输出目录：{continuationTarget}，不再另建文件夹）。"));

                    processed.AddRange(nextRound);
                    sourcePaths.UnionWith(BuildSourcePathSet(nextRound));
                    roundTargets = nextRound;
                }
            }
            finally
            {
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
            }

            /*
             * 链结束后的**源包补搬**（2026-09-22 修掉的真实缺陷）。
             *
             * 用户的真实文件（222.mp4 = 假 MP4 头 + 尾部 ZIP + 内层加密分卷）第一层只出内层分卷，
             * 一个内容物都没有 —— 最外层那一轮因此没法按"内容物已定稿"把源包搬进其余物，
             * 而真正产出内容物的是续解子任务，子任务按设计跳过源包处理。
             * 于是最外层那一轮把源包记账成"留到链结束后补搬"（SourcePackageMoveState.DeferredToChainEnd），
             * 由这里 —— **只有这里知道链什么时候结束** —— 补做一次。
             *
             * 链没跑完时一律不补搬（源包留在原地）：
             * · 被「停止后续」打断 / 有任务没轮到：这是取消语义，不该再动用户的源文件；
             * · 撞到轮数上限：更深的包还没解，内容物可能不全，不能在此时把源包搬走。
             * 两种情况都写 WARN 说明原因，绝不静默。
             */
            await CompleteRootSourcePackagesAsync(
                rootTargets,
                processed,
                stopped || stopRequested,
                hitRoundLimit,
                runOptions);

            string summary = BuildSummaryLine(processed, stopped, stopRequested, continuationLayers, hitRoundLimit, pendingContinuation.Count);

            AppendLog("INFO", summary);

            /*
             * 批末的**一份**结论（用户 2026-09-30 第 3 条："严重度管颜色、诊断管文字，
             * 两者都从同一份任务终态算出来，⛔ 不许各算一遍"）：
             * 颜色（Severity）与"出错在哪"那份文字清单（Text）都从**这一个**对象里取 ——
             * 下面给 OneClickOutcome 的两项就是它的两个字段，不存在第二份快照。
             *
             * 判据与排版全在 Models\BatchSummaryDiagnostics.cs（唯一出口），
             * ⛔ 这里与窗口里都不判断"哪一组、算不算失败"。
             */
            BatchSummaryReport report = BatchSummaryDiagnosticsRules.Build(processed);

            return new OneClickOutcome
            {
                Rounds = round,
                ContinuationLayers = continuationLayers,
                Stopped = stopped,
                HitRoundLimit = hitRoundLimit,
                PendingContinuationCount = pendingContinuation.Count,
                Summary = summary,
                Diagnostics = report.Text,
                DiagnosticLines = report.Lines,

                /*
                 * 严重度从**这一批真正处理过的全部任务**算（根任务 + 所有续解子任务，
                 * 就是上面那个 processed 清单）：判据是机器终态，⛔ 不比中文文案。
                 * 空批（processed 为空）会得到 Success —— "什么都没发生"用蓝色最诚实。
                 */
                Severity = report.Severity
            };
        }

        /// <summary>
        /// 续解链跑完之后，对"本轮没有内容物、记账成待补搬"的**最外层源包**再给一次机会
        /// （2026-09-22 修复的真实缺陷，见调用点的说明）。
        ///
        /// <para>
        /// 三条不补搬的情况（都写日志，不静默）：
        /// ① 链没跑完（停止后续 / 撞轮数上限）→ 取消语义，源包留在原地；
        /// ② 没有一个源包需要补搬 → 什么都不做（连日志都不写，免得每批都刷一行废话）；
        /// ③ 具体的判据不成立（根任务没成功 / 校验没过 / 分卷不完整 / 那个目录里没有内容物）→
        ///    由 <see cref="ExtractionCoordinator.CompleteRootSourcePackagesAfterChainAsync"/> 逐条说清原因。
        /// </para>
        /// </summary>
        private async Task CompleteRootSourcePackagesAsync(
            IReadOnlyList<ArchiveTask> rootTasks,
            IReadOnlyList<ArchiveTask> chainTasks,
            bool stopped,
            bool hitRoundLimit,
            OneClickRunOptions? runOptions = null)
        {
            int pending = rootTasks.Count(task => task.SourcePackageMove == SourcePackageMoveState.DeferredToChainEnd);

            if (stopped)
            {
                /*
                 * ⚠ 2026-09-30：**即使 pending == 0 也要写一句**。
                 * 链没跑完 ⇒ 链尾那一档整段不执行（"按「删除操作」处理其余物"也在里面），
                 * 于是选了「彻底删除」的人会发现过程物还留着 —— 而老写法只在 pending > 0 时才吭声，
                 * pending == 0 时**一个字都不写**，用户只能猜（与 ApplyRestHandlingAfterChainAsync
                 * 那处静默 return 是同一类毛病）。
                 */
                AppendLog(
                    "WARN",
                    $"一键处理：被「停止后续」中断，链尾那一档（补搬源包 / 按「删除操作」处理其余物）整段没有执行 —— "
                    + $"源包与其余物都留在原地（{pending} 个源包本来要补搬）。");
                return;
            }

            if (hitRoundLimit)
            {
                AppendLog(
                    "WARN",
                    $"一键处理：还有更深的包没解（已达到 {RoundLimit} 轮上限），链尾那一档整段没有执行 —— "
                    + $"源包与其余物都留在原地（{pending} 个源包本来要补搬）；内容物可能还不全，此时不动源包。");
                return;
            }

            if (pending > 0)
            {
                AppendLog(
                    "INFO",
                    $"一键处理：续解链已结束，对 {pending} 个「本轮没有内容物」的最外层源包做一次补搬（按源包处理档位）。");
            }

            /*
             * ⚠ **即使 pending == 0 也必须调下去**（2026-09-25 第 33 条被测试逮到的那一处）：
             * 链尾这一步不只做"源包补搬"，还负责**按「删除操作」那一档处理已经搬好的其余物**。
             * 形状 A（第一层直接出内容物）下源包在第 1 轮就当场搬进其余物了，
             * 到这里 `SourcePackageMove` 已经是 <c>Done</c> → 老的 `if (pending == 0) return;`
             * 让整条链尾钩子**一次都不跑** → 用户选了「彻底删除」，跑完其余物还在
             * （正是他真机报的那个现象；形状 B 的包因为 pending > 0 反而照常生效，
             * 所以这个缺陷只在"第一层就出内容物"的包上露出来）。
             */
            await _extractionCoordinator.CompleteRootSourcePackagesAfterChainAsync(
                rootTasks,
                chainTasks,
                cancellationToken: default,
                runOptions);
        }

        /// <summary>
        /// 一轮的"识别"步骤。
        /// 第 1 轮沿用原来的整体扫描；第 2 轮起**只扫本轮新加进来的包**。
        /// </summary>
        private async Task ScanRoundAsync(int round, IReadOnlyList<ArchiveTask> roundTargets)
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
        private async Task<List<ArchiveTask>> AddInnerTasksAsync(IReadOnlyList<InnerArchiveCandidate> candidates)
        {
            int before = Tasks.Count;

            /*
             * ⚠ 这里必须是**追加**（ImportMode.Append）。
             *
             * 「添加文件 / 添加文件夹」的默认语义是"清空整张表再加"（用户 2026-09-24 第 12 条），
             * 而这一步是解压途中往列表里补内层包 —— 用替换语义就会把用户的任务表
             * （连同刚刚解完的那些结果行）整张清掉。AddPathsAsync 刻意**不给默认值**，
             * 就是为了让这种调用点必须把意图写出来。
             */
            await _scanCoordinator.AddPathsAsync(
                candidates.Select(c => c.Path).ToList(),
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

                    ApplyVolumeGroupingFromDirectory(Tasks[i]);

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
        /// 把一个任务按**它所在目录里的真实文件**归组（分卷组挂上 VolumePaths / IsVolumeGroup）。
        ///
        /// <para>为什么要单独做（2026-09-28 真机最后一环）：续解层的内层任务是走
        /// AddPathsAsync(..., suppressAutoScan: true) 加进来的，那条路**不跑扫描期的分卷归组**，
        /// 于是内层分卷组的任务 IsVolumeGroup = false —— 链尾"把内层包收进其余物"的安全闸门
        /// （"名字像分卷、同目录还有同组的卷，却没有整组清单 → 宁可不搬"）就把它挡住了，
        /// 用户看到的就是 amb.7z.001..004 一直赖在成品目录里。</para>
        ///
        /// <para>判据只此一份：VolumeGroupDetector.Group + VolumeGroupingService.ApplyGroupInfo
        /// （与扫描期、与手动档完全同一条路）。归不上组时什么都不动（行为与以前一样，不会更糟）。</para>
        /// </summary>
        internal static void ApplyVolumeGroupingFromDirectory(ArchiveTask? task)
        {
            if (task == null)
            {
                return;
            }

            ArchiveFixer.Detection.VolumeGroup? group = ResolveVolumeGroupFromDirectory(task);

            if (group != null)
            {
                new ArchiveFixer.Services.VolumeGroupingService().ApplyGroupInfo(task, group);
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
        /// 只对"还没识别"的任务重扫：
        /// 一律重扫会把已经识别好的、甚至已经解压完的任务重新洗一遍，白费时间还冲掉结果。
        /// </summary>
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
        private static bool NeedsRename(ArchiveTask task)
        {
            /*
             * 内嵌归档（文件尾部藏着 ZIP 的双面文件）**不改名**。
             *
             * 它看起来最像"该改名"的那一类（`xxx.mp4` 里明明有 ZIP），但改名是纯粹的误导：
             * ZIP 的内部偏移相对它自己，而前置数据远超 7-Zip 的容忍上限（实测 8 MiB），
             * 所以 `xxx.zip` 交到 7z 手里仍然打不开。
             * 该做的事是解压管线里按偏移把尾部那段取出来，不是动后缀。
             */
            if (task.ExtensionStatus == StatusText.ExtensionEmbedded)
            {
                return false;
            }

            return task.ExtensionStatus == StatusText.ExtensionMissing ||
                task.ExtensionStatus == StatusText.ExtensionMismatch ||
                task.ExtensionStatus == StatusText.ExtensionMultiFake;
        }

        /// <summary>源文件路径集合（当前路径 + 最初路径 + 该分卷组的每一卷）。</summary>
        private static HashSet<string> BuildSourcePathSet(IEnumerable<ArchiveTask> tasks)
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
        private async Task<(HashSet<string> ExistingFiles, HashSet<string> UnreadableDirectories)> SnapshotCandidateDirectoriesAsync(
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
                directories.AddRange(CandidateDirectories(task));
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
        /// 老实现的第一道门是后缀白名单（<see cref="IsArchiveStartPoint"/>）。真机现场：
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
        private async Task<(List<InnerArchiveCandidate> Candidates, List<string> LogLines)> CollectInnerLayersAsync(
            int round,
            IReadOnlyList<ArchiveTask> roundTargets,
            HashSet<string> existingFiles,
            HashSet<string> unreadableDirectories,
            HashSet<string> sourcePaths)
        {
            // 已经在任务列表里的文件不重复加（去重）。
            var knownTaskPaths = new HashSet<string>(
                Tasks.Select(t => t.CurrentPath).Where(p => !string.IsNullOrWhiteSpace(p)),
                StringComparer.OrdinalIgnoreCase);

            /*
             * task.OutputPath / task.CollectedPath 是解压管线回写的**实际落点**（唯一权威来源），
             * 不是"打算输出到哪"。所以这里无条件按它去找内层包：
             * 目录已经存在时它可能是自动改名后的 xxx(1)，绝不是快照里那个原定目录。
             * 是不是"这一轮新产物"由下面的 existingFiles 继续把关 ——
             * 解压前就有的文件会被过滤掉，不会把用户自己放在旁边的包重新解一遍。
             */
            var parents = new List<(ArchiveTask Task, string OutputDirectory)>();
            var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ArchiveTask task in roundTargets)
            {
                /*
                 * 只有**产物校验通过**的任务才有"产物"可言（用户 2026-09-24 要求：判据只准看事实）。
                 *
                 * 老写法是 `IsSuccessStatus(task)`，也就是拿 `task.Status == "解压成功"` 这个**字符串**去决定
                 * "要不要继续往下解"。真机日志里恰好出现过"状态写着解压成功、校验却已判否"的那一刻 ——
                 * 那种情况下续解链会在两个 0 字节的桩文件上继续解两层，全程在解空气。
                 * 现在读的是校验那一刻的事实；失败/取消的任务在这里天然被排除（它们的结论不是 Passed）。
                 */
                if (task.OutputVerification != OutputVerificationOutcome.Passed)
                {
                    continue;
                }

                foreach (string directory in CandidateDirectories(task))
                {
                    /*
                     * ⚠ 同一个目录只扫一次（2026-09-25 第 35 条修的真机日志噪声）：
                     * 共用输出根那一档下 4 个任务指向同一个目录，逐个任务扫就把同一批文件数了 4 遍 ——
                     * 日志里出现"新文件 16 个"（实际 4 个）和同一条"认出…"重复 4 行，纯噪声。
                     */
                    if (!string.IsNullOrWhiteSpace(directory) &&
                        !unreadableDirectories.Contains(directory) &&
                        seenDirectories.Add(directory))
                    {
                        parents.Add((task, directory));
                    }
                }
            }

            if (parents.Count == 0)
            {
                return (new List<InnerArchiveCandidate>(), new List<string>());
            }

            List<(string OutputDirectory, List<string> Files)> scanned = await Task.Run(() =>
            {
                var results = new List<(string, List<string>)>();

                // 扫描发生在后台线程上，先把当前生效的工作区根读出来（读的是批首定好的那一个）。
                string workRoot = _vm.CurrentWorkspaceRoot;

                foreach ((ArchiveTask _, string outputDirectory) in parents)
                {
                    if (!Directory.Exists(outputDirectory))
                    {
                        continue;
                    }

                    try
                    {
                        /*
                         * ⛔ 第 N 层产物扫描**必须排除工作区自己那棵树**（用户 2026-09-30）：
                         * 工作区默认就建在目标目录里面（<目标目录>\.ArchiveFixer.work），
                         * 里面躺着抠出来的内嵌归档副本、逐层的中间包 —— 它们**全都能被识别成归档**。
                         * 不排除的话，程序会把工作区里的中间产物当成"用户的内层包"继续解，
                         * 解完再把它们当内容物搬出去：不可逆的事故。
                         */
                        results.Add((
                            outputDirectory,
                            WorkspaceTree.EnumerateFiles(outputDirectory, workRoot).ToList()));
                    }
                    catch
                    {
                        // 读不了这个目录：跳过（快照阶段已经写过一条 WARN，不重复打扰用户）。
                    }
                }

                return results;
            });

            var found = new Dictionary<string, InnerArchiveCandidate>(StringComparer.OrdinalIgnoreCase);

            // 本轮按父任务登记的候选（父任务、父任务名、内层包路径）—— 落点要等扫完一整轮才能定。
            var candidates = new List<(ArchiveTask Parent, string ParentName, string File)>();

            var scanLog = new List<string>();
            var recognizedLines = new List<string>();
            var skippedLines = new List<string>();
            int newFileCount = 0;

            foreach ((ArchiveTask task, string outputDirectory) in parents)
            {
                List<string>? files = scanned
                    .Where(entry => string.Equals(entry.OutputDirectory, outputDirectory, StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.Files)
                    .FirstOrDefault();

                if (files == null)
                {
                    continue;
                }

                string parentName = string.IsNullOrWhiteSpace(task.FileName)
                    ? Path.GetFileName(task.CurrentPath)
                    : task.FileName;

                foreach (string file in files)
                {
                    // 只认"这一轮新出现"的：解压前就在那里的文件不是这一轮的产物。
                    if (existingFiles.Contains(file))
                    {
                        continue;
                    }

                    newFileCount++;

                    // 源文件本身（含分卷各卷）不算内层包。
                    if (sourcePaths.Contains(file))
                    {
                        skippedLines.Add($"{file}（是本批的源包）");
                        continue;
                    }

                    if (knownTaskPaths.Contains(file))
                    {
                        skippedLines.Add($"{file}（已经在任务列表里）");
                        continue;
                    }

                    /*
                     * ===== 内层包候选的**下限**（用户 2026-09-24 铁证）=====
                     *
                     * 现场：外层 ZIP 是加密条目 → 7z 半成功写出 **0 字节**的 `2部轻熟1.7z.001/.002` →
                     * 老逻辑只看后缀，把那个 0 字节的 `.001` 当成"包"继续往下解，一路解到 `2部轻熟1.7z`
                     * 才被 7-Zip 用"文件为空"打回来 —— 用户看到的是连解两层空气。
                     * 用户明确要求：**0 字节的文件绝不许进入续解链**。
                     */
                    if (!TryGetCandidateLength(file, out long candidateLength) || candidateLength <= 0)
                    {
                        skippedLines.Add($"{file}（0 字节或读不到，不可能是包）");
                        continue;
                    }

                    /*
                     * 唯一还按名字排除的一类：分卷的**后续卷**（.002 / .z02 / .r01 / .part2+）。
                     * 理由是"哪一卷是组的开头"只能从名字看出来 —— 这是**分组**信息，不是格式判断；
                     * 格式一律只看内容（下一段）。⛔ 别在这里再加"后缀得是已知归档后缀"那种条件：
                     * 那正是 2026-09-25 真机上"名字被塞了两个字就只解一层"的根因。
                     */
                    if (IsVolumeContinuationPart(file))
                    {
                        skippedLines.Add($"{file}（是分卷的后续卷，起点在第一卷上）");
                        continue;
                    }

                    (bool isArchive, string how) = await ClassifyArchiveByContentAsync(file);

                    if (!isArchive)
                    {
                        skippedLines.Add($"{file}（{how}）");
                        continue;
                    }

                    recognizedLines.Add($"{file}（{TaskSpaceEstimate.FormatSize(candidateLength)}，{how}）");

                    if (!found.ContainsKey(file))
                    {
                        /*
                         * ⚠ 落点**按每个内层包自己所在的目录**算（第 35 条），不能用"每个父任务一个" ——
                         * 共用输出根那一档下，同一个父任务的两个内层包分别躺在
                         * `…\111\2222\` 与 `…\111\3333\`，各自的内容物必须回到各自那一层旁边。
                         *
                         * 先只记下来，"要不要给这一层再套一层包名目录"**必须等这一轮扫完**才能定：
                         * 判据里有"同一个父任务认出了几个内层包"（分支 or 单链）这条事实。
                         */
                        candidates.Add((task, parentName, file));
                    }
                }
            }

            /*
             * ===== 续解层那一层目录（用户 2026-09-27：首尾必留，中间看开关）=====
             *
             * 判据全部在 <see cref="ShouldAddContinuationLevelLayer"/> 里（唯一实现），这里只做四件事：
             * ① 按父任务分组（"这一轮认出几个内层包"是按父任务数的）；
             * ② 要建层时取**这一层内层包的包基名**（分卷组取不出来 → 过程物名不成层，见 AppendOwnLayer）；
             * ③ 简洁档因为**分支**而没能省掉那一层时，写一条日志说清是哪种分支（⛔ 绝不静默）——
             *    "父任务自己出了内容物"与"这一层认出多个内层包"两种都要说；
             * ④ 层名被内层包自己那个文件占着时不建层，同样写日志（见下面那一段）。
             */
            foreach (IGrouping<ArchiveTask, (ArchiveTask Parent, string ParentName, string File)> group in
                     candidates.GroupBy(candidate => candidate.Parent))
            {
                ArchiveTask parent = group.Key;
                List<(ArchiveTask Parent, string ParentName, string File)> children = group.ToList();

                // 父任务自己产出了内容物 = 这一层是"内容 + 内层包"的分支，不是干净的单链中间层。
                // ⚠ 判据是**搬出去的内容物文件数**（唯一权威来源 = 定稿计划），不是
                // `ContentDirectoryPath` —— 后者对"只出过程物"的层也非空（见 ArchiveTask.ContentFileCount）。
                bool parentProducedContent = parent.ContentFileCount > 0;

                bool addLayer = ShouldAddContinuationLevelLayer(
                    Settings.OmitMiddleContinuationLayers,
                    parentProducedContent,
                    children.Count);

                if (parentProducedContent && addLayer && children.Count > 1)
                {
                    scanLog.Add(
                        $"{parent.FileName}：这一层自己产出了内容物、又认出了 {children.Count} 个内层包 ——"
                        + "它们各自占一层（否则几个包的内容物会倒进同一层）。");
                }
                else if (parentProducedContent && !addLayer)
                {
                    /*
                     * 用户 2026-09-27 真机：父层已经出了内容物 → 内层包解出来的东西**并进父层那一个目录**，
                     * 与父层自己的内容物并排，不再用内层包名另造一层（那会把真内容埋深一层）。
                     */
                    scanLog.Add(
                        $"{parent.FileName}：这一层自己产出了内容物 —— 内层包解出来的东西并进这一层"
                        + "（不再另建以包名命名的目录，免得真内容被埋到上一层文件下面）。");
                }
                else if (Settings.OmitMiddleContinuationLayers && addLayer)
                {
                    // 简洁档下没省成（这种形状只剩"同一层认出多个内层包"）：说清为什么，⛔ 不许静默。
                    scanLog.Add(
                        $"{parent.FileName}：这一层认出了 {children.Count} 个内层包（不是单链）——"
                        + "「省略中间层」只在单链时生效，这一层照旧各占一层。");
                }

                foreach ((ArchiveTask _, string parentName, string file) in children)
                {
                    string layerName = addLayer ? ResolveContinuationLayerName(file) : string.Empty;

                    /*
                     * 层名**被内层包自己那个文件占着**（名字被改坏、剥不出后缀那种）→ 那一层建不出来，
                     * 内容落进父任务那一层。这是用户看得见的落点差别，必须写日志说清（⛔ 绝不静默）。
                     */
                    if (layerName.Length > 0
                        && IsOwnLayerOccupiedByPackageFile(ResolveContinuationBaseDirectory(parent, file), layerName, file))
                    {
                        scanLog.Add(
                            $"{file}：这一层要建的位置被「内层包自己」占着（名字被改坏、剥不出后缀）——"
                            + "这一层不另建，内容落进父任务那一层里。");
                    }

                    found[file] = new InnerArchiveCandidate
                    {
                        Path = file,
                        ParentOutputDirectory = ResolveContinuationOutputDirectory(parent, file, layerName),
                        ParentTaskName = parentName,
                        RootSourcePath = parent.ChainRootIdentity
                    };
                }
            }

            /*
             * 扫描日志：**先写清"看了多少、认出几个、跳过几个"，再把逐条明细列出来**。
             * 上限 20 条明细（一个包里几千个文件时不能让日志爆掉），超出只报个数 ——
             * 但"有多少没列出来"必须写出来，绝不静默截断。
             */
            scanLog.Add(
                $"一键处理：第 {round} 层产物扫描 —— 新文件 {newFileCount} 个；"
                + $"按内容认出归档 {recognizedLines.Count} 个；跳过 {skippedLines.Count} 个。");

            foreach (string line in AppendCapped(recognizedLines, "认出（会继续解）"))
            {
                scanLog.Add(line);
            }

            foreach (string line in AppendCapped(skippedLines, "跳过"))
            {
                scanLog.Add(line);
            }

            return (found.Values.OrderBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase).ToList(), scanLog);
        }

        /// <summary>明细行的条数上限（每个方向）：够看清现场，又不至于让日志爆掉。</summary>
        internal const int MaxScanDetailLines = 20;

        /// <summary>把明细行截成"最多 N 条 + 一行说明还剩多少"（绝不静默截断）。</summary>
        private static IEnumerable<string> AppendCapped(List<string> lines, string label)
        {
            foreach (string line in lines.Take(MaxScanDetailLines))
            {
                yield return $"一键处理：    {label}：{line}";
            }

            if (lines.Count > MaxScanDetailLines)
            {
                yield return $"一键处理：    {label}：…还有 {lines.Count - MaxScanDetailLines} 个（略）";
            }
        }

        /// <summary>
        /// 取候选文件的字节数；读不到（文件刚好被删 / 权限）返回 false = 不算候选。
        ///
        /// <para>0 字节的 `*.7z.001` **不是包**（用户 2026-09-24 铁证）：7z 用错密码"半成功"时
        /// 就会写出这种桩文件，把它当内层包继续解等于连解两层空气。</para>
        /// </summary>
        private static bool TryGetCandidateLength(string filePath, out long length)
        {
            length = 0;

            try
            {
                length = new FileInfo(filePath).Length;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 按**内容**判断一个候选是不是归档（后缀一个字都不看）。
        ///
        /// <para>判据与识别阶段**同一口径**：直接调 <see cref="ArchiveDetectService.DetectAsync"/>
        /// ——也就是"扫描任务"那一步用的同一个实现：先读文件头魔数（RAR5 / 7z / ZIP / 分卷起点…），
        /// 文件头认不出时它自己会去看文件尾部有没有"内嵌归档"（假 MP4 头 + 尾部完整 ZIP 那种双面文件）。
        /// ⛔ 这里**不许**再另造一套签名表或另加一道按名字的筛子 —— 那正是 2026-09-25
        /// 真机上"名字被塞了两个字就只解一层"的根因。</para>
        ///
        /// <para>返回 <c>(是归档?, 判据说明)</c>：说明会原样进日志（用户要求"看日志就能定位"）。</para>
        /// </summary>
        private async Task<(bool IsArchive, string How)> ClassifyArchiveByContentAsync(string filePath)
        {
            try
            {
                DetectResult result = await _detectService.DetectAsync(filePath);

                if (!result.IsArchive || !result.IsKnownFormat)
                {
                    return (false, "按内容认不出是归档（文件头与尾部都试过了）");
                }

                /*
                 * 说清是**哪一条**判据认出来的：
                 * · 尾部内嵌归档 → 这种文件必须按偏移抠出来才能解（解压侧已经这么做了）；
                 * · 文件头魔数 → 普通包。
                 * 用户要的是"日志能自证"，所以这一句不能含糊。
                 */
                return result.EmbeddedArchiveOffset > 0
                    ? (true, $"尾部内嵌归档（{result.Format}，偏移 {result.EmbeddedArchiveOffset}）")
                    : (true, $"文件头魔数（{result.Format}）");
            }
            catch
            {
                // 读不了（被独占 / 刚好被删）：当作"认不出来"，不把它当包 —— 宁可不续解，也不解空气。
                return (false, "读不了（被占用或刚好被删）");
            }
        }

        /// <summary>
        /// 一个任务的产物可能落在哪几个目录。
        ///
        /// 除了输出目录，还要看归集目录：开了"结果归集"时产物会被**移动**到归集目标目录，
        /// 内层包也跟着跑了 —— 只看 OutputPath 的话这种配置下永远找不到内层包（看起来就像功能没生效）。
        /// </summary>
        private static IEnumerable<string> CandidateDirectories(ArchiveTask task)
        {
            if (!string.IsNullOrWhiteSpace(task.OutputPath))
            {
                yield return task.OutputPath;
            }

            if (!string.IsNullOrWhiteSpace(task.CollectedPath))
            {
                yield return task.CollectedPath;
            }
        }

        /// <summary>
        /// 判断一个文件是不是<b>分卷的后续卷</b>（<c>.002+</c>／<c>.z02+</c>／<c>.r01+</c>／<c>.part2+</c>）。
        ///
        /// <para>这是续解扫描里**唯一**还按名字排除的一类，理由：一组分卷里"哪一卷是开头"
        /// 只能从名字看出来 —— 那是<b>分组</b>信息，不是格式判断。格式一律只看内容
        /// （见 <see cref="ClassifyArchiveByContentAsync"/>）。</para>
        ///
        /// <para>⛔ 别在这里加"后缀必须是已知归档后缀"那种条件：2026-09-25 真机上
        /// "名字被塞了两个字（<c>222.ra删除r</c>）就只解一层"的根因正是那样一道门。</para>
        /// </summary>
        internal static bool IsVolumeContinuationPart(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            string fileName = Path.GetFileName(filePath);
            string extension = Path.GetExtension(fileName);

            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            /*
             * ⚠ 判据改用**容差档**（2026-09-28 审计：这是全项目最后一处还在用"前缀档"的分卷名判断）。
             * 老写法 `IsVolumePartExtension(extension)` 只认干净的 `.001`/`.z01`，于是名字被伪装过的后续卷
             * （`amb909.7删z.00除2`、`amb909.7z.002sc`）在这里判成"不是后续卷" —— 与探测器、改名闸门
             * 的口径不一致（同一判据第三次翻车就是这种"两处不同步"）。判据只留 `ExtensionHelper` 一份。
             */
            if (ExtensionHelper.TrySplitVolumeSegmentTolerant(extension.TrimStart('.'), out string volumeMark, out _))
            {
                // 三位数字分卷里只有 001 是起点；.z01 / .r00 这类也不是组的开头（老口径不变）。
                return !string.Equals(volumeMark, "001", StringComparison.OrdinalIgnoreCase);
            }

            // xxx.part2.rar 的最后后缀是 .rar，编号在倒数第二个后缀上。
            return GetPartSegmentNumber(fileName) > 1;
        }

        /// <summary>
        /// 判断一个文件是不是"归档的起点"（分卷组的第一卷，或者单文件归档）。
        ///
        /// <para>⚠ <b>它只用于"按名字粗筛 + 列表/断言"，⛔ 不再是续解扫描的判据</b>
        /// （2026-09-25 第 33 条：后缀可以骗人，续解必须按内容认，见
        /// <see cref="ClassifyArchiveByContentAsync"/>）。</para>
        ///
        /// 排除项：<c>.002</c> 及更大编号、<c>.z01/.z02…</c>、<c>.r00/.r01…</c>、
        /// <c>.part2</c> 及更大的分卷段 —— 它们既不是完整归档，也不是组的开头。
        /// </summary>
        internal static bool IsArchiveStartPoint(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            string fileName = Path.GetFileName(filePath);
            string extension = Path.GetExtension(fileName);

            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            if (IsVolumeContinuationPart(filePath))
            {
                return false;
            }

            return ExtensionHelper.IsKnownArchiveExtension(extension);
        }

        /// <summary>取 <c>xxx.part01.rar</c> 里的 1；不是 part 命名返回 0。</summary>
        private static int GetPartSegmentNumber(string fileName)
        {
            string partSegment = Path.GetExtension(Path.GetFileNameWithoutExtension(fileName));

            if (string.IsNullOrWhiteSpace(partSegment))
            {
                return 0;
            }

            string digits = partSegment.TrimStart('.');

            if (digits.Length < 5 ||
                !digits.StartsWith("part", StringComparison.OrdinalIgnoreCase) ||
                !digits.Skip(4).All(char.IsDigit))
            {
                return 0;
            }

            return int.TryParse(digits.Substring(4), out int number) ? number : 0;
        }

        /// <summary>
        /// 一行汇总。
        ///
        /// 硬要求：各分项加起来**必须等于本次处理的任务数**。
        /// 以前这里统计整个列表，还把"扫描过但从没被处理"的任务算成失败 ——
        /// 于是出现过 `成功 0 / 失败 1 / 跳过 2（共 4 个任务）`：0+1+2=3≠4，
        /// 用户根本没法判断到底发生了什么。汇总如果自己都对不上，还不如不显示。
        ///
        /// 续解加进来的任务同样计入"本次处理的任务数"（它们确实被处理了），口径写在 scope 里。
        /// </summary>
        /// <param name="pendingContinuation">
        /// 撞到轮数上限时"已加进列表、这一批没解"的内层包个数（用户 2026-09-24 第 16 条追加）。
        /// 大于 0 时汇总里必须写出**还剩多少 + 怎么继续** —— 到顶静默停下正是他要修的那件事。
        /// </param>
        private string BuildSummaryLine(
            IReadOnlyList<ArchiveTask> targets,
            bool stopped = false,
            bool stopRequestedByUser = false,
            int continuationLayers = 0,
            bool hitRoundLimit = false,
            int pendingContinuation = 0)
        {
            /*
             * ===== 分项一律按**机器终态**算（用户 2026-09-27 真机：口径打架）=====
             *
             * 旧写法读的是状态字符串（`Status == StatusText.X`），而递归失败那条路
             * 过去只写了状态、`Outcome` 留在 `Pending` —— 于是同一个任务：
             * ①页说「部分完成」、这里说「未处理 1」、批末诊断说「下一步：其他」。
             *
             * 现在五档全部读 <see cref="ArchiveTask.Outcome"/>（唯一的机器事实），
             * 分项之和 + 未处理 = 本次任务数这条恒等式成立：
             * `Outcome` 非 `Pending` 的一定落在成功 / 失败 / 部分完成 / 跳过 / 取消 五档里，
             * 一个都不会漏出去（漏出去就会变成凭空多一个"未处理"）。
             */
            int success = targets.Count(task =>
                task.Outcome == TaskOutcome.Succeeded &&
                task.OutputVerification != OutputVerificationOutcome.Failed);
            int partial = targets.Count(task => task.Outcome == TaskOutcome.PartiallyCompleted);
            int cancelled = targets.Count(task => task.Outcome == TaskOutcome.Cancelled);
            int skipped = targets.Count(task => task.Outcome == TaskOutcome.Skipped);
            int failed = targets.Count(task => task.Outcome == TaskOutcome.Failed);

            /*
             * "终态说成功、校验却判否"那一帧（真机出现过，不变量 6）：上面成功那一档已经
             * 把它剔掉了，它必须落到失败侧 —— 否则它会从五个分项里一起漏出去，变成"未处理 +1"。
             */
            failed += targets.Count(task =>
                task.Outcome == TaskOutcome.Succeeded &&
                task.OutputVerification == OutputVerificationOutcome.Failed);

            // 剩下的就是"既没成功也没失败、也没跳过"的：没轮到它（例如格式未知却没被处理）。
            int untouched = targets.Count - success - partial - cancelled - skipped - failed;

            var parts = new List<string> { $"成功 {success}", $"失败 {failed}", $"跳过 {skipped}" };

            if (partial > 0)
            {
                parts.Add($"部分完成 {partial}");
            }

            if (cancelled > 0)
            {
                parts.Add($"取消 {cancelled}");
            }

            if (untouched > 0)
            {
                parts.Add($"未处理 {untouched}");
            }

            string scope = targets.Count == Tasks.Count
                ? $"本次 {targets.Count} 个任务"
                : $"本次 {targets.Count} 个 / 列表共 {Tasks.Count} 个";

            /*
             * 被「停止后续」打断时不许写成"完成"：失败/取消/部分完成不得显示成成功（不变量 6）。
             * 分项数字照旧自洽，只是把话说准。
             */
            string line = $"{(stopped ? "一键处理已停止" : "一键处理完成")}：{string.Join(" / ", parts)}（{scope}）。";

            int renameSuccess = targets.Count(t => t.Status == StatusText.RenameSuccess);

            if (renameSuccess > 0)
            {
                line += $" 已修正后缀 {renameSuccess} 个。";
            }

            // 跳过必须说清为什么，否则"跳过 2"等于没说
            int notArchive = targets.Count(t => t.Status == StatusText.Skipped && !t.IsArchive);

            if (notArchive > 0)
            {
                line += $" 跳过的 {notArchive} 个已由 7-Zip 确认不是压缩包。";
            }

            int passwordError = targets.Count(t => t.Status == StatusText.WrongPassword);

            if (passwordError > 0)
            {
                line += $" 密码错误 {passwordError} 个：检查密码本里是否包含这些包的密码。";
            }

            int corrupted = targets.Count(t => t.Status == StatusText.Corrupted);

            if (corrupted > 0)
            {
                line += $" 文件损坏 {corrupted} 个：这类只能重新下载。";
            }

            if (stopped)
            {
                /*
                 * ⚠ 两种"停下"必须分开说（2026-09-28 真机）：用户按了「停止后续」，和
                 * "本轮有任务真的没轮到"（程序自己的判断）是两件事 —— 老代码一律写"已按「停止后续」中断"，
                 * 于是用户没按过也被告知"你按了"，还会误以为程序没错。
                 */
                string reason = stopRequestedByUser
                    ? "已按「停止后续」中断"
                    : "本轮有任务没轮到，已停下（不是你按的「停止后续」）";

                line += continuationLayers > 0
                    ? $" {reason}（已完成续解 {continuationLayers} 层）。"
                    : $" {reason}，没有继续解内层包。";
            }
            else if (continuationLayers > 0)
            {
                // 续解出来的内层包**不另建目录**：产物全部归到源包那一个输出目录里
                // （用户诉求："一个源包 = 一个最终目录"）。这句话就是给用户对账用的。
                line += $" 自动续解 {continuationLayers} 层（产物归入同一个输出目录，不再另建文件夹）。";
            }

            if (hitRoundLimit)
            {
                /*
                 * 用户 2026-09-24 第 16 条追加：到顶必须"提示还剩多少 + 怎么继续"。
                 * 只有"已达到 N 轮上限，没有继续"这句话时，用户看到的就是"又只解了两层"——
                 * 他不知道还剩什么、也不知道下一步点哪里。
                 */
                line += pendingContinuation > 0
                    ? $" 已达到 {RoundLimit} 轮上限，还剩 {pendingContinuation} 个内层包没解（已加进列表并勾好，点「继续解」接着解）。"
                    : $" 还有更深的内层包，但已达到 {RoundLimit} 轮上限，没有继续。";
            }

            return line;
        }

        /// <summary>
        /// 解压成功（含"已覆盖"）**且产物校验没有判否**。
        ///
        /// <para>
        /// ⚠ 2026-09-24 起加上了后半句（不变量 6 的真机违反）：用户机器上出现过
        /// "状态写着解压成功、校验却已判否"的那一帧。**统计口径不许把那种任务算成成功**，
        /// 所以这里读一次校验事实 —— 这是**机器可判的字段**，不是中文文案比较
        /// （用户 2026-09-24 明确要求：统计与裁决不许依赖状态字符串）。
        /// </para>
        /// <para>
        /// ⛔ 它只用于**统计与显示**。凡是"要不要删源 / 搬源 / 继续往下解"的裁决，
        /// 一律读 <see cref="ArchiveTask.OutputVerification"/> 与 <see cref="ArchiveTask.Outcome"/>，
        /// 不许调这个方法（那是本次修复的另一半）。
        /// </para>
        /// </summary>
        internal static bool IsSuccessStatus(ArchiveTask task)
        {
            /*
             * 判据本体搬到了 Models/TaskOutcomeClassifier —— 这里转调。
             * 搬家的理由见那个类的注释：同一份名单以前在 OneClickCoordinator 与
             * TaskSummaryService 各写了一遍，漏一条就表现为"①页算失败、一键汇总算未处理"。
             */
            return TaskOutcomeClassifier.IsSuccessStatus(task);
        }

        /// <summary>
        /// 这个任务"这一轮处理过了"吗 —— 有终态的都算处理过。
        /// 没有终态的（已识别 / 等待解压 / 解压中…）说明它**根本没轮到**，那就是被「停止后续」截断的信号。
        ///
        /// internal 是为了让测试直接钉住"新增状态有没有被认账"（同类先例：<see cref="IsArchiveStartPoint"/>）：
        /// 漏一个状态不会编译失败，只会让汇总把"跑完了"说成"已停止 / 未处理 N"。
        /// </summary>
        internal static bool IsHandled(ArchiveTask task)
        {
            if (task == null)
            {
                return false;
            }

            /*
             * **机器终态优先**（用户 2026-09-27 真机：口径打架的根子之一）。
             *
             * `Outcome` 不是 `Pending` 就说明这一单确实跑过了并落了结论 —— 这是事实，
             * 比任何状态名单都硬。旧写法只读状态名单，于是"状态写着某个名单外的中文、
             * 终态却已经是失败"的帧会被读成"没轮到"，汇总当场打印「未处理 1 / 已按停止后续中断」，
             * 而用户根本没按过停止（真机 `giu.7z.001` 就是这一帧）。
             */
            if (task.Outcome != TaskOutcome.Pending)
            {
                return true;
            }

            // 终态还没落的（少数路径 / 老数据 / 测试直接造的任务）：退回状态名单判。
            return IsSuccessStatus(task) ||
                   task.Status == StatusText.PartiallyCompleted ||
                   task.Status == StatusText.Cancelled ||
                   task.Status == StatusText.Skipped ||
                   IsFailureStatus(task);
        }

        /// <summary>
        /// 真正"处理过并且失败了"的状态。
        ///
        /// 「达到密码尝试上限」也算（不变量 6 的反面同样成立：跑完了不许说成"被停止"）：
        /// 候选试到上限就停了，这是一次**正常的失败终态**。漏掉它会同时坏两件事：
        /// ① 汇总的"失败"里少算一个，分项之和与任务数对不上；
        /// ② <see cref="IsHandled"/> 跟着返回 false → 被当成"没轮到" → 汇总打印
        ///    "一键处理已停止 / 未处理 N"，把正常结束误报成被用户停止。
        /// 实测量到的错话（负向对照）：
        /// "一键处理已停止：成功 0 / 失败 0 / 跳过 0 / 未处理 1（本次 1 个任务）。已按「停止后续」中断…"
        /// </summary>
        internal static bool IsFailureStatus(ArchiveTask task)
        {
            /*
             * 名单本体只有一个出口：<see cref="TaskOutcomeClassifier"/>（本方法只转调）。
             *
             * 旧写法是在这里 `task.Status is …` 再写一遍名单，与 ①页那份
             * （`TaskSummaryService.IsExtractFailureStatus`）各写各的 —— 2026-09-29 因此漏过两条，
             * 2026-09-27 真机 `giu.7z.001` 又出现了"①页算失败、一键汇总算未处理"。
             * ⛔ 以后加状态只改 <see cref="TaskOutcomeClassifier"/> 一处。
             */
            return task != null && TaskOutcomeClassifier.IsFailureStatus(task.Status);
        }
    }
}

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
    /// 轮数卡死在 <see cref="MaxRounds"/> 轮（不变量 8）。RecursionMode 的默认值不动。
    ///
    /// 刻意不做的事：
    /// - 不自动删源包（那是 M3 的独立开关，默认关闭，且要校验通过才删）；
    /// - 不改用户没勾选的任务（沿用既有"只处理选中项"的约定）——
    ///   续解只处理**本轮产物里新出现**的归档，产物目录里本来就有的包一个都不碰。
    /// </summary>
    internal sealed class OneClickCoordinator
    {
        /// <summary>
        /// 轮数硬上限（不变量 8）：含第一层在内一共 **10 轮**。
        ///
        /// <para>⚠ 2026-09-24 第 16 条追加由用户拍板从 3 提到 10：他要的是**一键解到尽头**
        /// （原话："你为什么只弄了两层，我要的一键解压时多重解压……都到最后一步了还没成功"）。
        /// 但硬上限这件事本身不许取消（不变量 8）：真遇到"套了几十层"的包，继续无脑往下解
        /// 只会把时间和磁盘烧在一个可能失控的展开上。</para>
        ///
        /// <para>到顶**不是静默停下**：剩下的内层包会被加进任务列表并勾好，汇总与弹窗里写明
        /// "还剩 N 个内层包没解"，界面给一个「继续解」按钮接着跑下一批 10 轮
        /// （见 <see cref="OneClickOutcome.PendingContinuationCount"/>）。</para>
        /// </summary>
        internal const int MaxRounds = 10;

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
        /// （中间件集中处）—— 那时要爬到「其余物」外面那一层，否则下一层的内容物会被放进其余物里、
        /// 跟着「彻底删除」一起没了。</para>
        ///
        /// <para>归集（<c>CollectResultsToDirectory</c>）是"把产物目录整个搬走"，那时旧路径已经不存在，
        /// 权威落点是 <see cref="ArchiveTask.CollectedPath"/>（既有行为，不变）。</para>
        /// </summary>
        /// <param name="parentTask">内层包所属的父任务。</param>
        /// <param name="innerPackagePath">内层包自己的路径（给 null 时退回"父任务的内容物层 / 输出目录"）。</param>
        internal static string ResolveContinuationOutputDirectory(ArchiveTask parentTask, string? innerPackagePath = null)
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
             * ⚠ 只有"算出来的目录**就是这个链根自己的输出根**"时才补那一层（⛔ 不许用"最后一段名字不等于包名"
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

                EnterBusy();

                try
                {
                    // 新的一批开始：上一次留下的"还剩 N 个内层包"提示先清掉（跑完按本次结论重设）。
                    _vm.ReportPendingContinuation(0);

                    OneClickOutcome outcome = await RunPipelineAsync(targets, runOptions);

                    _vm.ReportPendingContinuation(outcome.PendingContinuationCount);

                    _dialogService.ShowInfo(outcome.Summary);
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
            AppendLog("INFO", $"一键处理开始，共 {firstRoundTargets.Count} 个任务。");

            // 处理过的任务按轮累加：汇总要算"本次一共处理了多少个"，只算第一轮会和实际不符。
            var processed = new List<ArchiveTask>(firstRoundTargets);

            // 源文件（含分卷各卷）不算内层包：不能把用户最初给的那个 .mp4 / .7z.001 又加一遍。
            HashSet<string> sourcePaths = BuildSourcePathSet(firstRoundTargets);

            List<ArchiveTask> roundTargets = firstRoundTargets.ToList();

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
                while (round < MaxRounds)
                {
                    round++;

                    // 第一步：识别（第 2 轮起只扫本轮新加进来的包）。
                    await ScanRoundAsync(round, roundTargets);

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
                    if (stopRequested || roundTargets.Any(t => !IsHandled(t)))
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

                    if (round >= MaxRounds)
                    {
                        hitRoundLimit = true;

                        /*
                         * 用户 2026-09-24 第 16 条追加：到顶**不能静默停下**。
                         *
                         * 把这一层发现的内层包**照样加进任务列表并勾好**（走与正常续解同一条路），
                         * 只是这一批不再解它们：于是用户看到的列表里就摆着"还没解的那些包"，
                         * 汇总与弹窗写明还剩几个，界面给「继续解」接着跑下一批 10 轮。
                         *
                         * 为什么不直接继续解完：硬上限就是不变量 8 的那道闸（见 MaxRounds 的说明）。
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
                            + $"但已达到 {MaxRounds} 轮上限，本批先停在这里 —— "
                            + $"{pendingContinuation.Count} 个内层包已加进列表并勾好，点「继续解」接着解（每次最多再解 {MaxRounds} 轮）。");

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
                firstRoundTargets,
                processed,
                stopped || stopRequested,
                hitRoundLimit,
                runOptions);

            string summary = BuildSummaryLine(processed, stopped, continuationLayers, hitRoundLimit, pendingContinuation.Count);

            AppendLog("INFO", summary);

            return new OneClickOutcome
            {
                Rounds = round,
                ContinuationLayers = continuationLayers,
                Stopped = stopped,
                HitRoundLimit = hitRoundLimit,
                PendingContinuationCount = pendingContinuation.Count,
                Summary = summary
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
                if (pending > 0)
                {
                    AppendLog(
                        "WARN",
                        $"一键处理：被「停止后续」中断，{pending} 个源包的补搬（移入其余物）没有执行，源包留在原地。");
                }

                return;
            }

            if (hitRoundLimit)
            {
                if (pending > 0)
                {
                    AppendLog(
                        "WARN",
                        $"一键处理：还有更深的包没解（已达到 {MaxRounds} 轮上限），" +
                        $"{pending} 个源包的补搬没有执行 —— 内容物可能还不全，此时不动源包。");
                }

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
        /// 为什么临时关掉 <see cref="AppSettings.AutoScanAfterDrop"/>：
        /// AddPathsAsync 在它开启时会顺手对整个列表做一次重新识别，同样会把已经解压完的任务状态冲回
        /// "已识别"（第 1 层的结果在界面上就没了）。这里只要"把新文件变成任务"，
        /// 识别由本轮自己按任务做（见 <see cref="ScanRoundAsync"/>），所以临时关掉、用完立刻还原。
        /// </summary>
        private async Task<List<ArchiveTask>> AddInnerTasksAsync(IReadOnlyList<InnerArchiveCandidate> candidates)
        {
            int before = Tasks.Count;
            bool autoScanAfterDrop = Settings.AutoScanAfterDrop;

            Settings.AutoScanAfterDrop = false;

            try
            {
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
                    ImportMode.Append);
            }
            finally
            {
                Settings.AutoScanAfterDrop = autoScanAfterDrop;
            }

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

                    if (byPath.TryGetValue(NormalizePath(Tasks[i].CurrentPath), out InnerArchiveCandidate? candidate))
                    {
                        Tasks[i].ParentOutputDirectory = candidate.ParentOutputDirectory;
                        Tasks[i].ParentTaskName = candidate.ParentTaskName;

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
                        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
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

                foreach ((ArchiveTask _, string outputDirectory) in parents)
                {
                    if (!Directory.Exists(outputDirectory))
                    {
                        continue;
                    }

                    try
                    {
                        results.Add((
                            outputDirectory,
                            Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories).ToList()));
                    }
                    catch
                    {
                        // 读不了这个目录：跳过（快照阶段已经写过一条 WARN，不重复打扰用户）。
                    }
                }

                return results;
            });

            var found = new Dictionary<string, InnerArchiveCandidate>(StringComparer.OrdinalIgnoreCase);
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

                string continuationOutput = ResolveContinuationOutputDirectory(task); foreach (string file in files)
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
                        found[file] = new InnerArchiveCandidate
                        {
                            Path = file,

                            /*
                             * ⚠ 落点**按每个内层包自己所在的目录**算（第 35 条），不能用"每个父任务一个" ——
                             * 共用输出根那一档下，同一个父任务的两个内层包分别躺在
                             * `…\111\2222\` 与 `…\111\3333\`，各自的内容物必须回到各自那一层旁边。
                             */
                            ParentOutputDirectory = ResolveContinuationOutputDirectory(task, file),
                            ParentTaskName = parentName
                        };
                    }
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

            if (ExtensionHelper.IsVolumePartExtension(extension))
            {
                // 三位数字分卷里只有 .001 是起点；.z01 / .r00 这类也不是组的开头。
                return !extension.Equals(".001", StringComparison.OrdinalIgnoreCase);
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
            int continuationLayers = 0,
            bool hitRoundLimit = false,
            int pendingContinuation = 0)
        {
            int success = targets.Count(IsSuccessStatus);
            int partial = targets.Count(t => t.Status == StatusText.PartiallyCompleted);
            int cancelled = targets.Count(t => t.Status == StatusText.Cancelled);
            int skipped = targets.Count(t => t.Status == StatusText.Skipped);
            int failed = targets.Count(IsFailureStatus);

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
                line += continuationLayers > 0
                    ? $" 已按「停止后续」中断（已完成续解 {continuationLayers} 层）。"
                    : " 已按「停止后续」中断，没有继续解内层包。";
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
                    ? $" 已达到 {MaxRounds} 轮上限，还剩 {pendingContinuation} 个内层包没解（已加进列表并勾好，点「继续解」接着解）。"
                    : $" 还有更深的内层包，但已达到 {MaxRounds} 轮上限，没有继续。";
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
            if (task == null || task.OutputVerification == OutputVerificationOutcome.Failed)
            {
                return false;
            }

            return task.Status == StatusText.ExtractSuccess ||
                   task.Status == StatusText.Overwritten;
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
             * 「源文件已变化」（不变量 11）也在这个名单里：引擎一次都没被调用，
             * 但它是一次**正常的失败终态** —— 用户重新扫描之后还要接着处理，汇总里必须算进"失败"，
             * 否则分项之和与任务数对不上、IsHandled 还会把它当成"没轮到"。
             */
            return task.Status is
                StatusText.ExtractFailed or
                StatusText.WrongPassword or
                StatusText.Corrupted or
                StatusText.AccessDenied or
                StatusText.OutputConflict or
                StatusText.VolumeMissing or
                StatusText.PathTooLong or
                StatusText.SevenZipMissing or
                StatusText.UnknownError or
                StatusText.RenameFailed or
                StatusText.TestFailed or
                StatusText.PasswordAttemptLimitReached or
                StatusText.SourceChanged;
        }
    }
}

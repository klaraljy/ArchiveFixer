using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 递归展开策略（设计.md §十–§十四、AGENTS.md §6 第 8 条：多分支默认不展开，必须问）。
    /// </summary>
    public enum RecursionMode
    {
        /// <summary>只解当前这一层，不看里面还有什么。</summary>
        SingleLayer,

        /// <summary>
        /// 默认：只有一个"主要内层归档"（同层其它文件都是说明类文件）时才自动继续。
        /// 出现多个内层归档就停下来问用户，绝不替他决定。
        /// </summary>
        SingleChain,

        /// <summary>展开所有内层归档（必须由用户显式选择），仍受全部上限约束。</summary>
        AllBranches
    }

    /// <summary>
    /// 递归的硬上限（AGENTS.md §6 第 8 条：层数 / 文件数 / 总大小 / 展开比 / 密码尝试次数）。
    ///
    /// 这些值是"安全阀"而不是"性能参数"：命中任何一个都必须**停下并报告**，
    /// 不允许"再试一层看看"。压缩炸弹靠的就是把其中某一条顶穿。
    /// </summary>
    public sealed class RecursionLimits
    {
        /// <summary>最多解几层（含第 0 层）。3 层足够覆盖 外层.zip → 内层.7z → data.tar 这种真实包。</summary>
        public int MaxDepth { get; init; } = 3;

        /// <summary>所有层累计的输出文件数上限。</summary>
        public int MaxTotalFiles { get; init; } = 100_000;

        /// <summary>所有层累计的输出总字节数上限（默认 50 GiB）。</summary>
        public long MaxTotalSize { get; init; } = 50L * 1024 * 1024 * 1024;

        /// <summary>
        /// 单层展开比上限：该层解压后总大小 / 该层归档文件大小。
        /// 500 倍对正常资源包足够宽松（视频、图片再压也到不了），超了就是"疑似压缩炸弹"。
        /// </summary>
        public double MaxExpansionRatio { get; init; } = 500d;

        /// <summary>单层里最多允许出现多少个内层归档；超了说明这不是"套娃"而是"归档集合"，应当让用户单独处理。</summary>
        public int MaxInnerArchivesPerLayer { get; init; } = 50;

        /// <summary>
        /// 每层最多试几个密码候选。
        ///
        /// 为什么和"密码错误"必须分开（AGENTS.md §9.2）：
        /// 密码错误 = "试遍了所有候选都不对"，用户该去补密码本；
        /// 达到上限 = "候选还有，只是我们不再试了"，用户该做的是缩小候选或调高上限。
        /// 两种情况给用户的下一步动作完全不同，混成一个会让人白折腾。
        /// </summary>
        public int MaxPasswordAttemptsPerLayer { get; init; } = 8;

        /// <summary>默认上限。写成静态单例是为了让"默认值"只有一处，便于核对与调整。</summary>
        public static RecursionLimits Default { get; } = new();
    }

    /// <summary>
    /// 一层的执行报告。"失败定位到层"（AGENTS.md §6 第 14 条）靠的就是它。
    /// 注意：本类**不得**出现明文密码，只有 <see cref="UsedPasswordMasked"/> 这个脱敏占位符。
    /// </summary>
    public sealed class RecursionLayerReport
    {
        public int Depth { get; init; }

        /// <summary>本层解的那个归档。</summary>
        public string ArchivePath { get; init; } = string.Empty;

        /// <summary>本层的产物目录（工作区里的 layer-XXX\output）。</summary>
        public string OutputPath { get; init; } = string.Empty;

        public bool Success { get; init; }

        /// <summary>引擎给的状态文案（如"解压成功"）。</summary>
        public string Status { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;

        /// <summary>本层产物里探测到的内层归档（完整路径）。</summary>
        public IReadOnlyList<string> InnerArchives { get; init; } = Array.Empty<string>();

        public int OutputFileCount { get; init; }

        public long OutputSize { get; init; }

        /// <summary>
        /// **本层解压前从引擎拿到的清单**（条目数 + 解压后总字节），L3 的预期来源。
        ///
        /// <para>为什么必须留在报告里（用户 2026-09-30 真机）：检验等级 L3 要拿"产出最终内容物的那一层"
        /// 的清单去核对产物，而解压前那次列目录（<see cref="CheckEntriesBeforeExtractAsync"/>）
        /// 以前只用来做路径预检，`ArchiveListResult` **用完即弃** —— 于是链尾只剩第 0 层的清单，
        /// 而第 0 层清单对不上叶子层的产物，L4 只能判「判不出」，源包与其余物一个字节都不动。
        /// 现在每一层各留各的（⛔ 第 0 层的清单只对第 0 层用）。</para>
        /// </summary>
        public LayerManifest Manifest { get; init; } = LayerManifest.Unavailable("这一层没有列过清单");

        /// <summary>只用 "空密码" 或 "******"；本层没试过密码时为空串。</summary>
        public string UsedPasswordMasked { get; init; } = string.Empty;
    }

    /// <summary>
    /// 需要用户拍板的多分支询问。字段是给 GUI 直接用的：<see cref="Prompt"/> 就是弹窗正文。
    /// </summary>
    public sealed class RecursionDecisionRequest
    {
        public string CurrentArchivePath { get; init; } = string.Empty;

        public int Depth { get; init; }

        public IReadOnlyList<string> CandidateArchives { get; init; } = Array.Empty<string>();

        /// <summary>中文，直接弹给用户。</summary>
        public string Prompt { get; init; } = string.Empty;
    }

    /// <summary>为什么会停。<b>不要</b>把它压成"成功/失败"两态：这里的每一项都对应不同的用户动作。</summary>
    public enum RecursionStopReason
    {
        None,

        /// <summary>一路解到底（或按用户选择的模式正常收尾）。</summary>
        Completed,

        /// <summary>出现多个内层归档，等用户拍板。</summary>
        NeedsDecision,

        /// <summary>
        /// 第 1 层起出现多分支：不再问第二遍（问过一次了），停在这一层。
        ///
        /// 与 <see cref="NeedsDecision"/> 分开、而不复用它的理由：两者的用户动作不同 ——
        /// NeedsDecision 是"等你回答"，这一条是"已经按你的选择做完了，剩下的分支没展开"。
        /// 混成一个会让"已完成"和"等你决定"分不清，用户会以为拿到的是最终数据。
        /// </summary>
        BranchNotExpanded,

        MaxDepthReached,

        MaxTotalFilesReached,

        MaxTotalSizeReached,

        /// <summary>单层展开比超限，疑似压缩炸弹。</summary>
        ExpansionRatioExceeded,

        TooManyInnerArchives,

        /// <summary>候选还有，但已经试满 MaxPasswordAttemptsPerLayer。**不是**密码错误。</summary>
        PasswordAttemptsExceeded,

        /// <summary>
        /// 解压途中**磁盘写满**（引擎报 <see cref="Engines.EngineErrorTypes.NoDiskSpace"/>）。
        ///
        /// <para>为什么要单列一条（用户 2026-09-27 真机：递归内层包撞空间不足，批末诊断却归到"其他"）：
        /// 归档本身没有任何问题，用户要做的是**清空间 / 换盘**，而不是去怀疑包坏了或改权限 ——
        /// 把它压在 <see cref="EngineFailed"/> 里，用户就看不到正确方向。</para>
        /// </summary>
        DiskSpaceInsufficient,

        /// <summary>候选全试完了都不对。</summary>
        WrongPassword,

        Corrupted,

        /// <summary>
        /// **两义**：引擎同一句话里既说"密码不对"又说"数据坏了"，谁也单独定不了原因（用户 2026-09-30 真机）。
        ///
        /// <para>它必须与 <see cref="Corrupted"/> 和 <see cref="WrongPassword"/> 都分开：
        /// 折成 Corrupted ⇒ 结论断言"文件损坏"（用户被指去重新下载，而可能只是密码没试对）；
        /// 折成 WrongPassword ⇒ 把真损坏说成密码错（用户去反复核对没写错的密码本）。
        /// 与 7-Zip 侧同一口径：那句 <c>CRC Failed in encrypted file. Wrong password?</c>
        /// 从不当成"已损坏"（见 <c>Item37SafetyTests</c>）。</para>
        /// </summary>
        PasswordOrCorrupted,

        /// <summary>条目路径不安全（预检拦下），本层不落盘。</summary>
        UnsafeEntry,

        /// <summary>
        /// 源文件发生了变化（不变量 11）：本层**一个字节都没解**，本批到此为止。
        ///
        /// <para>与 <see cref="EngineFailed"/> 分开：引擎压根没被调用，这不是"引擎解不开"，
        /// 而是"手上这份识别结果已经不对应这个文件了"。用户要做的事也不同 ——
        /// 重新扫描后再处理，而不是去怀疑包坏了或换个引擎。</para>
        /// </summary>
        SourceChanged,

        UserCancelled,

        EngineFailed
    }

    /// <summary>递归展开的最终结论。</summary>
    public sealed class RecursionResult
    {
        public RecursionStopReason StopReason { get; init; }

        /// <summary>真的走完了（含 SingleLayer 这种"按模式正常收尾"）。</summary>
        public bool Completed { get; init; }

        /// <summary>解出了一些层但没走完（含 NeedsDecision，以及产物留在工作区的情形）。</summary>
        public bool PartiallyCompleted { get; init; }

        /// <summary>非 null 表示需要用户就多分支做选择。</summary>
        public RecursionDecisionRequest? Decision { get; init; }

        public IReadOnlyList<RecursionLayerReport> Layers { get; init; } = Array.Empty<RecursionLayerReport>();

        /// <summary>指向**已完成的最深一层**产物；部分完成时这里是工作区里的路径。</summary>
        public string FinalOutputPath { get; init; } = string.Empty;

        /// <summary>一行中文结论，直接显示给用户。</summary>
        public string Summary { get; init; } = string.Empty;

        /// <summary>
        /// **这一趟有没有真的解出过东西**（任意一层成功、或任意一层的产物目录里留下了文件）。
        ///
        /// <para>为什么要单列一条（用户 2026-09-27 真机 `giu.7z.001`）：调用方要据此区分
        /// "一半做完了"（<c>部分完成</c>）与"什么都没产出"（<c>失败</c>）——
        /// 后者说成"部分完成"会让用户去工作区里找根本不存在的产物；</para>
        ///
        /// <para>判据只看**事实**（层的成功标记 + 产物文件数 / 字节数），⛔ 不比对任何中文文案
        /// （AGENTS.md §7）。</para>
        /// </summary>
        public bool ProducedAnyLayerOutput =>
            Layers.Any(layer => layer.Success || layer.OutputFileCount > 0 || layer.OutputSize > 0);
    }

    /// <summary>
    /// 递归解压核心（M4）。
    ///
    /// 定位与边界：
    /// 1. 引擎、探测器、密码来源**全部从构造函数注入** —— 递归核心不许自己 new 引擎、
    ///    不许直接拼 7z 参数、不许认识 GUI（AGENTS.md §3.1 四条禁止项）。这样才能脱离 GUI 被测。
    /// 2. 它只**解压**：不删源文件（那是 SourceCleanupService 的事）、不改名、不写最终目录
    ///    （中间产物一律先落工作区，完成后再发布）。
    ///    它唯一会删的东西是**它自己造出来的那个工作区目录**（<see cref="CurrentWorkspace"/>），
    ///    而且只在"解完 + 产物发布成功 + 没被取消"时删（见 <see cref="FinalizeRun"/>）；
    ///    失败 / 取消 / 部分完成一律保留，那些目录是用户唯一的线索。
    /// 3. 密码候选由 <c>passwordProvider</c> 给，本类不碰密码本、不记明文；
    ///    对外只暴露 "空密码" / "******"。
    /// 4. 日志由 <c>log</c> 委托注入（可空）：本类不认识 GUI，也不引用具体日志实现 ——
    ///    与引擎 / 探测器 / 密码来源全部注入同一个理由（AGENTS.md §4 分层铁律）。
    ///    清理工作区是**不可逆操作**，删前删后各要一条 INFO、删失败一条 WARN（§9.5 同一要求）。
    ///
    /// 主循环是**一趟宽度优先**的：队列里每一项 = "一个待解的归档 + 它在第几层"，
    /// 解一层、探一层、再决定要不要把探测到的内层归档入队。
    /// 之所以不用递归写法：层数上限、累计预算、以及"多分支要问用户"这三件事，
    /// 在循环里都只在**入队前**检查一次，比散在递归调用里更好审。
    /// </summary>
    public sealed class RecursiveExtractor
    {
        /// <summary>说明类文件后缀：它们和内层归档同级出现时，不构成"多分支"。</summary>
        private static readonly string[] InformationalExtensions =
        {
            ".txt", ".nfo", ".url", ".md", ".sfv", ".jpg", ".png"
        };

        /// <summary>展开比检查的上限保护：只对最大若干层做（防止有人把上限配得极大时白算）。</summary>
        private const int MaxExpansionRatioChecks = 20;

        private readonly IArchiveEngine _engine;
        private readonly IArchiveProber _prober;
        private readonly Func<string, IReadOnlyList<string>> _passwordProvider;
        private readonly RecursionLimits _limits;

        /// <summary>
        /// 日志出口（level, message）。为 null 时只做事、不写日志（单元测试直接 new 的场合）。
        ///
        /// 用委托而不是引用具体日志实现：递归核心不认识 GUI，也不该依赖 LogService ——
        /// 与引擎 / 探测器 / 密码来源全部注入是同一个理由。调用方若是多线程宿主，
        /// 得保证这个委托本身线程安全（<c>MainViewModel.AppendLog</c> 走 BeginInvoke，是安全的）。
        /// </summary>
        private readonly Action<string, string>? _log;

        /// <summary>
        /// **本实例自己持有的**本次（最近一次）运行的工作区。
        ///
        /// 清理永远只针对这个对象，绝不按目录名或"最新目录"去扫 <c>data\work\recursive</c> ——
        /// 并发跑两个递归任务时，那种扫描式删除会把对方正在写的工作区端掉。
        /// 真正被删的是**本次运行**建出来的那一个（<see cref="ExtractAsync"/> 里的局部变量，
        /// 见 <see cref="FinalizeRun"/>），这个属性只是把同一个对象暴露出来给诊断 / 测试 /
        /// "结论被调用方改写"的那条路（<see cref="TryCleanupCurrentWorkspace"/>）用。
        ///
        /// ⚠ 同一个实例**不适合并发跑两次 ExtractAsync**：这个属性会被后一次覆盖。
        /// 协调器是"一个任务一个实例"（<c>ExtractionCoordinator.CreateRecursiveExtractor</c>），
        /// 并发任务各有各的实例，所以互不影响。
        /// </summary>
        public ExtractionWorkspace? CurrentWorkspace { get; private set; }

        /// <summary>
        /// 「续解时省略中间层」这一档（②页设置 <c>AppSettings.OmitMiddleContinuationLayers</c>）。
        ///
        /// <para>
        /// **首层与末层永不受它影响**（用户 2026-09-30 红线：第一层与最后一层绝对不能省，
        /// 只能省中间层）；判据只有一处：<see cref="PackageLayerRules.ShouldKeepLayerFolder"/>。
        /// 由调用方按设置当场赋值（与 <see cref="RecursionLimits"/> 同一个理由：改完设置不重启也要生效）。
        /// 默认 false = 忠实档（每层各占一层）。
        /// </para>
        /// </summary>
        public bool OmitMiddlePackageLayers { get; set; }

        /// <summary>
        /// 上一次运行留下、已被本次续跑取代的工作区（多分支询问 → 用户确认继续这一条路）。
        ///
        /// 为什么是**列表**而不是一个槽位：一个实例上可能出现"询问 → 续跑"这样的多次运行，
        /// 单槽位会在后一次续跑时把前一份**悄悄忘掉**——而那一份往往就是几百 MB。
        /// 这里只负责"记着"；清理时机只有一个：某次运行**成功**收尾时（见 FinalizeRun）。
        ///
        /// 换归档（不是同一个包的续跑）时整份清空：那些工作区属于**别的**任务，
        /// 不能因为后来某个任务成功就被删掉 —— 它们的状态是"部分完成，要保留"。
        /// </summary>
        private readonly List<ExtractionWorkspace> _supersededWorkspaces = new();

        /// <summary>最近一次运行解的是哪个归档。用来判断"这一次是不是同一个归档的续跑"。</summary>
        private string _lastRunArchivePath = string.Empty;

        /// <summary>
        /// **不变量 11 的检查口**（AGENTS.md §6 第 11 条），可空。
        ///
        /// <para>
        /// 每一层**开工之前**问一次："这一层要解的那个源文件还是原来那一份吗？"
        /// 返回非 null = 已经拦下，本层不解、整条递归停下，那句话就是这次的结论。
        /// </para>
        /// <para>
        /// 为什么由调用方注入而不是递归核心自己判：快照挂在 <see cref="ArchiveTask"/> 上、
        /// 判据与状态落法都在协调器（同一句话要同时出现在任务状态、失败清单与日志里）。
        /// 递归核心只知道"要解哪个归档"，它不认识快照，也不该认识 ——
        /// 与引擎 / 探测器 / 密码来源全部注入是同一个理由。
        /// </para>
        /// <para>
        /// ⚠ 注入方要**自己判断是不是第 0 层**：第 1 层起解的是工作区里的过程物，
        /// 拿源包的快照去比它们只会得出一句必然错误的结论（见 ExtractionCoordinator）。
        /// </para>
        /// </summary>
        private readonly Func<ArchiveTask, Task<string?>>? _sourceCheck;

        /// <summary>
        /// **详细日志档**（⑥设置 →「详细日志（排查用）」，默认关）。
        ///
        /// <para>为什么递归核心要知道它（用户 2026-09-27：「开了更详细的日志选项怎么还是这么简单」）：
        /// 递归路径以前**连一条候选日志都没有** —— 真机那次 13 分钟走的正是这条路，
        /// 日志里连"试了几个候选"都看不出来。打开详细日志后，每个候选一条 INFO、
        /// 每层开工一条 INFO，与单层路径同一套措辞（见 <see cref="VerboseLog"/> 的说明）。</para>
        ///
        /// <para>⚠ 候选日志里**只有脱敏占位符**：本类拿到的 passwordProvider 只给值，
        /// 它自己也不知道来源，所以描述由调用方注入（<see cref="DescribeCandidate"/>）。</para>
        /// </summary>
        public bool VerboseLog { get; set; }

        /// <summary>
        /// 把一个密码候选描述成"能写进日志的那半句"（**必须已脱敏**）。
        ///
        /// <para>由调用方注入而不是本类自己拼：候选的来源（空密码 / 统一密码 / 密码本第 N 项…）
        /// 只有 <c>PasswordService</c> 知道，递归核心只拿到值。注入方给的就是单层路径
        /// 用的那一份 <c>BuildTryPasswordLogText</c>，于是两处口径**逐字一致**。</para>
        /// </summary>
        public Func<string, int, string>? DescribeCandidate { get; set; }

        /// <summary>
        /// passwordProvider：给定归档路径，返回按优先级排好的密码候选（**空字符串代表试空密码**）。
        /// 它由调用方注入，递归层自己不碰密码本，也不记明文。
        /// </summary>
        /// <param name="log">
        /// 日志出口（level, message），可空。清理工作区是**不可逆操作**，删前删后各要一条 INFO、
        /// 删失败要一条 WARN（AGENTS.md §9.5 同一要求）—— 所以本类需要一个写日志的地方。
        /// </param>
        /// <param name="sourceCheck">
        /// 每层开工前的"源文件有没有变"检查（不变量 11），可空（不传 = 不做这件事，
        /// 既有调用点与单测的行为因此一个字都不变）。
        /// </param>
        public RecursiveExtractor(
            IArchiveEngine engine,
            IArchiveProber prober,
            Func<string, IReadOnlyList<string>> passwordProvider,
            RecursionLimits? limits = null,
            Action<string, string>? log = null,
            Func<ArchiveTask, Task<string?>>? sourceCheck = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _prober = prober ?? throw new ArgumentNullException(nameof(prober));
            _passwordProvider = passwordProvider ?? (_ => Array.Empty<string>());
            _limits = limits ?? RecursionLimits.Default;
            _log = log;
            _sourceCheck = sourceCheck;
        }

        /// <summary>
        /// 从 <see cref="ArchiveTask.CurrentPath"/> 开始递归展开。
        /// </summary>
        /// <param name="previousDecision">
        /// 非 null 表示"用户已经就上一次的多分支询问做出了选择：继续"。
        /// 这时只处理 <see cref="RecursionDecisionRequest.CandidateArchives"/> 里那几个归档，
        /// **不重新全盘扫描** —— 用户看到的候选清单可能已经被别的东西改过，重扫等于偷偷扩大范围。
        /// </param>
        /// <param name="progress">
        /// 进度接收端（可空）。**直接转发给引擎**（写进每层的 <see cref="ArchiveRequest.Progress"/>），
        /// 节流由引擎层的 <see cref="ArchiveProgressReporter"/> 负责 —— 递归核心**不自己再节流一次**，
        /// 也不自己造一套进度类型（AGENTS.md §3.1：统一进度只有 <see cref="ArchiveProgress"/> 一个）。
        ///
        /// <para>
        /// 为什么补这个口子（2026-09-22）：递归内层包过去完全没挂进度，界面上那一段是"处理中"死等，
        /// 而递归恰恰是最容易久的一段（一层套一层）。挂上之后每层的百分比 / 当前条目都会照常上报，
        /// 于是"当前解的是哪一层里的哪个文件"用户看得见。
        /// </para>
        /// </param>
        /// <param name="stalled">
        /// "长时间没有任何引擎输出"的通知（可空，默认阈值 90 秒）。与 <paramref name="progress"/> 同一个口径：
        /// 只提示、不杀进程，由上层决定怎么显示（不变量 9）。
        /// </param>
        public async Task<RecursionResult> ExtractAsync(
            ArchiveTask task,
            string finalOutputDirectory,
            RecursionMode mode,
            RecursionDecisionRequest? previousDecision = null,
            CancellationToken cancellationToken = default,
            IProgress<ArchiveProgress>? progress = null,
            Action<ArchiveStallNotice>? stalled = null)
        {
            var layers = new List<RecursionLayerReport>();
            var stopReason = RecursionStopReason.None;

            /*
             * 这个字段只表示"这一次运行有没有产生新的、还没回答的询问"。
             * 刻意不从 previousDecision 起手：传了 previousDecision 就说明用户已经回答过了，
             * 把它原样塞回结果会让调用方以为"还得再问一次"（RecursionResult.Decision 的约定是
             * "非 null = 需要用户就多分支做选择"）。
             */
            RecursionDecisionRequest? decision = null;

            int totalFiles = 0;
            long totalSize = 0;

            // 第 1 层起停下来时要能说清"还有多少个内层归档没展开"（不变量 8：多分支默认不展开，必须问）。
            int unexpandedCount = 0;

            /*
             * 因"源文件已变化"停下时，那句话就是本次的结论（不变量 11）。
             * 与别的停因分开记：它的**理由来自调用方**（快照与状态都在协调器那边），
             * 这里只负责原样带进 RecursionResult.Summary，不许在这里自己编一句。
             */
            string sourceChangedReason = string.Empty;

            // 参数校验放在建工作区之前：路径都没有就没什么可展开的，
            // 这里也**不抛异常**（递归核心对外的约定是"用返回值说清楚"，不是"炸给调用方"）。
            if (task == null || string.IsNullOrWhiteSpace(task.CurrentPath))
            {
                return new RecursionResult
                {
                    StopReason = RecursionStopReason.EngineFailed,
                    Completed = false,
                    PartiallyCompleted = false,
                    Decision = decision,
                    Layers = Array.Empty<RecursionLayerReport>(),
                    FinalOutputPath = string.Empty,
                    Summary = "任务没有可处理的归档路径，未开始递归解压"
                };
            }

            // 工作区必须在循环之前就建出来：取消 / 立刻失败时也要留下"产物在哪"的线索。
            ExtractionWorkspace workspace = CreateWorkspace(task);

            /*
             * 本次运行的工作区由**本实例自己持有**（见 CurrentWorkspace 的说明）：
             * 无论后面是清理还是发布，用的都是这一个对象 —— 不去扫目录、不去按名字猜，
             * 所以并发跑的两个任务永远碰不到对方的目录。
             *
             * 顺带判断"这一次是不是同一个归档的续跑"（多分支询问 → 用户确认继续）：
             * 续跑会把第 0 层在新工作区里**重解一遍**，上一次那份产物因此变成纯垃圾
             * （往往是几百 MB）。它当时没被清是对的（"等用户决定"属于部分完成，线索要留住）；
             * 现在用户已经决定、整条递归也真的走完了，那个理由不再成立 —— 见 CleanupSupersededWorkspaces。
             *
             * 判据里必须比对**归档路径**：调用方若把同一个实例拿去跑另一个任务，
             * 上一个任务的工作区属于"部分完成"，不能因为别的任务成功就被删掉。
             */
            string runArchivePath = SafePathHelper.GetFullPathSafe(task.CurrentPath);
            ExtractionWorkspace? previousWorkspace = CurrentWorkspace;

            bool continuesSameArchive = previousDecision != null &&
                previousWorkspace != null &&
                runArchivePath.Length > 0 &&
                SafePathHelper.PathEquals(_lastRunArchivePath, runArchivePath);

            if (continuesSameArchive)
            {
                _supersededWorkspaces.Add(previousWorkspace!);
            }
            else
            {
                // 不是同一个包的续跑：上一轮攒下的那几份属于**别的**任务（部分完成、要保留），
                // 不能再由本次的成功去清它们。
                _supersededWorkspaces.Clear();
            }

            CurrentWorkspace = workspace;
            _lastRunArchivePath = runArchivePath;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pending = new Queue<WorkItem>();

                /*
                 * previousDecision 的语义（规则 4）：用户已经就上次的多分支询问选了"继续"。
                 * 把候选清单压成**文件名**挂在第 0 层的执行项上 —— 后面每一层都拿它做过滤，
                 * 于是"只处理用户点名的这几个归档、不重新全盘扫描"这件事在代码里只有一处实现。
                 *
                 * 为什么不能直接比完整路径：决策里带的是**上一次运行的工作区**里的路径，
                 * 而本次续跑会新建一个工作区（taskId 带时间戳 + 随机码），绝对前缀必然不同 ——
                 * 拿完整路径比对会把用户点过的每一个分支都判成"这一层里没有"，选择被静默丢掉。
                 * 层内部的文件名是两次运行之间唯一稳定的定位键（同一个归档、同一个引擎解出来）。
                 */
                IReadOnlyList<string>? decidedNames = previousDecision == null
                    ? null
                    : previousDecision.CandidateArchives
                        .Select(Path.GetFileName)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Select(name => name!)
                        .ToList();

                pending.Enqueue(new WorkItem
                {
                    Depth = 0,
                    ArchivePath = task.CurrentPath,
                    Layer = workspace.CreateNextLayer(task.CurrentPath),
                    IsRoot = true,
                    DecisionNames = decidedNames
                });

                while (pending.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    WorkItem item = pending.Dequeue();

                    /*
                     * 上限检查必须在**真正动手之前**做（规则 5），而且顺序固定：
                     * 层数 → 累计文件数 → 累计大小 → 展开比。
                     * 顺序固定是为了让"命中哪一条"可预期、可复现、可写进测试；
                     * 密码尝试上限不在这里查，它是**每层内部**的事（见 ExtractLayerAsync）。
                     *
                     * 展开比要问引擎"这个包解压后多大"（第 0 层）或量磁盘（内层），
                     * 两者都可能要等 I/O —— 所以它是 async 的，**绝不在这里同步等待**：
                     * 同步 GetResult() 会把 UI 线程占住，而 7z 的续体要回到同一个线程，
                     * 两边互等就是历史"界面永久无响应"的真凶（无弹窗、无子进程、CPU 不忙）。
                     */
                    RecursionStopReason blocked = CheckLimitsBeforeLayer(item, layers, totalFiles, totalSize);

                    if (blocked != RecursionStopReason.None)
                    {
                        stopReason = blocked;
                        break;
                    }

                    if (layers.Count < MaxExpansionRatioChecks &&
                        await IsExpansionRatioExceededAsync(item, layers, pending).ConfigureAwait(false))
                    {
                        stopReason = RecursionStopReason.ExpansionRatioExceeded;
                        break;
                    }

                    /*
                     * 不变量 11：**本层开工之前**再问一次"源文件还是原来那一份吗"。
                     *
                     * 位置就在上限检查之后、真正动手（列目录 / 调引擎）之前：
                     * 上限检查只算数、展开比那一步虽然会问引擎"这个包解压后多大"、
                     * 但那是只读的探查；真正的写盘发生在 ExtractLayerAsync 里面，
                     * 所以拦在这里 = 一个字节都没写过。
                     *
                     * 只对第 0 层问（见 _sourceCheck 的说明）：第 1 层起解的是我们自己
                     * 从内层抠出来的过程物，拿源包的快照去比它们只会得出一句错误的结论。
                     */
                    if (item.IsRoot && _sourceCheck != null)
                    {
                        string? sourceChanged = await _sourceCheck(task).ConfigureAwait(false);

                        if (!string.IsNullOrWhiteSpace(sourceChanged))
                        {
                            stopReason = RecursionStopReason.SourceChanged;
                            sourceChangedReason = sourceChanged;
                            break;
                        }
                    }

                    LayerOutcome outcome = await ExtractLayerAsync(item, cancellationToken, progress, stalled)
                        .ConfigureAwait(false);

                    if (outcome.Report != null)
                    {
                        layers.Add(outcome.Report);

                        /*
                         * 层成功与否只在这里标一次（就地替换发布只搬"真的解开了"的层）：
                         * 失败的报告会带着 StopReason 让循环 break，所以下面这行是**唯一**的标记点，
                         * ⛔ 不许在别处再标一遍 —— 两个地方各标一次迟早漂移。
                         */
                        item.Layer.Successful = outcome.Report.Success;
                    }

                    if (outcome.StopReason != RecursionStopReason.None)
                    {
                        stopReason = outcome.StopReason;
                        break;
                    }

                    RecursionLayerReport report = outcome.Report!;

                    totalFiles += report.OutputFileCount;
                    totalSize += report.OutputSize;

                    var enqueueState = new EnqueueState
                    {
                        Pending = pending,
                        UnexpandedCount = 0
                    };

                    if (!TryEnqueueNextLayers(
                            item,
                            report,
                            workspace,
                            mode,
                            enqueueState,
                            out RecursionDecisionRequest? askUser,
                            out RecursionStopReason enqueueStopReason))
                    {
                        unexpandedCount = enqueueState.UnexpandedCount;

                        if (askUser != null)
                        {
                            decision = askUser;
                            stopReason = RecursionStopReason.NeedsDecision;
                        }
                        else
                        {
                            stopReason = enqueueStopReason;
                        }

                        break;
                    }
                }

                if (stopReason == RecursionStopReason.None)
                {
                    // 队列空了 = 没有更多可展开的内层归档。
                    stopReason = RecursionStopReason.Completed;
                }

                bool publish = stopReason == RecursionStopReason.Completed;

                return FinalizeRun(
                    BuildResult(
                        stopReason,
                        layers,
                        decision,
                        workspace,
                        finalOutputDirectory,
                        // "源文件已变化"那句话由调用方给（快照与状态都在协调器那边），原样带进结论。
                        sourceChangedReason,
                        publish,
                        unexpandedCount),
                    workspace,
                    task.FileName,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                /*
                 * 取消不是失败（AGENTS.md §6 第 6 条）：保留工作区，让用户能看到已经解出来的部分，
                 * 也能从那里接着处理。绝不在这里抛异常给上层。
                 */
                return FinalizeRun(
                    BuildResult(
                        RecursionStopReason.UserCancelled,
                        layers,
                        decision,
                        workspace,
                        finalOutputDirectory,
                        string.Empty,
                        published: false,
                        unexpandedCount),
                    workspace,
                    task.FileName,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                // 兜底：一个包炸了不能把整批任务带下水（AGENTS.md §6 第 9 条）。
                return FinalizeRun(
                    BuildResult(
                        RecursionStopReason.EngineFailed,
                        layers,
                        decision,
                        workspace,
                        finalOutputDirectory,
                        PasswordMasker.Sanitize(ex.Message),
                        published: false,
                        unexpandedCount),
                    workspace,
                    task.FileName,
                    cancellationToken);
            }
        }

        /// <summary>
        /// 解一层：逐个密码候选试，直到成功或确定停因。
        /// </summary>
        /// <param name="progress">
        /// 进度接收端（可空）：原样挂到本层每一次引擎调用上。
        /// ⚠ 一次"解一层"可能真的跑好几遍引擎（每个密码候选一次），进度因此会**从 0 重新开始** ——
        /// 这是如实反映（上一遍确实白跑了），上层按"进度回退 = 新的一轮"处理（见 TaskProgressSink）。
        /// </param>
        /// <summary>
        /// 把**这一层在工作区里的产物目录**清空并重建（换密码候选之前调用）。
        ///
        /// <para>为什么必须清：7z 用错密码时会先建出 0 字节的桩文件再报错，而本层的提取参数是
        /// `SkipExisting`（`-aos`）—— 下一次尝试会被这些桩文件跳过，正确的密码就永远解不出东西
        /// （用户 2026-09-25 真机日志里"偶数候选产物为空、奇数候选密码错误"的奇偶交替就是这个）。</para>
        ///
        /// <para>安全边界：只动传进来的这个目录（它是 <see cref="ExtractionWorkspace"/> 造的层产物目录）；
        /// 清不掉（被占用 / 权限 / 路径过长）**不抛异常** —— 清不动最多让这一次尝试按旧垃圾判废，
        /// 由结果校验兜底，不该因为一次清理失败就把整层判死。</para>
        /// </summary>
        private static void TryResetLayerOutputDirectory(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                Directory.CreateDirectory(directory);
            }
            catch
            {
                // 见上面的说明：清理失败只影响这一次尝试的质量，不影响正确性（校验那道闸门还在）。
            }
        }

        private async Task<LayerOutcome> ExtractLayerAsync(
            WorkItem item,
            CancellationToken cancellationToken,
            IProgress<ArchiveProgress>? progress = null,
            Action<ArchiveStallNotice>? stalled = null)
        {
            var options = new ExtractOptions
            {
                // 每层都是全新目录，正常情况下不会撞名；万一撞上（归档内有重复条目）
                // 一律"跳过 + 保留先落地的那个"，绝不覆盖：改名属于改名模块的事，递归核心不越权。
                OverwriteMode = "SkipExisting",
                KeepArchiveNameFolder = false
            };

            options.Normalize();

            IReadOnlyList<string> candidates = BuildPasswordCandidates(item.ArchivePath);

            int attempts = 0;
            bool triedAny = false;
            ArchiveOperationResult? lastFailure = null;
            ArchiveOperationResult? informativeFailure = null;
            ArchiveOperationResult? success = null;
            string? succeededPassword = null;

            /*
             * 两义那一档的"第一次出现"（引擎既说密码不对、又说数据坏了）：
             * 收尾时优先拿它当结论（<see cref="ResolvePasswordStopReason"/>），
             * 于是结论与日志同时保留两种可能，⛔ 不会退化成一句"文件损坏"或"密码错误"。
             */
            ArchiveOperationResult? ambiguousPasswordFailure = null;

            /*
             * 本层的清单（L3 的预期，见 <see cref="RecursionLayerReport.Manifest"/>）：
             * `candidateManifest` 是"当前这个候选列出来的清单"，`layerManifest` 只认
             * **真正解开这一层的那一个候选**的清单（见下面赋值处）。
             */
            LayerManifest candidateManifest = LayerManifest.Unavailable("这一层还没有列过清单");
            LayerManifest layerManifest = LayerManifest.Unavailable("这一层没能列出清单（解压前那次列目录没成功）");

            /*
             * 这一层的最多候选数：**与循环用的同一个上限**（`_limits.MaxPasswordAttemptsPerLayer`）。
             * 先算出来是为了让"候选 i/N"里的 N 与真正会试的个数一致 ——
             * 写成 candidates.Count 会在被上限截断时给出一个永远到不了的 N。
             */
            int layerCandidateLimit = Math.Min(candidates.Count, _limits.MaxPasswordAttemptsPerLayer);

            /*
             * 日志里的任务标签：**只写文件名 + 层号**（§8 隐私红线：完整路径不进日志）。
             * 层号是排查多层嵌套时唯一能对号入座的信息，既有日志的行首形状就是它。
             */
            string layerLabel = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.RecursionLayerLogPrefixFormat,
                item.Depth) + Path.GetFileName(item.ArchivePath);

            if (VerboseLog)
            {
                Log(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.RecursionLayerAttemptLogFormat,
                        layerLabel,
                        item.Depth,
                        Path.GetFileName(item.ArchivePath)));
            }

            foreach (string candidate in candidates)
            {
                if (attempts >= _limits.MaxPasswordAttemptsPerLayer)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                attempts++;
                triedAny = true;

                /*
                 * ===== 候选日志（用户 2026-09-27：「试了几个候选」必须看得见）=====
                 *
                 * **默认档就写**：单层路径那条同类日志本来就在候选循环里（每候选一行 INFO），
                 * 而递归路径过去**一条都没有** —— 真机那次 13 分钟走的正是递归路径，
                 * 日志里连"试了几个候选"都看不出来。两条路的口径必须一样。
                 *
                 * 措辞与单层路径**共用同一个格式常量**（StatusText.PasswordCandidateAttemptLogFormat），
                 * 来源说明也共用同一份描述器（DescribeCandidate ← PasswordService.BuildTryPasswordLogText）。
                 */
                int ordinal = attempts;
                string described = DescribeCandidate?.Invoke(candidate, ordinal)
                    ?? $"尝试密码候选第 {ordinal} 项：******";

                Log(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PasswordCandidateAttemptLogFormat,
                        layerLabel,
                        ordinal,
                        layerCandidateLimit,
                        described));

                /*
                 * 第一道防线（不变量 4）：解压前先列目录，把危险条目名挑出来。
                 *
                 * 递归展开的内层包过去**完全绕过** Security 层（预检与落点校验都只接在单层 GUI 路径上），
                 * 于是一个带 `..\` 的内层包会被原样解开、产物落到目标根之外，而报告里一个字都不提。
                 * 这里按"当前这个密码候选"列一次目录：能列出来就顺手做预检，
                 * 列不出来（加密头 -mhe、损坏）**不拦** —— 拦下来会让正常包也解不开，
                 * 那道兜底是解压后的落点校验。
                 */
                (string? unsafeSummary, ArchiveListResult? listedThisCandidate) =
                    await CheckEntriesBeforeExtractAsync(
                            item.ArchivePath,
                            candidate,
                            cancellationToken)
                        .ConfigureAwait(false);

                /*
                 * 本层的清单（L3 的预期）**只认真正解开的那一个候选**：加密头包用错密码时
                 * 引擎也可能回一份残缺/空清单，拿它当预期会把"解得好好的"判成不完整。
                 * 于是每个候选各记一份，等这次解压成功（下面的 `result.Success`）才采信。
                 */
                candidateManifest = listedThisCandidate is { Success: true }
                    ? LayerManifest.From(listedThisCandidate)
                    : LayerManifest.Unavailable(LayerManifest.DescribeListFailure(listedThisCandidate));

                if (unsafeSummary != null)
                {
                    return LayerOutcome.Stop(
                        BuildLayerReport(item, lastFailure, succeededPassword: null, unsafeSummary),
                        RecursionStopReason.UnsafeEntry);
                }

                /*
                 * ===== 每换一个候选，先把这一层的产物目录清空（用户 2026-09-25 真机铁证）=====
                 *
                 * 与 ExtractionCoordinator 候选循环里同一处修复（那边有完整现场说明）：
                 * 7z 用**错密码**时也会先在输出目录里建出 0 字节的桩文件，然后才报 Wrong password；
                 * 而本层的 options 是 `SkipExisting`（`-aos`）—— 那是为了"归档内有重复条目时保留先落地的那个"，
                 * 结果下一个候选会被这些桩文件**跳过**、直接报成功，正确的密码反而永远没机会解一次。
                 *
                 * ⚠ 清的只是**这一层在工作区里的产物目录**（我们自己造的），绝不碰源文件、也绝不碰最终输出目录；
                 * 清不掉（被占用 / 权限）时不抛：引擎那边由结果校验兜底（校验不过就不算成功）。
                 */
                TryResetLayerOutputDirectory(item.Layer.OutputPath);

                ArchiveOperationResult result = await _engine.ExtractAsync(
                        new ArchiveRequest
                        {
                            ArchivePath = item.ArchivePath,
                            OutputPath = item.Layer.OutputPath,
                            Password = candidate,

                            /*
                             * 进度与"长时间无响应"提示（2026-09-22 补，本批之前递归内层完全没有进度）。
                             *
                             * 只**转发**，不在这里做任何加工：节流在引擎层（ArchiveProgressReporter，
                             * 最多 250ms 一条），落地在协调器的 TaskProgressSink。
                             * 参数为 null 时引擎侧一个字都不报 —— 既有调用点（测试、CLI 式用法）行为不变。
                             */
                            Progress = progress,
                            Stalled = stalled
                        },
                        options,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (result.Success)
                {
                    succeededPassword = candidate;
                    success = result;
                    lastFailure = null;

                    // 这一层真正解开了 ⇒ 采信"解开它的那个候选"给出的清单（L3 的预期）。
                    layerManifest = candidateManifest;

                    /*
                     * 详细日志档：成功那一次的命令行与原话也写下来 ——
                     * 排查的人要拿"成功那次调了什么"去对照"失败那次差在哪"。
                     * 默认档一个字都不写（成功 = 一行摘要，第 44 条）。
                     */
                    if (VerboseLog)
                    {
                        EngineOutputLog.LogVerbose(Log, layerLabel, result);
                    }

                    break;
                }

                lastFailure = result;

                /*
                 * ===== 引擎原话落日志（用户 2026-09-27：「引擎原话从不落日志」）=====
                 *
                 * 放在 `lastFailure = result` 之后、所有 return / continue **之前**：
                 * 三条出路（损坏停下 / 错密码继续 / 其它停下）都要留下这一次的原话 ——
                 * 但**每个候选只留这一份**：默认档与详细档都走这里，出口只有
                 * <see cref="EngineOutputLog"/> 一个（与单层路径同一个），
                 * ⛔ 这里不许自己挑行、自己拼前缀。
                 *
                 * ⚠ 详细档的"参数摘要 + 原话"由下面的 LogVerbose 写（INFO）。
                 */
                if (VerboseLog)
                {
                    EngineOutputLog.LogVerbose(Log, layerLabel, result);
                }

                /*
                 * 默认档：**这个候选失败了**那一条（ERROR / WARN，一行）——
                 * 与单层路径的候选循环同一口径（不变量 6：失败必须留痕），
                 * 十个错候选就是十行，一行一个候选，读得出来"第几个候选开始不对"。
                 */
                if (!VerboseLog)
                {
                    EngineOutputLog.LogFailure(Log, layerLabel, result);
                }

                /*
                 * ===== 报错结论要取**走得最远的那一次**，不是最后一次（用户 2026-09-30 真机）=====
                 *
                 * 现场：`giu.7z.001`（7z `-mhe` 四卷）的候选循环里，**正确密码那一次**真解了 13 分钟、
                 * 写出 17.7 GiB 才失败；紧跟其后的几个错密码候选 50 毫秒就被 7-Zip 顶回来
                 * （`Cannot open encrypted archive. Wrong password?`）。收尾用的是 `lastFailure` ⇒
                 * 用户看到的是**最后那个错候选**的原话，真正那次失败（数据校验不过 / 解不出来）被吃掉，
                 * 结论永远是"密码错误：所有候选都试过了" —— 方向全错。
                 *
                 * 判据只用**事实**：这一次尝试在产物目录里留下了多少字节。留下过东西的那一次，
                 * 信息量必然大于"连门都没进去"的那些 ⇒ 记下**第一次**留下产物的失败，收尾优先用它
                 * （没有就退回 `lastFailure`，行为与以前逐字一致）。
                 */
                if (informativeFailure == null && ProducedBytesInLayerOutput(item) > 0)
                {
                    informativeFailure = result;
                }

                /*
                 * 损坏不再换候选重试（规则 1）：换密码对损坏的归档没有任何帮助，
                 * 只会把同一个损坏包重试 N 遍，既浪费时间又掩盖真正的问题。
                 */
                if (result.IsCorrupted)
                {
                    Log(
                        "WARN",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.CandidateStoppedByCorruptedLogFormat,
                            layerLabel));

                    return LayerOutcome.Stop(
                        BuildLayerReport(item, result, succeededPassword: null),
                        RecursionStopReason.Corrupted);
                }

                // 需要密码 / 密码错误 → 这个候选没用了，试下一个。
                if (result.IsWrongPassword || result.IsNeedPassword)
                {
                    Log(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.PasswordCandidateRejectedLogFormat,
                            layerLabel));

                    continue;
                }

                /*
                 * ===== 两义那一档（用户 2026-09-30 真机）=====
                 *
                 * 引擎自己说"密码可能不对、也可能数据坏了"（RAR 1.5–4.x 的 `-p` 包：退出码 3 +
                 * 「在加密文件 X 里校验和错误。文件已损坏或密码错误。」），**谁都不许单独定原因**：
                 * · 判成"已损坏"⇒ 当场停下、其余 10 个候选一个都不试（真机就是这么只试了第 1 个）；
                 * · 判成"密码错误"⇒ 把真损坏说成密码错（用户会去反复核对没写错的密码本）。
                 * 所以与"密码错误"同一处置：**继续试下一个候选**；到收尾时结论落"两义"那一档
                 * （<see cref="TaskOutcomeClassifier.TryResolveRecursionStop"/>），日志里也保留两种可能。
                 * 与 7-Zip 侧同一口径（`CRC Failed in encrypted file. Wrong password?` 那句从不当"已损坏"，
                 * 见 `Item37SafetyTests`）。
                 */
                if (result.IsPasswordOrCorrupted)
                {
                    ambiguousPasswordFailure = result;

                    Log(
                        "WARN",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.CandidatePasswordOrCorruptedLogFormat,
                            layerLabel));

                    continue;
                }

                // 其余（引擎不可用、路径问题、输出冲突…）换密码也解决不了，直接停。
                Log(
                    "ERROR",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.CandidateStoppedByEngineErrorLogFormat,
                        layerLabel,
                        result.Message));

                return LayerOutcome.Stop(
                    BuildLayerReport(item, result, succeededPassword: null),
                    MapEngineErrorToStopReason(result));
            }

            if (succeededPassword == null)
            {
                /*
                 * 收尾取"走得最远的那一次"的失败（`informativeFailure`，可能为 null）——
                 * 见上面那一大段说明：最后一次候选往往只是"连门都没进去"，它会把真原因盖掉。
                 *
                 * ⚠ 两义那一档**优先**（`ambiguousPasswordFailure`）：它是"引擎自己说两种可能都有"
                 * 那次的原话，比"最后一次错密码"信息量大得多，也是结论能同时保留两种可能的唯一来源。
                 */
                ArchiveOperationResult? conclusion =
                    ambiguousPasswordFailure ?? informativeFailure ?? lastFailure;

                RecursionLayerReport failureReport = BuildLayerReport(item, conclusion, succeededPassword: null);

                RecursionStopReason reason = ResolvePasswordStopReason(
                    candidates.Count,
                    attempts,
                    conclusion,
                    triedAny);

                return LayerOutcome.Stop(failureReport, reason);
            }

            (int fileCount, long outputSize) = OutputVerifier.Measure(item.Layer.OutputPath);

            /*
             * 第二道防线（不变量 4）：解压后校验真实落点。
             *
             * 外部引擎（7z.exe / UnRAR.exe）是独立进程，第一道预检挡不住它自己拼出来的落点
             * （符号链接条目、引擎自身的路径处理都在预检视野之外）。越界必须成为**失败结论**：
             * 不发布、不清理源包，报告里写清越界的是哪个产物、落在哪 —— 只写一行日志而结论仍是
             * "解压成功"，等于把不变量 4 降级成一条没人看的提示。
             *
             * ⚠ 措辞与单层路径（ExtractionCoordinator 的落点校验）**逐字对齐**：
             * 同一个口径在两条路径上各说一套，用户会以为是两件事。
             */
            string? landingViolation = FindLandingViolation(item.Layer.OutputPath);

            if (landingViolation != null)
            {
                var violatedReport = new RecursionLayerReport
                {
                    Depth = item.Depth,
                    ArchivePath = item.ArchivePath,
                    OutputPath = item.Layer.OutputPath,
                    Success = false,
                    Status = StatusText.ExtractFailed,
                    Message = "产物越出本层产物目录 —— 按既定口径整包判定失败（不归集产物、不处理源包、其余物不生成）：" +
                              landingViolation +
                              "。⚠ 解压是外部引擎进程写的盘，越界的那一次写入拦不住；本层产物留在工作区、未发布。",
                    InnerArchives = Array.Empty<string>(),
                    OutputFileCount = fileCount,
                    OutputSize = outputSize,
                    UsedPasswordMasked = PasswordMasker.Mask(succeededPassword)
                };

                return LayerOutcome.Stop(violatedReport, RecursionStopReason.UnsafeEntry);
            }

            IReadOnlyList<string> innerArchives = await ProbeInnerArchivesAsync(
                    item.Layer.OutputPath,
                    cancellationToken)
                .ConfigureAwait(false);

            var report = new RecursionLayerReport
            {
                Depth = item.Depth,
                ArchivePath = item.ArchivePath,
                OutputPath = item.Layer.OutputPath,
                Success = true,

                // 状态一律取引擎给的（成功时就是"解压成功"），不在这里另造一套口径。
                Status = string.IsNullOrWhiteSpace(success?.Status)
                    ? StatusText.ExtractSuccess
                    : success!.Status,
                Message = string.Empty,
                InnerArchives = innerArchives,
                OutputFileCount = fileCount,
                OutputSize = outputSize,
                Manifest = layerManifest,
                UsedPasswordMasked = PasswordMasker.Mask(succeededPassword)
            };

            return LayerOutcome.Ok(report);
        }

        /// <summary>
        /// 第一道防线：解压前预检条目名。
        /// 返回 null = 没有发现问题、或者**列不出目录**（加密头 / 损坏，这种情况下不拦）；
        /// 返回非空字符串 = 发现了危险条目，内容是可以直接给用户看的一句话。
        ///
        /// <para>回传的第二个元素是这一次列目录的原始结论 —— **同一份清单不许列两遍**：
        /// 检验等级 L3 要拿本层的清单当预期（见 <see cref="RecursionLayerReport.Manifest"/>），
        /// 而这里本来就已经问过引擎列了一次，⛔ 不许为了拿预期再列一次（那会多花一次全包扫描）。</para>
        /// </summary>
        /// <returns>
        /// 第一个元素 = 预检结论（null = 没发现问题 / 列不出目录）；
        /// 第二个元素 = 这一次的列目录原始结论（列不出来时为 null）。
        /// </returns>
        private async Task<(string? UnsafeSummary, ArchiveListResult? Listed)> CheckEntriesBeforeExtractAsync(
            string archivePath,
            string password,
            CancellationToken cancellationToken)
        {
            ArchiveListResult list;

            try
            {
                list = await _engine
                    .ListAsync(ArchiveRequest.For(archivePath, password), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // 引擎抛异常时按"列不出来"处理：真正的兜底是解压后的落点校验。
                return (null, null);
            }

            if (!list.Success)
            {
                return (null, list);
            }

            PathSafetyReport report = ArchivePathGuard.CheckEntries(list.Entries);

            return (report.IsSafe ? null : report.Summary, list);
        }

        /// <summary>
        /// 第二道防线：逐个核对产物真实落点是否都在本层产物目录之内。
        /// 返回 null = 都老实待在该待的地方；否则返回第一条越界说明（给用户看的）。
        ///
        /// 判两件事：
        /// ① 路径规范化后必须落在产物目录之内（`..` / 绝对路径 / 盘符这类一眼可见的越界）；
        /// ② 产物里出现**目录联接点 / 符号链接**也算越界 —— 它的名字在目录里，写进去的内容却在别处。
        ///    （`OutputVerifier.Measure` 出于"不跟随链接"的考虑会跳过它们，所以这一条只能在这里拦。）
        ///
        /// ⚠ <b>能力边界</b>：这个方法只看得见产物目录**里面**的东西。
        /// 绕过我们直接写到别的目录去的引擎行为它发现不了 —— 那种情况靠
        /// <see cref="CheckEntriesBeforeExtractAsync"/> 的第一道预检拦。两道一起才是不变量 4。
        /// </summary>
        internal static string? FindLandingViolation(string outputRoot)
        {
            if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
            {
                return null;
            }

            var pending = new Stack<string>();
            pending.Push(outputRoot);

            while (pending.Count > 0)
            {
                string current = pending.Pop();
                string[] entries;

                try
                {
                    entries = Directory.GetFileSystemEntries(current);
                }
                catch
                {
                    // 读不了就跳过；漏看一个目录只是少查一层，不构成"越界"结论。
                    continue;
                }

                foreach (string entry in entries)
                {
                    if (!ArchivePathGuard.IsInsideRoot(outputRoot, entry, out string reason))
                    {
                        return $"{entry}（{reason}）";
                    }

                    if (!TryGetAttributes(entry, out FileAttributes attributes))
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return $"{entry}（是目录联接点或符号链接：它在产物目录里的名字看不出真实落点，可能把内容写到目录之外）";
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 引擎给的错误分类 → 递归停因。
        ///
        /// 这里刻意**按引擎的分类枚举**（DetectedErrorType）而不是按中文状态串来判：
        /// 中文文案是给用户看的，随时可能改；用它做控制流会让"改一句提示"变成"改行为"。
        /// </summary>
        private static RecursionStopReason MapEngineErrorToStopReason(ArchiveOperationResult result)
        {
            return result.DetectedErrorType switch
            {
                "CorruptedArchive" => RecursionStopReason.Corrupted,
                "WrongPassword" => RecursionStopReason.WrongPassword,
                "NeedPassword" => RecursionStopReason.WrongPassword,
                "Cancelled" => RecursionStopReason.UserCancelled,
                "UnsafePath" => RecursionStopReason.UnsafeEntry,

                /*
                 * 写不下盘（引擎报磁盘空间不足）单独成一档：它与"引擎解不开"要用户做的事完全不同
                 * （清空间 / 换盘 vs 查包 / 查引擎）。判据只读引擎的结构化错误码，⛔ 不比中文。
                 */
                EngineErrorTypes.NoDiskSpace => RecursionStopReason.DiskSpaceInsufficient,

                _ => RecursionStopReason.EngineFailed
            };
        }

        /// <summary>
        /// 判定"全都没解开"时到底是密码错误还是达到尝试上限（规则 5 与规则 6 的分界）。
        /// </summary>
        private static RecursionStopReason ResolvePasswordStopReason(
            int candidateCount,
            int attempts,
            ArchiveOperationResult? lastFailure,
            bool triedAny)
        {
            if (lastFailure != null && lastFailure.IsCorrupted)
            {
                return RecursionStopReason.Corrupted;
            }

            /*
             * 两义那一档：引擎自己说"密码可能不对、也可能数据坏了"。
             * ⛔ 不许折成上面那一档（"已损坏"= 断言单一原因），也不许折成下面那些
             * "密码错误 / 达到上限"（那是反过来把真损坏说成密码错）——
             * 它就是它自己：结论与日志同时保留两种可能（与 7-Zip 侧同一口径）。
             */
            if (lastFailure != null && lastFailure.IsPasswordOrCorrupted)
            {
                return RecursionStopReason.PasswordOrCorrupted;
            }

            if (!triedAny)
            {
                /*
                 * 一个候选都没有：调用方的密码来源没给出任何东西。
                 * 这里归到 WrongPassword（"没有可用密码"），而不是 EngineFailed ——
                 * 对用户来说要做的动作是一样的（去补一个密码），而 EngineFailed 会把他引向查引擎，
                 * 那是错的方向。
                 */
                return RecursionStopReason.WrongPassword;
            }

            // 候选还有剩、却因为上限停手 —— 这才是"达到密码尝试上限"。
            if (candidateCount > attempts)
            {
                return RecursionStopReason.PasswordAttemptsExceeded;
            }

            // 候选全试完了都不对 —— 常规的"密码错误"。
            return RecursionStopReason.WrongPassword;
        }

        /// <summary>
        /// 探测本层产物里的内层归档（**递归**找所有文件，跳过 0 字节与工作区自身）。
        /// </summary>
        private async Task<IReadOnlyList<string>> ProbeInnerArchivesAsync(
            string outputDirectory,
            CancellationToken cancellationToken)
        {
            var found = new List<string>();
            var pending = new Stack<string>();

            pending.Push(outputDirectory);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string currentDirectory = pending.Pop();
                string[] entries;

                try
                {
                    entries = Directory.GetFileSystemEntries(currentDirectory);
                }
                catch
                {
                    // 单个目录读不了就跳过；漏判只是少展开一层，不该让整次递归失败。
                    continue;
                }

                foreach (string entry in entries)
                {
                    /*
                     * ⛔ 名字叫工作区目录的那一项**永远不是内层归档的候选**（用户 2026-09-30）。
                     *
                     * 工作区默认落在目标目录里面（<目标目录>\.ArchiveFixer.work），而本方法扫的是
                     * "这一层的产物目录" —— 万一哪条岔路上产物目录把工作区套了进去，里面的
                     * 抠取副本 / 逐层中间包**个个都能被识别成归档**，于是被接着解、被当内容物搬出去。
                     *
                     * ⚠ 这里**只按名字排，绝不按"在不在工作区根之下"排**：
                     * 本层的产物目录本身就在工作区里（<根>\recursive\<id>\layer-NNN\output），
                     * 用"根之下"那一条会把本层所有产物一次性全排掉 —— 递归当场变成"什么都没找到"
                     * （2026-09-30 自查实测：两个内层包全被跳过，多分支确认整条路都不再触发）。
                     */
                    if (WorkspaceTree.IsWorkspaceDirectoryName(entry))
                    {
                        continue;
                    }

                    if (!TryGetAttributes(entry, out FileAttributes attributes))
                    {
                        continue;
                    }

                    // 不跟随符号链接 / 联接点：跟着走既可能绕圈，也会把工作区外的文件算成"内层归档"。
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    if (IsZeroByteFile(entry))
                    {
                        // 0 字节文件不可能是归档，却能被某些"看后缀"的探测器误判，直接排除。
                        continue;
                    }

                    bool isArchive = await _prober
                        .IsArchiveAsync(entry, cancellationToken)
                        .ConfigureAwait(false);

                    if (isArchive)
                    {
                        found.Add(entry);
                    }
                }
            }

            // 排序让候选清单在 UI 与测试里都是稳定顺序（不同文件系统返回顺序不一致）。
            found.Sort(StringComparer.OrdinalIgnoreCase);

            return found;
        }

        /// <summary>
        /// 一层成功之后判断是否还要继续，以及继续哪些。
        /// 返回 false 表示停止，并通过 out 参数说明是"要问用户"还是"命中上限"。
        ///
        /// 判断顺序是固定的：模式（SingleLayer 直接收尾）→ 用户点名的清单过滤 → 数量上限 →
        /// 单链启发式 / 要不要问用户 → 层数上限后入队。
        /// 这个顺序本身就是规则（规则 4 与规则 5 的交界），改任何一步之前先读那一步的注释。
        /// </summary>
        private bool TryEnqueueNextLayers(
            WorkItem item,
            RecursionLayerReport report,
            ExtractionWorkspace workspace,
            RecursionMode mode,
            EnqueueState state,
            out RecursionDecisionRequest? decision,
            out RecursionStopReason stopReason)
        {
            decision = null;
            stopReason = RecursionStopReason.None;

            IReadOnlyList<string> innerArchives = report.InnerArchives;

            if (innerArchives.Count == 0)
            {
                // 这一层里没有可继续的东西，队列自然消耗干净；停因在循环结束时统一判定为 Completed。
                return true;
            }

            if (mode == RecursionMode.SingleLayer)
            {
                /*
                 * 只解当前这一层：里面的归档原样留着，用户想展开再单独发起。
                 *
                 * 这一条必须排在数量上限之前 —— 单层模式根本不继续，"里面有几个内层归档"对结论
                 * 没有任何影响，在这里报 TooManyInnerArchives 只会给出一个与用户选择无关的停因。
                 */
                return true;
            }

            List<string> toProcess = innerArchives.ToList();

            /*
             * previousDecision 的语义（规则 4）：只处理用户点名的那几个归档。
             * 按**文件名**比对（大小写不敏感，见 DecisionNames 的注释），**不重新全盘扫描** ——
             * 用户点头的是他看到的那个清单，不是"这一层里现在有什么"。
             */
            bool decidedByUser = item.DecisionNames != null;

            if (decidedByUser)
            {
                var chosen = new HashSet<string>(item.DecisionNames!, StringComparer.OrdinalIgnoreCase);

                toProcess = toProcess
                    .Where(path => chosen.Contains(Path.GetFileName(path)))
                    .ToList();
            }

            /*
             * 数量上限的位置是刻意的（规则 4 与规则 5 的交界）：
             * ① 放在"用户点名的清单"过滤**之后**：他点的就是他要的，先查上限会让"只选 3 个"
             *    也被拦下，与 previousDecision 的语义直接冲突；
             * ② 放在"要不要问用户"**之前**：一层里冒出几十个内层归档，说明这不是套娃包，
             *    而是一个归档集合 —— 该给的结论是 TooManyInnerArchives（去逐个单独处理），
             *    而不是把同一个事实包装成"要不要继续展开"再问一遍，那个问题对用户没有意义。
             */
            if (toProcess.Count > _limits.MaxInnerArchivesPerLayer)
            {
                state.UnexpandedCount = toProcess.Count;
                stopReason = RecursionStopReason.TooManyInnerArchives;
                return false;
            }

            if (!decidedByUser && mode == RecursionMode.SingleChain)
            {
                bool isSingleChain = toProcess.Count == 1 && HasOnlyInformationalSiblings(report, toProcess[0]);

                if (!isSingleChain)
                {
                    /*
                     * 多分支询问只在第 0 层发生。
                     * 理由：用户要回答的是"这个包里的分叉要不要都展开"，那是**源包层面**的问题；
                     * 一旦他选了"继续"，后面每一层再问一遍就成了骚扰（而且他也没法逐层判断）。
                     * 后续每层都仍受全部上限约束，安全阀并没有因此失效。
                     */
                    if (item.Depth > 0)
                    {
                        /*
                         * 更深的层里出现多分支：不再问第二遍，停在这一层。
                         *
                         * 但**不能悄悄停**：过去的写法是 return true 让流程自然收尾，
                         * 于是停因变成 Completed、产物照常发布、用户看到"已完成 N 层递归解压"，
                         * 而这一层里其实还有 K 个内层包没展开 —— 他以为拿到的是最终数据。
                         * 这里是明确的停因 + 明确的数量，Summary 里会写"第 N 层还有 K 个内层包未展开"。
                         */
                        state.UnexpandedCount = innerArchives.Count;
                        stopReason = RecursionStopReason.BranchNotExpanded;
                        return false;
                    }

                    decision = BuildDecision(item, innerArchives);
                    state.UnexpandedCount = innerArchives.Count;
                    return false;
                }
            }

            foreach (string innerArchivePath in toProcess)
            {
                /*
                 * 层数上限在入队前查（规则 5）：下一层已经到 MaxDepth 就不再入队，
                 * 已经排在前面的项照常解完 —— 那些产物是有效的，扔掉它们没有意义。
                 *
                 * 判据是**层号** item.Depth + 1，不是"已经解了几层"（或者队列长度）：
                 * 分支模式下同一层会排好几项，拿已解层数当层号会把合法的宽树误拦
                 * （两个分支、MaxDepth = 3 时，第二个分支会被算成"第 3 层"而拦掉）。
                 */
                if (item.Depth + 1 >= _limits.MaxDepth)
                {
                    state.UnexpandedCount = toProcess.Count;
                    stopReason = RecursionStopReason.MaxDepthReached;
                    return false;
                }

                state.Pending.Enqueue(new WorkItem
                {
                    Depth = item.Depth + 1,
                    ArchivePath = innerArchivePath,
                    Layer = workspace.CreateNextLayer(innerArchivePath),
                    IsRoot = false
                });
            }

            return true;
        }

        /// <summary>
        /// 单链判定：同层除内层归档外，其余文件必须都是说明类文件（.txt/.nfo/.url/.md/.sfv/.jpg/.png）。
        /// 只要冒出一个别的类型的文件，就说明这层产物本身就是"用户要的东西"，
        /// 里面那个归档未必是主角 —— 这种情况必须问，不能替用户决定。
        /// </summary>
        private static bool HasOnlyInformationalSiblings(RecursionLayerReport report, string innerArchivePath)
        {
            string outputRoot = SafePathHelper.GetFullPathSafe(report.OutputPath);
            string archivePath = SafePathHelper.GetFullPathSafe(innerArchivePath);

            if (outputRoot.Length == 0 || archivePath.Length == 0)
            {
                return false;
            }

            var pending = new Stack<string>();
            pending.Push(outputRoot);

            while (pending.Count > 0)
            {
                string currentDirectory = pending.Pop();
                string[] entries;

                try
                {
                    entries = Directory.GetFileSystemEntries(currentDirectory);
                }
                catch
                {
                    // 读不了就当作"有疑问"，走需要询问的一侧更安全。
                    return false;
                }

                foreach (string entry in entries)
                {
                    if (!TryGetAttributes(entry, out FileAttributes attributes))
                    {
                        return false;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        // 链接指到哪我们不知道；算作"非说明类"，宁可问用户。
                        return false;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    if (string.Equals(
                            SafePathHelper.GetFullPathSafe(entry),
                            archivePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!IsInformationalFile(entry))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsInformationalFile(string filePath)
        {
            string extension = Path.GetExtension(filePath);

            return InformationalExtensions.Any(
                known => string.Equals(known, extension, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 每次要继续之前检查的硬上限（规则 5）。这里的每一项命中都必须**停止**，没有例外。
        ///
        /// ⚠ 这里**不再**包含展开比：展开比要知道"这个包解压后多大"，那是一次引擎调用或一次磁盘遍历，
        /// 是异步的（见 <see cref="IsExpansionRatioExceededAsync"/>）。把一个 async 操作塞进同步判定，
        /// 只能靠 <c>GetAwaiter().GetResult()</c>，而那正是历史卡死的原因。调用方按顺序查完这里，再 await 展开比。
        /// </summary>
        private RecursionStopReason CheckLimitsBeforeLayer(
            WorkItem item,
            List<RecursionLayerReport> layers,
            int totalFiles,
            long totalSize)
        {
            /*
             * 层数：只解层号 < MaxDepth 的层（0 起算，所以 MaxDepth = 1 时第 0 层照解、
             * 第 1 层被拦下）—— 这与 AGENTS.md §10 M4 的"达到上限安全停下并报告"一致。
             *
             * 判据是**层号**（item.Depth），不是"已经解了几层"（layers.Count）：分支模式下同一层
             * 会排好几项，拿已解层数当层号会在合法的宽树上假报超限（AllBranches 展开 3 个以上分支时，
             * 第 3 项就会被当成"第 3 层"拦掉）。
             *
             * 这一关也是"入队判据写错"的兜底：每一项都要先过这里才会被解，所以入队处万一漏判，
             * 多出来的层只会停在队列里、以 MaxDepthReached 收场，不会被真的解开。
             */
            if (item.Depth >= _limits.MaxDepth)
            {
                return RecursionStopReason.MaxDepthReached;
            }

            if (totalFiles >= _limits.MaxTotalFiles)
            {
                return RecursionStopReason.MaxTotalFilesReached;
            }

            if (totalSize >= _limits.MaxTotalSize)
            {
                return RecursionStopReason.MaxTotalSizeReached;
            }

            return RecursionStopReason.None;
        }

        /// <summary>
        /// 单层展开比检查：本层归档解压后的总大小 / 该归档文件大小，超限即"疑似压缩炸弹"。
        /// 取不到可信数值（文件不在、list 失败、被压缩成 0 字节）时**不拦**：
        /// 拿不准的时候拦下来会把正常包也误伤，而真正的兜底是 MaxTotalSize / MaxTotalFiles 这两条硬线。
        ///
        /// ⚠ **全程 async，绝不同步等待**：这里要问引擎"解压后多大"（第 0 层）或遍历磁盘（内层），
        /// 在 UI 线程上同步等它就是死锁（7z 的续体要回到同一个被阻塞的线程）。
        /// </summary>
        private async Task<bool> IsExpansionRatioExceededAsync(
            WorkItem item,
            List<RecursionLayerReport> layers,
            Queue<WorkItem> pending)
        {
            long archiveSize = SafeFileLength(item.ArchivePath);

            if (archiveSize <= 0)
            {
                return false;
            }

            long uncompressedSize;

            if (item.IsRoot)
            {
                /*
                 * 只有第 0 层需要问引擎"解压后多大"：那时的归档还是用户给的原始文件，
                 * 磁盘上还没有任何产物可量。
                 */
                uncompressedSize = await GetRootUncompressedSizeAsync(item.ArchivePath).ConfigureAwait(false);
            }
            else
            {
                /*
                 * 内层归档已经在磁盘上了：它自己所在那一层的产物实测大小就是它的解压后大小，
                 * 比 list 更可信。
                 *
                 * 这里必须**定位到它自己那一层**。旧写法取 layers[^1]（最近完成的那一层），
                 * 而主循环是宽度优先的：同一层排了多个分支时，处理第二个分支时 layers[^1] 是
                 * **第一个分支**的产物。两个不相干的数相除，结果就是"随便一个正常小包被误报成
                 * 压缩炸弹并拒绝发布"（例：第 0 层解出 2 GB 文件 + 一个 1 MB 的内层 zip → 2000 倍）。
                 */
                uncompressedSize = ResolveLayerOutputSize(item.ArchivePath, layers, pending);
            }

            if (uncompressedSize <= 0)
            {
                return false;
            }

            return uncompressedSize / (double)archiveSize > _limits.MaxExpansionRatio;
        }

        /// <summary>
        /// 问引擎"第 0 层的包解压后多大"。失败按"拿不准"处理（返回 0 = 不拦）。
        /// </summary>
        private async Task<long> GetRootUncompressedSizeAsync(string archivePath)
        {
            try
            {
                ArchiveListResult list = await _engine
                    .ListAsync(ArchiveRequest.For(archivePath), CancellationToken.None)
                    .ConfigureAwait(false);

                return list.Success ? list.TotalUncompressedSize : 0;
            }
            catch
            {
                // 引擎抛异常时按"拿不准"处理，不拦。
                return 0;
            }
        }

        /// <summary>
        /// 找出"这个内层归档所属那一层"的产物总大小。
        ///
        /// 反查链：<see cref="WorkspaceLayer.Layers"/> 的每一层都记着 InputPath（它解的是哪个归档），
        /// 于是能唯一确定这个内层归档属于哪一层；该层的 OutputSize 就是它解压后的大小。
        /// 只有那一层已经解完（在 <paramref name="layers"/> 里有报告）时才拿得到。
        /// </summary>
        private long ResolveLayerOutputSize(
            string innerArchivePath,
            List<RecursionLayerReport> layers,
            Queue<WorkItem> pending)
        {
            string target = SafePathHelper.GetFullPathSafe(innerArchivePath);

            if (target.Length == 0)
            {
                return 0;
            }

            foreach (WorkItem queued in pending)
            {
                if (queued.Layer.InputPath.Length == 0)
                {
                    continue;
                }

                if (!string.Equals(
                        SafePathHelper.GetFullPathSafe(queued.Layer.InputPath),
                        target,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string outputPath = SafePathHelper.GetFullPathSafe(queued.Layer.OutputPath);

                RecursionLayerReport? owner = layers.FirstOrDefault(layer =>
                    string.Equals(
                        SafePathHelper.GetFullPathSafe(layer.OutputPath),
                        outputPath,
                        StringComparison.OrdinalIgnoreCase));

                if (owner != null)
                {
                    return owner.OutputSize;
                }

                // 那一层还没解（理论上到不了这里）：拿不准就不拦。
                return 0;
            }

            return 0;
        }

        /// <summary>
        /// 把上次的多分支选择变成"只处理这几个路径"的执行项。
        /// 单独一个方法，是为了让"用户选了什么"这件事在代码里只有一处解释。
        /// </summary>
        private static RecursionDecisionRequest BuildDecision(WorkItem item, IReadOnlyList<string> innerArchives)
        {
            return new RecursionDecisionRequest
            {
                CurrentArchivePath = item.ArchivePath,
                Depth = item.Depth,
                CandidateArchives = innerArchives,
                Prompt =
                    $"检测到 {innerArchives.Count} 个内层归档，预计最多产生 {innerArchives.Count} 个子任务，" +
                    "可能产生大量输出，是否继续？"
            };
        }

        private IReadOnlyList<string> BuildPasswordCandidates(string archivePath)
        {
            IReadOnlyList<string> provided;

            try
            {
                provided = _passwordProvider(archivePath) ?? Array.Empty<string>();
            }
            catch
            {
                // 密码来源出问题（密码本损坏等）不该让递归核心炸掉：当成"没有候选"处理，
                // 后面会以 WrongPassword 收场并如实报告。
                return Array.Empty<string>();
            }

            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string? candidate in provided)
            {
                if (candidate == null)
                {
                    continue;
                }

                // 去重：调用方的候选顺序里常常"最近成功的密码"和"密码本命中"是同一个值，
                // 重复试同一个密码只会白烧一次密码尝试次数。
                if (seen.Add(candidate))
                {
                    candidates.Add(candidate);
                }
            }

            return candidates;
        }

        /// <summary>
        /// 这一次尝试在**这一层的产物目录**里留下了多少字节。
        ///
        /// <para>用途只有一处：判定"哪一个候选的失败更值得当结论"—— 留下过产物的那一次必然比
        /// "连门都没进去"的那些更有信息量（用户 2026-09-30 真机：正确密码那次解了 13 分钟 /
        /// 17.7 GiB 才失败，却被后面 50 毫秒就失败的错候选盖掉）。</para>
        ///
        /// <para>读不到（目录不在 / 被占用 / 权限）一律返回 0：那只会让结论退回老口径
        /// （用最后一次失败），⛔ 不会把"什么都没留下"误判成"留下了东西"。</para>
        /// </summary>
        private static long ProducedBytesInLayerOutput(WorkItem item)
        {
            try
            {
                string path = item.Layer.OutputPath;

                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                {
                    return 0;
                }

                long total = 0;

                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch
                    {
                        // 量不出单个文件不影响"有没有留下东西"这个结论。
                    }
                }

                return total;
            }
            catch
            {
                return 0;
            }
        }

        private RecursionLayerReport BuildLayerReport(
            WorkItem item,
            ArchiveOperationResult? result,
            string? succeededPassword)
        {
            return BuildLayerReport(
                item,
                result,
                succeededPassword,
                PasswordMasker.Sanitize(result?.Message));
        }

        /// <summary>
        /// 带"失败原因覆盖"的层报告：预检/落点校验这类**不是引擎给出**的失败，
        /// 原因来自安全检查，必须原样写进报告（Message 会被 PasswordMasker 再洗一遍，防密码泄漏）。
        /// </summary>
        private RecursionLayerReport BuildLayerReport(
            WorkItem item,
            ArchiveOperationResult? result,
            string? succeededPassword,
            string? overrideMessage)
        {
            string message = overrideMessage
                ?? (result == null ? "引擎没有返回结果" : PasswordMasker.Sanitize(result.Message));

            return new RecursionLayerReport
            {
                Depth = item.Depth,
                ArchivePath = item.ArchivePath,
                OutputPath = item.Layer.OutputPath,
                Success = false,
                Status = result?.Status ?? StatusText.ExtractFailed,
                Message = PasswordMasker.Sanitize(message),
                InnerArchives = Array.Empty<string>(),
                OutputFileCount = 0,
                OutputSize = 0,

                // 失败层没有可信清单（它压根没解开）：L3 不会拿它当预期，原因如实写。
                Manifest = LayerManifest.Unavailable("这一层没有成功解压，没有可信清单"),
                UsedPasswordMasked = succeededPassword == null ? string.Empty : PasswordMasker.Mask(succeededPassword)
            };
        }

        private static ExtractionWorkspace CreateWorkspace(ArchiveTask task)
        {
            string sourceName = string.IsNullOrWhiteSpace(task.CurrentPath)
                ? "未命名"
                : FileNameHelper.GetArchiveBaseName(task.CurrentPath);

            /*
             * taskId = 包基名 + 时间戳 + 随机码。
             * 为什么不能只用包名：同一批任务里常有两个同名包（C:\t\a\pack.zip 与 C:\t\b\pack.zip），
             * 共用一个工作区目录会让第二次解压撞上第一次的产物。
             * 为什么不能只用时间戳：并发解压时两个任务可能落在同一毫秒。
             *
             * 这里**不需要**自己做文件名清洗：ExtractionWorkspace 构造时会再清洗一遍，
             * 而且它才是"路径安全"的唯一责任方（清洗两次不冲突，但不能靠这里兜底）。
             */
            string taskId = $"{sourceName}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";

            return new ExtractionWorkspace(WorkspaceRootDirectory, taskId);
        }

        /// <summary>
        /// 工作区根目录。
        /// 用系统临时目录而不是源目录旁边（AGENTS.md §6 第 12 条：中间产物不得写进源目录），
        /// 也不写死在某个盘符上（非目标：不写死盘符）。
        /// </summary>
        /// <summary>
        /// 递归工作区的根目录，由调用方在启动时指定（通常是 PathService.WorkDirectory）。
        /// null/空 = 回落到系统临时目录（只给单元测试用，正式流程不会走这一支）。
        /// </summary>
        public static string? ConfiguredWorkspaceRoot { get; set; }
        private static string WorkspaceRootDirectory
        {
            get
            {
                /*
                 * 工作区根目录由调用方指定（PathService.WorkDirectory = <程序目录>\data\work）。
                 *
                 * 原来这里写的是 Path.GetTempPath()，也就是 %TEMP% —— 那是 **C 盘**。
                 * 用户明确要求：缓存绝不能进 C 盘，绿色软件跟着安装位置走；
                 * 而且递归工作区动辄几百 MB，塞系统盘既占空间又拖慢整机。
                 * 没指定时（例如单元测试直接 new）才回落到临时目录。
                 */
                string? configuredRoot = ConfiguredWorkspaceRoot;

                string root = string.IsNullOrWhiteSpace(configuredRoot)
                    ? Path.Combine(Path.GetTempPath(), "ArchiveFixer", "recursive")
                    : Path.Combine(configuredRoot, "recursive");

                // 工作区那棵树一律带隐藏属性（用户 2026-09-30 诉求③：单看着看不出来）。
                WorkspaceTree.EnsureHiddenDirectory(root);

                return root;
            }
        }

        /// <summary>
        /// 组装最终结论。
        ///
        /// 这里集中处理三件容易搞混的事（规则 6/7/8）：
        /// ① <see cref="RecursionResult.Completed"/> 只在真正走完时为 true；
        /// ② 部分完成时产物**留在工作区**、Summary 里必须写清工作区在哪；
        /// ③ 全完成时才发布，且发布失败要如实说出来，不能因为"层都解完了"就假装成功。
        ///
        /// ⚠ 工作区清理**不在这里**：本方法只负责给出结论（删东西要看结论 + 取消令牌，
        /// 见 <see cref="FinalizeRun"/>）。发布失败时它会把 Completed 降成 false，
        /// 那个 false 正是"产物还压在工作区里、绝不能删"的判据。
        /// </summary>
        private RecursionResult BuildResult(
            RecursionStopReason stopReason,
            List<RecursionLayerReport> layers,
            RecursionDecisionRequest? decision,
            ExtractionWorkspace workspace,
            string finalOutputDirectory,
            string extraMessage,
            bool published,
            int unexpandedCount = 0)
        {
            bool completed = stopReason == RecursionStopReason.Completed;
            bool partiallyCompleted = !completed && layers.Any(layer => layer.Success);

            /*
             * 一层都没解出来时指向工作区目录本身：那些情形（取消、第一次就失败）下产物还没有，
             * 但"工作区在哪"正是排查与重试最需要的信息，而这个目录在工作区建出来时就一定存在。
             */
            string finalOutputPath = workspace.Layers.Count > 0
                ? workspace.Layers[^1].OutputPath
                : workspace.TaskDirectory;

            string publishMessage = string.Empty;

            if (published)
            {
                /*
                 * ⛔ 展开了内层包 ⇒ 走「**就地替换**」那一档（用户 2026-09-30 中午）：
                 * 每个被解开的内层包在**它原来的位置**留下一个以它命名的文件夹
                 * （<c>AAA\DDDD\内容物</c>、<c>AAA\BBBB\CCCCC\内容物</c>），真文件原地不动 ——
                 * ⛔ 不再"把所有叶子层产物摊到发布目标根上"（那等于把它们都搬到顶层）。
                 * 「中间层省不省」读同一个出口；**首层与末层不受它影响**。
                 *
                 * 判据读**唯一**那个出口（PackageLayerRules），本方法自己数层数会与定稿侧漂移。
                 * 只解了一层（没有内层包）时照旧摊外壳：那一份口径有很多既有用例钉着，一个字都不改。
                 */
                bool inPlace = PackageLayerRules.ExpandedInnerPackage(layers);

                WorkspacePublishResult publishResult = workspace.Publish(
                    finalOutputDirectory,
                    inPlaceInnerPackages: inPlace,
                    omitMiddlePackageLayers: inPlace && OmitMiddlePackageLayers);

                publishMessage = publishResult.Message;

                if (publishResult.Success)
                {
                    // 只有真的发出去了才改指向：发布失败时 FinalOutputPath 仍指向工作区里的产物，
                    // 用户按这个路径还能找到东西。
                    finalOutputPath = publishResult.DestinationPath;
                }
                else
                {
                    completed = false;
                    partiallyCompleted = true;
                }
            }

            WriteRecoveryReport(stopReason, layers, workspace, finalOutputPath, completed, partiallyCompleted);

            return new RecursionResult
            {
                StopReason = stopReason,
                Completed = completed,
                PartiallyCompleted = partiallyCompleted,
                Decision = decision,
                Layers = layers.ToList(),
                FinalOutputPath = finalOutputPath,
                Summary = BuildSummary(
                    stopReason,
                    layers,
                    workspace,
                    finalOutputPath,
                    completed,
                    publishMessage,
                    extraMessage,
                    unexpandedCount)
            };
        }

        /// <summary>
        /// 一次运行的收尾：把结论交回调用方之前，按结论决定要不要清掉**本次运行自己建的那个**工作区。
        ///
        /// 只有"真的走完 + 没被取消"才清（AGENTS.md §6 第 13 条与单层路径
        /// <c>ExtractionCoordinator.CleanupTaskWorkspaceDirectory</c> 同一口径）：
        /// · <see cref="RecursionResult.Completed"/> 已经蕴含"产物发布成功"（发布失败时
        ///   <see cref="BuildResult"/> 会把它降成 false），所以这一条同时覆盖"产物已出去"这个前提；
        /// · 取消可能在最后一层解完之后才到（队列已经空了），那时结论仍是 Completed ——
        ///   按规则**不许清**，所以要在这里再看一眼令牌；
        /// · 失败 / 部分完成 / 等用户决定一律保留：那些目录里的中间产物是用户唯一的线索，
        ///   "失败能定位到层、能从那一层重试"这条能力本身就依赖它们还在（见类注释）。
        ///
        /// 为什么这条线放在本类而不是调用方：工作区是本类建出来的，谁来建谁负责收 ——
        /// 调用方（协调器）只拿到一个结论对象，它无从知道这次到底建了哪个目录。
        /// </summary>
        private RecursionResult FinalizeRun(
            RecursionResult result,
            ExtractionWorkspace workspace,
            string taskLabel,
            CancellationToken cancellationToken)
        {
            if (result.Completed && !cancellationToken.IsCancellationRequested)
            {
                CleanupWorkspace(workspace, taskLabel, "递归解压已完成且产物已发布");
                CleanupSupersededWorkspaces(taskLabel);
            }

            return result;
        }

        /// <summary>
        /// 由调用方在**接管了本次运行的结论**之后调用：把最近一次运行留下的工作区按"任务成功"清掉。
        ///
        /// 目前只有一个地方用得上（<c>ExtractionCoordinator</c> 的"用户选择只保留当前这一层"）：
        /// 递归核心是以 NeedsDecision 收的尾，按规则**没有**清工作区；而调用方随后把第 0 层产物
        /// 取回暂存目录、把结论改写成 Completed —— 那份工作区（含 report.json）从此是纯垃圾，
        /// 留着还会在下次启动时被算进"未完成的工作区"报告。
        ///
        /// ⚠ 调用方必须先确认**产物已经不在工作区里**（拿走之后）再调；否则这就是在丢用户的产物。
        /// 只认本实例持有的那一个目录，不做任何目录扫描。
        /// </summary>
        /// <returns>工作区确实不在了（本次删掉 / 本来就不在）返回 true。</returns>
        public bool TryCleanupCurrentWorkspace(string taskLabel, string reason)
        {
            ExtractionWorkspace? workspace = CurrentWorkspace;

            if (workspace == null)
            {
                return false;
            }

            return CleanupWorkspace(workspace, taskLabel, reason);
        }

        /// <summary>
        /// 失败 / 取消 / 部分完成收尾时，按用户设置（<c>AppSettings.KeepFailedWorkspace</c>，默认关）
        /// 清掉**本次运行自己建的**那个工作区（用户 2026-09-25 第 25 条：失败不留残留）。
        ///
        /// <para>它是逐层产物的落点，双层包里最占地方的一份（"解压 40G，两层，解压失败有 80G"）。
        /// 与 <see cref="TryCleanupCurrentWorkspace"/> 的区别只有"为什么清"与日志措辞：
        /// 那一条是**结论被调用方改写**时才调（"用户选择只保留当前一层"），
        /// 这一条是**整单没成功**时调 —— 调用方要判两件事才轮到它：这一单确实没成功、
        /// 而且用户没有打开「失败时保留中间产物」（判断在
        /// <c>ExtractionCoordinator.CleanupFailedTaskWorkspace</c> 里，一处收口）。</para>
        ///
        /// <para>安全口径与 <see cref="CleanupWorkspace"/> 完全一致：容器内校验先做一遍
        /// （必须在工作区根目录之下）、<see cref="ExtractionWorkspace.Cleanup"/> 内部再独立校验一遍、
        /// 只认本实例持有的那一个目录（绝不按目录名 / "最新目录"去扫 <c>work\recursive</c> ——
        /// 并发跑两个递归任务时，扫描式删除会把对方正在写的工作区端掉）、
        /// 删不掉只写 WARN（绝不改任务结论）。</para>
        ///
        /// <para>工作区本来就不在（成功那一支已经清过 / 这次没建到工作区）时**一个字都不写**：
        /// 那种情况下没有任何"删了什么"需要交代，多写一行只会让日志变噪声。</para>
        /// </summary>
        /// <returns>工作区确实不在了（本次删掉 / 本来就不在）返回 true。</returns>
        public bool TryDiscardCurrentWorkspaceOnFailure(string taskLabel)
        {
            ExtractionWorkspace? workspace = CurrentWorkspace;

            if (workspace == null)
            {
                return false;
            }

            if (!Directory.Exists(workspace.TaskDirectory))
            {
                return true;
            }

            if (!ArchivePathGuard.IsInsideRoot(workspace.RootDirectory, workspace.TaskDirectory, out string guardReason))
            {
                Log(
                    "WARN",
                    $"{taskLabel}：工作区不在工作区根目录之下，已跳过清理 —— {guardReason}：{workspace.TaskDirectory}");

                return false;
            }

            // 删除是**不可逆**的：动手之前先把"删什么、为什么、多大"写进日志（同 CleanupWorkspace）。
            (int fileCount, long totalSize) = OutputVerifier.Measure(workspace.TaskDirectory);

            Log(
                "INFO",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.WorkspaceClearedOnFailureFormat,
                    taskLabel,
                    fileCount,
                    TaskSpaceEstimate.FormatSize(totalSize),
                    workspace.TaskDirectory));

            WorkspaceCleanupResult cleanup = workspace.Cleanup();

            // 删成了是 INFO，没删成（占用 / 权限 / 越界）是 WARN，而 Cleanup 的消息里已经带上了路径。
            if (!cleanup.Cleaned)
            {
                Log("WARN", $"{taskLabel}：{cleanup.Message}");
            }

            return cleanup.Cleaned;
        }

        /// <summary>
        /// 清理**本实例持有的那个**工作区目录。
        ///
        /// 安全性（这是删目录，每一条都写死在这里）：
        /// · 删的是本实例手上的那个工作区对象（本次运行开头建出来的那一个），**不按目录名、
        ///   也不按"最新目录"去扫** <c>data\work\recursive</c> —— 并发跑两个递归任务时，
        ///   扫描式删除会把对方正在写的工作区端掉；
        /// · 容器内校验先在这里做一遍（必须在工作区根目录之下），
        ///   <see cref="ExtractionWorkspace.Cleanup"/> 内部还会再独立校验一遍，越界时一个字节都不动；
        /// · 删不掉只写 WARN：清工作区失败绝不该让**已经成功**的任务变成失败
        ///   （与 AGENTS.md §6 第 9 条同一精神）。
        ///
        /// 生产环境里工作区根目录是 <c>&lt;程序目录&gt;\data\work\recursive</c>
        /// （<see cref="ConfiguredWorkspaceRoot"/> 由 MainViewModel 设成 PathService.WorkDirectory），
        /// 所以"在工作区根之下"同时就是"在 data\work 之下"。
        /// </summary>
        private bool CleanupWorkspace(ExtractionWorkspace workspace, string taskLabel, string reason)
        {
            if (!ArchivePathGuard.IsInsideRoot(workspace.RootDirectory, workspace.TaskDirectory, out string guardReason))
            {
                // 越界＝工作区的身份本身可疑：一个字节都不动，但必须留下证据（这是失败，不是静默跳过）。
                Log(
                    "WARN",
                    $"{taskLabel}：工作区不在工作区根目录之下，已跳过清理 —— {guardReason}：{workspace.TaskDirectory}");

                return false;
            }

            // 删除是**不可逆**的：动手之前先把"删什么、为什么、多大"写进日志（AGENTS.md §9.5 同一要求）。
            (int fileCount, long totalSize) = OutputVerifier.Measure(workspace.TaskDirectory);

            Log(
                "INFO",
                $"{taskLabel}：{reason}，清理本次任务的工作区（{fileCount} 个文件 / {totalSize} 字节）：{workspace.TaskDirectory}");

            WorkspaceCleanupResult cleanup = workspace.Cleanup();

            // 删成了是 INFO，没删成（占用 / 权限 / 越界）是 WARN —— 两种都要留证据。
            Log(cleanup.Cleaned ? "INFO" : "WARN", $"{taskLabel}：{cleanup.Message}");

            return cleanup.Cleaned;
        }

        /// <summary>
        /// 清理"上一次运行留下、已被本次续跑取代"的工作区（多分支询问 → 用户确认继续这一条路）。
        ///
        /// 为什么它必须跟着一起清：用户点"继续"之后，续跑会把第 0 层在**新工作区**里重解一遍，
        /// 上一次那份产物因此变成纯垃圾 —— 而它往往就是几百 MB。它当时没被清是对的
        /// （"等用户决定"属于部分完成，规则 7 要求保留）；现在用户已经决定、整条递归也真的走完了，
        /// 那个理由不再成立，两份都留着才是泄漏。
        ///
        /// 只在本次**成功**时清（失败 / 取消 / 部分完成时两份都留着），而且只认"同一个归档的续跑"
        /// （判据在 <see cref="ExtractAsync"/> 里）—— 免得调用方拿同一个实例去跑另一个任务时误删。
        /// </summary>
        private void CleanupSupersededWorkspaces(string taskLabel)
        {
            if (_supersededWorkspaces.Count == 0)
            {
                return;
            }

            // 先清账再动手：万一清理过程里抛了（它自己不抛），也不会在下次成功时重复删同一个目录。
            ExtractionWorkspace[] superseded = _supersededWorkspaces.ToArray();
            _supersededWorkspaces.Clear();

            foreach (ExtractionWorkspace workspace in superseded)
            {
                CleanupWorkspace(workspace, taskLabel, "上一次运行（等待用户决定多分支）的工作区已被本次续跑取代");
            }
        }

        /// <summary>
        /// 写一条日志。没有日志出口时（单元测试直接 new）什么也不做；
        /// 日志出口自己抛异常时也吞掉 —— 写日志失败绝不该影响任务结论。
        /// </summary>
        private void Log(string level, string message)
        {
            try
            {
                _log?.Invoke(level, message);
            }
            catch
            {
                // 见方法注释。
            }
        }

        private static string BuildSummary(
            RecursionStopReason stopReason,
            List<RecursionLayerReport> layers,
            ExtractionWorkspace workspace,
            string finalOutputPath,
            bool completed,
            string publishMessage,
            string extraMessage,
            int unexpandedCount)
        {
            int done = layers.Count(layer => layer.Success);

            string reason = DescribeStopReason(stopReason);

            string head = stopReason switch
            {
                RecursionStopReason.Completed => $"已完成 {done} 层递归解压（{reason}）",
                RecursionStopReason.NeedsDecision => $"已完成 {done} 层，停在第 {done + 1} 层，原因：{reason}",
                // "源文件已变化"时一层都没解是**常态**（拦在第 0 层开工之前），
                // 所以它不走下面那句"第 1 层就没能解开" —— 那句话会让人以为解压失败。
                RecursionStopReason.SourceChanged => $"未开始解压，原因：{reason}",
                _ => done > 0
                    ? $"已完成 {done} 层，停在第 {done + 1} 层，原因：{reason}"
                    : $"第 1 层就没能解开，原因：{reason}"
            };

            var parts = new List<string> { head };

            /*
             * "还有多少内层包没展开"必须写出来（不变量 8）。
             * 只报"已完成"而把没展开的分支咽下去，用户会以为这就是最终数据 ——
             * 这与"部分成功不得显示为成功"是同一类问题。
             */
            if (unexpandedCount > 0)
            {
                parts.Add($"该层还有 {unexpandedCount} 个内层包未展开，需要时可对它们单独发起解压");
            }

            if (completed && !string.IsNullOrWhiteSpace(finalOutputPath))
            {
                parts.Add($"产物：{finalOutputPath}");
            }

            if (!string.IsNullOrWhiteSpace(publishMessage))
            {
                parts.Add(publishMessage);
            }

            /*
             * 没有发布就一定要写明工作区位置（规则 7）：部分完成、取消、等待用户决定这三种情形下
             * 产物都还留在工作区里，用户按这个路径就能看到已经解出来的部分（需求书 §十二：
             * 递归停止后必须保存"工作区位置"）。
             *
             * 判据用 !completed 而不是 partiallyCompleted：取消时可能一层都还没解出来
             * （partiallyCompleted 为 false），但"工作区在哪"同样是用户最需要的信息。
             */
            if (!completed)
            {
                parts.Add($"未发布的产物留在工作区：{workspace.TaskDirectory}");
            }

            if (!string.IsNullOrWhiteSpace(extraMessage))
            {
                parts.Add(extraMessage);
            }

            return string.Join("；", parts);
        }

        private static string DescribeStopReason(RecursionStopReason stopReason)
        {
            return stopReason switch
            {
                RecursionStopReason.Completed => "没有更多内层归档",
                RecursionStopReason.NeedsDecision => "检测到多个内层归档，等待用户决定是否继续展开",
                RecursionStopReason.BranchNotExpanded => "更深的层里还有多个内层归档未展开（多分支默认不展开）",
                RecursionStopReason.MaxDepthReached => "已达到最大递归层数",
                RecursionStopReason.MaxTotalFilesReached => "已达到累计输出文件数上限",
                RecursionStopReason.MaxTotalSizeReached => "已达到累计输出总大小上限",
                RecursionStopReason.ExpansionRatioExceeded => "单层展开比超限，疑似压缩炸弹",
                RecursionStopReason.TooManyInnerArchives => "本层内层归档数量超过上限",
                RecursionStopReason.PasswordAttemptsExceeded => "已达到密码尝试次数上限（候选还有剩余）",
                RecursionStopReason.WrongPassword => "密码错误：所有候选都试过了",
                RecursionStopReason.Corrupted => "压缩包损坏",
                RecursionStopReason.UnsafeEntry => "归档内存在不安全路径，已拒绝解压",
                // 不变量 11：这一条**不是"解不开"**，而是"手上这份识别结果已经不对应这个文件了"。
                // 措辞必须让用户知道该做什么（重新扫描），而不是去怀疑包坏了或换个引擎。
                RecursionStopReason.SourceChanged => "源文件已变化：识别结果作废，本层没有开始解压（引擎未被调用）",
                RecursionStopReason.UserCancelled => "用户取消",
                RecursionStopReason.EngineFailed => "引擎操作失败",
                _ => "未知原因"
            };
        }

        /// <summary>
        /// 写 report.json（给"崩溃后可恢复"用）。
        /// 报告里**只有**结构和数量：路径、层号、状态、计数、脱敏后的密码标记。
        /// 密码策略在这里只是**声明**（"每层最多试 N 个候选"），不含任何明文。
        /// </summary>
        private void WriteRecoveryReport(
            RecursionStopReason stopReason,
            List<RecursionLayerReport> layers,
            ExtractionWorkspace workspace,
            string finalOutputPath,
            bool completed,
            bool partiallyCompleted)
        {
            var report = new
            {
                SchemaVersion = 1,
                TaskDirectory = workspace.TaskDirectory,
                RootDirectory = workspace.RootDirectory,
                StopReason = stopReason.ToString(),
                Completed = completed,
                PartiallyCompleted = partiallyCompleted,
                FinalOutputPath = finalOutputPath,
                Engine = new
                {
                    Id = _engine.Id,
                    Version = _engine.Version
                },
                Limits = new
                {
                    _limits.MaxDepth,
                    _limits.MaxTotalFiles,
                    _limits.MaxTotalSize,
                    _limits.MaxExpansionRatio,
                    _limits.MaxInnerArchivesPerLayer,
                    _limits.MaxPasswordAttemptsPerLayer
                },
                PasswordPolicy = "每层按调用方给的候选顺序逐个尝试；报告不记录任何密码内容",
                Layers = layers.Select(layer => new
                {
                    layer.Depth,
                    layer.ArchivePath,
                    layer.OutputPath,
                    layer.Success,
                    layer.Status,
                    layer.Message,
                    layer.OutputFileCount,
                    layer.OutputSize,
                    layer.UsedPasswordMasked,
                    InnerArchives = layer.InnerArchives.ToArray()
                }).ToArray()
            };

            workspace.WriteReport(report);
        }

        private static bool IsZeroByteFile(string path)
        {
            try
            {
                return new FileInfo(path).Length == 0;
            }
            catch
            {
                // 读不到大小就当作非 0，交给探测器去判断（宁可多问一次，也别漏掉真的归档）。
                return false;
            }
        }

        private static long SafeFileLength(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return 0;
                }

                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        private static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch
            {
                attributes = default;
                return false;
            }
        }

        /// <summary>
        /// 入队这一步的可变状态。用一个对象而不是三个 out 参数，
        /// 是为了让"这一层还有几个内层包没展开"在每条提前返回的分支上都必须显式写一次 ——
        /// 忘了写就是 0，而 0 会让 Summary 少掉那句提示，所以每处都写清楚。
        /// </summary>
        private sealed class EnqueueState
        {
            public Queue<WorkItem> Pending { get; init; } = new();

            /// <summary>本层没有入队的内层归档数量（停因是"多分支不展开"时才有意义）。</summary>
            public int UnexpandedCount { get; set; }
        }

        /// <summary>队列里的一项：一个待解的归档 + 它在第几层 + 它的工作区目录。</summary>
        private sealed class WorkItem
        {
            public int Depth { get; init; }

            public string ArchivePath { get; init; } = string.Empty;

            public WorkspaceLayer Layer { get; init; } = new WorkspaceLayer();

            /// <summary>是否是任务自己的那个包（第 0 层）。</summary>
            public bool IsRoot { get; init; }

            /// <summary>
            /// 非 null 表示"只处理用户点名的这几个归档"。
            ///
            /// 存的是**文件名**而不是完整路径：决策里带的是上一次运行的工作区路径，本次续跑新建工作区后
            /// 前缀必然不同，按完整路径比对会把用户的选择全部判成"没了"。层内部的文件名是唯一稳定的定位键；
            /// 同一层里出现同名归档时两个都会被处理 —— 这是刻意的取舍：宁可多解一个同名分支，
            /// 也不能因为工作区换了就把用户点过的分支静默丢掉。
            ///
            /// 仅在 previousDecision 非 null 时落在第 0 层，后续层不继承（用户回答的是源包层面的问题）。
            /// </summary>
            public IReadOnlyList<string>? DecisionNames { get; init; }
        }

        /// <summary>一层的执行结果：要么成功（带报告，可以继续），要么停下（带报告 + 停因）。</summary>
        private sealed class LayerOutcome
        {
            public RecursionLayerReport? Report { get; private init; }

            public RecursionStopReason StopReason { get; private init; }

            public static LayerOutcome Ok(RecursionLayerReport report)
            {
                return new LayerOutcome { Report = report, StopReason = RecursionStopReason.None };
            }

            public static LayerOutcome Stop(RecursionLayerReport report, RecursionStopReason reason)
            {
                return new LayerOutcome { Report = report, StopReason = reason };
            }
        }
    }
}

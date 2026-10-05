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
    /// 递归展开策略（设计.md §十–§十四、AGENTS.md §6 第 8 条：硬上限一条都不能少）。
    ///
    /// <para>⚠ 2026-10-04（用户当场推翻旧口径）：出厂默认 = <see cref="AllBranches"/> ——
    /// "有压缩包就解压"。旧口径的"多分支默认不展开、必须问"只留在
    /// <see cref="SingleChain"/> 这一档里（用户显式选了它才生效）。</para>
    /// </summary>
    public enum RecursionMode
    {
        /// <summary>只解当前这一层，不看里面还有什么。</summary>
        SingleLayer,

        /// <summary>
        /// 只跟一条链：一层里**只有 1 个真归档**时才自动继续（旁边是什么文件都不影响）；
        /// 出现 **≥2 个**真归档就停下来等用户拍板，绝不替他决定。
        /// </summary>
        SingleChain,

        /// <summary>展开所有内层归档（**出厂默认**，用户 2026-10-04 拍板），仍受全部上限约束。</summary>
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
        /// 单个条目**解压后**的字节上限（默认 64 GiB —— 与⑥设置「安全上限」里那一格的默认值同一口径）。
        ///
        /// <para>为什么要单列一条（2026-10-05 只读审计）：递归这条路（出厂默认档 = 展开所有分支）
        /// 过去**一处都没引用 ResourceBudget**，于是"单文件"这道闸门在默认档下等于不存在 ——
        /// 一个含 200 GiB 单文件的包既不会被拦、事后报的还不是这一条。
        /// 判据与文案都转调解压前预算那一份（<see cref="Security.ResourceBudget.DescribeSingleFileOverLimit"/>），
        /// ⛔ 不许在这里再拼一句。</para>
        /// </summary>
        public long MaxSingleFileSize { get; init; } = 64L * 1024 * 1024 * 1024;

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

        /// <summary>
        /// **本层清单的逐条形状**（相对路径 + 解压后字节），部分完成发布（<see cref="PartialPublishPlanner"/>）
        /// 要靠它逐条对账。
        ///
        /// <para>为什么要单独带一份：<see cref="Manifest"/> 只有两个总数（条目数 / 总字节），
        /// 而"哪些文件敢发布"必须逐条回答（缺的 / 大小不符的 / 被引擎点名的）。取的是
        /// **这一层列目录成功的那一次**（<see cref="CheckEntriesBeforeExtractAsync"/> 的原始结论）——
        /// 密码不对时候选连目录都列不出来，那一档自然就是空表 ⇒ 判"没有清单" ⇒ 什么都不发布。</para>
        /// </summary>
        public IReadOnlyList<(string Path, long Size)> ManifestEntries { get; init; } =
            Array.Empty<(string, long)>();

        /// <summary>这一层失败时**引擎点名**的坏条目（点不出名时是空集合，见 <c>ArchiveOperationResult.FailedEntryNames</c>）。</summary>
        public IReadOnlyList<string> FailedEntryNames { get; init; } = Array.Empty<string>();

        /// <summary>这一层失败时引擎**自报**的出错条目数（闸门：比点得出名的多 ⇒ 一个字节都不发布）。</summary>
        public int ReportedSubItemErrors { get; init; }

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

        /// <summary>
        /// 本层清单里最大的那个条目超过单文件上限（不变量 8 的"单文件"那一条）。
        ///
        /// <para>与 <see cref="MaxTotalSizeReached"/> 同一档处置：**不是"包坏了"**，而是撞上了程序的安全上限，
        /// 用户要动的是⑥设置「安全上限」（或先看看这个包本身是不是有问题）。</para>
        /// </summary>
        MaxSingleFileSizeReached,

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

        /// <summary>
        /// **文件名已加密**（7z `-mhe` / RAR `-hp`）：这一层从头到尾**就没列出过清单**，
        /// 而引擎在列目录那一步说的是"加密头"（用户 2026-10-05 拍板：递归路也要与单层路同结论）。
        ///
        /// <para>为什么必须与 <see cref="WrongPassword"/> 分开：用户要做的动作完全不同 ——
        /// 密码错误是"去核对 / 补密码本"，而这一档是"**先给它一个密码，它才肯把内容清单给你看**"。
        /// 判据与单层路径同一套（列目录失败 + 引擎给的结构化错误类型是
        /// <see cref="EngineErrorTypes.EncryptedHeaders"/>，⛔ 不比中文）；
        /// ⛔ 只要**任何一个候选成功列出过清单**，这一档就不成立（那时失败在解压阶段，
        /// 结论照旧走密码错误 / 两义 / 损坏）。</para>
        /// </summary>
        EncryptedHeaders,

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

        /// <summary>
        /// **分卷缺失（免试那一档）**：这一组凑不齐，所以**一次引擎调用都没做**（用户 2026-10-05 拍板：
        /// 「要不然你在分开了你还会继续解压单独的001」）。
        ///
        /// <para>与 <see cref="EngineFailed"/> 分开的理由：用户要做的动作完全不同 ——
        /// EngineFailed 指向"查包 / 换引擎"，而这一档是"把同一组分卷凑到同一个目录里再来"。
        /// 判据只有一条硬证据（7z 起始头自述的整包字节数对不上），见
        /// <c>VolumeNameRepair.ResolveCrossLayerVolumeGather</c>；⛔ 其他族本轮不做这一档。</para>
        /// </summary>
        MissingVolume,

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

        /// <summary>
        /// 停下来时**这一层还有哪几个内层归档没展开**（完整路径；空 = 没有未展开的分支）。
        ///
        /// <para>为什么要带上名单（用户 2026-10-04 真机）：过去只有个数，Summary 里也只写个数 ——
        /// 用户看不出是哪个包没展开，只能自己去工作区里翻。调用方（协调器的「两条出路」那一条）
        /// 也读这一份，⛔ 不许自己再数一遍。</para>
        /// </summary>
        public IReadOnlyList<string> UnexpandedNames { get; init; } = Array.Empty<string>();

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

        /// <summary>
        /// 「该层还有哪几个内层包没展开」最多点几个名字（多出来的折成"…还有 K 个"）。
        ///
        /// <para>与「本次内容物」「定稿没能搬运的明细」同一个数字：**前 5 个足够定性**，
        /// 一层里几十个内层包时全列出来只会把摘要撑爆（用户 2026-09-25 第 44 条）。</para>
        /// </summary>
        private const int MaxUnexpandedNameLines = 5;

        /// <summary>
        /// 密码探针目录名（建在这一层产物目录**之外**，⛔ 不许混进产物）。
        ///
        /// <para><c>internal</c>：清工作区前的第二道容器内校验要认它（<see cref="WorkspaceCleanupGuard"/>），
        /// ⛔ 名字只能在这里写一次。</para>
        /// </summary>
        internal const string ProbeDirectoryName = "_密码预检";

        /// <summary>
        /// 内层"双面文件"抠出来的副本放在这一层的这个兄弟目录里（<c>layer-XXX\carved\</c>）。
        ///
        /// <para>⛔ 与 <see cref="ProbeDirectoryName"/> 同一个道理：**绝不放产物目录**
        /// （产物目录里的东西要参与结果校验与发布，一个字节都不能混进去）。</para>
        /// </summary>
        internal const string CarveDirectoryName = "carved";

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
        /// **「内容物保留关键词」判据**（用户 2026-10-04 的新功能「内容物压缩文件不解压」）。
        ///
        /// <para>命中关键词的内层归档**不当内层归档**：不展开、也不改名（还原工序那一步也不碰它），
        /// 它就当一件普通内容物留在结果里。判据唯一出口 <see cref="ContentKeepRules"/>
        /// （包含即命中、大小写不敏感、⛔ 不做通配 / 正则）。</para>
        ///
        /// <para>由调用方按设置当场赋值（与 <see cref="OmitMiddlePackageLayers"/>、<see cref="RecursionLimits"/>
        /// 同一个理由：改完设置不重启也要生效）。默认 <see cref="ContentKeepRules.Empty"/> =
        /// 一个关键词都没有 = 行为与以前**逐字相同**。</para>
        /// </summary>
        public ContentKeepRules KeepRules { get; set; } = ContentKeepRules.Empty;

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
        /// ⚠ **只在第 0 层**（用户给的源包）开工之前问一次："这一层要解的那个源文件还是原来那一份吗？"
        /// 返回非 null = 已经拦下，本层不解、整条递归停下，那句话就是这次的结论。
        /// 实现上的闸门就是主循环里那句 <c>item.IsRoot &amp;&amp; _sourceCheck != null</c>。
        /// </para>
        /// <para>
        /// 为什么第 1 层起不问：那几层解的是**我们自己产出的过程物**（工作区里的内层包），
        /// 它们本来就不在源包快照里 —— 拿源包的快照去比只会得出一句必然错误的结论
        /// （判据与快照都在协调器，见 <c>ExtractionCoordinator.CheckRootSourceUnchangedAsync</c>）。
        /// </para>
        /// <para>
        /// 为什么由调用方注入而不是递归核心自己判：快照挂在 <see cref="ArchiveTask"/> 上、
        /// 判据与状态落法都在协调器（同一句话要同时出现在任务状态、失败清单与日志里）。
        /// 递归核心只知道"要解哪个归档"，它不认识快照，也不该认识 ——
        /// 与引擎 / 探测器 / 密码来源全部注入是同一个理由。
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
        /// 「停止后续」的信号（可空）。为真 = 用户想让一切都停下来，**不是**取消当前任务。
        ///
        /// <para>为什么要这个口子（2026-10-05 只读审计）：单层路径在候选之间看 <c>IsStopping</c>
        /// （语义：正在解的那一次不打断，下一个密码不再试），而递归这条路**拿不到这个信号** ——
        /// 用户点了「停止后续」，这一层剩下的候选照样一个一个试完。</para>
        ///
        /// <para>为什么是**可选属性**、不是构造参数：测试里有 20 多处直接 <c>new RecursiveExtractor(...)</c>，
        /// 加参数会把它们全部改一遍；属性不传 = 行为与从前逐字相同。由调用方注入（协调器接
        /// <c>() =&gt; IsStopping</c>），与 <see cref="VerboseLog"/> 同一套注入方式：
        /// 递归核心不认识 GUI，也不该认识。</para>
        /// </summary>
        internal Func<bool>? StopRequested { get; set; }

        /// <summary>
        /// 「可疑条目提示」的**回传口**（可空）：本层清单里若有可执行 / 脚本类条目，
        /// 这里收到那句话（非空），由调用方写进任务字段与日志。
        ///
        /// <para>为什么要回传而不是本类自己写（2026-10-05 只读审计）：那条提示的**判据与文案**
        /// 只有一处（<c>ExtractionCoordinator.AnalyzeDangerousEntries</c>，internal static），
        /// 写进哪个字段也只有一处（<c>PublishDangerousEntriesHint</c>）—— 递归层只管"这一层的清单里有"，
        /// ⛔ 不复制第二份判据、也不自己决定显示在哪。</para>
        /// </summary>
        internal Action<string>? DangerousEntriesReported { get; set; }

        /// <summary>
        /// 要不要统计可疑条目（⑥设置 →「可疑条目提示」，默认开）。
        ///
        /// <para>注入方式照 <see cref="VerboseLog"/>：由调用方按设置当场赋值（改完设置不重启也要生效）。
        /// 关掉时判据返回空串 ⇒ 一个字段都不写、一行日志都不打，与加这条之前逐字相同。</para>
        /// </summary>
        public bool ReportDangerousEntries { get; set; }

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

            /*
             * 第 1 层起停下来时要能说清"还有**哪几个**内层归档没展开"（不变量 8：多分支默认不展开，
             * 必须问；用户 2026-10-04 真机：只报个数时他看不出是哪一个）。
             */
            IReadOnlyList<string> unexpandedNames = Array.Empty<string>();

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
                        Pending = pending
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
                        unexpandedNames = enqueueState.UnexpandedNames.ToList();

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
                        unexpandedNames),
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
                        unexpandedNames),
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
                        unexpandedNames),
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
            Action<ArchiveStallNotice>? stalled = null,
            string? carvedSource = null)
        {
            /*
             * ===== 这一层到底交给引擎哪一个文件（2026-10-04 真机，用户报"密码是对的怎么解压不了"）=====
             *
             * 现场：`HK.7z.001` 第 0 层解出 3 个 4K 视频（7.11 GiB，全部正常），第 1 层探到
             * `4K (11)_2.mp4` 是内层包 → 直接把**原文件**交给 7-Zip → 7-Zip 回
             * `Cannot open the file as archive` → 整条链判"部分完成" ⇒ 已解出的 7.11 GiB 一个字节都不发布、
             * 工作区整份被清掉（14 分钟白跑）。
             *
             * 根因：那个 mp4 是**双面文件**（真视频 + 尾部一整个 ZIP，用户资源包里的常见形态）。
             * 7-Zip 只在"前面垫的数据 ≤ 8 MiB"时才容忍这种整体偏移，超过就报上面那句原话；
             * 单层路径早就接了这一档（先按偏移抠出来再解），**递归这条路一直没接** ——
             * 而 2026-10-04 起出厂默认档正是"展开所有分支"，于是这条路成了默认路径。
             *
             * 修法：本方法多一个"改用哪一份"的参数。正常传 null（照旧用 <see cref="WorkItem.ArchivePath"/>）；
             * 引擎回过"这不是归档"、而它又确实是双面文件时，外层用
             * <see cref="TryCarveEmbeddedInnerArchiveAsync"/> 抠出副本，再带这个参数重跑一次本方法。
             * ⛔ 抠出来的只是**副本**，原文件一个字节都不动；⛔ 判不出是不是双面文件 ⇒ 什么都不做（照旧失败）。
             */
            string archivePath = string.IsNullOrWhiteSpace(carvedSource) ? item.ArchivePath : carvedSource!;

            var options = new ExtractOptions
            {
                // 每层都是全新目录，正常情况下不会撞名；万一撞上（归档内有重复条目）
                // 一律"跳过 + 保留先落地的那个"，绝不覆盖：改名属于改名模块的事，递归核心不越权。
                OverwriteMode = "SkipExisting",
                KeepArchiveNameFolder = false
            };

            options.Normalize();

            /*
             * ===== 每一层开头先报一帧"本层 0%"（用户 2026-10-02 真机）=====
             *
             * 现场：`P.7z.001` 第 0 层到 100% 之后，界面**停在 100% 一动不动**，
             * 直到 4 分 34 秒后整条链才结束 —— 那 4 分半其实是在解内层包（`P.ra` → 2735 个文件）。
             * 用户的原话是"解压完成到真正的解释有个 8 分钟的差距，这个会比较影响用户使用"。
             *
             * 成因：进度条记的是"引擎最后一次报的百分比"，而第 0 层结束时它是 100%；
             * 第 1 层从头开始时没有任何一帧把基准拉回来，于是那一层跑多久、界面就停在 100% 多久。
             * 所以每一层进来先合成一帧 Percent = 0（当前条目换成本层的说明）：
             * 进度条从这一层重新起算，日志里也会多一行「进度 0%（当前：第 N 层 …）」。
             * ⛔ 只补这一帧，引擎报的进度一个数都不改。
             */
            progress?.Report(new ArchiveProgress
            {
                Percent = 0,
                CurrentEntry = $"第 {item.Depth} 层：{Path.GetFileName(item.ArchivePath)}"
            });

            /*
             * ===== 双面文件 + 尾部只有一个原样存的条目 ⇒ 直接取那一段（2026-10-05 真机第六批）=====
             *
             * ⛔ 挂点在**引擎列不出清单之后**（见候选循环里那一段），不在这里先试：
             * 只有"引擎读不动这一份"才轮得到近路 —— 反例当场就有（真机同形夹具的外层 7z：
             * 它内部**存着**那些 mp4 的尾部 ZIP，`EmbeddedArchiveDetector` 会在它的数据流里
             * 找到一个自洽的 ZIP，先试近路就会"抢在引擎前面"把外层 7z 当成双面文件、
             * 只取出那一个条目并判这一层成功）。
             */

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
             * 本层"最近一次列目录成功的逐条清单"（部分完成发布的输入，见 ManifestEntries）。
             * 只记**成功**的那一次：列不出来（密码不对 / 加密头）时盘上那份清单根本不存在，
             * 记一份空的/残缺的只会让下游把"缺得太多"当成"包坏了"。
             */
            IReadOnlyList<(string Path, long Size)> lastListedEntries = Array.Empty<(string, long)>();

            /*
             * 「可疑条目提示」每层只回传一次（见下面那一段的说明）：它是"这个包里有什么"，
             * 与用哪个密码打开无关，逐候选重报只会把日志刷成噪声。
             */
            bool dangerousEntriesHintReported = false;

            /*
             * 「引擎读不动 + 双面文件 + 单条目原样存 ⇒ 直接取那一段」这条近路，**这一层只试一次**
             * （挂点在候选循环里"引擎列不出清单"那一刻；试不成 ⇒ 照旧走老路，不逐候选重试）。
             */
            bool attemptedDirectTake = false;

            /*
             * 这一层的最多候选数：**与循环用的同一个上限**（`_limits.MaxPasswordAttemptsPerLayer`）。
             * 先算出来是为了让"候选 i/N"里的 N 与真正会试的个数一致 ——
             * 写成 candidates.Count 会在被上限截断时给出一个永远到不了的 N。
             */
            int layerCandidateLimit = Math.Min(candidates.Count, _limits.MaxPasswordAttemptsPerLayer);

            /*
             * 「加密包不试空密码」要用到的一个事实（用户 2026-10-04 真机）：
             * 这一层**除空密码以外还有没有候选可试**（一个都不剩时不许跳 —— 与单层路径同一条边界，
             * 见 `ExtractionCoordinator` 的 `skippedOnlyEmptyPasswordBecauseEncrypted`）。
             */
            int usableCandidates = candidates.Count(candidate => !string.IsNullOrEmpty(candidate));

            /*
             * 这一层**跳过**了几个候选（"整包已加密" / "文件名已加密"两档：它们一个字节都没解）。
             *
             * <para>为什么要单独记一笔（与单层路径同一条口径）：收尾的
             * <see cref="ResolvePasswordStopReason"/> 是拿"候选总数 vs 试过几个"判断
             * "是不是被每层上限截断了"的，而被跳过的那几个**根本没试** ——
             * 不把它们从总数里减掉，就会把"候选全试完了都不对"误报成
             * 「达到密码尝试上限（候选还有剩余）」。单层路径那边是"先 RemoveAll 再重算
             * maxPasswordAttempts"（见 <c>ExtractionCoordinator</c> 候选循环里那一段），
             * 同一个意思。</para>
             */
            int skippedCandidates = 0;

            /*
             * ===== 这一层"到底有没有列出过清单"（用户 2026-10-05 拍板的那半条）=====
             *
             * `-mhe`（7z）/ `-hp`（RAR）的包**每个候选都列不出来**，引擎在列目录那一步就说"加密头"。
             * 这是「文件名已加密」唯一的证据来源（与单层路径同一个判据）：
             * ① 从头到尾**一次都没列成功**；② 引擎说的确实是加密头（结构化错误类型，⛔ 不比中文）；
             * ③ 没出现过**别的**列目录错误 —— 三条同时成立才认（证据混了 / 判不出 ⇒ 退回既有口径）。
             *
             * ⛔ 只要任何一个候选成功列出过清单，这一档就不成立：那时失败发生在**解压**阶段，
             * 结论照旧走密码错误 / 两义 / 损坏 —— 别把"密码试错了但清单列得出来"的普通加密包说成它。
             */
            bool listedAnyCandidate = false;
            bool sawEncryptedHeadersListFailure = false;
            bool sawOtherListFailure = false;

            /* 引擎在列目录那一步的原话（加密头那一档要带进结论里，与单层路径同一句文案）。 */
            string encryptedHeadersListMessage = string.Empty;

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

            /*
             * ===== 跨层收卷 + 凑不齐就别试（用户 2026-10-05 真机）=====
             *
             * 现场：三卷 7z 的三片分别躺在**三个不同的层产物目录**里（`layer-001\output\HK.7z.002`、
             * `layer-002\output\HK.7z.003`、`layer-003\output\HK.7z.001`）。引擎找兄弟卷**只看入口文件
             * 旁边那一层** ⇒ 第 2 层拿 `HK.7z.001` 单独去解必然报
             * `Open ERROR: Cannot open the file as [7z] archive` ⇒ 整条链判「部分完成」、
             * 工作区 7 个文件 / 13.48 GiB 整份删掉、什么都没发布。用户原话：
             * 「各分卷在不同的目录，你就将其全部移动到头文件 .001 同级目录里面去」、
             * 「要不然你在分开了你还会继续解压单独的001」。
             *
             * <para>挂点为什么在**这里**（而不是 `ProbeInnerArchivesAsync` 的「还原」工序之后）：
             * ① 收卷要的是"**到解这一层时**兄弟卷都齐了"—— 三片由三个同深度的分支各自产出，
             * 谁先谁后由队列决定，在"产出"那一刻收会漏掉后面才产出 / 才被抠出来的那几片；
             * 解这一层是**唯一**能确定"该在的都在了"的时刻。
             * ② "凑不齐就别试"本来就只能在**调用引擎之前**判（这里正是引擎调用之前、候选循环之外）。
             * ③ 「还原」工序那一挂点照旧不动：它管的是本层产物的名字，与收卷互不干扰。</para>
             *
             * <para>⛔ 判据与执行体都不在这里：整件事转调 `VolumeNameRepair.ResolveCrossLayerVolumeGather`
             * （纯计划）+ `VolumeNameRepair.TryApply`（全成或全不成、绝不覆盖、失败倒序回滚）。
             * ⛔ 候选池只有两处：**入口自己那一层**（只读）+ **这条链的各层产物目录的直接子文件**
             * （`CurrentWorkspace.Layers`，⛔ 不递归、⛔ 不含 `carved` 这种我们自造的兄弟目录、
             * ⛔ 绝不碰用户源目录）；跨盘一律不收（`BuildPlanFromOrder` 里判）。</para>
             */
            if (!TryGatherCrossLayerVolumes(item, archivePath, layerLabel, out string? missingVolumeMessage))
            {
                /*
                 * 这一组凑不齐、而且有硬证据 ⇒ **一次引擎调用都不做**，如实报"缺哪几片"
                 * （不变量 7）。⛔ 绝不让它落到「引擎操作失败」：引擎压根没被调用过。
                 */
                Log("WARN", missingVolumeMessage!);

                return LayerOutcome.Stop(
                    BuildLayerReport(
                        item,
                        result: null,
                        succeededPassword: null,
                        overrideMessage: missingVolumeMessage,
                        manifestEntries: null,
                        overrideStatus: StatusText.VolumeMissing),
                    RecursionStopReason.MissingVolume);
            }

            foreach (string candidate in candidates)
            {
                if (attempts >= _limits.MaxPasswordAttemptsPerLayer)
                {
                    break;
                }

                /*
                 * ===== 「停止后续」在候选之间生效（2026-10-05 只读审计）=====
                 *
                 * 单层路径 2026-09-27 就修掉了这一档（用户原话：「我都暂停了，你还在尝试新的密码」），
                 * 判据是"正在解的那一个不打断、下一个密码一个都不再试"；可递归这条路**拿不到那个信号**，
                 * 于是用户点了「停止后续」，这一层剩下的候选照样一个一个试完（大包就是几十分钟）。
                 *
                 * 位置刻意在 `attempts` 递增**之前**、与单层路径同一个落点（候选之间）：
                 * 已经试过至少一个候选（`attempts > 0`）才看信号 —— 一个都没试就停会让收场落到
                 * `triedAny == false`（"没有可用密码"），那是对用户的误报。
                 *
                 * ⛔ 停下之后按"候选还有、只是不再试"收尾（`attempts < candidates.Count` ⇒
                 * 「达到密码尝试上限」那一档），**不许报成密码错误** —— 候选根本没试完。
                 */
                if (attempts > 0 && StopRequested?.Invoke() == true)
                {
                    Log(
                        "WARN",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.RecursionStoppedByStopRequestLogFormat,
                            layerLabel,
                            Math.Max(0, layerCandidateLimit - attempts)));

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
                            archivePath,
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

                /*
                 * ===== 引擎读不动它 + 它确实是双面文件 ⇒ 直接按偏移取出那唯一一个原样存的条目 =====
                 *
                 * 真机现场（`ArchiveFixer-本次操作_20261005_134118.txt`）：第 1 层三个双面视频，
                 * 每个都先白试一次引擎（`Cannot open the file as archive`，写盘的那一次解压，
                 * 日志里留下三条假 ERROR），再把尾部归档整份抠成副本（78/48/83 秒），
                 * 然后让 7-Zip 从副本里**再解一遍**那唯一一个条目（88/48/92 秒）—— 同一份字节写了两遍、读了两遍。
                 *
                 * 判据三条缺一不可：① 引擎**列不出**这一份（列得出来就说明引擎本来读得动它 ⇒ 照旧走原路，
                 * ⛔ 近路绝不抢在引擎前面）；② 尾部确实有一个自洽的内嵌归档（识别阶段同一个检测器）；
                 * ③ 里面**恰好一个原样存的条目**（见 <see cref="TryTakeSingleStoredEntryAsync"/>）。
                 * 判不出来 ⇒ 一个字都不改，照旧走今天这条路。
                 */
                if (carvedSource == null
                    && !attemptedDirectTake
                    && listedThisCandidate is { Success: false })
                {
                    attemptedDirectTake = true;

                    LayerOutcome? taken = await TryTakeSingleStoredEntryAsync(item, progress, cancellationToken)
                        .ConfigureAwait(false);

                    if (taken != null)
                    {
                        return taken;
                    }
                }

                if (listedThisCandidate is { Success: true })
                {
                    listedAnyCandidate = true;

                    lastListedEntries = PartialPublishPlanner.ToManifestEntries(listedThisCandidate);

                    /*
                     * ===== 可疑条目提示（2026-10-05 只读审计）=====
                     *
                     * 单层路径在源包预检之后就会统计"可执行 / 脚本类条目"并写进任务字段
                     * （`ExtractionCoordinator.AnalyzeDangerousEntries` + `PublishDangerousEntriesHint`），
                     * 而递归这条路**一次都不做** —— 出厂默认档（展开所有分支）下，内层包里的
                     * `.exe / .bat / .ps1` 一个提示都没有。
                     *
                     * ⛔ 判据与文案仍只有那一处（转调同一个 static，不复制第二份）；
                     * 本类只负责"这一层的清单里有"，显示在哪由调用方决定（<see cref="DangerousEntriesReported"/>）。
                     *
                     * 每层只回传一次：同一份清单每个候选都列得出来，逐候选重报只会把日志刷成噪声
                     * （提示说的是这个包有什么，与用哪个密码打开无关）。
                     */
                    if (!dangerousEntriesHintReported)
                    {
                        dangerousEntriesHintReported = true;

                        DangerousEntriesReported?.Invoke(
                            ViewModels.ExtractionCoordinator.AnalyzeDangerousEntries(
                                listedThisCandidate.Entries,
                                ReportDangerousEntries));
                    }
                }
                else if (string.Equals(
                    listedThisCandidate?.ErrorType,
                    EngineErrorTypes.EncryptedHeaders,
                    StringComparison.OrdinalIgnoreCase))
                {
                    // 这一次列目录失败是"加密头"：它是「文件名已加密」那一档的证据（见上面的说明）。
                    sawEncryptedHeadersListFailure = true;
                    encryptedHeadersListMessage = listedThisCandidate?.Message ?? string.Empty;
                }
                else
                {
                    /*
                     * 别的列目录失败（引擎抛异常时为 null、损坏、超时…）：证据混了 ⇒ 那一档不成立，
                     * 结论退回既有的密码口径（"判不出就什么都不改"）。
                     */
                    sawOtherListFailure = true;
                }

                /*
                 * ===== 加密包不试空密码（用户 2026-10-04 真机；**唯一判据** = PasswordProbe）=====
                 *
                 * 现场：真机日志「第 0 层：第6集.zip：开始解压，密码候选 1/10，尝试空密码」——
                 * 一个**已加密**的包先拿空密码白跑一整包。单层路径 2026-09-25 就修掉了这一档
                 * （`PasswordProbe.ShouldSkipEmptyPassword`），可**递归这条路没接**：
                 * 一键处理里内层包走的正是递归，于是同一件事在两条路上长成两种行为。
                 *
                 * 判据只读引擎给的事实（清单里有没有加密条目），⛔ 不比中文、也不自己猜；
                 * 边界与单层路径**逐字相同**：还有别的候选可试才跳（一个都不剩时照旧试空密码，
                 * 否则收场会落到一句更难懂的"未知解压失败"）。
                 *
                 * 账目：这一档**没解过任何东西**，所以不占尝试次数（撤掉 `attempts` 那一格）、
                 * 也不算"试过了"（`triedAny` 跟着退回去），候选总数 N 也跟着减 1 ——
                 * 否则日志里的「候选 i/N」会给出一个永远到不了的 N。
                 */
                if (string.IsNullOrEmpty(candidate)
                    && usableCandidates > 0
                    && PasswordProbe.ShouldSkipEmptyPassword(listedThisCandidate))
                {
                    attempts--;
                    triedAny = attempts > 0;
                    layerCandidateLimit = Math.Max(1, layerCandidateLimit - 1);
                    skippedCandidates++;

                    Log(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.RecursionSkippedEmptyPasswordLogFormat,
                            layerLabel,
                            Math.Min(usableCandidates, _limits.MaxPasswordAttemptsPerLayer)));

                    continue;
                }

                /*
                 * ===== 加密头包也不试空密码（2026-10-05 只读审计；**只补"跳过"这一半**）=====
                 *
                 * 上一条靠"清单里的加密条目标记"判（列得出来才能判），而**文件名也加密**
                 * （7z `-mhe=on` / RAR `-hp`）时**清单根本列不出来** —— 引擎在列目录这一步就报
                 * "加密头"（结构化结论 <see cref="EngineErrorTypes.EncryptedHeaders"/>）。单层路径
                 * 2026-09-26 就补了这一档（`ExtractionCoordinator` 里的 `lastListErrorType ==
                 * SevenZipOutputParser.EncryptedHeadersErrorType`），递归这条路一直没接：
                 * 出厂默认档下，一个 `-mhe` 的内层包先拿空密码白跑一整包。
                 *
                 * 判据只读引擎给的结构化错误类型（⛔ 不比中文、也不自己猜）；记账口径与上一条
                 * **逐字相同**：没解过任何东西 ⇒ 不占尝试次数、不算"试过了"、候选总数 N 减 1。
                 * 边界也相同：还有别的候选可试才跳（一个都不剩时照旧试空密码，
                 * 否则收场会落到一句更难懂的"未知解压失败"）。
                 *
                 * ⚠ **本层的最终结论仍是"密码错误"那一档**（不会变成「文件名已加密」）：把结论也改掉
                 * 属于新增状态，要同时改 StatusText / StatusToBrushConverter / TaskSummaryService /
                 * TaskOutcomeClassifier 四处，超出本次修复范围 —— 如实记在报告里，别当成做完了。
                 */
                if (string.IsNullOrEmpty(candidate)
                    && usableCandidates > 0
                    && listedThisCandidate is { Success: false }
                    && string.Equals(
                        listedThisCandidate.ErrorType,
                        EngineErrorTypes.EncryptedHeaders,
                        StringComparison.OrdinalIgnoreCase))
                {
                    attempts--;
                    triedAny = attempts > 0;
                    layerCandidateLimit = Math.Max(1, layerCandidateLimit - 1);
                    skippedCandidates++;

                    Log(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.RecursionSkippedEmptyPasswordEncryptedHeadersLogFormat,
                            layerLabel,
                            listedThisCandidate.ErrorType));

                    continue;
                }

                /*
                 * ===== 单文件上限：本层清单里最大的那个条目（2026-10-05 只读审计）=====
                 *
                 * 不变量 8 的四条"解压前"上限（单文件 / 总大小 / 文件数 / 展开比）里，递归这条路
                 * 过去只映了三条（见 `ExtractionCoordinator.BuildRecursionLimits`）——
                 * 出厂默认档下，一个含 200 GiB 单文件的包不会被这道闸门拦下，事后报的还不是这一条。
                 *
                 * 判据与文案**都转调解压前预算那一份**（<see cref="ResourceBudget.FindLargestFileEntry"/>
                 * / <see cref="ResourceBudget.DescribeSingleFileOverLimit"/>）：同一个上限在两条路上
                 * 必须用同一把尺子、同一句话（§9.5）。
                 *
                 * ⛔ 拿不到清单 ⇒ **不拦**（与 <see cref="IsExpansionRatioExceededAsync"/> 的兜底同口径）：
                 * 加密头包 / 列目录失败时盘上那份清单根本不存在，凭"不知道"去拦会把正常包误杀。
                 */
                if (listedThisCandidate is { Success: true })
                {
                    (string largestEntryPath, long largestEntrySize) =
                        ResourceBudget.FindLargestFileEntry(listedThisCandidate);

                    if (largestEntrySize > _limits.MaxSingleFileSize)
                    {
                        string singleFileReason = ResourceBudget.DescribeSingleFileOverLimit(
                            largestEntrySize,
                            largestEntryPath,
                            _limits.MaxSingleFileSize);

                        Log("ERROR", $"{layerLabel}：{singleFileReason}");

                        /*
                         * 报告里**不带这一层的逐条清单**：这一层一个字节都没解（拦在解压之前），
                         * 拿一份"预期清单"去对账等于给部分完成发布递一把没有产物的尺子。
                         */
                        return LayerOutcome.Stop(
                            BuildLayerReport(
                                item,
                                result: null,
                                succeededPassword: null,
                                overrideMessage: singleFileReason),
                            RecursionStopReason.MaxSingleFileSizeReached);
                    }
                }

                if (unsafeSummary != null)
                {
                    return LayerOutcome.Stop(
                        BuildLayerReport(item, lastFailure, succeededPassword: null, unsafeSummary),
                        RecursionStopReason.UnsafeEntry);
                }

                /*
                 * ===== 先只解最小的那个条目（探针），再解整包 =====
                 *
                 * 与单层路径**同一个出口**（<see cref="PasswordProbe"/>）：清单里那个最小的文件能解开
                 * = 这个候选是对的；解不开（引擎明确说密码不对）= 这个候选不对 ——
                 * 一个字节的整包数据都没动。挑不出探针、或引擎给不出确定结论 ⇒ 照旧解整包。
                 *
                 * 为什么递归这条路也必须接（用户 2026-10-04 真机）：递归里每个候选过去都是**一次完整解压**，
                 * 大包 + 多个候选 = 几十分钟白跑（单层路径 2026-09-25 修的就是这个）。
                 *
                 * 探针失败的结论**原样当成"这个候选不对"**（`lastFailure` 收那一份引擎结果）⇒
                 * 收尾的停因、两义那一档、失败清单的口径一个字都不用改。
                 */
                ArchiveOperationResult? probeFailure = await ProbeCandidateAsync(
                        item,
                        archivePath,
                        options,
                        listedThisCandidate,
                        candidate,
                        progress,
                        stalled,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (probeFailure != null)
                {
                    lastFailure = probeFailure;

                    Log(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.PasswordCandidateRejectedLogFormat,
                            layerLabel));

                    continue;
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
                            ArchivePath = archivePath,
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
                        BuildLayerReport(item, result, succeededPassword: null, manifestEntries: lastListedEntries),
                        RecursionStopReason.Corrupted);
                }

                /*
                 * ===== 「密码其实已经对了，坏的是数据」这一档（用户 2026-10-01 真机 `giu910`）=====
                 *
                 * 判据与单层路径（`ExtractionCoordinator` 的候选循环）**同一个出口**
                 * （<see cref="ProducedContentGate"/>）：这一趟在**这一层的产物目录**里真解出了多少东西。
                 *
                 * ⚠ 为什么必须补在这里：那一档修复（2026-10-01）先只接在**单层**那条路上，
                 * 而真机 `giu910` 走的**是这一条**（`SingleChain` 递归）—— 结果用户当晚重跑一次，
                 * 13 分钟、17.7 GiB 又白扔了一遍，报的还是「密码错误」（他原话：
                 * 「为什么试了密码之后再去试一次，我说过要试密码的话要在最开始的时候」）。
                 *
                 * 两义那一档（`IsPasswordOrCorrupted`）一并覆盖：只要这一趟**真解出了内容**，
                 * 两义就当场收敛成"数据坏了"——密码已经不需要再猜。
                 */
                if ((result.IsWrongPassword || result.IsNeedPassword || result.IsPasswordOrCorrupted)
                    && ProducedContentGate.TryMeasure(item.Layer.OutputPath, out int provenFiles, out long provenBytes))
                {
                    string provenMessage = string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.PasswordProvenDataCorruptedFormat,
                            provenFiles,
                            provenBytes)
                        + "（引擎原话：" + PasswordMasker.Sanitize(result.Message) + "）";

                    Log("WARN", $"{layerLabel}：{provenMessage}");

                    return LayerOutcome.Stop(
                        BuildLayerReport(
                            item,
                            result,
                            succeededPassword: null,
                            overrideMessage: provenMessage,
                            manifestEntries: lastListedEntries),
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

                /*
                 * ===== 引擎说"这根本不是归档"时**不写 ERROR**（用户 2026-10-05：三条假 ERROR）=====
                 *
                 * 现场：第 1 层三个双面视频各留了一行
                 * `[ERROR] 解压失败：7-Zip 无法识别或不支持该格式`，而下面紧接着就是
                 * "已按偏移把归档那一段取出成副本……再解一次"并且真的解开了 —— 那是**已知原因**的一步，
                 * 用户读到的却是一句"包坏了"。识别阶段本来就是靠"尾部有归档"把这个文件认成归档的，
                 * 这个事实在交引擎之前就在手上 ⇒ 命中就不报失败，改由补救那一行 INFO 说清。
                 *
                 * ⛔ 对照（用户点名要保留的）：**普通"名字像归档其实不是"的文件**照旧走引擎、照旧报 ERROR ——
                 * 判据只认"尾部确实有一个自洽的内嵌归档"，看不出就是看不懂，一个字都不许放宽。
                 */
                bool looksDoubleFaced = carvedSource == null
                    && string.Equals(result.DetectedErrorType, EngineErrorTypes.UnsupportedFormat, StringComparison.Ordinal)
                    && IsDoubleFacedEmbeddedArchive(item.ArchivePath);

                if (!looksDoubleFaced)
                {
                    // 其余（引擎不可用、路径问题、输出冲突…）换密码也解决不了，直接停。
                    Log(
                        "ERROR",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.CandidateStoppedByEngineErrorLogFormat,
                            layerLabel,
                            result.Message));
                }

                /*
                 * 例外：引擎说"这根本不是归档"时，先看一眼它是不是**双面文件**
                 * （真视频 / 图片 + 尾部一整个 ZIP）—— 是的话抠出副本再解一次，
                 * 而不是把它当"这一层失败"（那样会把整条链判成部分完成、已解出的内容全丢）。
                 * 只在第一次（还没抠过）时试，⛔ 不会套娃重试。
                 */
                if (carvedSource == null
                    && string.Equals(result.DetectedErrorType, EngineErrorTypes.UnsupportedFormat, StringComparison.Ordinal))
                {
                    LayerOutcome? retried = await RetryWithCarvedEmbeddedArchiveAsync(
                            item,
                            cancellationToken,
                            progress,
                            stalled)
                        .ConfigureAwait(false);

                    if (retried != null)
                    {
                        return retried;
                    }
                }

                return LayerOutcome.Stop(
                    BuildLayerReport(item, result, succeededPassword: null, manifestEntries: lastListedEntries),
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

                RecursionStopReason reason = ResolvePasswordStopReason(
                    candidates.Count - skippedCandidates,
                    attempts,
                    conclusion,
                    triedAny);

                /*
                 * ===== 「文件名已加密」在递归路也要成立（2026-10-05 用户拍板）=====
                 *
                 * 现场：同一个 `-mhe` / `-hp` 的包，单层路报「文件名已加密」，而递归路
                 * （出厂默认档 = 展开所有分支）报「密码错误」—— 用户被指去翻密码本，
                 * 而他要做的是"先给它一个密码"（这个包连内容清单都读不出来）。
                 *
                 * 判据 = 四件事**同时**成立：
                 * ① 收尾本来就落在**「密码错误」**那一档（候选全试完了都不对）；
                 * ② 这一层从头到尾**一次都没列成功**；③ 引擎说的是**加密头**（结构化错误类型，⛔ 不比中文）；
                 * ④ 没出现过别的列目录错误（证据混了 ⇒ 判不出 ⇒ 退回既有口径）。
                 *
                 * ⚠ ① 是照单层路径**逐字对齐**的：那边恢复这个结论的条件是
                 * `preflightSaidEncryptedHeaders && (密码错误 || 解压失败)`，而它**刻意不覆盖**
                 * 「达到密码尝试上限」（那边原话："那是更具体的结论，不动"）—— 候选还有剩、再试就能开，
                 * 说成"文件名已加密"会把用户从"去补候选"引开。同理也不覆盖两义与损坏（各自断言了别的原因）。
                 */
                bool encryptedHeadersConclusion =
                    reason == RecursionStopReason.WrongPassword
                    && !listedAnyCandidate
                    && sawEncryptedHeadersListFailure
                    && !sawOtherListFailure;

                string? encryptedHeadersMessage = null;

                if (encryptedHeadersConclusion)
                {
                    reason = RecursionStopReason.EncryptedHeaders;

                    encryptedHeadersMessage = string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.RecursionEncryptedHeadersMessageFormat,
                        encryptedHeadersListMessage);

                    Log(
                        "WARN",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.RecursionEncryptedHeadersLogFormat,
                            layerLabel,
                            StatusText.EncryptedHeaders));
                }

                RecursionLayerReport failureReport = BuildLayerReport(
                    item,
                    conclusion,
                    succeededPassword: null,
                    overrideMessage: encryptedHeadersMessage,
                    manifestEntries: lastListedEntries,
                    overrideStatus: encryptedHeadersConclusion ? StatusText.EncryptedHeaders : null);

                return LayerOutcome.Stop(failureReport, reason);
            }

            return await FinishSuccessfulLayerAsync(
                    item,
                    success,
                    layerManifest,
                    succeededPassword,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// 这一层**真的解开了**之后的共同收尾：量产物 → 落点校验 → 探内层包 → 出层报告。
        ///
        /// <para><b>为什么把它抽出来</b>（2026-10-05 真机第六批）：多了一条**不经过引擎**的成功路
        /// （尾部归档里只有一个原样存的条目 ⇒ 直接按偏移把那一段取出来，见
        /// <see cref="TryTakeSingleStoredEntryAsync"/>）。两条路必须交出**同一形状**的层报告 ——
        /// 落点校验、内层包探测、清单口径一个字都不许分叉（§9.5）。</para>
        /// </summary>
        private async Task<LayerOutcome> FinishSuccessfulLayerAsync(
            WorkItem item,
            ArchiveOperationResult? success,
            LayerManifest layerManifest,
            string? succeededPassword,
            CancellationToken cancellationToken)
        {
            string layerLabel = DescribeProbeLabel(item);

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
                    layerLabel,
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
        /// **先只解清单里最小的那个条目**（探针），再决定要不要解整包（用户 2026-10-04 真机）。
        ///
        /// <para>判据全部来自唯一出口 <see cref="PasswordProbe"/>（值不值得探 / 挑哪一个条目 ——
        /// ⛔ 这里不另写一份）：</para>
        /// <list type="bullet">
        /// <item><description>不值得探（没有加密条目 / 包不大）或挑不出条目 ⇒ 返回 <c>null</c>（调用方照旧解整包）；</description></item>
        /// <item><description>探针**解不开且引擎明说密码不对** ⇒ 返回那一份引擎结果（调用方当"这个候选不对"处理，
        /// 一个字节的整包数据都没动）；</description></item>
        /// <item><description>探针解开 / 给不出确定结论 / 探针自己出错 ⇒ 返回 <c>null</c>（照旧解整包，
        /// 结论仍然由整包那一步说了算）。</description></item>
        /// </list>
        ///
        /// <para>探针目录建在**这一层产物目录之外**（同层的兄弟目录）：产物目录里的东西要参与
        /// 结果校验与发布，⛔ 一个字节都不能混进去；用完就整份删掉。</para>
        /// </summary>
        private async Task<ArchiveOperationResult?> ProbeCandidateAsync(
            WorkItem item,
            string archivePath,
            ExtractOptions options,
            ArchiveListResult? listed,
            string candidate,
            IProgress<ArchiveProgress>? progress,
            Action<ArchiveStallNotice>? stalled,
            CancellationToken cancellationToken)
        {
            if (!PasswordProbe.IsWorthProbing(listed, listed?.TotalUncompressedSize ?? 0))
            {
                return null;
            }

            string? probeEntry = PasswordProbe.ChooseProbeEntry(listed);

            if (string.IsNullOrWhiteSpace(probeEntry))
            {
                return null;
            }

            // 落点与单层路径**同一个出口**（2026-10-05 统一）：产物目录的兄弟位置，绝不放产物目录里。
            string probeDirectory = PasswordProbe.ResolveProbeDirectory(item.Layer.OutputPath);

            try
            {
                Directory.CreateDirectory(probeDirectory);
            }
            catch
            {
                // 建不出目录（权限 / 盘）：探针做不了 ⇒ 退回整包试解（结论不会被这里改掉）。
                return null;
            }

            IReadOnlyList<string> previousEntries = options.IncludeEntries;

            try
            {
                options.IncludeEntries = new[] { probeEntry };

                ArchiveOperationResult result = await _engine.ExtractAsync(
                        new ArchiveRequest
                        {
                            ArchivePath = archivePath,
                            OutputPath = probeDirectory,
                            Password = candidate,
                            Progress = progress,
                            Stalled = stalled
                        },
                        options,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (result.DetectedErrorType == "Cancelled" || result.Status == StatusText.Cancelled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (result.Success)
                {
                    Log(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.PasswordProbePassedLogFormat,
                            DescribeProbeLabel(item),
                            PasswordProbe.Describe(probeEntry, ResolveProbeEntrySize(listed, probeEntry))));

                    return null;
                }

                if (result.IsWrongPassword || result.IsNeedPassword)
                {
                    Log(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.PasswordProbeRejectedLogFormat,
                            DescribeProbeLabel(item),
                            PasswordProbe.Describe(probeEntry, ResolveProbeEntrySize(listed, probeEntry))));

                    return result;
                }

                // 给不出确定结论（损坏 / 权限 / 引擎怪话）：不拦，照旧解整包。
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 探针自己出错不是"这个包失败"：退回整包试解。
                return null;
            }
            finally
            {
                options.IncludeEntries = previousEntries;

                try
                {
                    if (Directory.Exists(probeDirectory))
                    {
                        Directory.Delete(probeDirectory, recursive: true);
                    }
                }
                catch
                {
                    // 删不掉只是工作区里多一个空目录（它不进产物、也不进发布）。
                }
            }
        }

        /// <summary>探针日志里的层标签（与候选日志同一个形状：`├ 第 N 层：包名`）。</summary>
        private static string DescribeProbeLabel(WorkItem item) =>
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.RecursionLayerLogPrefixFormat,
                item.Depth) + Path.GetFileName(item.ArchivePath);

        /// <summary>
        /// **尾部归档里只有一个原样存（stored）的条目 ⇒ 直接按偏移把那一段取出来。**
        ///
        /// <para><b>真机现场</b>（`ArchiveFixer-本次操作_20261005_134118.txt`，一键处理 21 分 34 秒）：
        /// 第 1 层三个双面视频，每个都先白试一次引擎（`Cannot open the file as archive`，0 秒），
        /// 再把尾部归档**整份抠成副本**（78 / 48 / 83 秒），然后让 7-Zip 从那个副本里**再解一遍**
        /// 那唯一一个条目（88 / 48 / 92 秒）—— 同一份字节写了两遍、读了两遍，约 3.8 分钟纯浪费。
        /// 而那段归档里其实只有一个**原样存**的条目：它的字节就是源文件里一段连续区间，
        /// 直接取出来就是产物（一次写、一次读、**一次引擎都不调**）。</para>
        ///
        /// <para><b>⛔ 只在能证明的前提下走这条路</b>（任何一条不成立都返回 <c>null</c> ⇒
        /// 原样退回今天那条"抠副本 + 引擎"的路，行为与加这一段之前逐字相同）：</para>
        /// <list type="number">
        /// <item><description>识别阶段那条只读判据成立：尾部有**自洽的** ZIP（<see cref="EmbeddedArchiveDetector"/>，
        /// 与识别阶段同一个检测器）、<c>Offset &gt; 0</c>、不是跨盘 ZIP 的末片；</description></item>
        /// <item><description><see cref="EmbeddedZipStreamExtractor.Probe"/> 说这份内嵌 ZIP 可直读
        /// （自洽 + 每个本地头是 <c>PK\x03\x04</c> + 方法 ∈ {stored, deflate} + 无加密位 + ZIP64 占位符取得到真值
        /// + 数据区不越过中央目录 + 条目名过既有的落点校验）；</description></item>
        /// <item><description>清单里**恰好一个**条目、不是目录、**没有加密**、压缩方法是 **stored（0）**、
        /// 且 <c>Size == CompressedSize</c>（原样存）—— 与"抠出来再让引擎解"得到的东西逐字节相同；</description></item>
        /// <item><description>条目大小不越过本层的单文件上限（越过的交给既有那道闸门照旧报，⛔ 不在这里抢着判）。</description></item>
        /// </list>
        ///
        /// <para><b>⚠ 与老路唯一的行为差别</b>（如实记在这里）：<see cref="EmbeddedZipStreamExtractor"/>
        /// 不校验 stored 条目的 CRC32，所以"结构自洽但字节坏了"的包在老路上会被 7-Zip 报 CRC 错误、
        /// 在这里会照旧成功。判据收得这么窄（单条目 + 原样存 + 无加密）就是为了把这一档压到最小。</para>
        ///
        /// <para>⛔ 成功时**不写 ERROR**：用户看到的必须是"为什么引擎打不开 + 我们换了什么办法"，
        /// 而不是一句会被读成"包坏了"的失败。</para>
        /// </summary>
        private async Task<LayerOutcome?> TryTakeSingleStoredEntryAsync(
            WorkItem item,
            IProgress<ArchiveProgress>? progress,
            CancellationToken cancellationToken)
        {
            string layerLabel = DescribeProbeLabel(item);

            try
            {
                EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(item.ArchivePath);

                if (!info.Found || info.IsVolumePart || info.Offset <= 0)
                {
                    return null;
                }

                /*
                 * ⛔ 传 passwords: null —— 探针阶段不猜密码：加密的（AES）内嵌包会返回"不支持"
                 * 这个**回落信号**，于是原样走"抠副本 + 引擎"那条路（那时密码候选由既有的候选循环管）。
                 */
                EmbeddedZipProbeResult probe = EmbeddedZipStreamExtractor.Probe(
                    item.ArchivePath,
                    info.Offset,
                    info.ArchiveEnd,
                    item.Layer.OutputPath,
                    passwords: null,
                    cancellationToken);

                if (!probe.Supported || probe.List == null || probe.Entries.Count != 1)
                {
                    return null;
                }

                EmbeddedZipEntry entry = probe.Entries[0];

                if (entry.IsDirectory
                    || entry.IsEncrypted
                    || entry.Method != 0
                    || entry.Size <= 0
                    || entry.Size != entry.CompressedSize
                    || string.IsNullOrWhiteSpace(entry.RelativePath))
                {
                    return null;
                }

                // 单文件上限：越过的交给既有那道闸门（它的文案与判据只有一处，⛔ 不在这里抢着判）。
                if (entry.Size > _limits.MaxSingleFileSize)
                {
                    return null;
                }

                string entryDescription = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    "{0}（{1}）",
                    entry.Name,
                    TaskSpaceEstimate.FormatSize(entry.Size));

                Log(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.InnerEmbeddedSingleEntryTakeFormat,
                        layerLabel,
                        info.Offset,
                        entryDescription));

                /*
                 * 界面也得有动静：这一段真机上是 48–83 秒的纯读写，引擎一个字都不报
                 * （用户 2026-10-05：那几十秒日志里一行进度都没有）。
                 */
                progress?.Report(new ArchiveProgress
                {
                    Percent = 0,
                    CurrentEntry = $"第 {item.Depth} 层：正在按偏移取出「{entry.Name}」"
                });

                EmbeddedZipExtractResult take = EmbeddedZipStreamExtractor.Extract(
                    item.ArchivePath,
                    info.Offset,
                    info.ArchiveEnd,
                    item.Layer.OutputPath,
                    progress: null,
                    cancellationToken,
                    budgetOptions: new ResourceBudgetOptions
                    {
                        MaxSingleFileSize = _limits.MaxSingleFileSize,
                        MaxTotalSize = _limits.MaxTotalSize,
                        MaxFileCount = _limits.MaxTotalFiles
                    },
                    passwords: null);

                if (!take.Success || take.FileCount != 1 || take.WrittenBytes != entry.Size)
                {
                    /*
                     * 没取成（或取出来的与清单对不上）⇒ 如实写一行 WARN 后**退回老路**：
                     * 半成品由直读器自己删干净（它保证"失败不留半成品"），接着走的
                     * "抠副本 + 引擎"那条路开头还会把这一层的产物目录整份重置一次。
                     */
                    Log(
                        "WARN",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.InnerEmbeddedSingleEntryTakeFailedFormat,
                            layerLabel,
                            string.IsNullOrWhiteSpace(take.Message) ? "取出来的字节数与清单对不上" : take.Message));

                    return null;
                }

                /*
                 * 可疑条目提示（与既有那一处**同一个出口**）：直读这条路跳过了引擎列目录那一段，
                 * 但"这个包里有什么"的结论必须照旧回传一次 —— 否则同一件事在两条路上只有一条会提示。
                 */
                DangerousEntriesReported?.Invoke(
                    ViewModels.ExtractionCoordinator.AnalyzeDangerousEntries(
                        probe.List.Entries,
                        ReportDangerousEntries));

                return await FinishSuccessfulLayerAsync(
                        item,
                        new ArchiveOperationResult
                        {
                            Success = true,
                            Status = StatusText.ExtractSuccess,
                            EngineDisplayName = EmbeddedZipStreamExtractor.ReaderDisplayName
                        },
                        LayerManifest.From(probe.List),
                        succeededPassword: null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消照旧往上抛（不变量 6：取消不得显示成成功）。
                throw;
            }
            catch (Exception ex)
            {
                /*
                 * 这一档是**加法**：判据里任何意外（读不到源文件、探测内部异常…）都只能得出
                 * "这条路走不通"，⛔ 绝不能让一条新的近路把本来能解开的包挡下来。
                 */
                Log(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.InnerEmbeddedSingleEntryTakeFailedFormat,
                        layerLabel,
                        ex.Message));

                return null;
            }
        }

        /// <summary>
        /// 只读判据：这个文件是不是「双面文件」（尾部藏着**自洽的** ZIP，而且不是跨盘 ZIP 的末片）。
        ///
        /// <para>与补救那一处**同一个检测器**（<see cref="EmbeddedArchiveDetector"/>，⛔ 不另写一份判据）；
        /// 它只读文件尾部那段（默认 256 KB），不建目录、不改名字、不调引擎。</para>
        /// </summary>
        private static bool IsDoubleFacedEmbeddedArchive(string archivePath)
        {
            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(archivePath);

            return info.Found && !info.IsVolumePart && info.Offset > 0;
        }

        /// <summary>
        /// 引擎回过"这根本不是归档"之后的一次补救：这个内层包是不是**双面文件**
        /// （真视频 / 图片 + 尾部一整个 ZIP）？是的话按偏移抠出副本、带副本重跑这一层。
        ///
        /// <para><b>为什么必须有这一步</b>（用户 2026-10-04 真机原话：「这么简单的操作，密码也是对的，
        /// 怎么解压不了」）：<c>HK.7z.001</c> 第 0 层正常解出 3 个 4K 视频（7.11 GiB），第 1 层探到
        /// <c>4K (11)_2.mp4</c> 是内层包，把它**原样**交给 7-Zip ⇒ <c>Cannot open the file as archive</c>
        /// ⇒ 整条链判"部分完成" ⇒ 已经解出来的 7.11 GiB 一个字节都不发布、工作区整份删掉（白跑 14 分钟）。
        /// 那个 mp4 是真视频 + 尾部一个完整 ZIP，而 <b>7-Zip 只在前面垫的数据 ≤ 8 MiB 时才容忍这种整体偏移</b>
        /// （实测边界见 <see cref="EmbeddedArchiveCarver"/> 的类注释）—— 单层路径早就接了这一档
        /// （<c>ExtractionCoordinator</c> 里那句"仍按偏移取出内嵌归档"），**递归这条路一直没接**；
        /// 而 2026-10-04 起出厂默认档正是"展开所有分支"，于是它成了默认路径。</para>
        ///
        /// <para>判据全部转调既有出口：是不是双面文件问 <see cref="EmbeddedArchiveDetector"/>
        /// （与识别阶段同一个检测器），抠取用 <see cref="EmbeddedArchiveCarver"/>（与单层路径同一个执行体）。
        /// ⛔ 判不出 / 抠不动 ⇒ 返回 <c>null</c>，调用方照旧按失败处置（口径一个字不改）；
        /// ⛔ 抠出来的只是**副本**（落在这一层的 <c>carved\</c> 兄弟目录里，不在产物目录里），原文件一个字节都不动。</para>
        /// </summary>
        private async Task<LayerOutcome?> RetryWithCarvedEmbeddedArchiveAsync(
            WorkItem item,
            CancellationToken cancellationToken,
            IProgress<ArchiveProgress>? progress,
            Action<ArchiveStallNotice>? stalled)
        {
            string layerLabel = DescribeProbeLabel(item);

            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(item.ArchivePath);

            /*
             * 不是双面文件 ⇒ 什么都不做。
             * · `IsVolumePart`（跨盘 ZIP 的最后一片）也在这里排除：它没有"可抠出来的独立区间"，
             *   抠了只会得到一个解不开的半套。
             */
            if (!info.Found || info.IsVolumePart || info.Offset <= 0)
            {
                return null;
            }

            string layerDirectory = Path.GetDirectoryName(item.Layer.OutputPath) ?? item.Layer.OutputPath;
            string carveDirectory = Path.Combine(layerDirectory, CarveDirectoryName);

            try
            {
                Directory.CreateDirectory(carveDirectory);
            }
            catch
            {
                // 建不出目录（权限 / 盘）⇒ 补救做不了，按原样失败（绝不因此改结论）。
                return null;
            }

            /*
             * ===== 抠取这一段的进度（用户 2026-10-05 真机）=====
             *
             * 现场：第 1 层三个双面视频的抠取分别跑了 78 / 48 / 83 秒，日志里**一行都没有** ——
             * 上一行是"开始解压，密码候选 1/10"，下一行就是"已按偏移取出来……再解一次"，
             * 中间那几十秒读起来像卡死。⛔ 不报百分比（抠取没有"第几个条目"这种语义），
             * 只说清在做什么、大概多少量。
             */
            long carveBytes = info.ArchiveLength > 0
                ? info.ArchiveLength
                : Math.Max(0, new FileInfo(item.ArchivePath).Length - info.Offset);

            Log(
                "INFO",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.InnerEmbeddedCarveProgressFormat,
                    layerLabel,
                    TaskSpaceEstimate.FormatSize(carveBytes)));

            CarveResult carve = EmbeddedArchiveCarver.Carve(
                item.ArchivePath,
                info.Offset,
                Path.Combine(carveDirectory, Path.GetFileName(item.ArchivePath)),
                info.ArchiveEnd,
                progress: null,
                cancellationToken);

            if (!carve.Success)
            {
                Log(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.InnerEmbeddedCarveFailedFormat,
                        layerLabel,
                        carve.Message));

                return null;
            }

            Log(
                "INFO",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.InnerEmbeddedCarveRetryFormat,
                    layerLabel,
                    info.Offset,
                    carve.BytesWritten,
                    info.EntryCount));

            LayerOutcome retried = await ExtractLayerAsync(
                    item,
                    cancellationToken,
                    progress,
                    stalled,
                    carve.OutputPath)
                .ConfigureAwait(false);

            /*
             * 重解成功 ⇒ 抠出来的副本用完就删（它可能有几个 GB，成功路径上不该留）。
             * 失败 ⇒ 留着：③页「失败时保留中间产物」打开时，那份副本是排查"到底抠对没抠对"的直接证据。
             */
            if (retried.Report is { Success: true })
            {
                TryDeleteDirectoryQuietly(carveDirectory);
            }

            return retried;
        }

        /// <summary>
        /// 引擎调用之前的那一道：**跨层收卷**（把同组散在别的层产物目录里的卷收进入口那一层），
        /// 以及收不到一起时的**免试**判定。
        ///
        /// <para>返回 false = 这一组凑不齐、而且有硬证据 ⇒ **一次引擎都不许调**，
        /// <paramref name="missingVolumeMessage"/> 就是那句"缺哪几片"（写日志 + 当层结论）。</para>
        ///
        /// <para>⛔ 任何意外（列目录失败、判据内部异常）一律返回 true：这一档是**加法**，
        /// 判不出来时必须退回"照旧让引擎去判"，⛔ 绝不能让一个新的判据把本来能解的包挡下来。</para>
        /// </summary>
        private bool TryGatherCrossLayerVolumes(
            WorkItem item,
            string archivePath,
            string layerLabel,
            out string? missingVolumeMessage)
        {
            missingVolumeMessage = null;

            try
            {
                string? skippedReason = null;

                /*
                 * 候选池 = 这条链各层产物目录的直接子文件（没有工作区就是空池 ⇒ 只按入口那一层判、
                 * 一个字节都不搬）。每次重判都**重新枚举**：搬完之后盘上的事实变了。
                 */
                if (Evaluate(EnumerateWorkspacePool(CurrentWorkspace)))
                {
                    return true;
                }

                missingVolumeMessage = skippedReason;
                return false;

                bool Evaluate(IReadOnlyList<VolumeCandidate> poolCandidates)
                {
                    VolumeNameRepair.CrossLayerVolumeGather decision =
                        VolumeNameRepair.ResolveCrossLayerVolumeGather(archivePath, poolCandidates);

                    if (!decision.Applicable)
                    {
                        return true;
                    }

                    if (decision.Plan is { CanRepair: true })
                    {
                        int count = decision.MovedCount;

                        VolumeNameRepairResult applied = VolumeNameRepair.TryApply(decision.Plan);

                        if (applied.Success)
                        {
                            Log(
                                "INFO",
                                string.Format(
                                    System.Globalization.CultureInfo.CurrentCulture,
                                    StatusText.CrossLayerGatherDoneFormat,
                                    layerLabel,
                                    count,
                                    Path.GetFileName(archivePath),
                                    decision.Detail));
                        }
                        else
                        {
                            // 全成或全不成（TryApply 自己倒序回滚）：盘上仍是"没收"那一档，如实写。
                            Log(
                                "WARN",
                                string.Format(
                                    System.Globalization.CultureInfo.CurrentCulture,
                                    StatusText.CrossLayerGatherBlockedFormat,
                                    layerLabel,
                                    applied.Message));
                        }

                        /*
                         * 收完（或回滚完）**重新读一次事实**：这一组现在到底齐不齐，
                         * 只能由盘上那几份回答 —— ⛔ 不拿"刚刚搬成功了"当结论。
                         */
                        decision = VolumeNameRepair.ResolveCrossLayerVolumeGather(
                            archivePath,
                            EnumerateWorkspacePool(CurrentWorkspace));
                    }

                    if (!decision.ShouldSkipTrial)
                    {
                        if (!decision.CompleteBesideEntry && !string.IsNullOrWhiteSpace(decision.Detail))
                        {
                            // 判不出 / 其他族（本轮不做免试）：如实写"为什么没收"，然后照旧让引擎去判。
                            Log(
                                "WARN",
                                string.Format(
                                    System.Globalization.CultureInfo.CurrentCulture,
                                    StatusText.CrossLayerGatherBlockedFormat,
                                    layerLabel,
                                    decision.Detail));
                        }

                        return true;
                    }

                    skippedReason = BuildMissingVolumeMessage(archivePath, layerLabel, decision);
                    return false;
                }
            }
            catch
            {
                // 见方法注释：这一档判不出来时，退回"照旧让引擎去判"。
                return true;
            }
        }

        /// <summary>
        /// 这条链**各层产物目录的直接子文件**（跨层收卷的候选池）。
        ///
        /// <para>⛔ 只这一处枚举：<c>layer-NNN\output</c> 的**直接**子文件 —— 不递归、不含
        /// <c>carved</c>（它挂在层目录下、是产物目录的兄弟）、不碰用户源目录（源包不在工作区里）。</para>
        /// </summary>
        private static List<VolumeCandidate> EnumerateWorkspacePool(ExtractionWorkspace? workspace)
        {
            var pool = new List<VolumeCandidate>();

            if (workspace == null)
            {
                // 拿不到工作区 ⇒ 空池（只按入口那一层判；⛔ 不替用户满盘找文件）。
                return pool;
            }

            foreach (WorkspaceLayer layer in workspace.Layers)
            {
                pool.AddRange(VolumeContentInference.EnumerateCandidatesIn(layer.OutputPath));
            }

            return pool;
        }

        /// <summary>
        /// 免试那一句"缺哪几片"：能点名的逐个点名（名字上的洞），点不了名的**如实说点不了**
        /// （命名里没有"共几卷"这个信息，⛔ 不编），再带上字节数那条证据与"为什么没收"。
        /// </summary>
        private static string BuildMissingVolumeMessage(
            string archivePath,
            string layerLabel,
            VolumeNameRepair.CrossLayerVolumeGather decision)
        {
            string names = decision.MissingNames.Count > 0
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.CrossLayerGatherMissingNamesFormat,
                    string.Join("、", decision.MissingNames))
                : StatusText.CrossLayerGatherNoMissingNames;

            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.CrossLayerGatherSkipTrialFormat,
                layerLabel,
                Path.GetFileName(archivePath),
                names,
                decision.Detail);
        }

        /// <summary>删一个目录，删不掉就算了（它只是工作区里的副本，不影响任何结论）。</summary>
        private static void TryDeleteDirectoryQuietly(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
                // 删不掉只是工作区里多占一份副本，工作区收尾时会一起清掉。
            }
        }

        private static long ResolveProbeEntrySize(ArchiveListResult? listed, string probeEntry)
        {
            if (listed?.Entries == null)
            {
                return 0;
            }

            foreach (ArchiveEntry? entry in listed.Entries)
            {
                if (entry != null && string.Equals(entry.Path, probeEntry, StringComparison.Ordinal))
                {
                    return entry.Size;
                }
            }

            return 0;
        }

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
        /// <param name="outputDirectory">这一层的产物目录（**我们自己解出来的副本**）。</param>
        /// <param name="layerLabel">日志行首那个"第 N 层：包名"标签（与这一层其它日志同形）。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        private async Task<IReadOnlyList<string>> ProbeInnerArchivesAsync(
            string outputDirectory,
            string layerLabel,
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

            /*
             * ===== 「内容物保留关键词」：命中就**不当内层归档**（用户 2026-10-04 的新功能）=====
             *
             * 用户原话：「只要文件名里面包含着这个字符就不能动……这些压缩文件碰都不要碰」。
             * 所以这一步要**排在还原改名那一步之前**：命中它的既不解开、也不改名 ——
             * 只把它当一件普通内容物留在这一层的结果里（定稿归位到目标目录不算碰）。
             *
             * ⛔ 判据只有一处（<see cref="ContentKeepRules"/>，包含即命中 / 大小写不敏感 / 不做通配正则）；
             * 这里只做两件事：把命中的摘出去、并如实说一句"因为哪个词"。
             * 空关键词列表 ⇒ 一个都不摘（行为与以前逐字相同）。
             *
             * ⚠ 连**同组的兄弟卷**一起摘：一组分卷（`小明.part1.rar` + 后续卷）是**一个**归档，
             * 只摘其中一卷会让引擎拿剩下的残卷去试开（既打不开，还会在日志里留下一条看不懂的失败）。
             */
            found = ApplyContentKeepRules(found, layerLabel);

            // 排序让候选清单在 UI 与测试里都是稳定顺序（不同文件系统返回顺序不一致）。
            found.Sort(StringComparer.OrdinalIgnoreCase);

            /*
             * ===== 「还原」工序：先擦掉伪装尾巴，再回到第 1 层重判一次（方案 §2.1 挂点②）=====
             *
             * 用户 2026-10-03 的口径：「首先第一步就是识别底层文件找出伪装文件，然后还原，再接着匹配」。
             * 顺序不可颠倒：**① 按魔数认出底层 → ② 还原名字 → ③ 才进族骨架匹配（组卷 / 定序 / 试开）**。
             *
             * 为什么挂在这里：包**里面**解出来的这一层，过去没有任何一步先擦伪装尾巴 ——
             * 一组 `风景01.part1.rar删除` / `.part2.rar删除` 解出来之后名字还是脏的，引擎按标准名
             * 找不到兄弟卷，只报「分卷缺失」，里面的内容永远出不来（AGENTS.md §11.4 §51 的现场）。
             *
             * ⛔ 只碰**我们自己产出的副本**：这里的路径全部来自本层产物目录（外层包刚解出来的东西），
             * 用户源目录里那一档归批首的「修正后缀」管，一步都不越界。
             * ⛔ 执行体与判据都不在这里：整件事转调 `VolumeNameRepair.RestoreDisguisedInnerPackageNames`
             * （它自己再转调 `VolumeNameRepair.TryApply` 与既有的两把去杂质尺子）。
             * ⛔ 认不出底层 / 目标名被占 / 有一份改不动 ⇒ 那**一组**一个名字都不改（全成或全不成）。
             *
             * "回环"就在下一行：还原完照旧走 `CollapseSameGroupVolumes`（它按**还原之后**的名字
             * 重新判"谁是首卷、哪些是它的续卷"）—— 一步都不跳。
             */
            IReadOnlyList<string> restored = VolumeNameRepair.RestoreDisguisedInnerPackageNames(
                found,
                (level, message) => Log(level, layerLabel + "：" + message));

            return CollapseSameGroupVolumes(restored.ToList());
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
                state.MarkUnexpanded(toProcess);
                stopReason = RecursionStopReason.TooManyInnerArchives;
                return false;
            }

            if (!decidedByUser && mode == RecursionMode.SingleChain)
            {
                /*
                 * ⛔ 2026-10-04（用户当场推翻旧口径）：「单链」的判据**只看这一层里有几个真归档**：
                 * **1 个 ⇒ 继续解**（旁边是什么文件都不影响）；**≥2 个 ⇒ 该档下才停**。
                 *
                 * 旧判据是"同层除该包以外**全是说明类文件**"（`HasOnlyInformationalSiblings`）⇒
                 * 一层里只有 1 个内层包、旁边放着一个 `.mp4` / `.pdf`，就被算成"多分支"停在那一层。
                 * 用户原话（真机 `第6集.7z`）：「那层内层包只有 1 个，但旁边还有不属于"说明类"的文件
                 * ⇒ 被算成"多分支" —— **这是压缩包吗，不是那你停什么**」。
                 *
                 * ⛔ 说明类后缀表**没有删**（`InformationalExtensions` / `IsInformationalFile` 仍在，
                 * 现在只决定下面那句日志要不要提"旁边还有别的文件"），改的只是"停 / 不停"的判据。
                 */
                bool isSingleChain = toProcess.Count == 1;

                if (isSingleChain)
                {
                    /*
                     * 继续解这一支也要**留一句**：不然用户下次翻日志只看到"开始解第 N 层"，
                     * 看不出旁边那个 `.mp4` 为什么没拦住它（那一格正是他点名的地方）。
                     * 判据仍是既有出口：只有"旁边确实还有别的文件"时才多这一句，⛔ 不新造判据。
                     */
                    if (!HasOnlyInformationalSiblings(report, toProcess[0]))
                    {
                        Log(
                            "INFO",
                            $"{DescribeProbeLabel(item)}：这一层里只有 1 个内层归档"
                            + $"（{Path.GetFileName(toProcess[0])}）——"
                            + "「单链自动展开」只看内层归档的数量，旁边还有别的文件也照旧继续解。");
                    }
                }
                else
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
                        state.MarkUnexpanded(innerArchives);
                        stopReason = RecursionStopReason.BranchNotExpanded;
                        return false;
                    }

                    decision = BuildDecision(item, innerArchives);
                    state.MarkUnexpanded(innerArchives);
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
                    state.MarkUnexpanded(toProcess);
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
        /// 同层除内层归档外，其余文件是不是都是说明类文件（.txt/.nfo/.url/.md/.sfv/.jpg/.png）。
        ///
        /// <para>⚠ <b>2026-10-04 起它不再是"单链判定"</b>（用户当场推翻旧口径：「这是压缩包吗，
        /// 不是那你停什么」）：停 / 不停的判据已经改成**只看这一层里有几个真归档**
        /// （见 <see cref="TryEnqueueNextLayers"/> 里 <c>isSingleChain</c> 那一段）。
        /// 它现在的唯一用途是：那一层**只有 1 个内层归档**时，决定要不要多写一句
        /// "旁边还有别的文件也照旧继续解" —— 说明类后缀表因此**一个字都没删**。</para>
        ///
        /// <para>⚠ 2026-10-02 修（用户真机 `1-6 电磁感应定律（1）`，见 `docs/真机事故复盘.md` §47.3）：
        /// **同一组的后续卷算那一份归档的一部分，不算"别的文件"**。现场 = 这一层里是
        /// <c>51658213.7z.001</c>（**唯一**的内层归档）+ <c>51658213.7z.002</c>（同组后续卷），
        /// 老判据只跳过"内层归档那一个文件"，于是 <c>.002</c> 被算成另一个分支 ⇒ 单链不成立 ⇒
        /// 日志写着「这一层里有 **1 个**内层归档（多分支）」并保守停在那一层：一键档白白多跑一轮
        /// （真机上第 2 轮才解出来），手动档还会拿"要不要展开多分支"去问用户 —— 而他看到的只有一个包。</para>
        ///
        /// <para>⛔ 认的只有**同目录 + 同包基名 + 是后续卷**三条同时成立的文件
        /// （<see cref="IsSameGroupContinuationVolume"/>）：别的目录、别的基名的 <c>.002</c>
        /// 照旧算"别的文件"，宁可多写一句也不当作没看见。</para>
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

                    if (IsSameGroupContinuationVolume(entry, archivePath))
                    {
                        // 同目录、同包基名的后续卷 = 这一份归档自己的另一片，不是另一个分支。
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

        /// <summary>
        /// 把这一层扫出来的候选里**命中「内容物保留关键词」**的那些摘掉（连同**同组的兄弟卷**）——
        /// 它们**不当内层归档**：不解开、也不改名，只当普通内容物留在这一层的结果里。
        ///
        /// <para>用户 2026-10-04 的新功能原话：「只要文件名里面包含着这个字符就不能动……
        /// 这些压缩文件碰都不要碰」「包含的也同样是」。判据唯一出口 <see cref="ContentKeepRules"/>
        /// （包含即命中、<see cref="StringComparison.OrdinalIgnoreCase"/>、空关键词忽略、⛔ 不做通配 / 正则）。</para>
        ///
        /// <para>⛔ **空关键词列表 = 一个都不摘**（原样返回，行为与加这条功能之前逐字相同）。
        /// 这一条是回归命根，别为了"顺手清理"改动它。</para>
        ///
        /// <para>⛔ 为什么不跟引擎、文件内容打交道：用户明确说"你甚至不用去检测他是否是压缩文件" ——
        /// 判据**只看文件名**（内容探测在这一步之前已经做完了，这里只决定"要不要当内层归档"）。</para>
        /// </summary>
        private List<string> ApplyContentKeepRules(List<string> found, string layerLabel)
        {
            if (KeepRules.IsEmpty || found.Count == 0)
            {
                return found;
            }

            var hitKeywords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in found)
            {
                string? keyword = KeepRules.FindMatch(path);

                if (keyword != null)
                {
                    hitKeywords[path] = keyword;
                }
            }

            if (hitKeywords.Count == 0)
            {
                return found;
            }

            var kept = new List<string>(found.Count);

            foreach (string path in found)
            {
                if (hitKeywords.TryGetValue(path, out string? ownKeyword))
                {
                    Log(
                        "INFO",
                        $"{layerLabel}：按「内容物保留关键词」原样留着，不解开也不改名 —— "
                        + $"{Path.GetFileName(path)}（命中「{ownKeyword}」）");

                    continue;
                }

                // 同组的兄弟卷：整组是一个归档，一卷命中就整组都不当内层归档。
                string? siblingKeyword = null;
                string? siblingHit = null;

                foreach (string hit in hitKeywords.Keys)
                {
                    if (IsSameGroupContinuationVolume(path, hit))
                    {
                        siblingKeyword = hitKeywords[hit];
                        siblingHit = hit;
                        break;
                    }
                }

                if (siblingKeyword != null)
                {
                    Log(
                        "INFO",
                        $"{layerLabel}：按「内容物保留关键词」原样留着，不解开也不改名 —— "
                        + $"{Path.GetFileName(path)}（与 {Path.GetFileName(siblingHit!)} 是同一组，"
                        + $"那一组命中「{siblingKeyword}」）");

                    continue;
                }

                kept.Add(path);
            }

            return kept;
        }

        /// <summary>
        /// 把"这一层扫出来的内层归档候选"里的**同组后续卷**折掉，只留每一组的**第一卷**（2026-10-03 真机 §51）。
        ///
        /// <para>一组 N 卷在**内容**上各自都是合法归档（每卷都带自己的 RAR / 7z 签名），所以内容扫描
        /// 会把它们全收进来 —— 老写法于是把一组 4 卷当成 **4 个内层归档**：「展开所有分支」档下
        /// 建 4 个分支层、同一组被解 4 遍（峰值多占 3 倍），其中三个分支层还会被当成"产物在哪"的答案
        /// 写进日志（真机现场见 <c>docs/真机事故复盘.md</c> §51）。</para>
        ///
        /// <para>判据**复用同一条**（同目录 + 同包基名 + 是后续卷 =
        /// <see cref="IsSameGroupContinuationVolume"/>，与单链那档同一个出口）：
        /// 只有真的同组后续卷才会被折叠，别的归档一个都不动。</para>
        ///
        /// <para>顺序：先按卷序（认不出卷序的排最后）决定"谁是首卷"，**结果仍按传进来的顺序返回** ——
        /// ⛔ 不许让折叠顺手改掉这一层的分支顺序。纯函数、不碰盘，所以能被单测直接摆布。</para>
        /// </summary>
        internal static List<string> CollapseSameGroupVolumes(List<string> found)
        {
            if (found == null || found.Count < 2)
            {
                return found ?? new List<string>();
            }

            List<string> byVolumeOrder = found
                .OrderBy(path => Detection.VolumeGroupDetector.TryGetVolumeIndex(Path.GetFileName(path))
                                 ?? int.MaxValue)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var firstVolumes = new List<string>();

            foreach (string candidate in byVolumeOrder)
            {
                if (firstVolumes.Any(first => IsSameGroupContinuationVolume(candidate, first)))
                {
                    continue;
                }

                firstVolumes.Add(candidate);
            }

            return firstVolumes.Count == found.Count
                ? found
                : found.Where(path => firstVolumes.Contains(path)).ToList();
        }

        /// <summary>
        /// 候选文件是不是**内层归档那一组的后续卷**：同目录 + 同包基名 + 名字是后续卷，三条同时成立。
        ///
        /// <para>判据全部转调既有出口（⛔ 这里不许自带第二套名字规则）：
        /// 「是不是后续卷」= <see cref="FileNameHelper.IsVolumeContinuationPart"/>（与续解扫描同一份），
        /// 「包基名」= <see cref="FileNameHelper.GetArchiveBaseName"/>（它内部剥分卷标记用的也是唯一那份
        /// <c>ExtensionHelper.TrySplitVolumeSegmentTolerant</c>）。</para>
        ///
        /// <para>为什么要"同目录"这一条：引擎找兄弟卷**只看入口文件旁边那一层**（AGENTS.md §11.4），
        /// 别的目录里那个 <c>.002</c> 不可能是这一组的可用分片 —— 那种情况照旧按"别的文件"处理。</para>
        ///
        /// <para><b>为什么是 internal</b>（2026-10-05）：<c>VolumeGroupRulerParityTests</c> 要把这一把尺子与
        /// 搬运/删除那一把（<c>ExtractionWorkspace.IsSameGroupMemberInSameDirectory</c>）放在**同一份名字语料**上
        /// 逐格比对。⛔ 那个用例里**不许复制一份判据**（复制出来的守卫拦不住真身漂移）—— 所以只放宽可见性，
        /// 判据本体一个字没动。</para>
        /// </summary>
        internal static bool IsSameGroupContinuationVolume(string candidate, string innerArchivePath)
        {
            if (!FileNameHelper.IsVolumeContinuationPart(candidate))
            {
                return false;
            }

            string candidateFull = SafePathHelper.GetFullPathSafe(candidate);
            string archiveFull = SafePathHelper.GetFullPathSafe(innerArchivePath);

            if (candidateFull.Length == 0 || archiveFull.Length == 0)
            {
                return false;
            }

            string candidateDirectory = Path.GetDirectoryName(candidateFull) ?? string.Empty;
            string archiveDirectory = Path.GetDirectoryName(archiveFull) ?? string.Empty;

            if (!string.Equals(candidateDirectory, archiveDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.Equals(
                FileNameHelper.GetArchiveBaseName(candidateFull),
                FileNameHelper.GetArchiveBaseName(archiveFull),
                StringComparison.OrdinalIgnoreCase);
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
            string? succeededPassword,
            IReadOnlyList<(string Path, long Size)>? manifestEntries = null)
        {
            return BuildLayerReport(
                item,
                result,
                succeededPassword,
                PasswordMasker.Sanitize(result?.Message),
                manifestEntries);
        }

        /// <summary>
        /// 带"失败原因覆盖"的层报告：预检/落点校验这类**不是引擎给出**的失败，
        /// 原因来自安全检查，必须原样写进报告（Message 会被 PasswordMasker 再洗一遍，防密码泄漏）。
        ///
        /// <para><paramref name="overrideStatus"/> 同理：像「文件名已加密」这种结论，
        /// 引擎最后一次给的往往是"密码错误"那一档（那是候选循环的驱动信号），
        /// 直接照抄会让层报告与结论自相矛盾。</para>
        /// </summary>
        private RecursionLayerReport BuildLayerReport(
            WorkItem item,
            ArchiveOperationResult? result,
            string? succeededPassword,
            string? overrideMessage,
            IReadOnlyList<(string Path, long Size)>? manifestEntries = null,
            string? overrideStatus = null)
        {
            string message = overrideMessage
                ?? (result == null ? "引擎没有返回结果" : PasswordMasker.Sanitize(result.Message));

            return new RecursionLayerReport
            {
                Depth = item.Depth,
                ArchivePath = item.ArchivePath,
                OutputPath = item.Layer.OutputPath,
                Success = false,
                Status = overrideStatus ?? result?.Status ?? StatusText.ExtractFailed,
                Message = PasswordMasker.Sanitize(message),
                InnerArchives = Array.Empty<string>(),
                OutputFileCount = 0,
                OutputSize = 0,

                // 失败层没有可信清单（它压根没解开）：L3 不会拿它当预期，原因如实写。
                Manifest = LayerManifest.Unavailable("这一层没有成功解压，没有可信清单"),

                /*
                 * 但"逐条清单"要带上（部分完成发布的输入）：它是**解压前**列出来的那一份，
                 * 与"这一层解开了没有"无关 —— 密码对、坏在个别条目上时，它正是逐条对账唯一能用的尺子。
                 */
                ManifestEntries = manifestEntries ?? Array.Empty<(string, long)>(),
                FailedEntryNames = result?.FailedEntryNames ?? Array.Empty<string>(),
                ReportedSubItemErrors = result?.ReportedSubItemErrors ?? 0,
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
        /// 递归工作区的根目录，由调用方在启动时指定（批首 <c>ApplyBatchWorkspaceRoot</c> 会设成
        /// <c>&lt;目标目录&gt;\.ArchiveFixer.work</c>；<c>MainViewModel</c> 构造时设成 <c>PathService.WorkDirectory</c>）。
        /// </summary>
        public static string? ConfiguredWorkspaceRoot { get; set; }

        /// <summary>
        /// **要不要允许"没人配过根时回落到系统临时目录（<c>%TEMP%</c>，通常是 C 盘）"** ——
        /// 显式开关，**默认 false**。
        ///
        /// <para><b>为什么默认必须关着</b>：不变量 12 的字面口径是"⛔ 绝不回落程序目录 / C 盘 /
        /// 源卷根 / <c>%TEMP%</c>"，而这一支兜底默认开着就等于**默认违反红线** ——
        /// 谁新写一条"没经过批首就解压"的路，它就会把几百 MB 的递归工作区写到系统盘上
        /// （用户 2026-09-24 第 23 条点名的正是这件事）。</para>
        ///
        /// <para><b>为什么不干脆删掉</b>：正式流程确实走不到它（批首解析不出来就整批不开工），
        /// 但 20 多个直接 <c>new RecursiveExtractor(...)</c> 的老用例没配根，删掉会让它们全变成异常。
        /// 折中就是这一位：**回落必须是调用方显式要的** —— 测试宿主在 <c>TestAssemblyInitialize</c>
        /// 里统一打开，生产代码一个字都不开。判断与遗留记录见 <c>docs\检验等级.md</c> 缺口 ⑨。</para>
        /// </summary>
        public static bool AllowSystemTempWorkspaceFallback { get; set; }

        private static string WorkspaceRootDirectory =>
            ResolveWorkspaceRootDirectory(ConfiguredWorkspaceRoot, AllowSystemTempWorkspaceFallback);

        /// <summary>
        /// **没人配过根时怎么走** —— 唯一出口（<see cref="WorkspaceRootDirectory"/> 转调它）。
        ///
        /// <para>单独抽出来是为了让守门用例直接钉判据，不必真跑一次递归（跑一次要备样本、要引擎，
        /// 而这条判据与递归本身无关）。</para>
        ///
        /// <para>拿不到根 ⇒ **什么都不做**（不变量 12 的"报错指路，绝不回落"那一档）：抛异常而不是
        /// 返回空串 —— 空串会被调用方拼成相对路径，那比写到 <c>%TEMP%</c> 更难查。</para>
        /// </summary>
        internal static string ResolveWorkspaceRootDirectory(string? configuredRoot, bool allowSystemTempFallback)
        {
            if (string.IsNullOrWhiteSpace(configuredRoot))
            {
                if (!allowSystemTempFallback)
                {
                    throw new InvalidOperationException(
                        "递归工作区根目录没有配置：批首会把这一批的工作区定在「本次目标目录里的 "
                        + ".ArchiveFixer.work」，手动路径由 MainViewModel 构造时设。按不变量 12，"
                        + "拿不到根时「不回落程序目录 / C 盘 / %TEMP%」—— 这一单不解，"
                        + "请确认目标目录可用后重试。（单元测试若确实不想配根，显式打开 "
                        + nameof(AllowSystemTempWorkspaceFallback) + "。）");
                }

                string fallbackRoot = Path.Combine(Path.GetTempPath(), "ArchiveFixer", "recursive");

                // 工作区那棵树一律带隐藏属性（用户 2026-09-30 诉求③：单看着看不出来）。
                WorkspaceTree.EnsureHiddenDirectory(fallbackRoot);

                return fallbackRoot;
            }

            string root = Path.Combine(configuredRoot, "recursive");

            // 工作区那棵树一律带隐藏属性（用户 2026-09-30 诉求③：单看着看不出来）。
            WorkspaceTree.EnsureHiddenDirectory(root);

            return root;
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
            IReadOnlyList<string>? unexpandedNames = null)
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

                /*
                 * ⛔ 发布侧"判不出就没动"的那几件事**必须一条一条说出来**（2026-10-03 就地替换那一档）：
                 * 典型是"这一组卷本该被替换掉，但同组有一片不在我们这一步搬进来的名单里（可能在别的目录 /
                 * 名字认不出）⇒ 一个字节都不删"。不写出来，用户只会看到"产物里怎么还留着几个包"，
                 * 而原因（我们不敢动）一个字都没有 —— 这正是 §9.5 那条"要删的动作判不出就什么都不做，
                 * 而且要留证据"。⛔ 只是日志，不改任务结论（发布成功仍然是成功）。
                 */
                foreach (string warning in publishResult.Warnings)
                {
                    /*
                     * 前缀用**本次递归那个归档的文件名**（`BuildResult` 这一层拿不到任务的显示名，
                     * 调用方会在汇总那一行自己加任务名）—— 没有它，一批里几十个包的 WARN 会混在一起，
                     * 谁也认不出是哪一单。
                     */
                    string label = Path.GetFileName(_lastRunArchivePath);

                    Log("WARN", label.Length == 0 ? warning : $"{label}：{warning}");
                }

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
                UnexpandedNames = unexpandedNames?.ToList() ?? new List<string>(),
                Summary = BuildSummary(
                    stopReason,
                    layers,
                    workspace,
                    finalOutputPath,
                    completed,
                    publishMessage,
                    extraMessage,
                    unexpandedNames ?? Array.Empty<string>())
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
        /// （必须在工作区根目录之下 + 目录里只许有我们自己造的子目录，见
        /// <see cref="WorkspaceCleanupGuard"/>）、<see cref="ExtractionWorkspace.Cleanup"/> 内部再独立校验一遍、
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

            /*
             * 第二道容器内校验（与 CleanupWorkspace 同一处判据）：目录里只许有我们自己造的子目录。
             * 见 WorkspaceCleanupGuard 的说明 —— 这道以前只有单层路径有，递归这两次删工作区一直缺它。
             */
            if (!HasOnlyOwnedWorkspaceSubdirectories(workspace.TaskDirectory, taskLabel))
            {
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
        /// · 容器内校验先在这里做一遍（必须在工作区根目录之下 **+ 目录里只许有我们自己造的子目录**，
        ///   见 <see cref="HasOnlyOwnedWorkspaceSubdirectories"/>），
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

            /*
             * 第二道容器内校验（2026-10-05 只读审计）：目录里只许有我们自己造的子目录。
             * 单层路径一直有两道，递归这条路（两次删工作区）以前只有上一道 ——
             * 判据本体是同一个纯函数（<see cref="WorkspaceCleanupGuard"/>），⛔ 不在这里再写一遍。
             */
            if (!HasOnlyOwnedWorkspaceSubdirectories(workspace.TaskDirectory, taskLabel))
            {
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
        /// 第二道容器内校验：这个工作区目录里**只许有我们自己造的子目录**（<c>layer-NNN</c> /
        /// <c>carved</c> / <c>_密码预检</c>）。返回 false = 发现外来的，调用方一律**一个字节都不删**。
        ///
        /// <para>判据本体是 <see cref="WorkspaceCleanupGuard"/> 里那个纯函数（单层路径也用它），
        /// 这里只负责"读目录 + 写 WARN"这两件与日志出口绑定的事。
        /// 白名单刻意**不是**单层路径那一套（<c>stage</c>/<c>volumes</c>）：递归工作区的形状是层的目录，
        /// 套用别人的白名单会把每一次清理都拦下 —— 那等于把清理整块关掉。</para>
        ///
        /// <para>读不动目录（占用 / 权限）时也拦下：这是"要删东西"的动作，判不出来就什么都不做
        /// （与 <see cref="CleanupWorkspace"/> 的兜底同一条口径）。</para>
        /// </summary>
        private bool HasOnlyOwnedWorkspaceSubdirectories(string taskDirectory, string taskLabel)
        {
            string[] subdirectories;

            try
            {
                if (!Directory.Exists(taskDirectory))
                {
                    // 本来就不在：没有"删了什么"需要交代，交给 Cleanup() 那一边如实回答。
                    return true;
                }

                subdirectories = Directory.GetDirectories(taskDirectory);
            }
            catch (Exception ex)
            {
                Log("WARN", $"{taskLabel}：读不了工作区目录（{ex.Message}），已跳过清理：{taskDirectory}");

                return false;
            }

            string? foreign = WorkspaceCleanupGuard.FindForeignSubdirectory(
                subdirectories,
                WorkspaceCleanupGuard.IsOwnedRecursiveWorkspaceDirectory);

            if (foreign == null)
            {
                return true;
            }

            Log(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.WorkspaceForeignSubdirectoryLogFormat,
                    taskLabel,
                    foreign,
                    taskDirectory));

            return false;
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
            IReadOnlyList<string> unexpandedNames)
        {
            int done = layers.Count(layer => layer.Success);

            string reason = DescribeStopReason(stopReason, unexpandedNames.Count);

            string head = stopReason switch
            {
                RecursionStopReason.Completed => $"已完成 {done} 层递归解压（{reason}）",
                RecursionStopReason.NeedsDecision => $"已完成 {done} 层，停在第 {done + 1} 层，原因：{reason}",
                // "源文件已变化"时一层都没解是**常态**（拦在第 0 层开工之前），
                // 所以它不走下面那句"第 1 层就没能解开" —— 那句话会让人以为解压失败。
                RecursionStopReason.SourceChanged => $"未开始解压，原因：{reason}",

                /*
                 * 两义那一档（引擎既说密码不对、又说数据坏了）：结论里**同时**保留两种可能
                 * 与**引擎原话**（用户 2026-09-30 真机；与 7-Zip 侧同一条口径）。
                 * 原话取那一层自己记下来的 Message —— BuildLayerReport 写进去的就是引擎原话。
                 */
                RecursionStopReason.PasswordOrCorrupted => (done > 0
                        ? $"已完成 {done} 层，停在第 {done + 1} 层，原因："
                        : "第 1 层就没能解开，原因：")
                    + DescribeAmbiguousPasswordFailure(layers),

                /*
                 * 分卷缺失（免试那一档）：结论里必须带上**缺哪几片**（不变量 7 要的就是"缺哪几个"），
                 * 而那段证据在那一层的 Message 里 —— 与两义那一档同一个写法：取那一层自己记下来的正文。
                 */
                RecursionStopReason.MissingVolume => (done > 0
                        ? $"已完成 {done} 层，停在第 {done + 1} 层，原因："
                        : "第 1 层就没能解开，原因：")
                    + DescribeMissingVolumeFailure(layers),

                _ => done > 0
                    ? $"已完成 {done} 层，停在第 {done + 1} 层，原因：{reason}"
                    : $"第 1 层就没能解开，原因：{reason}"
            };

            var parts = new List<string> { head };

            /*
             * "还有哪几个内层包没展开"必须写出来（不变量 8）。
             * 只报"已完成"而把没展开的分支咽下去，用户会以为这就是最终数据 ——
             * 这与"部分成功不得显示为成功"是同一类问题。
             *
             * ⚠ 2026-10-04（真机）：过去只写**个数**（"该层还有 1 个内层包未展开"），
             * 用户看不出是哪一个 —— 他只能自己去工作区里翻。现在**点名**（前
             * <see cref="MaxUnexpandedNameLines"/> 个，多出来的折成"…还有 K 个"）。
             */
            if (unexpandedNames.Count > 0)
            {
                parts.Add(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.RecursionUnexpandedListFormat,
                    unexpandedNames.Count,
                    DescribeUnexpandedNames(unexpandedNames)));
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

        /// <summary>
        /// 「这一层还有哪几个内层包没展开」的名单（前 <see cref="MaxUnexpandedNameLines"/> 个，
        /// 多出来的折成"…还有 K 个"）。
        ///
        /// <para><c>internal</c>（2026-10-04）：一键档停在这一层的那一行也要点名（用户原话
        /// 「停因里要点名那个未展开的包」），而那一支会把递归结论改写成"只解了当前这一层"、
        /// 正文里那份名单到不了用户眼前 ⇒ 由 <c>ExtractionCoordinator</c> 转调**这一个**出口，
        /// ⛔ 不另写一份"前 5 个 + …还有 K 个"。</para>
        ///
        /// <para>只写**文件名**（§8 隐私红线），而且用**包基名之外的原文文件名**：
        /// 用户拿着它才能在目录里对上号（这正是"看不出是哪个"要治的那件事）。</para>
        /// </summary>
        internal static string DescribeUnexpandedNames(IReadOnlyList<string> names)
        {
            var shown = new List<string>();

            foreach (string name in names.Take(MaxUnexpandedNameLines))
            {
                string fileName = Path.GetFileName(name);

                shown.Add(fileName.Length == 0 ? name : fileName);
            }

            string text = string.Join("、", shown);

            if (names.Count <= shown.Count)
            {
                return text;
            }

            return text + string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.RecursionUnexpandedMoreFormat,
                names.Count - shown.Count);
        }

        /// <summary>
        /// 停因那句话。<paramref name="unexpandedCount"/> 只用于"多分支"那两档的**真实数量**。
        ///
        /// <para>⚠ 2026-10-04（真机）：这两档过去写死"多个"，而判据其实是
        /// <see cref="HasOnlyInformationalSiblings"/> —— **1 个内层归档 + 它旁边还有别的文件**
        /// 也会停在这一档，于是日志里出现"多个内层归档未展开；该层还有 **1 个**内层包未展开"
        /// 这种自相矛盾。现在按真实数量说，并且把 1 个那一档的**真实理由**说出来。</para>
        /// </summary>
        private static string DescribeStopReason(RecursionStopReason stopReason, int unexpandedCount = 0)
        {
            return stopReason switch
            {
                RecursionStopReason.Completed => "没有更多内层归档",
                RecursionStopReason.NeedsDecision => unexpandedCount > 1
                    ? string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.RecursionNeedsDecisionReasonMultipleFormat,
                        unexpandedCount)
                    : StatusText.RecursionNeedsDecisionReasonSingle,
                RecursionStopReason.BranchNotExpanded => unexpandedCount > 1
                    ? string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.RecursionBranchStopReasonMultipleFormat,
                        unexpandedCount)
                    : StatusText.RecursionBranchStopReasonSingle,
                RecursionStopReason.MaxDepthReached => "已达到最大递归层数",
                RecursionStopReason.MaxTotalFilesReached => "已达到累计输出文件数上限",
                RecursionStopReason.MaxTotalSizeReached => "已达到累计输出总大小上限",
                // 与上一条同档（都是"撞上程序的安全上限，不是包坏了"）：措辞统一走 StatusText。
                RecursionStopReason.MaxSingleFileSizeReached => StatusText.RecursionSingleFileSizeReachedReason,
                // 文件名已加密（-mhe / -hp）：与自己"密码错误"那一档分开说（措辞与单层路径同一句）。
                RecursionStopReason.EncryptedHeaders => StatusText.RecursionEncryptedHeadersReason,

                /*
                 * 分卷缺失（免试那一档）：详细证据（缺哪几片 / 还差多少字节）在那一层的 Message 里，
                 * 由 `BuildSummary` 的同一个分支取出来（与两义那一档同一种写法）。
                 */
                RecursionStopReason.MissingVolume => StatusText.RecursionMissingVolumeReason,
                RecursionStopReason.ExpansionRatioExceeded => "单层展开比超限，疑似压缩炸弹",
                RecursionStopReason.TooManyInnerArchives => "本层内层归档数量超过上限",
                RecursionStopReason.PasswordAttemptsExceeded => "已达到密码尝试次数上限（候选还有剩余）",
                RecursionStopReason.WrongPassword => "密码错误：所有候选都试过了",
                RecursionStopReason.Corrupted => "压缩包损坏",

                // 两义那一档：两种可能都必须留着（⛔ 不许压成"损坏"或"密码错误"）。
                // 任务级结论那一条会再带上引擎原话（见 BuildSummary 的同名分支）。
                RecursionStopReason.PasswordOrCorrupted => "密码可能不对，也可能这个包的数据坏了",

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
        /// 分卷缺失（免试那一档）的结论正文：**缺哪几片 + 字节数证据**（取那一层自己记下来的 Message）。
        /// 拿不到正文时退回停因那句话 —— ⛔ 绝不用一句"未知原因"把它盖掉，也⛔ 不许说成"引擎解不开"
        /// （引擎一次都没被调用过）。
        /// </summary>
        private static string DescribeMissingVolumeFailure(List<RecursionLayerReport> layers)
        {
            RecursionLayerReport? failed = layers.LastOrDefault(
                layer => layer != null && !layer.Success && !string.IsNullOrWhiteSpace(layer.Message));

            return failed == null
                ? StatusText.RecursionMissingVolumeReason
                : failed.Message;
        }

        /// <summary>
        /// 两义那一档的结论正文：**两种可能 + 引擎原话**（原话取那一层自己记下来的 Message）。
        /// 拿不到原话时只写两种可能 —— ⛔ 绝不用一句"未知原因"把它盖掉。
        /// </summary>
        private static string DescribeAmbiguousPasswordFailure(List<RecursionLayerReport> layers)
        {
            RecursionLayerReport? failed = layers.LastOrDefault(
                layer => layer != null && !layer.Success && !string.IsNullOrWhiteSpace(layer.Message));

            return failed == null
                ? "密码可能不对，也可能这个包的数据坏了"
                : $"密码可能不对，也可能这个包的数据坏了（引擎原话：{failed.Message}）";
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
        /// 是为了让"这一层还有哪几个内层包没展开"在每条提前返回的分支上都必须显式写一次 ——
        /// 忘了写就是空的，而空的会让 Summary 少掉那句提示，所以每处都写清楚。
        ///
        /// <para>⚠ 2026-10-04（真机）：过去这里只记**个数**，于是 Summary 里只有"该层还有 1 个内层包未展开"，
        /// 用户看不出**是哪一个**（他要自己去工作区里翻）。现在**名单是唯一事实**，个数由名单长度推出来
        /// （<see cref="UnexpandedCount"/>）—— 数与名不可能对不上。</para>
        /// </summary>
        private sealed class EnqueueState
        {
            public Queue<WorkItem> Pending { get; init; } = new();

            /// <summary>本层没有入队的内层归档（停因是"多分支不展开"时才有意义）。</summary>
            public List<string> UnexpandedNames { get; } = new();

            /// <summary>本层没有入队的内层归档数量（= <see cref="UnexpandedNames"/> 的长度，⛔ 不另数一遍）。</summary>
            public int UnexpandedCount => UnexpandedNames.Count;

            /// <summary>这一层哪几个没入队（唯一写入点：四条提前返回各自调一次）。</summary>
            public void MarkUnexpanded(IEnumerable<string>? archivePaths)
            {
                UnexpandedNames.Clear();

                if (archivePaths == null)
                {
                    return;
                }

                foreach (string path in archivePaths)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        UnexpandedNames.Add(path);
                    }
                }
            }
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

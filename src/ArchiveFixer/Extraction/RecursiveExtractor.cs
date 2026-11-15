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
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction;


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
///
/// ⚠ 注入依赖、可配档位与跨层账本（"存放"的那一半）已经抽到 <see cref="RecursiveCoreState"/>：
/// 本文件只保留公开 API、常量、层入口与转发，状态的生命周期说明跟状态放在一起。
/// </summary>
public sealed partial class RecursiveExtractor
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

    /// <summary>
    /// 本次运行的注入依赖、可配档位与跨层账本：存储与装配都在 <see cref="RecursiveCoreState"/>。
    ///
    /// <para>本类只保留公开 API 与下面那些转发 —— 名字、可空性、默认值与抽类之前**逐字相同**；
    /// ⛔ 不许在别处再存第二份（那样"谁在什么时候清"就没有唯一答案了）。</para>
    /// </summary>
    private readonly RecursiveCoreState _core;

    /// <summary>层准备（确定实际归档、解压选项、密码候选和本层日志标签）。</summary>
    private readonly RecursiveLayerPlanner _layerPlanner;

    /*
     * ===== 内部 partial / 协作类仍在直接使用的转发 =====
     *
     * 这些名字是 LayerExecutor、CandidateFailureClassifier、RecursiveResultBuilder、
     * RecursiveLayerExpansion、RecursiveExtractor.Result 等既有代码已经在用的出口，
     * 成员身份与可空性一个字都不能变（⛔ 不许改名，也不许在别处再存第二份）。
     */

    private IArchiveEngine _engine => _core.Engine;

    private IArchiveProber _prober => _core.Prober;

    private Func<string, IReadOnlyList<string>> _passwordProvider => _core.PasswordProvider;

    private RecursionLimits _limits => _core.Limits;

    private Action<string, string>? _log => _core.Log;

    private Func<ArchiveTask, Task<string?>>? _sourceCheck => _core.SourceCheck;

    private bool _exactStopReasonReported { get => _core.ExactStopReasonReported; set => _core.ExactStopReasonReported = value; }

    private string _rootSourcePath { get => _core.RootSourcePath; set => _core.RootSourcePath = value; }

    private IReadOnlyList<VolumeCandidate>? _rootSourceCandidates
    {
        get => _core.RootSourceCandidatesCache;
        set => _core.RootSourceCandidatesCache = value;
    }

    private List<string> _crossBoundaryLinks => _core.CrossBoundaryLinks;

    private List<string> _consumedVolumeSources => _core.ConsumedVolumeSources;

    private List<string> _unresolvedVolumePieces => _core.UnresolvedVolumePieces;

    private List<ExtractionWorkspace> _supersededWorkspaces => _core.SupersededWorkspaces;

    private string _lastRunArchivePath { get => _core.LastRunArchivePath; set => _core.LastRunArchivePath = value; }

    /*
     * ===== 公开 API / 单测出口 =====
     *
     * 存储与完整说明（"为什么要有它、谁在什么时候写"）都在 RecursiveCoreState；
     * 这里只留调用方要的签名与最短契约，⛔ 不复制判断逻辑。
     */

    /// <summary>
    /// 空间不足模式保留「最后一层过程物」的最终根目录（协调器设成 task.OutputPath）；
    /// 空 = 退回老口径（工作区里的 kept 目录）。完整说明见 <see cref="RecursiveCoreState"/>。
    /// </summary>
    public string? RetainedArtifactDirectory
    {
        get => _core.RetainedArtifactDirectory;
        set => _core.RetainedArtifactDirectory = value;
    }

    /// <summary>空间不足模式在逐层回收**之前**已经保留到最终其余物的那一份（最后发现的为准）。</summary>
    public string RetainedProcessArtifactPath
    {
        get => _core.RetainedProcessArtifactPath;
        private set => _core.RetainedProcessArtifactPath = value;
    }

    /// <summary>
    /// 先测试再解压（默认 false = 与未接这条能力之前逐字相同）。
    /// 完整说明见 <see cref="RecursiveCoreState"/>。
    /// </summary>
    public bool TestBeforeExtract
    {
        get => _core.TestBeforeExtract;
        set => _core.TestBeforeExtract = value;
    }

    /// <summary>每次真正调用引擎后的观察口（不参与判据；完整说明见 <see cref="RecursiveCoreState"/>）。</summary>
    public Action<ArchiveOperationResult, string, string>? EngineCallObserved
    {
        get => _core.EngineCallObserved;
        set => _core.EngineCallObserved = value;
    }

    /// <summary>停因判定点（不参与判据；完整说明见 <see cref="RecursiveCoreState"/>）。</summary>
    public Action<RecursionStopReason, string, int, string, string>? StopReasonObserved
    {
        get => _core.StopReasonObserved;
        set => _core.StopReasonObserved = value;
    }

    /// <summary>本实例自己持有的本次（最近一次）运行工作区（完整说明见 <see cref="RecursiveCoreState"/>）。</summary>
    public ExtractionWorkspace? CurrentWorkspace
    {
        get => _core.CurrentWorkspace;
        private set => _core.CurrentWorkspace = value;
    }

    /// <summary>内容物保留关键词（命中即当普通内容物；完整说明见 <see cref="RecursiveCoreState"/>）。</summary>
    public ContentKeepRules KeepRules
    {
        get => _core.KeepRules;
        set => _core.KeepRules = value;
    }

    /// <summary>递归路逐层回收的落点（null = 一个字节都不动；完整说明见 <see cref="RecursiveCoreState"/>）。</summary>
    public Action<RecursiveLayerReclaimRequest>? LayerReclaim
    {
        get => _core.LayerReclaim;
        set => _core.LayerReclaim = value;
    }

    /// <summary>这一趟打不开的分卷片抢救目录（空 = 不抢救；完整说明见 <see cref="RecursiveCoreState"/>）。</summary>
    public string? UnresolvedPieceRescueDirectory
    {
        get => _core.UnresolvedPieceRescueDirectory;
        set => _core.UnresolvedPieceRescueDirectory = value;
    }

    /// <summary>详细日志档（默认关；完整说明见 <see cref="RecursiveCoreState"/>）。</summary>
    public bool VerboseLog
    {
        get => _core.VerboseLog;
        set => _core.VerboseLog = value;
    }

    /// <summary>把密码候选描述成"能写进日志的那半句"（必须已脱敏；完整说明见 <see cref="RecursiveCoreState"/>）。</summary>
    public Func<string, int, string>? DescribeCandidate
    {
        get => _core.DescribeCandidate;
        set => _core.DescribeCandidate = value;
    }

    /// <summary>某一层的密码被验证成功的回传口（只回传、不落地；完整说明见 <see cref="RecursiveCoreState"/>）。</summary>
    public Action<string>? PasswordVerified
    {
        get => _core.PasswordVerified;
        set => _core.PasswordVerified = value;
    }

    /// <summary>「停止后续」的信号（可空；完整说明见 <see cref="RecursiveCoreState"/>）。</summary>
    internal Func<bool>? StopRequested
    {
        get => _core.StopRequested;
        set => _core.StopRequested = value;
    }

    /// <summary>「可疑条目提示」的回传口（可空；完整说明见 <see cref="RecursiveCoreState"/>）。</summary>
    internal Action<string>? DangerousEntriesReported
    {
        get => _core.DangerousEntriesReported;
        set => _core.DangerousEntriesReported = value;
    }

    /// <summary>这一片要不要交给本单的递归去解（可空；完整说明见 <see cref="RecursiveCoreState"/>）。</summary>
    internal Func<string, bool>? ShouldStopAtVolumeGroupMember
    {
        get => _core.ShouldStopAtVolumeGroupMember;
        set => _core.ShouldStopAtVolumeGroupMember = value;
    }

    /// <summary>要不要统计可疑条目（默认开；完整说明见 <see cref="RecursiveCoreState"/>）。</summary>
    public bool ReportDangerousEntries
    {
        get => _core.ReportDangerousEntries;
        set => _core.ReportDangerousEntries = value;
    }

    /// <summary>
    /// **曾经交给"逐层回收"去处理的那几个过程物**（绝对路径）—— 唯一消费点是发布那一步
    /// （<see cref="ExtractionWorkspace.Publish"/> 的 <c>alreadyReclaimedPaths</c>）。
    /// 完整说明（为什么要有这份账）见 <see cref="RecursiveCoreState"/>。
    /// </summary>
    internal HashSet<string> ReclaimedProcessArtifacts => _core.ReclaimedProcessArtifacts;

    /// <summary>
    /// 规范卷名那一份候选：转调 <see cref="RecursiveSourceCandidatePool.PreferCanonicalSiblingName"/>。
    /// 判据、异常兜底与"只换交给引擎的那一份"全部在原出口，本类只保留既有调用点要的签名。
    /// </summary>
    internal static string PreferCanonicalSiblingName(string archivePath)
        => RecursiveSourceCandidatePool.PreferCanonicalSiblingName(archivePath);

    /// <summary>
    /// 找卷窗口里那批候选（懒算一次：同一条链里每一层都问同一份答案；算不出来就是空池）。
    ///
    /// <para>窗口基准的说明在 <see cref="RecursiveCoreState.RootSourcePath"/>；每一趟开始时由
    /// <c>RecursiveExtractor.ExtractAsync.cs</c> 的 <c>BeginRunVolumeWindow</c> 把缓存置空。
    /// 枚举不动 = 判不出：空池 ⇒ 那一档什么都不做（⛔ 不抛异常给上层）。</para>
    /// </summary>
    internal IReadOnlyList<VolumeCandidate> RootSourceCandidates =>
        _rootSourceCandidates ??= RecursiveSourceCandidatePool.EnumerateNearby(_rootSourcePath);

    /// <summary>未凑齐分卷片的记账与抢救（完整说明见 <see cref="RecursiveCoreState"/>）。</summary>
    internal void RememberUnresolvedVolumePiece(string? path)
        => _core.RememberUnresolvedVolumePiece(path);

    /// <summary>跨盘 ZIP 临时链名的清理（完整说明见 <see cref="RecursiveCoreState"/>）。</summary>
    private void RemoveCrossBoundaryLinks()
        => _core.RemoveCrossBoundaryLinks();

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
        _core = new RecursiveCoreState(engine, prober, passwordProvider, limits, log, sourceCheck);
        _layerPlanner = new RecursiveLayerPlanner(BuildPasswordCandidates, Log);
    }

    private async Task<LayerOutcome> ExtractLayerAsync(
        WorkItem item,
        CancellationToken cancellationToken,
        IProgress<ArchiveProgress>? progress = null,
        Action<ArchiveStallNotice>? stalled = null,
        string? carvedSource = null)
    {
        _exactStopReasonReported = false;

        LayerPlan plan = _layerPlanner.Prepare(item.Depth, item.ArchivePath, carvedSource, progress, VerboseLog);

        return await RecursiveLayerExpansion.ExecuteAsync(
                this,
                item,
                plan,
                carvedSource,
                cancellationToken,
                progress,
                stalled,
                (reason, message) => ReportStopReason(reason, message))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 缺卷判据的宿主出口：转调 <see cref="RecursiveVolumeGroupGate"/>（唯一判据仍在那个出口）。
    /// </summary>
    internal static bool IsGroupEntryPresentBeside(string piecePath)
        => RecursiveVolumeGroupGate.IsGroupEntryPresentBeside(piecePath);

    /// <summary>
    /// 「这一份是不是某个还没凑齐的分卷组的一员」：转调 <see cref="RecursiveVolumeGroupGate"/>。
    /// </summary>
    internal static bool IsPieceOfIncompleteGroup(string archivePath, bool requireNothingElseBeside = false)
        => RecursiveVolumeGroupGate.IsPieceOfIncompleteGroup(archivePath, requireNothingElseBeside);

    /// <summary>单测用：把"这一份是不是某个还没凑齐的分卷组的一员"那个**唯一出口**露给测试项目。</summary>
    internal static bool IsPieceOfIncompleteGroupForTests(string path) => IsPieceOfIncompleteGroup(path);

    /// <summary>单测用：把"同组入口在不在它旁边"那一半露给测试项目（用来钉住它的**已知限制**）。</summary>
    internal static bool IsGroupEntryPresentBesideForTests(string path) => IsGroupEntryPresentBeside(path);

    /// <summary>
    /// 发布前的产物校验：转调 <see cref="RecursivePublishGate.CandidateProductsVerified"/>。
    /// 判据仍是 <see cref="OutputVerifier.Verify"/> 那一个出口。
    /// </summary>
    internal static bool CandidateProductsVerified(
        WorkItem item,
        LayerManifest candidateManifest,
        out string message,
        out OutputVerificationResult verification)
    {
        return RecursivePublishGate.CandidateProductsVerified(
            item,
            candidateManifest,
            out message,
            out verification);
    }

}

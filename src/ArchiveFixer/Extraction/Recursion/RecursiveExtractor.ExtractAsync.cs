using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction;

public sealed partial class RecursiveExtractor
{
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
    /// <remarks>
    /// 实现已按职责搬进 <see cref="RecursiveExtractHandler"/>（主循环是它，不是本类的方法体）：
    /// 本方法只留同签名薄转发，⛔ 判据、顺序与写穿字段一个字没改。
    /// </remarks>
    public Task<RecursionResult> ExtractAsync(
        ArchiveTask task,
        string finalOutputDirectory,
        RecursionDecisionRequest? previousDecision = null,
        CancellationToken cancellationToken = default,
        IProgress<ArchiveProgress>? progress = null,
        Action<ArchiveStallNotice>? stalled = null)
        => new RecursiveExtractHandler(this).ExtractAsync(
            task,
            finalOutputDirectory,
            previousDecision,
            cancellationToken,
            progress,
            stalled);

    /*
     * ===== 主循环（RecursiveExtractHandler）的宿主出口 =====
     *
     * 这一段过去全部写在本文件里、直接读写私有字段 / 调私有方法；搬类之后只把**它本来就在用**
     * 的那几个出口按 internal 露出来（行为一个字不改：判据、清法、写穿字段仍是同一份）。
     * 账本、工作区、找卷窗口与上限**仍然只有本类这一个实例的**那一份，⛔ 不新增状态。
     */

    /// <summary>建本次运行的工作区（转调本类那个唯一出口，命名规则一个字不改）。</summary>
    internal static ExtractionWorkspace CreateRunWorkspace(ArchiveTask task) => CreateWorkspace(task);

    /// <summary>本次运行的工作区（写入口）—— 与 <see cref="CurrentWorkspace"/> 是**同一个**槽位。</summary>
    internal void SetCurrentWorkspace(ExtractionWorkspace workspace) => CurrentWorkspace = workspace;

    /// <summary>
    /// 这一趟"找卷窗口"的基准 = 最外层源包（每趟重算一次：续跑时源包可能已被改名 / 搬走），
    /// 同时作废上一趟懒算的那批候选。
    /// </summary>
    internal void BeginRunVolumeWindow(string rootSourcePath)
    {
        _rootSourcePath = rootSourcePath;
        _rootSourceCandidates = null;
    }

    /// <summary>最近一次运行解的是哪个归档（判"这一次是不是同一个归档的续跑"）。</summary>
    internal string LastRunArchivePath
    {
        get => _lastRunArchivePath;
        set => _lastRunArchivePath = value;
    }

    /// <summary>记下被本次续跑取代的那个工作区（多分支询问 → 用户确认继续）。</summary>
    internal void AddSupersededWorkspace(ExtractionWorkspace workspace) => _supersededWorkspaces.Add(workspace);

    /// <summary>换归档（不是续跑）时整份清空：那些工作区属于**别的**任务，不能由本次成功去清它们。</summary>
    internal void ClearSupersededWorkspaces() => _supersededWorkspaces.Clear();

    /// <summary>
    /// 这一趟另外那三本账都从零开始（同一个实例可能被续跑复用，上一趟记的路径在这一趟就是别人的事）：
    /// 跨盘 ZIP 临时链接（必须删干净）、"借来用过的用户源片"、"打不开的那几片"。
    /// </summary>
    internal void ResetRunLedgers()
    {
        _crossBoundaryLinks.Clear();
        _consumedVolumeSources.Clear();
        _unresolvedVolumePieces.Clear();
    }

    /// <summary>本层是否已在更精确的分支里报过停因（主循环读一次、读完清回 false）。</summary>
    internal bool ExactStopReasonReported
    {
        get => _exactStopReasonReported;
        set => _exactStopReasonReported = value;
    }

    /// <summary>每层开工前的硬上限（顺序固定：层数 → 累计文件数 → 累计大小；展开比不在这里查）。</summary>
    internal RecursionStopReason CheckLayerLimits(
        WorkItem item,
        List<RecursionLayerReport> layers,
        int totalFiles,
        long totalSize)
        => CheckLimitsBeforeLayer(item, layers, totalFiles, totalSize);

    /// <summary>展开比检查最多对前若干层做（上限保护，与私有常量同一份数字）。</summary>
    internal const int MaxExpansionRatioCheckLayers = MaxExpansionRatioChecks;

    /// <summary>单层展开比检查（async；⛔ 绝不同步等待，理由见那个私有出口）。</summary>
    internal Task<bool> IsLayerExpansionRatioExceededAsync(
        WorkItem item,
        List<RecursionLayerReport> layers,
        Queue<WorkItem> pending)
        => IsExpansionRatioExceededAsync(item, layers, pending);

    /// <summary>不变量 11 的检查口（只在第 0 层开工之前问一次；可空 = 不做这件事）。</summary>
    internal Func<ArchiveTask, Task<string?>>? RootSourceCheck => _sourceCheck;

    /// <summary>解一层（收卷预检 + 候选循环）：主循环每取出一项就调这里一次。</summary>
    internal Task<LayerOutcome> ExecuteLayerAsync(
        WorkItem item,
        CancellationToken cancellationToken,
        IProgress<ArchiveProgress>? progress,
        Action<ArchiveStallNotice>? stalled)
        => ExtractLayerAsync(item, cancellationToken, progress, stalled);

    /// <summary>跨盘 ZIP 临时链名的清理（这一层一跑完就删；只删本工作区里我们自己建的那几个名字）。</summary>
    internal void PruneCrossBoundaryLinks() => RemoveCrossBoundaryLinks();

    /// <summary>
    /// 入队判定：内部建 <c>EnqueueState</c>，把"这一层还有哪几个内层包没展开"按原口径带出来
    /// （只有停下那一支才取这份名单 —— 与搬出前同一时刻、同一份 <c>ToList()</c>）。
    /// </summary>
    internal bool EnqueueNextLayers(
        WorkItem item,
        RecursionLayerReport report,
        ExtractionWorkspace workspace,
        Queue<WorkItem> pending,
        out RecursionDecisionRequest? askUser,
        out RecursionStopReason enqueueStopReason,
        out IReadOnlyList<string> unexpandedNames)
    {
        var state = new EnqueueState
        {
            Pending = pending
        };

        bool enqueued = TryEnqueueNextLayers(item, report, workspace, state, out askUser, out enqueueStopReason);

        unexpandedNames = Array.Empty<string>();

        if (!enqueued)
        {
            unexpandedNames = state.UnexpandedNames.ToList();
        }

        return enqueued;
    }

    /// <summary>工作区根 → 目标目录（认 <c>.ArchiveFixer.work</c> 这个名字，⛔ 不扫盘）。</summary>
    internal static string? ResolveRunOutputRoot(string workspaceRoot) => ResolveWorkspaceOutputRoot(workspaceRoot);

    /// <summary>空间不足模式保留"最后一层过程物"的那个根目录（与收尾那一档同一个出口）。</summary>
    internal string? ResolveRetainedRoot(string outputRoot) => ResolveRetainedArtifactRoot(outputRoot);

    /// <summary>组装最终结论（发布 / 部分完成 / 工作区在哪全部由它一处决定）。</summary>
    internal RecursionResult BuildRunResult(
        RecursionStopReason stopReason,
        List<RecursionLayerReport> layers,
        RecursionDecisionRequest? decision,
        ExtractionWorkspace workspace,
        string finalOutputDirectory,
        string extraMessage,
        bool published,
        IReadOnlyList<string>? unexpandedNames = null,
        string passwordFallbackNote = "",
        Exception? failureException = null)
        => BuildResult(
            stopReason,
            layers,
            decision,
            workspace,
            finalOutputDirectory,
            extraMessage,
            published,
            unexpandedNames,
            passwordFallbackNote,
            failureException);

    /// <summary>一次运行的收尾（按结论决定清不清本次运行自己建的那个工作区）。</summary>
    internal RecursionResult CloseRun(
        RecursionResult result,
        ExtractionWorkspace workspace,
        string taskLabel,
        CancellationToken cancellationToken)
        => FinalizeRun(result, workspace, taskLabel, cancellationToken);

    /// <summary>
    /// 把这一层在工作区里的产物目录清空并重建（换密码候选之前调用）：
    /// 实现已搬进 <see cref="RecursiveExtractHandler.TryResetLayerOutputDirectory"/>，
    /// 签名、"只动传进来的那个目录"的安全边界与异常兜底一个字不改。
    /// </summary>
    internal static void TryResetLayerOutputDirectory(string? directory)
        => RecursiveExtractHandler.TryResetLayerOutputDirectory(directory);
}

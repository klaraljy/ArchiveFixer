using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


namespace ArchiveFixer.ViewModels;

/// <summary>成功后的收尾（源包处理 / 其余物 / 发布结果；第 13 块，同一 partial 类）。</summary>
internal sealed partial class ExtractionCoordinator
{
    /// <summary>
    /// 收尾重活的产物：后台线程只负责碰文件系统，结论与日志都通过它回传给 UI 线程，
    /// 免得后台线程去动界面集合 / 任务状态。
    ///
    /// <para>⚠ 类型本身留在协调器上（不搬去 <see cref="PostProcessSuccessHandler"/>）：产出它的
    /// <c>RunPostProcessWork</c> 仍在 <c>ExtractionCoordinator.Source.cs</c>，那里要 <c>new</c> 它。</para>
    /// </summary>
    internal sealed class PostProcessWorkResult
    {
        public OutputVerificationResult Verification { get; init; } = new();

        public List<(string Level, string Message)> LogEntries { get; init; } = new();

        public bool BudgetExceeded { get; init; }

        public string BudgetMessage { get; init; } = string.Empty;

        /// <summary>
        /// 产物越出目标根目录的说明（非 null = **不承认**这次解压：不归集、不清理源包）。
        /// 它必须走"结论"这条路回传，而不是像旧实现那样只往日志里塞一行 ERROR。
        /// </summary>
        public string? LandingViolation { get; init; }

        public CollectResult? Collected { get; init; }

        /// <summary>定稿（暂存区 → 最终目录）的结果；没走到定稿这一步时为 null。</summary>
        public StageCommitResult? Commit { get; init; }

        /// <summary>
        /// 定稿**整体失败**（一个内容物都没能搬进最终目录）：与越界同一口径 —— 结论不成立。
        /// 与 <see cref="LandingViolation"/> 分开写，是为了让状态与原因各自说自己的事，
        /// 别让"越界"这个词出现在一句讲搬运失败的提示里。
        /// </summary>
        public bool CommitFailed { get; init; }

        public string CommitFailureMessage { get; init; } = string.Empty;

        /// <summary>
        /// 源包没能移入其余物（非 null = **任务要标成"部分完成"**）。
        ///
        /// 与越界/预算同一口径地走"结论"这条路回传：内容物已经好了，这件事只影响任务的完成度，
        /// 所以它**不**顶掉"解压成功"，但也绝不允许被静默吞掉（D-12：移动失败要写明原因）。
        /// </summary>
        public string? SourceMoveFailure { get; init; }
    }

    /// <summary>
    /// 解压成功之后的收尾：**定稿搬运** → 校验 → 归集 → 视开关清理源包（M3/契约 §2）。
    ///
    /// <para>⚠ 实现已整体搬进 <see cref="PostProcessSuccessHandler"/>（从 <c>ExtractionCoordinator</c>
    /// 搬出的收尾协作类，本 partial 只留这一行转发）；顺序、判据、日志与搬出前逐字一致，
    /// 线程规则 / 取消规则 / 三个例外见那边的 XML 注释。</para>
    /// </summary>
    internal Task<bool> PostProcessSuccessAsync(
        ArchiveTask task,
        string engineArchivePath,
        string password,
        string stageDirectory,
        string outputRedirectNote,
        bool sharedOutputRoot,
        bool oneClickRun,
        CancellationToken cancellationToken,
        ArchiveListResult? knownList = null,
        RecursionResult? recursion = null) =>
        new PostProcessSuccessHandler(this).PostProcessSuccessAsync(
            task,
            engineArchivePath,
            password,
            stageDirectory,
            outputRedirectNote,
            sharedOutputRoot,
            oneClickRun,
            cancellationToken,
            knownList,
            recursion);

    /*
     * ===== 供 PostProcessSuccessHandler 使用的访问器 =====
     *
     * 只登记"搬出去的那一步真正碰到的私有成员"，一行转发、不加判据；已经在
     * ServiceForwarding / VolumeHandoffStep / LogCaptureStep 里 internal 的那些直接复用
     * （ArchiveEngine / Settings / RunOptions / IsStopping / _operationCts / AppendLog /
     * AppendLogPair / CriterionLog / StopAfterCurrent / ResolveRestDirectoryAfterCollect / FirstStepLedger）。
     * ⛔ 不用反射，也不把私有状态再抄一份到协作类里。
     */

    /// <summary>手动单层入口（= 这一层写出来的都是用户要的内容物）。</summary>
    internal bool ManualSingleLayerThisRun
    {
        get => _manualSingleLayerThisRun;
        set => _manualSingleLayerThisRun = value;
    }

    /// <summary>同名冲突处理档（唯一来源在 <c>ConflictStep</c>；空 / 非法回落 AutoRename）。</summary>
    internal string ConflictActionValueForStep => ConflictActionValue;

    /// <summary>本任务已经答过的同名冲突决定（判不出 = null）。</summary>
    internal ConflictDecision? GetEffectiveConflictDecisionForStep() => GetEffectiveConflictDecision();

    /// <summary>无界面宿主：问不到冲突档位，只能走保守档。</summary>
    internal bool ConflictPromptUnavailableForStep => IsConflictPromptUnavailable;

    /// <summary>定稿前的同名冲突预检（磁盘活，调用方负责 <c>Task.Run</c>）。</summary>
    internal ConflictPrecheck PrecheckFinalLayoutConflictsForStep(
        ArchiveTask task,
        string stageDirectory,
        string destinationDirectory,
        bool sharedOutputRoot,
        TerminalLayoutMode terminalLayout,
        RecursionResult? recursion = null,
        bool stageFilesAreProducedContent = false) =>
        PrecheckFinalLayoutConflicts(
            task,
            stageDirectory,
            destinationDirectory,
            sharedOutputRoot,
            terminalLayout,
            recursion,
            stageFilesAreProducedContent);

    /// <summary>问用户同名冲突怎么处理（UI 活，可取消；无界面宿主 / 超时 = null）。</summary>
    internal Task<ConflictDecision?> AskConflictForStepAsync(
        ArchiveTask task,
        string message,
        string detail,
        CancellationToken cancellationToken) =>
        AskConflictAsync(task, message, detail, cancellationToken);

    /// <summary>冲突预检里"其中几个是内容物 / 都在其余物里"那句话。</summary>
    internal static string BuildConflictDetailForStep(ConflictPrecheck precheck) => BuildConflictDetail(precheck);

    /// <summary>收尾重活本体（同步磁盘活，只能在后台线程上跑；见 <c>RunPostProcessWork</c>）。</summary>
    internal PostProcessWorkResult RunPostProcessWorkForStep(
        ArchiveTask task,
        string stageDirectory,
        ArchiveListResult expected,
        bool collectResults,
        string collectTargetDirectory,
        SourceHandlingMode sourceHandling,
        bool sharedOutputRoot,
        TerminalLayoutMode terminalLayout,
        bool oneClickRun,
        string conflictAction,
        ConflictDecision? conflictDecision,
        CancellationToken cancellationToken,
        RecursionResult? recursion = null,
        string expectedRootPath = "") =>
        RunPostProcessWork(
            task,
            stageDirectory,
            expected,
            collectResults,
            collectTargetDirectory,
            sourceHandling,
            sharedOutputRoot,
            terminalLayout,
            oneClickRun,
            conflictAction,
            conflictDecision,
            cancellationToken,
            recursion,
            expectedRootPath);

    /// <summary>内容物实际落地的那一层（按归集后的位置换算）。</summary>
    internal static string ResolveContentDirectoryAfterCollectForStep(
        StageCommitResult commit,
        CollectResult? collected) =>
        ResolveContentDirectoryAfterCollect(commit, collected);

    /// <summary>带错误码的日志入口（失败清单写入器的四参数注入点）。</summary>
    internal void AppendLogWithCodeForStep(string level, string message, string errorCode, string component) =>
        AppendLogWithCode(level, message, errorCode, component);

    /// <summary>L4 那一行证据（"这次通过是核对过清单还是只做了非空底线"）。</summary>
    internal void AppendCompletenessEvidenceForStep(
        ArchiveTask task,
        OutputVerificationResult verification,
        SourceHandlingMode sourceHandling) =>
        AppendCompletenessEvidence(task, verification, sourceHandling);

    /// <summary>中间工作区清理（同步磁盘活，调用方负责 <c>Task.Run</c>）。</summary>
    internal void CleanupTaskWorkspaceDirectoryForStep(ArchiveTask task, string stageDirectory) =>
        CleanupTaskWorkspaceDirectory(task, stageDirectory);

    /// <summary>「定稿完成后打开输出目录」的批记账入口（本批只开一次）。</summary>
    internal bool TryOpenOutputFolderOnceForStep(ArchiveTask task) => TryOpenOutputFolderOnce(task);
}

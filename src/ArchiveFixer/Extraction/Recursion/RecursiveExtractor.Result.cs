using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Threading;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 递归解压的「结论落地」（组装最终结论 / 收尾 / 保留最后一层过程物）——
/// 实现已整块搬到协作类 <see cref="RecursiveResultHandler"/>，这一分片只留同签名薄转发 + 最小 internal 访问器。
///
/// <para>⛔ 判据、日志顺序、调用顺序一个字都没改：调用点（<c>ExtractAsync</c> 的三处结论出口、
/// <c>InnerDiscovery</c> 的那一处保留点）一处未动。</para>
/// </summary>
public sealed partial class RecursiveExtractor
{
    /// <summary>搬出去的那一份实现（懒建一次；宿主就是本类，全类共用同一个实例）。</summary>
    private RecursiveResultHandler? _resultHandler;

    private RecursiveResultHandler ResultHandler => _resultHandler ??= new RecursiveResultHandler(this);

    /// <summary>薄转发：实现见 <see cref="RecursiveResultHandler.BuildResult"/>（签名、默认值、可见性一字不改）。</summary>
    private RecursionResult BuildResult(
        RecursionStopReason stopReason,
        List<RecursionLayerReport> layers,
        RecursionDecisionRequest? decision,
        ExtractionWorkspace workspace,
        string finalOutputDirectory,
        string extraMessage,
        bool published,
        IReadOnlyList<string>? unexpandedNames = null,
        string passwordFallbackNote = "",
        Exception? failureException = null) =>
        ResultHandler.BuildResult(
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

    /// <summary>薄转发：实现见 <see cref="RecursiveResultHandler.RetainProcessArtifactBeforeConsumption"/>。</summary>
    private void RetainProcessArtifactBeforeConsumption(string archivePath) =>
        ResultHandler.RetainProcessArtifactBeforeConsumption(archivePath);

    /// <summary>薄转发：实现见 <see cref="RecursiveResultHandler.FinalizeRun"/>。</summary>
    private RecursionResult FinalizeRun(
        RecursionResult result,
        ExtractionWorkspace workspace,
        string taskLabel,
        CancellationToken cancellationToken) =>
        ResultHandler.FinalizeRun(result, workspace, taskLabel, cancellationToken);

    /// <summary>
    /// 薄转发：实现见 <see cref="RecursiveResultHandler.ResolveRetainedArtifactRoot"/>。
    ///
    /// <para>⚠ 本分片之外的另一个调用方 = <c>ExtractAsync</c> 那一分片的宿主访问器
    /// <c>ResolveRetainedRoot</c>（给 <c>RecursiveExtractHandler</c> 用）⇒ 实现搬走后必须留同签名转发，
    /// ⛔ 不能因为"本分片没人调用"就删。</para>
    /// </summary>
    private string? ResolveRetainedArtifactRoot(string outputRoot) =>
        ResultHandler.ResolveRetainedArtifactRoot(outputRoot);

    /// <summary>薄转发：实现见 <see cref="RecursiveResultHandler.ResolveWorkspaceOutputRoot"/>（静态：不读实例状态）。</summary>
    private static string? ResolveWorkspaceOutputRoot(string workspaceRoot) =>
        RecursiveResultHandler.ResolveWorkspaceOutputRoot(workspaceRoot);

    /*
     * ===== 结论落地（RecursiveResultHandler）的宿主出口 =====
     *
     * 协作类要用、但定义在**别的 partial** 里的 private 成员（不改那几个文件）：
     * 这里只加**最小 internal 访问器**，⛔ 不复制判据、⛔ 不用反射（唯一实现仍在原处）。
     *
     * 本来就能直接用的不再另开访问器：Log / ReclaimedProcessArtifacts / Limits / Engine（internal）、
     * LastRunArchivePath（ExtractAsync 那一分片已露 internal）、RetainedArtifactDirectory（public）。
     */

    /// <summary>访问器：转调 <c>Reporting.StopReason</c> 里的 private <c>IsInsideWorkspaceRoot</c>（唯一实现仍在原处）。</summary>
    internal static bool IsInsideWorkspaceRootForResult(string? path) => IsInsideWorkspaceRoot(path);

    /// <summary>访问器：转调 <c>Reporting.Recovery</c> 里的 private <c>WriteRecoveryReport</c>（报告内容一个字未改）。</summary>
    internal void WriteRecoveryReportForResult(
        RecursionStopReason stopReason,
        List<RecursionLayerReport> layers,
        ExtractionWorkspace workspace,
        string finalOutputPath,
        bool completed,
        bool partiallyCompleted) =>
        WriteRecoveryReport(stopReason, layers, workspace, finalOutputPath, completed, partiallyCompleted);

    /// <summary>访问器：转调跨盘 ZIP 临时链接的**唯一清理出口**（只删我们自己刚建的那几个名字）。</summary>
    internal void RemoveCrossBoundaryLinksForResult() => RemoveCrossBoundaryLinks();

    /// <summary>访问器：这一趟**借来用过的用户源片**（账本是本类字段，⛔ 不复制第二份）。</summary>
    internal IReadOnlyList<string> ConsumedVolumeSources => _consumedVolumeSources;

    /// <summary>访问器：这一趟停下时**打不开的那几片**（账本是本类字段，⛔ 不复制第二份）。</summary>
    internal IReadOnlyList<string> UnresolvedVolumePieces => _unresolvedVolumePieces;

    /// <summary>访问器：转调 <c>Reporting.StopReason</c> 里的 private <c>BuildSummary</c>（结论措辞唯一实现）。</summary>
    internal static string BuildSummaryForResult(
        RecursionStopReason stopReason,
        List<RecursionLayerReport> layers,
        ExtractionWorkspace workspace,
        string finalOutputPath,
        bool completed,
        string publishMessage,
        string extraMessage,
        IReadOnlyList<string> unexpandedNames,
        string passwordFallbackNote) =>
        BuildSummary(
            stopReason,
            layers,
            workspace,
            finalOutputPath,
            completed,
            publishMessage,
            extraMessage,
            unexpandedNames,
            passwordFallbackNote);

    /// <summary>访问器：转调 <c>WorkspaceCleanup</c> 里的 private <c>CleanupWorkspace</c>（删除闸门仍在原处）。</summary>
    internal bool CleanupWorkspaceForResult(ExtractionWorkspace workspace, string taskLabel, string reason) =>
        CleanupWorkspace(workspace, taskLabel, reason);

    /// <summary>访问器：转调 <c>WorkspaceCleanup</c> 里的 private <c>CleanupSupersededWorkspaces</c>。</summary>
    internal void CleanupSupersededWorkspacesForResult(string taskLabel) =>
        CleanupSupersededWorkspaces(taskLabel);

    /// <summary>访问器：空间不足模式保留的那一份（<c>private set</c> 在别的 partial，⛔ 不改成 public）。</summary>
    internal void SetRetainedProcessArtifactPath(string path) => RetainedProcessArtifactPath = path;

    /*
     * ===== 失败收尾段（RecursiveResultBuilder）的宿主出口 =====
     *
     * 这一段过去全部写在本类实例方法里，直接用私有字段；搬类之后只把**它本来就在用**的
     * 那几个出口按 internal 露出来（行为一个字不改：清法、报告、停因观察口、写穿字段都同一份）。
     * ⛔ 不新增判据、不新增状态。
     */

    /// <summary>交给换引擎兜底与清单恢复的主引擎（与候选循环用的是**同一个**实例）。</summary>
    internal IArchiveEngine Engine => _engine;

    /// <summary>递归上限：点名"哪几条候选没试到"时要用每层密码尝试上限。</summary>
    internal RecursionLimits Limits => _limits;

    /// <summary>
    /// 换引擎兜底那句结论（"已换 WinRAR 再试过一遍" / "没找到 WinRAR，所以没换引擎再试"）。
    ///
    /// <para>⚠ **写穿到实例字段**、不放进收尾段的局部量：本方法可能被
    /// <see cref="TryCarveEmbeddedInnerArchiveAsync"/> 递归重入，外层收尾时整份回写会把
    /// 内层刚写下的那句话覆盖掉（同一层里两句话只有一句成立）。</para>
    /// </summary>
    internal string PasswordFallbackNote
    {
        get => _passwordFallbackNote;
        set => _passwordFallbackNote = value;
    }

    /// <summary>
    /// 「本层已在更精确的分支里报过停因」——同样**写穿实例字段**（理由同上：重入时外层不许覆盖）。
    /// 调用方 <see cref="ExtractAsync"/> 读一次后会清回 false。
    /// </summary>
    internal void MarkExactStopReasonReported()
    {
        _exactStopReasonReported = true;
    }

    /*
     * ===== 候选循环（LayerExecutor）的宿主出口 =====
     *
     * 同样只把循环本来就在用的两笔账按方法露出来：账本仍是本类这两个字段
     * （谁建谁负责清，见 ExtractAsync 里那两处 Clear），⛔ 不复制第二份。
     */

    /// <summary>跨盘 ZIP 那一档建出来的临时链接（收尾要删干净：留着会被当成内容物发布）。</summary>
    internal void AddCrossBoundaryLinks(IReadOnlyList<string> linkedPaths)
    {
        _crossBoundaryLinks.AddRange(linkedPaths);
    }

    /// <summary>"这一组的内容已由谁解出"的记账（借来用过的用户源片）。</summary>
    internal void AddConsumedVolumeSources(IReadOnlyList<string> linkedSources)
    {
        _consumedVolumeSources.AddRange(linkedSources);
    }

}

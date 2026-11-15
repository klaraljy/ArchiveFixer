using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System.Threading;
using System.Threading.Tasks;


namespace ArchiveFixer.ViewModels;

/// <summary>
/// 递归解压与解前分卷判定（原 <c>ExtractionCoordinator.Recursion.cs</c> 的全部实现）——
/// 实现已整块搬到协作类 <see cref="RecursionHandler"/>，这一分片只留同签名薄转发 + 最小 internal 访问器。
///
/// <para>⛔ 判据、日志顺序、调用顺序一个字都没改；调用点（<c>ExtractionRecursiveStep</c> /
/// <c>ExtractionCoordinator.VolumeStepPreflightStep</c>）一处未动。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    /// <summary>搬出去的那一份递归实现（懒建一次；宿主就是本类，全类共用同一个实例）。</summary>
    private RecursionHandler? _recursionHandler;

    private RecursionHandler Recursion => _recursionHandler ??= new RecursionHandler(this);

    /// <summary>薄转发：实现见 <see cref="RecursionHandler.ResolveVolumeGroupAsync"/>。</summary>
    private Task<VolumeGroupResolution> ResolveVolumeGroupAsync(
        ArchiveTask task,
        CancellationToken cancellationToken) =>
        Recursion.ResolveVolumeGroupAsync(task, cancellationToken);

    /// <summary>薄转发：实现见 <see cref="RecursionHandler.BuildVolumeVerdictMessage"/>。</summary>
    private static string BuildVolumeVerdictMessage(ArchiveTask task, VolumeGroupResolution resolution) =>
        RecursionHandler.BuildVolumeVerdictMessage(task, resolution);

    /// <summary>薄转发：实现见 <see cref="RecursionHandler.LogVolumeGroupEvidence"/>。</summary>
    private void LogVolumeGroupEvidence(ArchiveTask task, VolumeGroupResolution resolution) =>
        Recursion.LogVolumeGroupEvidence(task, resolution);

    /// <summary>薄转发：实现见 <see cref="RecursionHandler.RunRecursiveAsync"/>（参数含义照旧）。</summary>
    internal Task<RecursionResult?> RunRecursiveAsync(
        ArchiveTask task,
        string engineArchivePath,
        string stageDirectory,
        bool oneClickRun,
        CancellationToken cancellationToken,
        int? maxDepth = null) =>
        Recursion.RunRecursiveAsync(
            task,
            engineArchivePath,
            stageDirectory,
            oneClickRun,
            cancellationToken,
            maxDepth);

    /*
     * 协作类要用、但定义在**别的 partial** 里的 private 成员（不改那几个文件）：
     * 这里只加**最小 internal 访问器**，⛔ 不复制判据、⛔ 不用反射（唯一实现仍在原处）。
     */
    /// <summary>访问器：给 <see cref="RecursionHandler"/> 造这一单的递归核心（唯一实现 = 本类）。</summary>
    internal RecursiveExtractor CreateRecursiveExtractorForRecursion(
        int? maxDepth,
        bool manualSingleLayer,
        bool spaceTight) =>
        CreateRecursiveExtractor(maxDepth, manualSingleLayer, spaceTight);

    /// <summary>访问器：给 <see cref="RecursionHandler"/> 判“这一片是不是还没凑齐的分卷组的一员”。</summary>
    internal bool ShouldStopAtCandidateVolumePieceForRecursion(string path, ArchiveTask owner) =>
        ShouldStopAtCandidateVolumePiece(path, owner);

    /// <summary>访问器：给 <see cref="RecursionHandler"/> 记下“这一单用的是哪个递归核心”（收尾要按结论处理它的工作区）。</summary>
    internal void RememberTaskRecursiveExtractor(ArchiveTask task, RecursiveExtractor extractor) =>
        _taskRecursiveExtractors[task] = extractor;

    /// <summary>访问器：给 <see cref="RecursionHandler"/> 在 UI 线程上弹“要不要展开内层归档”的确认框。</summary>
    internal Task<bool> ShowConfirmOnUiThreadForRecursion(string message) =>
        ShowConfirmOnUiThreadAsync(message);

    /// <summary>访问器：给 <see cref="RecursionHandler"/> 把递归结论落到任务上（唯一实现 = 本类）。</summary>
    internal void ApplyRecursionResultForRecursion(ArchiveTask task, RecursionResult result) =>
        ApplyRecursionResult(task, result);

    /// <summary>访问器：给 <see cref="RecursionHandler"/> 问“任务表里已有别单代表这一组了吗”。</summary>
    internal bool AnyTaskRepresentsGroupForRecursion(string piecePath) =>
        AnyTaskRepresentsGroup(piecePath);

    /// <summary>访问器：给 <see cref="RecursionHandler"/> 登记部分完成发布候选（唯一实现 = 本类）。</summary>
    internal void RecordRecursivePartialPublishCandidateForRecursion(ArchiveTask task, RecursionResult? result) =>
        RecordRecursivePartialPublishCandidate(task, result);

    /// <summary>访问器：给 <see cref="RecursionHandler"/> 按③页档位处置最后一层过程物（唯一实现 = 本类）。</summary>
    internal void PreserveStoppedLayerProcessArtifactForRecursion(ArchiveTask task, RecursionResult? result) =>
        PreserveStoppedLayerProcessArtifact(task, result);

    /// <summary>访问器：给 <see cref="RecursionHandler"/> 搬“用户只保留这一层”的产物（唯一实现 = 本类）。</summary>
    internal static int MoveDirectoryContentForRecursion(string sourceDirectory, string targetDirectory) =>
        MoveDirectoryContent(sourceDirectory, targetDirectory);

    /// <summary>访问器：给 <see cref="RecursionHandler"/> 判“这份工作区能不能安全清掉”（唯一实现 = 本类）。</summary>
    internal static bool HasNoFilesForRecursion(string? directory) => HasNoFiles(directory);
}

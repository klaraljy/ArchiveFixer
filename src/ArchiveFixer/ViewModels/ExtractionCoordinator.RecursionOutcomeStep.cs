using ArchiveFixer.Extraction;
using ArchiveFixer.Models;


namespace ArchiveFixer.ViewModels;

/// <summary>
/// 递归结论落地到任务（原 <c>ExtractionCoordinator.RecursionOutcomeStep.cs</c> 的全部实现）——
/// 实现已整块搬到协作类 <see cref="RecursionOutcomeHandler"/>，这一分片只留同签名薄转发 + 最小 internal 访问器。
///
/// <para>⛔ 判据、日志顺序、调用顺序一个字都没改；调用点（<c>ExtractionCoordinator.Recursion.cs</c> 的
/// 访问器、<c>RecursionHandler</c>）一处未动。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    /// <summary>搬出去的那一份实现（懒建一次；宿主就是本类，全类共用同一个实例）。</summary>
    private RecursionOutcomeHandler? _recursionOutcomeHandler;

    private RecursionOutcomeHandler RecursionOutcome => _recursionOutcomeHandler ??= new RecursionOutcomeHandler(this);

    /// <summary>薄转发：实现见 <see cref="RecursionOutcomeHandler.ApplyRecursionResult"/>。</summary>
    private void ApplyRecursionResult(ArchiveTask task, RecursionResult result) =>
        RecursionOutcome.ApplyRecursionResult(task, result);

    /// <summary>薄转发：实现见 <see cref="RecursionOutcomeHandler.MoveDirectoryContent"/>
    /// （调用方 = <c>RecursionHandler</c>，经 <c>MoveDirectoryContentForRecursion</c> 访问器）。</summary>
    private static int MoveDirectoryContent(string sourceDirectory, string targetDirectory) =>
        RecursionOutcomeHandler.MoveDirectoryContent(sourceDirectory, targetDirectory);

    /// <summary>薄转发：实现见 <see cref="RecursionOutcomeHandler.HasNoFiles"/>
    /// （调用方 = <c>RecursionHandler</c>，经 <c>HasNoFilesForRecursion</c> 访问器）。</summary>
    private static bool HasNoFiles(string? directory) => RecursionOutcomeHandler.HasNoFiles(directory);

    /*
     * 协作类要用、但定义在**别的 partial** 里的 private 成员（不改那几个文件）：
     * 这里只加**最小 internal 访问器**，⛔ 不复制判据、⛔ 不用反射（唯一实现仍在原处）。
     *
     * 已经在 LogCaptureStep / VolumeLedgerStep / ExtractionCoordinator.cs 里 internal 的那些直接复用：
     * AppendLog / CriterionLog / _consumedVolumeSources / _vm。
     */

    /// <summary>访问器：给 <see cref="RecursionOutcomeHandler"/> 写「产物越界」注记（唯一实现 = 终态收口分片）。</summary>
    internal void ApplyLandingViolationNoteForRecursionOutcome(ArchiveTask task, RecursionResult result) =>
        ApplyLandingViolationNote(task, result);
}

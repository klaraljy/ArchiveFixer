using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System.Collections.Generic;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 内容组最终内容判决：任务级成功不代替组级最终内容。
///
/// <para>实现已整块搬进 <see cref="ContentGroupFinalizerHandler"/>
/// （<c>ViewModels/Extraction/Steps/ContentGroupFinalizerHandler.cs</c>），本文件只保留
/// 同签名薄转发与一处最小 internal 访问器；⛔ 判据、日志顺序、账本写入点与所有调用点一个字不改。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    // ===== 搬出后的入口薄转发（实现见 Extraction/Steps/ContentGroupFinalizerHandler.cs）=====
    private ContentGroupFinalizerHandler? _contentGroupFinalizerHandler;

    private ContentGroupFinalizerHandler ContentGroupFinalizer =>
        _contentGroupFinalizerHandler ??= new ContentGroupFinalizerHandler(this);

    /// <summary>薄转发：实现见 <see cref="ContentGroupFinalizerHandler.RecordFinalGroupFailure"/>。</summary>
    internal bool RecordFinalGroupFailure(
        VolumeGroupUnit? unit,
        FinalGroupFailureKind kind,
        string evidence,
        IReadOnlyList<ArchiveTask>? tasks) =>
        ContentGroupFinalizer.RecordFinalGroupFailure(unit, kind, evidence, tasks);

    /// <summary>薄转发：实现见 <see cref="ContentGroupFinalizerHandler.FinalizeContentGroups"/>。</summary>
    internal int FinalizeContentGroups(IReadOnlyList<ArchiveTask>? tasks) =>
        ContentGroupFinalizer.FinalizeContentGroups(tasks);

    /*
     * 协作类要用、但定义在**别的 partial** 里的 private 成员（不改那个文件）：
     * 这里只加**最小 internal 访问器**，⛔ 不复制判据、⛔ 不用反射（唯一实现仍在 ServiceForwarding）。
     *
     * 已经在别处 internal 的那些直接复用：_extractionContentGraph（VolumeHandoffStep）、
     * IsUserImportedSourcePath（PieceAdoptionStep）、AppendLog（LogCaptureStep）。
     */

    /// <summary>访问器：给 <see cref="ContentGroupFinalizerHandler"/> 按档保留组内过程物（唯一实现 = 本类）。</summary>
    internal void PreserveCurrentProcessArtifactsForContentGroupFinalizer(
        ArchiveTask task,
        IReadOnlyList<string>? artifactPaths,
        string? entryPath) =>
        PreserveCurrentProcessArtifacts(task, artifactPaths, entryPath);
}

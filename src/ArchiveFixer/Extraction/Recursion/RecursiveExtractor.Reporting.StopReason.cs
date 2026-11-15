using ArchiveFixer.Models;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 停因观察与递归结论拼装（原 <c>RecursiveExtractor.Reporting.StopReason.cs</c> 的全部实现）——
/// 实现已整块搬到协作类 <see cref="RecursiveStopReasonHandler"/>，这一分片只留同签名薄转发 + 最小 internal 访问器。
///
/// <para>⛔ 判据、文案、拼接顺序一个字都没改；调用点（<c>ExtractAsync</c>、<c>BuildResult</c>、
/// <c>RecursionHandler</c>）一处未动。</para>
/// </summary>
public sealed partial class RecursiveExtractor
{
    /// <summary>搬出去的那一份实现（懒建一次；宿主就是本类，全类共用同一个实例）。</summary>
    private RecursiveStopReasonHandler? _recursiveStopReasonHandler;

    private RecursiveStopReasonHandler StopReasonReporting =>
        _recursiveStopReasonHandler ??= new RecursiveStopReasonHandler(this);

    /// <summary>薄转发：实现见 <see cref="RecursiveStopReasonHandler.ReportStopReason"/>。</summary>
    internal void ReportStopReason(
        RecursionStopReason reason,
        string message,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string caller = "") =>
        StopReasonReporting.ReportStopReason(reason, message, sourceFile, sourceLine, caller);

    /// <summary>薄转发：实现见 <see cref="RecursiveStopReasonHandler.IsInsideWorkspaceRoot"/>。</summary>
    private static bool IsInsideWorkspaceRoot(string? path) =>
        RecursiveStopReasonHandler.IsInsideWorkspaceRoot(path);

    /// <summary>薄转发：实现见 <see cref="RecursiveStopReasonHandler.BuildSummary"/>。</summary>
    private static string BuildSummary(
        RecursionStopReason stopReason,
        List<RecursionLayerReport> layers,
        ExtractionWorkspace workspace,
        string finalOutputPath,
        bool completed,
        string publishMessage,
        string extraMessage,
        IReadOnlyList<string> unexpandedNames,
        string passwordFallbackNote = "") =>
        RecursiveStopReasonHandler.BuildSummary(
            stopReason,
            layers,
            workspace,
            finalOutputPath,
            completed,
            publishMessage,
            extraMessage,
            unexpandedNames,
            passwordFallbackNote);

    /// <summary>薄转发：实现见 <see cref="RecursiveStopReasonHandler.DescribeUnexpandedNames"/>。</summary>
    internal static string DescribeUnexpandedNames(IReadOnlyList<string> names) =>
        RecursiveStopReasonHandler.DescribeUnexpandedNames(names);

    /// <summary>薄转发：实现见 <see cref="RecursiveStopReasonHandler.DescribeStopReason"/>。</summary>
    private static string DescribeStopReason(RecursionStopReason stopReason, int unexpandedCount = 0) =>
        RecursiveStopReasonHandler.DescribeStopReason(stopReason, unexpandedCount);

    /// <summary>薄转发：实现见 <see cref="RecursiveStopReasonHandler.DescribeMissingVolumeFailure"/>。</summary>
    private static string DescribeMissingVolumeFailure(List<RecursionLayerReport> layers) =>
        RecursiveStopReasonHandler.DescribeMissingVolumeFailure(layers);

    /// <summary>薄转发：实现见 <see cref="RecursiveStopReasonHandler.DescribeAmbiguousPasswordFailure"/>。</summary>
    private static string DescribeAmbiguousPasswordFailure(List<RecursionLayerReport> layers) =>
        RecursiveStopReasonHandler.DescribeAmbiguousPasswordFailure(layers);

    /*
     * 协作类要用、但定义在**别的 partial** 里的 private 成员（不改那几个文件）：
     * 这里只加**最小 internal 访问器**，⛔ 不复制判据、⛔ 不用反射（唯一实现仍在原处）。
     *
     * 已经在别处 public / internal 的那些直接复用：StopReasonObserved（public 属性）、
     * ConfiguredWorkspaceRoot（public static 属性）。
     */

    /// <summary>访问器：给 <see cref="RecursiveStopReasonHandler"/> 取「点名未展开包」的最大条数（唯一常量 = <c>RecursiveExtractor.cs</c> 的 <c>MaxUnexpandedNameLines</c>）。</summary>
    internal static int MaxUnexpandedNameLinesForRecursiveStopReason => MaxUnexpandedNameLines;
}

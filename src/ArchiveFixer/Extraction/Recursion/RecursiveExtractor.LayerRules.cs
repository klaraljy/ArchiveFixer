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
/// 每层规则（内容物保留关键词 / 内容容器保护 / 同组后续卷折叠 / 层上限与展开比 / 多分支决策请求）——
/// 实现已整块搬到协作类 <see cref="RecursiveLayerRulesHandler"/>，这一分片只留同签名薄转发 + 最小 internal 访问器。
///
/// <para>⛔ 判据、日志顺序、调用顺序一个字都没改；调用点（<c>RecursiveExtractor.ExtractAsync.cs</c> 的主循环、
/// <c>RecursiveExtractor.InnerDiscovery.cs</c> 的候选扫描、<c>VolumeGroupRulerParityTests</c> /
/// <c>VolumeJunkTailTests</c>）一处未动。</para>
/// </summary>
public sealed partial class RecursiveExtractor
{
    /// <summary>薄转发：实现见 <see cref="RecursiveLayerRulesHandler.ApplyContentKeepRules"/>。</summary>
    private List<string> ApplyContentKeepRules(List<string> found, string layerLabel) =>
        new RecursiveLayerRulesHandler(this).ApplyContentKeepRules(found, layerLabel);

    /// <summary>薄转发：实现见 <see cref="RecursiveLayerRulesHandler.ApplyContentContainerProtection"/>。</summary>
    private List<string> ApplyContentContainerProtection(List<string> found, string layerLabel) =>
        new RecursiveLayerRulesHandler(this).ApplyContentContainerProtection(found, layerLabel);

    /// <summary>薄转发：实现见 <see cref="RecursiveLayerRulesHandler.CollapseSameGroupVolumes"/>。</summary>
    internal static List<string> CollapseSameGroupVolumes(List<string> found) =>
        RecursiveLayerRulesHandler.CollapseSameGroupVolumes(found);

    /// <summary>薄转发：实现见 <see cref="RecursiveLayerRulesHandler.IsSameGroupContinuationVolume"/>。</summary>
    internal static bool IsSameGroupContinuationVolume(string candidate, string innerArchivePath) =>
        RecursiveLayerRulesHandler.IsSameGroupContinuationVolume(candidate, innerArchivePath);

    /// <summary>薄转发：实现见 <see cref="RecursiveLayerRulesHandler.IsInformationalFile"/>。</summary>
    private static bool IsInformationalFile(string filePath) =>
        RecursiveLayerRulesHandler.IsInformationalFile(filePath);

    /// <summary>薄转发：实现见 <see cref="RecursiveLayerRulesHandler.CheckLimitsBeforeLayer"/>。</summary>
    private RecursionStopReason CheckLimitsBeforeLayer(
        WorkItem item,
        List<RecursionLayerReport> layers,
        int totalFiles,
        long totalSize) =>
        new RecursiveLayerRulesHandler(this).CheckLimitsBeforeLayer(item, layers, totalFiles, totalSize);

    /// <summary>薄转发：实现见 <see cref="RecursiveLayerRulesHandler.IsExpansionRatioExceededAsync"/>。</summary>
    private Task<bool> IsExpansionRatioExceededAsync(
        WorkItem item,
        List<RecursionLayerReport> layers,
        Queue<WorkItem> pending) =>
        new RecursiveLayerRulesHandler(this).IsExpansionRatioExceededAsync(item, layers, pending);

    /*
     * 协作类要用、但定义在**别的 partial** 里的 private 成员（不改那几个文件）：
     * 这里只加**最小 internal 访问器**，⛔ 不复制判据、⛔ 不用反射（唯一实现仍在原处）。
     *
     * 已经在 Result.cs / Reporting.Log.cs 里 internal 的那些直接复用：
     * KeepRules（public）、Log、Engine、Limits。
     */

    /// <summary>访问器：给 <see cref="RecursiveLayerRulesHandler"/> 量归档大小（唯一实现 = 恢复报告那一分片）。</summary>
    internal static long SafeFileLengthForLayerRules(string? path) => SafeFileLength(path);

    /// <summary>访问器：给 <see cref="RecursiveLayerRulesHandler"/> 判"说明类后缀"（唯一实现 = 本类静态字段）。</summary>
    internal static IReadOnlyList<string> InformationalExtensionsForLayerRules => InformationalExtensions;
}

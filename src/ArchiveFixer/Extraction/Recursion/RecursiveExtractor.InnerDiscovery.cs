using ArchiveFixer.Models;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 内层归档发现与续解入队（原 <c>RecursiveExtractor.InnerDiscovery.cs</c> 的全部实现）：
/// 探测本层产物里的内层归档、决定要不要继续展开并把它排进队列、给入队项备一份逐层回收请求。
///
/// <para>实现已整块搬进协作类 <see cref="RecursiveInnerDiscoveryHandler"/>
/// （<c>Extraction/Recursion/Steps/RecursiveInnerDiscoveryHandler.cs</c>），这一分片只留同签名薄转发
/// 与最小 internal 访问器；⛔ 判据、日志顺序、调用顺序一个字没改。</para>
/// </summary>
public sealed partial class RecursiveExtractor
{
    // ===== 搬出后的入口薄转发（实现见 Extraction/Recursion/Steps/RecursiveInnerDiscoveryHandler.cs）=====

    /// <summary>搬出去的那一份实现（懒建一次；宿主就是本类，全类共用同一个实例）。</summary>
    private RecursiveInnerDiscoveryHandler? _innerDiscoveryHandler;

    private RecursiveInnerDiscoveryHandler InnerDiscovery =>
        _innerDiscoveryHandler ??= new RecursiveInnerDiscoveryHandler(this);

    /// <summary>薄转发：实现见 <see cref="RecursiveInnerDiscoveryHandler.ProbeInnerArchivesAsync"/>。</summary>
    private Task<IReadOnlyList<string>> ProbeInnerArchivesAsync(
        string outputDirectory,
        string layerLabel,
        CancellationToken cancellationToken) =>
        InnerDiscovery.ProbeInnerArchivesAsync(outputDirectory, layerLabel, cancellationToken);

    /// <summary>薄转发：实现见 <see cref="RecursiveInnerDiscoveryHandler.TryEnqueueNextLayers"/>。</summary>
    private bool TryEnqueueNextLayers(
        WorkItem item,
        RecursionLayerReport report,
        ExtractionWorkspace workspace,
        EnqueueState state,
        out RecursionDecisionRequest? decision,
        out RecursionStopReason stopReason) =>
        InnerDiscovery.TryEnqueueNextLayers(
            item,
            report,
            workspace,
            state.Pending,
            state.MarkUnexpanded,
            out decision,
            out stopReason);

    /// <summary>
    /// 薄转发：实现见 <see cref="RecursiveInnerDiscoveryHandler.BuildLayerReclaim"/>。
    /// （搬出前是 <c>private static</c>；搬出后由持宿主的协作类实例实现，本类里没有调用点。）
    /// </summary>
    private RecursiveLayerReclaimRequest BuildLayerReclaim(
        WorkItem item,
        string innerArchivePath,
        ExtractionWorkspace workspace,
        RecursionLayerReport layerReport) =>
        InnerDiscovery.BuildLayerReclaim(item, innerArchivePath, workspace, layerReport);

    /// <summary>
    /// 薄转发：实现见 <see cref="RecursiveInnerDiscoveryHandler.HasOnlyInformationalSiblings"/>。
    /// （搬出前是 <c>private static</c>；搬出后由持宿主的协作类实例实现，本类里没有调用点。）
    /// </summary>
    private bool HasOnlyInformationalSiblings(RecursionLayerReport report, string innerArchivePath) =>
        InnerDiscovery.HasOnlyInformationalSiblings(report, innerArchivePath);

    /*
     * 协作类要用、但定义在别的 partial 里的 private 成员（不改那些文件）：
     * 这里只加最小 internal 访问器，⛔ 不复制判据、⛔ 不用反射（唯一实现仍在原处）。
     *
     * 已经在别处 internal 的那些直接复用：Limits（RecursiveExtractor.Result.cs）、
     * ShouldStopAtVolumeGroupMember（RecursiveExtractor.cs）、CollapseSameGroupVolumes /
     * IsSameGroupContinuationVolume（RecursiveExtractor.LayerRules.cs）。
     */

    /// <summary>访问器：给 <see cref="RecursiveInnerDiscoveryHandler"/> 读文件属性（唯一实现 = 本类）。</summary>
    internal static bool TryGetAttributesForInnerDiscovery(string path, out FileAttributes attributes) =>
        TryGetAttributes(path, out attributes);

    /// <summary>访问器：给 <see cref="RecursiveInnerDiscoveryHandler"/> 判 0 字节文件（唯一实现 = 本类）。</summary>
    internal static bool IsZeroByteFileForInnerDiscovery(string path) => IsZeroByteFile(path);

    /// <summary>访问器：给 <see cref="RecursiveInnerDiscoveryHandler"/> 判「魔数像归档、名字读不出卷标记」（唯一实现 = 本类）。</summary>
    internal static bool IsArchiveByMagicWithoutVolumeNameForInnerDiscovery(string path) =>
        IsArchiveByMagicWithoutVolumeName(path);

    /// <summary>访问器：给 <see cref="RecursiveInnerDiscoveryHandler"/> 摘「内容物保留关键词」（唯一实现 = 本类）。</summary>
    internal List<string> ApplyContentKeepRulesForInnerDiscovery(List<string> found, string layerLabel) =>
        ApplyContentKeepRules(found, layerLabel);

    /// <summary>访问器：给 <see cref="RecursiveInnerDiscoveryHandler"/> 做内容容器默认保护（唯一实现 = 本类）。</summary>
    internal List<string> ApplyContentContainerProtectionForInnerDiscovery(List<string> found, string layerLabel) =>
        ApplyContentContainerProtection(found, layerLabel);

    /// <summary>访问器：给 <see cref="RecursiveInnerDiscoveryHandler"/> 在消费前留下最后一层过程物（唯一实现 = 本类）。</summary>
    internal void RetainProcessArtifactBeforeConsumptionForInnerDiscovery(string archivePath) =>
        RetainProcessArtifactBeforeConsumption(archivePath);

    /// <summary>访问器：给 <see cref="RecursiveInnerDiscoveryHandler"/> 拼探针层标签（唯一实现 = 本类）。</summary>
    internal static string DescribeProbeLabelForInnerDiscovery(WorkItem item) => DescribeProbeLabel(item);

    /// <summary>访问器：给 <see cref="RecursiveInnerDiscoveryHandler"/> 判说明类后缀（唯一实现 = 本类）。</summary>
    internal static bool IsInformationalFileForInnerDiscovery(string filePath) => IsInformationalFile(filePath);
}

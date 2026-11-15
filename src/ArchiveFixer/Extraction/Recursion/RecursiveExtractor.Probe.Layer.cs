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

public sealed partial class RecursiveExtractor
{
    /*
     * 这一层"怎么解"的探针段已搬到 RecursiveLayerProbeHandler（独立协作类）：
     * 解压前条目预检 / 最小条目密码探针 / 媒体前缀与双面文件的 ZIP 直读 / 成功层的共同收尾出口。
     *
     * 这里只保留**原签名的薄转发**：调用方与测试的入口一个字都不变，实现全在新类；
     * 判据、日志级别与文案、停因、密码回传、落点校验的挂点全部照旧。
     */

    internal Task<LayerOutcome> FinishSuccessfulLayerAsync(
        WorkItem item,
        ArchiveOperationResult? success,
        LayerManifest layerManifest,
        string? succeededPassword,
        CancellationToken cancellationToken)
        => new RecursiveLayerProbeHandler(this).FinishSuccessfulLayerAsync(
            item,
            success,
            layerManifest,
            succeededPassword,
            cancellationToken);

    internal Task<(string? UnsafeSummary, ArchiveListResult? Listed)> CheckEntriesBeforeExtractAsync(
        string archivePath,
        string password,
        CancellationToken cancellationToken)
        => new RecursiveLayerProbeHandler(this).CheckEntriesBeforeExtractAsync(
            archivePath,
            password,
            cancellationToken);

    internal Task<ArchiveOperationResult?> ProbeCandidateAsync(
        WorkItem item,
        string archivePath,
        ExtractOptions options,
        ArchiveListResult? listed,
        string candidate,
        IProgress<ArchiveProgress>? progress,
        Action<ArchiveStallNotice>? stalled,
        CancellationToken cancellationToken)
        => new RecursiveLayerProbeHandler(this).ProbeCandidateAsync(
            item,
            archivePath,
            options,
            listed,
            candidate,
            progress,
            stalled,
            cancellationToken);

    internal Task<LayerOutcome?> TryTakeEmbeddedZipDirectAsync(
        WorkItem item,
        IReadOnlyList<string> candidates,
        IProgress<ArchiveProgress>? progress,
        CancellationToken cancellationToken)
        => new RecursiveLayerProbeHandler(this).TryTakeEmbeddedZipDirectAsync(
            item,
            candidates,
            progress,
            cancellationToken);

    /*
     * ===== 探针协作类用到的宿主出口 =====
     * 只把新类**本来就在用**的那几个私有成员按 internal 露出来（⛔ 不新增判据、不新增状态）：
     * · LogSink —— `_log` 原样直通，⛔ 不经过 Log 的 try/catch（列目录命名告警那一处原本就是裸调用）
     * · ProbeInnerArchivesAsync / DetectEmbeddedArchive / ResolveProbeEntrySize —— 各自唯一实现
     */

    internal Action<string, string>? LogSink => _log;

    internal Task<IReadOnlyList<string>> ProbeInnerArchivesForLayerProbeAsync(
        string outputDirectory,
        string layerLabel,
        CancellationToken cancellationToken)
        => ProbeInnerArchivesAsync(outputDirectory, layerLabel, cancellationToken);

    internal static EmbeddedArchiveInfo DetectEmbeddedArchiveForLayerProbe(string archivePath)
        => DetectEmbeddedArchive(archivePath);

    internal static long ResolveProbeEntrySizeForLayerProbe(ArchiveListResult? listed, string probeEntry)
        => ResolveProbeEntrySize(listed, probeEntry);

    /// <summary>探针日志里的层标签（与候选日志同一个形状：`├ 第 N 层：包名`）。</summary>
    internal static string DescribeProbeLabel(WorkItem item) =>
        string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            StatusText.RecursionLayerLogPrefixFormat,
            item.Depth) + Path.GetFileName(item.ArchivePath);

}

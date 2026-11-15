using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;


namespace ArchiveFixer.ViewModels;

/// <summary>
/// 双面文件（视频+尾部归档）直解 / 内嵌归档落点与按内容起名（第 12 块）——实现已按职责搬到
/// <see cref="EmbeddedZipHandler"/>（<c>ViewModels/Extraction/Steps/EmbeddedZipHandler.cs</c>，
/// 与别的步骤 Handler 同一种写法：扁平命名空间、只持有宿主），本分片只留同签名薄转发。
///
/// <para>⛔ 本分片不再放任何判据：搬走的是**实现**，调用点（<c>ExtractionFirstAttempt</c> /
/// <c>ExtractionCandidateLoop</c> / <c>ExtractionTaskConclusionWriter</c> / <c>ExtractionPreflight</c> /
/// <c>ExtractionCoordinator.EngineFallbackStep</c> / <c>ExtractionCoordinator.ServiceForwarding</c> /
/// <c>ExtractionCoordinator.ExtractTaskMainStep</c>）一个字都没改。</para>
///
/// <para>两个私有成员（<c>BuildEmbeddedArchivePath</c> / <c>AlignCarvedArchiveExtension</c>）保持
/// private 与原名：同一 partial 类的其他分片仍直接调它们，可见性一个字节没动。嵌套的进度桥
/// <c>DirectReadProgressBridge</c> 跟着实现搬进 Handler（只被直读那一步用），类型仍是 private，
/// 宿主的对外表面没有变化。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    /// <summary>
    /// 内嵌 ZIP 直读的**执行**那一步 —— 实现见
    /// <see cref="EmbeddedZipHandler.RunEmbeddedZipDirectExtractionAsync"/>；判据、状态写点、
    /// 日志顺序与搬出前逐字一致。
    /// </summary>
    internal Task RunEmbeddedZipDirectExtractionAsync(
        ArchiveTask task,
        EmbeddedZipProbeResult plan,
        IReadOnlyList<string> passwordCandidates,
        ArchiveListResult? knownList,
        string stageDirectory,
        string outputRedirectNote,
        bool sharedOutputRoot,
        bool oneClickRun,
        ProgressHeartbeatStep.Sink progressSink,
        CancellationToken cancellationToken) =>
        new EmbeddedZipHandler(this).RunEmbeddedZipDirectExtractionAsync(
            task,
            plan,
            passwordCandidates,
            knownList,
            stageDirectory,
            outputRedirectNote,
            sharedOutputRoot,
            oneClickRun,
            progressSink,
            cancellationToken);

    /// <summary>抠出来的那一份落在哪 —— 实现见 <see cref="EmbeddedZipHandler.BuildEmbeddedArchivePath"/>。</summary>
    private string BuildEmbeddedArchivePath(ArchiveTask task) =>
        new EmbeddedZipHandler(this).BuildEmbeddedArchivePath(task);

    /// <summary>抠出来的那一份按内容起名 —— 实现见 <see cref="EmbeddedZipHandler.AlignCarvedArchiveExtension"/>。</summary>
    private string AlignCarvedArchiveExtension(ArchiveTask task, string carvedPath) =>
        new EmbeddedZipHandler(this).AlignCarvedArchiveExtension(task, carvedPath);

    /// <summary>引擎报「分卷缺失」时的补充说明 —— 实现见 <see cref="EmbeddedZipHandler.BuildEmbeddedVolumeMissingHint"/>。</summary>
    internal string BuildEmbeddedVolumeMissingHint(ArchiveTask task, string? detectedErrorType) =>
        new EmbeddedZipHandler(this).BuildEmbeddedVolumeMissingHint(task, detectedErrorType);

    /// <summary>引擎报的是不是「分卷缺失」这一类 —— 实现见 <see cref="EmbeddedZipHandler.IsVolumeMissingErrorType"/>。</summary>
    internal static bool IsVolumeMissingErrorType(string? detectedErrorType) =>
        EmbeddedZipHandler.IsVolumeMissingErrorType(detectedErrorType);
}

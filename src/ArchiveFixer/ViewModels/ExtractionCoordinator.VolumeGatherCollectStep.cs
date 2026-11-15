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
/// 整组候选收集与收拢（组目录/参考名/成员判定/槽位键/规范名准备；第 9 块，同一 partial 类）。
///
/// <para>实现已整块搬进 <see cref="ArchiveFixer.ViewModels.Extraction.Steps.VolumeGatherCollectHandler"/>，本文件只保留同名薄转发
/// 与一处最小 internal 访问器；⛔ 判据与日志顺序一个字不改。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    // ===== 搬出后的入口薄转发（实现见 Extraction/Steps/VolumeGatherCollectHandler.cs）=====
    private VolumeGatherCollectHandler? _volumeGatherCollectHandler;

    private VolumeGatherCollectHandler VolumeGatherHandler =>
        _volumeGatherCollectHandler ??= new VolumeGatherCollectHandler(this);

    private static FileNatureCard? ResolveGroupAnchorCard(
        ArchiveTask? owner,
        string targetDir,
        string baseName,
        string? groupEntryPath = null) =>
        VolumeGatherCollectHandler.ResolveGroupAnchorCard(owner, targetDir, baseName, groupEntryPath);

    private static FileNatureCard? ResolveGroupFirstVolumeCard(string targetDir, string baseName) =>
        VolumeGatherCollectHandler.ResolveGroupFirstVolumeCard(targetDir, baseName);

    internal static int? ResolveVolumeSlotIndexForPiece(string path, FileNatureCard card) =>
        VolumeGatherCollectHandler.ResolveVolumeSlotIndexForPiece(path, card);

    internal bool IsFirstVolumeOwnedByAnotherTask(ArchiveTask task, string firstVolumePath) =>
        VolumeGatherHandler.IsFirstVolumeOwnedByAnotherTask(task, firstVolumePath);

    private static bool CardSaysItIsOneOfTheGroup(string path) =>
        VolumeGatherCollectHandler.CardSaysItIsOneOfTheGroup(path);

    private static string DescribeGatherAnchor(
        VolumeGatherPlanner.CrossLayerVolumeGather decision,
        string entryPath) =>
        VolumeGatherCollectHandler.DescribeGatherAnchor(decision, entryPath);

    private static bool TryMovePieceIntoGroup(string piecePath, string target) =>
        VolumeGatherCollectHandler.TryMovePieceIntoGroup(piecePath, target);

    internal static string ResolveGroupReferenceName(ArchiveTask? task, string baseName, string? memberPath = null) =>
        VolumeGatherCollectHandler.ResolveGroupReferenceName(task, baseName, memberPath);

    private static bool MemberBelongsToGroup(string path, string tailName) =>
        VolumeGatherCollectHandler.MemberBelongsToGroup(path, tailName);

    internal string ResolveBatchGroupDirectory(string baseName, ArchiveTask? task = null, string? memberPath = null) =>
        VolumeGatherHandler.ResolveBatchGroupDirectory(baseName, task, memberPath);

    private static string SafeFileSizeText(string path) =>
        VolumeGatherCollectHandler.SafeFileSizeText(path);

    private static bool IsInsideProcessArtifactDirectory(string path) =>
        VolumeGatherCollectHandler.IsInsideProcessArtifactDirectory(path);

    internal ArchiveTask? FindTaskByPath(string path) =>
        VolumeGatherHandler.FindTaskByPath(path);

    internal static IEnumerable<string> SafeEnumerateFiles(string? directory) =>
        VolumeGatherCollectHandler.SafeEnumerateFiles(directory);

    private static string ResolveObservedPiecePath(ArchiveTask? task, string? memberPath) =>
        VolumeGatherCollectHandler.ResolveObservedPiecePath(task, memberPath);

    private static string VolumeSlotKey(string path) =>
        VolumeGatherCollectHandler.VolumeSlotKey(path);

    private void PrepareCanonicalVolumeNamesForConfirmedOrder(
        string entryLayer,
        IReadOnlyList<string> orderedPieces,
        string stem,
        VolumeNamingFamily family,
        string logName) =>
        VolumeGatherHandler.PrepareCanonicalVolumeNamesForConfirmedOrder(
            entryLayer, orderedPieces, stem, family, logName);

    /// <summary>
    /// 搬出到 <see cref="ArchiveFixer.ViewModels.Extraction.Steps.VolumeGatherCollectHandler"/> 的实现要用
    /// 「吐出这一片的那一单」（私有成员的最小 internal 访问器；⛔ 实现一个字不改）。
    /// </summary>
    internal ArchiveTask? FindProducerTaskForPackageForHandler(
        ArchiveTask task,
        string packagePath,
        string entryName) =>
        FindProducerTaskForPackage(task, packagePath, entryName);

    // ===== 搬出后的入口薄转发（实现见 ExtractionVolumeCandidateCollector）=====
    private ExtractionVolumeCandidateCollector? _volumeCandidateCollector;
    private ExtractionVolumeCandidateCollector VolumeCandidateCollector =>
        _volumeCandidateCollector ??= new ExtractionVolumeCandidateCollector(this);

    internal List<string> CollectVolumeCandidatePaths(
        ArchiveTask task,
        string entryLayer,
        out string referenceName,
        out int scannedInEntryLayer) =>
        VolumeCandidateCollector.CollectVolumeCandidatePaths(
            task, entryLayer, out referenceName, out scannedInEntryLayer);
}

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
/// 用户源包就地保护/真名还原（改名与缺卷补救两处共用；第 8 块，同一 partial 类）。
///
/// <para>实现已整块搬进 <see cref="SourceProtectionHandler"/>
/// （<c>ViewModels/Extraction/Steps/SourceProtectionHandler.cs</c>），本文件只保留仍有调用方的
/// 同名同签名薄转发与最小 internal 访问器；⛔ 判据、日志文案与顺序、硬链接与删除路径一个字不改。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    // ===== 搬出后的入口薄转发（实现见 Extraction/Steps/SourceProtectionHandler.cs）=====
    private SourceProtectionHandler? _sourceProtectionHandler;

    private SourceProtectionHandler SourceProtection =>
        _sourceProtectionHandler ??= new SourceProtectionHandler(this);

    /// <summary>薄转发：实现见 <see cref="SourceProtectionHandler.BeginKeepInPlaceSourceProtection"/>。</summary>
    internal void BeginKeepInPlaceSourceProtection(IReadOnlyList<ArchiveTask> tasks) =>
        SourceProtection.BeginKeepInPlaceSourceProtection(tasks);

    /// <summary>薄转发：实现见 <see cref="SourceProtectionHandler.RestoreKeepInPlaceSourceNames"/>。</summary>
    internal void RestoreKeepInPlaceSourceNames() => SourceProtection.RestoreKeepInPlaceSourceNames();

    /*
     * 搬出去的实现要用的成员：定义在**别的 partial** 里的 private 成员（不改那些文件），
     * 这里只加**最小 internal 访问器**，⛔ 不复制判据、⛔ 不用反射（唯一实现仍在原处）。
     * 搬走后本类已无调用方的 3 个 private 辅助（`ProtectKeepInPlaceSourcesBeforeRepair` /
     * `RestoreKeepInPlaceSourcesAfterRepair` / `CleanupKeepInPlaceSourceProtection`）按 ADR 0003
     * 不留在本文件做空转发（实现已随整块搬进 handler）。
     */

    /// <summary>访问器：KeepInPlace 源文件硬链接快照账（原名 → 备份路径），实现在 VolumeLedgerStep 分片。</summary>
    internal List<(string Original, string Backup)> KeepSourceSnapshotsForSourceProtection =>
        _keepSourceSnapshots;

    /// <summary>访问器：本批保护快照目录；收尾要读写同一个字段（实现在 VolumeLedgerStep 分片）。</summary>
    internal string KeepSourceSnapshotDirectoryForSourceProtection
    {
        get => _keepSourceSnapshotDirectory;
        set => _keepSourceSnapshotDirectory = value;
    }

    /// <summary>
    /// 访问器：缺卷补救前的一次性源包保护仍走那一份「账上明确登记的用户源路径」收集口径
    /// （实现在 VolumeRepairStep 分片）。
    /// </summary>
    internal static IEnumerable<string> CollectOriginalSourceFilesForSourceProtection(
        IEnumerable<ArchiveTask> tasks) =>
        CollectOriginalSourceFiles(tasks);

    // `_suffixRenamedSources` 已有跨分片访问器 `SuffixRenamedSourcesForVolumeLedger`（VolumeLedgerStep 分片），
    // 搬出去的实现直接走宿主那一个；⛔ 这里不声明第二个同名成员。
}

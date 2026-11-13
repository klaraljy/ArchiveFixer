using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;


namespace ArchiveFixer.ViewModels
{
    /// <summary>分卷交接登记与"这一份是不是本批某组的一片"（第 13 块，从主文件搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>第一大步的交接事实表（第二大步的组分析从这里取候选）。</summary>
        internal readonly List<ArchiveFixer.Detection.VolumeHandoffEntry> _volumeHandoffEntries = new();

        /// <summary>
        /// **第一大步文件账本**（共用骨架）：闸门通过的每一份文件都登记，
        /// 包括内容 Unknown 的中间片；族 resolver 从这里取候选。
        /// </summary>
        internal readonly FirstStepFileLedger _firstStepFileLedger = new();

        /// <summary>本批的源根 → 产出物 → 内容组账本骨架（第一阶段只登记源根）。</summary>
        internal readonly ExtractionContentGraph _extractionContentGraph = new();

        /// <summary>
        /// **边界解析管线**（共用骨架）：族 resolver 从这里注册；骨架阶段未注册任何族，
        /// 因此 `ResolveAllAsync` 返回空，既有行为一个字不变。
        /// </summary>
        private readonly VolumeBoundaryPipeline _volumeBoundaryPipeline = CreateVolumeBoundaryPipeline();

        private static VolumeBoundaryPipeline CreateVolumeBoundaryPipeline()
        {
            var pipeline = new VolumeBoundaryPipeline();

            // 7z / RAR 先接；ZIP 支线再接，⛔ 不在共享层写族特判。
            pipeline.Register(new SevenZipBoundaryResolver());
            pipeline.Register(new RarBoundaryResolver());
            pipeline.Register(new ZipSpannedBoundaryResolver());
            pipeline.Register(new ZipNumberedBoundaryResolver());

            return pipeline;
        }

        /// <summary>本批的交接事实（只读给第二大步/日志用）。</summary>
        internal IReadOnlyList<ArchiveFixer.Detection.VolumeHandoffEntry> VolumeHandoffEntries => _volumeHandoffEntries;

        /// <summary>边界解析管线（第二大步/测试用）。</summary>
        internal VolumeBoundaryPipeline VolumeBoundaryPipeline => _volumeBoundaryPipeline;

        /// <summary>本批的第一大步文件账本（只读给第二大步/日志用）。</summary>
        internal FirstStepFileLedger FirstStepLedger => _firstStepFileLedger;

        /// <summary>本批源根图（只读给测试/后续阶段用）。</summary>
        internal ExtractionContentGraph ContentGraph => _extractionContentGraph;

        /// <summary>
        /// 批首/每轮登记源根。只登记用户导入的根任务；续解任务不是源根。
        /// </summary>
        internal int RegisterSourceRoots(IEnumerable<ArchiveTask>? tasks, OneClickRunOptions? runOptions)
        {
            if (tasks == null)
            {
                return 0;
            }

            SourceHandlingMode sourceHandling = runOptions?.SourceHandling
                ?? AppSettings.ParseSourceHandling(Settings.SourceHandling);

            OutputPlacementMode placementMode = runOptions?.PlacementMode
                ?? OutputPlacement.FromLegacyFlags(
                    Settings.ExtractToOriginalDirectory,
                    Settings.KeepArchiveNameFolder,
                    Settings.CustomOutputDirectory);

            string customRoot = runOptions is { IsPlacementValid: true }
                ? runOptions.CustomRoot
                : !string.IsNullOrWhiteSpace(_vm.SelectedOutputDirectory)
                    ? _vm.SelectedOutputDirectory
                    : Settings.CustomOutputDirectory;

            int registered = 0;

            foreach (ArchiveTask task in tasks)
            {
                if (task == null || task.IsContinuationTask)
                {
                    continue;
                }

                string placementKey = BuildSourcePlacementKey(task, placementMode, customRoot);
                _extractionContentGraph.RegisterSourceRoot(task, placementKey, sourceHandling);
                registered++;
            }

            return registered;
        }

        /// <summary>
        /// 源根落点域：未指定位置时按选中文件夹/选中文件所在目录；指定位置时目标根必须同类。
        /// ⛔ 不拿盘根当域。
        /// </summary>
        private static string BuildSourcePlacementKey(
            ArchiveTask task,
            OutputPlacementMode placementMode,
            string? customRoot)
        {
            string scope;

            if (task.SourceSelectionKind == SourceSelectionKind.Folder
                && !string.IsNullOrWhiteSpace(task.SourceSelectionRoot))
            {
                scope = "folder:" + NormalizePlacementPath(task.SourceSelectionRoot);
            }
            else
            {
                string directory = Path.GetDirectoryName(task.OriginalPath) ?? string.Empty;
                scope = "file:" + NormalizePlacementPath(directory);
            }

            return $"{(int)OutputPlacement.NormalizeLegacyMode(placementMode)}"
                + "|" + NormalizePlacementPath(customRoot)
                + "|" + scope;
        }

        /// <summary>
        /// 交界屏障：把 prepared 组登记成内容组，接上来源根与落点域。
        /// ⛔ 只登记事实，不建入口、不点名、不动盘。
        /// </summary>
        internal int RegisterContentGroups(IEnumerable<PreparedVolumeGroup>? groups)
        {
            if (groups == null)
            {
                return 0;
            }

            int registered = 0;

            foreach (PreparedVolumeGroup group in groups)
            {
                if (group == null || string.IsNullOrWhiteSpace(group.BaseName))
                {
                    continue;
                }

                ContentGroup contentGroup = _extractionContentGraph.RegisterContentGroup(
                    group,
                    _firstStepFileLedger);

                AppendLog("INFO", $"[内容组] {contentGroup.Describe()}");
                registered++;
            }

            return registered;
        }

        /// <summary>把唯一 owner 写回内容组；⛔ 不换 owner，只补空位。</summary>
        internal void AttachContentGroupOwner(
            PreparedVolumeGroup? prepared,
            string? ownerTaskFileName,
            string? ownerTaskPath = null)
        {
            if (prepared == null
                || string.IsNullOrWhiteSpace(ownerTaskFileName)
                || !_extractionContentGraph.TryGetContentGroup(
                    prepared.BaseName,
                    prepared.Family,
                    prepared.OriginalSourceLayer,
                    out ContentGroup group))
            {
                return;
            }

            group.AttachOwner(ownerTaskFileName, ownerTaskPath);
            AppendLog("INFO", $"[内容组] {group.BaseName}：owner={ownerTaskFileName}。");
        }

        private static string NormalizePlacementPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim();
            }
        }

        /// <summary>
        /// 递归层遇到“某个还没凑齐的分卷组的一员”时，不再把它当独立内层包解：
        /// 登记成候选产出物并加租约，原样留给交界处归组。
        /// </summary>
        private bool ShouldStopAtCandidateVolumePiece(string path, ArchiveTask owner)
        {
            if (string.IsNullOrWhiteSpace(path)
                || !File.Exists(path)
                || !RecursiveExtractor.IsPieceOfIncompleteGroup(path))
            {
                return false;
            }

            string originRootId = _extractionContentGraph.ResolveRootId(owner);
            string placementKey = _extractionContentGraph.ResolvePlacementKey(owner);
            string leaseId = _extractionContentGraph.LeaseCandidate(
                path,
                originRootId,
                owner.TaskId,
                placementKey);

            if (_firstStepFileLedger.TryGetByPath(path, out _))
            {
                return true;
            }

            try
            {
                ArchiveFixer.Detection.FileNatureCard card =
                    ArchiveFixer.Detection.FileNature.Inspect(path);

                int? nameSlot = LegacyVolumeNames.TryGetIndex(FileNameHelper.GetFileName(path));

                _firstStepFileLedger.AddOrReplace(FirstStepFileLedgerEntry.FromCard(
                    card,
                    owner.LogName,
                    VolumeHandoffSource.ChainProduced,
                    gatePassed: true,
                    rarVariant: card.RarVariant,
                    zipVariant: ResolveZipVariant(card),
                    nameSlot: nameSlot,
                    originRootId: originRootId,
                    producedByTaskId: owner.TaskId,
                    role: ArtifactRole.CandidateVolumePiece,
                    candidateLeaseId: leaseId,
                    placementKey: placementKey));
            }
            catch
            {
                // 读不动内容卡时租约仍然有效；不把候选降级成可回收过程物。
            }

            return true;
        }

        internal static ZipVariant ResolveZipVariant(ArchiveFixer.Detection.FileNatureCard card)
        {
            if (card.Format != ArchiveFixer.Detection.VolumeContentFormat.Zip)
            {
                return ZipVariant.Unknown;
            }

            if (card.Membership == ArchiveFixer.Detection.VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                && card.VolumeNumber is int number
                && number >= 2)
            {
                return ZipVariant.Spanned;
            }

            string name = FileNameHelper.GetFileName(card.Path);
            string[] segments = name.Split('.');

            /*
             * `.zNN` 是跨盘族的第 1 片；⛔ 不能用 `.zip`=1 的那套名字口径。
             *
             * ⚠ 2026-11-01（真机 901000020，调试器逐条读出的账本事实）：第一片叫
             * `222-L2.&-z01.删除` —— 卷标记被伪装杂质挤到了**中间那一段**。老写法只看最后一个点段
             *（`删除`）⇒ 认不出 ⇒ 落进下面"有本地头 ⇒ Numbered"，与末片的 Spanned 强锚点撞成
             * `SizeAnomaly（不混组）`，整组一次引擎都不调。⇒ 改成逐段认（用的还是同一把容差尺子
             * `TrySplitVolumeSegmentTolerant`，⛔ 没有新门槛、⛔ 不按整名相似度猜）。
             */
            for (int index = segments.Length - 1; index >= 1; index--)
            {
                if (ArchiveFixer.Helpers.ExtensionHelper.TrySplitVolumeSegmentTolerant(
                        segments[index],
                        out string canonical,
                        out _)
                    && canonical.Length == 3
                    && (canonical[0] == 'z' || canonical[0] == 'Z')
                    && char.IsAsciiDigit(canonical[1])
                    && char.IsAsciiDigit(canonical[2]))
                {
                    return ZipVariant.Spanned;
                }
            }

            if (card.ZipEndRecordMeansSplit || card.HasZipLocalHeader)
            {
                return ZipVariant.Numbered;
            }

            return ZipVariant.Unknown;
        }

        /// <summary>
        /// 第二大步入口的**纯后缀清洗**：只把文件名第一个点之后那段中的
        /// 非 ASCII 杂质删掉，基名/内容不动；目标名被占就不改。
        /// </summary>
        internal int CleanupSuffixJunkAtSecondStep(IReadOnlyCollection<ArchiveTask>? tasks)
        {
            int renamed = 0;

            foreach (ArchiveFixer.Detection.VolumeHandoffEntry entry in _volumeHandoffEntries)
            {
                if (string.IsNullOrWhiteSpace(entry.Path) || !File.Exists(entry.Path))
                {
                    continue;
                }

                string fileName = Path.GetFileName(entry.Path);

                if (!Extraction.VolumeNameRepair.TryStripSuffixJunk(fileName, out string cleaned)
                    || string.IsNullOrWhiteSpace(cleaned))
                {
                    continue;
                }

                string directory = Path.GetDirectoryName(entry.Path) ?? string.Empty;
                string target = Path.Combine(directory, cleaned);

                if (File.Exists(target)
                    || string.Equals(target, entry.Path, StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog(
                        "WARN",
                        $"[第二大步·后缀清洗] {fileName}：目标名已被占用或没有变化 ⇒ 不改名。");
                    continue;
                }

                try
                {
                    FileOperations.Move(entry.Path, target);

                    string oldPath = entry.Path;
                    entry.Path = target;
                    _suffixRenamedSources.Add((oldPath, target));
                    renamed++;

                    if (tasks != null)
                    {
                        foreach (ArchiveTask task in tasks)
                        {
                            if (SafePathHelper.PathEquals(task.CurrentPath, oldPath))
                            {
                                task.CurrentPath = target;
                            }

                            for (int index = 0; index < task.VolumePaths.Count; index++)
                            {
                                if (SafePathHelper.PathEquals(task.VolumePaths[index], oldPath))
                                {
                                    task.VolumePaths[index] = target;
                                }
                            }
                        }
                    }

                    AppendLog(
                        "INFO",
                        $"[第二大步·后缀清洗] {fileName} → {cleaned}（只改后缀，基名/内容未动）");
                }
                catch (Exception ex)
                {
                    AppendLog(
                        "WARN",
                        $"[第二大步·后缀清洗] {fileName}：改名失败（{ex.Message}）⇒ 保留原名。");
                }
            }

            return renamed;
        }

        private readonly List<(string OriginalPath, string CurrentPath)> _gatheredUserSourcePieces = new();

        /// <summary>记下"这几片是从用户自己的源目录里收拢过来的"（只记搬离过的那几份）。</summary>
        private void RememberGatheredUserSourcePieces(IReadOnlyList<VolumeRepairItem> items)
        {
            foreach (VolumeRepairItem item in items)
            {
                if (string.IsNullOrWhiteSpace(item.CurrentPath) || string.IsNullOrWhiteSpace(item.TargetPath))
                {
                    continue;
                }

                if (SafePathHelper.PathEquals(item.CurrentPath, item.TargetPath))
                {
                    continue;
                }

                if (!IsUserImportedSourcePath(item.CurrentPath))
                {
                    continue;
                }

                _gatheredUserSourcePieces.Add((item.CurrentPath, item.TargetPath));
            }
        }

        /// <summary>
        /// **把收拢搬走的用户源片放回原目录**（逐层回收动删除之前唯一该做的补偿）。
        /// 返回放回的份数；放不回（原位置又被占 / 出错）⇒ 如实写 WARN，⛔ 不拦别人的删除。
        /// </summary>
        internal int RestoreGatheredUserSourcePieces(List<(string Level, string Message)>? logEntries)
        {
            if (_gatheredUserSourcePieces.Count == 0)
            {
                return 0;
            }

            int restored = 0;

            foreach ((string originalPath, string currentPath) in _gatheredUserSourcePieces.ToList())
            {
                try
                {
                    if (!File.Exists(currentPath))
                    {
                        _gatheredUserSourcePieces.Remove((originalPath, currentPath));
                        continue;
                    }

                    if (File.Exists(originalPath))
                    {
                        logEntries?.Add((
                            "WARN",
                            $"{Path.GetFileName(originalPath)}：原目录里已经有一份同名的了 —— "
                            + "不覆盖，收拢搬走的那一份仍留在这边。"));

                        continue;
                    }

                    string? directory = Path.GetDirectoryName(originalPath);

                    if (string.IsNullOrWhiteSpace(directory))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(directory);
                    FileOperations.Move(currentPath, originalPath);

                    logEntries?.Add((
                        "INFO",
                        $"用户源片放回原目录：{Path.GetFileName(originalPath)}（它是被「收拢」搬走的，"
                        + "按「源包操作 = 留下」一个字节都不许少）。"));

                    _gatheredUserSourcePieces.Remove((originalPath, currentPath));
                    restored++;
                }
                catch (Exception ex)
                {
                    logEntries?.Add((
                        "WARN",
                        $"{Path.GetFileName(originalPath)}：没能放回原目录（{ex.Message}）—— "
                        + "这一份仍是收拢过后的位置。"));
                }
            }

            return restored;
        }

        private static string BuildVolumeGroupAttemptSignature(VolumeGroup group)
        {
            var builder = new System.Text.StringBuilder(group.GroupKey);

            foreach (VolumeCandidate volume in group.Volumes)
            {
                builder.Append('|').Append(volume.Path).Append('|');

                try
                {
                    builder.Append(new FileInfo(volume.Path).Length);
                }
                catch
                {
                    builder.Append('-');
                }
            }

            return builder.ToString();
        }


        /// <summary>
        /// 落点解释那一行（用户 2026-10-05 口径："落在**第一卷的位置**"）：这一组的第 1 卷与这一单
        /// 自己的文件不在同一层时，把"为什么解到这里"写进日志。⛔ 只写日志、不动盘。
        ///
        /// <para>判据与落点推导共用同一个出口（`PathService.ResolveOutputPlacement` →
        /// `Detection.GroupVolumeDirectory`），这里不另算一遍；⛔ 第一大步不再自己读整组事实。</para>
        /// </summary>
        private void LogVolumeGroupFirstVolumeLanding(ArchiveTask task, string outputPath)
        {
            if (!task.IsVolumeGroup || string.IsNullOrWhiteSpace(outputPath))
            {
                return;
            }

            var groupMembers = new List<string> { task.CurrentPath };
            groupMembers.AddRange(task.VolumePaths.Where(path => !string.IsNullOrWhiteSpace(path)));

            Detection.GroupVolumeDirectory.Entry entry = Detection.GroupVolumeDirectory.Resolve(groupMembers);

            if (entry.Path.Length > 0
                && entry.Directory.Length > 0
                && !SafePathHelper.PathEquals(entry.Directory, FileNameHelper.GetDirectoryName(task.CurrentPath)))
            {
                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.VolumeGroupFirstVolumeLandingFormat,
                        task.FileName,
                        outputPath,
                        Path.GetFileName(entry.Path),
                        Path.GetFileName(entry.Directory)));
            }
        }

        // ===== 搬出后的入口薄转发（实现见独立类）=====
        private ExtractionVolumeHandoffLedger? _volumeHandoffLedger;
        private ExtractionVolumeHandoffLedger VolumeHandoffLedger =>
            _volumeHandoffLedger ??= new ExtractionVolumeHandoffLedger(this);

        internal ArchiveFixer.Detection.VolumeHandoffEntry RegisterVolumeHandoff(
            ArchiveTask task,
            ArchiveFixer.Detection.FileNatureCard card) =>
            VolumeHandoffLedger.RegisterVolumeHandoff(task, card);

        internal void ClearVolumeHandoffLedger() => VolumeHandoffLedger.ClearVolumeHandoffLedger();

        internal void MarkVolumeHandoffConsumed(IEnumerable<string> paths, string by) =>
            VolumeHandoffLedger.MarkVolumeHandoffConsumed(paths, by);

        internal void AppendVolumeHandoffBatchSummary() => VolumeHandoffLedger.AppendVolumeHandoffBatchSummary();

        internal void MarkVolumeHandoffVerdict(IEnumerable<string>? pieces, string verdict) =>
            VolumeHandoffLedger.MarkVolumeHandoffVerdict(pieces, verdict);

        private void RegisterUnrecognizedSuspect(ArchiveTask task) =>
            VolumeHandoffLedger.RegisterUnrecognizedSuspect(task);

        private ExtractionProducedVolumeGate? _producedVolumeGate;
        private ExtractionProducedVolumeGate ProducedVolumeGate =>
            _producedVolumeGate ??= new ExtractionProducedVolumeGate(this);

        internal static (long Lookups, long Hits, long Misses, long FileInfoCalls, long Remembered) ProducedCardDiagnostics =>
            ExtractionProducedVolumeGate.ProducedCardDiagnostics;

        internal static void ResetProducedCardDiagnostics() => ExtractionProducedVolumeGate.ResetProducedCardDiagnostics();

        internal bool TryGetProducedCard(string path, out FileNatureCard card) =>
            ProducedVolumeGate.TryGetProducedCard(path, out card);

        internal Task RegisterProducedOutputsToLedgerAsync(ArchiveTask task) =>
            ProducedVolumeGate.RegisterProducedOutputsToLedgerAsync(task);

        internal FirstStepFileLedgerEntry RegisterFirstStepLedgerEntry(
            ArchiveTask task,
            ArchiveFixer.Detection.FileNatureCard card) =>
            ProducedVolumeGate.RegisterFirstStepLedgerEntry(task, card);

        internal bool HasContentProvenVolumeFirstSibling(ArchiveTask task, string entryName) =>
            ProducedVolumeGate.HasContentProvenVolumeFirstSibling(task, entryName);
        /// <summary>已经试过的分卷组签名（跨 partial 使用：批内去重）。</summary>
        internal readonly HashSet<string> _triedVolumeGroupSignatures =
            new(StringComparer.OrdinalIgnoreCase);

        internal void ClearProducedCardCache() => ProducedVolumeGate.ClearProducedCardCache();
    }
}

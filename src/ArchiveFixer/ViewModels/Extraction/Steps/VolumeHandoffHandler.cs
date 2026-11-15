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
using System.Collections.Concurrent;
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
/// 分卷交接登记与"这一份是不是本批某组的一片"（从 `ExtractionCoordinator.VolumeHandoffStep.cs` 搬出）：
/// 源根登记 / 内容组登记与 owner 补齐 / 递归层候选片拦截 / 后缀清洗 / 收拢源片放回 / 落点解释那一行。
/// ⛔ 判据、日志顺序、落点推导一个字都没改。
///
/// <para>M5：协调器那一分片已删除，本类就是**交接事实与入口的唯一落点** —— 交接账、第一大步文件账本、
/// 内容组图、边界解析管线、收拢源片账、"这一组同一套片试过一遍"签名都归本类持有；
/// 协调器只保留 <c>VolumeHandoff</c> 一个访问器（⛔ 不再留同名薄转发）。
/// 交接账本记录器 <see cref="Ledger"/> 与产出物内容卡闸门 <see cref="ProducedVolumeGate"/>
/// 也挂在这里（它们读写同一份账，⛔ 各自 new 会分成两半）。</para>
///
/// <para>⚠ <b>唯一实例</b>：本类有实例状态（上面那几本账），只能由
/// <c>ExtractionCoordinator.VolumeHandoff</c> 懒建一次；⛔ 不许在别处 <c>new VolumeHandoffHandler(host)</c>
/// —— 第二份实例会让同一批的交接账分成两半。</para>
/// </summary>
internal sealed class VolumeHandoffHandler
{
    private readonly ExtractionCoordinator _host;

    /*
     * 交接事实与第一大步账本：唯一存放点就是本类（原为协调器分片里的实例字段）。
     * 字段名、类型、初始化与搬家前逐字相同；别的 partial / 协作类改走下面的 internal 属性，
     * ⛔ 不复制判据、⛔ 不另造第二本账。
     */
    private readonly List<ArchiveFixer.Detection.VolumeHandoffEntry> _volumeHandoffEntries = new();
    private readonly FirstStepFileLedger _firstStepFileLedger = new();
    private readonly ExtractionContentGraph _extractionContentGraph = new();
    private readonly VolumeBoundaryPipeline _volumeBoundaryPipeline = CreateVolumeBoundaryPipeline();
    private readonly List<(string OriginalPath, string CurrentPath)> _gatheredUserSourcePieces = new();
    private readonly HashSet<string> _triedVolumeGroupSignatures = new(StringComparer.OrdinalIgnoreCase);

    private ExtractionVolumeHandoffLedger? _ledger;
    private ExtractionProducedVolumeGate? _producedVolumeGate;

    internal VolumeHandoffHandler(ExtractionCoordinator host)
    {
        _host = host;
    }

    /// <summary>第一大步的交接事实表（第二大步的组分析从这里取候选）。</summary>
    internal IReadOnlyList<ArchiveFixer.Detection.VolumeHandoffEntry> Entries => _volumeHandoffEntries;

    /// <summary>交接账的**可变**口子（只给 <see cref="Ledger"/> 登记/消费/清空；别处只读 <see cref="Entries"/>）。</summary>
    internal List<ArchiveFixer.Detection.VolumeHandoffEntry> EntryLedger => _volumeHandoffEntries;

    /// <summary>
    /// **第一大步文件账本**（共用骨架）：闸门通过的每一份文件都登记，
    /// 包括内容 Unknown 的中间片；族 resolver 从这里取候选。
    /// </summary>
    internal FirstStepFileLedger FirstStepLedger => _firstStepFileLedger;

    /// <summary>本批的源根 → 产出物 → 内容组账本骨架。</summary>
    internal ExtractionContentGraph ContentGraph => _extractionContentGraph;

    /// <summary>
    /// **边界解析管线**（共用骨架）：族 resolver 从这里注册；骨架阶段未注册任何族，
    /// 因此 `ResolveAllAsync` 返回空，既有行为一个字不变。
    /// </summary>
    internal VolumeBoundaryPipeline VolumeBoundaryPipeline => _volumeBoundaryPipeline;

    /// <summary>被「收拢」搬离用户原目录的那几片（原路径 ↔ 搬走后的路径）。</summary>
    internal List<(string OriginalPath, string CurrentPath)> GatheredUserSourcePieces => _gatheredUserSourcePieces;

    /// <summary>
    /// **这一批已经用「完全同一组片」试过一遍的组签名**（批末补判去重用的那本账）。
    /// 签名 = 组的 `GroupKey` + 每一卷「完整路径 + 字节数」；只要有新片进来签名就会变。
    /// </summary>
    internal HashSet<string> TriedVolumeGroupSignatures => _triedVolumeGroupSignatures;

    /// <summary>
    /// 第二大步交接账本记录器（登记/消费/结论/批末汇总/未识别嫌疑登记）—— 本类唯一实例。
    /// 它读写的交接账就是本类的 <see cref="EntryLedger"/>，⛔ 各自 new 会分成两半。
    /// </summary>
    internal ExtractionVolumeHandoffLedger Ledger => _ledger ??= new ExtractionVolumeHandoffLedger(_host);

    /// <summary>
    /// 产出物内容卡闸门（内容卡缓存 / 产出物登记 / 第一大步账本登记）—— 本类唯一实例。
    /// ⚠ 它有实例缓存（内容卡），必须共享同一个实例，⛔ 不许每处 new。
    /// </summary>
    internal ExtractionProducedVolumeGate ProducedVolumeGate =>
        _producedVolumeGate ??= new ExtractionProducedVolumeGate(_host);

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
            ?? AppSettings.ParseSourceHandling(_host.Settings.SourceHandling);

        OutputPlacementMode placementMode = runOptions?.PlacementMode
            ?? OutputPlacement.FromLegacyFlags(
                _host.Settings.ExtractToOriginalDirectory,
                _host.Settings.KeepArchiveNameFolder,
                _host.Settings.CustomOutputDirectory);

        string customRoot = runOptions is { IsPlacementValid: true }
            ? runOptions.CustomRoot
            : !string.IsNullOrWhiteSpace(_host._vm.SelectedOutputDirectory)
                ? _host._vm.SelectedOutputDirectory
                : _host.Settings.CustomOutputDirectory;

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
    internal static string BuildSourcePlacementKey(
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

            _host.AppendLog("INFO", $"[内容组] {contentGroup.Describe()}");
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
        _host.AppendLog("INFO", $"[内容组] {group.BaseName}：owner={ownerTaskFileName}。");
    }

    internal static string NormalizePlacementPath(string? path)
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
    internal bool ShouldStopAtCandidateVolumePiece(string path, ArchiveTask owner)
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
                _host.AppendLog(
                    "WARN",
                    $"[第二大步·后缀清洗] {fileName}：目标名已被占用或没有变化 ⇒ 不改名。");
                continue;
            }

            try
            {
                FileOperations.Move(entry.Path, target);

                string oldPath = entry.Path;
                entry.Path = target;
                _host._volumeLedgerState.SuffixRenamedSources.Add((oldPath, target));
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

                _host.AppendLog(
                    "INFO",
                    $"[第二大步·后缀清洗] {fileName} → {cleaned}（只改后缀，基名/内容未动）");
            }
            catch (Exception ex)
            {
                _host.AppendLog(
                    "WARN",
                    $"[第二大步·后缀清洗] {fileName}：改名失败（{ex.Message}）⇒ 保留原名。");
            }
        }

        return renamed;
    }

    /// <summary>记下"这几片是从用户自己的源目录里收拢过来的"（只记搬离过的那几份）。</summary>
    internal void RememberGatheredUserSourcePieces(IReadOnlyList<VolumeRepairItem> items)
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

            if (!_host.PieceAdoption.IsUserImportedSourcePath(item.CurrentPath))
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

    internal static string BuildVolumeGroupAttemptSignature(VolumeGroup group)
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
    internal void LogVolumeGroupFirstVolumeLanding(ArchiveTask task, string outputPath)
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
            _host.AppendLog(
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
}

using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 第二大步 B 段：组分析（只读）→ 点名入口任务（链接类）→ 补建入口任务。
///
/// <para>调用方必须**在 <c>using SecondStepWorkScope</c> 内**调用本方法（scope 生命周期与
/// 原实现一致：一轮一个句柄、循环体结束才 Dispose）；本方法只在作用域内读它的 Token。</para>
/// </summary>
internal sealed class OneClickSecondStepRunner
{
    private readonly MainViewModel _vm;
    private readonly ExtractionCoordinator _extractionCoordinator;
    private readonly OneClickBatchFinalizer _batchFinalizer;
    private readonly OneClickSecondStepEntryLinker _entryLinker;

    internal OneClickSecondStepRunner(
        MainViewModel vm,
        ExtractionCoordinator extractionCoordinator,
        OneClickBatchFinalizer batchFinalizer)
    {
        _vm = vm;
        _extractionCoordinator = extractionCoordinator;
        _batchFinalizer = batchFinalizer;
        _entryLinker = new OneClickSecondStepEntryLinker(vm, extractionCoordinator, batchFinalizer);
    }

    private ObservableCollection<ArchiveTask> Tasks => _vm.Tasks;

    private AppSettings Settings => _vm.Settings;

    private void AppendLog(string level, string message) => _vm.AppendLog(level, message);

    /// <summary>
    /// B 段主体。返回 true = 第二大步被取消（原 catch 里的 while 级 break），调用方直接 break。
    /// </summary>
    internal async Task<bool> RunAsync(
        OneClickCoordinator host,
        OneClickRunState state,
        OneClickRoundState roundState,
        IReadOnlyList<InnerArchiveCandidate> innerArchives,
        IReadOnlyList<string> scanLog,
        SecondStepWorkScope secondStepWork)
    {
        var handoffGroups = new List<VolumeCandidateDiscovery.VolumeCandidate>();

        /*
         * 第二大步"当前工作"句柄：解析 / 试开 / 命名 / 建入口根都归它。
         * 暂停或「停止当前任务」取消它 ⇒ 试开里的引擎进程立刻被取消，且不会再进下一批。
         */
        try
        {
            /*
             * ⛔ 第二大步不再物理改用户源名：`.001删 → .001` 由 StandardNameFastPath
             * 的逻辑规范名 + 入口层硬链接承担。物理改名会拆散任务/账本/prepared 三份路径
             * （真机 336 的 13:45/13:51 两次路径不一致就是这么来的）。
             */

            List<string> groupRoots = _extractionCoordinator.VolumeHandoffEntries
                .Select(entry => entry.SourceLayer)
                .Concat(_extractionCoordinator.VolumeHandoffEntries
                    .Select(entry => Path.GetDirectoryName(entry.Path ?? string.Empty) ?? string.Empty))
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            /*
             * 候选池的种子不能只来自交接单：脏名字的片（`.zip.@z002`、`.7z.=Z002`）
             * 内容认不出、闸门不会登记，可它们确实是某一组的片（用户第 1 步：基名+大小）；
             * 没有种子 ⇒ 整组永远不入候选池 ⇒ 源包/其余物那里红。
             * ⇒ 在组根里补“名字/内容像分卷片”的种子，只做候选，判定仍交给族算法。
             */
            var extraGroupSeeds = new List<string>(innerArchives.Select(candidate => candidate.Path));

            foreach (string root in groupRoots)
            {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(
                                 root,
                                 "*",
                                 SearchOption.TopDirectoryOnly))
                    {
                        if (InnerLayerCandidateClassifier.IsVolumeCandidateSeed(file))
                        {
                            extraGroupSeeds.Add(file);
                        }
                    }
                }
                catch
                {
                    // 扫不到的目录只影响候选池，不影响任何破坏性动作。
                }
            }

            List<string> analysisRoots = groupRoots
                .Concat(_extractionCoordinator.FirstStepLedger.Entries
                    .Select(entry => entry.SourceLayer)
                    .Where(layer => !string.IsNullOrWhiteSpace(layer)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            /*
             * 第二大步候选的唯一正式来源 = FirstStepFileLedger。
             * VolumeHandoffEntries 只是其中一部分，必须把账本里
             * 未单独建交接单的 Unknown/内层片也补成候选。
             */
            List<ArchiveFixer.Detection.VolumeHandoffEntry> ledgerHandoff =
                _extractionCoordinator.FirstStepLedger.Entries
                    /*
                     * ⛔ 账本收所有已发布产物（MP4/TXT/PNG 也在内），但第二大步候选只认
                     * 分卷事实：内容身份/卷号/名字槽/ZIP split 证据/交界前租约。
                     * 不过这一道会把“内容文件”当成 1 片候选（真机 15:32/15:33 的
                     * `SSNI-727-UC`、`promotion`、`点我安装`）。
                     * 脏名的 Unknown 片仍由下面的 extraGroupSeeds 扫描补进来，不在这里放宽。
                     */
                    .Where(entry => entry != null && entry.HasSecondStepEvidence)
                    .Select(entry => new ArchiveFixer.Detection.VolumeHandoffEntry
                    {
                        Path = entry.Path,
                        BaseName = entry.BaseName,
                        Format = entry.Format,
                        Size = entry.Size,
                        BodyKind = entry.BodyKind,
                        ContentKind = entry.ContentKind,
                        ContentClass = entry.ContentClass,
                        ContentFormat = entry.ContentFormat,
                        Evidence = entry.BodyKind is ArchiveFixer.Detection.ArchiveBodyKind.VolumeFirstPiece
                                or ArchiveFixer.Detection.ArchiveBodyKind.VolumePiece
                            ? ArchiveFixer.Detection.VolumeHandoffEvidence.ContentProven
                            : entry.NameSlot.HasValue
                                ? ArchiveFixer.Detection.VolumeHandoffEvidence.NameSlot
                                : ArchiveFixer.Detection.VolumeHandoffEvidence.Unknown,
                        VolumeNumber = entry.VolumeNumber,
                        NameVolumeIndex = entry.NameSlot,
                        FirstVolumeFlag = entry.FirstVolumeFlag,
                        Membership = entry.Membership,
                        DeclaredTotalBytes = entry.DeclaredTotalBytes,
                        DeclaredVolumeCount = entry.DeclaredVolumeCount,
                        RawVolumeField = entry.RawVolumeField,
                        HasZipEndRecord = entry.HasZipEndRecord,
                        ZipEndRecordMeansSplit = entry.ZipEndRecordMeansSplit,
                        HasZipLocalHeader = entry.HasZipLocalHeader,
                        SourceLayer = entry.SourceLayer,
                        Source = entry.Source
                    })
                    .Concat(_extractionCoordinator.VolumeHandoffEntries)
                    .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

            /*
             * ⛔ 2026-11-01 用户口径：「内容物保留关键词」命中的文件**碰都不要碰**，
             * 自然也不许进第二大步的分卷候选（`上老王论坛当老王.zip` 曾被当 1 片分卷组）。
             */
            ArchiveFixer.Security.ContentKeepRules keepRules =
                ArchiveFixer.Security.ContentKeepRules.FromSettings(Settings);

            ledgerHandoff = ledgerHandoff
                .Where(entry => entry != null && !keepRules.ShouldKeep(entry.Path))
                .ToList();
            extraGroupSeeds = extraGroupSeeds
                .Where(seed => !keepRules.ShouldKeep(seed))
                .ToList();

            /*
             * ⛔ 2026-11-01 真机（2.exe 与 2.part1/2/3.rar）：父包是**任务源包**，
             * 内容卡是 Unknown（内嵌归档）⇒ 任何"内容自证 SingleComplete"的过滤都抓不到它。
             * 判据放回**来源层**：根任务源包自身没有分卷证据（不是 Volume 片、没有名字卷槽）
             * ⇒ 它绝不参与自己产物所组成的分卷组。
             */
            var nonVolumeRootSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ledgerByPath = _extractionCoordinator.FirstStepLedger.Entries
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Path))
                .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            foreach (ArchiveTask root in Tasks)
            {
                if (root == null || root.IsContinuationTask)
                {
                    continue;
                }

                foreach (string? rootPath in new[] { root.CurrentPath, root.OriginalPath })
                {
                    if (string.IsNullOrWhiteSpace(rootPath)
                        || !ledgerByPath.TryGetValue(rootPath, out ArchiveFixer.Detection.FirstStepFileLedgerEntry? rootEntry))
                    {
                        continue;
                    }

                    if (rootEntry.BodyKind != ArchiveFixer.Detection.ArchiveBodyKind.VolumeFirstPiece
                        && rootEntry.BodyKind != ArchiveFixer.Detection.ArchiveBodyKind.VolumePiece
                        && !rootEntry.NameSlot.HasValue
                        && rootEntry.Membership != ArchiveFixer.Detection.VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember)
                    {
                        nonVolumeRootSources.Add(rootPath);
                    }
                }
            }

            /*
             * 路径事实（2026-11-10 真机）：账本里标成「已消费源包」的路径 = 这一单自己的源包
             * 已经解成功并定稿 ⇒ ⛔ 不论后缀、不论基名、不论名字里有几个数字，都不许再当分卷候选/种子。
             * 真机现场：`D:\ArchiveFixer\logs\ArchiveFixer_20261110_161401.log` 719/794/801/852/853 行，
             * 外层 tar 与它解出的 5 片同基名，tar 被当成"中间片"收进组 ⇒ 整包字节和翻倍 ⇒ SizeAnomaly。
             */
            foreach (ArchiveFixer.Detection.FirstStepFileLedgerEntry consumedEntry
                     in ledgerByPath.Values.Where(entry => entry.IsConsumedSource))
            {
                nonVolumeRootSources.Add(consumedEntry.Path);
            }

            UiActivity.Mark("第二大步组分析", string.Empty, "扫描交接单候选");
            List<VolumeCandidateDiscovery.VolumeCandidate> analyzedGroups = await Task.Run(
                () => VolumeCandidateDiscovery.Analyze(
                    ledgerHandoff,
                    analysisRoots,
                    extraGroupSeeds.Distinct(StringComparer.OrdinalIgnoreCase),
                    nonVolumeRootSources).ToList());
            UiActivity.Mark("空闲");

            foreach (VolumeCandidateDiscovery.VolumeCandidate group in analyzedGroups)
            {
                handoffGroups.Add(group);
                AppendLog("INFO", $"[第二大步·组分析] {group.Describe()}");
            }

            /*
             * 共用骨架接线点：族 resolver 先出 prepared 组，再让 VolumeCandidateDiscovery 只吃标准名。
             * 骨架阶段一个族都没注册 ⇒ 返回空，下面的过滤与追加都不会发生，既有行为不变。
             */
            foreach (ArchiveTask ledgerTask in Tasks)
            {
                if (!string.IsNullOrWhiteSpace(ledgerTask.CurrentPath)
                    && !string.Equals(
                        ledgerTask.OriginalPath,
                        ledgerTask.CurrentPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _extractionCoordinator.FirstStepLedger.RetargetPath(
                        ledgerTask.OriginalPath,
                        ledgerTask.CurrentPath);
                }
            }

            IReadOnlyList<PreparedVolumeGroup> preparedGroups =
                await _extractionCoordinator.VolumeBoundaryPipeline.ResolveAllAsync(
                        _extractionCoordinator.FirstStepLedger,
                        baseName =>
                        {
                            long trialLastTicks = 0;
                            int trialLastPercent = -1;

                            return new VolumeBoundaryContext
                            {
                                Ledger = _extractionCoordinator.FirstStepLedger,
                                BaseName = baseName,
                                Engine = _extractionCoordinator.ArchiveEngine,
                                WorkspaceRoot = ArchiveFixer.Extraction.RecursiveExtractor.ConfiguredWorkspaceRoot,

                                /*
                                 * 「代表路径 / 代表层」只从候选片取（SecondStepCandidates）：
                                 * 同基名可能同时挂着外层包（X.tar 与 X.7z.001 都切成 X），
                                 * 用外层包那条的路径/层会让密码探测和快通道键全偏（真机 2026-11-10）。
                                 */
                                PasswordCandidates = _extractionCoordinator.BuildVolumeProbePasswordCandidates(
                                    _extractionCoordinator.FirstStepLedger.CandidateRepresentativePath(baseName)),
                                OriginalSourceLayer = _extractionCoordinator.FirstStepLedger
                                    .CandidateSourceLayer(baseName),
                                Log = (level, message) => AppendLog(level, message),
                                Progress = new Progress<ArchiveFixer.Engines.ArchiveProgress>(progress =>
                                {
                                    long now = Environment.TickCount64;

                                    if (progress.Percent == trialLastPercent
                                        && now - trialLastTicks < 5000)
                                    {
                                        return;
                                    }

                                    trialLastPercent = progress.Percent;
                                    trialLastTicks = now;
                                    AppendLog(
                                        "INFO",
                                        progress.Percent >= 0
                                            ? $"[第二大步·试开] {baseName}：{progress.Percent}%"
                                            : $"[第二大步·试开] {baseName}：引擎正在扫描（还没有百分比）");
                                }),
                                Stalled = notice => AppendLog(
                                    "WARN",
                                    $"[第二大步·试开] {baseName}：{notice}")
                            };
                        },
                        secondStepWork.Token)
                    .ConfigureAwait(true);

            secondStepWork.Token.ThrowIfCancellationRequested();

            /*
             * ⛔ 2026-11-01 真机：字节补齐配对已在组分析层证完整（`风景01`），
             * 但 prepared resolver 对同名跨目录首片只给 Unknown；它一旦参与建组，
             * 会把 Complete 的结论顶掉。⇒ 同一基名已有字节补齐 Complete 时，
             * **非 Complete 的 prepared 组不参与**这一轮的组建设。
             */
            var byteFitCompleteBases = new HashSet<string>(
                analyzedGroups
                    .Where(group => group.ByteFitPaired)
                    .Select(group => group.BaseName),
                StringComparer.OrdinalIgnoreCase);

            if (byteFitCompleteBases.Count > 0)
            {
                preparedGroups = preparedGroups
                    .Where(group => group.IsComplete
                        || !byteFitCompleteBases.Contains(group.BaseName))
                    .ToList();
            }

            if (preparedGroups.Count > 0)
            {
                /*
                 * 标准名硬链接必须在 VolumeCandidateDiscovery 之前建好：VolumeCandidateDiscovery 只吃临时入口层。
                 * 建不出来 ⇒ 这一组落 Unknown（⛔ 不退回脏名硬拼、⛔ 不猜）。
                 */
                var materialized = new List<PreparedVolumeGroup>();

                foreach (PreparedVolumeGroup prepared in preparedGroups)
                {
                    secondStepWork.Token.ThrowIfCancellationRequested();

                    if (prepared.State != PreparedGroupState.Complete)
                    {
                        materialized.Add(prepared);
                        continue;
                    }

                    string? targetWorkRoot = _batchFinalizer.ResolvePreparedGroupTargetWorkRoot(prepared);
                    PreparedVolumeGroup? linked = VolumeGroupEntryDirectory.TryMaterialize(
                        prepared,
                        VolumeGroupEntryDirectory.TryResolveTargetEntryRoot(
                            prepared,
                            targetWorkRoot,
                            allowSourceVolumeFallback: _vm.SpaceTightMode),
                        (level, message) => AppendLog(level, $"[第二大步·标准名] {message}"),
                        out string linkError,
                        allowCrossVolumeCopy: !_vm.SpaceTightMode);

                    /*
                     * ⛔ 不再为目标盘与源片不同卷回落源卷根/盘根：
                     * 拿不到合法工作区入口就不建目录，由这一组落 Unknown。
                     */
                    if (linked == null)
                    {
                        materialized.Add(prepared.WithState(
                            PreparedGroupState.Unknown,
                            $"标准名硬链接没建好：{linkError}"));
                    }
                    else
                    {
                        materialized.Add(linked);
                    }
                }

                preparedGroups = materialized;

                /*
                 * 交界屏障：prepared 组此刻已经拿到族/片/入口，登记成内容组，
                 * 接上第一大步账本里的来源根与落点域。只登记事实，不点名、不动盘。
                 */
                _extractionCoordinator.RegisterContentGroups(preparedGroups);

                /*
                 * ⛔ 2026-11-01：旧行为"prepared 组一旦存在，同基名旧候选让位"必须保留
                 * （真机 904 批：让位被改成"只有 Complete 才让位"后，多出来的 Unknown 候选
                 * 干扰了正常组结算 ⇒ c4 过程物按档 6 条红）。
                 * 唯一例外：**字节补齐配对**已经在同基名给出 Complete，而 resolver 对该基名
                 * 只有 Unknown（2916/3100 的 `风景01`）—— 这一档保留配对组。
                 */
                var preparedBaseNames = new HashSet<string>(
                    preparedGroups.Select(group => group.BaseName),
                    StringComparer.OrdinalIgnoreCase);

                /*
                 * `SizeAnomaly` 的 prepared 组按设计没有 Pieces，不能提交；
                 * 同一基名的发现层组如果已经有完整候选，必须保留它作回退。
                 */
                var zeroPieceSizeAnomalyBases = new HashSet<string>(
                    preparedGroups
                        .Where(group => group.State == PreparedGroupState.SizeAnomaly
                                        && group.Pieces.Count == 0)
                        .Select(group => group.BaseName),
                    StringComparer.OrdinalIgnoreCase);

                handoffGroups.RemoveAll(group =>
                    preparedBaseNames.Contains(group.BaseName)
                    && !zeroPieceSizeAnomalyBases.Contains(group.BaseName)
                    && group.PreparedGroup == null
                    && !group.ByteFitPaired);

                var byteFitBaseNames = new HashSet<string>(
                    handoffGroups
                        .Where(group => group.ByteFitPaired)
                        .Select(group => group.BaseName),
                    StringComparer.OrdinalIgnoreCase);

                List<VolumeCandidateDiscovery.VolumeCandidate> preparedCandidates =
                    VolumeCandidateDiscovery.AnalyzePrepared(preparedGroups);

                foreach (VolumeCandidateDiscovery.VolumeCandidate group in preparedCandidates)
                {
                    // 字节补齐配对已经给出可证完整的组 ⇒ 同一基名的 resolver Unknown 不再叠加。
                    if (byteFitBaseNames.Contains(group.BaseName))
                    {
                        continue;
                    }

                    handoffGroups.Add(group);
                    AppendLog("INFO", $"[第二大步·prepared] {group.Describe()}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            state.Stopped = true;
            state.StoppedPhase = "第二大步解析/试开";
            return true;
        }
        catch (Exception probeError)
        {
            AppendLog(
                "WARN",
                $"[第二大步·组分析] 跳过：{probeError.GetType().Name}：{probeError.Message}");
        }

        await _entryLinker.RunNamingAsync(roundState, handoffGroups).ConfigureAwait(false);

        foreach (string line in scanLog)
        {
            AppendLog(line.Contains("[WARN]", StringComparison.Ordinal) ? "WARN" : "INFO", line);
        }

        List<ArchiveTask> volumeEntryTasks = roundState.VolumeEntryTasks;
        List<InnerArchiveCandidate> secondStepEntryCandidates = roundState.SecondStepEntryCandidates;
        var secondStepEntryGroups = roundState.SecondStepEntryGroups;

        foreach (string line in scanLog)
        {
            AppendLog(line.Contains("[WARN]", StringComparison.Ordinal) ? "WARN" : "INFO", line);
        }

        /*
         * **P3a 第二大步接住**：已整段停用（用户 2026-10-24）。
         * 实现（`TryMarkHandoffGroupEntryTasks`、搬/链接片、记账）仍保留在
         * `SecondStepResidue/ExtractionCoordinator.VolumeHandoffStep.cs`，重建第二大步时去那儿挑。
         */
        /*
         * 第二大步的入口任务晚到（例如这一组的片是上一轮刚产出的）⇒ 走与续解同一条加任务路
         * 补进表；下一轮由第二大步点名成 owner。⛔ 不绕过任务表自己造一条执行路径。
         */
        if (secondStepEntryCandidates.Count > 0)
        {
            try
            {
                List<ArchiveTask> createdSecondStepTasks =
                    await host.AddInnerTasksAsync(secondStepEntryCandidates);

                foreach (ArchiveTask createdTask in createdSecondStepTasks)
                {
                    if (!volumeEntryTasks.Contains(createdTask))
                    {
                        volumeEntryTasks.Add(createdTask);
                    }
                }

                AppendLog(
                    "INFO",
                    $"[第二大步·组] 补建入口任务 {createdSecondStepTasks.Count} 个（下一轮点名）。");

                /*
                 * 任务刚建出来就**当场点名并建好规范名链接**：
                 * 不能等下一轮边界再点名 —— 下一轮一开工就按原文件名解压，
                 * 7z 找不到 `.7z.001` 兄弟卷，直接把入口判成“无法识别”，
                 * 之后又因“上一轮 Failed”永不重跑（真机 15151515 现场）。
                 */
                foreach ((VolumeCandidateDiscovery.VolumeCandidate group, ArchiveFixer.Extraction.VolumeGroupUnit unit)
                         in secondStepEntryGroups)
                {
                    ArchiveTask? createdTask = createdSecondStepTasks.FirstOrDefault(task =>
                        SafePathHelper.PathEquals(
                            task.CurrentPath,
                            unit.NamePlan?.Items.FirstOrDefault(item => item.IsEntry)?.PiecePath
                            ?? unit.EntryPath));

                    if (createdTask == null)
                    {
                        continue;
                    }

                    string layer = Path.GetDirectoryName(
                        unit.NamePlan?.Items.FirstOrDefault(item => item.IsEntry)?.PiecePath
                        ?? unit.EntryPath) ?? string.Empty;

                    ArchiveFixer.Extraction.VolumeGroupUnit? ownedUnit =
                        _extractionCoordinator.TryCommitVolumeGroup(group, createdTask, layer);

                    if (ownedUnit?.NamePlan is { IsEmpty: false } plan)
                    {
                        ArchiveFixer.Extraction.VolumeGroupEntryLayer.PlanLinkResult linked =
                            ArchiveFixer.Extraction.VolumeGroupEntryLayer.LinkByPlan(
                                plan,
                                layer,
                                (level, message) => AppendLog(level, $"[第二大步·搬进入口层] {message}"));

                        _extractionCoordinator.MarkVolumeGroupLinked(ownedUnit, linked.LinkedNames);
                        AppendLog("INFO", $"[第二大步·搬进入口层] {group.BaseName}：{linked.Describe()}");

                        DiagnosticLog.WriteGroup(
                            _vm.Logger,
                            "group.linked",
                            group.BaseName,
                            group.Format.ToString(),
                            ownedUnit.State.ToString(),
                            ownedUnit.Verdict,
                            group.EntryPath,
                            ownedUnit.OrderedPieces,
                            ownedUnit.MissingNumbers,
                            detail: plan.Evidence);

                        if (plan.Items.FirstOrDefault(item => item.IsEntry) is { } entryItem)
                        {
                            createdTask.EngineEntryPathOverride =
                                Path.Combine(layer, entryItem.TargetName);
                        }
                    }

                    _extractionCoordinator.RememberVolumeStepTaskForRound(createdTask);
                    _extractionCoordinator.MarkVolumeHandoffConsumed(group.Pieces, "第二大步·点名（补建）");
                }
            }
            catch (Exception ex)
            {
                AppendLog(
                    "ERROR",
                    $"一键处理：把第二大步入口任务补进列表失败 —— {ex.Message}（片在产物目录里，可以自己添加）");
            }
        }

        return false;
    }
}


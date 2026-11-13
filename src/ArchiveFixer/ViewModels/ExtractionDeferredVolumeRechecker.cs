using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels
{
    /// <summary>缺卷批末补判的重检主体（从 ExtractionCoordinator.DeferredVolumeDeficitStep.cs 搬出）：跑完再补判一次、链尾收口前的事实重核。</summary>
    internal sealed class ExtractionDeferredVolumeRechecker
    {
        private readonly ExtractionCoordinator _host;

        internal ExtractionDeferredVolumeRechecker(ExtractionCoordinator host)
        {
            _host = host;
        }

        internal async Task<List<ArchiveTask>> RecheckDeferredVolumeDeficitsAsync(bool finalPass, CancellationToken cancellationToken)
        {
            var toRun = new List<ArchiveTask>();

            /*
             * 先给"吐出这一片的那几单"收场（真机第九批 CCCC 的第二个现场问题：`111.rar` / `111(2)_.zip`
             * 两个原包为什么还留着）。判据只有两条事实位：那份"谁在解"的账 + 消费方的**机器终态**（成功）。
             * 位置在名单判据之前 —— 与"缺卷留到最后再判"那份名单无关，幂等。
             */
            _host.SettleSucceededGroupPieceProducers();
            _host.SettleParentOfSucceededVolumeGroup();

            /*
             * ⛔ **已验证组里的跟班片也在这里收场**（2026-10-18 用户 `&&` 口径，`22222222` 现场）：
             * 跟班片（`IsVolumeGroupFollower` + 机器终态成功）的源片有时不在“谁吐过片”那本账里
             * （它没吐过东西、只是被借去拼组）⇒ 上面那一档扫不到它，源片一直留着。
             * 判据只有两条事实位：① 它是跟班 + 成功；② 这一组里有一个**可证完整且校验通过**的消费方
             * （`ResultCompletenessClassifier.Classify(consumer).AllowsSourceRemoval`）——`&&` 的第二步成立。
             * ⛔ 判不出消费方 / 组不完整 / 校验没过 ⇒ 什么都不做。
             */
            _host.SettleVerifiedGroupFollowers();

            /*
             * 再给"这一组的内容已经由本批另一单解出来了"的那几单收场（真机 CCCC 2026-10-06）。
             *
             * 现场（用户：「原包怎么还没有删除」「其余物怎么还留着」）：`111.z0删除2` / `111.z0删除3`
             * 是散在 `111(3)`/`111(4)` 的**续卷片**，整组由 `111.rar` 那一单解开 ⇒ 它们按跟班卷收场、
             * **源片却一直留在盘上** —— 因为上面那一档只认"吐出过片的那几单"（`_groupPieceProducers`），
             * 而这两单自己没吐过任何东西，从没进过那份账。
             *
             * 判据只有两条**事实位**：① 这一单的路径在既有那份"借来用过的源片"账（`_consumedVolumeSources`）
             * 里；② 那位消费方**真的成功了**（机器终态）。⛔ 与"如实报缺卷"那条路**同一个事实位**，
             * ⛔ 不另造第二套判据；⛔ 搬运仍只有既有出口（六道闸门一条都不绕过）。
             */
            _host.SettleConsumedGroupVolumeSources(logEntries: null);

            if (_host._volumeDeficitDeferred.Count == 0)
            {
                return toRun;
            }

            foreach (ArchiveTask task in _host._volumeDeficitDeferred.ToList())
            {
                if (task == null)
                {
                    _host._volumeDeficitDeferred.Remove(task!);
                    continue;
                }

                /*
                 * 这一单**进批末重判之前**的落点（试开暂存会把 `task.CurrentPath` 改到暂存组入口，
                 * 不能让产物跟着写进 `.ArchiveFixer.work`）—— 恢复时用这一份，⛔ 不用改过之后的。
                 */
                string taskOutputPathBeforeRecheck = task.OutputPath;

                /*
                 * ①.5 这一组的内容**正在由本批另一单解出来**、而它还没收尾 ⇒ 这一单**先不落结论**。
                 *
                 * 真机第九批 CCCC 的形状：同一组的片各自是一单（`111.z0删除2` / `111.z0删除3`），
                 * 批末补齐之后只有其中一单去把整组解开；另一单若在**同一趟**里就去判"仍然缺"，
                 * 会得到一句"分卷缺失、未开始解压"—— 而那一刻内容正在被解开（用户原话：
                 * 别在解压过程中就报分卷缺失）。等消费方收尾之后，下一次重判会走既有的
                 * <see cref="TryResolveConsumedByAnotherTask"/> 把它按**跟班卷**收场。
                 */
                if (_host.TryGetGroupConsumerPending(task, out string pendingConsumer))
                {
                    _host.AppendLog(
                        "INFO",
                        $"{task.LogName}：这一组的内容正在由「{pendingConsumer}」解出来 —— 这一单先不动，等它收尾再判（不提前把话说死）。");

                    continue;
                }

                /*
                 * ② 现归一次组（盘上事实）—— **先把散着的片收到入口包那一层，再看齐不齐**
                 * （用户 2026-10-06：「111.zip 的位置已经确定，你把分卷移过来不就是了吗」）。
                 *
                 * 为什么必须先收：`VolumeGroupDetector.Group` 的桶键是**目录 + 基名 + 族** ⇒
                 * 片散在 `111(3)\`、入口在 `111\111\` 时，两边会各自成一个"不完整桶"，
                 * 直接问它必然回"仍然缺卷"（一句会把人指错的结论）。
                 * ⛔ 判据一个字没放宽：收完仍然只由它回答"齐不齐"。
                 */
                string entryLayer = _host.ResolveBatchGroupDirectory(
                    FileNameHelper.GetArchiveBaseName(_host.ResolveDeferredGroupMemberPath(task)),
                    task,
                    _host.ResolveDeferredGroupMemberPath(task));

                entryLayer = _host.ResolveEntryLayerFallbacks(task, entryLayer);

                ArchiveFixer.Detection.CardVolumeGroupReading? gatherCardEvidence = null;
                bool contentOrderUndetermined = false;
                bool stagedOrderGroup = false;
                IReadOnlyList<string>? confirmedOrderForGroup = null;
                VolumeNamingFamily? confirmedFamilyForGroup = null;
                bool confirmedGroupForTask = false;

                /*
                 * 组解析用的层：老路 = 入口层；试开确认后 = 全新的硬链接暂存目录。
                 * ⛔ `entryLayer` 本身（落点/任务层）**不改**，否则组任务会把产物写进 `.ArchiveFixer.work`。
                 */
                string groupResolutionLayer = entryLayer;

                if (entryLayer.Length > 0)
                {
                    /*
                     * ⚠ 一行"这一组该拼到哪一层 = 哪儿"（2026-10-11 加）：这是批末唯一会**收片**的地方，
                     * 而它此前一个字都不写 —— 判出来没判出来、"片收去哪儿了"在用户日志里都看不见
                     * （真机 FFFF 那一批就是这么卡住的）。⛔ 只写程序当场算出来的值。
                     */
                    _host.AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.GroupLayerResolvedAtBatchEndFormat,
                            task.LogName,
                            entryLayer));

                    /*
                     * **收拢决策器接线（第一步：只观测、不改行为）** —— 2026-10-17 第 161 轮。
                     *
                     * `VolumeGatherPlanner` 实现用户口径的 ②分组（族+基名+卷号，至少两片）与
                     * ③硬链接试开证实（体积规律 + 试开回调）。这一轮先只把结论与理由写进日志
                     * （⛔ 不改变任何搬运行为），拿到运行期分布之后，下一轮才按 `Gather/Leave` 分流。
                     *
                     * 候选 = 这一层里与"这一单手上那一片"同族的文件（既有尺子 `BelongsToSameGroup`），
                     * ⛔ 不递归、⛔ 不猜。
                     */
                    List<string> gatherCandidates = _host.CollectVolumeCandidatePaths(
                        task,
                        entryLayer,
                        out string gatherReference,
                        out int gatherScannedInEntryLayer);

                    /*
                     * ===== 底层识别卡那一行（用户 2026-10-17：「分卷的底层识别算法还要加强」）=====
                     * 7z 的中间卷**内容里没有卷号**，只有首卷的起始头自述"整包多少字节 / 共几片" ——
                     * 所以收拢这一步要能一眼看出"这一层里到底有没有首卷、合计对不对得上"。
                     * 判据唯一出口 <see cref="VolumePiecesFromCards"/>（三条证据：首卷自述 → 体积规律 → 合计）。
                     * ⛔ 只写事实、不改搬运行为（这一轮只接线这一行）。
                     */
                    var gatherCards = new List<FileNatureCard>();

                    foreach (string candidatePath in gatherCandidates)
                    {
                        try
                        {
                            gatherCards.Add(FileNature.Inspect(candidatePath));
                        }
                        catch
                        {
                            // 读不出来就少一张卡（判据那边会如实说"判不出"）。
                        }
                    }

                    CardVolumeGroupReading cardReading = VolumePiecesFromCards.Read(gatherCards);

                    gatherCardEvidence = cardReading;

                    _host.AppendLog(
                        "INFO",
                        $"{task.LogName}：底层识别卡 —— {cardReading.Reason}"
                        + $"（卡上首卷={(cardReading.HasFirstPiece ? "有" : "没有")}"
                        + $"｜片数={cardReading.PieceCount}"
                        + $"｜自述={cardReading.DeclaredTotalBytes}"
                        + $"｜合计={cardReading.SumBytes}"
                        + (cardReading.Slots.Count == 0
                            ? "｜槽位=判不出"
                            : "｜槽位=" + string.Join(
                                " → ",
                                cardReading.Slots.Select(slot =>
                                    slot.Index + ":" + FileNameHelper.GetFileName(slot.Path)))) + "）");

                    /*
                     * ===== (d) 目标层 = **入口包所在层**，而"入口包是哪一份"由底层识别优先回答 =====
                     * 名字那把尺子判出来的层与卡指出的入口层不一致时，**以卡为准**（用户 2026-10-17 总则）。
                     * ⛔ 只在两边都判得出、且确实不同时才换；卡判不出/盘上没有 ⇒ 一个字不动。
                     */
                    if (cardReading.EntryPath.Length > 0)
                    {
                        string cardEntryLayer = Path.GetDirectoryName(cardReading.EntryPath) ?? string.Empty;

                        if (cardEntryLayer.Length > 0
                            && !string.Equals(cardEntryLayer, entryLayer, StringComparison.OrdinalIgnoreCase))
                        {
                            _host.AppendLog(
                                "INFO",
                                $"{task.LogName}：这一组该拼到哪一层 —— 名字判的是「{Path.GetFileName(entryLayer)}」，"
                                + $"底层识别卡指出入口在「{Path.GetFileName(cardEntryLayer)}」⇒ 以卡为准");

                            entryLayer = cardEntryLayer;
                        }
                    }

                    /*
                     * 分组决策：名字那把尺子先分组；**归不出组时把卡证据喂进去**（底层识别优先）。
                     */
                    VolumeGatherPlan gatherPlan = VolumeGatherPlanner.Decide(
                        gatherCandidates,
                        entryLayer,
                        probeOpens: null,
                        cardEvidence: cardReading);

                    /*
                     * ⛔ 底层识别卡说"还差字节" ⇒ 这一趟不搬（用户 2026-10-17：「三条证据任何一条不成立 ⇒ 不动」）。
                     * 只认一条硬事实：**首卷自述的整包字节数 > 手上这几片的合计**。
                     * ⚠ 措辞仍然只是"这一趟先不收"—— 对整组的判决留给第二大步（与试开那条同一个口径）。
                     */
                    if (gatherPlan.Action == VolumeGatherAction.Gather
                        && cardReading.HasFirstPiece
                        && cardReading.MissingBytes > 0)
                    {
                        long missingByCard = cardReading.MissingBytes;

                        _host.AppendLog(
                            "WARN",
                            $"{task.LogName}：这一趟先不收（底层识别卡说还差 {missingByCard} 字节，留到第二大步再判）");

                        gatherPlan = VolumeGatherPlan.Leave(
                            $"这一趟先不收（卡上说还差 {missingByCard} 字节，留到第二大步再判）");
                    }

                    OrderTrialOutcome trial = await _host.RunOrderTrialAsync(
                        task,
                        entryLayer,
                        gatherPlan,
                        gatherCandidates,
                        gatherReference,
                        cardReading,
                        groupResolutionLayer,
                        taskOutputPathBeforeRecheck,
                        gatherScannedInEntryLayer,
                        cancellationToken);

                    gatherPlan = trial.GatherPlan;
                    cardReading = trial.CardReading;
                    groupResolutionLayer = trial.GroupResolutionLayer;
                    gatherCardEvidence = trial.GatherCardEvidence;
                    contentOrderUndetermined = trial.ContentOrderUndetermined;
                    stagedOrderGroup = trial.StagedOrderGroup;
                    confirmedOrderForGroup = trial.ConfirmedOrderForGroup;
                    confirmedFamilyForGroup = trial.ConfirmedFamilyForGroup;
                    confirmedGroupForTask = trial.ConfirmedGroupForTask;
                }

                /*
                 * ⛔ **优先按内容定的规范名认这一组**（2026-10-18，困难批 `19191919`）：
                 * 入口 = 槽位 1 的规范名（通用分片 `X.zip.001`），⛔ 不让用户那一份原来的名字
                 * （`X.zip`，同一份文件的别名）冒充入口 —— 否则引擎按跨盘族入口名去找 `.z01`
                 * ⇒ `Unexpected end of archive` ⇒ 整组判缺卷（真跑逐字）。判不出 ⇒ 照旧老口径。
                 */
                VolumeGroup? group = (gatherCardEvidence == null
                        ? null
                        : ExtractionCoordinator.ResolveCanonicalGroupFromCardSlots(groupResolutionLayer, gatherCardEvidence))
                    ?? _host.ResolveGatheredGroupFromEntryLayer(
                            task,
                            entryLayer,
                        _host._deferredVolumeGroupMember.TryGetValue(task, out string? stalledForGroup) ? stalledForGroup : null)
                    ?? ExtractionCoordinator.ResolveCompleteGroupFromDisk(task, entryLayer);

                /*
                 * ⛔ **内容定序判不出 ⇒ 不许名字那把尺子再拼一个"完整的一组"**（用户 2026-10-18 口径：
                 * 全试不出 ⇒ 判不出、没内容物就如实显示失败；⛔ 不按名字硬拼）。
                 * 现场（`30303030` 组合 6 批内）：三份名字里有两份是同一份物理片的两个别名 ⇒ 名字判「3 卷完整」，
                 * 引擎打开必然 `Unexpected end of archive`；而内容试开已经回答"这一组凑不齐"。
                 */
                if (group != null && contentOrderUndetermined)
                {
                    _host.AppendLog(
                        "WARN",
                        $"{task.LogName}：内容试开定序判不出 ⇒ 这一组这一趟不动"
                        + "（⛔ 不按名字硬拼一个完整组；留下现场，等补片/下一轮再判）");

                    group = null;
                }

                if (group != null)
                {
                    string firstVolume = group.FirstVolumePath;

                    /*
                     * ===== 组事实只接“源层”这一项（2026-10-18 第 64 轮回退后的增量接线）=====
                     * 组齐这一刻把源层记下来；入口层若落在过程物目录（`其余物`/工作区），
                     * 用组事实的源层，⛔ 不把工作区当入口层。owner/顺序本轮不接，避免一次改三处。
                     */
                    string groupBaseName = string.IsNullOrWhiteSpace(group.BaseName)
                        ? FileNameHelper.GetArchiveBaseName(firstVolume)
                        : group.BaseName;

                    VolumeNamingFamily? groupFamily = gatherCardEvidence?.NamingFamily;

                    /*
                     * 源层候选按“第一个不在过程物里的层”取：当前落点 → 用户原始路径所在层 →
                     * 第 1 卷所在层 → 入口层。⛔ 不再无条件用 `Path.GetDirectoryName(firstVolume)`：
                     * 第 1 卷本身可能已经被搬进 `其余物`（`19191919` 现场），那一层不是源层。
                     */
                    string groupSourceLayer = new[]
                        {
                            taskOutputPathBeforeRecheck,
                            Path.GetDirectoryName(task.OriginalPath ?? string.Empty) ?? string.Empty,
                            Path.GetDirectoryName(firstVolume) ?? string.Empty,
                            entryLayer
                        }
                        .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate)
                            && !ProcessArtifactLayout.IsInsideDeletableProcessFolders(candidate))
                        ?? string.Empty;

                    string groupUnitKey = groupBaseName + "|" + (groupFamily?.ToString() ?? "?");

                    if (!_host._volumeGroupUnits.TryGetValue(groupUnitKey, out VolumeGroupUnit? groupUnit))
                    {
                        groupUnit = new VolumeGroupUnit(
                            groupBaseName,
                            groupFamily,
                            groupSourceLayer,
                            firstVolume);

                        _host._volumeGroupUnits[groupUnitKey] = groupUnit;

                        _host.AppendLog(
                            "INFO",
                            $"{task.LogName}：组事实建立 —— 基名「{groupBaseName}」｜族={groupFamily?.ToString() ?? "判不出"}"
                            + $"｜源层「{Path.GetFileName(groupSourceLayer)}」｜入口「{FileNameHelper.GetFileName(firstVolume)}」");
                    }

                    /* 组成功后的源包删除只走 SourceCleanupService 的唯一决策；
                     * 这里不再按祖先目录递归扫描/删除（样本目录名分支阶段 0 移除）。 */

                    /*
                     * 试开确认的顺序 + 唯一 owner **落在组事实上**（⛔ 不能只留在局部变量里）：
                     * 下面“这一单已经成功过”的守卫要读它；没有它，`3333` 那种“只先解出一片”的成功
                     * 会把整组跳过，组一次都开不了。
                     */
                    if (confirmedGroupForTask && confirmedOrderForGroup != null)
                    {
                        if (groupUnit.ConfirmedOrder == null)
                        {
                            groupUnit.ConfirmedOrder = confirmedOrderForGroup;
                        }

                        if (groupUnit.OwnerTaskFileName == null)
                        {
                            groupUnit.OwnerTaskFileName = task.FileName;
                        }
                    }

                    string groupLanding = string.Equals(
                        Path.GetFileName(groupUnit.SourceLayer),
                        groupBaseName,
                        StringComparison.OrdinalIgnoreCase)
                            ? groupUnit.SourceLayer
                            : Path.Combine(groupUnit.SourceLayer, groupBaseName);

                    if (!stagedOrderGroup
                        && groupUnit.SourceLayer.Length > 0
                        && (entryLayer.Length == 0
                            || ProcessArtifactLayout.IsInsideDeletableProcessFolders(entryLayer)))
                    {
                        _host.AppendLog(
                            "WARN",
                            $"{task.LogName}：这一组的入口层是过程物层「{Path.GetFileName(entryLayer)}」—— "
                            + $"改用组事实里的源层「{Path.GetFileName(groupUnit.SourceLayer)}」");

                        entryLayer = groupUnit.SourceLayer;
                        groupResolutionLayer = groupUnit.SourceLayer;

                        /*
                         * 只改入口层不改任务落点 ⇒ 引擎仍会按原 CurrentPath/OutputPath 把产物写回
                         * `其余物`（`19191919` 批内逐字：入口层已改，落点仍是 `其余物\pack111-L2`）。
                         * ⇒ 起点与落点一起改到源层。
                         */
                        task.CurrentPath = firstVolume;
                        task.OutputPath = groupLanding;
                        _host._stagedGroupOutputPaths[task] = groupLanding;
                    }
                    else if (!stagedOrderGroup
                        && groupUnit.SourceLayer.Length > 0
                        && !string.IsNullOrWhiteSpace(task.OutputPath)
                        && ProcessArtifactLayout.IsInsideDeletableProcessFolders(task.OutputPath))
                    {
                        _host.AppendLog(
                            "WARN",
                            $"{task.LogName}：这一单的落点在过程物层「{Path.GetFileName(task.OutputPath)}」—— "
                            + $"按组事实改到源层落点「{Path.GetFileName(groupLanding)}」");

                        task.CurrentPath = firstVolume;
                        task.OutputPath = groupLanding;
                        _host._stagedGroupOutputPaths[task] = groupLanding;
                    }

                    /*
                     * 只在“跟班互相把对方记成 consumer”这一种现场接管：把已被标成跟班成功的这一单
                     * 复位并让它开组；同一组只允许一个这样的接管者。⛔ 不动正常成功单，⛔ 不接普通 owner。
                     */
                    bool forceGroupOwner = false;

                    if (task.Outcome == TaskOutcome.Succeeded && task.IsVolumeGroupFollower)
                    {
                        if (groupUnit.OwnerTaskFileName == null)
                        {
                            _host.AppendLog(
                                "WARN",
                                $"{task.LogName}：这一单原来被标成「跟班已成功」，但这一组还没有 owner —— "
                                + "复位成待跑，由这一单把整组正经解一次（⛔ 不清它的源片、不碰用户原件）");

                            task.Outcome = TaskOutcome.Pending;
                            task.IsVolumeGroupFollower = false;
                            task.SettledWithGroupUnitName = string.Empty;
                            forceGroupOwner = true;
                            groupUnit.OwnerTaskFileName = task.FileName;
                        }
                        else if (!string.Equals(groupUnit.OwnerTaskFileName, task.FileName, StringComparison.Ordinal))
                        {
                            _host.AppendLog(
                                "INFO",
                                $"{task.LogName}：这一组已有跟班接管者「{groupUnit.OwnerTaskFileName}」—— "
                                + "这一单不重复开组");

                            _host._volumeDeficitDeferred.Remove(task);
                            task.IsVolumeDeficitDeferred = false;
                            continue;
                        }
                    }

                    /*
                     * 收齐之后**当场把这一组的状态写回任务**（`ApplyGroupInfo`：卷清单 / 缺卷名单 /
                     * 「共 N 卷」那一句）。⛔ 不能省：下一步 `ExtractionCoordinator.ResolveCompleteGroupFromDisk` 的
                     * "账上点过名的缺卷必须真的出现"读的就是这份**名单** —— 盘上明明已经收齐、
                     * 名单却还是批首那一份（`111.zip`、`111.z01`…），就会把"已经齐了"判成"仍然缺"。
                     */
                    new ArchiveFixer.Services.VolumeGroupingService().ApplyGroupInfo(task, group);

                    /*
                     * ⚠ 2026-10-17（困难批 `12121212`，运行期实测）：**这一单这一批已经成功过的，不许再为这一组跑第二遍**。
                     *
                     * <para>现场逐字：入口那一单（`打包111-W2-0.zip`，名字是伪装基名）第 1 轮就成功了
                     * （`定稿完成 —— 内容物 1 个文件`、内容物 = `打包111-L2.mp4`）；到了批末补判，
                     * 这一组的入口被认成**产物扫描导进来的那一份**（`打包111-L2.zip`）⇒ 判「现在齐了…照常跑这一组」
                     * ⇒ 日志逐字「落点 …\打包111-W2-0\打包111-W2-0 —— 取的是这一组分卷入口包「打包111-L2.zip」
                     * 所在的那一层…分卷组从入口包开解，产物就跟它走」⇒ **同一单又跑了一遍**，
                     * 成品两份 `content-60mb.bin`（判据 ① 红）。</para>
                     *
                     * <para>判据只读**机器终态**这一条事实（⛔ 不比中文、⛔ 不看名字）：已经 `Succeeded`
                     * = 「成功得到完整内容物」这件事已经成立 ⇒ 这一组的活不该再交给它。
                     * ⛔ 不碰它的终态、落点与账（它自己的成功结论一个字都不动）；
                     * 停在缺卷上那些单**照旧**走下面那两支（它们的终态不是 `Succeeded`）。</para>
                     */
                    if (task.Outcome == TaskOutcome.Succeeded)
                    {
                        bool ownsConfirmedGroup = groupUnit.ConfirmedOrder != null
                            && (groupUnit.Family == VolumeNamingFamily.SevenZipNumbered
                                || groupUnit.Family == VolumeNamingFamily.RarPart)
                            && string.Equals(
                                groupUnit.OwnerTaskFileName,
                                task.FileName,
                                StringComparison.Ordinal);

                        if (!ownsConfirmedGroup)
                        {
                            _host.AppendLog(
                                "INFO",
                                $"{task.LogName}：这一单这一批已经成功过（内容物已经得到）—— 这一组不再交给它跑第二遍"
                                + "（不重复解压：同一组只解一次）。");

                            _host._volumeDeficitDeferred.Remove(task);
                            task.IsVolumeDeficitDeferred = false;

                            continue;
                        }

                        /*
                         * 它只是先解出了**一片**，真正的整组顺序是刚刚试开确认的 ⇒ 把这一次成功
                         * 复位成待跑，由唯一 owner 把整组开一次（⛔ 不是“解了两遍”：上一遍不是这一组）。
                         */
                        _host.AppendLog(
                            "WARN",
                            $"{task.LogName}：这一单先前只解出了一片，组事实已确认整组顺序 —— "
                            + "复位成待跑，由这一单把整组正经开一次（⛔ 不重复：上一遍不是这一组）");

                        task.Outcome = TaskOutcome.Pending;
                        task.EndTime = null;
                        task.IsVolumeGroupFollower = false;
                        forceGroupOwner = true;
                    }

                    /*
                     * ⛔ **这一批已经用「完全同一组片」试过一遍 ⇒ 不再排第二趟**（2026-10-17 真机 `10101010`）。
                     *
                     * <para>现场逐行：批末补判把同一组排了 21 趟，每趟都以「判出完整的一组（1 卷）」放行 ⇒
                     * 跑 ⇒ 第一枚候选就撞结构性失败（自述 62919013 / 手上 26214400 ⇒ `VolumeMissing`）⇒
                     * 组仍不完整 ⇒ 下一趟再来。这 21 趟里有 20 趟是**同一组片、同一结论**，纯属空转。</para>
                     *
                     * <para>判据只读**既有事实**（组签名 = `GroupKey` + 每一卷「路径|字节数」）：
                     * 签名相同 ⇒ 同一批同样的片已经试过 ⇒ 拒；**只要有新片进来签名就变 ⇒ 照旧放行**
                     * （批末补判"后来又补齐了"那条合法路径不受影响）。⛔ 不放宽"缺卷 ⇒ 源包原地不动"，
                     * 也⛔ 不动本节其它判据。</para>
                     */

                    /*
                     * 补齐了 ⇒ 收拢 + 正常跑这一组（起点改到第 1 卷、快照重拍）。
                     *
                     * ⚠ 第 1 卷已经由**同批另一个任务**负责时**不改起点、也不改账**：那一组的活该由
                     * 首卷那一单干（很可能它刚刚已经解成功了）。这一单照旧按"跟班卷"走既有的跳过口径 ——
                     * 改起点会让两单指着同一个文件、把同一份内容解第二遍。
                     */
                    if (_host.IsFirstVolumeOwnedByAnotherTask(task, firstVolume) && !forceGroupOwner)
                    {
                        task.CaptureSourceSnapshot();
                    }
                    else
                    {
                        if (!string.Equals(
                                SafePathHelper.GetFullPathSafe(task.CurrentPath),
                                SafePathHelper.GetFullPathSafe(firstVolume),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            /*
                             * ⚠ 2026-10-06 试过一版"入口包在别的目录时不动 CurrentPath"，**已撤回**：
                             * 真机形状那两条 E2E 当场变成"产物一个都没有"（`Assert.NotNull` 失败）——
                             * 这一单正是靠改到入口包那一份才能把整组解开。⇒ 起点照旧改，
                             * "批末给它收场 / 搬源片"那一档改用 `OriginalPath` 判它自己那一片
                             * （见 `ResolveOwnPiecePath`），⛔ 不再动这里的起点口径。
                             */
                            new ArchiveFixer.Services.VolumeGroupingService().ApplyGroupInfo(task, group);
                            task.CurrentPath = firstVolume;

                            /*
                             * ⛔ 起点改了，但**列表那一行仍旧显示"用户自己那个文件"**（真机 CCCC 2026-10-06：
                             * "48MB 在列表里面显示到了 111(4) 那一行"）。只改显示，⛔ 起点口径一个字不动。
                             */
                            task.ShowUserFileIdentity(firstVolume, ExtractionCoordinator.FindUserOwnFileBesideItsDirectory(task, firstVolume));
                        }

                        task.CaptureSourceSnapshot();
                    }

                    _host.AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.VolumeDeficitRecheckResolvedFormat,
                            task.FileName,
                            task.VolumeInfoText));

                    _host._volumeDeficitDeferred.Remove(task);
                    _host._volumeDeficitFinalPass.Add(task);

                    /*
                     * ⚠ 2026-10-10 修：**离开名单就得把那个显示/统计事实位一起清掉**——
                     * 与下面"按跟班卷收场"那一支（`task.IsVolumeDeficitDeferred = false;`）同一套口径，
                     * 这一支原来漏了。
                     *
                     * <para><b>真机后果（运行期实测，不是推断）</b>：`111.z03` 那一单走的就是这一支
                     * （批末补齐 ⇒ 改起点 ⇒ 交 `toRun` 去把整组解开），它**成功**了（Status=「这一组解压成功」、
                     * 机器终态成功），可事实位一直是 true ⇒ 批末汇总把它算进「跳过」，
                     * 印出「成功 1 / 跳过 1」—— 那一个"跳过"其实是一件**做成了**的事。
                     * 断点证据（debug-mcp 停在 `AppendBatchSummary`）：`tasks[3].Status = "解压成功"`、
                     * `tasks[3].IsVolumeDeficitDeferred = true`、`tally.Succeeded = 1 / tally.Skipped = 1`。</para>
                     */
                    task.IsVolumeDeficitDeferred = false;

                    /*
                     * 试开定序时 `task.CurrentPath` 被改到暂存组入口（引擎要从那里开），
                     * 但**产物仍然落在这一单原来的落点**（`OutputPath`）—— 暂存目录只在
                     * `.ArchiveFixer.work` 里、批末整份清掉；⛔ 不让产物写进工作区。
                     */
                    if (stagedOrderGroup)
                    {
                        task.OutputPath = taskOutputPathBeforeRecheck;
                    }

                    toRun.Add(task);

                    /*
                     * ⚠ **这一单从现在起是"第二大步"**（用户 2026-10-11 轮次模型）：分卷阶段**只解一层** ——
                     * 解出来的东西一律交回轮判定（里面又有分卷也不就地接着解，下一轮从第二大步再来）。
                     * 位置就在"那一组齐了 ⇒ 照常跑这一组"这一刻；⛔ 这不是用户的档位设置，
                     * 产品只有完全递归，这里只是第二大步任务位。
                     */
                    _host.RememberVolumeStepTask(task);

                    /*
                     * 这一组**其余的片**：内容已经由这一单解出来 ⇒ 记进既有那份"借来用过的源片"账
                     * （`_consumedVolumeSources`），让它们按**跟班卷**收场
                     * （`TryResolveConsumedByAnotherTask` 读的就是它；⛔ 不另造第二套判据）。
                     *
                     * 真机第九批 CCCC 的现场：这一档必须也覆盖"批末补判才补上的那一组" ——
                     * 否则同一组里另一单在批末仍然会如实报「分卷缺失」，而内容其实刚刚被这一单解出来了
                     * （用户原话：别在解压过程中就报分卷缺失）。
                     */
                    _host.RememberGroupPiecesConsumedBy(task);

                    // 这一组的消费方是谁：同一组别的单在它收尾之前**不落任何结论**（见下面的判据）。
                    _host._groupConsumerByName[FileNameHelper.GetArchiveBaseName(task.CurrentPath ?? string.Empty)] = task.FileName;
                    continue;
                }

                string missing = ExtractionCoordinator.DescribeMissingVolumeNames(task);

                /*
                 * ===== 这一组的内容**已经由本批另一个任务解出来了** ⇒ 按跟班卷收场（2026-10-05 真机第八批）=====
                 *
                 * 位置刻意排在"仍缺 ⇒ 如实报缺卷"**之前**：那三片确实还没在同一个目录里凑齐，
                 * 但它们的末片是从包里解出来的、整组已经解开并发布 —— 这一单再报「分卷缺失」就是一句
                 * 会把人指错的话（汇总写"失败 3"，批末还教用户去搬卷）。
                 */
                if (_host.TrySettleAsVolumeGroupFollower(task))
                {
                    continue;
                }

                if (!finalPass)
                {
                    // 还有后续轮次 ⇒ 继续留着（⛔ 不提前把话说死）。
                    _host.AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.VolumeDeficitStillDeferredLogFormat,
                            task.FileName,
                            missing));

                    continue;
                }

                _host.MarkStillMissingAtFinalPass(task, missing);
            }

            return toRun;
        }
    }
}


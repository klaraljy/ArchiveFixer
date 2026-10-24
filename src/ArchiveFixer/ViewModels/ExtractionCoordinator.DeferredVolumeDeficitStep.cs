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
    /// <summary>缺卷不在批首判死：跑完再补判一次 + 链尾收口（第 10 块，从 Password 大杂烩搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        private async Task<List<ArchiveTask>> RecheckDeferredVolumeDeficitsAsync(bool finalPass, CancellationToken cancellationToken)
        {
            var toRun = new List<ArchiveTask>();

            /*
             * 先给"吐出这一片的那几单"收场（真机第九批 CCCC 的第二个现场问题：`111.rar` / `111(2)_.zip`
             * 两个原包为什么还留着）。判据只有两条事实位：那份"谁在解"的账 + 消费方的**机器终态**（成功）。
             * 位置在名单判据之前 —— 与"缺卷留到最后再判"那份名单无关，幂等。
             */
            SettleSucceededGroupPieceProducers();

            /*
             * ⛔ **已验证组里的跟班片也在这里收场**（2026-10-18 用户 `&&` 口径，`22222222` 现场）：
             * 跟班片（`IsVolumeGroupFollower` + 机器终态成功）的源片有时不在“谁吐过片”那本账里
             * （它没吐过东西、只是被借去拼组）⇒ 上面那一档扫不到它，源片一直留着。
             * 判据只有两条事实位：① 它是跟班 + 成功；② 这一组里有一个**可证完整且校验通过**的消费方
             * （`ResultCompletenessClassifier.Classify(consumer).AllowsSourceRemoval`）——`&&` 的第二步成立。
             * ⛔ 判不出消费方 / 组不完整 / 校验没过 ⇒ 什么都不做。
             */
            SettleVerifiedGroupFollowers();

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
            SettleConsumedGroupVolumeSources(logEntries: null);

            if (_volumeDeficitDeferred.Count == 0)
            {
                return toRun;
            }

            foreach (ArchiveTask task in _volumeDeficitDeferred.ToList())
            {
                if (task == null)
                {
                    _volumeDeficitDeferred.Remove(task!);
                    continue;
                }

                /*
                 * 这一单**进批末重判之前**的落点（试开暂存会把 `task.CurrentPath` 改到暂存组入口，
                 * 不能让产物跟着写进 `.ArchiveFixer.work`）—— 恢复时用这一份，⛔ 不用改过之后的。
                 */
                string taskOutputPathBeforeRecheck = task.OutputPath;

                // ① 再收一次卷：那时缺的那几卷可能已经被解出来 / 被收拢到位。
                TryGatherVolumesBeforeExtract(task, out _);

                /*
                 * ①.5 这一组的内容**正在由本批另一单解出来**、而它还没收尾 ⇒ 这一单**先不落结论**。
                 *
                 * 真机第九批 CCCC 的形状：同一组的片各自是一单（`111.z0删除2` / `111.z0删除3`），
                 * 批末补齐之后只有其中一单去把整组解开；另一单若在**同一趟**里就去判"仍然缺"，
                 * 会得到一句"分卷缺失、未开始解压"—— 而那一刻内容正在被解开（用户原话：
                 * 别在解压过程中就报分卷缺失）。等消费方收尾之后，下一次重判会走既有的
                 * <see cref="TryResolveConsumedByAnotherTask"/> 把它按**跟班卷**收场。
                 */
                if (TryGetGroupConsumerPending(task, out string pendingConsumer))
                {
                    AppendLog(
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
                string entryLayer = ResolveBatchGroupDirectory(
                    FileNameHelper.GetArchiveBaseName(ResolveDeferredGroupMemberPath(task)),
                    task,
                    ResolveDeferredGroupMemberPath(task));

                entryLayer = ResolveEntryLayerFallbacks(task, entryLayer);

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
                    AppendLog(
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
                    List<string> gatherCandidates = CollectGroupCandidatePaths(
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

                    AppendLog(
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
                            AppendLog(
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

                        AppendLog(
                            "WARN",
                            $"{task.LogName}：这一趟先不收（底层识别卡说还差 {missingByCard} 字节，留到第二大步再判）");

                        gatherPlan = VolumeGatherPlan.Leave(
                            $"这一趟先不收（卡上说还差 {missingByCard} 字节，留到第二大步再判）");
                    }

                    OrderTrialOutcome trial = await RunOrderTrialAsync(
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
                        : ResolveCanonicalGroupFromCardSlots(groupResolutionLayer, gatherCardEvidence))
                    ?? ResolveGatheredGroupFromEntryLayer(
                            task,
                            entryLayer,
                        _deferredVolumeGroupMember.TryGetValue(task, out string? stalledForGroup) ? stalledForGroup : null)
                    ?? ResolveCompleteGroupFromDisk(task, entryLayer);

                /*
                 * ⛔ **内容定序判不出 ⇒ 不许名字那把尺子再拼一个"完整的一组"**（用户 2026-10-18 口径：
                 * 全试不出 ⇒ 判不出、没内容物就如实显示失败；⛔ 不按名字硬拼）。
                 * 现场（`30303030` 组合 6 批内）：三份名字里有两份是同一份物理片的两个别名 ⇒ 名字判「3 卷完整」，
                 * 引擎打开必然 `Unexpected end of archive`；而内容试开已经回答"这一组凑不齐"。
                 */
                if (group != null && contentOrderUndetermined)
                {
                    AppendLog(
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

                    if (!_volumeGroupUnits.TryGetValue(groupUnitKey, out VolumeGroupUnit? groupUnit))
                    {
                        groupUnit = new VolumeGroupUnit(
                            groupBaseName,
                            groupFamily,
                            groupSourceLayer,
                            firstVolume);

                        _volumeGroupUnits[groupUnitKey] = groupUnit;

                        AppendLog(
                            "INFO",
                            $"{task.LogName}：组事实建立 —— 基名「{groupBaseName}」｜族={groupFamily?.ToString() ?? "判不出"}"
                            + $"｜源层「{Path.GetFileName(groupSourceLayer)}」｜入口「{FileNameHelper.GetFileName(firstVolume)}」");
                    }

                    TryPurgeConfirmedSevenZipGroupSourceAtFact(task, groupUnit, group);

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
                        AppendLog(
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
                        _stagedGroupOutputPaths[task] = groupLanding;
                    }
                    else if (!stagedOrderGroup
                        && groupUnit.SourceLayer.Length > 0
                        && !string.IsNullOrWhiteSpace(task.OutputPath)
                        && ProcessArtifactLayout.IsInsideDeletableProcessFolders(task.OutputPath))
                    {
                        AppendLog(
                            "WARN",
                            $"{task.LogName}：这一单的落点在过程物层「{Path.GetFileName(task.OutputPath)}」—— "
                            + $"按组事实改到源层落点「{Path.GetFileName(groupLanding)}」");

                        task.CurrentPath = firstVolume;
                        task.OutputPath = groupLanding;
                        _stagedGroupOutputPaths[task] = groupLanding;
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
                            AppendLog(
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
                            AppendLog(
                                "INFO",
                                $"{task.LogName}：这一组已有跟班接管者「{groupUnit.OwnerTaskFileName}」—— "
                                + "这一单不重复开组");

                            _volumeDeficitDeferred.Remove(task);
                            task.IsVolumeDeficitDeferred = false;
                            continue;
                        }
                    }

                    /*
                     * 收齐之后**当场把这一组的状态写回任务**（`ApplyGroupInfo`：卷清单 / 缺卷名单 /
                     * 「共 N 卷」那一句）。⛔ 不能省：下一步 `ResolveCompleteGroupFromDisk` 的
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
                            AppendLog(
                                "INFO",
                                $"{task.LogName}：这一单这一批已经成功过（内容物已经得到）—— 这一组不再交给它跑第二遍"
                                + "（不重复解压：同一组只解一次）。");

                            _volumeDeficitDeferred.Remove(task);
                            task.IsVolumeDeficitDeferred = false;

                            continue;
                        }

                        /*
                         * 它只是先解出了**一片**，真正的整组顺序是刚刚试开确认的 ⇒ 把这一次成功
                         * 复位成待跑，由唯一 owner 把整组开一次（⛔ 不是“解了两遍”：上一遍不是这一组）。
                         */
                        AppendLog(
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
                    if (IsFirstVolumeOwnedByAnotherTask(task, firstVolume) && !forceGroupOwner)
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
                            task.ShowUserFileIdentity(firstVolume, FindUserOwnFileBesideItsDirectory(task, firstVolume));
                        }

                        task.CaptureSourceSnapshot();
                    }

                    AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.VolumeDeficitRecheckResolvedFormat,
                            task.FileName,
                            task.VolumeInfoText));

                    _volumeDeficitDeferred.Remove(task);
                    _volumeDeficitFinalPass.Add(task);

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
                     * 位置就在"那一组齐了 ⇒ 照常跑这一组"这一刻；⛔ 不动用户的 `递归档位（已删除）` 设置。
                     */
                    RememberVolumeStepTask(task);

                    /*
                     * 这一组**其余的片**：内容已经由这一单解出来 ⇒ 记进既有那份"借来用过的源片"账
                     * （`_consumedVolumeSources`），让它们按**跟班卷**收场
                     * （`TryResolveConsumedByAnotherTask` 读的就是它；⛔ 不另造第二套判据）。
                     *
                     * 真机第九批 CCCC 的现场：这一档必须也覆盖"批末补判才补上的那一组" ——
                     * 否则同一组里另一单在批末仍然会如实报「分卷缺失」，而内容其实刚刚被这一单解出来了
                     * （用户原话：别在解压过程中就报分卷缺失）。
                     */
                    RememberGroupPiecesConsumedBy(task);

                    // 这一组的消费方是谁：同一组别的单在它收尾之前**不落任何结论**（见下面的判据）。
                    _groupConsumerByName[FileNameHelper.GetArchiveBaseName(task.CurrentPath ?? string.Empty)] = task.FileName;
                    continue;
                }

                string missing = DescribeMissingVolumeNames(task);

                /*
                 * ===== 这一组的内容**已经由本批另一个任务解出来了** ⇒ 按跟班卷收场（2026-10-05 真机第八批）=====
                 *
                 * 位置刻意排在"仍缺 ⇒ 如实报缺卷"**之前**：那三片确实还没在同一个目录里凑齐，
                 * 但它们的末片是从包里解出来的、整组已经解开并发布 —— 这一单再报「分卷缺失」就是一句
                 * 会把人指错的话（汇总写"失败 3"，批末还教用户去搬卷）。
                 */
                if (TrySettleAsVolumeGroupFollower(task))
                {
                    continue;
                }

                if (!finalPass)
                {
                    // 还有后续轮次 ⇒ 继续留着（⛔ 不提前把话说死）。
                    AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.VolumeDeficitStillDeferredLogFormat,
                            task.FileName,
                            missing));

                    continue;
                }

                MarkStillMissingAtFinalPass(task, missing);
            }

            return toRun;
        }

        internal async Task<IReadOnlyList<ArchiveTask>> FinalizeDeferredVolumeDeficitsAsync(CancellationToken cancellationToken = default)
        {

            /*
             * ⚠ 2026-10-08：**这一站才是"真缺卷"下结论的地方** ⇒ 先把进来时那份名单快照下来，
             * 补判跑完之后把已经离开名单的那些单的"待判"事实位清掉（它们要么被补判判出了真结论、
             * 要么已经被放行去跑）。仍在名单里的（例如"消费方还没跑完"那一支）保持"待判"。
             */
            ArchiveTask[] pending = _volumeDeficitDeferred.ToArray();

            /*
             * ⚠ 2026-10-09 **试做后已撤回**（如实记账）：本想在这里加一趟"最后一轮分卷操作"——
             * 对全表每一单再跑一次 `AdoptPublishedVolumePieces`，把"某一组还缺的那一片"接进那一层，
             * 以解掉真机 EEEE 的时序竞争（`111(3).rar` 收尾 22:44:30 早于入口 `111.zip` 落地 22:44:32
             * ⇒ 那一次接片是空操作，`111(3)\111(3)\111.z02` 一直没被接走）。
             *
             * ⛔ 实做第一步就打红：`SiblingFolderVolumeGatherTests.真机形状_过路层那一单的其余物按档删掉_48MB那处`
             * ③"盘上收口只留成品" ⇒ 留下孤儿 `111_outer\111.z01` —— 全表扫会把片接进**产出方自己的落点层**，
             * 而收尾的删除档不认那个新名字。⇒ 要做的不是"再扫一遍全表"，而是
             * **接片的目标层与收尾的清理口径必须一致**（唯一出口仍是 `ResolveBatchGroupDirectory` +
             * `PublishUnresolvedPieceLandingCopies`）。撤回后该用例恢复绿。
             */
            List<ArchiveTask> chainEndCompleted = await RecheckDeferredVolumeDeficitsAsync(
                finalPass: true,
                cancellationToken).ConfigureAwait(false);

            foreach (ArchiveTask task in chainEndCompleted)
            {
                AppendLog(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.VolumeDeficitChainEndCompleteLogFormat,
                        task.FileName));

                /*
                 * ⚠ 2026-10-09 **B1：链尾给它们一个落定结论**（原话：「链尾 toRun 落结论、Pending 收口」）。
                 *
                 * 改之前这里**只打一行日志**，那几单的机器终态一直停在 `Pending`、状态停在「等待解压」
                 * ⇒ 批末汇总按终态把它们算进「未处理」，而它其实"这一批没轮到"（原因很具体：
                 * 整组是在链尾这一趟才凑齐的，而这一批的轮次已经跑完了）。
                 *
                 * ⛔ **这不是"放宽批中间不落结论"那条判据**：判据管的是**批中间**，而走到这一行时
                 * 整条链已经跑完 —— 就地落结论正是「分卷和其他未识别的东西统统跳过，到了最后再重新
                 * 检验一步」那条口径的最后一格。
                 *
                 * ⛔ 结论**不许写成缺卷 / 跟班卷**：这一组的完整性刚在这里被判成"齐了"，
                 * 写"缺卷"是假话；内容也确实没有任何一单解过（解过的话上面
                 * `TryResolveConsumedByAnotherTask` 早就把它按跟班卷收场了）。⇒ 如实写
                 * 「这一批没轮到它」，并且**明确告诉用户再点一次就会解**。
                 */
                task.Outcome = TaskOutcome.Skipped;
                task.MarkSkipped(
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.VolumeDeficitChainEndNotRunText,
                        task.FileName));
            }

            foreach (ArchiveTask task in pending)
            {
                if (!_volumeDeficitDeferred.Contains(task))
                {
                    task.IsVolumeDeficitDeferred = false;
                }
            }

            /*
             * ⚠ 2026-10-08（EEEE 真机）：上面这一趟是**整批跑完之后**才改状态的那一处 ——
             * 导出的日志若在这里就结束，"①页每一行显示什么"那份快照会停在批首的中性档
             * （真机那两行写着「等待解压」，用户据此判定"显示不对"）。⇒ 补一份定稿后的快照。
             */
            AppendTaskRowSnapshot();

            /*
             * ⚠ 返回"链尾这一趟才凑齐、但这一批已经没有机会再跑"的那几单：调用方（一键档）拿它写汇总，
             * 测试拿它断言"一单都不许留在 `Pending`"。⛔ 它们的状态已经在上面的循环里落定。
             */
            return chainEndCompleted;
        }

        /// <summary>
        /// 这一单是不是"缺卷留到最后再判"名单里那一单（**事实位**，供一键档那道守卫用）。
        ///
        /// <para>⛔ 只读这份名单本身：不看状态、不看终态、不重推判据 —— 名单的写入点唯一
        /// （<see cref="RecordDeferredVolumeDeficit"/>）。</para>
        /// </summary>
        internal bool IsDeferredVolumeDeficit(ArchiveTask? task) =>
            task != null && _volumeDeficitDeferred.Contains(task);


        /// <summary>
        /// 最后一轮仍凑不齐：如实报缺卷（不变量 7：从头到尾没有"带着缺卷开始解"这回事），
        /// 再按盘上事实决定终态（不完整 ⇒ 部分完成，完整却解不出 ⇒ 由既有失败收口判失败）。
        /// </summary>
        private void MarkStillMissingAtFinalPass(ArchiveTask task, string missing)
        {
            // ③ 到这一步才如实报缺卷（不变量 7：从头到尾没有"带着缺卷开始解"这回事）。
            string diagnosis = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.VolumeDeficitFinalDiagnosisFormat,
                task.FileName,
                missing);

            AppendLog(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.VolumeDeficitRecheckStillFormat,
                    task.FileName,
                    missing));

            /*
             * ⚠ 2026-10-06 试过一版"批末这一档也改成「部分完成」+ WARN"，**已撤回**：
             * 全量当场 6 条红线用例变红（`对照_一直补不上_批末才如实报缺卷`、
             * `PipelineWiringTests.缺卷_真案一的现场_批末补判才拦下并点名缺第2卷` 等）——
             * **"缺卷不开始解 + 如实报缺卷"是用户早先拍过并有用例钉住的口径**，
             * 真正该改的是"内容已被接手的那些单不该走到这一支"（见下面那行排障）。
             */
            MarkStoppedBeforeExtract(task, StatusText.VolumeMissing, diagnosis);
            AppendLog("ERROR", $"分卷缺失，未开始解压：{task.FileName}，{diagnosis}");

            /*
             * ===== 到最后一刻仍然不完整 ⇒ **部分完成**，⛔ 不是失败（用户 2026-10-06）=====
             *
             * 用户原话：「假如，1_最后剩一个 `111.7z.001` 你不能说是解压失败了，这应该是部分完成，
             * 因为这个分卷不完整，到了最后检测不到完整的；2_如果最后完整了，但是解压不出来，那才是失败了」。
             *
             * 判据只读**盘上事实**（`VolumeGroupDetector` 的结论）：这一组到底完不完整 ——
             * · 不完整（缺片）⇒ `PartiallyCompleted`：我们**根本没拿到全部数据**，不是"解压失败"；
             * · 完整了却解不出来 ⇒ 走既有的失败收口 ⇒ `Failed`（那才是真失败）。
             * ⛔ 「源包一个字节都不动」这条红线一个字不改（下面既有的收尾照旧）。
             */
            var leftoverCandidates = new List<VolumeCandidate>();

            foreach (string path in task.VolumePaths)
            {
                if (File.Exists(path))
                {
                    leftoverCandidates.Add(new VolumeCandidate { Path = path });
                }
            }

            foreach (string path in CollectVolumeFilesInDirectory(
                         Path.GetDirectoryName(task.CurrentPath ?? string.Empty) ?? string.Empty))
            {
                leftoverCandidates.Add(new VolumeCandidate { Path = path });
            }

            bool stillIncomplete = VolumeGroupDetector
                .Group(leftoverCandidates)
                .Any(candidateGroup => !candidateGroup.IsComplete);

            if (stillIncomplete)
            {
                task.Outcome = TaskOutcome.PartiallyCompleted;
                AppendLog(
                    "WARN",
                    $"{task.LogName}：这一组到最后一刻仍然不完整（缺 {missing}）—— 按「部分完成」记，⛔ 不是解压失败。");
            }

            // 机器终态收口：这一单是在**任务收尾之后**才落的状态，走不到单任务那个 finally ⇒ 这里补。
            FinalizeOutcomeIfPending(task);

            /*
             * ⚠ 2026-10-08：离开"缺卷待批末判"名单的**那一刻**就要把显示/统计事实位清掉
             * （手动档不走链尾 `FinalizeDeferredVolumeDeficits`，只靠那一处会漏 ⇒ 真机表现是
             * 诊断里仍按"跳过"分组、拿不到"缺哪几卷 + 下一步：补卷"）。
             */
            task.IsVolumeDeficitDeferred = false;

            _volumeDeficitDeferred.Remove(task);
            _volumeDeficitFinalPass.Add(task);
        }

        /// <summary>
        /// 这一组的内容已经由本批另一个任务解出来 ⇒ 按跟班卷收场（不算"没做成"）。
        /// 位置必须排在"仍缺 ⇒ 如实报缺卷"之前：那一单再报缺卷就是一句把人指错的话。
        /// </summary>
        private bool TrySettleAsVolumeGroupFollower(ArchiveTask task)
        {
            if (!TryResolveConsumedByAnotherTask(task, out string consumerName))
            {
                return false;
            }

            string consumedNote = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.VolumeDeficitConsumedByOtherTaskFormat,
                task.FileName,
                consumerName);

            /*
             * 跟班卷 = **不算没做成**（唯一事实位 `IsVolumeGroupFollower` ⇒ `CountsTowardBatchOutcome`）：
             * 四处消费点（色带 / 批末诊断 / 一键汇总行 / 本批汇总）读的都是它。
             *
             * ⚠ **机器终态必须显式落 `Skipped`**：`MarkSkipped` 只写 `Status`、不写 `Outcome`
             * ⇒ 汇总会把这些单算成「未处理」。这里与既有那条跳过路逐行同一套：
             * 清校验 / 清落点 + 落终态 + 标跟班。
             */
            task.IsOutputVerified = false;
            task.OutputVerification = OutputVerificationOutcome.NotAttempted;
            task.OutputPath = string.Empty;
            task.Outcome = TaskOutcome.Skipped;
            task.IsVolumeGroupFollower = true;
            task.MarkSkipped(consumedNote);

            AppendLog("INFO", consumedNote);

            // ⚠ 2026-10-08：离开名单 ⇒ 事实位一起清（理由见下面那一处）。
            task.IsVolumeDeficitDeferred = false;

            _volumeDeficitDeferred.Remove(task);
            _volumeDeficitFinalPass.Add(task);

            return true;
        }

        /// <summary>
        /// 族规则判不出"该拼到哪一层"时的两级兜底：① 底层识别卡自证首卷 ⇒ 它所在层就是入口层；
        /// ② 还判不出 ⇒ 读接片那一档真的落地时记下的层（唯一出口 `_groupLayerByBaseName`）。
        /// ⛔ 已经判出层的一个字都不改；两级都判不出 ⇒ 原样返回空串（照旧什么都不做）。
        /// </summary>
        private string ResolveEntryLayerFallbacks(ArchiveTask task, string entryLayer)
        {
            /*
             * ===== 族规则判不出"该拼到哪一层"时，让**底层识别卡**补一次 =====
             * （用户 2026-10-17 第 4 条：「第一卷竟然还有弄不成的情况，就是识别出入口包的情况」）
             *
             * 只在 `entryLayer` **为空**时补：手上那一份如果自证是**首卷**（`VolumeFirstPiece`，
             * 名字被改到族规则认不出时它是唯一还认得出"我是入口"的证据），那它所在的层就是入口层 ——
             * 引擎就是拿它开始解的。⛔ 已经判出层的一个字都不改；⛔ 卡也判不出 ⇒ 照旧什么都不做。
             */
            if (entryLayer.Length == 0)
            {
                string ownMemberPath = ResolveDeferredGroupMemberPath(task);

                if (!string.IsNullOrWhiteSpace(ownMemberPath) && File.Exists(ownMemberPath))
                {
                    try
                    {
                        CardVolumeGroupReading ownCard =
                            VolumePiecesFromCards.Read(new[] { FileNature.Inspect(ownMemberPath) });

                        if (ownCard.EntryPath.Length > 0)
                        {
                            entryLayer = Path.GetDirectoryName(ownCard.EntryPath) ?? string.Empty;

                            if (entryLayer.Length > 0)
                            {
                                AppendLog(
                                    "INFO",
                                    $"{task.LogName}：这一组该拼到哪一层 —— 族规则判不出，改由底层识别卡指出入口"
                                    + $"（卡上自证首卷=「{FileNameHelper.GetFileName(ownCard.EntryPath)}」）");
                            }
                        }
                    }
                    catch
                    {
                        // 卡读不出来 ⇒ 照旧什么都不做。
                    }
                }
            }

            /*
             * ===== 层判不出 ⇒ 读**接片那一档算过的层**（唯一出口 `_groupLayerByBaseName`）=====
             *
             * <para>2026-10-18 困难批 `30303030` 现场：三片都已按内容规范名 `.001/.002/.003`
             * 接到 `333(2)` 那一层，可批末重判时这一单手上那一份在**已删掉的工作区**里 ⇒
             * 族规则与卡都判不出入口层 ⇒ 逐字 `该拼到哪一层=「」｜这一层不在盘上 ⇒ 判不出完整的一组`
             * ⇒ 整组明明齐了却不开始解压。</para>
             *
             * <para>判据不另造：`_groupLayerByBaseName` 就是接片真的落地那一刻记下的层
             * （`ExtractionCoordinator` 里"层只有一个出口"那条注释指的就是它）。⛔ 只在这一单判不出时用；
             * 目录不存在 ⇒ 照旧空（什么都不做）。</para>
             */
            if (entryLayer.Length == 0)
            {
                string ledgerBaseName = FileNameHelper.GetArchiveBaseName(
                    ResolveDeferredGroupMemberPath(task));

                if (ledgerBaseName.Length > 0
                    && _groupLayerByBaseName.TryGetValue(ledgerBaseName, out string? assembledLayer)
                    && !string.IsNullOrWhiteSpace(assembledLayer)
                    && Directory.Exists(assembledLayer))
                {
                    entryLayer = assembledLayer!;

                    AppendLog(
                        "INFO",
                        $"{task.LogName}：这一组该拼到哪一层 —— 族规则与卡都判不出，"
                        + $"改用接片那一档算过的层「{assembledLayer}」");
                }
            }

            return entryLayer;
        }

        /// <summary>
        /// (c) 硬链接试开 / 内容定序：通用分片先按相似度排顺序、真的解一遍验证；
        /// 其余族照旧只按内容/名字给出的那一种顺序列一次目录。
        /// ⛔ 试开只读、只交顺序；接片仍走既有出口 TryGatherGroupPiecesInto。
        /// </summary>
        private async Task<OrderTrialOutcome> RunOrderTrialAsync(
            ArchiveTask task,
            string entryLayer,
            VolumeGatherPlan gatherPlan,
            List<string> gatherCandidates,
            string gatherReference,
            CardVolumeGroupReading cardReading,
            string groupResolutionLayer,
            string taskOutputPathBeforeRecheck,
            int gatherScannedInEntryLayer,
            CancellationToken cancellationToken)
        {
            ArchiveFixer.Detection.CardVolumeGroupReading? gatherCardEvidence = null;
            bool contentOrderUndetermined = false;
            bool stagedOrderGroup = false;
            IReadOnlyList<string>? confirmedOrderForGroup = null;
            VolumeNamingFamily? confirmedFamilyForGroup = null;
            bool confirmedGroupForTask = false;

            /*
             * ===== (c) 硬链接试开 / 内容定序（用户 2026-10-17 四步里的第三步 + 2026-10-18 顺序口径）=====
             *
             * ① **通用分片**（`X.zip.001..00N`）：中间片内容里没有卷号 ⇒ 先按相似度排高概率顺序、
             *    真的解一遍验证（硬链接试开）；全试不出 ⇒ 判不出（这一趟不按名字硬拼）。
             * ② 其余族（RAR / 7z / 跨盘 ZIP）：卷序由内容或名字明确给出，照旧只按那一种顺序列一次目录。
             *
             * ⛔ 试开是只读的；赢的那一版**只交顺序**，接片仍走既有出口 `TryGatherGroupPiecesInto`
             * （它会记"谁吐出的片、谁借的片"，批末收场才能把产出方那一份按档收干净）。
             */
            AppendLog(
                "INFO",
                $"{task.LogName}：试开前决策 = {gatherPlan.Action}（{gatherPlan.Reason}）"
                + $"｜同族候选 {gatherCandidates.Count} 个");

            GroupOrderTrialResult probe = await ProbeGatherGroupAsync(
                    gatherPlan.Pieces,
                    gatherCandidates,
                    entryLayer,
                    cardReading.EntryPath,
                    gatherReference,
                    allowFallback: gatherPlan.Action == VolumeGatherAction.Gather,
                    cancellationToken)
                .ConfigureAwait(false);

            if (probe.Failure != null)
            {
                /*
                 * ⛔ 试开**只是"这一次先不搬"的闸门，不是对整组的判决**（用户 2026-10-17：
                 * 「只是简单的判断一下，不能够形成判决情况，因为这个是第二大步才做的」）。
                 * ⇒ 这里只写"这一趟先不收"，把结论留给第二大步；⛔ 不写"整组不动/判不出"。
                 *
                 * ⚠ 但**内容定序这一档适用却失败**（片数 ≤5、末片可定、引擎全试不出）⇒ 名字那把尺子
                 * 也不能再拿来硬拼一个"完整的一组"（那正是 `30303030` 反复假完整的来源）：
                 * 记下"判不出"，下面认组那一站据此跳过。
                 */
                AppendLog(
                    "WARN",
                    $"{task.LogName}：这一趟先不收（硬链接试开没通过，留到第二大步再判）—— {probe.Failure}");

                gatherPlan = VolumeGatherPlan.Leave("这一趟先不收（试开没通过，留到第二大步再判）");
                contentOrderUndetermined = probe.Applicable;
            }
            else if (probe.Confirmed)
            {
                confirmedOrderForGroup = probe.OrderedPieces;
                confirmedFamilyForGroup = probe.Family;
                confirmedGroupForTask = true;
                /*
                 * 试开真的解开了这个顺序 ⇒ 先把它按规范卷名落进一个**全新的硬链接暂存目录**，
                 * 用暂存目录当组入口（2026-10-18 新批 `24242424`）：原入口层里可能已经有早先建的
                 * `.z01/.z02` 旧名字，与内容定出的新槽位撞名；而那些旧名字同时被组任务写进
                 * `VolumePaths`，受"用户源"红线保护，原地删不得。暂存目录是全新的，不撞任何名字，
                 * 也不碰用户/任务源；接片仍只建零字节硬链接。
                 */
                ArchiveFixer.Detection.CardVolumeGroupReading? stagedReading =
                    VolumeOrderMaterializer.TryStageConfirmedOrder(
                        entryLayer,
                        probe.OrderedPieces,
                        probe.Stem,
                        probe.Family,
                        out string stagingDirectory,
                        out string stageFailure);

                if (stagedReading != null)
                {
                    cardReading = stagedReading;
                    gatherCardEvidence = stagedReading;
                    groupResolutionLayer = stagingDirectory;
                    stagedOrderGroup = true;
                    _confirmedOrderStagingDirectories.Add(stagingDirectory);
                    _stagedGroupOutputPaths[task] = taskOutputPathBeforeRecheck;

                    /*
                     * 这一组用的那几片里，来自**包装壳产出**的（`333-W1-0\333-L2.pdf`）
                     * 要记进"产出方那一份已经落地的片"那本账 —— 批末收场才会把包装壳的源包
                     * 与其产出片按档收干净（⛔ 用户源片由 `IsAnyTaskSourcePath` 自动排除）。
                     */
                    foreach (string orderedPiece in probe.OrderedPieces)
                    {
                        RememberAdoptedLandedPiece(
                            orderedPiece,
                            task,
                            probe.Stem,
                            renamedInPlace: false);
                    }

                    gatherPlan = new ArchiveFixer.Extraction.VolumeGatherPlan(
                        VolumeGatherAction.Gather,
                        "内容试开定序成立 ⇒ 用硬链接暂存目录当组入口",
                        probe.OrderedPieces);

                    AppendLog(
                        "INFO",
                        $"{task.LogName}：{probe.Reason}；已按规范卷名接进硬链接暂存目录"
                        + $"「{Path.GetFileName(stagingDirectory)}」，原入口层一个名字都不动");
                }
                else
                {
                    AppendLog(
                        "WARN",
                        $"{task.LogName}：硬链接暂存目录没建成（{stageFailure}）⇒ 退回入口层接片老路");

                    ArchiveFixer.Detection.CardVolumeGroupReading? confirmedReading =
                        VolumeOrderMaterializer.BuildConfirmedReading(
                            probe.OrderedPieces,
                            probe.Stem,
                            probe.Family);

                    if (confirmedReading == null)
                    {
                        AppendLog(
                            "WARN",
                            $"{task.LogName}：这一趟先不收（内容试开定序成立了，但折不出可用的内容读取）—— 整组不动");

                        gatherPlan = VolumeGatherPlan.Leave("这一趟先不收（内容读取折不出来）");
                        contentOrderUndetermined = true;
                    }
                    else
                    {
                        cardReading = confirmedReading;
                        gatherCardEvidence = confirmedReading;

                        gatherPlan = new ArchiveFixer.Extraction.VolumeGatherPlan(
                            VolumeGatherAction.Gather,
                            "内容试开定序成立 ⇒ 按赢的那一版接进入口层",
                            probe.OrderedPieces);

                        AppendLog("INFO", $"{task.LogName}：{probe.Reason}");
                    }
                }
            }

            AppendLog(
                "INFO",
                $"{task.LogName}：收拢决策 = {gatherPlan.Action} —— {gatherPlan.Reason}"
                + $"（这一层同族候选 {gatherCandidates.Count} 个｜这一层文件 {gatherScannedInEntryLayer} 个"
                + $"｜参照名=「{(gatherReference.Length == 0 ? "判不出" : gatherReference)}」）");

            /*
             * ⛔ **名字归不出组、但底层识别卡可证完整 ⇒ 允许搬**（用户 2026-10-17 总则：底层优先）。
             * 现场形状：片名被改烂到 `VolumeGroupDetector.Group` 一个组都归不出来，
             * 而卡上"首卷自述 + 体积规律 + 合计对上"三条全成立 —— 这时名字只是线索坏了，
             * 事实（整组齐）是成立的。搬运本体仍走 `TryGatherGroupPiecesInto`（它自己的
             * 卡证据收源 + 那几道闸门一条不落），这里只负责**放行**。
             */
            bool allowMoveIntoGroup = gatherPlan.Action == VolumeGatherAction.Gather;

            if (!allowMoveIntoGroup
                && cardReading.Complete
                && cardReading.EntryPath.Length > 0
                && File.Exists(cardReading.EntryPath))
            {
                allowMoveIntoGroup = true;

                AppendLog(
                    "INFO",
                    $"{task.LogName}：收拢决策 = Gather（名字归不出组，但底层识别卡可证完整）—— {cardReading.Reason}");
            }

            if (!stagedOrderGroup)
            {
                TryGatherGroupPiecesInto(
                    task,
                    entryLayer,
                    _deferredVolumeGroupMember.TryGetValue(task, out string? stalledMember) ? stalledMember : null,
                    allowMoveIntoGroup: allowMoveIntoGroup,
                    cardEntryPath: cardReading.EntryPath,
                    cardEvidence: cardReading);

                /*
                 * ⚠ 2026-10-13（第 15 修之后）：收片只认名字 ⇒ 名字被伪装到"读不出卷号"的那几片收不进来。
                 * 再走一步**字节数**那条硬证据（7z 起始头自述）把缺口按规范槽位名**硬链接**补上
                 * （⛔ 原件一个字节都不动；凑不出正好相等就一份都不补）。
                 */
                TryLinkMissingPiecesByByteBudget(
                    task,
                    entryLayer,
                    _deferredVolumeGroupMember.TryGetValue(task, out string? stalledForBudget) ? stalledForBudget : null,
                    cardEntryPath: cardReading.EntryPath);
            }

            return new OrderTrialOutcome(
                gatherPlan,
                cardReading,
                groupResolutionLayer,
                gatherCardEvidence,
                contentOrderUndetermined,
                stagedOrderGroup,
                confirmedOrderForGroup,
                confirmedFamilyForGroup,
                confirmedGroupForTask);
        }

        /// <summary>试开那一段的结论（把 9 个被改写的局部量一次交回调用方）。</summary>
        private sealed record OrderTrialOutcome(
            VolumeGatherPlan GatherPlan,
            CardVolumeGroupReading CardReading,
            string GroupResolutionLayer,
            ArchiveFixer.Detection.CardVolumeGroupReading? GatherCardEvidence,
            bool ContentOrderUndetermined,
            bool StagedOrderGroup,
            IReadOnlyList<string>? ConfirmedOrderForGroup,
            VolumeNamingFamily? ConfirmedFamilyForGroup,
            bool ConfirmedGroupForTask);
    }
}
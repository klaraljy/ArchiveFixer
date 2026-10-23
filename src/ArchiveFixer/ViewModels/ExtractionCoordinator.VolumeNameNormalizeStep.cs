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
    /// <summary>批首改名归一 + 改后任务表同步（第 11 块，从 Password 大杂烩搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// 同目录里还有没有"同一分卷组的别的卷"（只按名字判，**只用于兜底拒搬**）。
        ///
        /// <para>用于"分组信息没拿到时宁可不动"那一条：搬走一卷、把同组别的卷留在原地，
        /// 等于把一套完整的包拆成废件（2026-09-28 真机：内层包 4 卷只搬走了 `.001`）。</para>
        /// </summary>
        internal static bool HasSiblingVolumeBeside(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return false;
                }

                string name = FileNameHelper.GetFileName(path);

                if (!FileNameHelper.IsVolumePartFileName(name))
                {
                    return false;
                }

                string directory = Path.GetDirectoryName(path) ?? string.Empty;

                foreach (string sibling in Directory.EnumerateFiles(directory))
                {
                    string siblingName = Path.GetFileName(sibling);

                    if (string.Equals(siblingName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (FileNameHelper.IsVolumePartFileName(siblingName) &&
                        VolumeGroupDetector.BelongsToSameGroup(name, siblingName))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // 读目录失败就当"没有兄弟卷"：调用点那边保守方向是"不搬"，不会因为这里失败而多搬东西。
            }

            return false;
        }

        /// <summary>
        /// 解压前把"名字被伪装的整组卷名"改回标准名（用户 2026-09-28 三层方案的第 3 步）。
        ///
        /// <para>判据只有一处（<see cref="VolumeNameRepair.Plan"/>），执行体也只有一处
        /// （<see cref="VolumeNameRepair.TryApply"/>）—— 手动档的「修复分卷名并重试」调的是**同一对**。
        /// 这里把它挪到"每单开工前"自动做一次：一键处理与手动解压都从这个入口走，两条路行为一致。</para>
        ///
        /// <para>⛔ 底线：只改名字，不动内容；目标名被占 / 证据不足 → 一个字节都不动，写清为什么。</para>
        /// </summary>
        private async Task NormalizeDisguisedVolumeNamesAsync(ArchiveTask? task, CancellationToken cancellationToken)
        {
            if (task == null)
            {
                return;
            }

            try
            {
                string current = task.CurrentPath ?? string.Empty;

                if (string.IsNullOrWhiteSpace(current))
                {
                    return;
                }

                VolumeNameRepairPlan? plan = null;

                /*
                 * 试开探针的落点 = **本批的目标工作区根**（`<目标目录>\.ArchiveFixer.work`，
                 * 见 ApplyBatchWorkspaceRoot；不变量 12：需要临时物的地方一律由调用方把工作区根传进去）。
                 * ⛔ 它不能为空时"另找一个地方"：空 / 跨盘 ⇒ VolumeNameRepair 直接不试开、如实报"无法确认"，
                 * 代价是这一单要么按原名继续、要么报缺卷 —— 比在用户盘上开工作区划算。
                 */
                string volumeProbeWorkRoot = _pathService.WorkDirectory;
                string currentName = FileNameHelper.GetFileName(current);

                /*
                 * 跨盘 zip 那一档要拿密码去**试拼验证**（末片的中央目录把大部分片钉住了，
                 * 剩下几片"内容里一个文件都没开始"的，只能靠"按这个顺序真解一遍"来证）。
                 * 候选与解压那一步**同一套算法**（§9.5 同一件事只有一个出口），
                 * 密码只活在内存里、绝不进日志（不变量 5）。
                 */
                IReadOnlyList<string> volumeTrialPasswords = BuildVolumeProbePasswordCandidates(current);

                /*
                 * 什么时候值得问"名字那条路"：
                 * ① 自己的名字里带卷标记（老口径）；
                 * ② **账上已经归过组**（`task.IsVolumeGroup`）—— 真机 DDD（用户 2026-10-01 18:09）：
                 *    跨盘 ZIP 分了 4 片，本体 `222.zip` 是**标准名**（它不是卷标记），被改坏的是三个续卷
                 *    （`222.z0删1` / `222.z除02` / `222.z文03`）。只按 ① 判 ⇒ 名字那条路**压根不跑**，
                 *    内容那条路又只支持 2 片的跨盘 zip ⇒ 两边都不动 ⇒ 7-Zip 报 `Missing volume : 222.z01`、
                 *    整包解不开（用户原话：「我这次将 zip 多分了几个卷你就弄不了了」）。
                 * ③ **同目录里躺着一片"自述带盘号的跨盘 zip 末片"** —— 真机 FFF（用户 2026-10-01 19:23）：
                 *    一组 7 片的名字里**一个卷号都没有**（`222.z0删除A` / `222.删除C` / `222.zDDD` …），
                 *    扫描期按名字归组得到 **0 组**（他盘上实测就是这个数）⇒ ② 也不成立 ⇒ 这条路又一次
                 *    压根不会被问到（新写的"按归档自己的索引定盘"再准也轮不到它跑）。
                 *    这一档的判据只有一条事实：**目录里真有一片自述是末片**（EOCD 盘号 > 0）。
                 */
                bool namePathWorthAsking = FileNameHelper.IsVolumePartFileName(currentName)
                    || task.IsVolumeGroup
                    || VolumeNameRepair.HasSpannedZipTailNearby(current);

                /*
                 * ===== 底层识别优先（用户 2026-10-17 总则：「一开始肯定是用底层识别来弄……只有底层
                 * 做不到或者哪些地方需要名字的修改来弄的」）=====
                 * 先用**内容**那条路（族/卷号来自内容 + 硬链接试开证实），名字那条路只在它判不出时兜底。
                 * ⚠ 候选池口径一个字不改：先同目录；判不出仍由下面"放宽到邻近目录"那一段接手。
                 */
                plan = await VolumeNameRepair.PlanByContentAsync(
                    current,
                    VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(current),
                    _archiveEngine,
                    volumeProbeWorkRoot,
                    volumeTrialPasswords,
                    cancellationToken).ConfigureAwait(false);

                if ((plan == null || !plan.CanRepair) && namePathWorthAsking)
                {
                    VolumeNameRepairPlan? byName = VolumeNameRepair.Plan(
                        current,
                        VolumeNameRepair.EnumerateFileNamesInDirectory(current));

                    if (byName != null && (byName.CanRepair || plan == null))
                    {
                        plan = byName;
                    }
                }

                /*
                 * **跨目录只在"同目录判不出来"时才放宽**（用户 2026-10-01 点名的跨目录找卷 + 「这种是小概率，
                 * 一般都会放在一起」）。
                 *
                 * ⚠ 顺序很要紧：候选池一放宽，7z 那条路（"尺寸排序 + 硬链接试开"）的排列数就会跟着涨
                 * —— 邻近目录里**别的包**的卷会一起进候选，把真正那一组的顺序挤出排列上限（`MaxOrderings`）。
                 * 真机夹具上实测过：先放宽就直接让两组解不出来（产物 20 → 10）。
                 * 所以：**先用老口径（只有自己这一层）试一次；判不出来，才把范围放宽到邻近目录**。
                 */
                if (plan == null || !plan.CanRepair)
                {
                    VolumeNameRepairPlan? widened = await VolumeNameRepair.PlanByContentWithNearbyCandidatesAsync(
                        current,
                        VolumeNameRepair.EnumerateVolumeCandidatesNearby(current),
                        _archiveEngine,
                        volumeProbeWorkRoot,
                        volumeTrialPasswords,
                        cancellationToken).ConfigureAwait(false);

                    if (widened != null && widened.CanRepair)
                    {
                        plan = widened;
                    }
                    else if (plan == null)
                    {
                        plan = widened;
                    }
                }

                if (plan == null || !plan.CanRepair || plan.Items.Count == 0)
                {
                    /*
                     * ===== 名字路 / 内容路都改不出来 ⇒ 再试**跨目录收卷**（2026-10-05 口径 1）=====
                     *
                     * 真机形状：`555\B250135(1)\B250135.7z.001` + `555\B250135(2)\B250135.7z.002`
                     * —— 两半被网盘拆到两个**兄弟文件夹**里。这一档两条改名路都插不上手
                     *（`.001` 的名字本来就是标准的 ⇒ 内容路判"问题不在名字上"直接返回），
                     * 而 7-Zip 找兄弟卷**只看入口文件旁边那一层** ⇒ 引擎只看到一片 ⇒ 必然解不开。
                     *
                     * 判据与执行体都是既有出口（`ResolveCrossLayerVolumeGather` + `TryApply`），
                     * 这里给的是**找卷窗口**（基准 = 手上那一卷，⛔ 绝不是 001）；收完重新读一次盘上事实。
                     * ⛔ 判不出 / 收不成 ⇒ 一个字节都不动，照旧往下走（让引擎去判）。
                     */
                    if (task.IsVolumeGroup)
                    {
                        if (TryGatherVolumesBeforeExtract(task, out string gatheredDetail))
                        {
                            /*
                             * 收完并且**盘上自证完整** ⇒ 这一档的活干完了：名字与位置都对，
                             * 下面的流程照旧（引擎自己解得开）。
                             */
                            return;
                        }

                        if (!string.IsNullOrWhiteSpace(gatheredDetail))
                        {
                            AppendLog(
                                "WARN",
                                string.Format(
                                    System.Globalization.CultureInfo.CurrentCulture,
                                    StatusText.VolumeGatherBeforeExtractBlockedFormat,
                                    task.FileName,
                                    gatheredDetail));
                        }
                    }

                    /*
                     * ⚠ 这里**只**对"试开真跑过、结论是不成立"的情形写一行 —— 用户 2026-09-29 真机
                     * 查不出断在哪，正是因为这条路上从前**一句日志都没有**（内容级推断默默返回，
                     * 用户看到的只有后面那句「分卷缺失」，看不出程序到底试没试）。
                     *
                     * ⛔ 不能对所有"不能改"都写：内容级推断对**每个**普通包都会问一遍（名字里没有卷号 → 走内容路），
                     * 全都记一行会把第 45 条的日志纪律打掉（成功的任务只留一行，见 Item45LogAndPasswordTests）。
                     * 判据是 `TrialAttempted`（闸门放行过、硬链接与引擎调用真发生过），不是文案。
                     */
                    if (plan != null
                        && !plan.CanRepair
                        && (plan.TrialAttempted || plan.SpannedTailMissing || plan.ByteBudgetMismatch))
                    {
                        /*
                         * 三种"值得说一句"的结论共用这一行，措辞按事实分开：
                         * ① 试开真跑过（硬链接 + 引擎列目录）而不成立；
                         * ② 一次引擎都没调、但**判出了缺的是末片**（满片规律 + 跨盘标记，用户要的那句诊断）；
                         * ③ 一次引擎都没调、但**按 7z 起始头算出了还差多少字节**（2026-10-03 阶段 B：
                         *    起始头是明文、与卷序无关，"缺多少"是算得出来的事实 —— 用户问过"到底缺多少"）。
                         */
                        AppendLog(
                            "INFO",
                            plan.TrialAttempted
                                ? $"{task.LogName}：按内容试开过这一组分卷（同卷硬链接 + 引擎列目录），不成立 —— "
                                  + $"{plan.Reason}；这一单按原名继续（源文件一个字节都没动）。"
                                : $"{task.LogName}：没有动它 —— {plan.Reason}；这一单按原名继续（源文件一个字节都没动）。");
                    }

                    return;
                }

                VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

                if (!result.Success)
                {
                    AppendLog("WARN", $"{task.LogName}：分卷名没改（{result.Message}），这一单可能因此解不开。");
                    return;
                }

                AppendLog(
                    "INFO",
                    $"{task.LogName}：分卷名不标准，已按标准名改好（{plan.Items.Count} 卷，只改名字、内容一个字节没动）：{plan.Describe()}"
                    + (plan.GatheredVolumes > 0
                        ? $"。⚠ 这一组有几卷散在别的文件夹里（{plan.GatheredVolumes} 卷），已一并收进入口那一层"
                          + "（引擎找兄弟卷只看入口文件旁边那一层，散着放即使名字都对也解不开；⛔ 只在同一盘上收，不跨盘搬）"
                        : string.Empty)
                    + (plan.ProbeNeedsPassword
                        ? "。⚠ 这一组是「文件名也加密」的归档（7z -mhe / RAR -hp）：试开时引擎只认得出它是一份加密归档、"
                          + "列不出清单 —— 解压那一步要靠「密码」页里的密码本，没有可用密码就会停在「密码错误」。"
                        : string.Empty));

                // 记在任务上：状态那一列会写成"已跳过（已改回标准名）"——
                // 免得"改过名"与"这一单没解成"读起来自相矛盾（用户 2026-09-28 真机反馈）。
                task.VolumeNameAutoRenamed = true;

                // 任务上的路径同步成新名字（"谁负责更新任务路径 + 快照"的唯一出口）。
                RememberGatheredUserSourcePieces(plan.Items);

                SyncTasksAfterVolumeRename(plan.Items, task);
            }
            catch (Exception ex)
            {
                // 改名失败不该拖垮整单：写清楚，后面的流程按原名去跑（它会如实报缺卷 / 打不开）。
                AppendLog("WARN", $"{task.LogName}：分卷名自动修正跳过（{ex.Message}）。");
            }
        }

        /// <summary>
        /// **批级整组改名**：在**任何任务开工之前**，把这一批里"名字被改坏的分卷组"改回标准名。
        ///
        /// <para>为什么必须串行、必须在批首（而不是每个任务开工前各改各的）：并发跑 N 个任务时，
        /// "改名 → 归组 → 解压"三步不是原子的，而同一个包的多卷是**多个任务** ——
        /// 谁先开工不定，另一单就可能拿着**旧名字**判完归组（认不出组）再往下跑，
        /// 于是同一组被解两遍、第二棵产物树落成 `名字(1)`、后续卷那一单报「分卷缺失」，
        /// 连整组的链尾处理也被它拦住（用户 2026-10-01 真机现场）。</para>
        ///
        /// <para>位置要求两条：① 工作区根**已经定下来**（名字路不用试开，但内容级推断要拿工作区
        /// 做硬链接试开，拿不到就只能降级成"无法确认"）；② **并发启动之前**。
        /// 调用点只有 <c>StartExtractForOneClickAsync</c> 里、<c>ApplyBatchWorkspaceRoot</c> 之后那一处。</para>
        /// </summary>
        internal async Task NormalizeDisguisedVolumeNamesForBatchAsync(
            IReadOnlyList<ArchiveTask>? tasks,
            CancellationToken cancellationToken = default)
        {
            if (tasks == null || tasks.Count == 0)
            {
                return;
            }

            foreach (ArchiveTask task in tasks)
            {
                if (task == null)
                {
                    continue;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

            // 第一大步不改分卷名（改名归第二大步）
            }
        }

        /// <summary>
        /// **这一单是不是"同一分卷组的后续卷"** —— 是就跳过，不重复解一遍（一组只解一次）。
        ///
        /// <para>判据只读事实，两步：① 先按**当前真实文件名**重新归组
        /// （<see cref="OneClickCoordinator.ApplyVolumeGroupingFromDirectory"/>，与扫描期、
        /// 与手动档补救走的是同一条路 —— 扫描期那一份是按改名**前**的名字算的，已经过期）；
        /// ② 再看这一组的**第一卷**是不是任务表里**另一个任务**在负责。</para>
        ///
        /// <para>⛔ 首卷不在任务表里时**不跳**（用户只把 `.002` 拖进来那一档）：照旧让引擎去报缺卷，
        /// 绝不因为"组里没人负责"就把这一单静默吞掉 —— 那等于把一个真失败说成"已跳过"。</para>
        ///
        /// <para>跳过的这一单**不拍快照、不碰盘、不写任何产物**；它的源包跟着整组一起被处理
        /// （首卷那一单的 <c>VolumePaths</c> 记的是整组）。终态落 <see cref="TaskOutcome.Skipped"/>
        /// （既不是成功也不是失败）：批末汇总与批末诊断读的都是它。</para>
        /// </summary>
        /// <returns>true = 已经跳过（调用方必须立刻 return，什么都不许做）。</returns>
        /// <summary>
        /// 这一趟**真解出来的东西**够不够证明"密码是对的"（带这一趟引擎的结构化事实）。
        ///
        /// <para>判据本体**唯一出口** <see cref="ProducedContentGate"/> —— 单层这条路与递归那条路
        /// （<c>RecursiveExtractor</c>）问的是同一个方法，⛔ 不许各写一份（2026-10-01 只接了单层那条路，
        /// 而真机 `giu910` 走的是递归那条 ⇒ 修复等于没生效，用户白等 13 分钟）。</para>
        ///
        /// <para>为什么必须把引擎事实带上（2026-10-12 真机 `2222`）：RAR4 + 存储 + 数据加密的包上，
        /// **错密码**也会把垃圾明文按原尺寸写满盘，光数文件数与字节数会把错密码判成"密码已证实"
        /// ⇒ 候选循环当场 break、正确密码（密码本第 7 位）永远没机会被试。这里只负责把两个计数递过去。</para>
        /// </summary>
        private static bool TryMeasureProducedContent(
            string? directory,
            ArchiveOperationResult engineResult,
            out int files,
            out long bytes) =>
            ProducedContentGate.TryMeasure(
                directory,
                engineResult.ReportedSubItemErrors,
                engineResult.FailedEntryNames.Count,
                out files,
                out bytes);

        private bool SkipWhenAnotherTaskOwnsThisVolumeGroup(ArchiveTask task)
        {
            try
            {
                string currentPath = task.CurrentPath ?? string.Empty;

                if (string.IsNullOrWhiteSpace(currentPath))
                {
                    return false;
                }

                // 每次开工重算（任务对象是复用的：上一次的结论不能留到这一次）。
                task.IsVolumeGroupFollower = false;

                /*
                 * 只有"看着像分卷"或"扫描期已经被归过组"的任务才值得重新归组：
                 * 普通单卷包每次开工都去扫一遍目录，换不来任何结论。
                 *
                 * ⚠ 2026-10-01 补第三档：**同目录里有没有和它同基名、名字带卷标记的兄弟**
                 * （`222.zip` 旁边躺着 `222.z01`）—— 这一档名字上看不出来（`.zip` 不是卷标记），
                 * 而真机上恰恰就是它（用户第三 / 第四次报的那 400 MB 源卷）。
                 * 判据抽在 `OneClickCoordinator.NeedsVolumeGroupRefreshByName`（唯一出口，有用例钉着）。
                 */
                bool looksLikeVolume = OneClickCoordinator.NeedsVolumeGroupRefreshByName(task, currentPath);

                if (!looksLikeVolume)
                {
                    return false;
                }

                List<string> pathsBeforeGrouping = task.GetSnapshotPaths();

                /*
                 * **只看不写**：按本目录算一次组，用来判"自己是不是这一组的后续卷"。
                 * ⛔ 不用 `ApplyVolumeGroupingFromDirectory`：那个会把结论写回任务账上，
                 * 而账上那份可能来自改名同步 / 扫描期归组（认得出跨目录的整组）——
                 * 按目录归组只会给出本目录里那几卷，照它覆盖就把"整组一起搬"砍成"只搬第一卷"。
                 */
                ArchiveFixer.Detection.VolumeGroup? group =
                    OneClickCoordinator.ResolveVolumeGroupFromDirectory(task);

                /*
                 * ⚠ 2026-10-01（用户真机第三 / 第四 / 第五次报）：**两个来源都要问，取更全的那份**
                 * （`OneClickCoordinator.PickFullerGroup`，判据只此一份、有用例钉着）。
                 *
                 * 现场：批首整组改名把 `222.zscip` → `222.zip`、`222.z删除01` → `222.z01`（都改对了），
                 * 可任务账上的清单只是扫描期那一份（1 卷）。而 `ResolveVolumeGroupFromDirectory(222.zip)`
                 * **返回的不是 null，而是一个"只有 1 卷"的组** ⇒ 老写法下"只增不减"的闸门 `1 > 1` 不成立
                 * ⇒ 按名字补清单（2 卷）那一路永远轮不到 ⇒ 源包搬运只搬 `222.zip` 自己，
                 * 400 MB 的 `222.z01` 留在源目录。
                 */
                ArchiveFixer.Detection.VolumeGroup? byName =
                    OneClickCoordinator.ResolveVolumeGroupFromDirectoryByName(task, currentPath);

                group = OneClickCoordinator.PickFullerGroup(group, byName);

                if (group == null)
                {
                    return false;
                }

                string firstVolume = group.FirstVolumePath;

                if (string.Equals(firstVolume, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    /*
                     * 自己就是首卷：把**整组清单**补到账上（缺卷预检、源包搬运都按整组走）。
                     *
                     * ⚠ **只增不减**：新的归组结果不比账上多时，一个字都不动 ——
                     * 账上那份可能更全（见上面那段），而且"少"往往只是因为别的卷不在这个目录里。
                     * 这条也顺手保住了既有用例的语义：手工构造的组信息不会被这里悄悄改写
                     *（`整组改名之后_同批别的任务也要搬到新名字` 那条当场逮到过被砍短）。
                     */
                    /*
                     * ⚠ 2026-10-01（用户真机第五次，诊断日志给出答案）：**补清单的判据不能只看"自己是不是首卷"**。
                     *
                     * 诊断现场：`[222.zip] isGroup=True volPaths=2[222.zscip,222.z01] byName=2[222.z01,222.zip]`
                     * —— 按名字补出来的清单把**续卷排在了第 1 位**（`FirstVolumePath` 落到 `222.z01` 头上），
                     * 于是下面那个"自己是不是首卷"判不过（自己叫 `222.zip`）⇒ 清单一条都没写进账 ⇒
                     * `ResolveSourceGroup` 发现"自己的路径不在清单里"⇒ 只搬了 `222.zip` 自己，
                     * 400 MB 的 `222.z01` 留在源目录（用户连报三次的就是它）。
                     *
                     * 判据改成：**账里的卷数确实能变多**就补（这正是本方法的唯一目的）。
                     * "自己是不是首卷"只用来决定**要不要把自己也拒掉**（下面那段跟班判定），不再拦补账。
                     */
                    if (group.Volumes.Count > task.VolumePaths.Count)
                    {
                        /* 清单必须"自己在前、其余按卷序跟在后"：源包搬运按顺序读它。 */
                        var ordered = new List<ArchiveFixer.Detection.VolumeCandidate>();

                        ordered.Add(new ArchiveFixer.Detection.VolumeCandidate { Path = currentPath, Size = -1 });
                        ordered.AddRange(group.Volumes.Where(
                            v => !string.Equals(v.Path, currentPath, StringComparison.OrdinalIgnoreCase)));

                        new ArchiveFixer.Services.VolumeGroupingService().ApplyGroupInfo(
                            task,
                            new ArchiveFixer.Detection.VolumeGroup
                            {
                                GroupKey = group.GroupKey,
                                DirectoryPath = group.DirectoryPath,
                                BaseName = group.BaseName,
                                Volumes = ordered,
                                FirstVolumePath = currentPath,
                                KnownVolumeCount = group.KnownVolumeCount,
                                ExpectedVolumeCount = group.ExpectedVolumeCount,
                                IsComplete = group.IsComplete,
                                MissingVolumeNames = group.MissingVolumeNames,
                                Note = group.Note
                            });

                        /*
                         * 账变长了 ⇒ 源文件快照盯的路径集合也变了，必须跟着重拍。
                         *
                         * 快照是**按位**比对的（GetSnapshotPaths() = CurrentPath + VolumePaths，顺序即比对位置）。
                         * 名单一变长，按位比对必然错位 ⇒ 误报「源文件已变化（修改时间（没有记录）→ …）」——
                         * 这一版第一次回归时就是这样：整批 4 个任务全红，根因就在这一句之前。
                         *
                         * ⛔ 这不叫"放宽不变量 11"：变的这一项是**程序自己刚记的账**
                         *（VolumePaths 是按目录里真实存在的文件填的），不是用户改了文件；
                         * 而且只在真的写长了时才重拍 —— 用户改文件仍然照旧被拦下。
                         */
                        if (!SamePaths(pathsBeforeGrouping, task.GetSnapshotPaths()))
                        {
                            task.CaptureSourceSnapshot();
                        }
                    }

                    return false;
                }

                ArchiveTask[] table = SnapshotTaskTable(Tasks);
                bool ownedByAnotherTask = false;

                foreach (ArchiveTask candidate in table)
                {
                    if (candidate == null || ReferenceEquals(candidate, task))
                    {
                        continue;
                    }

                    if (string.Equals(candidate.CurrentPath, firstVolume, StringComparison.OrdinalIgnoreCase))
                    {
                        ownedByAnotherTask = true;
                        break;
                    }
                }

                if (!ownedByAnotherTask)
                {
                    return false;
                }

                string ownerName = FileNameHelper.GetFileName(firstVolume);

                /*
                 * 这一轮没有任何产物、也没有任何校验：上一轮留下的校验结论与落点都要清干净
                 * （任务对象是复用的 —— 不清的话界面上会挂着一条已经不成立的"输出目录"）。
                 */
                task.IsOutputVerified = false;
                task.OutputVerification = OutputVerificationOutcome.NotAttempted;
                task.OutputPath = string.Empty;
                task.Outcome = TaskOutcome.Skipped;
                task.IsVolumeGroupFollower = true;

                task.MarkSkipped(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.VolumeGroupFollowerSkippedFormat,
                    ownerName));

                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.VolumeGroupFollowerSkippedLogFormat,
                        task.FileName,
                        ownerName));

                return true;
            }
            catch (Exception ex)
            {
                // 判不出来就**照常解**（保守一侧：宁可让引擎去报缺卷，也不静默吞掉一单）。
                AppendLog("WARN", $"{task.LogName}：分卷归组跳过判定失败（{ex.Message}），这一单按原名照常处理。");

                return false;
            }
        }

        /// <summary>两份路径名单是不是**逐位相同**（大小写不敏感，与 Windows 上其它路径比较同一口径）。</summary>
        private static bool SamePaths(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// **一次改名之后，谁负责把任务路径 + 源文件快照更新到新名字** —— 全仓唯一出口。
        ///
        /// <para><b>为什么必须覆盖整个任务表、而不是"驱动改名的那一单"</b>（用户 2026-09-30 真机）：
        /// <see cref="VolumeNameRepair.TryApply"/> 改的是**整组每一卷**（<c>VolumeNameRepair.cs</c> 里
        /// `plan.Items` 逐项 <c>File.Move</c>），而"组里别的卷"完全可能是**同一批里另一个任务**指着的文件。
        /// 只同步自己那一单，别的任务就会继续指着**已经不在磁盘上**的旧名字 ——
        /// 紧接着的不变量 11 比对（<see cref="StopIfSourceChanged"/>）当场报「源文件已变化（文件不见了）」，
        /// 那一单连解压都没开始（真机：一键档 5 个任务里 2 个因此失败，报的是"文件不见了"，
        /// 而文件其实好端端地躺在同一个目录里、只是改了名）。</para>
        ///
        /// <para><b>为什么快照也必须重拍</b>：路径不是不变量 11 要保护的东西（大小与修改时间才是），
        /// 但"改名"这件事实必须由程序自己记账 —— 重拍之后基准与新名字对齐，后面各层比对的才是同一份文件。
        /// ⛔ 这**不是**"放宽成文件不在也算没变"：比对口径一个字没动，变的只是"谁的名字"。</para>
        ///
        /// <para>返回被同步到的任务（调用方要重新识别的那条手动路会用到）。名字没变的项不重拍快照
        /// （<c>BuildPlanFromOrder</c> 会把"本来就标准"的那一卷也放进 Items）。</para>
        /// </summary>
        /// <param name="items">改名计划里的每一卷（旧路径 → 新路径）。</param>
        /// <param name="primary">驱动这次改名的任务（可能在任务表里、也可能在表外，比如测试直接造的）。</param>
        internal IReadOnlyList<ArchiveTask> SyncTasksAfterVolumeRename(
            IEnumerable<VolumeRepairItem>? items,
            ArchiveTask? primary = null)
        {
            var affected = new List<ArchiveTask>();

            List<VolumeRepairItem> moves = (items ?? Enumerable.Empty<VolumeRepairItem>())
                .Where(item => item != null &&
                               !string.IsNullOrWhiteSpace(item.CurrentPath) &&
                               !string.IsNullOrWhiteSpace(item.TargetPath) &&
                               !string.Equals(item.CurrentPath, item.TargetPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (moves.Count == 0)
            {
                return affected;
            }

            /*
             * 任务表的快照：这一步跑在后台线程上（解压管线），而任务表归 UI 线程写 ——
             * 必须用**下标**拷贝，不能用 LINQ 的 ToArray：List 的枚举器带版本号检查，
             * UI 线程正在导入 / 移除时枚举会抛 "Collection was modified"
             * （AGENTS.md §11.2 记过同形状的假红），而这一抛会连带把"改名已做完"的同步丢掉。
             * 下标拷贝最坏只是拿到一份稍微过期的名单，绝不会抛。
             */
            ArchiveTask[] table = SnapshotTaskTable(Tasks);

            foreach (ArchiveTask candidate in EnumerateDistinct(primary, table))
            {
                bool touched = false;

                foreach (VolumeRepairItem move in moves)
                {
                    if (string.Equals(candidate.CurrentPath, move.CurrentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        // CurrentPath 的 setter 会顺手刷新文件名 / 目录 / 后缀 / 体积。
                        candidate.CurrentPath = move.TargetPath;

                        /*
                         * **这一步变换也要记进那一行的链里**（唯一写入出口）：整组改名走的是这条路，
                         * 只有"改名记录"本身的两个名字 —— ⛔ 不许靠账上字段比对推
                         * （用户 2026-10-07：「最简单的改名操作你都要有一个变换的过程」）。
                         */
                        candidate.AppendTransformationStep(
                            $"{Path.GetFileName(move.CurrentPath)} → {Path.GetFileName(move.TargetPath)}");

                        touched = true;
                    }

                    for (int i = 0; i < candidate.VolumePaths.Count; i++)
                    {
                        if (string.Equals(candidate.VolumePaths[i], move.CurrentPath, StringComparison.OrdinalIgnoreCase))
                        {
                            candidate.VolumePaths[i] = move.TargetPath;
                            touched = true;
                        }
                    }
                }

                if (!touched)
                {
                    continue;
                }

                // 基准跟着新名字重拍（不变量 11 的"改名不算变化"就落在这里，而不是落在比对里放宽判据）。
                candidate.CaptureSourceSnapshot();
                affected.Add(candidate);

                if (!ReferenceEquals(candidate, primary))
                {
                    /*
                     * 别的任务被顺带改了名必须**说出来**：用户看到那一行的文件名变了、
                     * 而日志里一个字都没有的话，会以为程序动错了东西（AGENTS.md §9.5"值 + 通知"）。
                     */
                    AppendLog(
                        "INFO",
                        $"{candidate.LogName}：这一卷在同一个改名批次里被改成了标准名，"
                        + "任务路径与源文件快照已同步到新名字（内容一个字节没动）。");
                }
            }

            return affected;
        }

        /// <summary>把"驱动改名的任务"与任务表合成一个**不重复**的枚举（表里已有就不重复处理）。</summary>
        private static IEnumerable<ArchiveTask> EnumerateDistinct(ArchiveTask? primary, IEnumerable<ArchiveTask> table)
        {
            var seen = new HashSet<ArchiveTask>();

            if (primary != null && seen.Add(primary))
            {
                yield return primary;
            }

            foreach (ArchiveTask candidate in table)
            {
                if (candidate != null && seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        /// <summary>
        /// 任务表的**不会抛**的快照（下标拷贝；理由见调用处）。
        /// 中途被 UI 线程改了就以已经拷到的那部分为准 —— 少同步一个任务，下一次改名还会再同步一遍。
        /// </summary>
        private static ArchiveTask[] SnapshotTaskTable(ObservableCollection<ArchiveTask> tasks)
        {
            var copy = new List<ArchiveTask>();
            int count = tasks.Count;

            try
            {
                for (int i = 0; i < count; i++)
                {
                    copy.Add(tasks[i]);
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                // 名单在拷贝途中变短了：用已经拷到的部分。
            }

            return copy.ToArray();
        }

    }
}
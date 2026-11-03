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
    /// <summary>收片/组内片编排（第 2 块，2026-10-23 从主分片搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// **产出方那一份"已经落地的片"按删除档收掉**（用户 2026-10-10 拍板：与 `111.z01` 那一档一致）。
        ///
        /// <para><b>它修的是什么</b>：同一件事 —— 用户包解出一片、那一片属于组 `111` —— 真机上出现两种盘面，
        /// 差别只取决于"整组接手"发生在"这一单定稿"之前还是之后：
        /// · `111(2)_.zip` 的 `111.z01`：接片时那片还在过程物目录里 ⇒ 走**移动** ⇒ 产出方那边不留；
        /// · `111(3).rar` 的 `111.z02`：定稿（日志行 123，18:05:49）早于接片（行 183，18:05:50）⇒ 只能**建硬链接**
        ///   ⇒ 组层那一份随后随其余物被彻底删除（行 306），**产出方这份 200 MiB 留在盘上** —— 用户当场就问
        ///   「第二大步都已经成功了为什么还要留着，而且 `111.z01` 的结构和他是一样的」。</para>
        ///
        /// <para><b>⛔ 按"组"收、不按"哪一单"收</b>（真机 2026-10-10 20:09 那次真跑逮到）：这一片被接走的那一刻，
        /// 调用方给的那个"owner"未必是产出它的那一单（批量收片那条路给的是**被重判的那一单**，它的落点树里
        /// 没有这一片）⇒ 若按"某一单"去查这份账，真机上就会出现"账上有、可就是没人来收"⇒ 200 MiB 原样留着。
        /// 所以这一档只问两件事：**这一组**有没有"已经落地的片"（写入点
        /// <see cref="RememberAdoptedLandedPiece"/> 已经按盘上事实认过产出方），以及**接手这一组的那一单**
        /// 是不是真的成功且可证完整。</para>
        ///
        /// <para><b>判据全是事实，判不出 ⇒ 什么都不做</b>（与其余物那一档同一套口径）：</para>
        /// <list type="number">
        /// <item><description>这一组**确实**有"已经落地的片"被接走过（⛔ 用户源目录里的片永远不进这份账）—— 没有 ⇒ 一个字节都不动；</description></item>
        /// <item><description>本批是「删除操作」那一档（<see cref="RestHandlingModes.Keep"/> ⇒ 一个字节都不动）；</description></item>
        /// <item><description>没被取消；</description></item>
        /// <item><description>**接手它的那一单机器终态是「完成」**（⛔ 不比中文文案，与其余物那一档第一道门槛同一把尺子）；</description></item>
        /// <item><description>**接手它的那一单可证完整 + 校验通过**（L4 唯一出口
        /// <see cref="ResultCompletenessClassifier"/>，与 <see cref="PurgePassThroughRestOfSettledProducer"/>
        /// 同一份证据来源）—— 判不出 ⇒ 一个字节都不动；</description></item>
        /// <item><description>那一片**此刻还在盘上**、名字仍属于这一组（<see cref="LegacyVolumeNames.TryGetIndex"/>
        /// + <see cref="FileNameHelper.GetArchiveBaseName"/> 两把既有尺子）、而且仍在**当时记下的那个落点根**之内；</description></item>
        /// <item><description>没命中「内容物保留关键词」（<see cref="ContentKeepRules"/>，与其余物那一档同一道闸门）。</description></item>
        /// </list>
        ///
        /// <para>删除动作本身仍走**唯一执行体** <see cref="Storage.RecycleBinService"/>（含 <c>AllowedRoot</c> 安全前置），
        /// 本方法只决定"什么时候试、试哪一份"。⚠ 删除**不写回任务账**（`OutputPath` 早已被收场清空），
        /// 也不改任何结论：删不掉只写 WARN。</para>
        /// </summary>
        /// <summary>
        /// **第二大步的轮边界：把这一轮解开过的那一组卷按删除档收掉**（2026-10-11，用户口径：
        /// 「**分卷记账是第二大步的事**」）。
        ///
        /// <para><b>为什么不在收场那一档做</b>（调试器实测，EEE，同步帧读到的运行期事实）：
        /// 收场那一刻任务表里**只剩用户导入的那 4 单**（逐次读表：`111.rar` / `111(2)_.zip` / `111(3).rar` / `111.z03`，
        /// 四单 `IsContinuationTask` 全 false），把这一组解开的那一单（`111.part1.rar`）**已经不在表里**
        /// ⇒ 任何"读任务表"的判据都看不见它（上一版"扫全表的续解卷"就是这么一次都没命中的）。</para>
        ///
        /// <para>轮边界手上正好有**这一轮解出来的是哪几组分卷**（`RoundProductClassifier.VolumeEntries` = 每组的入口包）
        /// ⇒ 就在**入口包所在那一层**上收，⛔ 不读任务表、⛔ 不看时序。</para>
        ///
        /// <para>门槛与执行体与 <see cref="PurgeGroupLandedPieces"/> **逐条同一套**：
        /// 删除档不是「不动」/ 未取消 / 这一单完成且校验通过 / 可证完整（L4 唯一出口）/ 没命中「内容物保留关键词」/
        /// ⛔ **用户导入的原包**一个字节都不碰（红线出口 <see cref="IsUserImportedSourcePath"/> ——
        /// 这一档**不能**用宽尺子 <see cref="IsAnyTaskSourcePath"/>，理由见那个方法的说明）；
        /// 删除动作仍只有 <see cref="Storage.RecycleBinService"/> 一个出口。</para>
        /// </summary>
        /// <param name="entryPath">这一组的**入口包**路径（`RoundProductClassifier.VolumeEntries` 里那一条）。</param>
        internal void HarvestSolvedGroupVolumesForRound(string entryPath)
        {
            /*
             * ⚠ **记住"目录 + 这一组的身份"，⛔ 不记那条路径**（2026-10-11，判据输入实测两轮）：
             * 轮判定判出"这一组是分卷"的那一刻，**改名与解压都还没发生** —— 实测那一刻盘上还是脏名
             * （`入口包=「111.parts1.racr」，找到那一单=判不出`）；而把它改名成规范名之后，
             * **记下来的那条路径就没了**（下一轮逐字 `在盘上=False`）⇒ 记路径永远收不到。
             * ⇒ 记 `(目录, 这一组的参照名)`，每轮边界**当场按族尺子在那一层重新找**这一组的卷；
             * 门槛自带"那一层里得有某一单是已解开 + 校验通过" ⇒ 没解开的组自然被挡住，重复试是幂等的。
             */
            if (!string.IsNullOrWhiteSpace(entryPath))
            {
                string directory = Path.GetDirectoryName(entryPath) ?? string.Empty;

                if (directory.Length > 0
                    && !_volumeGroupEntriesThisBatch.Any(
                        item => string.Equals(item.Directory, directory, StringComparison.OrdinalIgnoreCase)
                                && LegacyVolumeNames.BelongsToSameGroup(
                                    item.ReferenceName,
                                    FileNameHelper.GetFileName(entryPath))))
                {
                    _volumeGroupEntriesThisBatch.Add((directory, FileNameHelper.GetFileName(entryPath)));
                }
            }

            HarvestRememberedGroupVolumes();
        }

        /// <summary>
        /// 把记住的每一组都试一遍："这一组解开了吗 ⇒ 收它的卷"（**两遍**：先定清单、再过闸门、最后才删）。
        ///
        /// <para><b>为什么要分两遍</b>（2026-10-11 全量实测打红 3 条守门用例后定的）：
        /// 第一遍只把"这一趟要收哪些卷"按组定下来（六道门槛一条不落）；
        /// 第二遍把**这一趟全部候选**一起交给半套分卷那道闸门
        /// （唯一出口 <see cref="RestVolumeCompletenessGate"/> —— 25 GB 那次事故立的红线：
        /// 「同组还有一片留在别处 ⇒ 一个字节都不删」）。</para>
        ///
        /// <para>⛔ **候选必须是"这一趟的并集"，不许一组一组各看各的**：真机 EEE 两层里各有一对
        /// `111.part1.rar` / `111.part2.rar`（基名、族全同），逐个看 ⇒ 各自都会把对方读成
        /// "同组还有一片留在别处" ⇒ **互相拦死、两份都收不掉**（实测：EEE ③ 从 1 个退回 5 个）。
        /// 并集之后"外面还剩什么"问的才是那道红线真正要问的事。</para>
        /// </summary>
        private void HarvestRememberedGroupVolumes()
        {
            var plans = new List<(ArchiveTask Consumer, List<string> Targets)>();

            foreach ((string Directory, string ReferenceName) group in _volumeGroupEntriesThisBatch.ToList())
            {
                (ArchiveTask Consumer, List<string> Targets)? plan =
                    PlanHarvestOneSolvedGroupVolumes(group.Directory, group.ReferenceName);

                if (plan.HasValue && plan.Value.Targets.Count > 0)
                {
                    plans.Add((plan.Value.Consumer, plan.Value.Targets));
                }
            }

            if (plans.Count == 0)
            {
                return;
            }

            var everythingThisPass = new List<string>();

            foreach ((ArchiveTask _, List<string> targets) in plans)
            {
                everythingThisPass.AddRange(targets);
            }

            foreach ((ArchiveTask consumer, List<string> targets) in plans)
            {
                var others = new List<string>();

                foreach (string path in everythingThisPass)
                {
                    if (!targets.Any(target => SafePathHelper.PathEquals(target, path)))
                    {
                        others.Add(path);
                    }
                }

                IReadOnlyList<string> roots = EnumerateSplitGateArtifactRoots(consumer, targets);

                if (roots.Count == 0)
                {
                    // 与「逐层回收」那一档同一条兜底：判不出 ⇒ 什么都不做（删除不可恢复）。
                    AppendLog(
                        "WARN",
                        $"{consumer.LogName}：拿不到成品目录树（任务上没有记下可用的输出目录）—— "
                        + "判不出同组还有没有别的片留在里面，所以这一组的卷这一个字节都不收（删除不可恢复）");
                    continue;
                }

                string? blocker = null;

                foreach (string root in roots)
                {
                    blocker = RestVolumeCompletenessGate.DescribeBlockerForCandidates(
                        targets,
                        root,
                        HarvestCandidatePrefix,
                        HarvestBlockerTail,
                        requireRecognizedCandidates: false,
                        others);

                    if (blocker != null)
                    {
                        break;
                    }
                }

                if (blocker != null)
                {
                    AppendLog("WARN", $"{consumer.LogName}：{blocker}");
                    continue;
                }

                DeleteHarvestTargets(consumer, targets);
            }
        }

        /// <summary>
        /// **批末再试一遍**（2026-10-11，判据输入实测）：轮边界那一刻这一组**还没被解开**
        /// （逐字：`这一组的卷 2 份，但那一层里没有哪一单是「已解开 + 校验通过」`）——
        /// 因为它的解压发生在**批末**那一站。⇒ 一整批跑完（含批末那一站）之后再试一次，其余门槛照旧。
        /// </summary>
        internal void HarvestRememberedGroupVolumesForBatch() => HarvestRememberedGroupVolumes();

        /// <summary>
        /// 试一次"这一组已经解开了吗"（判据见方法体里的门槛）；解开且该收 ⇒ 交回
        /// **这一组在这一层里该收的那几份**（⛔ 不在这里删：删除在
        /// <see cref="HarvestRememberedGroupVolumes"/> 过了半套分卷那道闸门之后）。
        /// </summary>
        private (ArchiveTask Consumer, List<string> Targets)? PlanHarvestOneSolvedGroupVolumes(
            string directory,
            string referenceName)
        {
            /*
             * ⚠ **判据输入**（同一条规矩：判不出就把判据输入写进日志）：这一档的早退原来全是静默的
             * —— 实测 EEE ③ 一点没变、日志里一个字都没有，分不出"没被调用 / 被哪道闸门挡下"。
             * 下面每一条早退都把当场算出来的值写出来。⛔ 只写事实，不改任何判据。
             */
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                AppendLog(
                    "WARN",
                    $"第二大步收卷：跳过（判据输入）｜那一层=「{directory}」，在盘上={Directory.Exists(directory)}");
                return null;
            }

            // 这一组此刻在那**一层**里的那几份（当场按族尺子重新认，⛔ 不用记下来的旧路径）。
            var members = new List<string>();

            foreach (string member in CollectVolumeFilesInDirectory(directory))
            {
                if (File.Exists(member)
                    && LegacyVolumeNames.BelongsToSameGroup(FileNameHelper.GetFileName(member), referenceName))
                {
                    members.Add(member);
                }
            }

            if (members.Count == 0)
            {
                return null;
            }

            // 这一组**解开了吗**：那一层里得有某一单是"这一组的入口包 + 已解开 + 校验通过"。
            ArchiveTask? consumer = null;

            foreach (string member in members)
            {
                ArchiveTask? owner = FindTaskByPath(member);

                if (owner != null
                    && owner.Outcome == TaskOutcome.Succeeded
                    && owner.OutputVerification == OutputVerificationOutcome.Passed)
                {
                    consumer = owner;
                    break;
                }
            }

            if (consumer == null)
            {
                AppendLog(
                    "WARN",
                    $"第二大步收卷：跳过（判据输入）｜那一层=「{directory}」，这一组的卷 {members.Count} 份，"
                    + "但那一层里没有哪一单是「已解开 + 校验通过」⇒ 判不出 ⇒ 一个字节都不动");
                return null;
            }

            string mode = RestHandlingModes.Normalize(RestHandlingThisBatch);

            if (string.Equals(mode, RestHandlingModes.Keep, StringComparison.OrdinalIgnoreCase)
                || IsStopping
                || _operationCts?.IsCancellationRequested == true
                || !ResultCompletenessClassifier.Classify(consumer).AllowsSourceRemoval)
            {
                AppendLog(
                    "WARN",
                    $"第二大步收卷：跳过（判据输入）｜那一层=「{directory}」，删除档=「{mode}」，"
                    + $"已取消={IsStopping || _operationCts?.IsCancellationRequested == true}，"
                    + $"可证完整={ResultCompletenessClassifier.Classify(consumer).AllowsSourceRemoval}");
                return null;
            }

            string entryName = referenceName;

            if (entryName.Length == 0)
            {
                return null;
            }

            ContentKeepRules keepRules = ContentKeepRules.FromSettings(Settings);
            var targets = new List<string>();

            foreach (string path in CollectVolumeFilesInDirectory(directory))
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                string name = FileNameHelper.GetFileName(path);

                /*
                 * ⛔ 判据只有一条（既有那把宽尺子）：**族 + 基名与入口包同一组**。
                 *
                 * ⛔ **用户源路径红线照旧，但尺子换成窄的那把**（2026-10-11 调试器定案）：
                 * 宽尺子 `IsAnyTaskSourcePath` 问的是"任何一单的路径"，而第二大步要收的**正是这一组
                 * 自己的那几片** —— 它们就是入口那一单（续解任务）自己的路径 ⇒ 宽尺子把这一档
                 * 拦成"一个字节都不动"，而且是**静默**的（下面那条 `continue` 原来不写日志）。
                 * 实测（断点停在 `HarvestOneSolvedGroupVolumes` 的删除循环）：EEE 第 2 轮边界那一趟，
                 * `members.Count=2`、`consumer != null`、六道门槛全过、求值
                 * `this.IsAnyTaskSourcePath(path)` = **true** ⇒ 两份都被跳过 ⇒ 盘上一个字节没少。
                 */
                if (!LegacyVolumeNames.BelongsToSameGroup(name, entryName))
                {
                    continue;
                }

                if (IsUserImportedSourcePath(path))
                {
                    // 「判不出 / 跳过」必须把判据输入写进日志：这一档原来全静默，正是它把 ③ 藏了两轮。
                    AppendLog(
                        "WARN",
                        $"{consumer.LogName}：这一组的卷 {name} 是「用户导入的原包」自己（不是这一批的产出）"
                        + " —— 一个字节都不碰，留在原处。");
                    continue;
                }

                string? keyword = keepRules.FindMatch(path);

                if (keyword != null)
                {
                    AppendLog(
                        "INFO",
                        $"{consumer.LogName}：这一组的卷 {name} 命中了「内容物保留关键词」（{keyword}）"
                        + " —— 按设置碰都不碰，这一份留在原处。");
                    continue;
                }

                targets.Add(path);
            }

            return targets.Count == 0 ? null : (consumer, targets);
        }


        private void RememberGroupPieceProducer(string baseName, string pieceName, ArchiveTask? owner)
        {
            if (owner == null || baseName.Length == 0)
            {
                return;
            }

            // 并发写：见 `_groupPieceProducersGate` 的说明（这里就是那个栈顶）。
            lock (_groupPieceProducersGate)
            {
                if (!_groupPieceProducers.TryGetValue(baseName, out List<(ArchiveTask Task, string PieceName)>? producers))
                {
                    producers = new List<(ArchiveTask Task, string PieceName)>();
                    _groupPieceProducers[baseName] = producers;
                }

                if (!producers.Any(item => ReferenceEquals(item.Task, owner)))
                {
                    producers.Add((owner, pieceName));
                }
            }
        }

        /// <summary>
        /// **吐出这一组某一片的那几单**按跟班卷收场（真机第九批 CCCC：`111.rar` / `111(2)_.zip`）。
        ///
        /// <para>它们各自只吐一片、自己因为缺对方那一片停在中途 ⇒ 落成「部分完成」⇒ 源包照"没成功就不动"
        /// 留在盘上（用户当场问：为什么这两个原包还留着）。可整组已经由 <paramref name="consumer"/> 解开、
        /// **可证完整** ⇒ 它们的内容**全在解出来的那一组里了**。</para>
        ///
        /// <para>⛔ 只认记账里那几单（<see cref="RememberGroupPieceProducer"/> 写、这里读）；⛔ 不碰消费方自己；
        /// ⛔ 搬运仍只有既有出口 <c>ExecuteSourcePackageMove</c>（六道闸门一条都不绕过）；
        /// ⛔ 已经收过场的（跟班 + 已搬）不重复做。</para>
        /// </summary>
        private void SettleGroupPieceProducers(
            ArchiveTask consumer,
            bool moveSources,
            List<(string Level, string Message)> logEntries)
        {
            string groupBaseName = FileNameHelper.GetArchiveBaseName(consumer.CurrentPath ?? string.Empty);

            if (groupBaseName.Length == 0)
            {
                return;
            }

            /*
             * **产出方那一份"已经落地的片"按删除档收掉**（用户 2026-10-10 点名的那条不一致）。
             *
             * ⛔ 位置必须在这里、**排在下面那份"谁吐出过片"的账之前**：真机 2026-10-10 20:09 那次真跑里，
             * 这一片是被"批量收片"那条路接走的（`owner` 给的是**被重判的那一单**，它的落点树里没有这一片），
             * 于是它**既没进那份账、也没人替它收** ⇒ 200 MiB 原样留着。这一档只认"这一组 + 接手方成功且可证完整"，
             * ⛔ 与"哪一单"无关。判据与红线见 `PurgeGroupLandedPieces`（幂等：片不在盘上就是一个字节都不动）。
             */
            if (!TrySnapshotGroupPieceProducers(
                    groupBaseName,
                    out List<(ArchiveTask Task, string PieceName)> producers))
            {
                return;
            }

            foreach ((ArchiveTask producer, string producedPieceName) in producers.ToList())
            {
                SettleOneConsumedPieceTask(producer, consumer, groupBaseName, producedPieceName, moveSources, logEntries);
            }

            /*
             * ===== 包装壳那一档：产出被这一组接手、组可证完整 ⇒ 也按交接收场（2026-10-18 用户 `&&` 口径）=====
             *
             * `_groupPieceProducers` 只收「自己吐过片」的单；包装壳（用户导入的外层包，解开后只有一片分卷）
             * 走的是「落点副本被接手」那条路，只进 `_adoptedLandedPieces`。两本账各补各的，
             * 判据仍是同一条 `&&`：第一大步成功 + 第二大步（组）可证完整且内容校验通过。
             * ⛔ 不碰消费方自己；⛔ 已在 producers 里的不重复；⛔ 片路径从账上取（名字脏也能用内容卡判族）。
             */
            foreach ((ArchiveTask Producer, string GroupBaseName, string PiecePath, string OutputRoot) adopted
                     in _adoptedLandedPieces.ToList())
            {
                if (adopted.Producer == null
                    || ReferenceEquals(adopted.Producer, consumer)
                    || !string.Equals(adopted.GroupBaseName, groupBaseName, StringComparison.OrdinalIgnoreCase)
                    || producers.Any(item => ReferenceEquals(item.Task, adopted.Producer)))
                {
                    continue;
                }

                SettleOneConsumedPieceTask(
                    adopted.Producer,
                    consumer,
                    groupBaseName,
                    FileNameHelper.GetFileName(adopted.PiecePath),
                    moveSources,
                    logEntries,
                    producedPiecePath: adopted.PiecePath);
            }
        }

        /// <summary>收**一片**：按规范卷名接到"这一组还缺卷"那一单的目录里；末片在手时顺手把整组接齐。</summary>

        /// <summary>
        /// **跨链收卷**（2026-10-05 真机第九批 CCCC；用户口径：「现在找不到的先跳过，等全部结束之后再看看」
        /// 「让批末那一站去查本批各单已经解出来的片」）。
        ///
        /// <para><b>现场</b>：一组跨盘 ZIP 四片分在四户人家 —— <c>.z02</c>/<c>.z03</c> 在用户源目录、
        /// 第 1 片 <c>111.z01</c> 压在 <c>111(2)_.zip</c> 里、末片 <c>111.zip</c> 压在 <c>111.rar</c> 里。
        /// 两条链各自把**对方缺的那一片解了出来**，可两条链自己都没走完（互相等对方）⇒ 收尾时工作区整份删掉
        /// ⇒ 批末那一站到盘上一看：缺的东西刚刚被自己扔掉（真机 22:06:02 与 22:06:12 那两行「已清理工作区」）。</para>
        ///
        /// <para>做法只有一件事：把这一趟**打不开的那几片**按**规范卷名**硬链接到"这一组还缺卷"的那一单
        /// 自己所在的目录里（真机 = <c>111(4)\</c>，与那两片源卷同一个目录），随后**转调既有出口**
        /// <see cref="VolumeNameRepair.ResolveSpannedZipDiskGather"/> 把这一组其余的片（散在兄弟目录里的）
        /// 也按规范名接过来 —— 判据（末片自述 / 一片对一片 / 除末片外等大 / 同盘）一条都没另写。</para>
        ///
        /// <para>⛔ 只是**硬链接**（零字节）：不改名、不搬、不覆盖、不删用户的任何文件；跨盘 / 判不出 /
        /// 建不了 ⇒ 什么都不做（⛔ 绝不复制大文件）。这几片按用户 2026-10-05 的拍板算**源包**：
        /// 这一组真解开之后，它们跟着一起进其余物、按删除档处理。</para>
        /// </summary>
        /// <param name="publishedPieces">
        /// <see cref="PublishUnresolvedVolumePieces"/> 刚落地的那几份（**落地之后的路径**）。
        /// ⛔ 不许拿 <see cref="RecursionResult.UnresolvedVolumePieces"/> 里那份原路径代替 ——
        /// 落地是移动，原路径已经不存在了。
        /// </param>
        private void AdoptUnresolvedVolumePieces(
            RecursionResult? recursion,
            ArchiveTask? owner,
            IReadOnlyList<string>? publishedPieces = null)
        {
            var pieces = new List<string>();

            if (publishedPieces != null)
            {
                pieces.AddRange(publishedPieces);
            }

            if (recursion != null)
            {
                pieces.AddRange(recursion.UnresolvedVolumePieces);
            }

            if (pieces.Count == 0)
            {
                return;
            }

            foreach (string piece in pieces)
            {
                try
                {
                }
                catch (Exception ex)
                {
                    // 加法：判不出来一律退回"照旧"（这一片跟着工作区一起消失，与改动前一个字都不差）。
                    AppendLog("WARN", $"跨链收卷跳过（{ex.Message}）。");
                }
            }
        }

        /// <summary>
        /// 这一单**刚发布出来的成品**里，有没有"某一组还缺的那一片" —— 有就按规范卷名接到那一组的目录里。
        ///
        /// <para>挂点 = <see cref="PostProcessSuccessAsync"/> 的收尾（校验通过之后、清理工作区之前）⇒
        /// **单层路与轮次续解那条路自动都在内**（它们每一轮都走这一个收尾）。⛔ 判据与执行体与递归路
        /// 是同一份 <see cref="TryAdoptUnresolvedVolumePiece"/> —— 这里只负责"挑出像分卷片的文件"。</para>
        ///
        /// <para>⛔ 只扫这一层（不递归）；⛔ 一个字节都不复制、不改名、不覆盖：`TryAdoptUnresolvedVolumePiece`
        /// 只在"那一组的目录里还没有这个规范卷名 + 同一个盘"时才建一个零字节硬链接，
        /// 所以那一组已经齐的时候这一档是**空操作**。</para>
        /// </summary>
        private void AdoptPublishedVolumePieces(ArchiveTask task)
        {
            if (task == null)
            {
                return;
            }

            /*
             * 两处都要看（真机 CCCC 单层路实测）：
             * · **成品那一层**（`ContentDirectoryPath`）—— 内容物里的片；
             * · **过程物那一层**（`RestDirectoryPath`）—— 单层路把"内层包"当过程物放进其余物，
             *   而这一组的片**恰恰就是内层包**（`111.zip` 就是在那儿被下一轮当新任务又解了一次）。
             * 这些片由 `TryAdoptUnresolvedVolumePiece` 按"过程物目录里的用移动"那一条接走。
             */
            foreach (string directory in new[] { task.ContentDirectoryPath, task.RestDirectoryPath })
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    continue;
                }

                try
                {
                    foreach (string file in Directory.EnumerateFiles(directory))
                    {
                        string name = Path.GetFileName(file);

                        /*
                         * ⛔ **用户导入的原包自己 —— 这一档一个都不接**（2026-10-11 调试器 + 环台定案）。
                         *
                         * <para><b>运行期事实（`batch_evaluate` 打在 `TryAdoptUnresolvedVolumePiece` 的日志行上，
                         * 逐字）</b>：`piecePath=…\in\111\111\111\其余物\111.rar`、`canonical=111.rar`、
                         * `owner.FileName=111.part1.rar`、`layerOverride=null`、`renamedInPlace=true`
                         * —— 即：**用户那份源包**（先按「源包 → 其余物」搬进过程物目录）被这条扫描当成"像分卷片"，
                         * 又按"过程物目录里的用移动"搬进了产出层 `…\in\111\111\111`。它的字节级身份 = 与
                         * `H:\…\测试\EEE\111\111.rar` 的 SHA-256 完全相同（`472D3B50…BE7D8`）。
                         * 后果：它已经不在其余物里 ⇒ 其余物那一档再也删不到它 ⇒ 判据 ③ 永远剩这一个
                         * （它**不是**这一组的片：老式族 `111.rar` 与本层那一组的新式/ZIP 族只是同基名）。</para>
                         *
                         * <para>⛔ 这一条**只收窄这一档**：用户的原包**按用户选的那一档处理**（其余物 / 删除），
                         * 不在这里被改名、更不许被搬走。真正需要把一个用户的原包接进某一组时，走的是
                         * <see cref="TryGatherGroupPiecesInto"/>（它自带"族 + 基名"那把尺子，且只在批末
                         * "这一组该拼到哪一层"算出来之后才跑）—— 那一条路一个字没动。</para>
                         */
                        if (IsUserImportedSourcePath(file))
                        {
                            continue;
                        }

                        /*
                         * 只挑"像分卷片"的：名字带卷标记（`.z01` / `.001` / `part1` …），
                         * 或者它自己自述是跨盘 ZIP 的末片（末片的 EOCD 是明文 ⇒ 这一条是**硬证据**）。
                         *
                         * ⛔ **再加一道底层识别**（用户口径：「统筹到同一组上面去…先看看底层能够识别到文件是什么东西」）：
                         * 走 <see cref="FileNature.Inspect"/> 那张卡 —— 内容**可证"不是分卷组的一员"** 就**不接**
                         * （第 125 轮那个真凶就是"名字像分卷片"的单卷包；那道红线原来只挡"用户的原包"这一档，
                         * 这一道把它**推广到任何来源**的同一形状，包括我们自己产出的单卷包）。
                         */
                        FileNatureCard nature = FileNature.Inspect(file);

                        bool plausible = nature.ParticipatesInVolumeHandling
                            && (nature.VolumeIndexByName != null
                                || nature.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                                || Detection.SpannedZipIndex.TryRead(file) != null);

                        if (!plausible)
                        {
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 加法：判不出来一律退回"照旧"（这一片就留在原地当内容物 / 过程物）。
                    AppendLog("WARN", $"跨链收卷跳过（{ex.Message}）。");
                }
            }
        }

        /// <summary>
        /// **链因「分卷缺失」停下时，把它已经解出来、下一步要用的产物搬回它自己那条链的家**
        /// （用户 2026-10-06 口径：「第一步就让你将能解压的统统解压了」「这个根本就不是部分完成，
        /// 对于 `111.rar` 这个就是成功了，只不过这个内容物又是下一轮解压需要的东西」）。
        ///
        /// <para><b>现场</b>：`111.rar` 第 0 层解出 `111\111.rar`、第 1 层解出 **`111\111.zip`**，
        /// 第 2 层要开 `111.zip` 时缺 `.z01` ⇒ 这一层不开（不试）。而 `111.zip` 是下一轮**那一组跨盘 ZIP
        /// 的入口包** —— 旧口径把它当"没走完的现场"，收尾连工作区一起删掉（真机磁盘上四个文件夹里
        /// **一个 `111.zip` 都没有**，就是这个原因）。⇒ 现在：**认得出是分卷片/入口包的，一律搬到
        /// `owner` 自己那条链的落点目录**（`CCCC\111\111\`），随后的收片与整组解压都在那一层。</para>
        ///
        /// <para>⛔ 动作只有"同卷改名"（我们自己工作区里的文件，零字节代价）；⛔ 用户源目录里的片
        /// 这一档碰都不碰（名字与字节都不动）；⛔ 搬不动（占用 / 目标被目录占着）⇒ 留在原地、
        /// 什么都不做（随后由既有的接片那一档按规范卷名处理）。</para>
        /// </summary>
        private List<string> PublishUnresolvedVolumePieces(RecursionResult? recursion, ArchiveTask? owner)
        {
            var published = new List<string>();

            if (recursion == null || recursion.UnresolvedVolumePieces.Count == 0 || owner == null)
            {
                return published;
            }

            /*
             * ⛔ **搬进"暂存目录"，不是搬进落点目录**（2026-10-06 真机 + 守门用例当场逮到）：
             * 收尾那条链（结果校验 / 定稿 / 源包处理）读的是 `<任务工作区>\stage`，
             * 而落点目录要等**定稿**按落点公式才建。上一版直接搬进落点目录 ⇒ 校验看到的是**空的暂存目录**
             * ⇒「校验未通过：输出目录是空目录，没有产物」⇒ ① 列表显示「解压失败」② 源包一个字节都不处理
             * （真机 `ArchiveFixer-本次操作_20261006_112403.txt` 第 121/123/127 行）。
             *
             * ⇒ 搬进暂存目录之后：校验看得见它、定稿按落点公式把它落到 `111\111\`、源包照常处理。
             */
            string stage = _pathService.BuildTaskStageDirectory(owner);

            if (string.IsNullOrWhiteSpace(stage))
            {
                return published;
            }

            try
            {
                Directory.CreateDirectory(stage);
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"{owner.LogName}：暂存目录建不出来（{ex.Message}），按原样留着。");
                return published;
            }

            foreach (string pieceFromResult in recursion.UnresolvedVolumePieces)
            {
                try
                {
                    string piece = pieceFromResult;

                    if (!File.Exists(piece))
                    {
                        /*
                         * ⚠ 原路径经常已经不在（递归核心收尾会清自己的工作区）—— 那一刻它已经把这一片
                         * **抢救复制**到我们的暂存目录里了（`RecursiveExtractor.UnresolvedPieceRescueDirectory`）
                         * ⇒ 这里改用抢救副本，别当成"没东西可搬"（真机 2026-10-06 排障日志逐字：
                         * `那一片已经不在盘上（…\output\111.zip）` ⇒ 搬了 0 份 ⇒ 落地那一层永远没有入口包）。
                         */
                        string rescued = Path.Combine(stage, FileNameHelper.GetFileName(pieceFromResult));

                        if (!File.Exists(rescued))
                        {
                            continue;
                        }

                        piece = rescued;
                    }

                    string name = FileNameHelper.GetFileName(piece);

                    /*
                     * ===== 两个目的地，职责分开（真机 CCCC 2026-10-06 第二次追问后拆开）=====
                     *
                     * ① **暂存目录**（这里就建 / 搬）—— 收尾链（结果校验 / 定稿）读的就是它；
                     *    不放进 ⇒「校验未通过：输出目录是空目录，没有产物」⇒ 列表显示「解压失败」+ 源包不处理。
                     * ② **"这一组该在的那一层" / 产出它的那一单自己的落点** —— 交给
                     *    `AdoptUnresolvedVolumePieces`（接片那一档，唯一出口）去接；
                     *    只有它接不进去（那一层还认不出来 / 被占）时，才由
                     *    `PublishUnresolvedPieceLandingCopies` 在产出它的那一单自己的落点里补一份。
                     *
                     * ⛔ 老写法**在这里就先建一份落点的硬链接**，于是接片成功之后那一份成了没人认领的
                     * 多余名（真机 `111(2)\111(2)_\111.z01`，200 MiB 一直留在盘上 —— 用户第二次追问）。
                     * 顺序必须是"先接、接不进再补"，⛔ 不是"先补、接完不管"。
                     */
                    string target = Path.Combine(stage, name);

                    /*
                     * ⛔ 已经在那一层了就什么都不做（幂等）；目标被占（文件或目录）⇒ 一个字节都不动。
                     * ⚠ 但**"已经在暂存目录里"这一档仍然要进 [收片] 与 [记账]**：它是入口包，
                     * 后面那一站要靠这份账收卷（2026-10-06：抢救副本本来就落在暂存目录 ⇒ 走到这里就 continue
                     * ⇒ 收片那一档与"吐出这一片的那一单"那份账一起被跳过）。
                     */
                    if (File.Exists(target) || Directory.Exists(target) || SafePathHelper.PathEquals(piece, target))
                    {
                        if (File.Exists(target))
                        {
                            /*
                             * ⛔ 这里**不写日志**：真机那一批是**成功**任务，而"成功任务只留一行摘要"是既有红线
                             * （`LogVolumePolicyTests` / `ProgressVisibilityTests` / `Item45LogAndPasswordTests`
                             * 等 6 条守门用例钉着）。"落地那一份真的在"由**用例断言文件**，不靠日志。
                             */
                            RememberGroupPieceProducer(FileNameHelper.GetArchiveBaseName(target), FileNameHelper.GetFileName(target), owner);
                            published.Add(target);
                        }

                        continue;
                    }

                    FileOperations.Move(piece, target);

                    if (File.Exists(target))
                    {
                        AppendLog(
                            "INFO",
                            $"{owner.LogName}：这一步解出来的「{name}」留在它自己那条链里（下一轮解压要用的就是它；"
                            + "⛔ 不是失败、也不算残缺）。");

                        RememberGroupPieceProducer(FileNameHelper.GetArchiveBaseName(target), FileNameHelper.GetFileName(target), owner);
                        published.Add(target);
                    }
                }
                catch (Exception ex)
                {
                    // 加法：搬不动 ⇒ 退回"什么都不做"（这一片仍留在工作区，行为与改动前一个字不差）。
                    AppendLog("WARN", $"跨链收卷跳过（{ex.Message}）。");
                }
            }

            return published;
        }

        private void PublishUnresolvedPieceLandingCopies(IReadOnlyList<string>? pieces, ArchiveTask? owner)
        {
            if (owner == null || pieces == null || pieces.Count == 0)
            {
                return;
            }

            string landing = ResolveOwnerChainDirectory(owner);

            if (landing.Length == 0)
            {
                return;
            }

            foreach (string piece in pieces)
            {
                try
                {
                    if (!File.Exists(piece))
                    {
                        continue;
                    }

                    string name = FileNameHelper.GetFileName(piece);
                    string baseName = FileNameHelper.GetArchiveBaseName(name);

                    /*
                     * 这一组那一层已经有同一个名字 ⇒ 不再在产出它的那一单自己的落点里多留一份。
                     *
                     * ⚠ **手上有什么就交什么**（2026-10-11 实测）：这一档原来只交基名 ⇒ 那条路只能退回
                     * 当年那条"先到先得"兜底；兜底已按用户口径删除 ⇒ 它答不出层 ⇒ 这一道闸门**静默失效**
                     * ⇒ 产出方落点里多出一个**没人认领的名字**（实测残留逐字：
                     * `…\out-…\111(2)_\111.z01`，`SiblingFolderVolumeGatherTests.真机形状_过路层那一单的其余物按档删掉_48MB那处`
                     * 第 ③ 条断言「盘上收口：只剩成品本身」钉的就是它）。这一帧里有**产出这一片的这一单**（`owner`）
                     * 与**这一片自己**（`piece`）⇒ 交给那把唯一尺子（⛔ 不看谁先到）。
                     */
                    string groupDirectory = ResolveBatchGroupDirectory(baseName, owner, piece);

                    if (groupDirectory.Length > 0 && File.Exists(Path.Combine(groupDirectory, name)))
                    {
                        continue;
                    }

                    /*
                     * ⛔ **上一层已经有同名同字节的一份 ⇒ 不再在子目录里多铺一层**（2026-10-17 真机 `4444`）。
                     *
                     * <p>现场（盘面逐字）：`bbb(3)\…\bbb-W2-1\bbb-L2.jpg` 与
                     * `bbb(3)\…\bbb-W2-1\bbb-L2\bbb-L2.jpg` 是**同字节的两份**（10,489,515 B、SHA-256 相同），
                     * 后者就是这一档在"以片名命名的那一层"里补出来的；判据 ③ 把两份都算残留。</p>
                     *
                     * <p>判据只读**盘上的事实**（同名 + 同大小 + 同 SHA-256）⇒ 不铺也不丢内容。</p>
                     */
                    string? landingParent = Path.GetDirectoryName(landing.TrimEnd('\\', '/'));

                    if (!string.IsNullOrWhiteSpace(landingParent))
                    {
                        string sibling = Path.Combine(landingParent!, name);

                        if (File.Exists(sibling) && IsByteIdentical(sibling, piece))
                        {
                            AppendLog(
                                "INFO",
                                $"{name}：上一层已经有同名同内容的一份 —— 不再在「{Path.GetFileName(landing)}」里多铺一层。");

                            continue;
                        }
                    }

                    Directory.CreateDirectory(landing);

                    string landingPath = Path.Combine(landing, name);

                    if (!File.Exists(landingPath))
                    {
                        HardLinkHelper.TryCreateHardLink(landingPath, piece);
                    }
                }
                catch (Exception ex)
                {
                    // 判不出 / 建不出来 ⇒ 什么都不做（与改动前"落地那一份没建出来"同一档）。
                    AppendLog("WARN", $"{owner.LogName}：落地那一份没建出来（{ex.Message}）。");
                }
            }
        }

        private VolumeGroup? ResolveGatheredGroupFromEntryLayer(
            ArchiveTask task,
            string? entryLayer,
            string? memberPath = null)
        {
            if (task == null || string.IsNullOrWhiteSpace(entryLayer) || !Directory.Exists(entryLayer))
            {
                AppendLog("WARN", $"收卷判据输入：{(task == null ? "(没有这一单)" : task.LogName)}｜该拼到哪一层=「{entryLayer ?? string.Empty}」｜这一层不在盘上 ⇒ 判不出完整的一组");

                return null;
            }

            try
            {
                string tailName = ResolveGroupReferenceName(
                    task,
                    FileNameHelper.GetArchiveBaseName(ResolveObservedPiecePath(task, memberPath)),
                    memberPath);
                var candidates = new List<VolumeCandidate>();

                foreach (string path in CollectVolumeFilesInDirectory(entryLayer))
                {
                    if (LegacyVolumeNames.BelongsToSameGroup(FileNameHelper.GetFileName(path), tailName))
                    {
                        long size = -1;

                        try
                        {
                            size = new FileInfo(path).Length;
                        }
                        catch
                        {
                            // 量不出大小不影响归组。
                        }

                        candidates.Add(new VolumeCandidate { Path = path, Size = size });
                    }
                }

                var judged = new List<string>();

                foreach (VolumeGroup group in VolumeGroupDetector.Group(candidates))
                {
                    judged.Add(
                        $"{group.BaseName}：完整={group.IsComplete} 已找到={group.KnownVolumeCount} 缺卷={(group.MissingVolumeNames.Count > 0 ? string.Join("、", group.MissingVolumeNames) : "（空）")} 说明={group.Note}｜成员={string.Join("、", group.Volumes.Select(item => FileNameHelper.GetFileName(item.Path)))}");

                    if (group.IsComplete
                        && !string.IsNullOrWhiteSpace(group.FirstVolumePath)
                        && File.Exists(group.FirstVolumePath))
                    {
                        AppendLog("INFO", $"收卷判据输入：引用名=「{tailName}」｜这一层同族同基名的候选 {candidates.Count} 个｜⇒ 判出完整的一组：首卷=「{FileNameHelper.GetFileName(group.FirstVolumePath)}」共 {group.KnownVolumeCount} 卷");

                        return group;
                    }
                }
                AppendLog("WARN", $"收卷判据输入：引用名=「{tailName}」｜这一层同族同基名的候选 {candidates.Count} 个（{string.Join("、", candidates.Select(item => FileNameHelper.GetFileName(item.Path)))}）｜判定器结论：{(judged.Count > 0 ? string.Join(" ； ", judged) : "一个分卷组都没归出来")}｜⇒ 判不出完整的一组");
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"收卷判据输入：判的时候抛了异常 ⇒ 判不出完整的一组（{ex.GetType().Name}：{ex.Message}）");
            }

            return null;
        }

        /// <summary>
        /// **把这一单手上那几片按规范卷名接到入口包那一层**（用户 2026-10-06：「你把分卷移过来不就是了吗」）。
        ///
        /// <para>⛔ 判据与执行体都不在这里：整件事转调 <see cref="TryAdoptUnresolvedVolumePiece"/>
        /// （它自己会判"同一组吗 / 目标名占没占 / 同不同盘"，也只硬链接，⛔ 用户源文件一个字节不动）。
        /// 这一档只是把"手上那几片"再喂一遍 —— 因为批末那一刻"入口包那一层"才刚确定下来。</para>
        ///
        /// <para>⛔ 一个都收不动 / 判不出来 ⇒ 什么都不做（调用方照旧读盘上事实下结论）。</para>
        /// </summary>
        /// <summary>
        /// 批末那一站该拿**哪一份**去问"这一组齐了没"：停下来的那一片（如果记过）—— 否则就是这一单自己的路径。
        ///
        /// <para>⛔ 判据输入一个字不改：记过 ⇒ 那一片自己的路径（它的基名 = 那一组的基名）；
        /// 没记过 ⇒ <c>task.CurrentPath</c>（与改动前逐字相同）。</para>
        /// </summary>
        private string ResolveDeferredGroupMemberPath(ArchiveTask task) =>
            _deferredVolumeGroupMember.TryGetValue(task, out string? recorded) && !string.IsNullOrWhiteSpace(recorded)
                ? recorded!
                : task.CurrentPath ?? string.Empty;

        /// <summary>
        /// **这一片在产出方落点里那一份「落地副本」**（唯一出口；⛔ 只回答"盘上有没有这么一个名字"，
        /// 一个字节都不搬、不删）：与 <paramref name="piecePath"/> **同名、不是同一个路径**、
        /// 且就在**产出方落点根**（<see cref="ResolveOwnerChainDirectory"/>）底下。
        ///
        /// <para>为什么要问它：接片走"移动"那一档，动的是**工作区里那一份**；而定稿发布补出来的
        /// "落地那一份"还在产出方落点里 —— 那同样是"产出方那一份已经落地的片"，收场时得有人认领它
        /// （见 <see cref="RememberAdoptedLandedPiece"/>）。判不出 ⇒ 空串（调用方什么都不记）。</para>
        /// </summary>
        private string FindLandedCopyBesideProducer(string piecePath, ArchiveTask? owner)
        {
            ArchiveTask? producer = ResolveLandedPieceProducer(piecePath, owner);

            if (producer == null)
            {
                return string.Empty;
            }

            string landing = ResolveOwnerChainDirectory(producer);
            string name = FileNameHelper.GetFileName(piecePath);

            if (landing.Length == 0 || name.Length == 0)
            {
                return string.Empty;
            }

            string candidate = Path.Combine(landing, name);

            return File.Exists(candidate) && !SafePathHelper.PathEquals(candidate, piecePath)
                ? candidate
                : string.Empty;
        }

        /// <summary>
        /// **已由本批另一单解出的"借来用过的源片"**（如那三片散在源目录里的跨盘 ZIP 分卷）——
        /// 它们也算**源包**，跟着这一组一起进其余物、一起按删除档处理
        /// （用户 2026-10-05 拍板原话：「那肯定啊，这个算原包，最后放到其余物里面一同删除」）。
        ///
        /// <para>判据只有两件事：① 这一片在 <see cref="_consumedVolumeSources"/> 里、且消费方正是
        /// <paramref name="consumer"/> 这一单；② 这一片自己的任务**确实是**按"内容已由别单解出"收场的
        /// 跟班卷（<see cref="ArchiveTask.IsVolumeGroupFollower"/>、终态 <see cref="TaskOutcome.Skipped"/>）。
        /// 搬运与删除全部交给既有出口（<see cref="ExecuteSourcePackageMove"/> → `SourcePackageMover`，
        /// 六道闸门一条都不绕过；后面那一段"按删除档处理其余物"照旧跑）。
        /// ⛔ 判不出 / 找不到那一单 ⇒ 什么都不做（源片留在原地）。</para>
        /// </summary>
        private void CollectConsumedSourcePiecesIntoRest(ArchiveTask consumer)
        {
            if (consumer == null)
            {
                return;
            }

            var logEntries = new List<(string Level, string Message)>();

            SettleGroupPieceProducers(consumer, moveSources: true, logEntries);

            if (_consumedVolumeSources.Count == 0)
            {
                foreach ((string level, string message) in logEntries)
                {
                    AppendLog(level, message);
                }

                return;
            }

            foreach ((string piecePath, string ownerName) in _consumedVolumeSources.ToList())
            {
                if (!string.Equals(ownerName, consumer.FileName, StringComparison.Ordinal))
                {
                    continue;
                }

                ArchiveTask? pieceTask = _vm.Tasks.FirstOrDefault(
                    candidate => candidate != null &&
                                 candidate.IsVolumeGroupFollower &&
                                 candidate.Outcome == TaskOutcome.Skipped &&
                                 EnumerateTaskPaths(candidate).Any(
                                     path => string.Equals(
                                         SafePathHelper.GetFullPathSafe(path),
                                         SafePathHelper.GetFullPathSafe(piecePath),
                                         StringComparison.OrdinalIgnoreCase)));

                if (pieceTask == null)
                {
                    continue;
                }

                /*
                 * ⛔ 2026-10-15（用户 2026-10-14 口径）：这里原来是 `ExecuteSourcePackageMove(…, AfterChain, …)`
                 * —— 把这一片**搬进消费方的其余物**。源包那一档只剩「留下 / 彻底删除」之后，
                 * **搬进其余物这条路不存在了**：留下 = 一个字节都不动；彻底删除 = **就地删**。
                 * ⇒ 与别处**同一条闸门链**（唯一出口 `PurgeLayerSourcePackage`）。
                 *
                 * ⚠ 与另一处源片同样的已知缺口：这里手上没有"接手方那一单"的定稿 / 校验两份事实
                 * ⇒ 故意不喂 ⇒ 执行体判不出就**什么都不做**并如实写 WARN（⛔ 兜底落在"判不出 ⇒ 不动"）。
                 */
                SourceHandlingMode pieceHandling = RunOptions?.SourceHandling
                    ?? AppSettings.ParseSourceHandling(Settings?.SourceHandling);

                if (pieceHandling == SourceHandlingMode.DeleteInPlace)
                {
                    PurgeLayerSourcePackage(
                        pieceTask,
                        verification: null,
                        commit: null,
                        logEntries,
                        LayerPurgeTrigger.SourceHandlingDelete);
                }
                else
                {
                    logEntries.Add((
                        "INFO",
                        $"{pieceTask.LogName}：按「源包留下」这一档，这一片一个字节都不动（留在原处，随时可手工处理）。"));
                }

                _consumedVolumeSources.Remove(piecePath);
            }

            foreach ((string level, string message) in logEntries)
            {
                AppendLog(level, message);
            }
        }


        /// <summary>第二大步收卷那一档的措辞（候选 = 这一趟按组定下来的那几份卷）。</summary>
        private const string HarvestCandidatePrefix = "第二大步收卷准备收的";

        /// <summary>第二大步收卷那一档的结论措辞（与「逐层回收」那一档同一件事、不同入口）。</summary>
        private const string HarvestBlockerTail =
            "当场收掉这几份就等于把这一组拆开，所以这一趟一个字节都不收"
            + "（删除不可恢复；这一组留到确认完整之后再收）";

    }
}
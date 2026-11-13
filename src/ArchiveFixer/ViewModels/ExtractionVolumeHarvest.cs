using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
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
    /// <summary>
    /// 第二大步的轮边界/批量收卷（从 `ExtractionCoordinator.VolumeGatherStep.cs` 搬出）：
    /// 把这一轮解开过的那一组卷按删除档收掉、记住已解组的卷、按跟班卷收掉吐出片的单；
    /// ⛔ 删除仍只走既有出口，判据与日志文案沿用原实现。
    /// </summary>
    internal sealed class ExtractionVolumeHarvest
    {
        private readonly ExtractionCoordinator _host;

        internal ExtractionVolumeHarvest(ExtractionCoordinator host)
        {
            _host = host;
        }


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
        /// ⛔ **用户导入的原包**一个字节都不碰（红线出口 <see cref="_host.IsUserImportedSourcePath"/> ——
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
                    && !_host._volumeGroupEntriesThisBatch.Any(
                        item => string.Equals(item.Directory, directory, StringComparison.OrdinalIgnoreCase)
                                && LegacyVolumeNames.BelongsToSameGroup(
                                    item.ReferenceName,
                                    FileNameHelper.GetFileName(entryPath))))
                {
                    _host._volumeGroupEntriesThisBatch.Add((directory, FileNameHelper.GetFileName(entryPath)));
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
        internal void HarvestRememberedGroupVolumes()
        {
            var plans = new List<(ArchiveTask Consumer, List<string> Targets)>();

            foreach ((string Directory, string ReferenceName) group in _host._volumeGroupEntriesThisBatch.ToList())
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

                IReadOnlyList<string> roots = _host.EnumerateSplitGateArtifactRoots(consumer, targets);

                if (roots.Count == 0)
                {
                    // 与「逐层回收」那一档同一条兜底：判不出 ⇒ 什么都不做（删除不可恢复）。
                    _host.AppendLog(
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
                        ExtractionCoordinator.HarvestCandidatePrefix,
                        ExtractionCoordinator.HarvestBlockerTail,
                        requireRecognizedCandidates: true,
                        others);

                    if (blocker != null)
                    {
                        break;
                    }
                }

                if (blocker != null)
                {
                    _host.AppendLog("WARN", $"{consumer.LogName}：{blocker}");
                    continue;
                }

                _host.DeleteHarvestTargets(consumer, targets);
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
        internal (ArchiveTask Consumer, List<string> Targets)? PlanHarvestOneSolvedGroupVolumes(
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
                _host.AppendLog(
                    "WARN",
                    $"第二大步收卷：跳过（判据输入）｜那一层=「{directory}」，在盘上={Directory.Exists(directory)}");
                return null;
            }

            // 这一组此刻在那**一层**里的那几份（当场按族尺子重新认，⛔ 不用记下来的旧路径）。
            var members = new List<string>();

            foreach (string member in ExtractionCoordinator.CollectVolumeFilesInDirectory(directory))
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
                ArchiveTask? owner = _host.FindTaskByPath(member);

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
                _host.AppendLog(
                    "WARN",
                    $"第二大步收卷：跳过（判据输入）｜那一层=「{directory}」，这一组的卷 {members.Count} 份，"
                    + "但那一层里没有哪一单是「已解开 + 校验通过」⇒ 判不出 ⇒ 一个字节都不动");
                return null;
            }

            string mode = RestHandlingModes.Normalize(_host.RestHandlingThisBatch);

            if (string.Equals(mode, RestHandlingModes.Keep, StringComparison.OrdinalIgnoreCase)
                || _host.IsStopping
                || _host._operationCts?.IsCancellationRequested == true
                || !ResultCompletenessClassifier.Classify(consumer).AllowsSourceRemoval)
            {
                _host.AppendLog(
                    "WARN",
                    $"第二大步收卷：跳过（判据输入）｜那一层=「{directory}」，删除档=「{mode}」，"
                    + $"已取消={_host.IsStopping || _host._operationCts?.IsCancellationRequested == true}，"
                    + $"可证完整={ResultCompletenessClassifier.Classify(consumer).AllowsSourceRemoval}");
                return null;
            }

            string entryName = referenceName;

            if (entryName.Length == 0)
            {
                return null;
            }

            ContentKeepRules keepRules = ContentKeepRules.FromSettings(_host.Settings);
            var targets = new List<string>();

            foreach (string path in ExtractionCoordinator.CollectVolumeFilesInDirectory(directory))
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

                if (_host.IsUserImportedSourcePath(path))
                {
                    // 「判不出 / 跳过」必须把判据输入写进日志：这一档原来全静默，正是它把 ③ 藏了两轮。
                    _host.AppendLog(
                        "WARN",
                        $"{consumer.LogName}：这一组的卷 {name} 是「用户导入的原包」自己（不是这一批的产出）"
                        + " —— 一个字节都不碰，留在原处。");
                    continue;
                }

                string? keyword = keepRules.FindMatch(path);
                bool protectedContainer = ArchiveFixer.Security.ContentContainerProtection.ShouldProtect(path);

                if (keyword != null || protectedContainer)
                {
                    string reason = keyword != null
                        ? $"命中了「内容物保留关键词」（{keyword}）"
                        : $"是内容容器（{ArchiveFixer.Security.ContentContainerProtection.Describe(path)}）";

                    _host.AppendLog(
                        "INFO",
                        $"{consumer.LogName}：这一组的卷 {name} {reason}"
                        + " —— 默认碰都不碰，这一份留在原处。");
                    continue;
                }

                targets.Add(path);
            }

            return targets.Count == 0 ? null : (consumer, targets);
        }


        internal void RememberGroupPieceProducer(string baseName, string pieceName, ArchiveTask? owner)
        {
            if (owner == null || baseName.Length == 0)
            {
                return;
            }

            // 并发写：见 `_host._groupPieceProducersGate` 的说明（这里就是那个栈顶）。
            lock (_host._groupPieceProducersGate)
            {
                if (!_host._groupPieceProducers.TryGetValue(baseName, out List<(ArchiveTask Task, string PieceName)>? producers))
                {
                    producers = new List<(ArchiveTask Task, string PieceName)>();
                    _host._groupPieceProducers[baseName] = producers;
                }

                if (!producers.Any(item => ReferenceEquals(item.Task, owner)))
                {
                    producers.Add((owner, pieceName));
                }
            }
        }
    }
}


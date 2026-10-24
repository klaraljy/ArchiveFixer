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
        /// <item><description>那一片**此刻还在盘上**、名字仍属于这一组（<see cref="VolumeGroupDetector.TryGetVolumeIndex"/>
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
                                && VolumeGroupDetector.BelongsToSameGroup(
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
                    && VolumeGroupDetector.BelongsToSameGroup(FileNameHelper.GetFileName(member), referenceName))
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
                if (!VolumeGroupDetector.BelongsToSameGroup(name, entryName))
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

        private void PurgeGroupLandedPieces(ArchiveTask consumer, string groupBaseName)
        {
            // 第二大步停摆中（用户 2026-10-24）：第一大步不许收片，一个字节都不动。
            if (ArchiveFixer.Extraction.SecondStepResidueState.Parked)
            {
                return;
            }

            PurgeRedundantLandedAliases(groupBaseName);

            var targets = new List<(ArchiveTask Producer, string Path, string OutputRoot)>();

            foreach ((ArchiveTask Producer, string GroupBaseName, string PiecePath, string OutputRoot) item
                     in _adoptedLandedPieces)
            {
                if (string.Equals(item.GroupBaseName, groupBaseName, StringComparison.OrdinalIgnoreCase))
                {
                    targets.Add((item.Producer, item.PiecePath, item.OutputRoot));
                }
            }

            /*
             * ===== 第二大步"这一组的卷"里、凡是**续解产出的**也要收（2026-10-11，用户口径：分卷记账是第二大步的事）=====
             *
             * 现场（真机 EEE，日志逐字）：第二大步把那一组解开、`解压成功 ｜ 校验通过 ｜ 续解`，可
             * `这一层的过程物没有回收（回收判据没放行）` —— 拦住它的是"过路层其余物"那道闸门：那个其余物目录里
             * **混着别的单的源包**（`111(2)_.zip` / `111(3).rar`）⇒ 拒绝删整目录 ⇒ 这一层的过程物回收整段没放行；
             * 链尾那条路又写着 `续解出来的内层包不参与源包处理 —— 源包一个字节都不搬` ⇒ 那两对卷没人收。
             *
             * ⚠ **判据不能挂在"收场的 consumer 是续解任务"上**（第一版就这么写的，实测没生效）：
             * 这一组收场时的 consumer 是**第一大步那一单**（真机 `111.z03`，`IsContinuationTask=false`），
             * 而"卷是续解产出的"这件事在**别的单**身上（`111.part1.rar`）。
             * ⇒ 扫**全表**：凡是**续解任务**（既有事实位 `IsContinuationTask`，项目自己的定义逐字：
             * 「续解任务的"源文件"是上一层交出来的内层包 —— 对用户来说它是过程物」）**且名字仍属于这一组**
             * （族 + 基名，既有宽尺子）的卷，都收进来。
             * ⛔ 用户最初导入的包绝不进这一档（它们 `IsContinuationTask == false`）；⛔ 六道门槛一条不绕过。
             */
            string volumeGroupReference = targets.Count > 0
                ? FileNameHelper.GetFileName(targets[0].Path)
                : FileNameHelper.GetFileName(consumer.CurrentPath ?? string.Empty);

            foreach (ArchiveTask derived in SnapshotTaskTable(Tasks))
            {
                if (derived == null || !derived.IsContinuationTask)
                {
                    continue;
                }

                string derivedRoot = ResolveOwnerChainDirectory(derived);

                if (derivedRoot.Length == 0)
                {
                    continue;
                }

                foreach (string volumePath in derived.VolumePaths.Concat(new[] { derived.CurrentPath ?? string.Empty }))
                {
                    if (string.IsNullOrWhiteSpace(volumePath) || !File.Exists(volumePath))
                    {
                        continue;
                    }

                    if (volumeGroupReference.Length > 0
                        && !VolumeGroupDetector.BelongsToSameGroup(
                            FileNameHelper.GetFileName(volumePath),
                            volumeGroupReference))
                    {
                        // 别的族 / 别的基名 ⇒ 判不出"它属于这一组" ⇒ 一个字节都不动。
                        continue;
                    }

                    if (!targets.Any(item => SafePathHelper.PathEquals(item.Path, volumePath)))
                    {
                        targets.Add((derived, volumePath, derivedRoot));
                    }
                }
            }

            if (targets.Count == 0)
            {
                // 这一组没有"已经落地的片"被接走过（真机 `111.z01` 那一档就是：接片走的是移动）
                // ⇒ 与改动前逐字相同，一个字节都不动。
                return;
            }

            string mode = RestHandlingModes.Normalize(RestHandlingThisBatch);

            /*
             * ===== 「其余物 = 不处理」这一档：**不删，但要集中进其余物** =====
             *
             * <para><b>用户口径（2026-10-13 拍板，选 A）</b>：「不动其余物」的"不动" = **不删用户的东西**；
             * 而**程序自己产出、且已被整组接走**的那份多余副本**允许挪位置**（搬进同层的 `其余物\`，
             * ⛔ 一个字节都不删、同一棵树、不跨盘）。问答与现场见 `大测试日志-困难\发现.md`。</para>
             *
             * <para><b>现场</b>（`run-22222222` / `run-25252525`，都是这一档）：老写法在这里**直接 return**
             * （"一个字节都不删"），于是那份副本没人管：套娃包那一单的成品目录里留着
             * `资源222-W0-0\资源222-L2.zip.0000000001`（同一份字节在组那一层已经有名字了）
             * ⇒ 判据 ③「盘上无残留」红（残留 = 跑完之后成品树里新出现、又不在 `其余物\`/`过程物\` 里的东西）。</para>
             *
             * <para>修法 = **同一条闸门链、动作参数化**（与 `PurgeLayerSourcePackage` 同一范式）：
             * 判据（在盘上 / 是分卷片 / 参与分卷那一套 / 族+基名对上 / 在落点根内 / 不是任何一单的源路径 /
             * 没命中内容物保留关键词）一条不动，只把"删除"换成"搬进产出方落点根下的其余物"
             * （唯一出口 <see cref="TryMoveIntoRest"/>）。实测：`run-22222222` ①–⑤ 全绿、整批 8 → 9 绿。</para>
             */
            bool keepMode = string.Equals(mode, RestHandlingModes.Keep, StringComparison.OrdinalIgnoreCase);

            bool cancelled = IsStopping || _operationCts?.IsCancellationRequested == true;

            if (cancelled)
            {
                return;
            }

            /*
             * 判据与其余物那一档的第一道门槛**同一把尺子**（`RestItemPurger.Purge` 那条）：
             * 动手之前先问**机器终态** —— ⛔ 不比任何中文文案。少了这一条，`CollectConsumedSourcePiecesIntoRest`
             * 那条路（它不带"消费方必须成功"的前置）就可能凭"完整性证据"放行一次删除。
             */
            if (consumer.Outcome != TaskOutcome.Succeeded)
            {
                return;
            }

            ResultCompletenessVerdict evidence = ResultCompletenessClassifier.Classify(consumer);

            if (!evidence.AllowsSourceRemoval)
            {
                return;
            }

            // ⚠ 2026-10-15：回收站档整条下线 ⇒ 只剩永久删（Keep 档在调用方就返回了）。
            DeleteMode deleteMode = DeleteMode.Permanent;

            string reason = RestItemPurger.SettledPieceCopyPurgeReason;

            ContentKeepRules keepRules = ContentKeepRules.FromSettings(Settings);
            var service = new RecycleBinService(RestDeleteExecutor, null, null);

            /*
             * ⚠ **账上那一条路径不是全部**（2026-10-11 真机 FFFF 实测，调试器定案）：
             *
             * 定稿会把同一片再搬进"最里层包名目录"（`<落点>\<内层包基名>\`）——
             * 那一份与账上记的那一条是**两个不同的路径**。实测：断点 `ResultFinalizer.cs:450` 读到
             * `items=1 chain=0 inPlace=false unopened=false inner=111__ dest=…\in\111__\111__` ⇒
             * 规划器按"最里层 = 内层包基名命名的目录"照建 ⇒ 盘上多出
             * `…\111__\111__\111__\111_.par删t1.ra除r`（209 MB，**脏名**）—— 账上没有第二条路径
             * ⇒ 谁都不认领它 ⇒ 它留到下一轮被当成新原包（"凭空多套一层"就是这么来的）。
             *
             * ⇒ 目标 = 账上那一条 **+ 产出方落点根里同族同基名的每一个名字**。
             * ⛔ 只在**已经过了上面全部闸门**之后才扫（判据一个字不放宽）；
             * ⛔ 只扫**那一个落点根**（不扫全盘、不扫别的单）；⛔ 任何一单的源路径一律不碰（红线唯一出口
             * `IsAnyTaskSourcePath`）；⛔ 名字必须仍属于这一组（族 + 基名，既有那把尺子）。
             */
            var scannedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /*
             * 「其余物 = 不处理」档搬进其余物时要用的让位账（跨这一趟扫到的所有产出方共用一份）：
             * 两条同名副本要算出**不同**的目标名，只问文件系统会撞（与源包搬运同一口径）。
             */
            var movedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /*
             * 参照名 = **账上那一条的第一个**（那一条按定义就是这一组的一片）；
             * 一条账都没有 ⇒ 上面已经早退过，走不到这里 ⇒ 判不出时用空串（`BelongsToSameGroup` 恒 false ⇒ 什么都不删）。
             */
            string groupReferenceName = targets.Count > 0
                ? FileNameHelper.GetFileName(targets[0].Path)
                : string.Empty;

            /*
             * ⚠ 2026-10-13 困难批 `11111111`（运行期实测，断点读的变量）：
             * **接片建出来的那一份副本没人认领** —— 三片都躺在"这一组该拼到的那一层"
             * （`…\222-W0-1\222-W0-0\`）里，而 `_adoptedLandedPieces` 记的是**产出方落点里那一份**
             * （part2 / part3 各一条，producer 是 W1 / W2 那两条链）⇒ 收场只扫了 W1 / W2 两棵树，
             * **入口包那一层一次都没扫到**（断点实测：`groupBaseName=222-L2`、`targets.Count=2`、
             * 参照名 `222-L2.part2.rar`）⇒ 那三份留在成品树里 ⇒ 判据 ③「盘上无残留」红。
             *
             * <para>⇒ 扫描根里补上**接手这一组那一单自己那一层**：它在"这一组齐了 ⇒ 照常跑这一组"
             * 那一刻已经被改到第 1 卷（`task.CurrentPath = firstVolume`），所以它的目录按定义就是
             * "这一组该拼到的那一层"。⛔ 逐文件那几道闸门一条不绕过（下面同一个循环体）。</para>
             */
            var rootsToScan = new List<(ArchiveTask Producer, string OutputRoot)>();

            foreach ((ArchiveTask producer, string _, string outputRoot) in targets.ToList())
            {
                if (outputRoot.Length > 0)
                {
                    rootsToScan.Add((producer, outputRoot));
                }
            }

            string consumerLayer = Path.GetDirectoryName(
                SafePathHelper.GetFullPathSafe(consumer.CurrentPath ?? string.Empty)) ?? string.Empty;

            if (consumerLayer.Length > 0)
            {
                rootsToScan.Add((consumer, consumerLayer));
            }

            /*
             * ⚠ 2026-10-13（同上，断点实测）：**"这一组拼到哪一层"那份账就是那几份副本所在的那一层**
             * （写点唯一 = 接片真的把这一片接进去的那一刻）。产出方落点里那一份已经由上面 targets 覆盖，
             * 而**接片建出来的那一份**只有这份账知道它在哪 ⇒ 必须把它当扫描根，否则那几份永远没人认领。
             */
            if (_groupLayerByBaseName.TryGetValue(groupBaseName, out string? assembledLayer)
                && !string.IsNullOrWhiteSpace(assembledLayer))
            {
                rootsToScan.Add((consumer, assembledLayer!));
            }

            foreach ((ArchiveTask producer, string outputRoot) in rootsToScan)
            {
                if (outputRoot.Length == 0 || !scannedRoots.Add(outputRoot))
                {
                    continue;
                }

                string[] found;

                try
                {
                    found = Directory.GetFiles(outputRoot, "*", SearchOption.AllDirectories);
                }
                catch
                {
                    // 那棵树读不动 ⇒ 判不出 ⇒ 什么都不做（与改动前逐字相同）。
                    continue;
                }

                foreach (string path in found)
                {
                    string foundName = FileNameHelper.GetFileName(path);

                    /*
                     * ⛔⛔ **必须问"族 + 基名"，⛔ 不许只比基名**（2026-10-11 真机 EEE：只比基名**删错了东西**）。
                     *
                     * 现场：那一组是**跨盘 ZIP 族**（`111.z01/z02/z03/111.zip`），而这一组解开之后
                     * 落在同一个落点根里的 `111.parts1.racr` / `111.part删2.ra除r` 是**下一层的 RAR 族**、
                     * **恰好同基名**（都叫 `111`）—— 只比基名就把它们当"这一组的多余名"删了。
                     * 取证逐字：第 1 遍跑完 `判据④：把输出目录再导入一次 ⇒ 导入后任务数 = 0`、
                     * `第 1 遍结束后的盘上清单` 空 —— **用户要的内容物被删干净了**（两条红线一起踩）。
                     *
                     * ⇒ 参照名取**账上那一条**（它按定义就是这一组的一片，`_adoptedLandedPieces` 记的），
                     * 用既有那把"宽尺子" `BelongsToSameGroup` 问"族 + 基名都对得上吗"。
                     * 判不出 / 对不上 ⇒ 一个字节都不动。
                     *
                     * ⛔ 再加一道底层识别（同一张卡）：内容**可证"不是分卷组的一员"** ⇒ 它不是"这一组的多余名"，
                     * 一个字节都不动（⛔ 判不出照旧按名字 —— 跨盘 ZIP 的中间片就在那一档）。
                     */
                    /*
                     * ⛔ **已经在 `其余物` 里的不再当候选**（2026-10-17 真机 `6666`，日志 `:231/:255/:269/:283/:285`）。
                     *
                     * <p>现场逐字：同一片在同一秒里被"集中进其余物"搬了 **5 次**，名字长成
                     * `pack111-L2.7z(1)(1)(1)(1)(1).003` —— 因为扫描是
                     * `Directory.GetFiles(outputRoot, "*", SearchOption.AllDirectories)`（**递归**），
                     * 把上一趟刚搬进 `其余物\` 的那一份又当候选搬了一遍，每趟撞名就按
                     * `stem(index)extension` 再插一个 `(1)`。</p>
                     *
                     * <p>其余物就是这些副本的终点 ⇒ 已经在那儿的，一个字节都不再动。</p>
                     */
                    if (IsInsideProcessArtifactDirectory(path))
                    {
                        continue;
                    }

                    FileNatureCard nature = FileNature.Inspect(path);

                    if (VolumeGroupDetector.TryGetVolumeIndex(foundName) == null
                        || !nature.ParticipatesInVolumeHandling
                        || !VolumeGroupDetector.BelongsToSameGroup(foundName, groupReferenceName)
                        || IsAnyTaskSourcePath(path))
                    {
                        continue;
                    }

                    if (!targets.Any(item => SafePathHelper.PathEquals(item.Path, path)))
                    {
                        targets.Add((producer, path, outputRoot));
                    }
                }
            }

            /*
             * ⚠ 2026-10-13（`1111` ③ 的最后一环）：**我亲手链接过的那几份来源**也算这一组的目标 ——
             * 它们的名字读不出卷号（`111-L2.7z` / `111-L2.7z-924`）⇒ 下面那些按名字的筛子一个都认不出它们，
             * 而"我从哪一棵树里把它链接成槽位名"这件事记在 <see cref="_byteBudgetLinkedSources"/> 里
             * （来源只在各单的产出树里扫，⛔ 不碰用户源目录）⇒ 只对**这些**记录放行，⛔ 别的文件一个字不改。
             */
            var ledgerProvenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach ((ArchiveTask Producer, string GroupBaseName, string SourcePath) item in _byteBudgetLinkedSources)
            {
                if (string.Equals(item.GroupBaseName, groupBaseName, StringComparison.OrdinalIgnoreCase))
                {
                    ledgerProvenSources.Add(SafePathHelper.GetFullPathSafe(item.SourcePath));
                }
            }

            /*
             * 试开定序那一档用的是**全新硬链接暂存目录**，产出方那一份（如 `333-W1-0\333-L2.pdf`）
             * 不在 `_byteBudgetLinkedSources` 里，而在 `_adoptedLandedPieces` 那本账里
             * （暂存时用 `RememberAdoptedLandedPiece` 记的）⇒ 同一组基名的也放行，否则名字读不出卷号的
             * 产出片会被这道筛子挡掉、变成 c3 残留。
             */
            foreach (var item in _adoptedLandedPieces)
            {
                if (string.Equals(item.GroupBaseName, groupBaseName, StringComparison.OrdinalIgnoreCase))
                {
                    ledgerProvenSources.Add(SafePathHelper.GetFullPathSafe(item.PiecePath));
                }
            }

            /*
             * 本次**已经成功集中进其余物**的物理身份（卷序列号:文件索引）。
             *
             * <p>2026-10-18 困难批 `8888` 现场：`打包111-L2.part1.rar` 与
             * `打包111-L2.part1删删删删删删删.rar` 是**同一个 File ID**（接片建的是零字节硬链接），
             * 而这一档按**名字**逐个搬 ⇒ 一份文件的两条名字都被搬进其余物，用户看到"识别到了、也改对了，
             * 为什么还并排放着两个名字"。⇒ 同一份物理文件只允许进一次其余物，第二条名字只收掉
             * （硬链接数 &gt; 1 ⇒ 一个字节都不丢）。</p>
             */
            var restMoveIdentities = new HashSet<string>(StringComparer.Ordinal);

            foreach ((ArchiveTask producer, string path, string outputRoot) in targets)
            {
                try
                {
                    /*
                     * ⚠ **判据输入**（2026-10-11，EEE ③ 定位用）：这一档的逐目标筛子原来**全是静默 `continue`**
                     * —— 拿一份日志分不出"这个目标压根没被收进来"还是"收进来了、被哪一道筛子丢了"。
                     * 按项目那条规矩（判不出就把判据输入写进日志），在**丢掉**时补一行 WARN。
                     * ⛔ 三条判据一个字没改，只是把三次 `continue` 合成一次并写出各自的取值。
                     */
                    string name = FileNameHelper.GetFileName(path);
                    bool exists = File.Exists(path);
                    bool isVolumePart = VolumeGroupDetector.TryGetVolumeIndex(name) != null;

                    /*
                     * ⛔ **底层识别再加一道**（同一张卡）：内容**可证"不是分卷组的一员"** ⇒ 它不是"这一组的多余名"，
                     * 一个字节都不动（⛔ 判不出照旧按名字 —— 跨盘 ZIP 的中间片就在那一档）。
                     */
                    bool participates = FileNature.Inspect(path).ParticipatesInVolumeHandling;
                    bool baseMatches = string.Equals(
                        FileNameHelper.GetArchiveBaseName(name),
                        groupBaseName,
                        StringComparison.OrdinalIgnoreCase);
                    bool insideRoot = ArchivePathGuard.IsInsideRoot(outputRoot, path, out _);
                    bool ledgerProven = ledgerProvenSources.Contains(SafePathHelper.GetFullPathSafe(path));

                    if (!exists
                        || !insideRoot
                        || (!ledgerProven && (!isVolumePart || !participates || !baseMatches)))
                    {
                        AppendLog(
                            "WARN",
                            $"收产出方那一份片：这一次跳过（判据输入）｜目标=「{path}」，在盘上={exists}，"
                            + $"是分卷片={isVolumePart}，参与分卷那一套={participates}，基名对上「{groupBaseName}」={baseMatches}，"
                            + $"在落点根「{outputRoot}」内={insideRoot}，我亲手链接过={ledgerProven}");
                        continue;
                    }

                    string? keyword = keepRules.FindMatch(path);

                    if (keyword != null)
                    {
                        AppendLog(
                            "INFO",
                            $"{producer.LogName}：产出方这一份 {name} 命中了「内容物保留关键词」（{keyword}）"
                            + " —— 按设置碰都不碰，这一份留在原处。");
                        continue;
                    }

                    /*
                     * 「其余物 = 不处理」档：**搬进产出方落点根下的其余物**（⛔ 不删、不进回收站）。
                     * 动作参数化、闸门链一条不绕过 —— 上面那串判据刚刚全部过完。
                     */
                    if (keepMode)
                    {
                        string aliasIdentity = FileIdentity.TryGetIdentity(path, out ulong aliasVolume, out ulong aliasIndex)
                            ? aliasVolume.ToString(System.Globalization.CultureInfo.InvariantCulture)
                              + ":"
                              + aliasIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            : string.Empty;

                        /*
                         * ⛔ 同一份物理文件的第二条名字：另一条已经进其余物了 ⇒ **只收掉这个多余的名字**
                         * （2026-10-18，`8888` 现场；见 `restMoveIdentities` 的说明）。
                         * 硬链接数 &gt; 1 是**删名字不删字节**的证据；判不出 / 只有一个名字 ⇒ 照旧搬。
                         */
                        if (aliasIdentity.Length > 0 && restMoveIdentities.Contains(aliasIdentity))
                        {
                            if (FileIdentity.TryGetLinkCount(path, out uint aliasLinks) && aliasLinks > 1)
                            {
                                try
                                {
                                    File.Delete(path);

                                    AppendLog(
                                        "INFO",
                                        $"{producer.LogName}：产出方这里那一份 {name} 与已经进其余物的那一份"
                                        + "是同一个文件 —— 收掉这个多余的名字，一个字节都不丢。");
                                }
                                catch (Exception ex)
                                {
                                    AppendLog(
                                        "WARN",
                                        $"{producer.LogName}：多余的名字 {name} 没能收掉（{ex.Message}）。");
                                }
                            }

                            continue;
                        }

                        string restTarget = Path.Combine(
                            outputRoot,
                            ProcessArtifactLayout.ArtifactDirectoryName,
                            name);

                        if (TryMoveIntoRest(path, restTarget, movedTargets, out string movedTo, out string? moveFailure))
                        {
                            if (aliasIdentity.Length > 0)
                            {
                                restMoveIdentities.Add(aliasIdentity);
                            }

                            AppendLog(
                                "INFO",
                                $"{producer.LogName}：这一片已经随整组解开，产出方这里那一份 {name} "
                                + "按「其余物 = 不处理」集中进其余物了（⛔ 一个字节都没删）—— "
                                + movedTo);
                        }
                        else
                        {
                            AppendLog(
                                "WARN",
                                $"{producer.LogName}：产出方这里那一份 {name} 没能集中进其余物 —— {moveFailure}"
                                + "（这一份仍在原处，内容物不受影响）。");
                        }

                        continue;
                    }

                    DeleteResult result = service.Delete(
                        new DeleteRequest(path, reason),
                        new DeleteOptions
                        {
                            AllowedRoot = outputRoot,
                            UserConfirmed = true,
                            Mode = deleteMode,
                            Reason = reason
                        });

                    DeleteOutcome? outcome = result.Outcomes.FirstOrDefault();

                    if (result.SuccessCount > 0)
                    {
                        AppendLog(
                            "INFO",
                            deleteMode == DeleteMode.RecycleBin
                                ? $"{producer.LogName}：这一片已经随整组解开，产出方这里那一份 {name} 按「删除操作」移入回收站了"
                                  + "（与 `111.z01` 那一档同一个收场口径）。"
                                : $"{producer.LogName}：这一片已经随整组解开，产出方这里那一份 {name} 按「删除操作」彻底删除"
                                  + $"（{outcome?.EntryCount ?? 0} 个条目 / 释放 {TaskSpaceEstimate.FormatSize(outcome?.FreedBytes ?? 0)}，不进回收站）。");
                    }
                    else
                    {
                        AppendLog(
                            "WARN",
                            $"{producer.LogName}：产出方这里那一份 {name} 这次没删成 —— {outcome?.Message ?? result.Message}"
                            + "（内容物不受影响，这一份仍在原处）。");
                    }
                }
                catch (Exception ex)
                {
                    // 删东西绝不许把已经成立的结论拖成异常：落成"没删成"，别的什么都不变。
                    AppendLog("WARN", $"{producer.LogName}：收产出方那一份片时出现意外错误：{ex.Message}");
                }
            }
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
            PurgeGroupLandedPieces(consumer, groupBaseName);

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
        private bool TryAdoptUnresolvedVolumePiece(
            string piecePath,
            ArchiveTask? owner,
            string? layerOverride = null,
            string? groupEntryPathOverride = null,
            bool allowMoveIntoGroup = true,
            IReadOnlyDictionary<string, int>? contentSlots = null,
            VolumeNamingFamily? namingFamily = null,
            IReadOnlyDictionary<int, string>? slotCanonicalTargets = null)
        {
            // 第二大步停摆中（用户 2026-10-24）：第一大步不许接片/建链接/改名。
            if (ArchiveFixer.Extraction.SecondStepResidueState.Parked)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(piecePath) || !File.Exists(piecePath))
            {
                return false;
            }

            /*
             * ===== ⛔ 内容证据优先：**可证"不是分卷组的一员" ⇒ 这一档一个字都不接** =====
             *
             * <para>用户 2026-10-11 口径：「判断一个文件，**绝对不能只看基名和后缀**，绝对还有其他的办法」
             * +「统筹到同一组上面去…我们就是先看看**底层能够识别到文件是什么东西**」。
             * 判据**不再写在这里** —— 转调底层那一个出口 <see cref="FileNature.Inspect"/> 那张卡上的
             * <see cref="FileNatureCard.ParticipatesInVolumeHandling"/>（内容说"不是分卷成员" ⇒ 不参与这一套动作）。</para>
             *
             * <para>为什么按名字判会出事：**一个归档本体自己就是它那一族的第 1 卷**
             * （`VolumeGroupDetector.TryGetVolumeIndex("111.rar")` = 1）⇒ 用户那份普普通通的单卷 `111.rar`
             * 也会被判成"像分卷片"（第 125 轮真凶就是这么把它接进别组的）。</para>
             *
             * <para><b>真样本实测（`DebugRunner volinfo` 档，只读）</b>：EEE `111\111.rar`（用户的原包）
             * ⇒ `NotVolumeMember`；EEE `111(4)\111.z11111111110删除3`（跨盘 ZIP 中间片，没有 EOCD）
             * ⇒ `NotAnArchive`（**判不出**）；FFFF `1114__\111_.part4.raAAAAAr`（真 RAR 分卷的一片）
             * ⇒ `IsVolumeMember = True`。⛔ 所以这里只对**可证不是**的那一档动作，判不出 ⇒ 照旧按名字。</para>
             */
            FileNatureCard nature = FileNature.Inspect(piecePath);

            if (!nature.ParticipatesInVolumeHandling)
            {
                AppendLog(
                    "WARN",
                    $"{FileNameHelper.GetFileName(piecePath)}：不接进这一组 —— 底层识别：{nature.Describe()}");

                CriterionLog(
                    "接片：这一片参不参与分卷那一套",
                    nature.Describe(),
                    "不参与（内容可证不是分卷组的一员）",
                    "不接、不改名、不搬、不建链接",
                    piecePath,
                    nature.HasIdentity
                        ? nature.VolumeSerial.ToString("X8", CultureInfo.InvariantCulture) + ":" + nature.FileIndex.ToString("X", CultureInfo.InvariantCulture)
                        : string.Empty,
                    "WARN");

                return false;
            }

            string baseName = FileNameHelper.GetArchiveBaseName(piecePath);

            if (baseName.Length == 0)
            {
                LogAdoptCriterionInputs(piecePath, StatusText.VolumeRepairNotAVolumeName, string.Empty, string.Empty, string.Empty);
                return false;
            }

            /*
             * ⚠ 2026-10-16：目标层原来在下面才算，可**这一组第 1 卷卡**（RAR4 锚定基数要用它）得先拿到 ——
             * 而第 1 卷就在"这一片要落的那一层"里（框架的口径：收片层 = 入口层 = 落点层）。
             * ⇒ 这里提前算一次，下面那一段原样复用（⛔ 只算一次，不重算）。
             */
            string targetDirEarly = string.IsNullOrWhiteSpace(layerOverride)
                ? ResolveBatchGroupDirectory(baseName, null, piecePath)
                : layerOverride!;

            /*
             * 规范卷名 = **这一片按它自己那一族**写成的名字（唯一出口 `VolumeGroupResolver.TryGetCanonicalVolumeName`）。
             *
             * ⛔ 这里原来写死成"跨盘 ZIP 的盘名"：`盘序 = 卷号 - 1` ⇒ 盘序 0 给 `.zip`、其余给 `.z0N`。
             * RAR 新式分卷的片一进这条路就被改成 `111_.z01` / `111_.z02` …（真机 FFFF 与守门夹具的日志里
             * 逐字可见「已按规范卷名 111_.z01 接到…」）—— **族被改掉了**：入口那一单还在找
             * `111_.part1.rar`，盘上却只有 `111_.z01` ⇒ 整组永远凑不齐 ⇒ 四片各自去解 ⇒ 全批没一个成功。
             * 判不出族 / 卷号 ⇒ 什么都不做（与老口径那两条早退逐字一致）。
             */
            /*
             * ===== 内容定的槽位 + 内容定的命名族：**名字一个字节都不参与命名** =====
             * （2026-10-18 困难批 `19191919` 现场；见 `TryGatherGroupPiecesInto` 里 `contentSlots` 的说明）
             */
            int? contentSlotForNaming = null;

            if (contentSlots != null
                && namingFamily.HasValue
                && contentSlots.TryGetValue(VolumeSlotKey(piecePath), out int slotByContent)
                && slotByContent >= 1)
            {
                contentSlotForNaming = slotByContent;
            }

            if (contentSlotForNaming == null
                && contentSlots != null
                && contentSlots.Count > 0
                && namingFamily.HasValue)
            {
                AppendLog(
                    "WARN",
                    $"{FileNameHelper.GetFileName(piecePath)}：内容槽位没命中（身份={VolumeSlotKey(piecePath)}"
                    + $"｜槽位键={string.Join("、", contentSlots.Keys)}）");
            }

            string canonical = string.Empty;

            bool nameGaveCanonical = contentSlotForNaming == null
                && VolumeGroupResolver.TryGetCanonicalVolumeName(Path.GetFileName(piecePath), out canonical);

            /*
             * ⚠ 2026-10-16（第 111 轮，困难批 `24242424` 运行期实测）：**名字出口给得出答案也不算数**的那一格。
             *
             * <para>现场逐字：这一片叫 `111-L2.part020745382.rar`（伪装法把卷标记那一截并进了基名）⇒
             * 名字出口**回它自己**（`TryGetFirstVolumeName` 也回它自己：那把尺子压根没认出卷标记、
             * 按"本体"读成第 1 卷）⇒ 下面那条"问卡"的路**一次都没进过** ⇒ 它被按自己的名字落进
             * 「111」这一层，整组永远收不齐 ⇒ 判据 ① 红。而**卡上有真话**：
             * `内容=VolumeMember 卷号=2 专属算法=VolumePiece`。</para>
             *
             * <para>⇒ 判据走唯一出口 <see cref="VolumePieceFacts.NameOnlyGuessedItselfAsFirstVolume"/>
             * （名字是猜的 + 内容是"第 N≥2 片"；⛔ 跨盘 ZIP 本体 `*.zip` 不算猜 —— 那个 1 是那一族的入口锚点，
             * 翻它会打红 `13131313`/`23232323`，今天 g93/g94 对照实测）。⛔ 判不出 ⇒ 与老口径逐字相同。</para>
             */
            if (!nameGaveCanonical
                || VolumePieceFacts.NameOnlyGuessedItselfAsFirstVolume(FileNameHelper.GetFileName(piecePath), nature))
            {
                /*
                 * ⚠ 2026-10-14（官方环台 `10101010`，日志逐字）：**名字判不出族时，问卡**。
                 *
                 * <para>现场：这一片叫 `pack111-L2.7z.txt`（伪装法⑤"改成别的类型"）⇒ 规范名出口认不出族
                 * ⇒ 逐字 `这一片接不进这一组 —— 认不出它属于哪一族 / 第几卷（规范卷名推不出来）`
                 * ⇒ 它永远接不进去；而**卡上全都有**（探针逐字）：`格式=SevenZip 分卷身份=Unknown
                 * 专属算法=VolumeFirstPiece 自述整包=62919013 按内容算的片数=3`。</para>
                 *
                 * <para>⇒ 卡优先的回落（用户口径「专属算法不就是靠识别的吗」）：族取卡上的格式、
                 * 卷号取卡上的（见 <see cref="ResolveVolumeSlotIndexForPiece"/>）、基名把伪装尾巴剥掉。
                 * ⛔ 卡上也给不出卷号（`VolumePiece` 那种"我是后续片但没写第几"）⇒ 照旧返回 false，
                 * 交给"缺口 + 体积"那一档，⛔ 不猜号。</para>
                 */
                /*
                 * ⚠ 2026-10-16（真机 `19191919` 的整条判据链探针逐字，`af-probe-d.txt`）：
                 * `[规范名·进] 片=aaa-L2-976 槽位=2 样本=null **锚=无**` ⇒ 槽位（卡上那张号）是对的，
                 * 卡在**锚**上；而同一时刻目标层里**没有**第 1 卷（另一条探针逐字：
                 * `目标层=有 目录在=是 层内文件数=2 层内第1卷=无`）—— 入口还没被链接进那一层。
                 * ⇒ 取锚**先看这一单自己那一份**（批末重判里"这一组该拼到哪一层"就是按这一单算出来的，
                 * 所以它手上那份就是这一组的入口：同一份探针里 `片=aaa-L2 卡号=1 卡类=VolumeFirstPiece`），
                 * 拿不到再扫目标层（原口径）。
                 */
                FileNatureCard? groupAnchor = ResolveGroupAnchorCard(owner, targetDirEarly, baseName, groupEntryPathOverride);

                canonical = VolumeNameOrchestration.ResolveCanonicalNameFromCard(
                    piecePath,
                    nature,
                    groupAnchor,
                    contentSlotForNaming,
                    namingFamily);

                if (canonical.Length == 0)
                {
                    LogAdoptCriterionInputs(
                        piecePath,
                        /*
                         * ⚠ 2026-10-16（用户口径与项目规矩："判不出的分支把**判据输入**写进日志"）：
                         * 这一行原来只说"推不出来"，**锚有没有、层是哪来的**一个字都没有 ——
                         * 而真机 `29292929` 卡住的地方恰恰就是这两件（纯函数实测：只要把锚交进去，
                         * 规范名立刻是 `pack111-L2.part2.rar`）。⇒ 永久把这三个字段写进来，⛔ 不改判据。
                         */
                        "认不出它属于哪一族 / 第几卷（规范卷名推不出来；卡上也没给出可用的族与卷号）"
                        + $"｜锚={(groupAnchor == null ? "无" : FileNameHelper.GetFileName(groupAnchor.Path))}"
                        + $"｜层={(layerOverride == null ? "自推" : "外层交下来的")}"
                        + $"｜目标层={(targetDirEarly.Length > 0 ? "有" : "空")}"
                        /*
                         * ⚠ 2026-10-16 再补两个数字（按 §29.24 的结论：目标层与锚**只能按"这一片所属的那一组"**算，
                         * 而"属于哪一组"必须从上层交下来）—— 这两个数字用于判"上层交下来的够不够用"：
                         * 任务表里**一份第 1 卷都没有** ⇒ 接片那一档根本没有可借的入口，得让上层把组交下来。
                         */
                        + $"｜这一单={(owner == null || string.IsNullOrEmpty(owner.CurrentPath) ? "无" : FileNameHelper.GetFileName(owner.CurrentPath))}"
                        + $"｜任务表首卷份数={CountFirstVolumeTasksInTable()}",
                        string.Empty,
                        string.Empty,
                        string.Empty);

                    return false;
                }

                AppendLog(
                    "INFO",
                    $"{FileNameHelper.GetFileName(piecePath)}：名字判不出族/卷号，改用底层识别卡 —— "
                    + $"卡上：{nature.Describe()} ⇒ 规范卷名「{canonical}」。");
            }

            /*
             * 目标层 = **入口包所在的那一层**（用户 2026-10-06：「你把分卷移过来不就是了吗」）——
             * 入口包（跨盘 ZIP 族 = 末片 `X.zip`）由 `PublishUnresolvedVolumePieces` 先落在
             * **产出它的那个包自己那条链的落点目录**里，`ResolveBatchGroupDirectory` 读的就是它；
             * 于是"收片层 = 入口层 = 落点层"三处是同一个事实位，⛔ 不再看任务表顺序。
             *
             * 入口包还不在盘上（批首那一刻 / 它没被解出来）⇒ 退回旧口径（任务表里第一单那一层），
             * 那一档行为与改动前逐字相同。
             *
             * ⚠ **一层只准算一次**（2026-10-11 实测，`ChainInnerVolumeRestoreTests.真机布局…` 那条真机形状打红）：
             * 批末那一遍（`TryGatherGroupPiecesInto`）**已经**按入口包把这一组的层算出来了，可它交下来的每一片
             * 原来又**各自重算一遍** —— 而重算这一遍用的是**这一片自己的名字**，真机上那几片的名字是脏的
             * （`111_.part2` / `111_.pasrt3.rcar` / `111_.par删t1.ra除r`）⇒ 撞上"脏名不启用新判据"那道闸门
             * ⇒ 每一片都回一句「判不出」 ⇒ 三片一片都接不进去，整组永远停在缺卷（日志逐字：
             * `批末重判 —— 这一组该拼到哪一层 = …\out\111__.rLLLLar` 紧跟着
             * `111_.part2：这一片接不进这一组 —— 推不出「这一组该拼到哪一层」`）。
             * ⇒ 同一件事的真值**只允许有一个出口**：外层已经定过的层，原样交下来用（`layerOverride`），
             * ⛔ 接片这一档**不再自己重算**。
             */
            string targetDir = targetDirEarly;

            if (targetDir.Length == 0)
            {
                LogAdoptCriterionInputs(
                    piecePath,
                    "推不出「这一组该拼到哪一层」（入口包还没落下来，任务表里也没找到这一组）",
                    canonical,
                    string.Empty,
                    string.Empty);

                return false;
            }

            string target = Path.Combine(targetDir, canonical);
            bool adopted = false;

            if (Directory.Exists(target))
            {
                // 目标名被一个**目录**占着：硬链接建不出来（⛔ 绝不删、绝不覆盖）。
                LogAdoptCriterionInputs(
                    piecePath,
                    "目标名被一个目录占着（⛔ 绝不覆盖、绝不删）",
                    canonical,
                    targetDir,
                    target);

                return false;
            }

            /*
             * ⚠ **目标位上已经有同名文件时先问一句：是不是这一片自己**（2026-10-11 环台实测逮到的假拒绝）。
             *
             * 现场（程序自己的判据输入，同一秒里自相矛盾的两句）：
             *   `111.zip：这一片接不进这一组 —— 目标名已经被别的文件占着（⛔ 绝不覆盖）｜判据输入：… 目标名=「…\111\111\111.zip」，同名位上=同一份文件（就是这一片自己，只是名字已经在位）`
             * 紧接着同一单又打：`111.rar：111.zip 已经在目标位上、而且是「同一份文件」（接片时建的硬链接）—— 不必再搬一次，也不留多余的名字。`
             * ⇒ 那根本不是"被占用"，是**这一片早就链接在位**了。老代码只判 `File.Exists(target)` 就返回 false
             * ⇒ 后面那套记账（`RememberGroupPieceProducer` / `RememberPieceSupplyingTask` /
             * `RememberAdoptedLandedPiece`）**整段被跳过** ⇒ 产出方那一份"已经落地的片"没人收
             * （用户 2026-10-10 点名要收的那一条），而且把一个成功的状态写成 WARN 假警报。
             * ⛔ 判据只读事实：同名位上**是不是同一份物理文件**（`FileIdentity.IsSamePhysicalFile`）；
             * 是**别人**的文件 ⇒ 照旧判"接不进"（⛔ 绝不覆盖）。
             */
            /*
             * ===== 按试开确认的顺序，收掉"旧的规范名" =====
             * 2026-10-18 新批 `24242424`：入口层里 `.z01`/`.z02` 是早先建的硬链接，但内容顺序把另一份中段片
             * 排到槽位 1 ⇒ 目标名被旧链接占着。**只在"占着这个名字的那一份，已经在它自己正确的规范名下"
             * 时才收**（它已有正确名字、字节还在，删的是多余链接）；⛔ 不碰用户 `OriginalPath`；
             * ⛔ 链接数 ≤ 1（唯一一份）一律不动。
             */
            if (File.Exists(target)
                && !IsTargetSlotAlreadyThisPiece(piecePath, target)
                && slotCanonicalTargets != null
                && contentSlots != null
                && contentSlots.TryGetValue(VolumeSlotKey(target), out int occupantSlot)
                && slotCanonicalTargets.TryGetValue(occupantSlot, out string? correctTarget)
                && !string.IsNullOrWhiteSpace(correctTarget)
                && !string.Equals(correctTarget, target, StringComparison.OrdinalIgnoreCase)
                && File.Exists(correctTarget)
                && FileIdentity.IsSamePhysicalFile(correctTarget, target)
                && FileIdentity.TryGetLinkCount(target, out uint targetLinks)
                && targetLinks > 1
                && !IsAnyTaskOriginalPath(target))
            {
                try
                {
                    File.Delete(target);

                    AppendLog(
                        "INFO",
                        $"{FileNameHelper.GetFileName(piecePath)}：目标名 {canonical} 原来是槽位 {occupantSlot} 的旧名字 —— "
                        + $"那一份已经在正确的规范名 {Path.GetFileName(correctTarget)} 下（零字节硬链接）⇒ 收掉旧名字，"
                        + "建这一片的正确链接（一个字节都不丢）。");
                }
                catch (Exception ex)
                {
                    AppendLog(
                        "WARN",
                        $"{FileNameHelper.GetFileName(piecePath)}：旧的规范名 {canonical} 没能收掉（{ex.Message}）");
                }
            }

            bool alreadyInPlace = IsTargetSlotAlreadyThisPiece(piecePath, target);

            if (!File.Exists(target) && HardLinkHelper.CanHardLink(piecePath, targetDir))
            {
                /*
                 * 这一片还在**我们自己的过程物目录**里（`.ArchiveFixer.work` / 其余物）⇒ 用**移动**
                 * （同盘改名、零字节）。为什么不能只建链接：那些目录收尾会整份删掉 / 按删除档处理，
                 * 留一份在里面等于"接了一个马上就要消失的名字"，而且轮次续解下一轮还会把剩下的那一份
                 * 当成一个"内层包"再去解一次（真机上就是那句「111.zip：分卷缺失」，用户最烦看到它）。
                 *
                 * ⛔ **唯一的例外 = 本单自己的暂存目录**（见 `ShouldMovePieceIntoGroup`）：
                 * 那是收尾链（结果校验 / 定稿）要读的那一份，搬走 = 把自己这一单唯一的产物拿走。
                 *
                 * 用户目录里的片一律**只硬链接**：⛔ 名字一个字符都不许改、文件不搬。
                 */
                bool fromProcessFolder = ProcessArtifactLayout.IsInsideDeletableProcessFolders(piecePath);
                string ownerStage = ResolveOwnerStageDirectory(owner);
                /*
             * ⚠ 2026-10-17（用户口径：「第一大步和第二大步衔接的时候在原来的地方不动…找到完整的才一起搬动到
             * 入口包位置」）：批末那一条路会传 `allowMoveIntoGroup = (决策器说 Gather)`。
             * 决策器说 `Leave`（证据还没齐）⇒ **只建链接、绝不搬原件** —— 链接是实名的第二份名字，
             * 原件仍在它自己那一层（也就是"原来的位置"）。
             */
                bool moveIntoGroup = allowMoveIntoGroup
                    && ShouldMovePieceIntoGroup(piecePath, ownerStage, CollectLiveChainWorkDirectories());

                /*
                 * ⛔ **用户导入的原包自己 ⇒ 只建链接，绝不搬**（项目既有红线的延伸：用户源目录里的片
                 * 「只硬链接、不改名不搬」—— 而那一条原来只管"在用户目录里"的情形，管不到"用户的原包
                 * 被源包档搬进过程物目录之后"这一档）。
                 *
                 * <para><b>运行期事实</b>（`batch_evaluate` 打在接片那一档的日志行上）：EEE 里
                 * `piecePath=…\in\111\111\111\其余物\111.rar` 走的就是"过程物目录 ⇒ 用移动"那一支
                 * （`renamedInPlace=true`）⇒ 用户那份源包被搬出其余物、再也没人按档处理它。</para>
                 *
                 * <para>⛔ 判据取既有事实位（<see cref="IsUserImportedSourcePath"/>：非续解任务的那几单
                 * = 用户给的原包）；⛔ 判不出 ⇒ 照旧老口径（该搬还是搬）。硬链接是**另起一个规范卷名**，
                 * 源文件一个字节没动 —— 与「接片」那一档对用户文件的既有口径完全一致。</para>
                 */
                if (moveIntoGroup && (IsUserImportedSourcePath(piecePath) || IsAnyTaskOriginalPath(piecePath)))
                {
                    moveIntoGroup = false;
                }

                /*
                 * ===== 用户源目录里的那一片：⛔ **仍然只硬链接**（四次试做都撞在同一处，如实记账）=====
                 *
                 * 用户 2026-10-06 拍板：「**去掉暗链当出口**，解压用盘上标准名的文件，临时名只许建在工作区」。
                 * **四次试做都失败、都已撤回**：① 只同步 `owner` ⇒ 红；② 对**全表**同步 ⇒ 红；
                 * ③ 再把**借片账的键**一起重映射 ⇒ 红；④ 再把**源文件快照**按新路径重拍（不变量 11 那一关）⇒ **还是红**。
                 * ⇒ 已排除四处（任务账路径 / 借片账键 / 收卷判据只认规范名 / 源文件快照）；
                 * **第五处仍未找到**。⛔ 没找到它之前不许再改这一行（改一次红一次，四次都白改）。
                 *
                 * ⚠ 2026-10-10：那一行只读取证的 `收片排障：…` 探针（2026-10-09 加的）**已删** ——
                 * 真机复跑把真正的病灶找到了，**不在这条支上**：散在 `111(4)\` 的那一片**根本没走进来**
                 * （`FileNameHelper.GetArchiveBaseName` 认不出被拉长的卷号 ⇒ 目标层算不出来 ⇒ 静默返回），
                 * 而且批末补判那份名单当时是**空的**（中途撞缺卷的单从不登记）。两处都已修，
                 * 与"搬还是链接"这一行的取舍无关 ⇒ 探针的使命结束（见 `修改日志.md` 本轮）。
                 */
                /*
                 * **接片落名判据（永久）**：2026-10-18 调试器实测出现过"canonical 是内容族、真正落链接的
                 * target 却是名字族"的错位（`.zip.003` 对 `.z01`）⇒ 把这三个值一次写清楚，
                 * ⛔ 只写程序当场算出来的值、⛔ 不改任何判据。
                 */
                AppendLog(
                    "INFO",
                    $"{FileNameHelper.GetFileName(piecePath)}：接片落名判据 —— 规范名=「{canonical}」，"
                    + $"目标名=「{Path.GetFileName(target)}」，目标层=「{targetDir}」，目标路径=「{target}」");

                adopted = moveIntoGroup
                    ? TryMovePieceIntoGroup(piecePath, target) || HardLinkHelper.TryCreateHardLink(target, piecePath)
                    : HardLinkHelper.TryCreateHardLink(target, piecePath);

                if (adopted)
                {
                    bool renamedInPlace = !File.Exists(piecePath);

                    if (renamedInPlace)
                    {
                        SyncEveryTaskPathAfterMove(piecePath, target);
                    }

                    /*
                     * 记下"**产出方那一份已经落地的片**被接进了这一组"（用户 2026-10-10 点名的那条不一致）：
                     * 接片是"搬"还是"建链接"取决于这一片那一刻**恰好在哪**（时序），而两档收场后的盘面
                     * 必须一致 —— 整组可证完整 + 校验通过 + 未取消之后，产出方那一份**不留**
                     * （`111.z01` 那一档就是这么收的）。判据与红线见 `RememberAdoptedLandedPiece`。
                     */
                    RememberAdoptedLandedPiece(piecePath, owner, baseName, renamedInPlace);

                    /*
                     * ⚠ **说话要按事实分三种**（旧文案一律写"零字节的硬链接、名字一个字符没改"，
                     * 而过程物那一档走的是移动 —— 那是假话；用户 2026-10-06 正是拿这一点追问的）。
                     * 第三种 = "本单自己的暂存目录里的那一份只建链接"（见 `ShouldMovePieceIntoGroup`）：
                     * 它既不是搬走、也不是"用户的源文件"，照实说。
                     */
                    AppendLog(
                        "INFO",
                        renamedInPlace
                            ? $"「{baseName}」这一组缺的那一片解出来了（已按规范卷名 {canonical} 落到「{targetDir}」这一层；"
                              + "它是我们自己解出来的过程物，用的是同盘改名的「移动」，原位置不再留一份）。"
                            : fromProcessFolder
                                ? $"「{baseName}」这一组缺的那一片解出来了（已按规范卷名 {canonical} 接到「{targetDir}」这一层；"
                                  + "这一片还在我们自己的工作区暂存目录里，只建了一个零字节的硬链接 —— 一个字节都没动）。"
                                : $"「{baseName}」这一组缺的那一片解出来了（已按规范卷名 {canonical} 接到「{targetDir}」这一层；"
                                  + "零字节的硬链接：源文件一个字节没动、名字也一个字符没改）。");
                }
            }

            else if (alreadyInPlace)
            {
                // 这一片早就在位、而且是同一份文件 ⇒ **算接片成功**（⛔ 一个字节都不搬、不覆盖）。
                adopted = true;

                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PieceAlreadyInPlaceFormat,
                        baseName,
                        canonical,
                        Path.GetFileName(targetDir)));
            }

            if (adopted)
            {
                /*
                 * **这一组拼到哪一层**是一个已经做过的决定（两条支路都算过）⇒ 记下来给别的档读，
                 * ⛔ 不让它们重算一遍（读点见 `_groupLayerByBaseName` 的注释：定稿那一档"不落地"的闸门）。
                 *
                 * ⚠ **只有"拼到了别处那一层"才算**：算出来的层就是这一片自己待着的那一层时（接片是空操作，
                 * 常见于"这一片本身就是入口/这一组只有它"），不存在"已经把这一份拼进去了"这件事
                 * —— 记了它，定稿那一档就会把本该发布的产物当成"多余名"而不落地
                 * （实测打红：`InnerLayerContinuationTests.外层包只有内层包时_内容物落进外层包名目录而不是平铺进共用根`）。
                 */
                if (!SafePathHelper.PathEquals(targetDir, Path.GetDirectoryName(piecePath) ?? string.Empty))
                {
                    _groupLayerByBaseName[baseName] = targetDir;
                }

                /*
                 * 记下"是这一单吐出了这一片"：整组由别单解开、而且**可证完整**之后，这一单也按跟班卷收场
                 * （真机第九批 CCCC：`111.rar` / `111(2)_.zip` 各吐一片，整组解开并校验通过了，
                 * 这两个原包却因为自己"部分完成"一直留在盘上 —— 用户当场就问为什么）。
                 */
                RememberGroupPieceProducer(baseName, FileNameHelper.GetFileName(target), owner);

                /*
                 * ⛔ **这一片是从哪一单自己的源片库里来的** ⇒ 把**那一单**也记成"这一组的一片由它供给"
                 * （按**任务身份**，⛔ 不按路径、⛔ 不按文件名）。真机 CCCC 2026-10-06：`111(4)\111.z03`
                 * 那一单永远收不了场 —— 因为批末按路径/文件名回查（它起点被改写到入口包上、最初路径还是
                 * 改名前的脏名），而物理同一性在"组层那个链接已经被删掉"之后也无从判定
                 * ⇒ 红线「判不出 ⇒ 什么都不做」，源片一直留着。按身份记，这一档从此不依赖事后反推。
                 */
                RememberPieceSupplyingTask(baseName, piecePath);
            }
            else
            {
                /*
                 * 接不上那一档也把判据输入写出来（目标单③）：目标名这一刻算成了什么、那一层在哪、
                 * 盘上有没有同名 —— 光看"没接上"三个字，下一轮排查还得从头再跑一遍。
                 */
                LogAdoptCriterionInputs(
                    piecePath,
                    DescribeAdoptRejection(piecePath, target),
                    canonical,
                    targetDir,
                    target);
            }

            // 末片在哪一层，整组就得凑到哪一层（引擎只看入口旁边那一层）⇒ 末片到手就顺手把整组接齐。
            string tail = Path.Combine(targetDir, baseName + ".zip");

            if (!File.Exists(tail))
            {
                return adopted;
            }

            VolumeNameRepair.SpannedZipDiskGather gather = VolumeNameRepair.ResolveSpannedZipDiskGather(
                tail,
                VolumeNameRepair.EnumerateVolumeCandidatesNearby(tail));

            if (gather.Applicable && gather.LinkedCount > 0)
            {
                AppendLog("INFO", $"「{baseName}」这一组又接过来 {gather.LinkedCount} 片：{gather.Detail}");
            }

            if (gather.Applicable && gather.Complete)
            {
                AppendLog(
                    "INFO",
                    $"「{baseName}」这一组现在凑齐了 —— 等这一批的解压都跑完，批末那一站会直接把这一组解开。");
            }

            return adopted || (gather.Applicable && gather.Complete);
        }

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
                    TryAdoptUnresolvedVolumePiece(piece, owner);
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

                        TryAdoptUnresolvedVolumePiece(file, task);
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

                    File.Move(piece, target);

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
                    if (VolumeGroupDetector.BelongsToSameGroup(FileNameHelper.GetFileName(path), tailName))
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
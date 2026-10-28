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
    /// <summary>被别单接手的片/组的结算（第 10 块，从 Password 大杂烩搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// **任务表里已经有一单"代表"这一组了吗**（= 有一单自己的文件就是这一组的一片）。
        ///
        /// <para>用途只有一个：决定"停在**别人**那一组"的单要不要进批末名单。真机形状
        /// （`SiblingFolderVolumeGatherTests` 的跨链收卷那几条）里**用户导入的那几片自己就是任务**
        /// ⇒ 那一组的活该由它们干（老口径本来就能把它们登记进来）⇒ 产出方那一单**不进名单**，
        /// 一个字节的行为都不变；而困难批 `11111111` 那一档里**没有任何一单是这一组的片**
        /// （用户导入的只有三条产出方链）⇒ 产出方那一单进名单，那一组才有人收拢、才有人解开。</para>
        ///
        /// <para>⛔ 判据仍走唯一那把尺子 <see cref="VolumeGroupDetector.BelongsToSameGroup"/>（族 + 基名）。</para>
        /// </summary>
        private bool AnyTaskRepresentsGroup(string piecePath)
        {
            string pieceName = FileNameHelper.GetFileName(piecePath);

            if (pieceName.Length == 0)
            {
                return false;
            }

            foreach (ArchiveTask? candidate in SnapshotTaskTable(Tasks))
            {
                string name = FileNameHelper.GetFileName(candidate?.CurrentPath ?? string.Empty);

                if (name.Length > 0 && VolumeGroupDetector.BelongsToSameGroup(name, pieceName))
                {
                    return true;
                }
            }

            return false;
        }

        /// `111(2)\111(2)_\111.z01`，200 MiB）。
        ///
        /// <para>为什么要补这一份：工作区收尾会把暂存那一份整份删掉；如果那一刻"这一组该在的那一层"
        /// 还认不出来（入口包还没被解出来 / 目录被占），这一片就会跟着工作区一起消失
        /// ⇒ 批末那一站到盘上再也找不到它（真机 `那一片已经不在盘上（…\output\111.zip）` 那一次）。</para>
        ///
        /// <para>⛔ **只在"那一层还没有它"时补**：接片成功之后那一层已经有同一个名字了
        /// （硬链接、零字节），再在落点里留一个名字就是**没人认领的多余名**
        /// —— 收尾谁也不认它，用户只会看到 200 MiB 的残留。⛔ 这里只建链接，不改名、不搬、不覆盖。</para>
        /// </summary>
        /// <param name="pieces">暂存目录里的那几份（<see cref="PublishUnresolvedVolumePieces"/> 的返回值）。</param>
        /// <summary>两份文件是不是**字节完全相同**（大小 + SHA-256；读不了 ⇒ false ⇒ 当作不同，⛔ 不乱判）。</summary>
        private static bool IsByteIdentical(string left, string right)
        {
            try
            {
                var leftInfo = new FileInfo(left);
                var rightInfo = new FileInfo(right);

                if (leftInfo.Length != rightInfo.Length || leftInfo.Length == 0)
                {
                    return false;
                }

                return string.Equals(SafeSha256(left), SafeSha256(right), StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static string SafeSha256(string path)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                using var sha = System.Security.Cryptography.SHA256.Create();

                return Convert.ToHexString(sha.ComputeHash(stream));
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 「过路层那一单的其余物按档删」那一档"判不出 ⇒ 一个字节都不删"时把**判据输入**写进日志
        /// （与 <see cref="LogAdoptCriterionInputs"/> 同一规矩，用户 2026-10-10 目标单③）。
        ///
        /// <para>为什么必须有它（2026-10-11 实测）：这一档**五道闸门全是静默早退**
        /// （没记下其余物目录 / 目录不在盘上 / 其余物里有不属于这一组的文件 / 完整性证据不允许 /
        /// 删除档是「不动其余物」），而它的两条结论日志只在**真删**时才写 ⇒
        /// 拿一份用户日志**分不出**"这一档压根没被走到"和"走到了、被哪一道闸门挡下"
        /// —— 实测：整个 <c>SiblingFolderVolumeGatherTests</c>（19 条）跑完，
        /// 那两条结论日志出现 **0 次**，加之前无法判断是没调用还是被拦。</para>
        ///
        /// <para>⛔ 只写程序当场算出来的值（判不出就写「判不出」），⛔ 它一处判据都不改。</para>
        /// </summary>
        private void LogPassThroughRestCriterionInputs(
            ArchiveTask producer,
            ArchiveTask? consumer,
            string groupBaseName,
            string restDirectory,
            string why)
        {
            string[] files;

            try
            {
                files = string.IsNullOrWhiteSpace(restDirectory) || !Directory.Exists(restDirectory)
                    ? Array.Empty<string>()
                    : Directory
                        .EnumerateFiles(restDirectory, "*", SearchOption.AllDirectories)
                        .Select(FileNameHelper.GetFileName)
                        .ToArray();
            }
            catch
            {
                // 读不出来 ⇒ 如实写"判不出"，⛔ 不猜。
                files = Array.Empty<string>();
            }

            AppendLog(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.CriterionInputsPassThroughRestFormat,
                    producer.LogName,
                    why,
                    consumer == null ? "判不出" : consumer.LogName,
                    string.IsNullOrWhiteSpace(groupBaseName) ? "判不出" : groupBaseName,
                    string.IsNullOrWhiteSpace(restDirectory) ? "判不出" : restDirectory,
                    files.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    files.Length == 0 ? "（空）" : string.Join("；", files.Take(8))));
        }

        /// <summary>
        /// **真删**这一组的那几份（调用方已经过完全部门槛，含半套分卷那道闸门）。
        ///
        /// <para>执行体仍只有 <see cref="Storage.RecycleBinService"/> 一个出口
        /// （<c>AllowedRoot</c> = 这一层的目录）；删不掉只写 WARN，⛔ 不动任何结论、⛔ 不写回任务账。</para>
        /// </summary>
        private void DeleteHarvestTargets(ArchiveTask consumer, List<string> targets)
        {
            string mode = RestHandlingModes.Normalize(RestHandlingThisBatch);

            // ⚠ 2026-10-15：回收站档整条下线 ⇒ 只剩永久删（调用方已保证这一批不是 Keep 档）。
            DeleteMode deleteMode = DeleteMode.Permanent;

            string reason = RestItemPurger.SettledPieceCopyPurgeReason;

            var service = new RecycleBinService(RestDeleteExecutor, null, null);
            int deleted = 0;

            foreach (string path in targets)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    string name = FileNameHelper.GetFileName(path);
                    string directory = Path.GetDirectoryName(path) ?? string.Empty;

                    DeleteResult result = service.Delete(
                        new DeleteRequest(path, reason),
                        new DeleteOptions
                        {
                            AllowedRoot = directory,
                            UserConfirmed = true,
                            Mode = deleteMode,
                            Reason = reason
                        });

                    if (result.SuccessCount > 0)
                    {
                        deleted++;

                        AppendLog(
                            "INFO",
                            deleteMode == DeleteMode.RecycleBin
                                ? $"{consumer.LogName}：这一组已经解开，它这一份卷 {name} 按「删除操作」移入回收站了。"
                                : $"{consumer.LogName}：这一组已经解开，它这一份卷 {name} 按「删除操作」彻底删除"
                                  + $"（{result.Outcomes.FirstOrDefault()?.EntryCount ?? 0} 个条目 / "
                                  + $"释放 {TaskSpaceEstimate.FormatSize(result.Outcomes.FirstOrDefault()?.FreedBytes ?? 0)}，不进回收站）。");
                    }
                    else
                    {
                        AppendLog(
                            "WARN",
                            $"{consumer.LogName}：这一组的卷 {name} 这次没删成 —— "
                            + $"{result.Outcomes.FirstOrDefault()?.Message ?? result.Message}（内容物不受影响，这一份仍在原处）。");
                    }
                }
                catch (Exception ex)
                {
                    // 删东西绝不许把已经成立的结论拖成异常：落成"没删成"，别的什么都不变。
                    AppendLog("WARN", $"{consumer.LogName}：收这一组卷时出现意外错误：{ex.Message}");
                }
            }

            if (deleted > 0)
            {
                AppendLog("INFO", $"{consumer.LogName}：这一组的卷已按「删除操作」收掉 {deleted} 份（这一层的过程物，不进回收站那一档）。");
            }
        }

        /// <summary>
        /// 产出方落点子目录里、与已被接手的片同一物理文件的多余名：只收名字不删字节
        /// （链接数 &gt; 1；⛔ 不碰 adopted 目标本身、用户源、`其余物`/工作区）。
        /// `28282828` 的 `aaa-W2-1\aaa-W2-0\aaa-L2.zip` 就是这一档。
        /// </summary>
        private void PurgeRedundantLandedAliases(string groupBaseName)
        {
            if (string.IsNullOrWhiteSpace(groupBaseName))
            {
                return;
            }

            var identities = new HashSet<string>(StringComparer.Ordinal);
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var adoptedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in _adoptedLandedPieces)
            {
                if (!string.Equals(item.GroupBaseName, groupBaseName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(item.PiecePath))
                {
                    adoptedPaths.Add(SafePathHelper.GetFullPathSafe(item.PiecePath));
                }

                if (FileIdentity.TryGetIdentity(item.PiecePath, out ulong volume, out ulong index))
                {
                    identities.Add(volume.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                if (!string.IsNullOrWhiteSpace(item.OutputRoot) && Directory.Exists(item.OutputRoot))
                {
                    roots.Add(item.OutputRoot);
                }
            }

            if (identities.Count == 0 || roots.Count == 0)
            {
                return;
            }

            foreach (string root in roots)
            {
                IEnumerable<string> files;

                try
                {
                    files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
                }
                catch
                {
                    continue;
                }

                foreach (string file in files)
                {
                    if (!FileIdentity.TryGetIdentity(file, out ulong fileVolume, out ulong fileIndex))
                    {
                        continue;
                    }

                    string identity = fileVolume.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ":" + fileIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

                    if (!identities.Contains(identity)
                        || ProcessArtifactLayout.IsInsideDeletableProcessFolders(file)
                        || IsAnyTaskSourcePath(file)
                        || adoptedPaths.Contains(SafePathHelper.GetFullPathSafe(file))
                        || !FileIdentity.TryGetLinkCount(file, out uint linkCount)
                        || linkCount <= 1)
                    {
                        continue;
                    }

                    try
                    {
                        File.Delete(file);

                        AppendLog(
                            "INFO",
                            $"{FileNameHelper.GetFileName(file)}：产出方落点子目录里这一份与已接手的片是同一个文件 —— "
                            + "收掉这个多余的名字，一个字节都不丢（其余名字还在）。");
                    }
                    catch (Exception ex)
                    {
                        AppendLog(
                            "WARN",
                            $"{FileNameHelper.GetFileName(file)}：多余名字没能收掉（{ex.Message}）");
                    }
                }
            }
        }

        /// <summary>
        /// **一单"已经借给本批另一单用过的源片/片"的收场 + 搬运**（唯一实现，两处转调：
        /// <see cref="SettleGroupPieceProducers"/> 遍历那份"吐出过片"的账，
        /// <see cref="SettleConsumedGroupVolumeSources"/> 处理"从没吐过东西、只是一片散着的续卷"那几单）。
        ///
        /// <para><b>机器终态为什么是「完成」而不是「跳过」</b>（真机 CCCC 2026-10-06 实测逮到）：
        /// 它的源片要随这一组进其余物、再按「删除操作」删掉，而删除侧那道闸门读的正是机器终态
        /// —— 终态落「跳过」时实测逐字写着「任务的机器终态不是「完成」（当前：Skipped）—— 其余物一个字节都不删」
        /// ⇒ 源片被搬进其余物之后**永久留在盘上**（用户问的"其余物怎么还留着"）。
        /// 这一单**确实没有失败**：它的内容已经被本批另一单完整解出来、校验通过、源片也已经归集到位
        /// —— 与消费方自己的源包同一档，所以终态落 <see cref="TaskOutcome.Succeeded"/>，
        /// 同时保留 <see cref="ArchiveTask.IsVolumeGroupFollower"/>（四处"怎么数"的判据照旧不算它）。</para>
        /// </summary>
        private void SettleOneConsumedPieceTask(
            ArchiveTask piece,
            ArchiveTask consumer,
            string groupBaseName,
            string producedPieceName,
            bool moveSources,
            List<(string Level, string Message)> logEntries,
            string? producedPiecePath = null)
        {
            if (piece == null)
            {
                return;
            }

            /*
             * ⛔ **"自己解开的组"也要收自己那一片**（真机 CCCC 2026-10-07 00:15，排障行逮到）：
             * 批末会有一单被借去当"解开这一组的单元"；如果**供给这一片的就是它自己**
             * （排障行逐字：`Original=…\111(4)\111.z0删除3`、`Current=…\其余物\111.zip`、
             * `组生产者账=111`、`账里是本单的键=空`），老写法在这里一句 `ReferenceEquals` 就返回了
             * ⇒ 它的源片 `…\111(4)\111.z03` 一直留在盘上（用户连问三次的那一个）。
             * ⇒ 分开处理：**"标跟班"那一套只对别的单做**（它自己是单元，结论是"这一组解压成功"）；
             * **源片搬运照做**（这一组确实已经解开、那一片已被接手）。
             */
            bool selfConsumed = ReferenceEquals(piece, consumer);

            if (!selfConsumed && (!piece.IsVolumeGroupFollower || piece.Outcome != TaskOutcome.Succeeded))
            {
                /*
                 * ⛔ **族必须对得上**（2026-10-11 真机 EEE 定案，调试器 + 程序判据输入 + 引擎三方取证）：
                 *
                 * 「谁吐出过哪一组的片」这本账的键是**基名**（`_groupPieceProducers`），而同一个基名底下
                 * 可能同时存在**两族**：EEE 的跨盘 ZIP 组 `111.z01/z02/z03/111.zip`，与它解开之后得到的
                 * RAR 对 `111.parts1.racr` / `111.partɾ2.ra??r`（引擎证实这一对含 5 个 mp4）——
                 * 两族都叫 `111`。缺这一道闸门时，RAR 那两卷被记成"ZIP 组的片"⇒ 在这里被**静默**
                 * 清掉落点 / 清校验 / 标跟班（真机逐字：`校验=NotAttempted，落点=空，归集=空`）
                 * ⇒ 轮尾扫描没有目录可扫 ⇒ 链条停在内层那一对卷上，用户要的 mp4 永远出不来。
                 *
                 * 判据只有一条**既有尺子**：这一片的族与**消费方那一组**的族是不是同一组
                 * （`BelongsToSameGroup` = 族 + 基名）。⛔ 不新造判据、⛔ 不放宽"证明完整"那一步。
                 * 对不上 ⇒ **判不出 ⇒ 什么都不做**（不清落点、不清校验、不标跟班），并如实写判据输入。
                 */
                string pieceName = FileNameHelper.GetFileName(producedPieceName ?? string.Empty);
                string consumerGroupName = FileNameHelper.GetFileName(consumer.CurrentPath ?? string.Empty);

                /*
                 * ⛔ **名字认不出族时，用底层内容卡回答**（用户 2026-10-18 的 `&&` 口径：
                 * 第一大步的片是不是这一族的成员，看内容；名字只是辅助）。
                 * 现场（`19191919`）：`bbb-L2.zip-658` 的名字读不出 `.zip.00N` 族 ⇒ 老判据拒绝跟班收场
                 * ⇒ 它的原包一直留着；而它的内容卡（Zip + 卷成员）与接手方同一族。
                 * 判据：① 两边**内容格式**相同且不是 Unknown；② 这一片的内容不是 `ProvablyNotVolumeMember`；
                 * ③ 基名与组基名有关（既有 `BaseNameRelatedToGroup`）。三条都成立才算同一族；
                 * 判不出 ⇒ 老口径（拒绝，不清、不删）。
                 */
                bool sameGroup = pieceName.Length > 0
                    && consumerGroupName.Length > 0
                    && VolumeGroupDetector.BelongsToSameGroup(pieceName, consumerGroupName);

                if (!sameGroup)
                {
                    try
                    {
                        FileNatureCard producedCard = FileNature.Inspect(producedPiecePath ?? piece.CurrentPath);
                        FileNatureCard consumerCard = FileNature.Inspect(consumer.CurrentPath);

                        sameGroup = producedCard.Format == consumerCard.Format
                            && producedCard.Format != VolumeContentFormat.Unknown
                            && producedCard.Membership != VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember
                            && VolumeGatherStep.BaseNameRelatedToGroup(pieceName, groupBaseName);
                    }
                    catch
                    {
                        // 卡读不出来 ⇒ 判不出 ⇒ 老口径（拒绝）。
                        sameGroup = false;
                    }
                }

                if (pieceName.Length > 0
                    && consumerGroupName.Length > 0
                    && !sameGroup)
                {
                    AppendLog(
                        "WARN",
                        $"「{piece.LogName}」不按跟班卷收场 —— 它吐出的那一片的族与接手方那一组对不上"
                        + $"（同一基名下的两族不许混算）｜判据输入：它吐出的片=「{pieceName}」，"
                        + $"接手方那一组的起点=「{consumerGroupName}」，账上记的组基名=「{groupBaseName}」"
                        + "，同组判据（族 + 基名）= 不成立 ⇒ 不清落点、不清校验、不标跟班");
                }
                else
                {
                    string consumedNote = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.VolumePieceProducerConsumedByOtherTaskFormat,
                    piece.LogName,
                    consumer.LogName,
                    groupBaseName);

                    /*
                     * 与既有那条跳过路**逐行同一套**（清校验 / 清落点 + 落终态 + 标跟班）：
                     * `MarkSkipped` 只写 Status，机器终态必须显式落，四处消费点才算得对。
                     */
                    piece.IsOutputVerified = false;
                    piece.OutputVerification = OutputVerificationOutcome.NotAttempted;
                    piece.OutputManifestCrossChecked = false;
                    piece.OutputPath = string.Empty;
                    piece.Outcome = TaskOutcome.Succeeded;
                    piece.IsVolumeGroupFollower = true;

                    /*
                     * ⛔ **另一半事实当场抄下来**（用户 2026-10-10 拍板：那一行「两句都写」）：
                     * 这一单自己那一趟是不是**已经跑完并定稿成功**过。判据与批末账目同一个出口 ——
                     * 还在"缺卷待批末判"名单里（`IsVolumeDeficitDeferred`）= 它自己是**停在中途**的
                     * （真机 `111.rar` / `111(2)_.zip`）；不在名单里却把片交出去 = 它自己**跑成了**、
                     * 只是产出的那片随后被整组接手（真机 `111(3).rar` + `111(3)\111(3)\111.z02`）。
                     * ⚠ 名单是**过程事实**、批末会被清（`RecheckDeferredVolumeDeficits`）⇒ 必须在这一刻
                     * 抄成持久事实，显示才不会随名单抖动。⛔ 它**只喂显示**：统计 / 删除 / 清单都不读它。
                     */
                    piece.OwnRunSucceededBeforeGroupSettlement = !piece.IsVolumeDeficitDeferred;

                    /*
                     * ⛔ 记下"是谁把这一组解开的"（显示名）—— 状态格靠这条**结构化事实**如实说
                     * 「这一片随整组解开（由「X」那一单解的）」，不再永远停在「已跳过 100%」
                     * （用户 2026-10-07：「列表里面最下面的两个，从来没有变化」）。
                     */
                    piece.SettledWithGroupUnitName = string.IsNullOrWhiteSpace(consumer.DisplayFileName)
                        ? consumer.FileName
                        : consumer.DisplayFileName;

                    // `MarkSkipped` 写的是"跳过"那句话；这里要的是"内容已由别单解出"的如实说法。
                    piece.MarkSkipped(consumedNote);

                    AppendLog("INFO", consumedNote);
                }
            }

            /*
             * 这一单的**其余物**也要按档处理（用户 2026-10-06 两次追问的那 48.35 MB）：
             * 它只出过程物、那份过程物已被这一组那次**可证完整**的解压接手 ⇒ 它的其余物不再有用了。
             */
            PurgePassThroughRestOfSettledProducer(piece, consumer, groupBaseName);

            /*
             * ⛔ **这一趟没删成就解除"已处理"标记、排进批末重试**（真机 2026-10-06 20:07 实测）：
             * 这一刻半套分卷那道闸门**拦得对** —— 同组的 `111.z01`（名字不同的一片）还在成品树里，
             * 删掉装着**入口包**的其余物就等于把这一组拆开。可那一组随后就解完了
             * （`彻底删除：111\其余物（7 项 / 1.07 GB）`），而旧写法把它钉在"已处理"上
             * ⇒ `111\111\其余物\111.zip`（48.35 MB）从此没有任何人再看一眼。
             */
            if (!string.IsNullOrWhiteSpace(piece.RestDirectoryPath) && Directory.Exists(piece.RestDirectoryPath))
            {
                _passThroughRestHandled.Remove(piece);
                RememberPendingPassThroughRest(piece, consumer, groupBaseName);
            }

            if (!moveSources || !_producerSourcesCollected.Add(piece))
            {
                return;
            }

            /*
             * ⛔ **源包那一档的闸门不许绕过**（与 `PostProcessSuccessAsync` 里那三支**逐字同一套**）：
             * 「不动其余物」= 源包一个字节都不搬（不变量 1 的默认档）；「空间不足 + 不删原包」= 安全档，
             * 同样什么都不做。少了这一道，`KeepInPlace` 下**用户的源片会被搬走** —— 真回归，
             * 守门用例 `真机形状_一组跨盘ZIP的片分别压在两个包里_批末照样解开`
             * 那句「用户的源片不许动」当场变红（实测逮到）。
             */
            /*
             * ⛔ **源包那一档必须读"本批的选择"，⛔ 不是⑥页那份常驻设置**（真机 2026-10-07 07:49 逮到）：
             * 一键处理的「本次选项」放在 `RunOptions` 里（同一表达式在别处用了两处：`:1235` / `:2881`），
             * 而这一档原来直接读 `Settings.SourceHandling` ⇒ 用户在本次选项里选了「放入其余物」，
             * 收场这条路却按默认档「留在原地」判 ⇒ 源片一个字节都不搬
             * （同一批里 `111.zip` 那一单却搬了 4 个 —— 它走的是读 `RunOptions` 的那条路）。
             * 这就是"一处改动要落到每一条路径上"那条规矩的反面现场。
             */
            SourceHandlingMode sourceHandling = _spaceTightThisBatch
                ? SourceHandlingMode.DeleteInPlace
                : RunOptions?.SourceHandling ?? AppSettings.ParseSourceHandling(Settings?.SourceHandling);

            if (_spaceTightThisBatch && _spaceTightKeepSourceThisBatch)
            {
                logEntries.Add((
                    "INFO",
                    $"{piece.LogName}：不删原包 —— 源片一个字节都不动（空间不足的安全档）。"));

                return;
            }

            if (sourceHandling != SourceHandlingMode.DeleteInPlace)
            {
                logEntries.Add((
                    "INFO",
                    $"{piece.LogName}：按「源包留下」这一档，源片一个字节都不动（留在原处，随时可手工处理）。"));

                return;
            }

            /*
             * ⚠ 2026-10-15：这里原来是 `ExecuteSourcePackageMove(piece, consumer.RestDirectoryPath, …)`
             * —— 把源片**搬进其余物**。用户 2026-10-14 订正之后那一档不存在了（源包操作只剩
             * 「留下 / 彻底删除」，而彻底删除是**就地删、⛔ 不搬进其余物**）⇒ 改走**同一条闸门链**的
             * 永久删那一支（唯一出口 `PurgeLayerSourcePackage`）。
             *
             * ⛔ **闸门一条都没绕过**：这一支不喂本任务的 verification / commit（这里手上没有"这一单自己定稿"
             * 的事实），改喂**接手方那一单的完整性结论**（`ResultCompletenessClassifier.Classify(consumer)`）——
             * 这正是用户 2026-10-18 拍板的 `&&`：第一大步成功 + 第二大步（组）可证完整且内容校验通过，
             * 才允许按「彻底删除」删这个原包；判不出 / 第二大步没成 ⇒ `PurgeLayerSourcePackage` 一个字节都不动。
             */
            PurgeLayerSourcePackage(
                piece,
                verification: null,
                commit: null,
                logEntries,
                LayerPurgeTrigger.SourceHandlingDelete,
                reclaimVerdict: ResultCompletenessClassifier.Classify(consumer));

            return;
        }

        /// <summary>
        /// **"这一组的内容已经由本批另一单解出来了"的那几单：源包按跟班卷收场 + 按删除档处理**
        /// （真机 CCCC 2026-10-06，用户原话「原包怎么还没有删除」「其余物怎么还留着」）。
        ///
        /// <para>与 <see cref="SettleGroupPieceProducers"/> 的分工：那一档管"**吐出过片**的那几单"
        /// （它自己解到一半停了、源包留在盘上）；这一档管"**从没吐过东西、只是一片散着的续卷**"的那几单
        /// —— 它们整组由别人解开，按跟班卷收场，可源片一直没人处理。</para>
        ///
        /// <para>判据只有两条事实位（<see cref="TryResolveConsumedByAnotherTask"/> 用的**同一对**）：
        /// ① 这一单的路径在既有那份"借来用过的源片"账 <see cref="_consumedVolumeSources"/> 里；
        /// ② 那位消费方**真的成功了**（机器终态）。⛔ 不碰消费方自己、⛔ 幂等、⛔ 搬运仍只有既有出口。</para>
        /// </summary>
        /// <param name="logEntries">非空 ⇒ 日志先攒起来交给调用方统一落（批内并发路径要回 UI 线程）；空 ⇒ 这里直接落。</param>
        private void SettleConsumedGroupVolumeSources(List<(string Level, string Message)>? logEntries)
        {
            if (_consumedVolumeSources.Count == 0)
            {
                return;
            }

            var collected = logEntries == null ? new List<(string Level, string Message)>() : null;

            foreach (ArchiveTask task in EnumerateCandidateTasksForConsumedSettle())
            {
                if (task == null)
                {
                    continue;
                }

                /*
                 * 兜底必须比"借片账"宽一档：这一组**确实由某个已成功的消费方解开**时，就算这一单的路径
                 * 没记进那份账，也照样收场 —— 判据仍是两个**事实位**（账上那一组谁在解 + 消费方成功）。
                 *
                 * 真机 CCCC 的 `111.z0删除2` 就是这样：同组的 `111.z0删除3` 走了账、被处理掉了，
                 * 而它没进账（收卷那一刻它那一片还没被采纳）⇒ 源片一直留在盘上。
                 */
                bool consumedHit = TryResolveConsumedByAnotherTask(task, out string consumerName);

                string owner = consumedHit ? consumerName : ResolveSuccessfulGroupConsumerName(task);

                if (owner.Length == 0)
                {
                    (collected ?? logEntries!).Add((
                        "INFO",
                        $"{task.LogName}：这一组的源片先不动 —— 既有账里查不到「这一组由哪个已成功的单解开」"
                        + "（判不出 ⇒ 什么都不做，源片原样留在原处）。"));

                    continue;
                }

                if (!consumedHit)
                {
                    // 记账与收场一起补（唯一不变量：两边同时写，⛔ 不留下写了一半的账）。
                    // 记的是**它自己那一片**的路径（`OriginalPath` 优先）—— 批末补判可能已经把
                    // `CurrentPath` 改到入口包上了（真机实测），记错了下一步就会去搬入口包。
                    RememberOneConsumedVolumeSource(ResolveOwnPiecePath(task), owner);
                }

                ArchiveTask? consumer = null;

                foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
                {
                    if (candidate != null && string.Equals(candidate.FileName, consumerName, StringComparison.Ordinal))
                    {
                        consumer = candidate;
                        break;
                    }
                }

                if (consumer == null || consumer.Outcome != TaskOutcome.Succeeded)
                {
                    continue;
                }

                // 记账 + 收场 + 搬源包：唯一实现 SettleOneConsumedPieceTask（跟班 / 幂等都只有那一处写）。
                SettleOneConsumedPieceTask(
                    task,
                    consumer,
                    FileNameHelper.GetArchiveBaseName(consumer.CurrentPath ?? string.Empty),
                    FileNameHelper.GetFileName(SourcePackageMover.ResolveOwnFileOnDisk(task) ?? string.Empty),
                    moveSources: true,
                    collected ?? logEntries!);

                if (task.IsVolumeGroupFollower)
                {
                    _volumeDeficitDeferred.Remove(task);
                    _volumeDeficitFinalPass.Add(task);
                }
            }

            if (collected != null)
            {
                foreach ((string level, string message) in collected)
                {
                    AppendLog(level, message);
                }
            }
        }

        /// <summary>
        /// **这一单自己那一片在盘上的路径**（优先 <c>OriginalPath</c>）。
        ///
        /// <p>⚠ 为什么不能直接用 <c>CurrentPath</c>：批末补判那一站会把补齐了的那一单的起点改到
        /// **入口包**上（真机 CCCC 的 `111.z0删除2` 实测：`CurrentPath` 变成了
        /// `…\111\其余物\111.zip`）—— 拿它去判"这一单的源片还在不在"会判到入口包头上。
        /// `OriginalPath` 是**最初导入路径**（搬运成功后才跟着更新）。判不出 ⇒ 空串。</p>
        /// </summary>
        private static string ResolveOwnPiecePath(ArchiveTask task)
        {
            /*
             * ⛔ **先问"这一份还在不在"**（真机 CCCC 2026-10-06 22:50）：`OriginalPath` 是**最初导入路径**，
             * 而已知改名/搬运**不会**把它改过来（实测它一直停在改名前的脏名 `111.z0删除3` 上）。
             * 拿一个不存在的路径去判"源片还在不在"、去搬源片 ⇒ 一定落空 ⇒ 源片永远留在盘上。
             * ⇒ 顺序：账上那条**真的在**就用它；不在就按"同目录同包基名"问盘（`FindOwnPieceOnDisk`）；
             * 再不行才退回 `CurrentPath`。⛔ 只读、⛔ 一个字都不改。
             */
            if (!string.IsNullOrWhiteSpace(task.OriginalPath) && File.Exists(task.OriginalPath))
            {
                return task.OriginalPath;
            }

            string onDisk = SourcePackageMover.ResolveOwnFileOnDisk(task);

            if (onDisk.Length > 0)
            {
                return onDisk;
            }

            return task.CurrentPath ?? string.Empty;
        }

        /// <summary>
        /// **这一单所在那一组的消费方是谁**（只读既有那份"谁在解"的账 `_groupConsumerByName`），
        /// 而且那位消费方**真的成功了**（机器终态）。判不出 / 还没成功 ⇒ 空串 ⇒ 调用方什么都不做。
        /// </summary>
        private string ResolveSuccessfulGroupConsumerName(ArchiveTask task)
        {
            string baseName = FileNameHelper.GetArchiveBaseName(task.CurrentPath ?? string.Empty);

            if (baseName.Length == 0
                || !_groupConsumerByName.TryGetValue(baseName, out string? name)
                || string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
            {
                if (candidate == null || !string.Equals(candidate.FileName, name, StringComparison.Ordinal))
                {
                    continue;
                }

                /*
                 * ⛔ **借片的那一单不是消费方**（真机 CCCC 2026-10-06 实测）：批末补判会把补齐了那一单的
                 * `CurrentPath` 改到入口包上 ⇒ 它与真正的消费方**撞名**（两个都叫 `111.zip`）。
                 * 判据 = 它自己那一片也在"借片账"的键里（`IsBorrowedPieceTask`）。
                 */
                if (ReferenceEquals(candidate, task) || IsBorrowedPieceTask(candidate))
                {
                    continue;
                }

                if (candidate.Outcome == TaskOutcome.Succeeded)
                {
                    return name;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// 这一趟该检查哪些单：**在册的延迟缺卷单** + **记账里那几单的生产者** + **全表**（三处并集，去重）。
        ///
        /// <para>为什么要有"全表"那一档：真机那一批里那两单在批末已经是「跟班卷 + 跳过」了
        /// （`TryResolveConsumedByAnotherTask` 那条路给它们落的），既不在延迟名单里、也从没吐过片
        /// —— 只有"全表"才扫得到它们。代价可控：真正的判据是"这一单的路径在不在这份借片账里"，
        /// 绝大多数单第一条就 false；搬过的由 <c>_producerSourcesCollected</c> 挡住，不会搬第二遍。</para>
        /// </summary>
        private IEnumerable<ArchiveTask> EnumerateCandidateTasksForConsumedSettle()
        {
            var seen = new HashSet<ArchiveTask>();

            foreach (ArchiveTask task in _volumeDeficitDeferred.ToList())
            {
                if (task != null && seen.Add(task))
                {
                    yield return task;
                }
            }

            foreach (List<(ArchiveTask Task, string PieceName)> producers in SnapshotGroupPieceProducerLists())
            {
                foreach ((ArchiveTask producer, string _) in producers.ToList())
                {
                    if (producer != null && seen.Add(producer))
                    {
                        yield return producer;
                    }
                }
            }

            foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
            {
                if (task != null && seen.Add(task))
                {
                    yield return task;
                }
            }

            /*
             * 还有一档非扫不可：**名字出现在"借片账"里的那些路径**对应的单。
             *
             * 真机 CCCC 的 `111.z0删除2` 就是这样：它的路径在账上（借给消费方用过），可那一单既不在
             * 延迟名单里（已经被别的分支收场过了）、也没吐过片 ⇒ 上面三处一处都覆盖不到它
             * ⇒ 源片留在盘上（实测：同组的 `111.z0删除3` 被处理了，它没有）。
             */
            foreach (string piecePath in _consumedVolumeSources.Keys.ToList())
            {
                foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
                {
                    if (task == null || seen.Contains(task))
                    {
                        continue;
                    }

                    if (EnumerateTaskPaths(task).Any(path => SafePathHelper.PathEquals(path, piecePath)))
                    {
                        seen.Add(task);
                        yield return task;
                    }
                }
            }
        }

        /// <summary>遍历"谁在解"的账：消费方**已经成功**的那几组，给它们的生产者收场（幂等；见 <see cref="SettleGroupPieceProducers"/>）。</summary>
        private void SettleSucceededGroupPieceProducers()
        {
            if (_groupConsumerByName.Count == 0 || GroupPieceProducerCount == 0)
            {
                return;
            }

            var logEntries = new List<(string Level, string Message)>();

            foreach (string consumerName in _groupConsumerByName.Values.ToList())
            {
                ArchiveTask? consumer = SnapshotTaskTable(Tasks).FirstOrDefault(
                    candidate => candidate != null && string.Equals(candidate.FileName, consumerName, StringComparison.Ordinal));

                if (consumer == null || consumer.Outcome != TaskOutcome.Succeeded)
                {
                    continue;
                }

                SettleGroupPieceProducers(consumer, moveSources: true, logEntries);
            }

            foreach ((string level, string message) in logEntries)
            {
                AppendLog(level, message);
            }
        }

        /// <summary>
        /// 内层分卷组由子任务解开后，**产出该组的父任务**也要收场。
        ///
        /// <para>真机 23.5：`导数的几何意义-polyflix.mp4` 先把 `.001/.002/.003` 解出来，
        /// 因为 `.003` 被误当独立包，父任务停在 `VolumeMissing/PartiallyCompleted`；
        /// 后来 `.001` 那一单成功，父任务却没人回写。判据只读两条既有事实：
        /// ① 子任务可证完整且成功；② 父任务的 `ProducedFiles` 里确实有这一组的片。
        /// ⛔ 不按名字猜、不碰源包、不改变其他父任务。</para>
        /// </summary>
        internal void SettleParentOfSucceededVolumeGroupForTests() =>
            SettleParentOfSucceededVolumeGroup();

        private void SettleParentOfSucceededVolumeGroup()
        {
            foreach (ArchiveTask consumer in SnapshotTaskTable(Tasks))
            {
                if (consumer == null
                    || consumer.Outcome != TaskOutcome.Succeeded
                    || !consumer.IsVolumeGroup
                    || !consumer.IsVolumeComplete)
                {
                    continue;
                }

                string groupBase = FileNameHelper.GetArchiveBaseName(consumer.CurrentPath ?? string.Empty);

                if (groupBase.Length == 0)
                {
                    continue;
                }

                ArchiveTask[] tasks = SnapshotTaskTable(Tasks);

                ArchiveTask? parent = tasks.FirstOrDefault(
                    candidate => candidate != null
                        && !ReferenceEquals(candidate, consumer)
                        && (!string.IsNullOrWhiteSpace(consumer.ParentTaskName)
                            && (string.Equals(candidate.FileName, consumer.ParentTaskName, StringComparison.Ordinal)
                                || string.Equals(candidate.DisplayFileName, consumer.ParentTaskName, StringComparison.Ordinal))));

                /*
                 * 第二大步补建的入口任务，ParentTaskName 可能是**组基名**而不是父任务文件名
                 * （真机 23.5：入口任务是组边界补建的，父任务名不在任务表里）。
                 * ⇒ 再按既有事实找：谁的 ProducedFiles 里确实有这一组的片，谁就是产出方父任务。
                 */
                parent ??= tasks.FirstOrDefault(candidate =>
                    candidate != null
                    && !ReferenceEquals(candidate, consumer)
                    && candidate.ProducedFiles.Any(path =>
                        string.Equals(
                            FileNameHelper.GetArchiveBaseName(path),
                            groupBase,
                            StringComparison.OrdinalIgnoreCase)));

                if (parent == null
                    || parent.Outcome is TaskOutcome.Succeeded or TaskOutcome.Skipped or TaskOutcome.Cancelled
                    || (parent.Outcome == TaskOutcome.Failed && parent.Status != StatusText.VolumeMissing))
                {
                    continue;
                }

                if (!parent.ProducedFiles.Any(path =>
                        string.Equals(
                            FileNameHelper.GetArchiveBaseName(path),
                            groupBase,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                parent.Outcome = TaskOutcome.Succeeded;
                parent.Status = StatusText.ExtractSuccess;
                parent.IsVolumeDeficitDeferred = false;
                _volumeDeficitDeferred.Remove(parent);
                _volumeDeficitFinalPass.Remove(parent);

                AppendLog(
                    "INFO",
                    $"{parent.LogName}：产出分卷组「{groupBase}」的子任务「{consumer.LogName}」已成功，"
                    + "父任务不再按缺卷/部分完成收场。");
            }
        }

        /// <summary>
        /// **已验证组里的跟班片**：源片按「彻底删除」收掉（2026-10-18 用户 `&&` 口径，`22222222` 现场）。
        ///
        /// <para>跟班片（<see cref="ArchiveTask.IsVolumeGroupFollower"/> + 机器终态成功）的源片有时
        /// 不在“谁吐过片”那本账里（它没吐过东西、只是被借去拼组）⇒ `SettleGroupPieceProducers` 扫不到它，
        /// 源片一直留着。判据只要两条事实位：① 跟班 + 成功；② 同一组里有一个**可证完整 + 内容校验通过**
        /// 的消费方（`ResultCompletenessClassifier.Classify(consumer).AllowsSourceRemoval`）——
        /// 这就是 `&&` 的第二步。⛔ 组不完整 / 校验没过 / 判不出消费方 ⇒ 什么都不做；幂等（Done 跳过）。</para>
        /// </summary>
        private void SettleVerifiedGroupFollowers()
        {
            SourceHandlingMode handling = _spaceTightThisBatch
                ? SourceHandlingMode.DeleteInPlace
                : RunOptions?.SourceHandling ?? AppSettings.ParseSourceHandling(Settings?.SourceHandling);

            if (handling != SourceHandlingMode.DeleteInPlace)
            {
                return;
            }

            var logEntries = new List<(string Level, string Message)>();
            ArchiveTask[] tasks = SnapshotTaskTable(Tasks);

            foreach (ArchiveTask task in tasks)
            {
                if (task != null && task.IsVolumeGroupFollower)
                {
                    logEntries.Add((
                        "INFO",
                        $"{task.LogName}：跟班源片结算判据｜成功={task.Outcome == TaskOutcome.Succeeded}"
                        + $"｜已处理={task.SourcePackageMove}｜组名=「{task.SettledWithGroupUnitName}」"
                        + $"｜当前=「{task.CurrentPath}」｜原始=「{task.OriginalPath}」"));
                }

                /*
                 * ⛔ **已标 Done、但改名前的原始那一份还在盘上**（2026-10-18 `22222222` 现场）：
                 * `part1869删除2` 的 `CurrentPath` 被改写成组里的 `part1.rar`，早先那次清理删的是
                 * `part1.rar` 并把它标成 Done；可用户导入的那个**原始名字**（`OriginalPath`）还在
                 * —— 两个名字是同一份字节的硬链接，删一个不等于删干净。⇒ 只对“跟班里已证完整 +
                 * 原始那份确实还在”这一种，允许再走一次删源（`ignoreDoneState`），别的 Done 一律不动。
                 */
                bool recoveryOfOriginal = task != null
                    && task.SourcePackageMove == SourcePackageMoveState.Done
                    && !string.IsNullOrWhiteSpace(task.OriginalPath)
                    && File.Exists(task.OriginalPath);

                if (task == null
                    || task.IsContinuationTask
                    || !task.IsVolumeGroupFollower
                    || (task.SourcePackageMove == SourcePackageMoveState.Done && !recoveryOfOriginal)
                    || task.Outcome != TaskOutcome.Succeeded)
                {
                    continue;
                }

                string baseName = FileNameHelper.GetArchiveBaseName(task.CurrentPath ?? string.Empty);

                /*
                 * 消费方先从**跟班片自己记下的那个事实**找（`SettledWithGroupUnitName`，由
                 * `SettleOneConsumedPieceTask` 在“这一组由 X 解开”那一刻写死）—— 脏名字底下
                 * `GetArchiveBaseName` 可能给出两个不同的基名，按基名会漏（`22222222` 现场：
                 * `part1869删除2` vs `part391392885删除1`）。判不出再退回基名那一档。
                 */
                ArchiveTask? consumer = null;
                string settledWith = task.SettledWithGroupUnitName ?? string.Empty;

                if (settledWith.Length > 0)
                {
                    consumer = tasks.FirstOrDefault(candidate =>
                        candidate != null
                        && candidate.Outcome == TaskOutcome.Succeeded
                        && candidate.IsVolumeGroup
                        && candidate.IsVolumeComplete
                        && (string.Equals(candidate.DisplayFileName, settledWith, StringComparison.Ordinal)
                            || string.Equals(candidate.FileName, settledWith, StringComparison.Ordinal)));
                }

                if (consumer == null && baseName.Length > 0)
                {
                    consumer = tasks.FirstOrDefault(candidate =>
                        candidate != null
                        && candidate.Outcome == TaskOutcome.Succeeded
                        && candidate.IsVolumeGroup
                        && candidate.IsVolumeComplete
                        && string.Equals(
                            FileNameHelper.GetArchiveBaseName(candidate.CurrentPath ?? string.Empty),
                            baseName,
                            StringComparison.OrdinalIgnoreCase));
                }

                if (consumer == null || !consumer.IsVolumeComplete)
                {
                    continue;
                }

                ResultCompletenessVerdict verdict = ResultCompletenessClassifier.Classify(consumer);

                if (!verdict.AllowsSourceRemoval)
                {
                    continue;
                }

                /*
                 * 组任务的删除清单只装 `VolumePaths`；改名前的**原始那一份**不在里面，而这一单自己的
                 * 校验又被跟班收场清过 ⇒ `BuildTargetList` 不会把 `OriginalPath` 放进去（`22222222` 现场：
                 * `part1869删除2` 的当前路径是组里的 `part1.rar`，原始名字还在盘上）。
                 * ⇒ 用一个只代表“这一份原始文件”的临时任务走**同一个删除出口**（⛔ 不另写删除代码），
                 * 并沿用跟班 + 消费方 verdict 那两道事实。
                 */
                ArchiveTask purgeTask = task;
                string originalPath = task.OriginalPath ?? string.Empty;
                bool purgeOriginalOnly = task.IsVolumeGroup
                    && !string.IsNullOrWhiteSpace(originalPath)
                    && File.Exists(originalPath);

                if (purgeOriginalOnly)
                {
                    purgeTask = new ArchiveTask(originalPath)
                    {
                        IsVolumeGroupFollower = true
                    };
                }

                PurgeLayerSourcePackage(
                    purgeTask,
                    verification: null,
                    commit: null,
                    logEntries,
                    LayerPurgeTrigger.SourceHandlingDelete,
                    reclaimVerdict: verdict,
                    ignoreDoneState: recoveryOfOriginal || purgeOriginalOnly);

                if (purgeOriginalOnly && !File.Exists(originalPath))
                {
                    // 原始那一份已经删掉 ⇒ 把真任务也记成“处理过”，避免后续每轮再扫一遍。
                    task.SourcePackageMove = SourcePackageMoveState.Done;
                }
            }

            foreach ((string level, string message) in logEntries)
            {
                AppendLog(level, message);
            }
        }

        /// <summary>
        /// 这一单所在的那一组，**是不是正在被本批另一单解**（而它还没收尾）⇒ 这一单先不落结论。
        /// 判据只有两条事实位：① 组基名在那份"谁在解"的账上；② 那位消费方**还没有机器终态**。
        /// </summary>
        private bool TryGetGroupConsumerPending(ArchiveTask task, out string consumer)
        {
            consumer = string.Empty;

            string baseName = FileNameHelper.GetArchiveBaseName(task.CurrentPath ?? string.Empty);

            if (baseName.Length == 0
                || !_groupConsumerByName.TryGetValue(baseName, out string? name)
                || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            if (string.Equals(name, task.FileName, StringComparison.Ordinal))
            {
                return false;   // 自己就是那个消费方
            }

            ArchiveTask? owner = SnapshotTaskTable(Tasks).FirstOrDefault(
                candidate => candidate != null && string.Equals(candidate.FileName, name, StringComparison.Ordinal));

            bool unfinished = owner == null
                || owner.Outcome is not (TaskOutcome.Succeeded
                    or TaskOutcome.Failed
                    or TaskOutcome.PartiallyCompleted
                    or TaskOutcome.Cancelled
                    or TaskOutcome.Skipped);

            if (!unfinished)
            {
                return false;
            }

            consumer = name;
            return true;
        }

        /// <summary>这一单的分卷，**是不是已经被本批另一个任务解出来了**（2026-10-05 真机第八批）。
        ///
        /// <para><b>现场</b>：一组跨盘 ZIP 的末片压在两层层层加密的 RAR 里、其余三片散在三个源目录里。
        /// 递归层用硬链接把那三片接过来、整组解开、结果发布成功 —— 可那三片自己那一单在批末仍然
        /// 如实报「分卷缺失」⇒ 汇总写成「成功 1 / 失败 3」，批末还指路"把缺的那几卷放到同一个目录里"
        /// （用户按这条去搬，只会白忙一场：内容已经在出来了）。</para>
        ///
        /// <para>判据只有两件事，都是**事实位**：① 这一单自己（或账上那几卷）出现在
        /// <see cref="_consumedVolumeSources"/> 里（写入点唯一 = 递归结果带回来的那份名单）；
        /// ② 那个消费方这一批**真的成功了**（机器终态）。两条都成立 ⇒ 按跟班卷处理。</para>
        /// </summary>
        private bool TryResolveConsumedByAnotherTask(ArchiveTask task, out string consumerName)
        {
            consumerName = string.Empty;

            if (task == null || _consumedVolumeSources.Count == 0)
            {
                return false;
            }

            /*
             * ===== 判据 = **这一单自己那一片**有没有进过那份"借片账" =====
             *
             * ⛔ 比的是**同一份文件**，⛔ 不是路径字符串（真机 CCCC 2026-10-06 20:55，用户原话：
             * 「为什么文件真正的名字不看」）：接片那一档在"这一组该在的那一层"另起了一个**零字节硬链接**
             * （`…\111\111\111.z03`），而用户那一份还在他自己那个目录里（`…\111(4)\111.z03`）
             * —— 两条路径指着**同一份字节**。账里记的可能只是其中一条 ⇒ 按路径比永远对不上
             * ⇒ 那一单落到「判不出 ⇒ 什么都不做」，源片一直留着。
             */
            var ownPaths = new List<string>(EnumerateTaskPaths(task));
            string ownOnDisk = SourcePackageMover.ResolveOwnFileOnDisk(task);

            if (ownOnDisk.Length > 0)
            {
                ownPaths.Add(ownOnDisk);
            }

            foreach ((string piecePath, string ownerName) in _consumedVolumeSources.ToList())
            {
                bool mine = false;

                foreach (string path in ownPaths)
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    if (SafePathHelper.PathEquals(path, piecePath) || FileIdentity.IsSamePhysicalFile(path, piecePath))
                    {
                        mine = true;
                        break;
                    }
                }

                if (!mine || string.IsNullOrWhiteSpace(ownerName))
                {
                    continue;
                }

                /*
                 * ⚠ **判据输入**（2026-10-11，真机 EEE 定案用）：这一档一旦命中，调用方就**清落点 / 清校验 /
                 * 落「跟班卷」**，可"到底是哪一条账目匹配上的"原来一个字都不写 —— 于是真机 EEE 只看到
                 * `111.part1.rar` 被算成跟班卷，看不到它被**哪一份文件**（物理同一 / 同一条路径）匹配上的。
                 * 按"判不出就把判据输入写进日志"那条规矩补一行 WARN。⛔ 只写事实，不改判据。
                 */
                AppendLog(
                    "WARN",
                    $"借片账命中：「{FileNameHelper.GetFileName(task.CurrentPath)}」这一单自己的文件与账上那一片"
                    + $"（{piecePath}）是同一份物理文件（或同一条路径）⇒ 按已由「{ownerName}」接手收场"
                    + $"｜判据输入：本单路径={task.CurrentPath}，匹配上的账目=「{piecePath}」→ 接手方「{ownerName}」");

                ArchiveTask? owner = null;

                foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
                {
                    if (candidate == null || !string.Equals(candidate.FileName, ownerName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    /*
                     * ⛔ **借片的那一单不是消费方**（真机 CCCC 2026-10-06 实测）：批末补判会把补齐了那一单的
                     * `CurrentPath` 改到入口包上 ⇒ 它与真正的消费方**撞名**（两个都叫 `111.zip`）。
                     */
                    if (IsBorrowedPieceTask(candidate))
                    {
                        continue;
                    }

                    owner = candidate;
                    break;
                }

                if (owner == null || owner.Outcome != TaskOutcome.Succeeded)
                {
                    continue;
                }

                consumerName = ownerName;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 这一单**自己那一片借给过别的单**吗（判据只有一条：它自己的某条路径是那份"借片账"的**键**）。
        ///
        /// <p>为什么要它：那份账记的消费方是文件名，而"补齐之后起点被改到入口包上"的那一单会与真正的
        /// 消费方**撞名** ⇒ 按名字回查必须先认出"这是借片的那一单，不是消费方"（真机 CCCC 2026-10-06）。</p>
        /// </summary>
        private bool IsBorrowedPieceTask(ArchiveTask task)
        {
            if (task == null || _consumedVolumeSources.Count == 0)
            {
                return false;
            }

            /*
             * ⚠ **只问"它自己那一份"两条路径**（`OriginalPath` / `CurrentPath`），⛔ **不许连 `VolumePaths` 一起问**：
             * `VolumePaths` 是"这一组那几卷"的账，里面有**别的单**的片（真机：消费方 `111.zip` 那一单的
             * `VolumePaths` 里就有 `111.z0删除2/3`）—— 一起问会把**真正的消费方**误判成"借片的那一单"，
             * 于是谁也当不成消费方（实测踩到）。
             */
            foreach (string path in EnumerateOwnPaths(task))
            {
                if (!string.IsNullOrWhiteSpace(path) && _consumedVolumeSources.ContainsKey(path))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 这一单**自己那一份**的两条路径（<c>OriginalPath</c> 在前、<c>CurrentPath</c> 在后；去重，且**不含**分卷账）。
        /// </summary>
        private static IEnumerable<string> EnumerateOwnPaths(ArchiveTask task)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(task.OriginalPath) && seen.Add(task.OriginalPath))
            {
                yield return task.OriginalPath;
            }

            if (!string.IsNullOrWhiteSpace(task.CurrentPath) && seen.Add(task.CurrentPath))
            {
                yield return task.CurrentPath;
            }
        }

        /// <summary>
        /// 这一单自己的全部路径（<c>OriginalPath</c> + <c>CurrentPath</c> + 账上那几卷；去重）。
        ///
        /// <p>⚠ <b>`OriginalPath` 必须排在**最前**（2026-10-06 真机 CCCC 实测）</b>：批末补判那一站会把
        /// 补齐了的这一单的 <c>CurrentPath</c> 改到**入口包**（跨盘 ZIP 的末片 `111.zip`）上 ⇒
        /// 拿 <c>CurrentPath</c> 去问"这一单自己那一片在不在借片账里"必然问不出来（那是入口包、不是它那一片），
        /// 源片就留在盘上了（真机盘上实测：`111.z0删除3` 被处理了，`111.z0删除2` 留在原处）。
        /// `OriginalPath` 是**最初导入路径**，搬到其余物之前一直指向它自己那一片
        /// （搬运成功后会跟着更新，见 <c>ExecuteSourcePackageMove</c>）。</p>
        /// </summary>
        private static IEnumerable<string> EnumerateTaskPaths(ArchiveTask task)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(task.OriginalPath) && seen.Add(task.OriginalPath))
            {
                yield return task.OriginalPath;
            }

            if (!string.IsNullOrWhiteSpace(task.CurrentPath) && seen.Add(task.CurrentPath))
            {
                yield return task.CurrentPath;
            }

            foreach (string path in task.VolumePaths ?? (IReadOnlyList<string>)Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
                {
                    yield return path;
                }
            }
        }

        /// <summary>
        /// 这一单现在**能不能**凑出完整的一组（只读盘上事实）：候选 = 账上那几卷 + 自己那一层 +
        /// 找卷窗口，交给既有出口 <see cref="RebuildVolumeGroup"/> 归组；补齐且第 1 卷真的在盘上 ⇒ 返回那一组。
        /// 判不出（归不出组 / 仍然不完整 / 第 1 卷不在）⇒ null（⛔ 什么都不做）。
        /// </summary>
        /// <param name="preferDirectory">
        /// 另外再看一层（用户 2026-10-06：「你把分卷移过来不就是了吗」）：这一组**入口包所在的那一层**
        /// （<c>ResolveBatchGroupDirectory</c> 的回答）。收片那一档已经把散着的片按规范卷名接到那一层了，
        /// 而这一单自己那一层（散着一片的那一层）永远凑不齐 —— 只看自己那一层就会得出
        /// "仍然缺卷"这种**会把人指错**的结论。判据本身一个字没放宽：补齐与否仍然只由
        /// <see cref="VolumeGroupDetector.Group"/> 的"完整 / 卷号连续 / 除末片外等大"回答，
        /// ⛔ 这里只是**把候选池看宽一层**（多喂一个目录）。
        /// </param>
        private static VolumeGroup? ResolveCompleteGroupFromDisk(ArchiveTask task, string? preferDirectory = null)
        {
            try
            {
                var extra = new List<string>();
                string? directory = Path.GetDirectoryName(task.CurrentPath ?? string.Empty);

                if (!string.IsNullOrWhiteSpace(directory))
                {
                    extra.AddRange(CollectVolumeFilesInDirectory(directory));
                }

                if (!string.IsNullOrWhiteSpace(preferDirectory) && Directory.Exists(preferDirectory))
                {
                    extra.AddRange(CollectVolumeFilesInDirectory(preferDirectory));
                }

                foreach (VolumeCandidate candidate in VolumeNameRepair.EnumerateVolumeCandidatesNearby(task.CurrentPath))
                {
                    if (candidate != null && !string.IsNullOrWhiteSpace(candidate.Path))
                    {
                        extra.Add(candidate.Path);
                    }
                }

                VolumeGroup? group = RebuildVolumeGroup(task, extra);

                if (group == null || !group.IsComplete || string.IsNullOrWhiteSpace(group.FirstVolumePath))
                {
                    return null;
                }

                if (!File.Exists(group.FirstVolumePath))
                {
                    return null;
                }

                /*
                 * ⛔ **账上点过名的缺卷必须真的出现**才叫"补齐了"。
                 *
                 * 为什么不能只看 `group.IsComplete`：命名里**没有"共几卷"这个信息**
                 * （`VolumeGroupDetector` 类注释第 2 条），所以"只有一个 `.001`"在名字上看起来
                 * 就是一组完整的卷 —— 而账上明明白白记着"缺 `.002`"。
                 * 少了这一条，真机那种"缺一卷"的包会被当成"已经补齐"直接开跑
                 *（2026-10-05 复核逮到：`RealMachineDefectFixesTests.缺卷预检拦下_机器终态必须是失败` 当场变红）。
                 */
                foreach (string missing in task.MissingVolumeNames)
                {
                    if (string.IsNullOrWhiteSpace(missing))
                    {
                        continue;
                    }

                    bool appeared = group.Volumes.Any(v => string.Equals(
                        SafeFileNameOf(v.Path),
                        SafeFileNameOf(missing),
                        StringComparison.OrdinalIgnoreCase));

                    if (!appeared)
                    {
                        return null;
                    }
                }

                return group;
            }
            catch
            {
                return null;
            }

            static string SafeFileNameOf(string? path) =>
                Path.GetFileName(SafePathHelper.GetFullPathSafe(path ?? string.Empty));
        }

    }
}
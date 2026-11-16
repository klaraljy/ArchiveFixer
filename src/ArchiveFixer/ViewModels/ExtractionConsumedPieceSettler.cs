using ArchiveFixer.BindingModels;
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
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>已消费源片的收场：真删、去重落地别名、单片结算、组源结算、同组内消费方解析（从 ExtractionCoordinator.ConsumedSettlementStep.cs 搬出）。</summary>
internal sealed class ExtractionConsumedPieceSettler
{
    private readonly ExtractionCoordinator _host;

    internal ExtractionConsumedPieceSettler(ExtractionCoordinator host)
    {
        _host = host;
    }

    /// <summary>
    /// **真删**这一组的那几份（调用方已经过完全部门槛，含半套分卷那道闸门）。
    ///
    /// <para>执行体仍只有 <see cref="Storage.RecycleBinService"/> 一个出口
    /// （<c>AllowedRoot</c> = 这一层的目录）；删不掉只写 WARN，⛔ 不动任何结论、⛔ 不写回任务账。</para>
    /// </summary>
    internal void DeleteHarvestTargets(ArchiveTask consumer, List<string> targets)
    {
        string reason = RestItemPurger.SettledPieceCopyPurgeReason;
        bool cancelled = _host.IsStopping || _host._operationCts?.IsCancellationRequested == true;

        if (cancelled)
        {
            return;
        }

        var service = new RecycleBinService(_host.Source.RestDeleteExecutor, null, null);
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

                var plan = new DeletionPlan
                {
                    Targets = new[] { path },
                    AllowedRoot = directory,
                    DeletesEverything = true,
                    Cancelled = cancelled,
                    Reason = reason
                };

                DeleteResult result = CleanupExecutor.ExecuteDeletion(
                    plan,
                    p => service.Delete(
                        new DeleteRequest(p.Targets[0], p.Reason),
                        new DeleteOptions
                        {
                            AllowedRoot = p.AllowedRoot,
                            UserConfirmed = true,
                            Mode = DeleteMode.Permanent,
                            Reason = p.Reason
                        }));

                if (result.SuccessCount > 0)
                {
                    deleted++;

                    _host.AppendLog(
                        "INFO",
                        $"{consumer.LogName}：这一组已经解开，它这一份卷 {name} 按「删除操作」彻底删除"
                          + $"（{result.Outcomes.FirstOrDefault()?.EntryCount ?? 0} 个条目 / "
                          + $"释放 {TaskSpaceEstimate.FormatSize(result.Outcomes.FirstOrDefault()?.FreedBytes ?? 0)}，不进回收站）。");
                }
                else
                {
                    _host.AppendLog(
                        "WARN",
                        $"{consumer.LogName}：这一组的卷 {name} 这次没删成 —— "
                        + $"{result.Outcomes.FirstOrDefault()?.Message ?? result.Message}（内容物不受影响，这一份仍在原处）。");
                }
            }
            catch (Exception ex)
            {
                // 删东西绝不许把已经成立的结论拖成异常：落成"没删成"，别的什么都不变。
                _host.AppendLog("WARN", $"{consumer.LogName}：收这一组卷时出现意外错误：{ex.Message}");
            }
        }

        if (deleted > 0)
        {
            _host.AppendLog("INFO", $"{consumer.LogName}：这一组的卷已按「删除操作」收掉 {deleted} 份（这一层的过程物，不进回收站那一档）。");
        }
    }

    /// <summary>
    /// 产出方落点子目录里、与已被接手的片同一物理文件的多余名：只收名字不删字节
    /// （链接数 &gt; 1；⛔ 不碰 adopted 目标本身、用户源、`其余物`/工作区）。
    /// `28282828` 的 `aaa-W2-1\aaa-W2-0\aaa-L2.zip` 就是这一档。
    /// </summary>
    internal void PurgeRedundantLandedAliases(string groupBaseName)
    {
        if (string.IsNullOrWhiteSpace(groupBaseName))
        {
            return;
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var adoptedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in _host._volumeLedgerState.AdoptedLandedPieces)
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
                    || _host.PieceAdoption.IsAnyTaskSourcePath(file)
                    || adoptedPaths.Contains(SafePathHelper.GetFullPathSafe(file))
                    || !FileIdentity.TryGetLinkCount(file, out uint linkCount)
                    || linkCount <= 1)
                {
                    continue;
                }

                try
                {
                    FileOperations.Delete(file);

                    _host.AppendLog(
                        "INFO",
                        $"{FileNameHelper.GetFileName(file)}：产出方落点子目录里这一份与已接手的片是同一个文件 —— "
                        + "收掉这个多余的名字，一个字节都不丢（其余名字还在）。");
                }
                catch (Exception ex)
                {
                    _host.AppendLog(
                        "WARN",
                        $"{FileNameHelper.GetFileName(file)}：多余名字没能收掉（{ex.Message}）");
                }
            }
        }
    }

    /// <summary>
    /// **一单"已经借给本批另一单用过的源片/片"的收场 + 搬运**（唯一实现，两处转调：
    /// <see cref="_host.SettleGroupPieceProducers"/> 遍历那份"吐出过片"的账，
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
    internal void SettleOneConsumedPieceTask(
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
             * 「谁吐出过哪一组的片」这本账的键是**基名**（`_volumeLedgerState.GroupPieceProducers`），而同一个基名底下
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
             * ⛔ 2026-11-01 真机：字节补齐组的尾片基名与组基名**必然不同**
             * （`风景01` 组里坐着 `风景02.mp4`），按"族+基名"判同组永远不成立。
             * 组是**显式片清单**成立的 ⇒ 先问组账：这一片的全路径是不是消费方那一组的片？
             */
            VolumeGroupUnit? consumerUnit = _host._volumeGroupService.FindForTask(
                consumer.CurrentPath,
                consumerGroupName);
            bool inConsumerGroup = consumerUnit != null
                && consumerUnit.Pieces.Any(pieceFact =>
                    !string.IsNullOrWhiteSpace(pieceFact.Path)
                    && SafePathHelper.PathEquals(
                        pieceFact.Path,
                        producedPiecePath ?? piece.CurrentPath ?? string.Empty));

            /*
             * ⛔ **名字认不出族时，用底层内容卡回答**（用户 2026-10-18 的 `&&` 口径：
             * 第一大步的片是不是这一族的成员，看内容；名字只是辅助）。
             * 现场（`19191919`）：`bbb-L2.zip-658` 的名字读不出 `.zip.00N` 族 ⇒ 老判据拒绝跟班收场
             * ⇒ 它的原包一直留着；而它的内容卡（Zip + 卷成员）与接手方同一族。
             * 判据：① 两边**内容格式**相同且不是 Unknown；② 这一片的内容不是 `ProvablyNotVolumeMember`；
             * ③ 基名与组基名有关（既有 `BaseNameRelatedToGroup`）。三条都成立才算同一族；
             * 判不出 ⇒ 老口径（拒绝，不清、不删）。
             */
            bool sameGroup = inConsumerGroup
                || (pieceName.Length > 0
                    && consumerGroupName.Length > 0
                    && LegacyVolumeNames.BelongsToSameGroup(pieceName, consumerGroupName));

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
                _host.AppendLog(
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

                _host.AppendLog("INFO", consumedNote);
            }
        }

        /*
         * 这一单的**其余物**也要按档处理（用户 2026-10-06 两次追问的那 48.35 MB）：
         * 它只出过程物、那份过程物已被这一组那次**可证完整**的解压接手 ⇒ 它的其余物不再有用了。
         */
        _host.PurgePassThroughRestOfSettledProducer(piece, consumer, groupBaseName);

        /*
         * ⛔ **这一趟没删成就解除"已处理"标记、排进批末重试**（真机 2026-10-06 20:07 实测）：
         * 这一刻半套分卷那道闸门**拦得对** —— 同组的 `111.z01`（名字不同的一片）还在成品树里，
         * 删掉装着**入口包**的其余物就等于把这一组拆开。可那一组随后就解完了
         * （`彻底删除：111\其余物（7 项 / 1.07 GB）`），而旧写法把它钉在"已处理"上
         * ⇒ `111\111\其余物\111.zip`（48.35 MB）从此没有任何人再看一眼。
         */
        if (!string.IsNullOrWhiteSpace(piece.RestDirectoryPath) && Directory.Exists(piece.RestDirectoryPath))
        {
            _host._volumeLedgerState.PassThroughRestHandled.Remove(piece);
            _host.VolumeLedger.RememberPendingPassThroughRest(piece, consumer, groupBaseName);
        }

        if (!moveSources || !_host._volumeLedgerState.ProducerSourcesCollected.Add(piece))
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
         * 一键处理的「本次选项」放在 `_host.RunOptions` 里（同一表达式在别处用了两处：`:1235` / `:2881`），
         * 而这一档原来直接读 `_host.Settings.SourceHandling` ⇒ 用户在本次选项里选了「放入其余物」，
         * 收场这条路却按默认档「留在原地」判 ⇒ 源片一个字节都不搬
         * （同一批里 `111.zip` 那一单却搬了 4 个 —— 它走的是读 `_host.RunOptions` 的那条路）。
         * 这就是"一处改动要落到每一条路径上"那条规矩的反面现场。
         */
        SourceHandlingMode sourceHandling = _host._spaceTightPolicy.Enabled
            ? SourceHandlingMode.DeleteInPlace
            : _host.RunOptions?.SourceHandling ?? AppSettings.ParseSourceHandling(_host.Settings?.SourceHandling);

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
         * 永久删那一支（唯一出口 `_host.PurgeLayerSourcePackage`）。
         *
         * ⛔ **闸门一条都没绕过**：这一支不喂本任务的 verification / commit（这里手上没有"这一单自己定稿"
         * 的事实），改喂**接手方那一单的完整性结论**（`ResultCompletenessClassifier.Classify(consumer)`）——
         * 这正是用户 2026-10-18 拍板的 `&&`：第一大步成功 + 第二大步（组）可证完整且内容校验通过，
         * 才允许按「彻底删除」删这个原包；判不出 / 第二大步没成 ⇒ `_host.PurgeLayerSourcePackage` 一个字节都不动。
         */
        _host.Source.PurgeLayerSourcePackage(
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
    /// <para>与 <see cref="_host.SettleGroupPieceProducers"/> 的分工：那一档管"**吐出过片**的那几单"
    /// （它自己解到一半停了、源包留在盘上）；这一档管"**从没吐过东西、只是一片散着的续卷**"的那几单
    /// —— 它们整组由别人解开，按跟班卷收场，可源片一直没人处理。</para>
    ///
    /// <para>判据只有两条事实位（<see cref="_host.TryResolveConsumedByAnotherTask"/> 用的**同一对**）：
    /// ① 这一单的路径在既有那份"借来用过的源片"账 <see cref="_host._volumeLedgerState.ConsumedVolumeSources"/> 里；
    /// ② 那位消费方**真的成功了**（机器终态）。⛔ 不碰消费方自己、⛔ 幂等、⛔ 搬运仍只有既有出口。</para>
    /// </summary>
    /// <param name="logEntries">非空 ⇒ 日志先攒起来交给调用方统一落（批内并发路径要回 UI 线程）；空 ⇒ 这里直接落。</param>
    internal void SettleConsumedGroupVolumeSources(List<(string Level, string Message)>? logEntries)
    {
        if (_host._volumeLedgerState.ConsumedVolumeSources.Count == 0)
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
            bool consumedHit = _host.TryResolveConsumedByAnotherTask(task, out string consumerName);

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
                _host.VolumeLedger.RememberOneConsumedVolumeSource(ResolveOwnPiecePath(task), owner);
            }

            ArchiveTask? consumer = null;

            foreach (ArchiveTask candidate in VolumeNameNormalizeHandler.SnapshotTaskTable(_host.Tasks))
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
                _host._volumeLedgerState.VolumeDeficitDeferred.Remove(task);
                _host._volumeLedgerState.VolumeDeficitFinalPass.Add(task);
            }
        }

        if (collected != null)
        {
            foreach ((string level, string message) in collected)
            {
                _host.AppendLog(level, message);
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
    internal static string ResolveOwnPiecePath(ArchiveTask task)
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
    /// **这一单所在那一组的消费方是谁**（只读既有那份"谁在解"的账 `_host._volumeLedgerState.GroupConsumerByName`），
    /// 而且那位消费方**真的成功了**（机器终态）。判不出 / 还没成功 ⇒ 空串 ⇒ 调用方什么都不做。
    /// </summary>
    internal string ResolveSuccessfulGroupConsumerName(ArchiveTask task)
    {
        string baseName = FileNameHelper.GetArchiveBaseName(task.CurrentPath ?? string.Empty);

        if (baseName.Length == 0
            || !_host._volumeLedgerState.GroupConsumerByName.TryGetValue(baseName, out string? name)
            || string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        foreach (ArchiveTask candidate in VolumeNameNormalizeHandler.SnapshotTaskTable(_host.Tasks))
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
            if (ReferenceEquals(candidate, task) || _host.IsBorrowedPieceTask(candidate))
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
    /// （`_host.TryResolveConsumedByAnotherTask` 那条路给它们落的），既不在延迟名单里、也从没吐过片
    /// —— 只有"全表"才扫得到它们。代价可控：真正的判据是"这一单的路径在不在这份借片账里"，
    /// 绝大多数单第一条就 false；搬过的由 <c>_host._volumeLedgerState.ProducerSourcesCollected</c> 挡住，不会搬第二遍。</para>
    /// </summary>
    internal IEnumerable<ArchiveTask> EnumerateCandidateTasksForConsumedSettle()
    {
        var seen = new HashSet<ArchiveTask>();

        foreach (ArchiveTask task in _host._volumeLedgerState.VolumeDeficitDeferred.ToList())
        {
            if (task != null && seen.Add(task))
            {
                yield return task;
            }
        }

        foreach (List<(ArchiveTask Task, string PieceName)> producers in _host.PieceAdoption.SnapshotGroupPieceProducerLists())
        {
            foreach ((ArchiveTask producer, string _) in producers.ToList())
            {
                if (producer != null && seen.Add(producer))
                {
                    yield return producer;
                }
            }
        }

        foreach (ArchiveTask task in VolumeNameNormalizeHandler.SnapshotTaskTable(_host.Tasks))
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
        foreach (string piecePath in _host._volumeLedgerState.ConsumedVolumeSources.Keys.ToList())
        {
            foreach (ArchiveTask task in VolumeNameNormalizeHandler.SnapshotTaskTable(_host.Tasks))
            {
                if (task == null || seen.Contains(task))
                {
                    continue;
                }

                if (ExtractionCoordinator.EnumerateTaskPaths(task).Any(path => SafePathHelper.PathEquals(path, piecePath)))
                {
                    seen.Add(task);
                    yield return task;
                }
            }
        }
    }

}


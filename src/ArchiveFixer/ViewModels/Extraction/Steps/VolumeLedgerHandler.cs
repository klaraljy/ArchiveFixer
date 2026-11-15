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
/// 分卷账本记账（从 `ExtractionCoordinator.VolumeLedgerStep.cs` 搬出）：过路层其余物/组片消费/
/// 缺卷延期/测试钩子。
/// ⛔ 判据、日志顺序、账本写入点逐字沿用原实现；协调器只保留同名薄转发与三处最小 internal 访问器。
/// </summary>
internal sealed class VolumeLedgerHandler
{
    private readonly ExtractionCoordinator _host;

    internal VolumeLedgerHandler(ExtractionCoordinator host)
    {
        _host = host;
    }

    /// <summary>把"这次没删成"的那一单排进批末重试名单（⛔ 同一个生产者只排一次）。</summary>
    internal void RememberPendingPassThroughRest(ArchiveTask producer, ArchiveTask consumer, string groupBaseName)
    {
        foreach ((ArchiveTask Producer, ArchiveTask Consumer, string GroupBaseName) item in _host.PendingPassThroughRestForVolumeLedger)
        {
            if (ReferenceEquals(item.Producer, producer))
            {
                return;
            }
        }

        _host.PendingPassThroughRestForVolumeLedger.Add((producer, consumer, groupBaseName));
    }

    /// <summary>
    /// **批末重试**：所有链都跑完之后，再试一次"过路层其余物按档删"（唯一执行体不变 —— 仍转调
    /// <see cref="PurgePassThroughRestOfSettledProducer"/>，六道门槛一条不绕过）。判据全在盘上：
    /// 目录已经不在 ⇒ 从名单里划掉；还在 ⇒ 再试一次（还是不成 ⇒ 留在名单里，如实不动）。
    /// </summary>
    internal void RetryPendingPassThroughRestPurges()
    {
        foreach ((ArchiveTask Producer, ArchiveTask Consumer, string GroupBaseName) item in _host.PendingPassThroughRestForVolumeLedger.ToList())
        {
            if (string.IsNullOrWhiteSpace(item.Producer.RestDirectoryPath)
                || !Directory.Exists(item.Producer.RestDirectoryPath))
            {
                _host.PendingPassThroughRestForVolumeLedger.RemoveAll(pending => ReferenceEquals(pending.Producer, item.Producer));
                continue;
            }

            _host.PurgePassThroughRestOfSettledProducer(item.Producer, item.Consumer, item.GroupBaseName);
        }
    }

    /// <summary>
    /// 把"这一组**其余的片**"记进既有那份"借来用过的源片"账（消费方 = <paramref name="consumer"/>）。
    ///
    /// <para>⛔ 只记**同一组**、且不是消费方自己那一条；⛔ 一个字节都不动 —— 搬运与删除仍旧由既有出口
    /// 在消费方**真的成功**之后按那六道闸门办（见 <see cref="CollectConsumedSourcePiecesIntoRest"/>）。</para>
    /// </summary>
    internal void RememberGroupPiecesConsumedBy(ArchiveTask consumer)
    {
        /*
         * ⛔ 引用名必须来自**这一单自己**（`ResolveGroupReferenceName`），⛔ 不许写死 `<基名>.zip`：
         * 组一旦不是跨盘 ZIP 族（`X.partN.rar` / `X.7z.001` / `X.r00`），写死的引用名一个成员都匹配不上
         * ⇒ 这一组"借来用过的源片"账**一条都记不上** ⇒ 那些片永远不会按跟班卷收场（2026-10-11 统一）。
         */
        string tailName = ExtractionCoordinator.ResolveGroupReferenceName(
            consumer,
            FileNameHelper.GetArchiveBaseName(consumer.CurrentPath));

        foreach (ArchiveTask other in ExtractionCoordinator.SnapshotTaskTable(_host.Tasks))
        {
            if (other == null || ReferenceEquals(other, consumer))
            {
                continue;
            }

            string path = other.CurrentPath ?? string.Empty;

            if (path.Length == 0
                || !LegacyVolumeNames.BelongsToSameGroup(FileNameHelper.GetFileName(path), tailName))
            {
                continue;
            }

            _host._consumedVolumeSources[path] = consumer.FileName;
        }
    }

    /// <summary>
    /// 记**一条**"这一份源片借给了谁用"（与 <see cref="RememberGroupPiecesConsumedBy"/> 写的是同一份账）。
    /// 单独一条是为了"补记"那一档：批末才发现某一单的路径没进账，而它的组确实由某个已成功的消费方解开。
    /// </summary>
    internal void RememberOneConsumedVolumeSource(string? piecePath, string consumerName)
    {
        if (string.IsNullOrWhiteSpace(piecePath) || string.IsNullOrWhiteSpace(consumerName))
        {
            return;
        }

        _host._consumedVolumeSources[piecePath] = consumerName;
    }

    /// <summary>
    /// **把这一单登记进"缺卷留到最后再判"那份名单** —— 登记这件事的**唯一出口**
    /// （<see cref="RecordDeferredVolumeDeficit"/> 与递归中途撞缺卷那一档都走这里）。
    ///
    /// <para>⛔ 只做两件事：进名单 + 点亮显示/统计事实位
    /// （<see cref="ArchiveTask.IsVolumeDeficitDeferred"/>）。⛔ **不碰** <c>Status</c> /
    /// <c>ErrorMessage</c>：那两个字段是"装箱单"，发布链 / 定稿 / 源包处理都读它
    /// （见 `ApplyRecursionResult` 后面那段说明，实测在那一档改它会整组连入口都不再落地）。
    /// 解前预检那一档要连状态与文案一起写，由 <see cref="RecordDeferredVolumeDeficit"/> 补。</para>
    /// </summary>
    internal void RememberDeferredVolumeDeficit(ArchiveTask? task, string? stalledMemberPath = null)
    {
        if (task == null)
        {
            return;
        }

        if (!_host._volumeDeficitDeferred.Contains(task))
        {
            _host._volumeDeficitDeferred.Add(task);
        }

        /*
         * ⚠ 2026-10-13 困难批 `11111111`：**停下来的那一组不一定就是这一单自己那一组**
         * （入口包的每一片各自被另一条链解出来）。那种情况把"那一片"记下来 ——
         * 批末那一站要靠它去问**对的那个组**（否则只能拿这一单自己的包基名去问，问出来是空）。
         * ⛔ 判不出（没给 / 空串）⇒ 不记 ⇒ 批末照旧用这一单自己的包基名（老口径一个字节不改）。
         */
        if (!string.IsNullOrWhiteSpace(stalledMemberPath))
        {
            _host._deferredVolumeGroupMember[task] = stalledMemberPath!;
        }

        task.IsVolumeDeficitDeferred = true;
    }

    /// <summary>
    /// **批首只记缺口**（用户 2026-10-05 原话：「所以你开始就得突破所有的伪装和压缩，这种分卷找不到的
    /// 情况可以留在最后做」）：⛔ 不落 Failed、⛔ 不写"本次不开始"、⛔ 不跳过 —— 只记进名单。
    ///
    /// <para>不变量 7 **一个字都没放松**：这一单现在**确实没有开始解压**（一个字节都没动），
    /// 只是"还缺哪几卷"这句话留到这一批的解压都跑完之后再说 —— 那时缺的第 1 卷可能已经被同批另一个包
    /// 解出来、或者已经被收拢到位（真机 `B250135.7z.002` 16:29:33 就被判死，而它的 `.001`
    /// 正躺在同一个目录树里没被解出来）。</para>
    /// </summary>
    internal void RecordDeferredVolumeDeficit(ArchiveTask task, string missingNames)
    {
        if (task == null)
        {
            return;
        }

        RememberDeferredVolumeDeficit(task);

        /*
         * 显示/统计那一层的事实位（⛔ 不是状态）：①页那一格据此说"跳过（缺卷，等批末再判）"，
         * 批末计数据此按「跳过」数 —— 用户 2026-10-08 口径「没有到最后一步都是先跳过」。
         * 批末定稿（FinalizeDeferredVolumeDeficits）会把它清掉。
         */
        task.IsVolumeDeficitDeferred = true;

        /*
         * ⛔ **批中间不许顶着红色「分卷缺失」**（用户 2026-10-06 原话：
         * 「我说了不要显示红色的分卷缺失，你还没有压倒最后就跳过，你是听不懂吗」）。
         *
         * 批首只是"先把缺口记下来"：这一单**这一批确实还没开工**，但它既没失败也不该被判死
         * —— 缺的那几卷可能马上就被同批另一个包解出来（真机 `111.z03` 就是），
         * 也可能在批末"这一组其实已由别单解开"那一档按跟班卷收场。
         * ⇒ 状态用既有的**中性档** `等待解压`（不是 `分卷缺失`：那个进了红色映射与失败名单，
         * 在批中间亮出来就是一句会把用户指错的话）；"还缺哪几个"写在 `ErrorMessage` 里、
         * 日志里也有一条，信息一个字没少。批末那一站仍在缺 ⇒ 才由它落最终结论（跳过）。
         */
        task.Status = StatusText.WaitingExtract;
        task.ErrorMessage = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            StatusText.VolumeDeficitDeferredNoteFormat,
            missingNames);

        _host.AppendLog(
            "INFO",
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.VolumeDeficitDeferredLogFormat,
                task.FileName,
                missingNames));
    }

    /// <summary>
    /// 批量清账（单测用）。
    ///
    /// <para>为什么需要它：这几份账都是**批首清、批内攒**的，于是"批末那一站该不该给某几单收场"
    /// 这件事在单测里造不出中间态 —— 除非能像批内那样先把账写好、再把消费方的终态摆成成功。
    /// ⛔ 只做"写入既有那几份账"，⛔ 一点判据都不复制。</para>
    /// </summary>
    internal void ResetBatchLedgersForTests()
    {
        _host._volumeDeficitDeferred.Clear();
        _host._triedVolumeGroupSignatures.Clear();
        _host._gatheredUserSourcePieces.Clear();
        _host._deferredVolumeGroupMember.Clear();
        _host._byteBudgetLinkedSources.Clear();
        _host._scoredSlotAttempts.Clear();
        _host._scoredSlotLinks.Clear();
        _host._volumeDeficitFinalPass.Clear();
        _host.ClearGroupPieceProducers();
        _host._groupConsumerByName.Clear();
        _host._volumeGroupUnits.Clear();
        _host._consumedVolumeSources.Clear();
        _host._producerSourcesCollected.Clear();
        _host._adoptedLandedPieces.Clear();
        _host._groupLayerByBaseName.Clear();
        _host.VolumeStepTasksForVolumeLedger.Clear();
        _host._volumeGroupEntriesThisBatch.Clear();
        _host.SuffixRenamedSourcesForVolumeLedger.Clear();
    }

    /// <summary>单测用：把"这一片是借某个消费方用过的源片"记进既有那份账。</summary>
    internal void RememberConsumedVolumeSourceForTests(string piecePath, string consumerName)
    {
        if (string.IsNullOrWhiteSpace(piecePath) || string.IsNullOrWhiteSpace(consumerName))
        {
            return;
        }

        _host._consumedVolumeSources[piecePath] = consumerName;
    }

    /// <summary>单测用：把"这一组谁在解"记进既有那份账。</summary>
    internal void RememberGroupConsumerForTests(string baseName, string consumerName)
    {
        if (string.IsNullOrWhiteSpace(baseName) || string.IsNullOrWhiteSpace(consumerName))
        {
            return;
        }

        _host._groupConsumerByName[baseName] = consumerName;
    }

    /// <summary>单测用：把"这一单吐出了这一组的一片"记进既有那份账（写入点唯一 = <see cref="RememberGroupPieceProducer"/>）。</summary>
    internal void RememberGroupPieceProducerForTests(string baseName, ArchiveTask producer)
        => _host.RememberGroupPieceProducer(baseName, FileNameHelper.GetFileName(producer?.CurrentPath ?? string.Empty), producer);

    /// <summary>
    /// 单测用：把"**产出方那一份已经落地的片**被接进了这一组"记进这本账
    /// （写入点唯一 = <see cref="RememberAdoptedLandedPiece"/>，⛔ 判据一条都不复制：
    /// 落点根、过程物目录、源包路径三道闸门照旧全部生效）。
    ///
    /// <para>⚠ <paramref name="adoptionOwner"/> = **接片那一刻调用方给的那一单**，⛔ 不等于产出方：
    /// 真机上"批量收片"那条路给的是**被重判的那一单**（落点树里没有这一片）——
    /// 产出方由 `ResolveLandedPieceProducer` 按盘上事实认。</para>
    /// </summary>
    internal void RememberAdoptedLandedPieceForTests(
        ArchiveTask adoptionOwner,
        string groupBaseName,
        string piecePath)
        => _host.RememberAdoptedLandedPiece(piecePath, adoptionOwner, groupBaseName, renamedInPlace: false);

    /// <summary>单测用：把一单放进"缺卷留到最后再判"那份名单（写入点唯一 = <see cref="RecordDeferredVolumeDeficit"/>）。</summary>
    internal void RecordDeferredVolumeDeficitForTests(ArchiveTask task)
    {
        if (task == null)
        {
            return;
        }

        // ⛔ 走**真实那条路**（同一个出口）：批首记缺口 = 中性状态 + 缺口写进错误信息 + 一条日志。
        // 只把任务塞进名单的旧写法会让"批中间顶着红色的分卷缺失"这条口径**没有守门**。
        RecordDeferredVolumeDeficit(task, ExtractionCoordinator.DescribeMissingVolumeNames(task));
    }

    /// <summary>这本账现在有几组（并发安全）。</summary>
    internal int GroupPieceProducerCount
    {
        get
        {
            lock (_host._groupPieceProducersGate)
            {
                return _host._groupPieceProducers.Count;
            }
        }
    }
}

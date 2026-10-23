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
    /// <summary>分卷账本记账（过路层其余物/组片消费/缺卷延期/测试钩子；第 9 块，同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>把"这次没删成"的那一单排进批末重试名单（⛔ 同一个生产者只排一次）。</summary>
        private void RememberPendingPassThroughRest(ArchiveTask producer, ArchiveTask consumer, string groupBaseName)
        {
            foreach ((ArchiveTask Producer, ArchiveTask Consumer, string GroupBaseName) item in _pendingPassThroughRest)
            {
                if (ReferenceEquals(item.Producer, producer))
                {
                    return;
                }
            }

            _pendingPassThroughRest.Add((producer, consumer, groupBaseName));
        }

        /// <summary>
        /// **批末重试**：所有链都跑完之后，再试一次"过路层其余物按档删"（唯一执行体不变 —— 仍转调
        /// <see cref="PurgePassThroughRestOfSettledProducer"/>，六道门槛一条不绕过）。判据全在盘上：
        /// 目录已经不在 ⇒ 从名单里划掉；还在 ⇒ 再试一次（还是不成 ⇒ 留在名单里，如实不动）。
        /// </summary>
        private void RetryPendingPassThroughRestPurges()
        {
            foreach ((ArchiveTask Producer, ArchiveTask Consumer, string GroupBaseName) item in _pendingPassThroughRest.ToList())
            {
                if (string.IsNullOrWhiteSpace(item.Producer.RestDirectoryPath)
                    || !Directory.Exists(item.Producer.RestDirectoryPath))
                {
                    _pendingPassThroughRest.RemoveAll(pending => ReferenceEquals(pending.Producer, item.Producer));
                    continue;
                }

                PurgePassThroughRestOfSettledProducer(item.Producer, item.Consumer, item.GroupBaseName);
            }
        }

        /// <summary>
        /// 把"这一组**其余的片**"记进既有那份"借来用过的源片"账（消费方 = <paramref name="consumer"/>）。
        ///
        /// <para>⛔ 只记**同一组**、且不是消费方自己那一条；⛔ 一个字节都不动 —— 搬运与删除仍旧由既有出口
        /// 在消费方**真的成功**之后按那六道闸门办（见 <see cref="CollectConsumedSourcePiecesIntoRest"/>）。</para>
        /// </summary>
        private void RememberGroupPiecesConsumedBy(ArchiveTask consumer)
        {
            /*
             * ⛔ 引用名必须来自**这一单自己**（`ResolveGroupReferenceName`），⛔ 不许写死 `<基名>.zip`：
             * 组一旦不是跨盘 ZIP 族（`X.partN.rar` / `X.7z.001` / `X.r00`），写死的引用名一个成员都匹配不上
             * ⇒ 这一组"借来用过的源片"账**一条都记不上** ⇒ 那些片永远不会按跟班卷收场（2026-10-11 统一）。
             */
            string tailName = ResolveGroupReferenceName(
                consumer,
                FileNameHelper.GetArchiveBaseName(consumer.CurrentPath));

            foreach (ArchiveTask other in SnapshotTaskTable(Tasks))
            {
                if (other == null || ReferenceEquals(other, consumer))
                {
                    continue;
                }

                string path = other.CurrentPath ?? string.Empty;

                if (path.Length == 0
                    || !VolumeGroupDetector.BelongsToSameGroup(FileNameHelper.GetFileName(path), tailName))
                {
                    continue;
                }

                _consumedVolumeSources[path] = consumer.FileName;
            }
        }

        /// <summary>
        /// 记**一条**"这一份源片借给了谁用"（与 <see cref="RememberGroupPiecesConsumedBy"/> 写的是同一份账）。
        /// 单独一条是为了"补记"那一档：批末才发现某一单的路径没进账，而它的组确实由某个已成功的消费方解开。
        /// </summary>
        private void RememberOneConsumedVolumeSource(string? piecePath, string consumerName)
        {
            if (string.IsNullOrWhiteSpace(piecePath) || string.IsNullOrWhiteSpace(consumerName))
            {
                return;
            }

            _consumedVolumeSources[piecePath] = consumerName;
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
        private void RememberDeferredVolumeDeficit(ArchiveTask? task, string? stalledMemberPath = null)
        {
            if (task == null)
            {
                return;
            }

            if (!_volumeDeficitDeferred.Contains(task))
            {
                _volumeDeficitDeferred.Add(task);
            }

            /*
             * ⚠ 2026-10-13 困难批 `11111111`：**停下来的那一组不一定就是这一单自己那一组**
             * （入口包的每一片各自被另一条链解出来）。那种情况把"那一片"记下来 ——
             * 批末那一站要靠它去问**对的那个组**（否则只能拿这一单自己的包基名去问，问出来是空）。
             * ⛔ 判不出（没给 / 空串）⇒ 不记 ⇒ 批末照旧用这一单自己的包基名（老口径一个字节不改）。
             */
            if (!string.IsNullOrWhiteSpace(stalledMemberPath))
            {
                _deferredVolumeGroupMember[task] = stalledMemberPath!;
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
        private void RecordDeferredVolumeDeficit(ArchiveTask task, string missingNames)
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

            AppendLog(
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
            _volumeDeficitDeferred.Clear();
            _triedVolumeGroupSignatures.Clear();
            _gatheredUserSourcePieces.Clear();
            _deferredVolumeGroupMember.Clear();
            _byteBudgetLinkedSources.Clear();
            _scoredSlotAttempts.Clear();
            _scoredSlotLinks.Clear();
            _volumeDeficitFinalPass.Clear();
            ClearGroupPieceProducers();
            _groupConsumerByName.Clear();
            _volumeGroupUnits.Clear();
            _consumedVolumeSources.Clear();
            _producerSourcesCollected.Clear();
            _adoptedLandedPieces.Clear();
            _groupLayerByBaseName.Clear();
            _volumeStepTasks.Clear();
            _volumeGroupEntriesThisBatch.Clear();
        }

        /// <summary>单测用：把"这一片是借某个消费方用过的源片"记进既有那份账。</summary>
        internal void RememberConsumedVolumeSourceForTests(string piecePath, string consumerName)
        {
            if (string.IsNullOrWhiteSpace(piecePath) || string.IsNullOrWhiteSpace(consumerName))
            {
                return;
            }

            _consumedVolumeSources[piecePath] = consumerName;
        }

        /// <summary>单测用：把"这一组谁在解"记进既有那份账。</summary>
        internal void RememberGroupConsumerForTests(string baseName, string consumerName)
        {
            if (string.IsNullOrWhiteSpace(baseName) || string.IsNullOrWhiteSpace(consumerName))
            {
                return;
            }

            _groupConsumerByName[baseName] = consumerName;
        }

        /// <summary>
        /// 单测用：走一遍**「接片」那一档的判据**（⛔ 不复制任何判据：唯一执行体就是
        /// <see cref="TryAdoptUnresolvedVolumePiece"/>，这里只是把它露给测试项目）。
        ///
        /// <para>为什么需要这个出口：本轮新加的"内容证据可证不是分卷成员 ⇒ 不接"那一句在**协调器内部**，
        /// 没有这一档就只能靠真机环台验（而环台验的是"整条链的最终盘面"，分不出是哪一句挡下的）。
        /// 有了它，守门用例能直接断言"这一份没有被改名/搬走"。</para>
        /// </summary>
        internal bool TryAdoptUnresolvedVolumePieceForTests(string piecePath, ArchiveTask? owner, string? layerOverride = null)
            => TryAdoptUnresolvedVolumePiece(piecePath, owner, layerOverride);

        /// <summary>单测用：把"这一单吐出了这一组的一片"记进既有那份账（写入点唯一 = <see cref="RememberGroupPieceProducer"/>）。</summary>
        internal void RememberGroupPieceProducerForTests(string baseName, ArchiveTask producer)
            => RememberGroupPieceProducer(baseName, FileNameHelper.GetFileName(producer?.CurrentPath ?? string.Empty), producer);

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
            => RememberAdoptedLandedPiece(piecePath, adoptionOwner, groupBaseName, renamedInPlace: false);

        /// <summary>单测用：把一单放进"缺卷留到最后再判"那份名单（写入点唯一 = <see cref="RecordDeferredVolumeDeficit"/>）。</summary>
        internal void RecordDeferredVolumeDeficitForTests(ArchiveTask task)
        {
            if (task == null)
            {
                return;
            }

            // ⛔ 走**真实那条路**（同一个出口）：批首记缺口 = 中性状态 + 缺口写进错误信息 + 一条日志。
            // 只把任务塞进名单的旧写法会让"批中间顶着红色的分卷缺失"这条口径**没有守门**。
            RecordDeferredVolumeDeficit(task, DescribeMissingVolumeNames(task));
        }


        // ================================================================ 缺卷补救（手动指定缺失卷所在目录）

        /// <summary>本批已经为哪几个任务问过"缺失卷在哪"（同一个包重试时不再重复打扰）。</summary>
        private readonly HashSet<ArchiveTask> _volumeRepairAsked = new();

        // ================================================================
        // 「缺卷不许在批首一次判死」+ 开工前跨目录收卷（用户 2026-10-05 两条口径）
        // ================================================================

        /// <summary>
        /// 这一批里"手上有、但这一组还缺几卷"的任务（**批首只记缺口**那一档）。
        ///
        /// <para>口径（用户 2026-10-05 原话：「所以你开始就得突破所有的伪装和压缩，这种分卷找不到的情况
        /// 可以留在最后做」）：批首**不落 Failed、不写"本次不开始"、不跳过** —— 只把缺口记在这里；
        /// 等这一批的解压都跑完之后再判一次（那时缺的那几卷可能已经被解出来 / 被收拢到位）。
        /// 补齐了 ⇒ 照常跑这一组；仍然缺 ⇒ 到那时才如实报缺卷（不变量 7 一个字没放松）。</para>
        ///
        /// <para>寿命：一键处理的**同一条续解链跨轮有效**（第 2 轮起不清空 —— 第 1 轮没解出来的
        /// 第 1 卷很可能在第 2 轮才出现），下一次**用户发起**的批次开始时清空。</para>
        /// </summary>
        private readonly List<ArchiveTask> _volumeDeficitDeferred = new();

        /// <summary>
        /// 试开确认后建的**硬链接暂存组入口目录**（每一条都是 `<入口层>\.ArchiveFixer.work\order-<id>`）——
        /// 批末统一删（里面只有零字节链接，删名字不丢字节；⛔ 不碰任何用户/任务源）。
        /// </summary>
        private readonly List<string> _confirmedOrderStagingDirectories = new();

        /// <summary>
        /// 一键开始前给 KeepInPlace 源文件做的同卷硬链接快照（原路径 → 备份路径）。
        /// 收尾时按快照恢复被搬走/改名的源名；⛔ 不复制字节、不删输出。
        /// </summary>
        private readonly List<(string Original, string Backup)> _keepSourceSnapshots = new();
        private string _keepSourceSnapshotDirectory = string.Empty;

        /// <summary>
        /// 试开定序的组任务：**原来的落点**（暂存组入口会改 `task.CurrentPath`，但产物必须回这个落点）。
        /// 提取那一站按这个覆盖掉"从暂存路径算出来的"输出目录，⛔ 不让产物写进 `.ArchiveFixer.work`。
        /// </summary>
        private readonly Dictionary<ArchiveTask, string> _stagedGroupOutputPaths = new();

        /// <summary>
        /// **按字节数补片那一档用过的来源**（我亲手把它链接成了槽位名）—— 判据 ③ 要的那个
        /// "**这一份是程序产的**"事实位（2026-10-13，困难批 `1111`）。
        ///
        /// <para>为什么必须有它：那一档补进来的两片（`111-L2.7z` / `111-L2.7z-924`）名字读不出卷号
        /// ⇒ 判据 ③ 的路上**任何按名字的筛子都认不出它们属于这一组**（实测：`PurgeGroupLandedPieces`
        /// 的扫描筛子第一句 `TryGetVolumeIndex == null` 就把它们挡下）⇒ 收场没人认领 ⇒ ③红。
        /// 而"我亲手从这一棵树里把它链接成槽位名"这件事**本身就是证据**（来源只在各单的产出树里扫，
        /// ⛔ 不碰用户源目录）⇒ 记下来，收场那一档据此按档处理它自己那一份。</para>
        ///
        /// <para>⛔ 只读它自己的记录，⛔ 不放宽任何按名字的判据（那些判据对别的文件照旧一个字不改）。</para>
        /// </summary>
        private readonly List<(ArchiveTask Producer, string GroupBaseName, string SourcePath)> _byteBudgetLinkedSources = new();

        /// <summary>
        /// **打分分配试到第几版**（键 = 组基名 + 那一层；⛔ 只记账、不碰盘）。
        ///
        /// <para>用户口径（2026-10-14）：「第一次前面的为 `003` 后面 `006`，**不成功就换一边**……
        /// 这是概率匹配，而且**只有前面的试过多次仍然没有办法**时才用；**而且没有成功你还是得换回原来的
        /// 样子**」。⇒ 同一组同一层**再来一次** = 上一版没成 ⇒ 先**删掉我们建的那几个槽位链接**（回滚，
        /// 因为它们只是零字节硬链接，删掉就等于回到原样），再试下一版；两版都不成 ⇒ 不再动手、如实写日志。</para>
        /// </summary>
        private readonly Dictionary<string, int> _scoredSlotAttempts = new();

        /// <summary>这一组这一层**我们亲手建过**的槽位链接（回滚用；⛔ 只删我们自己建的那些名字）。</summary>
        private readonly Dictionary<string, List<string>> _scoredSlotLinks = new();

        /// <summary>
        /// 这一批里"**停在别人那一组**"的那几单 → 停下来的那一片（2026-10-13 困难批 `11111111` 取证）。
        ///
        /// <para><b>为什么要有它</b>：批末那一站问"这一组齐了没"用的是**这一单自己的包基名**
        /// （`ResolveBatchGroupDirectory`），所以 <see cref="IsStalledGroupOwnGroup"/> 只放"停的就是自己这一组"的单进名单。
        /// 可困难批的形状是：**入口包那一组的每一片各自被另一条链解出来** ——
        /// 逐字（`11111111` 运行的判据输入）：`入口包=222-L2.part1.rar，产出它的那一单=判不出`
        /// ⇒ `推不出「这一组该拼到哪一层」` ⇒ `接片一个字节都不动`；而产出这一片的单叫 `222-W0-1.zip`
        /// （族与基名都对不上 `222-L2`）⇒ 老的闸门把它挡在名单外 ⇒ **运行期实测**
        /// `RecheckDeferredVolumeDeficits` 那一刻 `_volumeDeficitDeferred.Count = 0`
        /// ⇒ `批末重判` 一次都没出现 ⇒ 三片永远散在三条链的落点里 ⇒ ①「内容物解出来」红。</para>
        ///
        /// <para>存的就是"批末该拿谁去问那一组"：那一片自己的路径（它的基名 = 那一组的基名，
        /// 它的名字 = 那一组的规范参照物）。⛔ 只读，⛔ 不改任何删除 / 搬运判据。</para>
        /// </summary>
        private readonly Dictionary<ArchiveTask, string> _deferredVolumeGroupMember = new();

        /// <summary>
        /// **本批"借来用过的用户源片" → 解出它的那一单**（唯一写入点 = 递归结果带回来那份名单，
        /// 见 <see cref="RecursiveExtractor.RecursionResult.ConsumedVolumeSources"/>）。
        ///
        /// <para>用途只有一个：那一单自己不该再报「分卷缺失」（内容已经出来了），按跟班卷收场
        /// （<see cref="TryResolveConsumedByAnotherTask"/>）。⛔ 它不改任何删除 / 搬运判据。</para>
        /// </summary>
        private readonly Dictionary<string, string> _consumedVolumeSources =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 这一批里"某一组由谁在解"（组基名 → 消费方任务名）——跨链收卷那一条用（真机第九批 CCCC）。
        ///
        /// <para>用途只有一件：同一组的**别的单**在这个消费方收尾之前**不许落任何结论**
        /// （否则会得到一句"分卷缺失、未开始解压"，而那一刻内容正在被解开）。</para>
        /// </summary>
        private readonly Dictionary<string, string> _groupConsumerByName =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 每一组的**一等事实**（批内只建一次；本轮只接“源层”这一项，owner/顺序暂不接线）。
        /// </summary>
        private readonly Dictionary<string, VolumeGroupUnit> _volumeGroupUnits =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 这一批里"哪一单吐出了某一组的片"（组基名 → 那一单 **+ 它吐出的那一片的名字**）——跨链收卷用（真机第九批 CCCC）。
        ///
        /// <para>用途：整组由别单解开、而且**可证完整**之后，这些"只是包装"的单也按**跟班卷**收场
        /// （它的内容已经全在解出来的那一组里了）—— 否则真机上 `111.rar` / `111(2)_.zip` 会一直留着，
        /// 用户当场就问"为什么这两个原包还留着"。</para>
        ///
        /// <para><b>⛔ 为什么必须连"那一片的名字"一起记</b>（2026-10-11 真机 EEE 定案）：这本账的键是**基名**，
        /// 而同一个基名底下可能同时存在**两族** —— EEE 的跨盘 ZIP 组 `111.z01/z02/z03/111.zip`，
        /// 与它解开之后得到的 RAR 对 `111.parts1.racr` / `111.partɾ2.ra??r`（引擎证实这一对含 5 个 mp4），
        /// 两族都叫 `111`。只按"这一单自己的名字"去比族 ⇒ 要么混算（把 RAR 那两卷记成 ZIP 组的片，
        /// 于是它们被静默清掉落点/校验 ⇒ 链条停住）、要么连合法的跟班卷一起拦掉
        /// （`111.rar` 自己叫 `.rar`、可它吐出的那一片是 ZIP 族的 `111.z01`）。
        /// ⇒ **判据必须拿"它吐出的那一片"的名字去比族**（`BelongsToSameGroup` = 族 + 基名）。</para>
        /// </summary>
        private readonly Dictionary<string, List<(ArchiveTask Task, string PieceName)>> _groupPieceProducers =
            new(StringComparer.OrdinalIgnoreCase);

        /*
         * ⛔ **这本账会被并发写**（2026-10-10 用带栈的临时探针逮到的真缺陷，不是推断）：
         * 每一单的解压各在自己的任务线程上跑，收尾那一档
         * （`PublishUnresolvedVolumePieces` → `RememberGroupPieceProducer`）会**同时**往这里写，
         * 而批末 / 收场那几处又在读（`.Keys` / `.Values` / `.Count` / `TryGetValue`）。
         * `Dictionary` 不是并发容器 ⇒ 实测抛出
         * `Operations that change non-concurrent collections must have exclusive access. …`，
         * 而那个异常**被 `TryGatherGroupPiecesInto` 的 catch 吞掉**（只留一句「跨链收卷跳过」）
         * ⇒ 那一片没被记成"某单吐出的片" ⇒ 跨链收卷静默跳过 ⇒ **整组有时凑齐、有时凑不齐**
         * （同一条守门用例 `ChainInnerVolumeRestoreTests.真机布局_…` 跑两次一红一绿，实测）。
         * 栈：`RememberGroupPieceProducer(:8891)` ← `PublishUnresolvedVolumePieces(:9577)`。
         *
         * ⇒ 读写一律过这把锁，**读的一方拿快照**（⛔ 不把活列表交出去）。
         * ⛔ 只加互斥，判据一个字不改。
         */
        private readonly object _groupPieceProducersGate = new();

        /// <summary>这些生产者的源包**已经交给既有出口搬过了**（幂等：搬一次就够，⛔ 不重复搬）。</summary>
        private readonly HashSet<ArchiveTask> _producerSourcesCollected = new();

        /// <summary>
        /// 这些"过路层"生产者的**其余物已经按档处理过了**（幂等：⛔ 不重复删、不重复写日志）。
        /// 见 <see cref="PurgePassThroughRestOfSettledProducer"/>（真机 CCCC 2026-10-06 的 48.35 MB）。
        /// ⚠ **没删成的会被移除**、排进 <see cref="_pendingPassThroughRest"/> 等批末重试。
        /// </summary>
        private readonly HashSet<ArchiveTask> _passThroughRestHandled = new();

        /// <summary>
        /// "这一刻还删不成"的过路层其余物（生产者 + 接手它的那一单 + 组基名）——批末再试一次。
        ///
        /// <para><b>真机 2026-10-06 20:07 现场</b>：收场那一刻同组的 `111.z01` 还在成品树里
        /// ⇒ 半套分卷那道闸门**拦得对**（删了就等于把这一组拆开）；等整组解完、那几片也搬走之后
        /// 就没有任何障碍了，可旧写法只试一次 ⇒ `111\111\其余物\111.zip`（48.35 MB）永远留着。</para>
        /// </summary>
        private readonly List<(ArchiveTask Producer, ArchiveTask Consumer, string GroupBaseName)> _pendingPassThroughRest = new();

        /// <summary>
        /// 这一批里"**产出方那一份已经落地的片**被接进了某一组"（唯一写入点
        /// <see cref="RememberAdoptedLandedPiece"/>；唯一读点是收场时的
        /// <see cref="PurgeGroupLandedPieces"/>）。
        ///
        /// <para><b>为什么必须单独记这一笔</b>（用户 2026-10-10 点名的那条"同一结构两种结果"）：
        /// 同一件事 —— 用户包解出一片、那一片属于组 `111` —— 在真机上出现两种盘面：
        /// · `111(2)_.zip` 解出的 `111.z01`：接片时它还在**过程物目录**里 ⇒ `ShouldMovePieceIntoGroup`
        ///   为真 ⇒ 走**移动**，产出方那边一个名字都不留；
        /// · `111(3).rar` 解出的 `111.z02`：接片之前它就已经**定稿落地**
        ///   （`…\111(3)\111(3)\111.z02`，日志行 123 早于行 183）⇒ 判据为假 ⇒ 只能**建硬链接**
        ///   ⇒ 组层那一份随后随其余物被彻底删除，**产出方这份 200 MiB 一直留在盘上**。</para>
        ///
        /// <para>⇒ 判据不该读"这一片**此刻恰好在哪**"（位置 + 时序），而该读**它是不是已经被整组接手**
        /// 这条事实 —— 这一笔就是那条事实：谁供给的、哪一组、那一份的路径、它的落点根在哪。
        /// ⛔ 只在"产出方自己的落点树里、且不在过程物目录里"的那一档才记（见
        /// <see cref="RememberAdoptedLandedPiece"/>：用户源目录里的片**永远不进这份账**）。</para>
        /// </summary>
        private readonly List<(ArchiveTask Producer, string GroupBaseName, string PiecePath, string OutputRoot)>
            _adoptedLandedPieces = new();

        /// <summary>
        /// **这一组已经拼到哪一层去了**（组基名 → 层目录；本批内有效）。
        ///
        /// <para>写点唯一 = <see cref="TryAdoptUnresolvedVolumePiece"/> **真的把这一片接进去之后**
        /// （那一刻层是它按入口包算出来的）；读点 = 「定稿那一档」那道
        /// "这一片已经由接片搬进这一组的落点层 ⇒ 产出方这一份是多余名，不落地"（见它调用处）。
        ///
        /// <para><b>为什么要它</b>（2026-10-11 实测）：那道闸门原来问的是
        /// <see cref="ResolveBatchGroupDirectory"/> 的 1 参版 —— 它当年靠"先到先得"那条兜底才答得出层，
        /// 兜底按用户口径删掉之后就**静默失效** ⇒ 定稿把工作区那一份又发布到产出方落点里
        /// （实测残留逐字 `…\out-…\111(2)_\111.z01`，一个字节不多、但**没人认领**）。
        /// 同一件事的真值只允许有一个出口 ⇒ 这里**记住**接片那一刻算出来的层，⛔ 不重算、⛔ 不看谁先到。</para>
        /// </summary>
        private readonly Dictionary<string, string> _groupLayerByBaseName = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// **这一批里哪些单是"第二大步"**（分卷阶段，只解一层 —— 用户 2026-10-11 轮次模型）。
        /// 写点唯一 = 批末那一组齐了、交 `toRun` 照常跑这一组的那一刻（见 `RememberVolumeStepTask`）；
        /// 读点唯一 = <see cref="IsSingleLayerRecursion(ArchiveTask?)"/>（三处递归档判据）。
        /// ⛔ 不改用户的 `递归档位（已删除）` 设置；本批结束随其它账一起清。
        /// </summary>
        private readonly HashSet<ArchiveTask> _volumeStepTasks = new();
        /// <summary>
        /// **这一批里"被判成是分卷"的那几组的入口包**（2026-10-11，轮次模型：第二大步收卷用）。
        /// 写点唯一 = `HarvestSolvedGroupVolumesForRound`（轮边界交下来的入口包）；读点唯一 = 它自己
        /// （每轮边界把记住的都再试一遍 —— 因为判出"这一组是分卷"的那一刻**改名与解压还没发生**）。
        /// ⛔ 不改任何判据；本批结束随其它账一起清。
        /// </summary>
        private readonly List<(string Directory, string ReferenceName)> _volumeGroupEntriesThisBatch = new();

        /// <summary>
        /// 已经在"批末补判"里跑过一遍的任务：⛔ 不许再记一次缺口（否则它会永远留在
        /// <see cref="_volumeDeficitDeferred"/> 里、永远不落结论）。</summary>
        private readonly HashSet<ArchiveTask> _volumeDeficitFinalPass = new();

        /// <summary>
        /// **"未识别"但可能是某个分卷组一片的那些**（唯一用途：批末用严格判据再检验一步）。
        ///
        /// <para>用户口径（2026-10-09 逐字）：「分卷和其他**未识别的东西统统跳过**到了最后再重新检验一步」。
        /// 它们**当场照旧落「已跳过」+ 机器终态收口**（守门
        /// <c>DeferredVolumeStatusTests.未识别的东西_只跳过_机器终态必须收口不许算未处理</c> 钉着这条），
        /// 这里只是"记一笔"，好在批末把它们**升级**成「分卷缺失」（当严格判据成立时）。</para>
        ///
        /// <para>⛔ 与"缺卷名单"（<c>_volumeDeficitDeferred</c>）**不是一回事**：那一份装的是
        /// "已确认是分卷、只是缺片"的单；装错了会把纯文本文件改判成「分卷缺失」（第一版就是这么错的）。</para>
        /// </summary>
        private readonly HashSet<ArchiveTask> _unrecognizedMaybePiece = new();

        // ================================================================
        // 开工前跨目录收卷（用户 2026-10-05 口径 1）
        // ================================================================



        // ================================================================
        // 「缺卷不许在批首一次判死」（用户 2026-10-05 口径 2）
        // ================================================================




        /// <summary>这本账现在有几组（并发安全）。</summary>
        private int GroupPieceProducerCount
        {
            get
            {
                lock (_groupPieceProducersGate)
                {
                    return _groupPieceProducers.Count;
                }
            }
        }












    }
}
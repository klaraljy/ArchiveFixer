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
    /// <summary>分卷交接登记与"这一份是不是本批某组的一片"（第 13 块，从主文件搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>第一大步的交接事实表（第二大步的组分析从这里取候选）。</summary>
        private readonly List<ArchiveFixer.Detection.VolumeHandoffEntry> _volumeHandoffEntries = new();

        /// <summary>
        /// **第一大步文件账本**（共用骨架）：闸门通过的每一份文件都登记，
        /// 包括内容 Unknown 的中间片；族 resolver 从这里取候选。
        /// </summary>
        private readonly FirstStepFileLedger _firstStepFileLedger = new();

        /// <summary>
        /// **边界解析管线**（共用骨架）：族 resolver 从这里注册；骨架阶段未注册任何族，
        /// 因此 `ResolveAllAsync` 返回空，既有行为一个字不变。
        /// </summary>
        private readonly VolumeBoundaryPipeline _volumeBoundaryPipeline = CreateVolumeBoundaryPipeline();

        private static VolumeBoundaryPipeline CreateVolumeBoundaryPipeline()
        {
            var pipeline = new VolumeBoundaryPipeline();

            // 7z / RAR 先接；ZIP 支线再接，⛔ 不在共享层写族特判。
            pipeline.Register(new SevenZipBoundaryResolver());
            pipeline.Register(new RarBoundaryResolver());
            pipeline.Register(new ZipSpannedBoundaryResolver());
            pipeline.Register(new ZipNumberedBoundaryResolver());

            return pipeline;
        }

        /// <summary>本批的交接事实（只读给第二大步/日志用）。</summary>
        internal IReadOnlyList<ArchiveFixer.Detection.VolumeHandoffEntry> VolumeHandoffEntries => _volumeHandoffEntries;

        /// <summary>边界解析管线（第二大步/测试用）。</summary>
        internal VolumeBoundaryPipeline VolumeBoundaryPipeline => _volumeBoundaryPipeline;

        /// <summary>本批的第一大步文件账本（只读给第二大步/日志用）。</summary>
        internal FirstStepFileLedger FirstStepLedger => _firstStepFileLedger;

        /// <summary>
        /// 第一大步闸门通过后登记账本（不判定分卷、不碰盘）。
        /// 同一路径在一轮里重复登记时保留后到的内容卡（改名/补片后事实会变）。
        /// </summary>
        internal FirstStepFileLedgerEntry RegisterFirstStepLedgerEntry(
            ArchiveTask task,
            ArchiveFixer.Detection.FileNatureCard card)
        {
            string path = task.CurrentPath ?? string.Empty;
            int? nameSlot = ArchiveFixer.Detection.VolumeGroupDetector.TryGetVolumeIndex(
                FileNameHelper.GetFileName(path));

            ZipVariant zipVariant = ResolveZipVariant(card);

            FirstStepFileLedgerEntry entry = FirstStepFileLedgerEntry.FromCard(
                card,
                task.LogName,
                task.IsContinuationTask
                    ? ArchiveFixer.Detection.VolumeHandoffSource.ChainProduced
                    : ArchiveFixer.Detection.VolumeHandoffSource.UserImported,
                gatePassed: true,
                rarVariant: card.RarVariant,
                zipVariant: zipVariant,
                nameSlot: nameSlot);

            return _firstStepFileLedger.AddOrReplace(entry);
        }

        /// <summary>
        /// ZIP 两族的**初步**变体（只读内容卡现有的三件事；RAR/7z 不在这里判）。
        /// 最终族结论仍由 <see cref="VolumeFamilyArbiter"/> 给。
        /// </summary>
        private static ZipVariant ResolveZipVariant(ArchiveFixer.Detection.FileNatureCard card)
        {
            if (card.Format != ArchiveFixer.Detection.VolumeContentFormat.Zip)
            {
                return ZipVariant.Unknown;
            }

            if (card.Membership == ArchiveFixer.Detection.VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                && card.VolumeNumber is int number
                && number >= 2)
            {
                return ZipVariant.Spanned;
            }

            string name = FileNameHelper.GetFileName(card.Path);
            int lastDot = name.LastIndexOf('.');

            if (lastDot >= 0
                && lastDot + 1 < name.Length
                && ArchiveFixer.Helpers.ExtensionHelper.TrySplitVolumeSegmentTolerant(
                    name[(lastDot + 1)..],
                    out string canonical,
                    out _)
                && canonical.Length == 3
                && (canonical[0] == 'z' || canonical[0] == 'Z')
                && char.IsAsciiDigit(canonical[1])
                && char.IsAsciiDigit(canonical[2]))
            {
                // `.z01` 是跨盘族的第 1 片；⛔ 不能用 `.zip`=1 的那套名字口径。
                return ZipVariant.Spanned;
            }

            if (card.ZipEndRecordMeansSplit || card.HasZipLocalHeader)
            {
                return ZipVariant.Numbered;
            }

            return ZipVariant.Unknown;
        }

        /// <summary>
        /// 第二大步入口的**纯后缀清洗**：只把文件名第一个点之后那段中的
        /// 非 ASCII 杂质删掉，基名/内容不动；目标名被占就不改。
        /// </summary>
        internal int CleanupSuffixJunkAtSecondStep(IReadOnlyCollection<ArchiveTask>? tasks)
        {
            int renamed = 0;

            foreach (ArchiveFixer.Detection.VolumeHandoffEntry entry in _volumeHandoffEntries)
            {
                if (string.IsNullOrWhiteSpace(entry.Path) || !File.Exists(entry.Path))
                {
                    continue;
                }

                string fileName = Path.GetFileName(entry.Path);

                if (!Extraction.VolumeNameRepair.TryStripSuffixJunk(fileName, out string cleaned)
                    || string.IsNullOrWhiteSpace(cleaned))
                {
                    continue;
                }

                string directory = Path.GetDirectoryName(entry.Path) ?? string.Empty;
                string target = Path.Combine(directory, cleaned);

                if (File.Exists(target)
                    || string.Equals(target, entry.Path, StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog(
                        "WARN",
                        $"[第二大步·后缀清洗] {fileName}：目标名已被占用或没有变化 ⇒ 不改名。");
                    continue;
                }

                try
                {
                    File.Move(entry.Path, target);

                    string oldPath = entry.Path;
                    entry.Path = target;
                    renamed++;

                    if (tasks != null)
                    {
                        foreach (ArchiveTask task in tasks)
                        {
                            if (SafePathHelper.PathEquals(task.CurrentPath, oldPath))
                            {
                                task.CurrentPath = target;
                            }

                            for (int index = 0; index < task.VolumePaths.Count; index++)
                            {
                                if (SafePathHelper.PathEquals(task.VolumePaths[index], oldPath))
                                {
                                    task.VolumePaths[index] = target;
                                }
                            }
                        }
                    }

                    AppendLog(
                        "INFO",
                        $"[第二大步·后缀清洗] {fileName} → {cleaned}（只改后缀，基名/内容未动）");
                }
                catch (Exception ex)
                {
                    AppendLog(
                        "WARN",
                        $"[第二大步·后缀清洗] {fileName}：改名失败（{ex.Message}）⇒ 保留原名。");
                }
            }

            return renamed;
        }

        private ArchiveFixer.Detection.VolumeHandoffEntry RegisterVolumeHandoff(
            ArchiveTask task,
            ArchiveFixer.Detection.FileNatureCard card)
        {
            string path = task.CurrentPath ?? string.Empty;
            string baseName = FileNameHelper.SecondStepBaseName(path);
            int? volumeIndex = ArchiveFixer.Detection.VolumeGroupDetector.TryGetVolumeIndex(
                FileNameHelper.GetFileName(path));
            bool userImported = !task.IsContinuationTask;

            ArchiveFixer.Detection.VolumeHandoffEntry? dropChainDuplicate = null;

            foreach (ArchiveFixer.Detection.VolumeHandoffEntry existing in _volumeHandoffEntries)
            {
                if (string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    return existing;
                }

                /*
                 * 同一个基名、同一个卷号的两份来源：用户源片优先，输出层副本不重复进第二大步。
                 * P53 真机：输入 P53-8 与成品 P53-8 各有一份 .001/.002，两份都进交接
                 * ⇒ 组分析看到 4 片、快通道判 Incomplete。判据只读真实来源与卷号。
                 */
                int? existingIndex = existing.NameVolumeIndex
                    ?? ArchiveFixer.Detection.VolumeGroupDetector.TryGetVolumeIndex(
                        FileNameHelper.GetFileName(existing.Path));

                if (!string.Equals(existing.BaseName, baseName, StringComparison.OrdinalIgnoreCase)
                    || !volumeIndex.HasValue
                    || existingIndex != volumeIndex)
                {
                    continue;
                }

                if (userImported
                    && existing.Source == ArchiveFixer.Detection.VolumeHandoffSource.ChainProduced)
                {
                    dropChainDuplicate = existing;
                    break;
                }

                if (!userImported
                    && existing.Source == ArchiveFixer.Detection.VolumeHandoffSource.UserImported)
                {
                    return existing;
                }
            }

            if (dropChainDuplicate != null)
            {
                _volumeHandoffEntries.Remove(dropChainDuplicate);
            }

            var entry = new ArchiveFixer.Detection.VolumeHandoffEntry
            {
                Path = path,
                BaseName = baseName,
                Format = card.Format,
                Size = card.Size,
                BodyKind = card.BodyKind,
                Evidence = card.BodyKind is ArchiveBodyKind.VolumeFirstPiece or ArchiveBodyKind.VolumePiece
                    || card.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                        ? VolumeHandoffEvidence.ContentProven
                        : VolumeHandoffEvidence.NameSlot,
                VolumeNumber = card.VolumeNumber,
                NameVolumeIndex = volumeIndex,
                FirstVolumeFlag = card.FirstVolumeFlag,
                /* 底层识别事实（用户 2026-10-24：第二大步要用的判据必须随交接单过来） */
                Membership = card.Membership,
                ContentFail = card.ContentFail,
                DeclaredTotalBytes = card.DeclaredTotalBytes,
                DeclaredVolumeCount = card.DeclaredVolumeCount,
                RawVolumeField = card.RawVolumeField,
                HasZipEndRecord = card.HasZipEndRecord,
                ZipEndRecordMeansSplit = card.ZipEndRecordMeansSplit,
                HasZipLocalHeader = card.HasZipLocalHeader,
                SourceLayer = Path.GetDirectoryName(path) ?? string.Empty,
                Source = task.IsContinuationTask
                    ? ArchiveFixer.Detection.VolumeHandoffSource.ChainProduced
                    : ArchiveFixer.Detection.VolumeHandoffSource.UserImported
            };

            _volumeHandoffEntries.Add(entry);
            return entry;
        }

        /// <summary>
        /// **批首清空交接账**（与其它批内账本同一处调用）。
        /// ⛔ 不许把上一批的交接单带进下一批：账本原来是只 Add、从不处理的 List，
        /// 跨批残留会让轮边界组分析拿着上一批的路径做判定（2026-10-24 记账的缺口）。
        /// </summary>
        internal void ClearVolumeHandoffLedger()
        {
            _volumeHandoffEntries.Clear();
            _firstStepFileLedger.Clear();
        }

        /// <summary>
        /// 第二大步**取用**交接单后打点（幂等：已有时间的条目不再改）。
        /// 消费方按路径点名；判不出的路径不影响其它条目。
        /// </summary>
        internal void MarkVolumeHandoffConsumed(IEnumerable<string> paths, string by)
        {
            if (paths == null)
            {
                return;
            }

            var wanted = new HashSet<string>(
                paths.Where(path => !string.IsNullOrWhiteSpace(path)),
                StringComparer.OrdinalIgnoreCase);

            if (wanted.Count == 0)
            {
                return;
            }

            foreach (ArchiveFixer.Detection.VolumeHandoffEntry entry in _volumeHandoffEntries)
            {
                if (entry.ConsumedAt.HasValue || !wanted.Contains(entry.Path))
                {
                    continue;
                }

                entry.ConsumedBy = by;
                entry.ConsumedAt = DateTime.Now;
            }
        }

        /// <summary>
        /// 批末一行：本批交接 N 份（在盘 / 已不在）｜已消费 M 份｜待处理 K 份。
        /// ⛔ 只在有交接事实时写；没有交接就不打这一行（普通单包批次不受影响）。
        /// </summary>
        internal void AppendVolumeHandoffBatchSummary()
        {
            if (_volumeHandoffEntries.Count == 0 && _volumeStepGroupVerdicts.Count == 0)
            {
                return;
            }

            int onDisk = 0;

            foreach (ArchiveFixer.Detection.VolumeHandoffEntry entry in _volumeHandoffEntries)
            {
                try
                {
                    if (File.Exists(entry.Path))
                    {
                        onDisk++;
                    }
                }
                catch
                {
                    // 读不动就按"不在盘上"计（不影响总数与消费数的口径）。
                }
            }

            int consumed = _volumeHandoffEntries.Count(entry => entry.ConsumedAt.HasValue);

            string consumers = string.Join(
                "、",
                _volumeHandoffEntries
                    .Where(entry => entry.ConsumedBy.Length > 0)
                    .Select(entry => entry.ConsumedBy)
                    .Distinct(StringComparer.Ordinal));

            if (_volumeHandoffEntries.Count > 0)
            {
                AppendLog(
                    "INFO",
                    $"[第二大步·交接] 本批交接 {_volumeHandoffEntries.Count} 份"
                    + $"（在盘 {onDisk} / 已不在 {_volumeHandoffEntries.Count - onDisk}）"
                    + $"｜已消费 {consumed} 份｜待处理 {_volumeHandoffEntries.Count - consumed} 份"
                    + (consumers.Length > 0 ? $"｜消费方：{consumers}" : string.Empty));
            }

            /*
             * **P4 回写：每组一行结论**（用户 2026-10-24「回写结论」）：
             * 结论由第二大步收尾时写进账本（`_volumeStepGroupVerdicts`），⛔ 不从日志里反推；
             * 没有交接单的批次（批首点名那一档）也能读到。
             */
            foreach (KeyValuePair<string, string> verdict in _volumeStepGroupVerdicts)
            {
                AppendLog("INFO", $"[第二大步·结论] {verdict.Key}：{verdict.Value}");
            }
        }

        /// <summary>
        /// **P4 回写**：把第二大步对某一组的结论写回交接单（同一组多份都写，批末按基名汇总）。
        /// 判据与文案由调用方给；⛔ 这里不做任何推断。
        /// </summary>
        internal void MarkVolumeHandoffVerdict(IEnumerable<string>? pieces, string verdict)
        {
            if (pieces == null || string.IsNullOrWhiteSpace(verdict))
            {
                return;
            }

            var wanted = new HashSet<string>(
                pieces.Where(path => !string.IsNullOrWhiteSpace(path)),
                StringComparer.OrdinalIgnoreCase);

            if (wanted.Count == 0)
            {
                return;
            }

            foreach (ArchiveFixer.Detection.VolumeHandoffEntry entry in _volumeHandoffEntries)
            {
                if (wanted.Contains(entry.Path))
                {
                    entry.ConsumedVerdict = verdict;
                }
            }
        }

        /// <summary>
        /// **开工前就拦下这一单**的统一收尾（唯一出口）。
        ///
        /// <para>谁走它：不变量 11 的"源文件已变化"、不变量 7 的"缺卷预检"、以及"源文件不在了 /
        /// 落点无效"这三条 —— 它们的共同点是**引擎一次都没被调用、这一单一个字节都没产出**，
        /// 所以状态、原因、结束时间必须一次落全。</para>
        ///
        /// <para><b>为什么必须收口成一个出口</b>：机器终态的唯一收口（<c>ProcessExtractTaskAsync</c>
        /// 的 finally，判据 <c>Outcome == Pending &amp;&amp; EndTime 有值 &amp;&amp; 校验没通过</c>）
        /// 只在"这一单已经结束"时才敢补结论。少写一个 <c>EndTime</c>，任务就永远停在 <c>Pending</c>：
        /// 批末汇总按终态把它算进「未处理」，而批末诊断按状态说「分卷缺失」—— 同一件事两处口径打架
        /// （2026-09-30 真机 `222.z01`：`失败 4 / 未处理 1` vs `分卷缺失：1 个`）。</para>
        ///
        /// <para>⛔ 只落"这一单已结束 + 失败"这套事实（走 <see cref="ArchiveTask.MarkFailed"/>），
        /// <b>不在这里替任何一档下别的结论</b>：状态与原因由调用方给（各档自己的判据），
        /// 中文文案一个字都不改。</para>
        /// </summary>
        /// <summary>
        /// **"7-Zip 打不开"的那一份，是不是某个分卷组的一片？**（唯一出口，判据 2026-10-14 定、2026-10-16 落地）
        ///
        /// <para>现场（官方环台 `10101010` / `19191919`）：一份名为 `pack111-L2.7z` 的文件 ——
        /// 它其实是分卷 7z 的**第 3 片**，只被伪装法吃掉了卷标记 ⇒ <b>内容判不出</b>
        /// （7z 族的中间片/末片按定义没有魔数，卡上 `格式=Unknown 分卷身份=Unknown`）、
        /// <b>名字也不像后续卷</b>。老口径把它落成「已跳过 ⇒ 不是压缩包」，于是整组永远凑不齐。</para>
        ///
        /// <para>判据（⛔ 只用既有事实，⛔ 不猜卷号、⛔ 不动盘上任何东西）：<br/>
        /// ① 同批任务里有另一单的名字读得出"第 1 卷"（<see cref="VolumeGroupDetector.TryGetVolumeIndex"/> = 1）；<br/>
        /// ② 它的基名（唯一出口 <see cref="FileNameHelper.GetArchiveBaseName"/>）与这一份**相同或互为前缀**；<br/>
        /// ③ 这一份的体积 **≤** 那一份的体积（"除末卷外等大" ⇒ 任何一片都不会比满片还大）。<br/>
        /// 三条同时成立 ⇒ 判"疑似这一组的一片" ⇒ 结论是**缺卷**，交给批末/跨链那一套去凑。</para>
        ///
        /// <para>⛔ 判不出 ⇒ 返回 <c>false</c>（照旧落「已跳过」）—— 兜底永远是"什么都不做"。</para>
        /// </summary>
        /// <summary>
        /// 批末：把 <see cref="_unrecognizedMaybePiece"/> 里那些**升级**成「分卷缺失」（严格判据成立时）。
        ///
        /// <para>⛔ 只升级、不改别的：判据不成立的那些**保持「已跳过」**（它们是真的"不是压缩包"）；
        /// 机器终态与结束时间由 <see cref="MarkStoppedBeforeExtract"/> 一次性收口（那一支的注释里有理由）。</para>
        /// </summary>
        private void UpgradeUnrecognizedMaybePieceVerdicts()
        {
            if (_unrecognizedMaybePiece.Count == 0)
            {
                return;
            }

            foreach (ArchiveTask task in _unrecognizedMaybePiece.ToList())
            {
                if (task.Outcome != TaskOutcome.Skipped)
                {
                    continue;
                }

                if (!LooksLikePieceOfBatchVolumeGroup(task, out string diagnosis))
                {
                    continue;
                }

                MarkStoppedBeforeExtract(task, StatusText.VolumeMissing, diagnosis);

                /*
                 * ⛔ 状态改了、终态也必须落：`MarkStoppedBeforeExtract` 先把 `Outcome` 清成 `Pending`，
                 * 而**终态由谁落**只有 `FinalizeOutcomeIfPending` 一个出口（缺卷那一档在里面就是
                 * 「部分完成」）。少了这一句，这一单会以「分卷缺失」的**状态** + `Pending` 的
                 * **终态**收场 ⇒ 批末汇总把它算进「未处理」（2026-10-17 新用例当场逮到）。
                 */
                FinalizeOutcomeIfPending(task);

                AppendLog("WARN", $"分卷缺失，未开始解压：{task.FileName}，{diagnosis}");
            }
        }

        private bool LooksLikePieceOfBatchVolumeGroup(ArchiveTask task, out string diagnosis)
        {
            diagnosis = string.Empty;

            string ownName = FileNameHelper.GetFileName(task.CurrentPath);

            if (ownName.Length == 0)
            {
                return false;
            }

            long ownSize;

            try
            {
                ownSize = new FileInfo(task.CurrentPath).Length;
            }
            catch
            {
                return false;
            }

            string ownBase = FileNameHelper.GetArchiveBaseName(task.CurrentPath);

            if (string.IsNullOrWhiteSpace(ownBase))
            {
                return false;
            }

            foreach (ArchiveTask? other in SnapshotTaskTable(Tasks))
            {
                if (other == null || ReferenceEquals(other, task) || string.IsNullOrWhiteSpace(other.CurrentPath))
                {
                    continue;
                }

                string otherName = FileNameHelper.GetFileName(other.CurrentPath);

                // ① 那一单的名字必须读得出"第 1 卷"。
                if (VolumeGroupDetector.TryGetVolumeIndex(otherName) != 1)
                {
                    continue;
                }

                string otherBase = FileNameHelper.GetArchiveBaseName(other.CurrentPath);

                // ② 基名相同，或互为前缀（`pack111-L2` vs `pack111-L2.7z` 这种"归档后缀差一段"）。
                bool sameBase = string.Equals(ownBase, otherBase, StringComparison.OrdinalIgnoreCase)
                    || ownBase.StartsWith(otherBase, StringComparison.OrdinalIgnoreCase)
                    || otherBase.StartsWith(ownBase, StringComparison.OrdinalIgnoreCase);

                if (!sameBase)
                {
                    continue;
                }

                long otherSize;

                try
                {
                    otherSize = new FileInfo(other.CurrentPath).Length;
                }
                catch
                {
                    continue;
                }

                // ③ "除末卷外等大" ⇒ 这一份不会比满片还大。
                if (ownSize > otherSize)
                {
                    continue;
                }

                diagnosis = $"它可能是「{otherName}」那一组的一片（同族同基名、体积 {ownSize} ≤ {otherSize}），"
                    + "但这一组现在凑不齐 —— 先不判它，等这一批的解压都跑完再判一次。";

                return true;
            }

            return false;
        }

        /// <summary>
        /// **这一批已经用「完全同一组片」试过一遍的组签名**（批末补判去重用的那本账）。
        ///
        /// <para>签名 = 组的 `GroupKey` + 每一卷「完整路径 + 字节数」。**只要有新片进来签名就会变**
        /// ⇒ 补判"后来又补齐了、再跑一次"那条合法路径**一个字都不受影响**；
        /// 只有"这一批已经拿完全同样的几片试过、结论也就那样"才拒。</para>
        ///
        /// <para>2026-10-17 真机现场（`10101010`，逐行计数）：批末补判把同一组排了 **21 趟**，
        /// 每趟候选序号恒为 `1/10`（第一趟第一枚候选就撞结构性失败）、`分卷收不到一起` 21 次。</para>
        /// </summary>
        private readonly HashSet<string> _triedVolumeGroupSignatures =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 组的"试过一遍"签名（唯一出口）：`GroupKey` + 按卷序每一卷的「路径|字节数」。
        /// 读不到字节数记 `-`（⛔ 不因此判"不同组"——判不出就照旧算同一组，宁可不重跑）。
        /// </summary>
        /// <summary>
        /// **被「收拢」搬离用户原目录的那几片**（原路径 ↔ 搬走后的路径）。
        ///
        /// <para>收拢本身是用户 2026-10-05 拍板的（「各分卷在不同的目录，你就将其全部移动到头文件 .001
        /// 同级目录里面去」）；但「源包操作 = 留下」要求用户源文件**一个字节都不许少** ——
        /// 2026-10-17 真机 `10101010` 的判据 ④ 就是这么红的（第三片 10,490,213 字节被搬走后
        /// 当过程物永久删除，日志逐字「逐层回收…已立刻永久删除内层包（这一层的源）3 个」）。</para>
        ///
        /// <para>用法：逐层回收**动删除之前**先把这几份放回原目录；放不回就如实写一行，
        /// ⛔ 绝不因为这本账把别人的删除整段拦下。</para>
        /// </summary>
        private readonly List<(string OriginalPath, string CurrentPath)> _gatheredUserSourcePieces = new();

        /// <summary>记下"这几片是从用户自己的源目录里收拢过来的"（只记搬离过的那几份）。</summary>
        private void RememberGatheredUserSourcePieces(IReadOnlyList<VolumeRepairItem> items)
        {
            foreach (VolumeRepairItem item in items)
            {
                if (string.IsNullOrWhiteSpace(item.CurrentPath) || string.IsNullOrWhiteSpace(item.TargetPath))
                {
                    continue;
                }

                if (SafePathHelper.PathEquals(item.CurrentPath, item.TargetPath))
                {
                    continue;
                }

                if (!IsUserImportedSourcePath(item.CurrentPath))
                {
                    continue;
                }

                _gatheredUserSourcePieces.Add((item.CurrentPath, item.TargetPath));
            }
        }

        /// <summary>
        /// **把收拢搬走的用户源片放回原目录**（逐层回收动删除之前唯一该做的补偿）。
        /// 返回放回的份数；放不回（原位置又被占 / 出错）⇒ 如实写 WARN，⛔ 不拦别人的删除。
        /// </summary>
        private int RestoreGatheredUserSourcePieces(List<(string Level, string Message)>? logEntries)
        {
            if (_gatheredUserSourcePieces.Count == 0)
            {
                return 0;
            }

            int restored = 0;

            foreach ((string originalPath, string currentPath) in _gatheredUserSourcePieces.ToList())
            {
                try
                {
                    if (!File.Exists(currentPath))
                    {
                        _gatheredUserSourcePieces.Remove((originalPath, currentPath));
                        continue;
                    }

                    if (File.Exists(originalPath))
                    {
                        logEntries?.Add((
                            "WARN",
                            $"{Path.GetFileName(originalPath)}：原目录里已经有一份同名的了 —— "
                            + "不覆盖，收拢搬走的那一份仍留在这边。"));

                        continue;
                    }

                    string? directory = Path.GetDirectoryName(originalPath);

                    if (string.IsNullOrWhiteSpace(directory))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(directory);
                    File.Move(currentPath, originalPath);

                    logEntries?.Add((
                        "INFO",
                        $"用户源片放回原目录：{Path.GetFileName(originalPath)}（它是被「收拢」搬走的，"
                        + "按「源包操作 = 留下」一个字节都不许少）。"));

                    _gatheredUserSourcePieces.Remove((originalPath, currentPath));
                    restored++;
                }
                catch (Exception ex)
                {
                    logEntries?.Add((
                        "WARN",
                        $"{Path.GetFileName(originalPath)}：没能放回原目录（{ex.Message}）—— "
                        + "这一份仍是收拢过后的位置。"));
                }
            }

            return restored;
        }

        private static string BuildVolumeGroupAttemptSignature(VolumeGroup group)
        {
            var builder = new System.Text.StringBuilder(group.GroupKey);

            foreach (VolumeCandidate volume in group.Volumes)
            {
                builder.Append('|').Append(volume.Path).Append('|');

                try
                {
                    builder.Append(new FileInfo(volume.Path).Length);
                }
                catch
                {
                    builder.Append('-');
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// 第一大步"7-Zip 打不开"那一支**只登记、不判定**：这一份有没有可能是某个分卷组的一片，
        /// 由批末的 <see cref="UpgradeUnrecognizedMaybePieceVerdicts"/> 用严格判据回答
        /// （同族第 1 卷 + 基名相同或互为前缀 + 体积 ≤ 满片）。
        ///
        /// <para>为什么登记放在第一大步：只有它当场知道"引擎打不开这一份"这个事实；
        /// ⛔ 判据、结论与账本全部留在第二大步侧（第一大步不判分卷）。</para>
        /// </summary>
        private void RegisterUnrecognizedSuspect(ArchiveTask task)
        {
            _unrecognizedMaybePiece.Add(task);
        }

        /// <summary>
        /// 落点解释那一行（用户 2026-10-05 口径："落在**第一卷的位置**"）：这一组的第 1 卷与这一单
        /// 自己的文件不在同一层时，把"为什么解到这里"写进日志。⛔ 只写日志、不动盘。
        ///
        /// <para>判据与落点推导共用同一个出口（`PathService.ResolveOutputPlacement` →
        /// `Detection.GroupVolumeDirectory`），这里不另算一遍；⛔ 第一大步不再自己读整组事实。</para>
        /// </summary>
        private void LogVolumeGroupFirstVolumeLanding(ArchiveTask task, string outputPath)
        {
            if (!task.IsVolumeGroup || string.IsNullOrWhiteSpace(outputPath))
            {
                return;
            }

            var groupMembers = new List<string> { task.CurrentPath };
            groupMembers.AddRange(task.VolumePaths.Where(path => !string.IsNullOrWhiteSpace(path)));

            Detection.GroupVolumeDirectory.Entry entry = Detection.GroupVolumeDirectory.Resolve(groupMembers);

            if (entry.Path.Length > 0
                && entry.Directory.Length > 0
                && !SafePathHelper.PathEquals(entry.Directory, FileNameHelper.GetDirectoryName(task.CurrentPath)))
            {
                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.VolumeGroupFirstVolumeLandingFormat,
                        task.FileName,
                        outputPath,
                        Path.GetFileName(entry.Path),
                        Path.GetFileName(entry.Directory)));
            }
        }

        /// <summary>
        /// **第二大步接住（P3a）：把可证完整的交接组点名为组入口任务**（用户 2026-10-24）。
        ///
        /// <para>只做三件事：① 在任务表里找到入口那一份对应的任务；② 清掉它上一趟「已跳过」的结论，
        /// 让它能作为第二大步入口重新跑（只解一层由 `_volumeStepTasks` 保证）；③ 把这一组登记成
        /// 「已消费」。⛔ 不搬片、不改名、不动盘 —— 搬进入口层是 P3b 的事。</para>
        ///
        /// <para>判据全部来自组分析（它已经用交接单的族/卷号/首卷/自述片数/自述整包对过账）；
        /// 找不到入口任务 ⇒ 只写一行 WARN、什么都不做（⛔ 不新建任务绕过任务表）。</para>
        /// </summary>
        internal IReadOnlyList<ArchiveTask> TryMarkHandoffGroupEntryTasks(
            IReadOnlyList<ArchiveFixer.Extraction.GroupAnalysis.GroupCandidate>? groups,
            IReadOnlyCollection<ArchiveTask> tasks)
        {
            var result = new List<ArchiveTask>();

            if (groups == null || groups.Count == 0 || tasks == null || tasks.Count == 0)
            {
                return result;
            }

            foreach (ArchiveFixer.Extraction.GroupAnalysis.GroupCandidate group in groups)
            {
                if (group == null
                    || group.SuggestedVerdict != ArchiveFixer.Extraction.GroupAnalysis.GroupAnalysisVerdict.Complete
                    || string.IsNullOrWhiteSpace(group.EntryPath))
                {
                    continue;
                }

                ArchiveTask? entryTask = null;

                foreach (ArchiveTask candidate in tasks)
                {
                    if (candidate != null
                        && SafePathHelper.PathEquals(candidate.CurrentPath, group.EntryPath))
                    {
                        entryTask = candidate;
                        break;
                    }
                }

                if (entryTask == null)
                {
                    AppendLog(
                        "WARN",
                        $"[第二大步·接住] {group.BaseName}：可证完整，但任务表里找不到入口那一份"
                        + $"（{FileNameHelper.GetFileName(group.EntryPath)}）⇒ 这一组不动。");
                    continue;
                }

                /*
                 * ⛔ 已经由第二大步解成功过的入口不再点名：再点一次会把它加进下一轮重跑，
                 * 同名产物只能改名 ⇒ 盘上多一份 `(1)` 垃圾副本（2026-10-05 只读审计那条老坑）。
                 */
                if (entryTask.Outcome == TaskOutcome.Succeeded || entryTask.IsOutputVerified)
                {
                    AppendLog(
                        "INFO",
                        $"[第二大步·接住] {group.BaseName}：入口已经解成功过 ⇒ 不再点名（⛔ 不重跑）。");
                    continue;
                }

                /*
                 * 清掉上一趟的结论（这一单上一轮是被闸门跳过的那一份）：不清理的话，
                 * 它在下一轮会带着 `Skipped` 终态直接收场，第二大步一次都跑不起来。
                 */
                entryTask.Outcome = TaskOutcome.Pending;
                entryTask.IsOutputVerified = false;
                entryTask.EndTime = null;
                entryTask.ErrorMessage = string.Empty;
                entryTask.Status = StatusText.WaitingExtract;
                entryTask.Operation = StatusText.OpWaiting;
                entryTask.ProgressText = StatusText.ProgressWaiting;

                RememberVolumeStepTask(entryTask);
                MarkVolumeHandoffConsumed(group.Pieces, "第二大步·点名");
                result.Add(entryTask);

                /*
                 * ===== P3b 第一步：搬进入口层 =====
                 * 程序产出的片同卷搬、跨卷复制；**用户导入的原件只建零字节硬链接**（源目录只读红线）。
                 * 判据由唯一出口 `IsUserImportedSourcePath` 回答；⛔ 目标名被占 ⇒ 不覆盖、写清原因。
                 */
                string entryLayer = Path.GetDirectoryName(group.EntryPath) ?? string.Empty;

                ArchiveFixer.Extraction.VolumeGroupEntryLayer.MoveResult moved =
                    ArchiveFixer.Extraction.VolumeGroupEntryLayer.MovePiecesIntoEntryLayer(
                        group.Pieces,
                        entryLayer,
                        path => IsUserImportedSourcePath(path),
                        (level, message) => AppendLog(level, $"[第二大步·搬进入口层] {message}"));

                AppendLog("INFO", $"[第二大步·搬进入口层] {group.BaseName}：{moved.Describe()}");

                _volumeStepGroupWork[entryTask] = new VolumeStepGroupWork(
                    group.BaseName,
                    entryLayer,
                    group.Pieces);

                AppendLog(
                    "INFO",
                    $"[第二大步·接住] {group.BaseName}：可证完整（{group.Pieces.Count} 片，{group.Evidence}）"
                    + $"—— 入口={FileNameHelper.GetFileName(group.EntryPath)}，这一单只解这一层"
                    + "（片已按上一条处理：程序产出搬、用户原件只建链接）。");
            }

            return result;
        }

    }
}
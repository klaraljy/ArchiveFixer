using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels
{
    /// <summary>产物内容卡缓存/诊断与第一大步账本登记（从 ExtractionCoordinator.VolumeHandoffStep.cs 搬出）。</summary>
    internal sealed class ExtractionProducedVolumeGate
    {
        private readonly ExtractionCoordinator _host;

        internal ExtractionProducedVolumeGate(ExtractionCoordinator host)
        {
            _host = host;
        }

        /// <summary>
        /// 把这一单产出的全部文件登记进唯一第一大步账本。
        /// 这是“文件产物产生时”的登记点，覆盖包装/递归产出的 Unknown 与中间片。
        /// </summary>
        private static long _producedCardLookups;
        private static long _producedCardHits;
        private static long _producedCardMisses;
        private static long _producedCardFileInfoCalls;
        private static long _producedCardRemembered;

        internal static (long Lookups, long Hits, long Misses, long FileInfoCalls, long Remembered) ProducedCardDiagnostics =>
            (System.Threading.Interlocked.Read(ref _producedCardLookups),
             System.Threading.Interlocked.Read(ref _producedCardHits),
             System.Threading.Interlocked.Read(ref _producedCardMisses),
             System.Threading.Interlocked.Read(ref _producedCardFileInfoCalls),
             System.Threading.Interlocked.Read(ref _producedCardRemembered));

        internal static void ResetProducedCardDiagnostics()
        {
            System.Threading.Interlocked.Exchange(ref _producedCardLookups, 0);
            System.Threading.Interlocked.Exchange(ref _producedCardHits, 0);
            System.Threading.Interlocked.Exchange(ref _producedCardMisses, 0);
            System.Threading.Interlocked.Exchange(ref _producedCardFileInfoCalls, 0);
            System.Threading.Interlocked.Exchange(ref _producedCardRemembered, 0);
        }

        internal void ClearProducedCardCache() => _producedCardCache.Clear();

        private readonly ConcurrentDictionary<string, (long Length, long Ticks, FileNatureCard Card)> _producedCardCache =
            new(StringComparer.OrdinalIgnoreCase);

        internal bool TryGetProducedCard(string path, out FileNatureCard card)
        {
            card = null!;
            System.Threading.Interlocked.Increment(ref _producedCardLookups);

            if (string.IsNullOrWhiteSpace(path)
                || !_producedCardCache.TryGetValue(path, out (long Length, long Ticks, FileNatureCard Card) cached))
            {
                System.Threading.Interlocked.Increment(ref _producedCardMisses);
                return false;
            }

            try
            {
                System.Threading.Interlocked.Increment(ref _producedCardFileInfoCalls);
                var info = new FileInfo(path);

                if (!info.Exists || info.Length != cached.Length || info.LastWriteTimeUtc.Ticks != cached.Ticks)
                {
                    _producedCardCache.TryRemove(path, out _);
                    System.Threading.Interlocked.Increment(ref _producedCardMisses);
                    return false;
                }
            }
            catch
            {
                System.Threading.Interlocked.Increment(ref _producedCardMisses);
                return false;
            }

            card = cached.Card;
            System.Threading.Interlocked.Increment(ref _producedCardHits);
            return true;
        }

        private void RememberProducedCard(string path, FileNatureCard card)
        {
            try
            {
                System.Threading.Interlocked.Increment(ref _producedCardFileInfoCalls);
                var info = new FileInfo(path);

                if (info.Exists)
                {
                    _producedCardCache[path] = (info.Length, info.LastWriteTimeUtc.Ticks, card);
                    System.Threading.Interlocked.Increment(ref _producedCardRemembered);
                }
            }
            catch
            {
            }
        }
        private sealed class ProducedOutputFact
        {
            public string File { get; init; } = string.Empty;
            public FileNatureCard Card { get; init; } = null!;
            public int? NameSlot { get; init; }
            public bool Candidate { get; init; }
        }

        internal async Task RegisterProducedOutputsToLedgerAsync(ArchiveTask task)
        {
            if (task == null || string.IsNullOrWhiteSpace(task.OutputPath))
            {
                return;
            }

            string output = task.OutputPath;

            if (!Directory.Exists(output))
            {
                return;
            }

            string originRootId = _host._extractionContentGraph.ResolveRootId(task);
            string placementKey = _host._extractionContentGraph.ResolvePlacementKey(task);
            string currentPath = task.CurrentPath ?? string.Empty;

            /*
             * 这里原来在 UI 线程逐个 FileNature.Inspect 整棵产物树；1~12 的 1979 个文件实测
             * 阻塞 UI 46 秒（运行期调用栈：RegisterProducedOutputsToLedger → FileNature.Inspect
             * → VolumeNumberFromContent.ReadZip）。内容事实的读取放后台；账本写入仍回 UI 线程，
             * 因为 FirstStepFileLedger 不是并发容器。
             */
            List<ProducedOutputFact> facts = await Task.Run(() =>
            {
                var collected = new List<ProducedOutputFact>();

                IEnumerable<string> files;

                try
                {
                    files = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories);
                }
                catch
                {
                    return collected;
                }

                foreach (string file in files)
                {
                    if (file.Contains(
                            Path.DirectorySeparatorChar + ".ArchiveFixer.work" + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.Equals(file, currentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    FileNatureCard card;

                    try
                    {
                        card = FileNature.Inspect(file);
                    }
                    catch
                    {
                        continue;
                    }

                    RememberProducedCard(file, card);

                    if (!card.HasIdentity || card.IsKnownNormalMedia)
                    {
                        continue;
                    }

                    int? nameSlot = LegacyVolumeNames.TryGetIndex(
                        FileNameHelper.GetFileName(file));

                    collected.Add(new ProducedOutputFact
                    {
                        File = file,
                        Card = card,
                        NameSlot = nameSlot,
                        Candidate = RecursiveExtractor.IsPieceOfIncompleteGroup(file)
                    });
                }

                return collected;
            }).ConfigureAwait(true);

            foreach (ProducedOutputFact fact in facts)
            {
                /*
                 * 已经由候选登记点挂上租约的片：保留那份角色/租约/来源根，
                 * ⛔ 不用这次“普通产出物”登记把它降级成可回收。
                 */
                if (_host._firstStepFileLedger.TryGetByPath(fact.File, out FirstStepFileLedgerEntry existing)
                    && existing.HasCandidateLease)
                {
                    continue;
                }

                string leaseId = fact.Candidate
                    ? _host._extractionContentGraph.LeaseCandidate(
                        fact.File,
                        originRootId,
                        task.TaskId,
                        placementKey)
                    : string.Empty;

                if (fact.Candidate)
                {
                    _host.AppendLog(
                        "INFO",
                        $"{task.LogName}：候选分卷片已登记交界前租约 —— {FileNameHelper.GetFileName(fact.File)}"
                        + "（组收片前不再按普通过程物回收）");
                }

                _host._firstStepFileLedger.AddOrReplace(
                    FirstStepFileLedgerEntry.FromCard(
                        fact.Card,
                        FileNameHelper.GetFileName(fact.File),
                        VolumeHandoffSource.ChainProduced,
                        gatePassed: true,
                        nameSlot: fact.NameSlot,
                        originRootId: originRootId,
                        producedByTaskId: task.TaskId,
                        role: fact.Candidate
                            ? ArtifactRole.CandidateVolumePiece
                            : ArtifactRole.Unknown,
                        candidateLeaseId: leaseId,
                        placementKey: placementKey));
            }
        }

        private static bool IsInsideWorkDirectory(string? path) =>
            !string.IsNullOrWhiteSpace(path)
            && path.Contains(
                Path.DirectorySeparatorChar + ".ArchiveFixer.work" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 第一大步闸门通过后登记账本（不判定分卷、不碰盘）。
        /// 同一路径在一轮里重复登记时保留后到的内容卡（改名/补片后事实会变）。
        /// </summary>
        internal FirstStepFileLedgerEntry RegisterFirstStepLedgerEntry(
            ArchiveTask task,
            ArchiveFixer.Detection.FileNatureCard card)
        {
            string path = task.CurrentPath ?? string.Empty;

            /*
             * ⛔ 工作区里的 task.CurrentPath / 兄弟片是临时层，收尾会消失。
             * 登记进账本会让第二大步拿旧路径建硬链接（Win32 错误 3）。
             * 已发布产物由 RegisterProducedOutputsToLedger 单独登记。
             */
            bool insideWork = IsInsideWorkDirectory(path);
            int? nameSlot = LegacyVolumeNames.TryGetIndex(
                FileNameHelper.GetFileName(path));

            ZipVariant zipVariant = ExtractionCoordinator.ResolveZipVariant(card);

            string originRootId = _host._extractionContentGraph.ResolveRootId(task);
            string placementKey = _host._extractionContentGraph.ResolvePlacementKey(task);
            ArtifactRole role = insideWork
                ? ArtifactRole.Unknown
                : task.IsContinuationTask
                    ? ArtifactRole.InnerArchive
                    : ArtifactRole.SourceRoot;

            FirstStepFileLedgerEntry entry = FirstStepFileLedgerEntry.FromCard(
                card,
                task.LogName,
                task.IsContinuationTask
                    ? ArchiveFixer.Detection.VolumeHandoffSource.ChainProduced
                    : ArchiveFixer.Detection.VolumeHandoffSource.UserImported,
                gatePassed: true,
                rarVariant: card.RarVariant,
                zipVariant: zipVariant,
                nameSlot: nameSlot,
                originRootId: originRootId,
                producedByTaskId: task.TaskId,
                role: role,
                placementKey: placementKey);

            FirstStepFileLedgerEntry registered = insideWork
                ? entry
                : _host._firstStepFileLedger.AddOrReplace(entry);

            /*
             * 账本不能只收"任务表里那一份"：同目录的 `.002` 可能只是上一轮解出来的兄弟片，
             * 没有独立任务；7z 中间片内容又没有魔数，不补进来就只剩 1 片可判。
             * 只扫同基名、卷标记候选，且只在当前项本身像分卷时才扫。
             */
            if (!insideWork
                && (card.BodyKind is ArchiveBodyKind.VolumeFirstPiece or ArchiveBodyKind.VolumePiece
                    || card.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                    || card.Format == VolumeContentFormat.SevenZip))
            {
                RegisterSiblingLedgerEntries(task, card);
            }

            return registered;
        }

        private void RegisterSiblingLedgerEntries(
            ArchiveTask task,
            ArchiveFixer.Detection.FileNatureCard currentCard)
        {
            string currentPath = task.CurrentPath ?? string.Empty;
            string directory = Path.GetDirectoryName(currentPath) ?? string.Empty;
            string baseName = FileNameHelper.SecondStepBaseName(currentPath);

            if (directory.Length == 0 || baseName.Length == 0)
            {
                return;
            }

            string[] siblings;

            try
            {
                siblings = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return;
            }

            foreach (string sibling in siblings)
            {
                if (SafePathHelper.PathEquals(sibling, currentPath))
                {
                    continue;
                }

                string name = FileNameHelper.GetFileName(sibling);

                if (!LegacyVolumeNames.IsVolumePart(name)
                    || !string.Equals(
                        FileNameHelper.SecondStepBaseName(sibling),
                        baseName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    ArchiveFixer.Detection.FileNatureCard card =
                        ArchiveFixer.Detection.FileNature.Inspect(sibling);

                    string siblingRootId = _host._extractionContentGraph.ResolveRootId(task);
                    string siblingPlacementKey = _host._extractionContentGraph.ResolvePlacementKey(task);

                    _host._firstStepFileLedger.AddOrReplace(FirstStepFileLedgerEntry.FromCard(
                        card,
                        task.LogName,
                        task.IsContinuationTask
                            ? VolumeHandoffSource.ChainProduced
                            : VolumeHandoffSource.UserImported,
                        gatePassed: true,
                        rarVariant: card.RarVariant,
                        zipVariant: ExtractionCoordinator.ResolveZipVariant(card),
                        originRootId: siblingRootId,
                        producedByTaskId: task.TaskId,
                        role: task.IsContinuationTask
                            ? ArtifactRole.CandidateVolumePiece
                            : ArtifactRole.SourceRoot,
                        placementKey: siblingPlacementKey));
                }
                catch
                {
                    // 兄弟片读不动 ⇒ 不登记，不影响当前项。
                }
            }
        }

        /// <summary>
        /// 名字槽 &gt; 1 的未知内容片：只有当**同基名、同批**里真有一份内容自证的第一卷时，
        /// 才把它当第二大步候选。这样既不用 `.7z` 字面量，也不会把普通的 `作用域.002` 挡在第一大步外。
        /// </summary>
        internal bool HasContentProvenVolumeFirstSibling(ArchiveTask task, string entryName)
        {
            string baseName = FileNameHelper.SecondStepBaseName(task.CurrentPath ?? string.Empty);

            if (string.IsNullOrWhiteSpace(baseName))
            {
                return false;
            }

            string currentPath = task.CurrentPath ?? string.Empty;
            string currentDirectory = Path.GetDirectoryName(currentPath) ?? string.Empty;

            /*
             * 递归层刚解出的中间片可能还没有独立任务（真机 23.5：`.003` 被扫到的时候
             * `.001` 只是一份文件，不在任务表里）⇒ 先查第一大步账本；账本里的兄弟片
             * 正是 RegisterSiblingLedgerEntries 刚补进来的。
             */
            foreach (ArchiveFixer.Detection.FirstStepFileLedgerEntry entry
                     in _host._firstStepFileLedger.SameBase(baseName))
            {
                if (SafePathHelper.PathEquals(entry.Path, currentPath)
                    || !string.Equals(
                        Path.GetDirectoryName(entry.Path) ?? string.Empty,
                        currentDirectory,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry.Size > 0
                    && entry.Format != ArchiveFixer.Detection.VolumeContentFormat.Unknown
                    && (entry.BodyKind is ArchiveFixer.Detection.ArchiveBodyKind.VolumeFirstPiece
                            or ArchiveFixer.Detection.ArchiveBodyKind.VolumePiece
                        || entry.Membership == ArchiveFixer.Detection.VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember))
                {
                    return true;
                }
            }

            foreach (ArchiveTask candidate in ExtractionCoordinator.SnapshotTaskTable(_host.Tasks))
            {
                if (candidate == null
                    || ReferenceEquals(candidate, task)
                    || string.IsNullOrWhiteSpace(candidate.CurrentPath)
                    || !File.Exists(candidate.CurrentPath))
                {
                    continue;
                }

                if (!string.Equals(
                        FileNameHelper.SecondStepBaseName(candidate.CurrentPath),
                        baseName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    ArchiveFixer.Detection.FileNatureCard card =
                        ArchiveFixer.Detection.FileNature.Inspect(candidate.CurrentPath);

                    if (card.Size > 0
                        && card.Format != ArchiveFixer.Detection.VolumeContentFormat.Unknown
                        && (card.BodyKind is ArchiveFixer.Detection.ArchiveBodyKind.VolumeFirstPiece
                                or ArchiveFixer.Detection.ArchiveBodyKind.VolumePiece
                            || card.Membership == ArchiveFixer.Detection.VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember))
                    {
                        return true;
                    }
                }
                catch
                {
                    // 兄弟片读不动 ⇒ 不因它放行。
                }
            }

            return false;
        }

        /// <summary>
        /// ZIP 两族的**初步**变体（只读内容卡现有的三件事；RAR/7z 不在这里判）。
        /// 最终族结论仍由 <see cref="VolumeFamilyArbiter"/> 给。
        /// </summary>
    }
}


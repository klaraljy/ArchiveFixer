using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **RAR 族边界 resolver**（2026-10-27，三类分开）。
    ///
    /// <para>RAR5：内容绝对卷号；RAR4 新编号：卷尾字段换算的卷号；RAR4 老编号：
    /// <c>.rar + .r00/.r01…</c> 的名字约定。RAR 内容能证明的比 7z 多，定序不靠猜；
    /// 判不出就交 Unknown，⛔ 不拿名字硬凑。</para>
    ///
    /// <para>加密头多卷按用户口径交给「标准名快速通道」：本 resolver 直接返回 null，
    /// 不在第二大步里做密码/排列猜测。</para>
    /// </summary>
    public sealed class RarBoundaryResolver : IVolumeBoundaryResolver
    {
        public VolumeNamingFamily Family => VolumeNamingFamily.RarPart;

        public Task<PreparedVolumeGroup?> ResolveAsync(
            VolumeBoundaryContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (context == null || context.Ledger == null || string.IsNullOrWhiteSpace(context.BaseName))
            {
                return Task.FromResult<PreparedVolumeGroup?>(null);
            }

            List<FirstStepFileLedgerEntry> candidates = context.Ledger
                .SameBase(context.BaseName)
                .Where(entry => entry.IsGatePassed
                    && entry.Format == VolumeContentFormat.Rar)
                .ToList();

            if (candidates.Count == 0)
            {
                return Task.FromResult<PreparedVolumeGroup?>(null);
            }

            RarVariant variant = candidates
                .Select(entry => entry.RarVariant)
                .FirstOrDefault(value => value != RarVariant.Unknown);

            /*
             * 多卷加密头：用户口径是「一般已经是标准名，走快速通道；不是标准名就没有办法」。
             * 这一档不在这里猜，直接交回既有快速通道/密码流程。
             */
            if (variant is RarVariant.Rar5HeadersEncrypted or RarVariant.Rar4HeadersEncrypted)
            {
                return Task.FromResult<PreparedVolumeGroup?>(null);
            }

            if (variant == RarVariant.Unknown)
            {
                /*
                 * 只有 RAR 签名、没有可读主头（普通单卷 / 假头 / 头加密但连变体都判不出）：
                 * 这不是本 resolver 能接的分卷组，交回第一大步/标准名快通道。
                 * ⛔ 返回 null，不返回 Unknown 组：否则会把单卷任务从第一大步结果里“重开”一次。
                 */
                return Task.FromResult<PreparedVolumeGroup?>(null);
            }

            if (candidates.Any(entry => entry.RarVariant != RarVariant.Unknown
                    && entry.RarVariant != variant))
            {
                return Task.FromResult<PreparedVolumeGroup?>(
                    SizeAnomaly(context.BaseName, candidates, "同基名混了两种 RAR 变体 ⇒ 不混组"));
            }

            return variant switch
            {
                RarVariant.Rar5 => Task.FromResult(ResolveNumbered(
                    context.BaseName,
                    candidates,
                    requireFirstFlag: false,
                    evidence: "RAR5 绝对卷号")),
                RarVariant.Rar4New => Task.FromResult(ResolveNumbered(
                    context.BaseName,
                    candidates,
                    requireFirstFlag: true,
                    evidence: "RAR4 新编号卷尾字段")),
                RarVariant.Rar4Old => Task.FromResult(ResolveOldNumbered(
                    context.BaseName,
                    candidates)),
                _ => Task.FromResult<PreparedVolumeGroup?>(
                    Unknown(context.BaseName, candidates, "RAR 变体不支持 ⇒ 不动"))
            };
        }

        /// <summary>RAR5 / RAR4 新编号：内容卷号连成 1..N。</summary>
        private static PreparedVolumeGroup? ResolveNumbered(
            string baseName,
            IReadOnlyList<FirstStepFileLedgerEntry> candidates,
            bool requireFirstFlag,
            string evidence)
        {
            List<FirstStepFileLedgerEntry> unique = CollapsePhysicalDuplicates(candidates);

            List<FirstStepFileLedgerEntry> firsts = unique
                .Where(entry => entry.BodyKind == ArchiveBodyKind.VolumeFirstPiece
                    || entry.VolumeNumber == 1
                    || (!requireFirstFlag && entry.FirstVolumeFlag))
                .ToList();

            if (firsts.Count != 1)
            {
                return Unknown(baseName, unique, $"RAR 首卷证据有 {firsts.Count} 个 ⇒ 判不出");
            }

            FirstStepFileLedgerEntry first = firsts[0];

            if (unique.Any(entry => entry.VolumeNumber == null))
            {
                return Unknown(baseName, unique, "RAR 有片内容卷号判不出 ⇒ 判不出");
            }

            var byNumber = new Dictionary<int, FirstStepFileLedgerEntry>();

            foreach (FirstStepFileLedgerEntry entry in unique)
            {
                int number = entry.VolumeNumber!.Value;

                if (number < 1)
                {
                    return SizeAnomaly(baseName, unique, $"RAR 卷号 {number} 越界");
                }

                if (byNumber.ContainsKey(number))
                {
                    return SizeAnomaly(baseName, unique, $"RAR 卷号 {number} 有两份不同文件");
                }

                byNumber[number] = entry;
            }

            int total = byNumber.Keys.Max();

            if (byNumber.Count > total)
            {
                return SizeAnomaly(baseName, unique, "RAR 卷号数量超过最大卷号 ⇒ 结构异常");
            }

            List<int> missing = Enumerable.Range(1, total)
                .Except(byNumber.Keys)
                .OrderBy(number => number)
                .ToList();

            if (missing.Count > 0)
            {
                return new PreparedVolumeGroup
                {
                    BaseName = baseName,
                    Family = VolumeNamingFamily.RarPart,
                    OriginalSourceLayer = first.SourceLayer,
                    OriginalEntryPath = first.Path,
                    State = PreparedGroupState.Missing,
                    MissingNumbers = missing,
                    Evidence = $"RAR 内容卷号连不成 1..{total}（缺 {string.Join(",", missing)}）"
                };
            }

            if (byNumber[total].Size > byNumber[1].Size)
            {
                return SizeAnomaly(baseName, unique, "RAR 末卷大于首卷 ⇒ 大小结构异常");
            }

            IReadOnlyList<string> targets = VolumeNumberFromContent.BuildStandardNames(
                baseName,
                VolumeNamingFamily.RarPart,
                total);

            if (targets.Count != total)
            {
                return Unknown(baseName, unique, "RAR 标准名生成数量不符 ⇒ 判不出");
            }

            var pieces = new List<PreparedVolumePiece>();

            for (int number = 1; number <= total; number++)
            {
                FirstStepFileLedgerEntry entry = byNumber[number];
                pieces.Add(new PreparedVolumePiece
                {
                    CanonicalName = targets[number - 1],
                    OriginalPath = entry.Path,
                    Size = entry.Size,
                    Source = entry.Source
                });
            }

            return new PreparedVolumeGroup
            {
                BaseName = baseName,
                Family = VolumeNamingFamily.RarPart,
                OriginalSourceLayer = first.SourceLayer,
                OriginalEntryPath = first.Path,
                EntryCanonicalName = targets[0],
                Pieces = pieces,
                State = PreparedGroupState.Complete,
                Evidence = $"{evidence}连成 1..{total}；入口 = 第 1 卷"
            };
        }

        /// <summary>RAR4 老编号：<c>.rar</c> = 1，<c>.r00</c> = 2，<c>.r01</c> = 3…</summary>
        private static PreparedVolumeGroup? ResolveOldNumbered(
            string baseName,
            IReadOnlyList<FirstStepFileLedgerEntry> candidates)
        {
            List<FirstStepFileLedgerEntry> unique = CollapsePhysicalDuplicates(candidates);

            FirstStepFileLedgerEntry? first = unique.FirstOrDefault(entry =>
                entry.FirstVolumeFlag
                && string.Equals(
                    Path.GetExtension(entry.Path),
                    ".rar",
                    StringComparison.OrdinalIgnoreCase));

            if (first == null)
            {
                return Unknown(baseName, unique, "RAR 老编号：找不到内容自证的第 1 卷 .rar ⇒ 判不出");
            }

            var bySlot = new Dictionary<int, FirstStepFileLedgerEntry> { [1] = first };

            foreach (FirstStepFileLedgerEntry entry in unique)
            {
                if (ReferenceEquals(entry, first))
                {
                    continue;
                }

                string extension = Path.GetExtension(entry.Path);

                if (extension.Length != 4
                    || (extension[1] != 'r' && extension[1] != 'R')
                    || !char.IsAsciiDigit(extension[2])
                    || !char.IsAsciiDigit(extension[3]))
                {
                    return Unknown(baseName, unique, $"RAR 老编号：{Path.GetFileName(entry.Path)} 不是 .rNN ⇒ 判不出");
                }

                int slot = int.Parse(extension[2..], System.Globalization.CultureInfo.InvariantCulture) + 2;

                if (bySlot.ContainsKey(slot))
                {
                    return SizeAnomaly(baseName, unique, $"RAR 老编号槽位 {slot} 有两份文件");
                }

                bySlot[slot] = entry;
            }

            int total = bySlot.Keys.Max();
            List<int> missing = Enumerable.Range(1, total).Except(bySlot.Keys).ToList();

            if (missing.Count > 0)
            {
                return new PreparedVolumeGroup
                {
                    BaseName = baseName,
                    Family = VolumeNamingFamily.RarPart,
                    OriginalSourceLayer = first.SourceLayer,
                    OriginalEntryPath = first.Path,
                    State = PreparedGroupState.Missing,
                    MissingNumbers = missing,
                    Evidence = $"RAR 老编号缺 {string.Join(",", missing)} ⇒ 不处理"
                };
            }

            if (bySlot[total].Size > bySlot[1].Size)
            {
                return SizeAnomaly(baseName, unique, "RAR 老编号末卷大于首卷 ⇒ 大小结构异常");
            }

            var pieces = new List<PreparedVolumePiece>();

            for (int slot = 1; slot <= total; slot++)
            {
                FirstStepFileLedgerEntry entry = bySlot[slot];
                string canonical = slot == 1
                    ? baseName + ".rar"
                    : baseName + ".r" + (slot - 2).ToString("D2", System.Globalization.CultureInfo.InvariantCulture);

                pieces.Add(new PreparedVolumePiece
                {
                    CanonicalName = canonical,
                    OriginalPath = entry.Path,
                    Size = entry.Size,
                    Source = entry.Source
                });
            }

            return new PreparedVolumeGroup
            {
                BaseName = baseName,
                Family = VolumeNamingFamily.RarPart,
                OriginalSourceLayer = first.SourceLayer,
                OriginalEntryPath = first.Path,
                EntryCanonicalName = baseName + ".rar",
                Pieces = pieces,
                State = PreparedGroupState.Complete,
                Evidence = $"RAR 老编号连成 1..{total}（.rar + .r00…）；入口 = 第 1 卷"
            };
        }

        private static List<FirstStepFileLedgerEntry> CollapsePhysicalDuplicates(
            IReadOnlyList<FirstStepFileLedgerEntry> entries)
        {
            var kept = new List<FirstStepFileLedgerEntry>();
            var indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (FirstStepFileLedgerEntry entry in entries
                         .OrderBy(item => item.Source == VolumeHandoffSource.UserImported ? 0 : 1)
                         .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
            {
                string key = entry.HasIdentity
                    ? "I:"
                      + entry.VolumeSerial.ToString(System.Globalization.CultureInfo.InvariantCulture)
                      + ":"
                      + entry.FileIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "P:" + entry.Path;

                if (!indexByKey.TryGetValue(key, out int index))
                {
                    indexByKey[key] = kept.Count;
                    kept.Add(entry);
                    continue;
                }

                if (entry.Source == VolumeHandoffSource.UserImported
                    && kept[index].Source == VolumeHandoffSource.ChainProduced)
                {
                    kept[index] = entry;
                }
            }

            return kept;
        }

        private static PreparedVolumeGroup Unknown(
            string baseName,
            IReadOnlyList<FirstStepFileLedgerEntry> entries,
            string evidence)
        {
            return new PreparedVolumeGroup
            {
                BaseName = baseName,
                Family = VolumeNamingFamily.RarPart,
                OriginalSourceLayer = FirstLayer(entries),
                OriginalEntryPath = FirstPath(entries),
                State = PreparedGroupState.Unknown,
                Evidence = evidence
            };
        }

        private static PreparedVolumeGroup SizeAnomaly(
            string baseName,
            IReadOnlyList<FirstStepFileLedgerEntry> entries,
            string detail)
        {
            return new PreparedVolumeGroup
            {
                BaseName = baseName,
                Family = VolumeNamingFamily.RarPart,
                OriginalSourceLayer = FirstLayer(entries),
                OriginalEntryPath = FirstPath(entries),
                State = PreparedGroupState.SizeAnomaly,
                Evidence = detail,
                SizeAnomalyDetail = detail
            };
        }

        private static string FirstLayer(IReadOnlyList<FirstStepFileLedgerEntry> entries) =>
            entries.Select(entry => entry.SourceLayer)
                .FirstOrDefault(layer => !string.IsNullOrWhiteSpace(layer))
            ?? string.Empty;

        private static string FirstPath(IReadOnlyList<FirstStepFileLedgerEntry> entries) =>
            entries.Select(entry => entry.Path)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
            ?? string.Empty;
    }
}

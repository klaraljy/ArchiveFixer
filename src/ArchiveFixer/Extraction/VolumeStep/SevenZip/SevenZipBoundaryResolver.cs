using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **7z 族边界 resolver**（共用骨架下的第一个族实现，2026-10-27）。
    ///
    /// <para>证据：只有首卷的起始头自述 `DeclaredTotalBytes` / `DeclaredVolumeCount`；
    /// 其余片内容里没有身份。判据：片数必须等于自述 N、字节和必须等于自述整包、
    /// 中间片大小必须等于首卷、末片不大于首卷。槽位先按名字容忍档；名字读不出的，
    /// 只允许「只剩一个槽位」或「末槽 + 唯一最小片」；更多歧义交后续试开。</para>
    ///
    /// <para>⛔ 本类不读文件内容、不建链接；只把结论与标准名计划交给
    /// <see cref="VolumeGroupEntryDirectory"/>。</para>
    /// </summary>
    public sealed class SevenZipBoundaryResolver : IVolumeBoundaryResolver
    {
        public VolumeNamingFamily Family => VolumeNamingFamily.SevenZipNumbered;

        public async Task<PreparedVolumeGroup?> ResolveAsync(
            VolumeBoundaryContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (context == null || context.Ledger == null || string.IsNullOrWhiteSpace(context.BaseName))
            {
                return (null);
            }

            List<FirstStepFileLedgerEntry> candidates = context.Ledger
                .SameBase(context.BaseName)
                .Where(IsSevenZipCandidate)
                .ToList();

            List<FirstStepFileLedgerEntry> unique = CollapsePhysicalDuplicates(candidates);

            List<FirstStepFileLedgerEntry> firsts = unique
                .Where(entry => entry.BodyKind == ArchiveBodyKind.VolumeFirstPiece
                    && entry.DeclaredTotalBytes > 0
                    && entry.DeclaredVolumeCount >= 2)
                .ToList();

            if (firsts.Count != 1)
            {
                return (
                    Unknown(
                        context.BaseName,
                        unique,
                        $"7z 首卷证据有 {firsts.Count} 个（应恰好 1 个）⇒ 判不出"));
            }

            FirstStepFileLedgerEntry first = firsts[0];
            int total = first.DeclaredVolumeCount;

            if (first.Size <= 0 || first.DeclaredTotalBytes <= 0 || total < 2)
            {
                return (
                    Unknown(context.BaseName, unique, "7z 首卷自述的大小/片数不成立 ⇒ 判不出"));
            }

            var slots = new Dictionary<int, FirstStepFileLedgerEntry> { [1] = first };
            var unassigned = new List<FirstStepFileLedgerEntry>();

            foreach (FirstStepFileLedgerEntry entry in unique)
            {
                if (ReferenceEquals(entry, first))
                {
                    continue;
                }

                int? slot = ResolveSlot(entry, total);

                if (slot == null)
                {
                    unassigned.Add(entry);
                    continue;
                }

                if (slot.Value < 2 || slot.Value > total)
                {
                    return (
                        SizeAnomaly(
                            context.BaseName,
                            unique,
                            $"7z 名字序号越界：{Path.GetFileName(entry.Path)} → {slot.Value}"));
                }

                if (slots.ContainsKey(slot.Value))
                {
                    return (
                        SizeAnomaly(
                            context.BaseName,
                            unique,
                            $"7z 槽位 {slot.Value} 有两份不同文件"));
                }

                slots[slot.Value] = entry;
            }

            if (unique.Count != total)
            {
                return (
                    SizeAnomaly(
                        context.BaseName,
                        unique,
                        $"7z 自述 {total} 片，实际候选 {unique.Count} 片 ⇒ 数量结构异常"));
            }

            List<int> missing = Enumerable.Range(1, total)
                .Except(slots.Keys)
                .OrderBy(number => number)
                .ToList();

            if (missing.Count != unassigned.Count)
            {
                return (
                    SizeAnomaly(
                        context.BaseName,
                        unique,
                        $"7z 自述 {total} 片：缺槽 {missing.Count} 个、未定槽 {unassigned.Count} 片 ⇒ 对不上"));
            }

            if (unassigned.Count == 1)
            {
                slots[missing[0]] = unassigned[0];
            }
            else if (unassigned.Count > 1)
            {
                Dictionary<int, FirstStepFileLedgerEntry>? probed =
                    await TryProbeUnassignedOrderAsync(
                            context,
                            first,
                            missing,
                            unassigned,
                            total,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (probed == null)
                {
                    return (
                        Unknown(
                            context.BaseName,
                            unique,
                            $"7z 有 {unassigned.Count} 片未定槽、缺 {missing.Count} 个槽，"
                            + "顺序不明且没有可用的试开条件 ⇒ 判不出"));
                }

                foreach (KeyValuePair<int, FirstStepFileLedgerEntry> pair in probed)
                {
                    slots[pair.Key] = pair.Value;
                }
            }

            long receivedBytes = unique.Sum(entry => entry.Size);

            if (receivedBytes != first.DeclaredTotalBytes)
            {
                return (
                    SizeAnomaly(
                        context.BaseName,
                        unique,
                        $"7z 首卷自述整包 {first.DeclaredTotalBytes} 字节，实际 {receivedBytes} 字节"));
            }

            for (int slot = 2; slot < total; slot++)
            {
                if (slots[slot].Size != first.Size)
                {
                    return (
                        SizeAnomaly(
                            context.BaseName,
                            unique,
                            $"7z 中间片 {slot} 大小 {slots[slot].Size} ≠ 首卷 {first.Size}"));
                }
            }

            if (slots[total].Size > first.Size)
            {
                return (
                    SizeAnomaly(
                        context.BaseName,
                        unique,
                        $"7z 末片 {slots[total].Size} 字节大于首卷 {first.Size} 字节"));
            }

            IReadOnlyList<string> targets = VolumeNumberFromContent.BuildStandardNames(
                context.BaseName,
                VolumeNamingFamily.SevenZipNumbered,
                total);

            if (targets.Count != total)
            {
                return (
                    Unknown(context.BaseName, unique, "7z 标准名生成数量不符 ⇒ 判不出"));
            }

            var pieces = new List<PreparedVolumePiece>();

            for (int slot = 1; slot <= total; slot++)
            {
                FirstStepFileLedgerEntry entry = slots[slot];
                pieces.Add(new PreparedVolumePiece
                {
                    CanonicalName = targets[slot - 1],
                    OriginalPath = entry.Path,
                    Size = entry.Size,
                    Source = entry.Source
                });
            }

            return (new PreparedVolumeGroup
            {
                BaseName = context.BaseName,
                Family = Family,
                OriginalSourceLayer = first.SourceLayer,
                OriginalEntryPath = first.Path,
                EntryCanonicalName = targets[0],
                Pieces = pieces,
                State = PreparedGroupState.Complete,
                Evidence = $"7z 首卷自述 {total} 片 / {first.DeclaredTotalBytes} 字节；字节和、中间片大小、末片大小均对上"
            });
        }

        /// <summary>
        /// 7z 中间片名字读不出时的**高概率顺序试开**：最多 3 个未定槽 ⇒ 最多 6 种排列，
        /// 用硬链接让引擎真的开一遍；赢的那一版才锁槽。没有引擎/工作区/跨卷 ⇒ null（判不出，不猜）。
        /// </summary>
        private static async Task<Dictionary<int, FirstStepFileLedgerEntry>?> TryProbeUnassignedOrderAsync(
            VolumeBoundaryContext context,
            FirstStepFileLedgerEntry first,
            IReadOnlyList<int> missing,
            IReadOnlyList<FirstStepFileLedgerEntry> unassigned,
            int total,
            CancellationToken cancellationToken)
        {
            if (context.Engine == null
                || string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                || missing.Count == 0
                || missing.Count != unassigned.Count
                || unassigned.Count > 3)
            {
                return null;
            }

            var orderings = new List<IReadOnlyList<VolumeCandidate>>();

            foreach (IReadOnlyList<FirstStepFileLedgerEntry> permutation in Permute(unassigned))
            {
                var rest = new List<VolumeCandidate>(permutation.Count);

                for (int index = 0; index < permutation.Count; index++)
                {
                    rest.Add(new VolumeCandidate
                    {
                        Path = permutation[index].Path,
                        Size = permutation[index].Size
                    });
                }

                orderings.Add(rest);
            }

            if (orderings.Count == 0)
            {
                return null;
            }

            VolumeProbeOutcome outcome = await new VolumeProbeVerifier(context.Engine)
                .VerifyAsync(
                    first.Path,
                    orderings,
                    cancellationToken,
                    context.WorkspaceRoot,
                    VolumeContentFormat.SevenZip)
                .ConfigureAwait(false);

            if (!outcome.Confirmed
                || outcome.OrderedVolumes.Count != total)
            {
                return null;
            }

            var byPath = new Dictionary<string, FirstStepFileLedgerEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (FirstStepFileLedgerEntry entry in unassigned.Append(first))
            {
                byPath[Path.GetFullPath(entry.Path)] = entry;
            }

            var resolved = new Dictionary<int, FirstStepFileLedgerEntry>();

            for (int index = 0; index < outcome.OrderedVolumes.Count; index++)
            {
                string path = Path.GetFullPath(outcome.OrderedVolumes[index].Path);

                if (!byPath.TryGetValue(path, out FirstStepFileLedgerEntry? entry))
                {
                    return null;
                }

                int slot = index + 1;

                if (resolved.ContainsKey(slot))
                {
                    return null;
                }

                resolved[slot] = entry;
            }

            return resolved.Count == total ? resolved : null;
        }

        private static IEnumerable<IReadOnlyList<FirstStepFileLedgerEntry>> Permute(
            IReadOnlyList<FirstStepFileLedgerEntry> items)
        {
            if (items.Count == 0)
            {
                yield return Array.Empty<FirstStepFileLedgerEntry>();
                yield break;
            }

            var used = new bool[items.Count];
            var current = new FirstStepFileLedgerEntry[items.Count];

            IEnumerable<IReadOnlyList<FirstStepFileLedgerEntry>> Fill(int position)
            {
                if (position == items.Count)
                {
                    yield return current.ToArray();
                    yield break;
                }

                for (int index = 0; index < items.Count; index++)
                {
                    if (used[index])
                    {
                        continue;
                    }

                    used[index] = true;
                    current[position] = items[index];

                    foreach (IReadOnlyList<FirstStepFileLedgerEntry> result in Fill(position + 1))
                    {
                        yield return result;
                    }

                    used[index] = false;
                }
            }

            foreach (IReadOnlyList<FirstStepFileLedgerEntry> result in Fill(0))
            {
                yield return result;
            }
        }

        private static bool IsSevenZipCandidate(FirstStepFileLedgerEntry entry)
        {
            if (!entry.IsGatePassed || string.IsNullOrWhiteSpace(entry.Path))
            {
                return false;
            }

            if (entry.Format == VolumeContentFormat.SevenZip)
            {
                return true;
            }

            /*
             * 7z 中间片内容里没有魔数，格式只能是 Unknown；允许它们进候选，
             * 但必须是同基名、且不是别族已证实的成员。最终仍由首卷自述与字节数回答。
             */
            return entry.Format == VolumeContentFormat.Unknown
                && entry.Membership != VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember;
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

                // 同一物理文件的另一条名字：用户源优先；否则保留先出现的那条。
                if (entry.Source == VolumeHandoffSource.UserImported
                    && kept[index].Source == VolumeHandoffSource.ChainProduced)
                {
                    kept[index] = entry;
                }
            }

            return kept;
        }

        private static int? ResolveSlot(FirstStepFileLedgerEntry entry, int total)
        {
            int? byName = VolumeGroupDetector.TryGetVolumeIndex(Path.GetFileName(entry.Path));

            if (byName is >= 2 && byName.Value <= total)
            {
                return byName;
            }

            string name = Path.GetFileName(entry.Path);
            int lastDot = name.LastIndexOf('.');

            if (lastDot >= 0 && lastDot + 1 < name.Length
                && ExtensionHelper.TrySplitVolumeSegmentTolerant(
                    name[(lastDot + 1)..],
                    out string canonical,
                    out _)
                && canonical.Length == 3
                && canonical.All(char.IsAsciiDigit)
                && int.TryParse(canonical, out int number)
                && number >= 2
                && number <= total)
            {
                return number;
            }

            Match tail = Regex.Match(name, @"(\d{1,3})$");

            if (tail.Success
                && int.TryParse(tail.Groups[1].Value, out int tailNumber)
                && tailNumber >= 2
                && tailNumber <= total)
            {
                return tailNumber;
            }

            return null;
        }

        private static PreparedVolumeGroup Unknown(
            string baseName,
            IReadOnlyList<FirstStepFileLedgerEntry> entries,
            string evidence)
        {
            return new PreparedVolumeGroup
            {
                BaseName = baseName,
                Family = VolumeNamingFamily.SevenZipNumbered,
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
                Family = VolumeNamingFamily.SevenZipNumbered,
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

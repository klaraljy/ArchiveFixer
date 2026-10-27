using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **ZIP 族边界 resolver 的共用核**：跨盘/通用两族只差「末片怎么认、标准名怎么拼」。
    ///
    /// <para>内容能给的很少：跨盘只有末片盘号；通用只有首片本地头 + 末片 EOCD。
    /// 其余顺序靠基名/大小/后缀相似，最后由 <see cref="VolumeProbeVerifier"/> 真试开定案。</para>
    /// </summary>
    internal sealed class ZipBoundaryResolverCore
    {
        private readonly ZipVariant _variant;

        internal ZipBoundaryResolverCore(ZipVariant variant)
        {
            _variant = variant;
        }

        internal async Task<PreparedVolumeGroup?> ResolveAsync(
            VolumeBoundaryContext context,
            CancellationToken cancellationToken)
        {
            if (context == null || context.Ledger == null || string.IsNullOrWhiteSpace(context.BaseName))
            {
                return null;
            }

            List<FirstStepFileLedgerEntry> candidates = context.Ledger
                .SameBase(context.BaseName)
                .Where(entry => IsCandidate(entry))
                .ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            List<FirstStepFileLedgerEntry> unique = CollapsePhysicalDuplicates(candidates);

            if (unique.Count < 2)
            {
                return null;
            }

            return _variant == ZipVariant.Spanned
                ? await ResolveSpannedAsync(context, unique, cancellationToken).ConfigureAwait(false)
                : await ResolveNumberedAsync(context, unique, cancellationToken).ConfigureAwait(false);
        }

        private bool IsCandidate(FirstStepFileLedgerEntry entry)
        {
            if (!entry.IsGatePassed || string.IsNullOrWhiteSpace(entry.Path))
            {
                return false;
            }

            if (entry.Format == VolumeContentFormat.Zip)
            {
                if (_variant == ZipVariant.Spanned)
                {
                    return entry.ZipVariant == ZipVariant.Spanned
                        || entry.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember;
                }

                return entry.ZipVariant != ZipVariant.Spanned
                    && entry.Membership != VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember;
            }

            // 中间片可能没有魔数；同基名 + 大小由上层收进来，最终仍由试开/末片回答。
            return entry.Format == VolumeContentFormat.Unknown
                && entry.Membership != VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember;
        }

        private async Task<PreparedVolumeGroup?> ResolveSpannedAsync(
            VolumeBoundaryContext context,
            IReadOnlyList<FirstStepFileLedgerEntry> pieces,
            CancellationToken cancellationToken)
        {
            List<FirstStepFileLedgerEntry> tails = pieces
                .Where(entry => entry.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                    && entry.VolumeNumber is int number
                    && number >= 2)
                .ToList();

            if (tails.Count != 1)
            {
                return Unknown(context.BaseName, pieces, "跨盘 ZIP：末片盘号证据不唯一 ⇒ 判不出");
            }

            FirstStepFileLedgerEntry tail = tails[0];
            int total = tail.VolumeNumber!.Value;

            if (pieces.Count > total)
            {
                return SizeAnomaly(context.BaseName, pieces, $"跨盘 ZIP 末片自述 {total} 片，候选却有 {pieces.Count} 片");
            }

            var slots = new Dictionary<int, FirstStepFileLedgerEntry> { [total] = tail };

            List<FirstStepFileLedgerEntry> firsts = pieces
                .Where(entry => entry.HasZipLocalHeader && !ReferenceEquals(entry, tail))
                .ToList();

            if (firsts.Count > 1)
            {
                return Unknown(context.BaseName, pieces, "跨盘 ZIP：带本地头的首片有多个 ⇒ 判不出");
            }

            if (firsts.Count == 1)
            {
                slots[1] = firsts[0];
            }

            var unassigned = new List<FirstStepFileLedgerEntry>();

            foreach (FirstStepFileLedgerEntry entry in pieces)
            {
                if (slots.Values.Any(existing => ReferenceEquals(existing, entry)))
                {
                    continue;
                }

                int? slot = TrySpannedSlot(Path.GetFileName(entry.Path), total);

                if (slot == null)
                {
                    unassigned.Add(entry);
                    continue;
                }

                if (slots.ContainsKey(slot.Value))
                {
                    return SizeAnomaly(
                        context.BaseName,
                        pieces,
                        $"跨盘 ZIP 槽位 {slot.Value} 有两份不同文件");
                }

                slots[slot.Value] = entry;
            }

            List<int> missing = Enumerable.Range(1, total)
                .Except(slots.Keys)
                .OrderBy(number => number)
                .ToList();

            if (missing.Count != unassigned.Count)
            {
                if (pieces.Count < total)
                {
                    return new PreparedVolumeGroup
                    {
                        BaseName = context.BaseName,
                        Family = VolumeNamingFamily.ZipSpanned,
                        OriginalSourceLayer = FirstLayer(pieces),
                        OriginalEntryPath = tail.Path,
                        State = PreparedGroupState.Missing,
                        MissingNumbers = missing,
                        Evidence = $"跨盘 ZIP 末片自述 {total} 片，当前只收到 {pieces.Count} 片"
                    };
                }

                return SizeAnomaly(
                    context.BaseName,
                    pieces,
                    $"跨盘 ZIP 自述 {total} 片：缺槽 {missing.Count} 个、未定槽 {unassigned.Count} 片");
            }

            if (missing.Count > 0)
            {
                if (missing.Count == 1)
                {
                    slots[missing[0]] = unassigned[0];
                }
                else
                {
                    slots = await TryProbeSpannedAsync(
                            context,
                            pieces,
                            tail,
                            total,
                            slots,
                            cancellationToken)
                        .ConfigureAwait(false)
                        ?? slots;

                    if (Enumerable.Range(1, total).Any(slot => !slots.ContainsKey(slot)))
                    {
                        return Unknown(
                            context.BaseName,
                            pieces,
                            $"跨盘 ZIP 有 {unassigned.Count} 片未定槽，顺序不明且试开不成立 ⇒ 判不出");
                    }
                }
            }

            if (missing.Count > 0 && missing.Count == 1)
            {
                // 单槽消去法已经填完，继续走校验。
            }

            if (pieces.Count != total || Enumerable.Range(1, total).Any(slot => !slots.ContainsKey(slot)))
            {
                return new PreparedVolumeGroup
                {
                    BaseName = context.BaseName,
                    Family = VolumeNamingFamily.ZipSpanned,
                    OriginalSourceLayer = FirstLayer(pieces),
                    OriginalEntryPath = tail.Path,
                    State = PreparedGroupState.Missing,
                    MissingNumbers = missing,
                    Evidence = $"跨盘 ZIP 末片自述 {total} 片，当前只收到 {pieces.Count} 片"
                };
            }

            long fullSize = pieces.Max(entry => entry.Size);

            if (tail.Size > fullSize)
            {
                return SizeAnomaly(context.BaseName, pieces, "跨盘 ZIP 末片大于满片 ⇒ 大小结构异常");
            }

            IReadOnlyList<string> targets = VolumeNumberFromContent.BuildStandardNames(
                context.BaseName,
                VolumeNamingFamily.ZipSpanned,
                total);

            if (targets.Count != total)
            {
                return Unknown(context.BaseName, pieces, "跨盘 ZIP 标准名生成数量不符 ⇒ 判不出");
            }

            var prepared = new List<PreparedVolumePiece>();

            for (int slot = 1; slot <= total; slot++)
            {
                FirstStepFileLedgerEntry entry = slots[slot];
                string canonical = slot == total ? context.BaseName + ".zip" : targets[slot - 1];
                prepared.Add(new PreparedVolumePiece
                {
                    CanonicalName = canonical,
                    OriginalPath = entry.Path,
                    Size = entry.Size,
                    Source = entry.Source
                });
            }

            return new PreparedVolumeGroup
            {
                BaseName = context.BaseName,
                Family = VolumeNamingFamily.ZipSpanned,
                OriginalSourceLayer = FirstLayer(pieces),
                OriginalEntryPath = tail.Path,
                EntryCanonicalName = context.BaseName + ".zip",
                Pieces = prepared,
                State = PreparedGroupState.Complete,
                Evidence = $"跨盘 ZIP 末片盘号 {total - 1} ⇒ 总片数 {total}"
            };
        }

        private async Task<PreparedVolumeGroup?> ResolveNumberedAsync(
            VolumeBoundaryContext context,
            IReadOnlyList<FirstStepFileLedgerEntry> pieces,
            CancellationToken cancellationToken)
        {
            List<FirstStepFileLedgerEntry> tails = pieces
                .Where(entry => entry.ZipEndRecordMeansSplit)
                .ToList();

            if (tails.Count != 1)
            {
                return Unknown(context.BaseName, pieces, "通用 ZIP：末片 EOCD 证据不唯一 ⇒ 判不出");
            }

            FirstStepFileLedgerEntry tail = tails[0];
            int total = pieces.Count;

            var slots = new Dictionary<int, FirstStepFileLedgerEntry> { [total] = tail };

            List<FirstStepFileLedgerEntry> firsts = pieces
                .Where(entry => entry.HasZipLocalHeader && !ReferenceEquals(entry, tail))
                .ToList();

            if (firsts.Count > 1)
            {
                return Unknown(context.BaseName, pieces, "通用 ZIP：带本地头的首片有多个 ⇒ 判不出");
            }

            if (firsts.Count == 1)
            {
                slots[1] = firsts[0];
            }

            var unassigned = new List<FirstStepFileLedgerEntry>();

            foreach (FirstStepFileLedgerEntry entry in pieces)
            {
                if (slots.Values.Any(existing => ReferenceEquals(existing, entry)))
                {
                    continue;
                }

                int? slot = TryNumberedSlot(Path.GetFileName(entry.Path), total);

                if (slot == null)
                {
                    unassigned.Add(entry);
                    continue;
                }

                if (slots.ContainsKey(slot.Value))
                {
                    return SizeAnomaly(
                        context.BaseName,
                        pieces,
                        $"通用 ZIP 槽位 {slot.Value} 有两份不同文件");
                }

                slots[slot.Value] = entry;
            }

            List<int> missing = Enumerable.Range(1, total)
                .Except(slots.Keys)
                .OrderBy(number => number)
                .ToList();

            if (missing.Count != unassigned.Count)
            {
                return SizeAnomaly(
                    context.BaseName,
                    pieces,
                    $"通用 ZIP：缺槽 {missing.Count} 个、未定槽 {unassigned.Count} 片");
            }

            if (missing.Count == 1)
            {
                slots[missing[0]] = unassigned[0];
            }
            else if (missing.Count > 1)
            {
                if (total == 3 && firsts.Count == 1)
                {
                    FirstStepFileLedgerEntry middle = pieces.First(entry =>
                        !ReferenceEquals(entry, firsts[0]) && !ReferenceEquals(entry, tail));
                    slots[2] = middle;
                }
                else
                {
                    Dictionary<int, FirstStepFileLedgerEntry>? probed =
                        await TryProbeNumberedAsync(
                                context,
                                pieces,
                                tail,
                                total,
                                cancellationToken)
                            .ConfigureAwait(false);

                    if (probed == null)
                    {
                        return Unknown(
                            context.BaseName,
                            pieces,
                            $"通用 ZIP 有 {unassigned.Count} 片未定槽，顺序不明且试开不成立 ⇒ 判不出");
                    }

                    slots = probed;
                }
            }

            if (Enumerable.Range(1, total).Any(slot => !slots.ContainsKey(slot)))
            {
                return Unknown(context.BaseName, pieces, "通用 ZIP 槽位没有全部锁定 ⇒ 判不出");
            }

            long fullSize = pieces.Max(entry => entry.Size);

            if (tail.Size > fullSize)
            {
                return SizeAnomaly(context.BaseName, pieces, "通用 ZIP 末片大于满片 ⇒ 大小结构异常");
            }

            IReadOnlyList<string> targets = VolumeNumberFromContent.BuildStandardNames(
                context.BaseName,
                VolumeNamingFamily.ZipNumbered,
                total);

            if (targets.Count != total)
            {
                return Unknown(context.BaseName, pieces, "通用 ZIP 标准名生成数量不符 ⇒ 判不出");
            }

            var prepared = new List<PreparedVolumePiece>();

            for (int slot = 1; slot <= total; slot++)
            {
                FirstStepFileLedgerEntry entry = slots[slot];
                prepared.Add(new PreparedVolumePiece
                {
                    CanonicalName = targets[slot - 1],
                    OriginalPath = entry.Path,
                    Size = entry.Size,
                    Source = entry.Source
                });
            }

            return new PreparedVolumeGroup
            {
                BaseName = context.BaseName,
                Family = VolumeNamingFamily.ZipNumbered,
                OriginalSourceLayer = FirstLayer(pieces),
                OriginalEntryPath = firsts.Count == 1 ? firsts[0].Path : slots[1].Path,
                EntryCanonicalName = targets[0],
                Pieces = prepared,
                State = PreparedGroupState.Complete,
                Evidence = $"通用 ZIP 连成 1..{total}（首片本地头 / 末片 EOCD）；试开/名字已校验"
            };
        }

        private async Task<Dictionary<int, FirstStepFileLedgerEntry>?> TryProbeSpannedAsync(
            VolumeBoundaryContext context,
            IReadOnlyList<FirstStepFileLedgerEntry> pieces,
            FirstStepFileLedgerEntry tail,
            int total,
            IReadOnlyDictionary<int, FirstStepFileLedgerEntry> knownSlots,
            CancellationToken cancellationToken)
        {
            if (context.Engine == null
                || string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                || pieces.Count != total
                || total > 5)
            {
                return null;
            }

            IReadOnlyList<IReadOnlyList<string>> orders = VolumeOrderTrial.BuildCandidateOrders(
                pieces.Select(entry => entry.Path).ToList(),
                tail.Path,
                VolumeNamingFamily.ZipSpanned,
                context.BaseName);

            if (orders.Count == 0)
            {
                return null;
            }

            VolumeProbeOutcome outcome = await new VolumeProbeVerifier(context.Engine)
                .VerifySpannedZipOrderAsync(
                    orders,
                    context.PasswordCandidates,
                    context.WorkspaceRoot,
                    cancellationToken)
                .ConfigureAwait(false);

            return MapConfirmedOrder(pieces, tail, total, outcome, knownSlots);
        }

        private async Task<Dictionary<int, FirstStepFileLedgerEntry>?> TryProbeNumberedAsync(
            VolumeBoundaryContext context,
            IReadOnlyList<FirstStepFileLedgerEntry> pieces,
            FirstStepFileLedgerEntry tail,
            int total,
            CancellationToken cancellationToken)
        {
            if (context.Engine == null
                || string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                || total > 5)
            {
                return null;
            }

            IReadOnlyList<IReadOnlyList<string>> orders = VolumeOrderTrial.BuildCandidateOrders(
                pieces.Select(entry => entry.Path).ToList(),
                tail.Path,
                VolumeNamingFamily.ZipNumbered,
                context.BaseName);

            if (orders.Count == 0)
            {
                return null;
            }

            VolumeProbeOutcome outcome = await new VolumeProbeVerifier(context.Engine)
                .VerifyNumberedZipOrderAsync(orders, context.WorkspaceRoot, cancellationToken)
                .ConfigureAwait(false);

            return MapConfirmedOrder(pieces, tail, total, outcome, knownSlots: null);
        }

        private static Dictionary<int, FirstStepFileLedgerEntry>? MapConfirmedOrder(
            IReadOnlyList<FirstStepFileLedgerEntry> pieces,
            FirstStepFileLedgerEntry tail,
            int total,
            VolumeProbeOutcome outcome,
            IReadOnlyDictionary<int, FirstStepFileLedgerEntry>? knownSlots)
        {
            if (!outcome.Confirmed || outcome.OrderedVolumes.Count != total)
            {
                return null;
            }

            var byPath = new Dictionary<string, FirstStepFileLedgerEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (FirstStepFileLedgerEntry entry in pieces)
            {
                byPath[Path.GetFullPath(entry.Path)] = entry;
            }

            var slots = new Dictionary<int, FirstStepFileLedgerEntry>();

            for (int index = 0; index < outcome.OrderedVolumes.Count; index++)
            {
                string path = Path.GetFullPath(outcome.OrderedVolumes[index].Path);

                if (!byPath.TryGetValue(path, out FirstStepFileLedgerEntry? entry))
                {
                    return null;
                }

                slots[index + 1] = entry;
            }

            if (!ReferenceEquals(slots[total], tail))
            {
                return null;
            }

            if (knownSlots != null)
            {
                foreach (KeyValuePair<int, FirstStepFileLedgerEntry> pair in knownSlots)
                {
                    if (pair.Key == 1 && slots[1] != pair.Value)
                    {
                        return null;
                    }
                }
            }

            return slots;
        }

        private static int? TrySpannedSlot(string fileName, int total)
        {
            int lastDot = fileName.LastIndexOf('.');

            if (lastDot < 0 || lastDot + 1 >= fileName.Length)
            {
                return null;
            }

            string segment = fileName[(lastDot + 1)..];

            if (segment.Equals("zip", StringComparison.OrdinalIgnoreCase))
            {
                return total;
            }

            if (TryReadZipMarker(segment, out int marker, allowZip: false))
            {
                return marker >= 1 && marker < total ? marker : null;
            }

            return null;
        }

        private static int? TryNumberedSlot(string fileName, int total)
        {
            int lastDot = fileName.LastIndexOf('.');

            if (lastDot < 0 || lastDot + 1 >= fileName.Length)
            {
                return null;
            }

            string segment = fileName[(lastDot + 1)..];

            if (!TryReadZipMarker(segment, out int marker, allowZip: false))
            {
                return null;
            }

            return marker >= 1 && marker <= total ? marker : null;
        }

        private static bool TryReadZipMarker(string segment, out int marker, bool allowZip)
        {
            marker = 0;

            if (allowZip && segment.Equals("zip", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (ExtensionHelper.TrySplitVolumeSegmentTolerant(segment, out string canonical, out _))
            {
                segment = canonical;
            }

            string digits = new(segment.Where(char.IsAsciiDigit).ToArray());

            if (digits.Length is < 1 or > 3)
            {
                return false;
            }

            return int.TryParse(digits, out marker) && marker >= 1;
        }

        private static PreparedVolumeGroup Unknown(
            string baseName,
            IReadOnlyList<FirstStepFileLedgerEntry> entries,
            string evidence)
        {
            return new PreparedVolumeGroup
            {
                BaseName = baseName,
                Family = entries.Any(entry => entry.ZipVariant == ZipVariant.Spanned)
                    ? VolumeNamingFamily.ZipSpanned
                    : VolumeNamingFamily.ZipNumbered,
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
                Family = entries.Any(entry => entry.ZipVariant == ZipVariant.Spanned)
                    ? VolumeNamingFamily.ZipSpanned
                    : VolumeNamingFamily.ZipNumbered,
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
    }

    /// <summary>跨盘 ZIP 专属 resolver（末片 `.zip` 是入口）。</summary>
    public sealed class ZipSpannedBoundaryResolver : IVolumeBoundaryResolver
    {
        private readonly ZipBoundaryResolverCore _core = new(ZipVariant.Spanned);

        public VolumeNamingFamily Family => VolumeNamingFamily.ZipSpanned;

        public Task<PreparedVolumeGroup?> ResolveAsync(
            VolumeBoundaryContext context,
            CancellationToken cancellationToken = default) =>
            _core.ResolveAsync(context, cancellationToken);
    }

    /// <summary>通用 ZIP 分片专属 resolver（第 1 片 `.zip.001` 是入口）。</summary>
    public sealed class ZipNumberedBoundaryResolver : IVolumeBoundaryResolver
    {
        private readonly ZipBoundaryResolverCore _core = new(ZipVariant.Numbered);

        public VolumeNamingFamily Family => VolumeNamingFamily.ZipNumbered;

        public Task<PreparedVolumeGroup?> ResolveAsync(
            VolumeBoundaryContext context,
            CancellationToken cancellationToken = default) =>
            _core.ResolveAsync(context, cancellationToken);
    }
}

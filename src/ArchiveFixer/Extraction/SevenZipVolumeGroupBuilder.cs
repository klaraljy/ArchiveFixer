using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 7z 第二大步建组：候选顺序只来自事实适配器，最终由 VolumeProbeVerifier 试开确认。
    /// </summary>
    public sealed class SevenZipVolumeGroupBuilder
    {
        private readonly VolumeProbeVerifier _probe;

        public SevenZipVolumeGroupBuilder(VolumeProbeVerifier probe)
        {
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        }

        public async Task<VolumeGroupUnit?> TryBuildAsync(
            IReadOnlyList<VolumePieceFact> facts,
            string workRoot,
            CancellationToken cancellationToken = default)
        {
            if (facts == null || facts.Count < 2 || string.IsNullOrWhiteSpace(workRoot))
            {
                return null;
            }

            IReadOnlyList<IReadOnlyList<VolumePieceFact>> proposeds = SevenZipVolumeOrdering.Build(facts.ToArray());

            if (proposeds.Count == 0)
            {
                return null;
            }

            VolumePieceFact first = proposeds[0][0];
            var byPath = facts.ToDictionary(fact => fact.Path, StringComparer.OrdinalIgnoreCase);

            var orderings = proposeds
                .Select(ordering => (IReadOnlyList<VolumeCandidate>)ordering
                    .Skip(1)
                    .Select(fact => new VolumeCandidate { Path = fact.Path, Size = fact.Size })
                    .ToList())
                .Where(ordering => ordering.Count > 0)
                .ToList();

            VolumeProbeOutcome probe = await _probe.VerifyAsync(
                    first.Path,
                    orderings,
                    cancellationToken,
                    workRoot,
                    VolumeContentFormat.SevenZip)
                .ConfigureAwait(false);

            if (!probe.Confirmed || probe.OrderedVolumes.Count != facts.Count)
            {
                return null;
            }

            var orderedFacts = new List<VolumePieceFact>();

            foreach (VolumeCandidate candidate in probe.OrderedVolumes)
            {
                if (byPath.TryGetValue(candidate.Path, out VolumePieceFact? fact))
                {
                    orderedFacts.Add(fact);
                }
            }

            if (orderedFacts.Count != facts.Count)
            {
                return null;
            }

            string baseName = FileNameHelper.GetArchiveBaseName(first.Card.ArchiveBaseName);

            if (string.IsNullOrWhiteSpace(baseName))
            {
                return null;
            }

            IReadOnlyList<string> names = VolumeNumberFromContent.BuildStandardNames(
                baseName,
                VolumeNamingFamily.SevenZipNumbered,
                orderedFacts.Count);

            if (names.Count != orderedFacts.Count)
            {
                return null;
            }

            string staging = Path.Combine(workRoot, "sevenzip-group-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);

            var staged = new List<string>();

            for (int index = 0; index < orderedFacts.Count; index++)
            {
                string target = Path.Combine(staging, names[index]);

                if (!HardLinkHelper.CanHardLink(orderedFacts[index].Path, staging)
                    || !HardLinkHelper.TryCreateHardLink(target, orderedFacts[index].Path))
                {
                    return null;
                }

                staged.Add(target);
            }

            var unit = new VolumeGroupUnit(
                baseName,
                VolumeNamingFamily.SevenZipNumbered,
                orderedFacts[0].SourceLayer,
                staged[0])
            {
                ConfirmedOrder = staged,
                PieceOrigins = orderedFacts
            };

            unit.OwnerTaskFileName = orderedFacts[0].ProducerTaskFileName;

            return unit;
        }
    }
}

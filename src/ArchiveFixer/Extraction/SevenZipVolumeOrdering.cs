using System;
using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 7z 事实适配器：只把 PieceFact 喂给既有 BuildOrderings，不重写组算法。
    /// </summary>
    public static class SevenZipVolumeOrdering
    {
        public static IReadOnlyList<IReadOnlyList<VolumePieceFact>> Build(VolumePieceFact[] facts)
        {
            if (facts == null || facts.Length < 2)
            {
                return Array.Empty<IReadOnlyList<VolumePieceFact>>();
            }

            VolumePieceFact? first = facts.FirstOrDefault(fact =>
                fact.Card.BodyKind == ArchiveBodyKind.VolumeFirstPiece
                || fact.Card.DeclaredTotalBytes > 0);

            if (first == null)
            {
                return Array.Empty<IReadOnlyList<VolumePieceFact>>();
            }

            var byPath = facts.ToDictionary(fact => fact.Path, StringComparer.OrdinalIgnoreCase);
            var candidates = facts
                .Where(fact => !string.Equals(fact.Path, first.Path, StringComparison.OrdinalIgnoreCase))
                .Select(fact => new VolumeCandidate { Path = fact.Path, Size = fact.Size })
                .ToList();

            IReadOnlyList<IReadOnlyList<VolumeCandidate>> orderings =
                VolumeContentInference.BuildOrderings(first.Path, candidates);

            var result = new List<IReadOnlyList<VolumePieceFact>>();

            foreach (IReadOnlyList<VolumeCandidate> ordering in orderings)
            {
                var ordered = new List<VolumePieceFact> { first };

                foreach (VolumeCandidate candidate in ordering)
                {
                    if (byPath.TryGetValue(candidate.Path, out VolumePieceFact? fact))
                    {
                        ordered.Add(fact);
                    }
                }

                if (ordered.Count == facts.Length)
                {
                    result.Add(ordered);
                }
            }

            return result;
        }
    }
}

using ArchiveFixer.Detection;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Extraction;

/// <summary>跨盘 ZIP 收卷预检：只做判据和日志文案，不改工作区账本。</summary>
internal static class RecursiveSpannedZipStage
{
    internal static SpannedZipStageResult Run(
        string archivePath,
        string layerLabel,
        IReadOnlyList<VolumeCandidate> pool)
    {
        VolumeGatherPlanner.SpannedZipDiskGather spanned =
            VolumeNameRepair.ResolveSpannedZipDiskGather(archivePath, pool);

        if (!spanned.Applicable)
        {
            return new SpannedZipStageResult();
        }

        if (!spanned.Complete)
        {
            return new SpannedZipStageResult
            {
                Blocked = true,
                BlockedMessage = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.SpannedZipDisksLayerBlockedFormat,
                    layerLabel,
                    spanned.Detail)
            };
        }

        return new SpannedZipStageResult
        {
            LinkedPaths = spanned.LinkedPaths,
            LinkedSources = spanned.LinkedSources,
            LinkedMessage = spanned.LinkedCount > 0
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.SpannedZipDisksLayerLinkedFormat,
                    layerLabel,
                    Path.GetFileName(archivePath),
                    spanned.Detail)
                : string.Empty
        };
    }
}

using ArchiveFixer.Engines;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction;

/// <summary>候选列目录结果投影成层清单事实；只读，不记账。</summary>
internal static class RecursiveCandidateFacts
{
    internal static (LayerManifest Manifest, bool ListingSaidNoEncryption) FromListing(
        ArchiveListResult? listed)
    {
        LayerManifest manifest = listed is { Success: true }
            ? LayerManifest.From(listed)
            : LayerManifest.Unavailable(LayerManifest.DescribeListFailure(listed));

        return (manifest, listed is { Success: true, IsEncrypted: false });
    }
}

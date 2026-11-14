using System.Collections.Generic;

namespace ArchiveFixer.Extraction
{
    public readonly record struct EncryptedHeaderManifestRecoveryResult(
        LayerManifest Manifest,
        IReadOnlyList<(string Path, long Size)> Entries);
}

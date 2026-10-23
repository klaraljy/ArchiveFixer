using System;
using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 第一大步停下来的分卷片事实。只记录，不建组、不改名。
    /// </summary>
    public sealed class VolumePieceFact
    {
        public string Path { get; init; } = string.Empty;

        public string SourceLayer { get; init; } = string.Empty;

        public string ProducerTaskFileName { get; init; } = string.Empty;

        public FileNatureCard Card { get; init; } = null!;

        public long Size { get; init; }

        public ulong VolumeSerial { get; init; }

        public ulong FileIndex { get; init; }

        public string IdentityKey => VolumeSerial.ToString("X16", System.Globalization.CultureInfo.InvariantCulture)
            + ":"
            + FileIndex.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
    }
}

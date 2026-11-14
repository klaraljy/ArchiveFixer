using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction.VolumeStep
{
    /// <summary>一个基名的族选择结论。</summary>
    public sealed class VolumeFamilySelection
    {
        public VolumeNamingFamily? Family { get; init; }

        /// <summary>同一基名下出现两种以上的强锚点 ⇒ 不混组。</summary>
        public bool Conflict { get; init; }

        public string Evidence { get; init; } = string.Empty;

        public bool IsKnown => Family != null && !Conflict;
    }
}

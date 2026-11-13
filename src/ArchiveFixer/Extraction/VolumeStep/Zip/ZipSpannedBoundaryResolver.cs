using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction
{
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
}

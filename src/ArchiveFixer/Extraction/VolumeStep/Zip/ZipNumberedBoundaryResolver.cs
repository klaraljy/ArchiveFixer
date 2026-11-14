using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction.VolumeStep
{
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

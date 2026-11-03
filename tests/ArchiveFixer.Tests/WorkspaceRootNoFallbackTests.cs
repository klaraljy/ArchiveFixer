using ArchiveFixer.Models;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    public class WorkspaceRootNoFallbackTests
    {
        [Fact]
        public void UnresolvedWorkspaceRootDoesNotFallBackToDataRootWork()
        {
            var pathService = new PathService
            {
                DataRootDirectory = @"C:\data-root"
            };

            Assert.False(pathService.WorkspaceRootResolved);
            Assert.Equal(string.Empty, pathService.WorkDirectory);
            Assert.Equal(
                string.Empty,
                pathService.BuildTaskWorkDirectory(new ArchiveTask(@"C:\in\a.7z")));
        }

        [Fact]
        public void ResolvedWorkspaceRootIsUsedAsIs()
        {
            var pathService = new PathService
            {
                DataRootDirectory = @"C:\data-root",
                WorkDirectory = @"D:\out\.ArchiveFixer.work"
            };

            Assert.True(pathService.WorkspaceRootResolved);
            Assert.Equal(@"D:\out\.ArchiveFixer.work", pathService.WorkDirectory);
            Assert.StartsWith(
                @"D:\out\.ArchiveFixer.work\",
                pathService.BuildTaskWorkDirectory(new ArchiveTask(@"C:\in\a.7z")),
                System.StringComparison.Ordinal);
        }
    }
}

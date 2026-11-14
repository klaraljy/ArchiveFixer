using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    public class AppSettingsToolPathTests
    {
        [Fact]
        public void OfflineDriveToolPathIsPreserved()
        {
            var settings = AppSettings.CreateDefault();
            settings.CustomSevenZipExePath = @"\\offline-host\share\7z.exe";

            settings.Normalize();

            Assert.Equal(@"\\offline-host\share\7z.exe", settings.CustomSevenZipExePath);
        }

        [Fact]
        public void MissingFileOnOnlineDriveIsCleared()
        {
            var settings = AppSettings.CreateDefault();
            settings.CustomSevenZipExePath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "af-missing-" + System.Guid.NewGuid().ToString("N") + ".exe");

            settings.Normalize();

            Assert.Equal(string.Empty, settings.CustomSevenZipExePath);
        }
    }
}

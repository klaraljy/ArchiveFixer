using System;
using System.IO;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    public class SettingsSnapshotTests
    {
        [Fact]
        public void SnapshotDoesNotFollowWorkingCopyMutation()
        {
            AppSettings settings = AppSettings.CreateDefault();
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.UseSpecificExtraction = false;

            SettingsSnapshot snapshot = SettingsSnapshot.Capture(settings);

            settings.SourceHandling = nameof(SourceHandlingMode.DeleteInPlace);
            settings.UseSpecificExtraction = true;

            Assert.Equal(nameof(SourceHandlingMode.KeepInPlace), snapshot.SourceHandling);
            Assert.False(snapshot.UseSpecificExtraction);
            Assert.True(snapshot.DiffersFrom(settings));
        }

    }
}

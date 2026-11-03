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

        [Fact]
        public void LegacyMigrationRunsOnceAndKeepsBackup()
        {
            string root = Path.Combine(Path.GetTempPath(), "af-settings-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                string settingsPath = Path.Combine(root, "appsettings.json");
                File.WriteAllText(settingsPath, "{}");

                AppSettings settings = AppSettings.CreateDefault();

                LegacySettingsMigration.Result first = LegacySettingsMigration.EnsureMigrated(
                    settings,
                    settingsPath,
                    root);

                Assert.True(first.Ran);
                Assert.True(first.Succeeded);
                Assert.True(File.Exists(first.BackupPath));
                Assert.True(File.Exists(Path.Combine(root, LegacySettingsMigration.VersionFileName)));

                LegacySettingsMigration.Result second = LegacySettingsMigration.EnsureMigrated(
                    settings,
                    settingsPath,
                    root);

                Assert.False(second.Ran);
                Assert.True(second.Succeeded);
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }
    }
}

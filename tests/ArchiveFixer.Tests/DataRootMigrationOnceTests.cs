using System;
using System.IO;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    public class DataRootMigrationOnceTests
    {
        [Fact]
        public void SecondMigrationCallIsSkippedByVersionMarker()
        {
            string root = Path.Combine(Path.GetTempPath(), "af-dataroot-once-" + Guid.NewGuid().ToString("N"));
            string program = Path.Combine(root, "program");
            string oldData = Path.Combine(program, "data");
            string destination = Path.Combine(root, "destination");

            Directory.CreateDirectory(oldData);
            File.WriteAllText(Path.Combine(oldData, "passwords.txt"), "old-a\n");

            try
            {
                DataRootMigrator.MigrateIfNeeded(program, destination);

                File.Delete(Path.Combine(oldData, "passwords.txt"));
                File.WriteAllText(Path.Combine(oldData, "passwords.txt"), "old-b\n");

                DataRootMigrator.MigrateIfNeeded(program, destination);

                Assert.True(
                    File.Exists(Path.Combine(destination, DataRootMigrator.VersionFileName)),
                    "迁移成功后必须写版本标记");
                Assert.Equal("old-a\n", File.ReadAllText(Path.Combine(destination, "passwords.txt")));
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

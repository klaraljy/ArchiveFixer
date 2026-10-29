using System;
using System.IO;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class DataRootMigrationTests : IDisposable
    {
        private readonly string _root;

        public DataRootMigrationTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerDataRoot", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }

        [Fact]
        public void 固定数据根在LocalAppData下_并且不跟程序目录走()
        {
            string expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                PathService.DataFolderName);

            Assert.Equal(expected, PathService.DefaultDataRootDirectory);
            Assert.DoesNotContain("ArchiveFixer\\data", PathService.DefaultDataRootDirectory, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void 迁移设置和密码_但不覆盖新文件也不删除旧文件()
        {
            string programDir = Path.Combine(_root, "program");
            string oldData = Path.Combine(programDir, "data");
            string newData = Path.Combine(_root, "localappdata", "ArchiveFixer");
            Directory.CreateDirectory(oldData);
            Directory.CreateDirectory(newData);

            File.WriteAllText(Path.Combine(oldData, "appsettings.json"), "{\"source\":\"old\"}");
            File.WriteAllText(Path.Combine(oldData, "passwords.txt"), "old-password");
            File.WriteAllText(Path.Combine(oldData, "passwords.meta.json"), "{\"old\":true}");
            File.WriteAllText(Path.Combine(oldData, "password-list.dat"), "encrypted-old");
            File.WriteAllText(Path.Combine(newData, "appsettings.json"), "{\"source\":\"new\"}");

            var copied = DataRootMigrator.MigrateIfNeeded(programDir, newData);

            Assert.Equal("{\"source\":\"new\"}", File.ReadAllText(Path.Combine(newData, "appsettings.json")));
            Assert.Equal("old-password", File.ReadAllText(Path.Combine(newData, "passwords.txt")));
            Assert.Equal("{\"old\":true}", File.ReadAllText(Path.Combine(newData, "passwords.meta.json")));
            Assert.Equal("encrypted-old", File.ReadAllText(Path.Combine(newData, "password-list.dat")));
            Assert.Contains(copied, path => path.EndsWith("passwords.txt", StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(Path.Combine(oldData, "passwords.txt")));
        }
    }
}

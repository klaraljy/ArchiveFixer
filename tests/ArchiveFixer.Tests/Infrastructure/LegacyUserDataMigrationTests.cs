using System;
using System.IO;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class LegacyUserDataMigrationTests : IDisposable
    {
        private readonly string _root;

        public LegacyUserDataMigrationTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af-legacy-migration-" + Guid.NewGuid().ToString("N"));
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
        public void 旧AppData只补目标缺失的设置和密码_不复制日志()
        {
            string target = Path.Combine(_root, "target");
            string roaming = Path.Combine(_root, "roaming");
            Directory.CreateDirectory(roaming);
            File.WriteAllText(Path.Combine(roaming, "appsettings.json"), "{\"old\":true}");
            File.WriteAllText(Path.Combine(roaming, "passwords.txt"), "old-pass");
            File.WriteAllText(Path.Combine(roaming, "ArchiveFixer_old.log"), "log");

            var copied = LegacyUserDataMigration.MigrateIfNeeded(
                Path.Combine(_root, "program"),
                target,
                roaming);

            Assert.Contains("appsettings.json", copied);
            Assert.Contains("passwords.txt", copied);
            Assert.True(File.Exists(Path.Combine(target, "appsettings.json")));
            Assert.True(File.Exists(Path.Combine(target, "passwords.txt")));
            Assert.False(File.Exists(Path.Combine(target, "ArchiveFixer_old.log")));
            Assert.True(File.Exists(Path.Combine(target, LegacyUserDataMigration.VersionFileName)));
        }

        [Fact]
        public void 主密码本已存在时不把旧明文备份带回数据根()
        {
            string target = Path.Combine(_root, "target");
            string roaming = Path.Combine(_root, "roaming");
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(roaming);
            File.WriteAllText(Path.Combine(target, "passwords.txt"), "current");
            File.WriteAllText(Path.Combine(roaming, "passwords.txt.bak"), "old-backup");
            File.WriteAllText(Path.Combine(roaming, "passwords.meta.json.bak"), "[]");

            var copied = LegacyUserDataMigration.MigrateIfNeeded(
                Path.Combine(_root, "program"),
                target,
                roaming);

            Assert.DoesNotContain("passwords.txt.bak", copied);
            Assert.Contains("passwords.meta.json.bak", copied);
            Assert.Equal("current", File.ReadAllText(Path.Combine(target, "passwords.txt")));
            Assert.False(File.Exists(Path.Combine(target, "passwords.txt.bak")));
        }

        [Fact]
        public void 主密码本缺失时旧备份可以补入()
        {
            string target = Path.Combine(_root, "target");
            string roaming = Path.Combine(_root, "roaming");
            Directory.CreateDirectory(roaming);
            File.WriteAllText(Path.Combine(roaming, "passwords.txt.bak"), "old-backup");

            var copied = LegacyUserDataMigration.MigrateIfNeeded(
                Path.Combine(_root, "program"),
                target,
                roaming);

            Assert.Contains("passwords.txt.bak", copied);
            Assert.Equal("old-backup", File.ReadAllText(Path.Combine(target, "passwords.txt.bak")));
        }

        [Fact]
        public void 目标已有当前密码时绝不覆盖_只补缺失设置()
        {
            string target = Path.Combine(_root, "target");
            string program = Path.Combine(_root, "program");
            string oldData = Path.Combine(program, "data");
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(oldData);
            File.WriteAllText(Path.Combine(target, "passwords.txt"), "current-pass");
            File.WriteAllText(Path.Combine(oldData, "passwords.txt"), "old-pass");
            File.WriteAllText(Path.Combine(oldData, "appsettings.json"), "{\"old\":1}");

            var copied = LegacyUserDataMigration.MigrateIfNeeded(program, target, Path.Combine(_root, "none"));

            Assert.Equal("current-pass", File.ReadAllText(Path.Combine(target, "passwords.txt")));
            Assert.Contains("appsettings.json", copied);
            Assert.DoesNotContain("passwords.txt", copied);
        }

        [Fact]
        public void 源文件暂时读不出时不写迁移标记_解锁后下一次还能补()
        {
            string target = Path.Combine(_root, "target");
            string roaming = Path.Combine(_root, "roaming");
            Directory.CreateDirectory(roaming);
            string source = Path.Combine(roaming, "passwords.txt");
            File.WriteAllText(source, "old-pass");

            using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var first = LegacyUserDataMigration.MigrateIfNeeded(
                    Path.Combine(_root, "program"),
                    target,
                    roaming);

                Assert.DoesNotContain("passwords.txt", first);
                Assert.False(File.Exists(Path.Combine(target, LegacyUserDataMigration.VersionFileName)));
            }

            var second = LegacyUserDataMigration.MigrateIfNeeded(
                Path.Combine(_root, "program"),
                target,
                roaming);

            Assert.Contains("passwords.txt", second);
            Assert.True(File.Exists(Path.Combine(target, "passwords.txt")));
        }

        [Fact]
        public void 迁移标记存在后不再重复扫描()
        {
            string target = Path.Combine(_root, "target");
            string roaming = Path.Combine(_root, "roaming");
            Directory.CreateDirectory(roaming);
            File.WriteAllText(Path.Combine(roaming, "passwords.txt"), "old-pass");

            LegacyUserDataMigration.MigrateIfNeeded(
                Path.Combine(_root, "program"),
                target,
                roaming);

            File.WriteAllText(Path.Combine(target, "second.txt"), "x");
            var copied = LegacyUserDataMigration.MigrateIfNeeded(
                Path.Combine(_root, "program"),
                target,
                roaming);

            Assert.Empty(copied);
        }
    }
}

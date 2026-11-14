using System;
using System.IO;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Storage;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class SuffixAliasReconcilerTests : IDisposable
    {
        private readonly string _root;

        public SuffixAliasReconcilerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSuffixAlias", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
            }
        }

        [Fact]
        public void 后缀清洗后_标准名收回原名_不留下第二个名字()
        {
            string original = Path.Combine(_root, "真真.7z.001删");
            string renamed = Path.Combine(_root, "真真.7z.001");
            string backup = Path.Combine(_root, "backup.bin");

            File.WriteAllText(original, "source-bytes");
            Assert.True(HardLinkHelper.TryCreateHardLink(backup, original));

            // 模拟后缀清洗：原名被 Move 成标准名。
            FileOperations.Move(original, renamed);

            var changed = SuffixAliasReconciler.Reconcile(
                new[] { (original, renamed) },
                _ => backup);

            Assert.Single(changed);
            Assert.True(File.Exists(original));
            Assert.False(File.Exists(renamed));
            Assert.True(FileIdentity.IsSamePhysicalFile(original, backup));
        }

        [Fact]
        public void 原名和标准名都在且同一份_收掉标准名()
        {
            string original = Path.Combine(_root, "真真.7z.001删");
            string renamed = Path.Combine(_root, "真真.7z.001");
            string backup = Path.Combine(_root, "backup.bin");

            File.WriteAllText(original, "source-bytes");
            Assert.True(HardLinkHelper.TryCreateHardLink(backup, original));
            Assert.True(HardLinkHelper.TryCreateHardLink(renamed, original));

            var changed = SuffixAliasReconciler.Reconcile(
                new[] { (original, renamed) },
                _ => backup);

            Assert.Single(changed);
            Assert.True(File.Exists(original));
            Assert.False(File.Exists(renamed));
            Assert.True(FileIdentity.IsSamePhysicalFile(original, backup));
        }
    }
}

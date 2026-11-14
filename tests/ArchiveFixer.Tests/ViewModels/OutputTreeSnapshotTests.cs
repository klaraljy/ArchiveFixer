using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class OutputTreeSnapshotTests
    {
        [Fact]
        public void 一次遍历后_文件数相对路径与总字节一致()
        {
            string root = Path.Combine(Path.GetTempPath(), "af-snapshot-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "a", "b"));

            try
            {
                File.WriteAllBytes(Path.Combine(root, "root.bin"), new byte[3]);
                File.WriteAllBytes(Path.Combine(root, "a", "one.bin"), new byte[5]);
                File.WriteAllBytes(Path.Combine(root, "a", "b", "two.bin"), new byte[7]);

                OutputTreeSnapshot snapshot = OutputTreeSnapshot.Capture(root);

                Assert.Equal(3, snapshot.FileCount);
                Assert.Equal(15, snapshot.TotalSize);
                Assert.Equal(3, snapshot.FilesByRelativePath["root.bin"]);
                Assert.Equal(5, snapshot.FilesByRelativePath["a/one.bin"]);
                Assert.Equal(7, snapshot.FilesByRelativePath["a/b/two.bin"]);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void 空目录和不存在的目录都是零()
        {
            string root = Path.Combine(Path.GetTempPath(), "af-snapshot-empty-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                OutputTreeSnapshot empty = OutputTreeSnapshot.Capture(root);
                OutputTreeSnapshot missing = OutputTreeSnapshot.Capture(Path.Combine(root, "nope"));

                Assert.Equal(0, empty.FileCount);
                Assert.Equal(0, empty.TotalSize);
                Assert.Equal(0, missing.FileCount);
                Assert.Equal(0, missing.TotalSize);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Verify_使用传入快照_不再回读磁盘()
        {
            string root = Path.Combine(Path.GetTempPath(), "af-snapshot-verify-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string output = Path.Combine(root, "out");
            Directory.CreateDirectory(output);
            string file = Path.Combine(output, "content.bin");
            File.WriteAllBytes(file, new byte[10]);

            try
            {
                OutputTreeSnapshot snapshot = OutputTreeSnapshot.Capture(output);
                File.Delete(file);

                OutputVerificationResult result = OutputVerifier.Verify(
                    output,
                    Expected(("content.bin", 10)),
                    snapshot: snapshot);

                Assert.True(result.Verified, result.Message);
                Assert.True(result.ManifestCrossChecked);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static ArchiveListResult Expected(params (string Path, long Size)[] entries)
        {
            var list = new List<ArchiveEntry>();

            foreach ((string path, long size) in entries)
            {
                list.Add(new ArchiveEntry { Path = path, Size = size, IsDirectory = false });
            }

            return new ArchiveListResult
            {
                Success = true,
                Entries = list,
                FileCount = list.Count,
                TotalUncompressedSize = 0
            };
        }
    }
}

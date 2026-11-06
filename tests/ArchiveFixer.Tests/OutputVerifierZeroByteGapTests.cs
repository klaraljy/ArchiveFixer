using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 清单逐条核对后的“只缺 0 字节条目”政策：
    /// 内容字节完整 ⇒ 可以发布，但完整性不升 Complete、源包不删。
    /// 非空条目缺失仍判失败，不能拿这条政策放宽。
    /// </summary>
    public sealed class OutputVerifierZeroByteGapTests : IDisposable
    {
        private readonly string _root;

        public OutputVerifierZeroByteGapTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af-output-zero-gap-" + Guid.NewGuid().ToString("N"));
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
        public void 只缺空条目_内容可发布但不允许删源()
        {
            string output = Path.Combine(_root, "out");
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, "content.bin"), new byte[10]);

            var expected = new ArchiveListResult
            {
                Success = true,
                FileCount = 2,
                TotalUncompressedSize = 10,
                Entries = new List<ArchiveEntry>
                {
                    new() { Path = "content.bin", Size = 10 },
                    new() { Path = "empty.txt", Size = 0 }
                }
            };

            OutputVerificationResult result = OutputVerifier.Verify(output, expected);

            Assert.True(result.Verified, "内容字节完整，产物本身可以发布");
            Assert.False(result.ManifestCrossChecked, "缺空条目仍不是可证完整");
            Assert.True(result.OnlyZeroByteEntriesMissing);
            ArchiveEntry missing = Assert.Single(result.MissingEntries);
            Assert.Equal("empty.txt", missing.Path);
            Assert.False(result.AllowsSourceRemoval, "缺条目时绝不允许删源包");
        }

        [Fact]
        public void 缺非空条目_仍然失败()
        {
            string output = Path.Combine(_root, "out-nonempty");
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, "content.bin"), new byte[10]);

            var expected = new ArchiveListResult
            {
                Success = true,
                FileCount = 2,
                TotalUncompressedSize = 20,
                Entries = new List<ArchiveEntry>
                {
                    new() { Path = "content.bin", Size = 10 },
                    new() { Path = "missing.bin", Size = 10 }
                }
            };

            OutputVerificationResult result = OutputVerifier.Verify(output, expected);

            Assert.False(result.Verified);
            Assert.False(result.ManifestCrossChecked);
            Assert.False(result.OnlyZeroByteEntriesMissing);
            Assert.Single(result.MissingEntries);
            Assert.False(result.AllowsSourceRemoval);
        }

        [Fact]
        public void 只缺空条目_L4仍判不出且不允许删源()
        {
            var result = new OutputVerificationResult
            {
                Verified = true,
                ManifestCrossChecked = false,
                OnlyZeroByteEntriesMissing = true,
                ExpectedFileCount = 2,
                ActualFileCount = 1,
                ExpectedTotalSize = 10,
                ActualTotalSize = 10,
                Message = "内容字节完整，但清单核对缺 1 个 0 字节条目"
            };

            ResultCompletenessVerdict verdict = ResultCompletenessClassifier.Classify(result);

            Assert.Equal(ResultCompleteness.Undeterminable, verdict.State);
            Assert.False(verdict.AllowsSourceRemoval);
            Assert.Contains("0 字节", verdict.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 清单逐条条目要随LayerManifest保留()
        {
            LayerManifest manifest = LayerManifest.From(new ArchiveListResult
            {
                Success = true,
                FileCount = 2,
                TotalUncompressedSize = 10,
                Entries = new List<ArchiveEntry>
                {
                    new() { Path = "content.bin", Size = 10 },
                    new() { Path = "empty.txt", Size = 0 },
                    new() { Path = "dir", Size = 0, IsDirectory = true }
                }
            });

            Assert.True(manifest.Available);
            Assert.Equal(2, manifest.FileCount);
            Assert.Equal(2, manifest.Entries.Count);
            Assert.Equal(2, manifest.ToExpected().Entries.Count);
            Assert.Equal(2, manifest.ToExpected().FileCount);
        }
    }
}

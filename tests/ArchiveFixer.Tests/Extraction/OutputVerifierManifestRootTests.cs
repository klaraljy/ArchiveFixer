using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 叶子层清单的路径根测试：预期条目可能落在最终产物树的一层子目录下；
    /// 缺非空条目即使被额外文件补足数量/字节，也必须判失败。
    /// </summary>
    public sealed class OutputVerifierManifestRootTests : IDisposable
    {
        private readonly string _root;

        public OutputVerifierManifestRootTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af-output-manifest-root-" + Guid.NewGuid().ToString("N"));
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
        public void 缺非空条目_额外文件补足数量与字节也必须失败()
        {
            string output = Path.Combine(_root, "out-missing");
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, "content.bin"), new byte[10]);
            File.WriteAllBytes(Path.Combine(output, "extra.bin"), new byte[10]);

            OutputVerificationResult result = OutputVerifier.Verify(output, Expected(
                ("content.bin", 10),
                ("missing.bin", 10)));

            Assert.False(result.Verified, result.Message);
            Assert.False(result.ManifestCrossChecked);
            Assert.False(result.AllowsSourceRemoval);
            Assert.Single(result.MissingEntries);
        }

        [Fact]
        public void 叶子层清单_带产物根前缀时能逐条对上()
        {
            string output = Path.Combine(_root, "out-nested");
            string nested = Path.Combine(output, "laowang-win-3955068");
            string expectedRoot = Path.Combine(nested, "laowang-win");
            Directory.CreateDirectory(expectedRoot);
            File.WriteAllBytes(Path.Combine(expectedRoot, "laowang.exe"), new byte[10]);

            OutputVerificationResult result = OutputVerifier.Verify(
                output,
                Expected(("laowang-win\\laowang.exe", 10)),
                nested);

            Assert.True(result.Verified, result.Message);
            Assert.True(result.ManifestCrossChecked);
            Assert.True(result.AllowsSourceRemoval);
            Assert.Empty(result.MissingEntries);
        }

        [Fact]
        public void 清单根未知_唯一后缀匹配可证内容完整()
        {
            string output = Path.Combine(_root, "out-suffix");
            string nested = Path.Combine(output, "laowang-win-3955068", "laowang-win");
            Directory.CreateDirectory(nested);
            File.WriteAllBytes(Path.Combine(nested, "laowang.exe"), new byte[10]);

            OutputVerificationResult result = OutputVerifier.Verify(
                output,
                Expected(("laowang-win\\laowang.exe", 10)));

            Assert.True(result.Verified, result.Message);
            Assert.True(result.ManifestCrossChecked);
            Assert.True(result.AllowsSourceRemoval);
            Assert.Contains("路径后缀", result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 大小不符_即使总字节够也必须失败()
        {
            string output = Path.Combine(_root, "out-size");
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, "content.bin"), new byte[9]);
            File.WriteAllBytes(Path.Combine(output, "extra.bin"), new byte[1]);

            OutputVerificationResult result = OutputVerifier.Verify(
                output,
                Expected(("content.bin", 10)));

            Assert.False(result.Verified, result.Message);
            Assert.False(result.ManifestCrossChecked);
            Assert.Empty(result.MissingEntries);
        }

        [Fact]
        public void 产物根在产物目录外_只做底线校验且不允许删源()
        {
            string output = Path.Combine(_root, "out-outside");
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, "content.bin"), new byte[10]);

            string outside = Path.Combine(_root, "other-root");
            Directory.CreateDirectory(outside);

            OutputVerificationResult result = OutputVerifier.Verify(
                output,
                Expected(("content.bin", 10)),
                outside);

            Assert.True(result.Verified, result.Message);
            Assert.False(result.Known);
            Assert.False(result.ManifestCrossChecked);
            Assert.False(result.AllowsSourceRemoval);
        }

        [Fact]
        public void 额外文件但预期全部对上_仍然通过()
        {
            string output = Path.Combine(_root, "out-extra");
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, "content.bin"), new byte[10]);
            File.WriteAllBytes(Path.Combine(output, "extra.bin"), new byte[3]);

            OutputVerificationResult result = OutputVerifier.Verify(
                output,
                Expected(("content.bin", 10)));

            Assert.True(result.Verified, result.Message);
            Assert.True(result.ManifestCrossChecked);
            Assert.Empty(result.MissingEntries);
        }

        private static ArchiveListResult Expected(params (string Path, long Size)[] entries)
        {
            var list = new List<ArchiveEntry>();

            long total = 0;

            foreach ((string path, long size) in entries)
            {
                list.Add(new ArchiveEntry { Path = path, Size = size });
                total += size;
            }

            return new ArchiveListResult
            {
                Success = true,
                FileCount = list.Count,
                TotalUncompressedSize = total,
                Entries = list
            };
        }
    }
}

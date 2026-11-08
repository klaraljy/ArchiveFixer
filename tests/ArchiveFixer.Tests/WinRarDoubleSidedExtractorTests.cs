using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ArchiveFixer.Engines.WinRar;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class WinRarDoubleSidedExtractorTests : IDisposable
    {
        private readonly string _root;

        public WinRarDoubleSidedExtractorTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "WinRarDoubleSided-" + Guid.NewGuid().ToString("N"));
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
        public async Task 双面zip_WinRAR直解_输出正确且源文件不动()
        {
            if (!new WinRarDoubleSidedExtractor().IsAvailable)
            {
                return;
            }

            string zipPath = Path.Combine(_root, "inner.zip");

            using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                using (StreamWriter first = new(zip.CreateEntry("第一条.txt").Open()))
                {
                    first.Write("first");
                }

                using (StreamWriter second = new(zip.CreateEntry("第二条.txt").Open()))
                {
                    second.Write("second");
                }
            }

            byte[] zipBytes = File.ReadAllBytes(zipPath);
            byte[] prefix = new byte[1024 * 1024];
            new Random(914).NextBytes(prefix);

            string source = Path.Combine(_root, "double.mp4");
            using (FileStream stream = File.Create(source))
            {
                stream.Write(prefix);
                stream.Write(zipBytes);
            }

            string beforeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
            string output = Path.Combine(_root, "out");

            WinRarDoubleSidedExtractResult result = await new WinRarDoubleSidedExtractor().TryExtractAsync(
                source,
                output);

            Assert.True(result.Success, result.Message);
            Assert.Equal("first", File.ReadAllText(
                Assert.Single(Directory.GetFiles(output, "第一条.txt", SearchOption.AllDirectories))));
            Assert.Equal("second", File.ReadAllText(
                Assert.Single(Directory.GetFiles(output, "第二条.txt", SearchOption.AllDirectories))));
            Assert.Equal(beforeHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
            Assert.Empty(Directory.GetFiles(_root, "af-winrar-*", SearchOption.AllDirectories));
        }
    }
}

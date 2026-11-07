using System;
using System.IO;
using System.IO.Compression;
using ArchiveFixer.Detection;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// Bounded archive-header fuzz: random single-byte mutations of a valid ZIP must
    /// never throw out of the read-only nature detector.
    /// </summary>
    public sealed class ArchiveMutationFuzzTests : IDisposable
    {
        private readonly string _root;

        public ArchiveMutationFuzzTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerFuzz-" + Guid.NewGuid().ToString("N"));
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
        public void 单字节随机破坏_文件性质探测不抛异常()
        {
            string seedPath = Path.Combine(_root, "seed.zip");

            using (ZipArchive zip = ZipFile.Open(seedPath, ZipArchiveMode.Create))
            {
                using StreamWriter writer = new(zip.CreateEntry("payload.txt").Open());
                writer.Write("ArchiveFixer fuzz seed");
            }

            byte[] seed = File.ReadAllBytes(seedPath);
            var random = new Random(20261107);

            for (int round = 0; round < 96; round++)
            {
                byte[] mutated = (byte[])seed.Clone();
                int offset = random.Next(mutated.Length);
                mutated[offset] ^= (byte)random.Next(1, 256);

                string path = Path.Combine(_root, $"mutated-{round:D3}.zip");
                File.WriteAllBytes(path, mutated);

                FileNatureCard card = FileNature.Inspect(path);
                Assert.True(Enum.IsDefined(typeof(ArchiveBodyKind), card.BodyKind));
            }
        }
    }
}

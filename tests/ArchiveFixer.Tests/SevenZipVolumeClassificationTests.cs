using System;
using System.IO;
using ArchiveFixer.Engines.SevenZip;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class SevenZipVolumeClassificationTests : IDisposable
    {
        private readonly string _root;

        public SevenZipVolumeClassificationTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "SevenZipVolumeClass-" + Guid.NewGuid().ToString("N"));
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
        public void 完整分卷组打不开_应落损坏而不是缺卷()
        {
            string first = Path.Combine(_root, "pack.7z.001");
            File.WriteAllBytes(first, new byte[16]);
            File.WriteAllBytes(Path.Combine(_root, "pack.7z.002"), new byte[16]);
            File.WriteAllBytes(Path.Combine(_root, "pack.7z.003"), new byte[16]);

            string type = SevenZipProcessRunner.ResolveVolumeMissingErrorType("VolumeMissing", first);

            Assert.Equal("CorruptedArchive", type);
        }

        [Fact]
        public void 分卷组缺号_仍然落缺卷()
        {
            string first = Path.Combine(_root, "pack.7z.001");
            File.WriteAllBytes(first, new byte[16]);
            File.WriteAllBytes(Path.Combine(_root, "pack.7z.003"), new byte[16]);

            string type = SevenZipProcessRunner.ResolveVolumeMissingErrorType("VolumeMissing", first);

            Assert.Equal("VolumeMissing", type);
        }

        [Fact]
        public void 只有第一卷没有兄弟卷_不擅自改损坏()
        {
            string first = Path.Combine(_root, "solo.7z.001");
            File.WriteAllBytes(first, new byte[16]);

            string type = SevenZipProcessRunner.ResolveVolumeMissingErrorType("VolumeMissing", first);

            Assert.Equal("VolumeMissing", type);
        }
    }
}

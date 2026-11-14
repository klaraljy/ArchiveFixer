using System;
using System.IO;
using System.IO.Compression;
using ArchiveFixer.Detection;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// FileNature 必须复用第一次内容读数的守门：
    /// 账单/轮扫描对同一文件只应该进入 VolumeNumberFromContent.Read 一次。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public sealed class FileNatureSingleReadTests
    {
        [Fact]
        public void 普通文件_一次FileNature只读一次内容()
        {
            string path = Path.Combine(Path.GetTempPath(), "af-nature-read-" + Guid.NewGuid().ToString("N") + ".bin");

            try
            {
                File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });
                VolumeNumberFromContent.ResetReadCallsForTests();

                FileNatureCard card = FileNature.Inspect(path);

                Assert.Equal(1, VolumeNumberFromContent.ReadCalls);
                Assert.Equal(5, card.Size);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void 单卷ZIP_一次FileNature只读一次内容()
        {
            string path = Path.Combine(Path.GetTempPath(), "af-nature-read-" + Guid.NewGuid().ToString("N") + ".zip");

            try
            {
                using (FileStream stream = File.Create(path))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    using StreamWriter writer = new StreamWriter(archive.CreateEntry("a.txt").Open());
                    writer.Write("hello");
                }

                VolumeNumberFromContent.ResetReadCallsForTests();

                FileNatureCard card = FileNature.Inspect(path);

                Assert.Equal(1, VolumeNumberFromContent.ReadCalls);
                Assert.Equal(VolumeContentFormat.Zip, card.Format);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void 字符串入口ClassifyMembership_只读一次()
        {
            string path = Path.Combine(Path.GetTempPath(), "af-nature-read-" + Guid.NewGuid().ToString("N") + ".bin");

            try
            {
                File.WriteAllBytes(path, new byte[] { 9, 8, 7 });
                VolumeNumberFromContent.ResetReadCallsForTests();

                _ = VolumeNumberFromContent.ClassifyMembership(path);

                Assert.Equal(1, VolumeNumberFromContent.ReadCalls);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}

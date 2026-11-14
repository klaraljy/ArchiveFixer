using System;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **把试开确认的顺序折成组读取**：槽位与入口只来自那一个赢的顺序；
    /// ⛔ 真实文件不改名、不搬、不覆盖（落规范卷名仍走既有接片出口）。
    /// </summary>
    public sealed class VolumeOrderMaterializerTests : IDisposable
    {
        private readonly string _root;

        public VolumeOrderMaterializerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerOrderMaterializer-" + Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论。
            }
        }

        [Fact]
        public void 试开确认的顺序折成内容读取_槽位与入口来自顺序()
        {
            string first = WritePiece("middle-a.z01", 120);
            string second = WritePiece("middle-b.zip", 80);
            string tail = WritePiece("tail.003", 50);

            CardVolumeGroupReading? reading = VolumeOrderMaterializer.BuildConfirmedReading(
                new[] { first, second, tail },
                "X-L2",
                VolumeNamingFamily.ZipNumbered);

            Assert.NotNull(reading);
            Assert.True(reading!.Complete);
            Assert.True(reading.HasFirstPiece);
            Assert.Equal(3, reading.PieceCount);
            Assert.Equal(250, reading.SumBytes);
            Assert.Equal(250, reading.DeclaredTotalBytes);
            Assert.Equal(VolumeNamingFamily.ZipNumbered, reading.NamingFamily);
            Assert.Equal(
                new[] { 1, 2, 3 },
                reading.Slots.Select(slot => slot.Index).ToArray());
            Assert.Equal(first, reading.EntryPath);
            Assert.Equal(first, reading.Slots[0].Path);
            Assert.Equal(second, reading.Slots[1].Path);
            Assert.Equal(tail, reading.Slots[2].Path);
            Assert.True(reading.Slots[2].IsLast);
            Assert.False(reading.Slots[0].IsLast);
        }

        [Fact]
        public void 少于两片或基名为空_判不出()
        {
            string only = WritePiece("only.zip", 10);

            Assert.Null(VolumeOrderMaterializer.BuildConfirmedReading(new[] { only }, "X-L2", VolumeNamingFamily.ZipNumbered));
            Assert.Null(VolumeOrderMaterializer.BuildConfirmedReading(new[] { only, only }, string.Empty, VolumeNamingFamily.ZipNumbered));
            Assert.Null(VolumeOrderMaterializer.BuildConfirmedReading(Array.Empty<string>(), "X-L2", VolumeNamingFamily.ZipNumbered));
        }

        private string WritePiece(string name, int length)
        {
            string path = Path.Combine(_root, name);
            var bytes = new byte[length];

            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)(i % 251);
            }

            File.WriteAllBytes(path, bytes);
            return path;
        }
    }
}

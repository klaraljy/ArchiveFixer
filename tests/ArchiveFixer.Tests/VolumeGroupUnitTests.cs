using System.Collections.Generic;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **组的一等事实**（用户 2026-10-18 口径：先把「组」变成程序里的一等事实，再回 30 份样本）。
    /// 这里只钉组事实本身：键带族（同基名两族不混）、源层/入口不被改写、owner 只有一个。
    /// ⛔ 不碰文件、不调引擎。
    /// </summary>
    public sealed class VolumeGroupUnitTests
    {
        [Fact]
        public void 同一基名不同族_键不同()
        {
            var numbered = new VolumeGroupUnit("aaa-L2", VolumeNamingFamily.ZipNumbered, @"C:\in", @"C:\in\aaa-L2.zip.001");
            var spanned = new VolumeGroupUnit("aaa-L2", VolumeNamingFamily.ZipSpanned, @"C:\in", @"C:\in\aaa-L2.zip");

            Assert.NotEqual(numbered.Key, spanned.Key);
        }

        [Fact]
        public void 判不出族_键仍然稳定()
        {
            var first = new VolumeGroupUnit("aaa-L2", null, @"C:\in", @"C:\in\aaa-L2.part1.rar");
            var second = new VolumeGroupUnit("aaa-L2", null, @"C:\in", @"C:\in\aaa-L2.part1.rar");

            Assert.Equal(first.Key, second.Key);
            Assert.Contains("?", first.Key);
        }

        [Fact]
        public void 源层与入口_按创建时的事实保留()
        {
            var unit = new VolumeGroupUnit("aaa-L2", VolumeNamingFamily.ZipNumbered, @"C:\source", @"C:\source\aaa-L2.zip.001");

            Assert.Equal(@"C:\source", unit.SourceLayer);
            Assert.Equal(@"C:\source\aaa-L2.zip.001", unit.EntryPath);
            Assert.Null(unit.OwnerTaskFileName);
        }

        [Fact]
        public void owner_一旦定下_不许改给另一个任务()
        {
            var unit = new VolumeGroupUnit("aaa-L2", VolumeNamingFamily.ZipNumbered, @"C:\source", @"C:\source\aaa-L2.zip.001");

            unit.OwnerTaskFileName = "first.rar";
            Assert.Equal("first.rar", unit.OwnerTaskFileName);

            Assert.Throws<System.InvalidOperationException>(() => unit.OwnerTaskFileName = "second.rar");
            Assert.Equal("first.rar", unit.OwnerTaskFileName);
        }

        [Fact]
        public void 试开确认的顺序_原样保存在组事实里()
        {
            var unit = new VolumeGroupUnit("aaa-L2", VolumeNamingFamily.ZipNumbered, @"C:\source", @"C:\source\aaa-L2.zip.001");
            IReadOnlyList<string> order = new[] { @"C:\source\a.001", @"C:\source\a.002", @"C:\source\a.003" };

            unit.ConfirmedOrder = order;

            Assert.Same(order, unit.ConfirmedOrder);
        }
    }
}

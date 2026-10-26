using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class SpecificBatchUnitBuilderTests : IDisposable
    {
        private readonly string _root;

        public SpecificBatchUnitBuilderTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "SpecificUnits_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch
            {
            }
        }

        [Fact]
        public void 标准分卷_整组一单_入口取第一卷()
        {
            ArchiveTask first = Add("111.7z.001", 10);
            ArchiveTask second = Add("111.7z.002", 20);

            IReadOnlyList<SpecificBatchUnit> units = SpecificBatchUnitBuilder.Build(new[] { second, first });

            SpecificBatchUnit unit = Assert.Single(units);
            Assert.Equal(first.CurrentPath, unit.Entry.CurrentPath);
            Assert.Single(unit.Followers);
            Assert.Equal(second.CurrentPath, unit.Followers[0].CurrentPath);
            Assert.Equal(30, unit.SourceBytes);
        }

        [Fact]
        public void 跨盘ZIP_入口取末片zip_不是z01()
        {
            ArchiveTask part1 = Add("111.z01", 10);
            ArchiveTask part2 = Add("111.z02", 20);
            ArchiveTask tail = Add("111.zip", 5);

            IReadOnlyList<SpecificBatchUnit> units = SpecificBatchUnitBuilder.Build(new[] { part1, part2, tail });

            SpecificBatchUnit unit = Assert.Single(units);
            Assert.Equal(tail.CurrentPath, unit.Entry.CurrentPath);
            Assert.Equal(2, unit.Followers.Count);
            Assert.Equal(35, unit.SourceBytes);
        }

        [Fact]
        public void 独立RAR_名字里有NO点001_也各自一单()
        {
            ArchiveTask first = Add("弦音sic NO.001 Alter EGO egoist [9P-92MB].rar", 10);
            ArchiveTask second = Add("弦音sic NO.002 FGO 伊莉雅 白裙 [9P-75MB].rar", 20);

            IReadOnlyList<SpecificBatchUnit> units = SpecificBatchUnitBuilder.Build(new[] { first, second });

            Assert.Equal(2, units.Count);
            Assert.All(units, unit => Assert.Empty(unit.Followers));
            Assert.Equal(2, units.Select(unit => unit.Entry.CurrentPath).Distinct().Count());
        }

        private ArchiveTask Add(string name, int bytes)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, new byte[bytes]);
            return new ArchiveTask(path);
        }
    }
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class StandardNameFastPathTests
    {
        [Fact]
        public void 同目录001到004_同基名005在别处_判Incomplete不判内容缺片()
        {
            string a = @"C:\in-a";
            string b = @"C:\in-b";
            var paths = new[]
            {
                Path.Combine(a, "9953.part1.rar"),
                Path.Combine(a, "9953.part2.rar"),
                Path.Combine(a, "9953.part3.rar"),
                Path.Combine(a, "9953.part4.rar"),
                Path.Combine(b, "9953.part5.rar")
            };

            IReadOnlyList<StandardNameFastPath.Decision> decisions = StandardNameFastPath.Decide(paths);

            Assert.Single(decisions);
            Assert.Equal(StandardNameFastPath.DecisionKind.Incomplete, decisions[0].Kind);
        }

        [Fact]
        public void 同目录完整标准组_判Ready()
        {
            string directory = @"C:\in";
            IReadOnlyList<StandardNameFastPath.Decision> decisions = StandardNameFastPath.Decide(new[]
            {
                Path.Combine(directory, "9953.part1.rar"),
                Path.Combine(directory, "9953.part2.rar")
            });

            StandardNameFastPath.Decision decision = Assert.Single(decisions);
            Assert.Equal(StandardNameFastPath.DecisionKind.Ready, decision.Kind);
        }

        [Fact]
        public void Rar标准partN_同目录同基名一组_入口part1()
        {
            string directory = @"C:\in";
            var groups = StandardNameFastPath.Find(new[]
            {
                Path.Combine(directory, "9953.part1.rar"),
                Path.Combine(directory, "9953.part2.rar"),
                Path.Combine(directory, "9953.part3.rar")
            });

            StandardNameFastPath.Group group = Assert.Single(groups);
            Assert.Equal("9953", group.BaseName);
            Assert.Equal(StandardNameFastPath.Family.RarPart, group.Family);
            Assert.Equal(Path.Combine(directory, "9953.part1.rar"), group.EntryPath);
            Assert.Equal(3, group.Pieces.Count);
        }

        [Fact]
        public void 只有001到004是本目录标准名_也返回标准组_不在这里判完整性()
        {
            string directory = @"C:\in";
            var groups = StandardNameFastPath.Find(new[]
            {
                Path.Combine(directory, "9953.part1.rar"),
                Path.Combine(directory, "9953.part2.b2"),
                Path.Combine(directory, "9953.part3.c3"),
                Path.Combine(directory, "9953.part4.rar")
            });

            StandardNameFastPath.Group group = Assert.Single(groups);
            Assert.Equal(2, group.Pieces.Count);
            Assert.Equal(Path.Combine(directory, "9953.part1.rar"), group.EntryPath);
        }

        [Fact]
        public void 跨盘Zip_标准入口是末片zip()
        {
            string directory = @"C:\in";
            var groups = StandardNameFastPath.Find(new[]
            {
                Path.Combine(directory, "archive.z01"),
                Path.Combine(directory, "archive.z02"),
                Path.Combine(directory, "archive.zip")
            });

            StandardNameFastPath.Group group = Assert.Single(groups);
            Assert.Equal(StandardNameFastPath.Family.ZipSpanned, group.Family);
            Assert.Equal(Path.Combine(directory, "archive.zip"), group.EntryPath);
        }

        [Fact]
        public void 七z和Zip通用分片各自成组_入口001()
        {
            string directory = @"C:\in";
            var groups = StandardNameFastPath.Find(new[]
            {
                Path.Combine(directory, "pack.7z.001"),
                Path.Combine(directory, "pack.7z.002"),
                Path.Combine(directory, "zip.zip.001"),
                Path.Combine(directory, "zip.zip.002")
            });

            Assert.Equal(2, groups.Count);
            Assert.Contains(groups, group => group.Family == StandardNameFastPath.Family.SevenZip
                && group.EntryPath.EndsWith("pack.7z.001", System.StringComparison.OrdinalIgnoreCase));
            Assert.Contains(groups, group => group.Family == StandardNameFastPath.Family.ZipNumbered
                && group.EntryPath.EndsWith("zip.zip.001", System.StringComparison.OrdinalIgnoreCase));
        }
    }
}

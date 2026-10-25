using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class SpaceQueueUnitBuilderTests : IDisposable
    {
        private readonly string _root;

        public SpaceQueueUnitBuilderTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "SpaceQueue_" + Guid.NewGuid().ToString("N"));
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
        public void 单一包按大小倒序()
        {
            ArchiveTask small = CreateTask("small.7z", 10);
            ArchiveTask big = CreateTask("big.7z", 100);

            IReadOnlyList<SpaceQueueUnit> units = SpaceQueueUnitBuilder.Build(new[] { small, big });

            Assert.Equal(new[] { big, small }, units.Select(unit => unit.Entry));
            Assert.Equal(100, units[0].SourceBytes);
        }

        [Fact]
        public void 分卷按整组总和_只出一个单位()
        {
            ArchiveTask first = CreateTask("set.7z.001", 10);
            first.IsVolumeGroup = true;
            first.VolumeGroupKey = "dir|set";
            first.VolumePaths.Add(first.CurrentPath);

            ArchiveTask second = CreateTask("set.7z.002", 20);
            second.IsVolumeGroup = true;
            second.IsVolumeGroupFollower = true;
            second.VolumeGroupKey = "dir|set";
            second.VolumePaths.Add(second.CurrentPath);

            IReadOnlyList<SpaceQueueUnit> units = SpaceQueueUnitBuilder.Build(new[] { second, first });

            SpaceQueueUnit unit = Assert.Single(units);
            Assert.Equal(30, unit.SourceBytes);
            Assert.Equal(first, unit.Entry);
            Assert.Equal(2, unit.Tasks.Count);
        }

        private ArchiveTask CreateTask(string name, int length)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, new byte[length]);

            return new ArchiveTask
            {
                CurrentPath = path,
                FileName = name
            };
        }
    }
}

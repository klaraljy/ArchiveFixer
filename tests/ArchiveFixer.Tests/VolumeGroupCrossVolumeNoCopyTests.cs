using System;
using System.IO;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 空间不足模式禁止为了建组入口而跨盘复制源包。
    /// 这条只钉 TryMaterialize 的策略位；真实 H 盘不存在时跳过（不伪造跨盘事实）。
    /// </summary>
    public sealed class VolumeGroupCrossVolumeNoCopyTests : IDisposable
    {
        private readonly string _sourceRoot;
        private readonly string _targetRoot;

        public VolumeGroupCrossVolumeNoCopyTests()
        {
            _sourceRoot = Path.Combine(
                @"E:\DeepSeekProjects\_tmp\af-space-cross",
                Guid.NewGuid().ToString("N"));
            _targetRoot = Path.Combine(
                Path.Combine(TestPaths.TempRoot, "_tmp-space-cross"),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sourceRoot);
        }

        public void Dispose()
        {
            TryDelete(_sourceRoot);
            TryDelete(_targetRoot);
        }

        [Fact]
        public void 跨盘标准名入口_空间不足禁止复制源包()
        {
            if (!Directory.Exists(@"H:\"))
            {
                FixtureGate.Skip("VolumeGroupCrossVolumeNoCopyTests.cs", "前置条件不满足：夹具或运行环境缺失");
                 return;
            }

            string source = Path.Combine(_sourceRoot, "111.7z.001");
            File.WriteAllBytes(source, new byte[1024]);
            Directory.CreateDirectory(_targetRoot);

            var group = new PreparedVolumeGroup
            {
                BaseName = "111",
                Family = VolumeNamingFamily.SevenZipNumbered,
                OriginalSourceLayer = _sourceRoot,
                OriginalEntryPath = source,
                EntryCanonicalName = "111.7z.001",
                State = PreparedGroupState.Complete,
                Pieces = new[]
                {
                    new PreparedVolumePiece
                    {
                        CanonicalName = "111.7z.001",
                        OriginalPath = source,
                        Size = 1024
                    }
                }
            };

            PreparedVolumeGroup? materialized = VolumeGroupEntryDirectory.TryMaterialize(
                group,
                _targetRoot,
                null,
                out string error,
                allowCrossVolumeCopy: false);

            Assert.Null(materialized);
            Assert.Contains("禁止复制源包", error, StringComparison.Ordinal);
            Assert.True(File.Exists(source), "源片必须原地不动");

            if (Directory.Exists(_targetRoot))
            {
                Assert.Empty(Directory.GetFiles(_targetRoot, "*", SearchOption.AllDirectories));
            }
        }

        [Fact]
        public void 跨盘空间不足_标准名入口落在源盘_目标盘零复制()
        {
            if (!Directory.Exists(@"H:\"))
            {
                FixtureGate.Skip("VolumeGroupCrossVolumeNoCopyTests.cs", "前置条件不满足：夹具或运行环境缺失");
                 return;
            }

            string source = Path.Combine(_sourceRoot, "111.7z.001");
            File.WriteAllBytes(source, new byte[1024]);
            Directory.CreateDirectory(_targetRoot);

            var group = new PreparedVolumeGroup
            {
                BaseName = "111",
                Family = VolumeNamingFamily.SevenZipNumbered,
                OriginalSourceLayer = _sourceRoot,
                OriginalEntryPath = source,
                EntryCanonicalName = "111.7z.001",
                State = PreparedGroupState.Complete,
                Pieces = new[]
                {
                    new PreparedVolumePiece
                    {
                        CanonicalName = "111.7z.001",
                        OriginalPath = source,
                        Size = 1024
                    }
                }
            };

            string? entryRoot = VolumeGroupEntryDirectory.TryResolveTargetEntryRoot(
                group,
                _targetRoot,
                allowSourceVolumeFallback: true);

            Assert.NotNull(entryRoot);
            Assert.StartsWith(
                Path.GetFullPath(_sourceRoot),
                Path.GetFullPath(entryRoot!),
                StringComparison.OrdinalIgnoreCase);

            PreparedVolumeGroup? materialized = VolumeGroupEntryDirectory.TryMaterialize(
                group,
                entryRoot,
                null,
                out string error,
                allowCrossVolumeCopy: false);

            Assert.NotNull(materialized);
            Assert.True(File.Exists(Path.Combine(materialized!.EntryLayer, "111.7z.001")), error);
            Assert.True(File.Exists(source));
            Assert.Empty(Directory.GetFiles(_targetRoot, "*", SearchOption.AllDirectories));

            Assert.True(VolumeGroupEntryDirectory.TryDelete(materialized.EntryLayer, entryRoot));
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}

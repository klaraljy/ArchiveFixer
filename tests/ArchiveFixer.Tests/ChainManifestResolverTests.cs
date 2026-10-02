using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using System;
using System.Collections.Generic;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **L3 的预期清单该拿哪一层**（唯一出口 <see cref="ChainManifestResolver"/>）—— 判据级的守门用例。
    ///
    /// <para>端到端那一条在 <c>ChainManifestCompletenessTests.形状6</c>（真 7z + 真管线）；
    /// 这里钉的是判据本身：**展开恰好一层时，也要先用"这一层解压前就列过的那份清单"**，
    /// 而不是直接落「现问引擎列一份」—— 递归那条路的调用方给"现问"传的是空密码，
    /// 加密头（7z <c>-mhe</c> / RAR <c>-hp</c>）的包必然问不出来 ⇒ 判不出 ⇒ 源包与其余物都不动
    /// （用户 2026-10-02 真机：其余物留在目录里没被删）。</para>
    /// </summary>
    public class ChainManifestResolverTests
    {
        private const string ArchivePath = @"C:\样本\51658213.7z.001";

        [Fact]
        public void 展开一层且这一层列过清单_用本层清单_不问引擎()
        {
            ManifestExpectation expectation = ChainManifestResolver.Resolve(
                RecursionWith(SuccessfulLayer(depth: 0, files: 6, bytes: 996_484_903)),
                knownList: null);

            Assert.Equal(ManifestExpectationSource.LeafLayer, expectation.Source);
            Assert.Equal(0, expectation.LayerDepth);
            Assert.True(expectation.HasManifest);
            Assert.Equal(6, expectation.Expected!.FileCount);
            Assert.Equal(996_484_903, expectation.Expected.TotalUncompressedSize);
        }

        [Fact]
        public void 展开一层但这层没列过清单_照旧落现问引擎()
        {
            var layer = new RecursionLayerReport
            {
                Depth = 0,
                ArchivePath = ArchivePath,
                Success = true,
                Manifest = LayerManifest.Unavailable("测试用：这一层没列过清单")
            };

            ManifestExpectation expectation = ChainManifestResolver.Resolve(RecursionWith(layer), knownList: null);

            Assert.Equal(ManifestExpectationSource.QueryArchive, expectation.Source);
            Assert.Equal(0, expectation.LayerDepth);
        }

        [Fact]
        public void 手上已有直读清单_照旧优先用它()
        {
            var known = new ArchiveListResult
            {
                Success = true,
                FileCount = 2,
                TotalUncompressedSize = 4096,
                Entries = new List<ArchiveEntry>()
            };

            ManifestExpectation expectation = ChainManifestResolver.Resolve(
                RecursionWith(SuccessfulLayer(depth: 0, files: 6, bytes: 996_484_903)),
                known);

            Assert.Equal(ManifestExpectationSource.OuterList, expectation.Source);
            Assert.Equal(2, expectation.Expected!.FileCount);
        }

        [Fact]
        public void 展开多层_照旧用最深的那一层()
        {
            ManifestExpectation expectation = ChainManifestResolver.Resolve(
                RecursionWith(
                    SuccessfulLayer(depth: 0, files: 2, bytes: 1000),
                    SuccessfulLayer(depth: 1, files: 5, bytes: 5000)),
                knownList: null);

            Assert.Equal(ManifestExpectationSource.LeafLayer, expectation.Source);
            Assert.Equal(1, expectation.LayerDepth);
            Assert.Equal(5, expectation.Expected!.FileCount);
        }

        [Fact]
        public void 不是递归路径_照旧落现问引擎()
        {
            ManifestExpectation expectation = ChainManifestResolver.Resolve(recursion: null, knownList: null);

            Assert.Equal(ManifestExpectationSource.QueryArchive, expectation.Source);
        }

        private static RecursionResult RecursionWith(params RecursionLayerReport[] layers)
        {
            return new RecursionResult
            {
                Layers = layers,
                FinalOutputPath = @"C:\样本\out"
            };
        }

        private static RecursionLayerReport SuccessfulLayer(int depth, int files, long bytes)
        {
            return new RecursionLayerReport
            {
                Depth = depth,
                ArchivePath = ArchivePath,
                Success = true,
                OutputFileCount = files,
                OutputSize = bytes,
                Manifest = LayerManifest.From(new ArchiveListResult
                {
                    Success = true,
                    FileCount = files,
                    TotalUncompressedSize = bytes,
                    Entries = new List<ArchiveEntry>()
                })
            };
        }
    }
}

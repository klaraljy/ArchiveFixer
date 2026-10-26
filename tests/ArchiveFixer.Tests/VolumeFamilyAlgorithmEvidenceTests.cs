using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 分卷族算法的**证据边界**：族算法只能按内容/自述能证明的事实下结论；
    /// 自述片数/字节与当前成员对不上时，不能把“识别不足”写成“缺片”。
    /// </summary>
    public sealed class VolumeFamilyAlgorithmEvidenceTests
    {
        [Fact]
        public void 七z_自述片数与实际成员对不上_不能报缺片()
        {
            /*
             * P53/P55 形状：首卷自述 138 片 / 一个很大的整包字节，
             * 当前只收到 2 个成员。底层只能证明“自述与实收不一致”，
             * 不能证明“缺 3..138 这 136 片” —— 大小不均匀/加密头都会造成这种表象。
             */
            var unit = new VolumeGroupUnit(
                "P53-8",
                VolumeNamingFamily.SevenZipNumbered,
                @"C:\in",
                @"C:\in\P53-8.7z.001");

            unit.Pieces = new List<GroupPieceFact>
            {
                Piece(
                    @"C:\in\P53-8.7z.001",
                    size: 18 * 1024 * 1024,
                    number: 1,
                    first: true,
                    declaredCount: 138,
                    declaredBytes: 138L * 1024 * 1024 * 1024),
                Piece(
                    @"C:\in\P53-8.7z.002",
                    size: 2_579_478_242,
                    number: 2,
                    first: false,
                    declaredCount: 0,
                    declaredBytes: 0)
            };

            FamilyResult result = new SevenZipFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Unknown, result.Verdict);
            Assert.DoesNotContain("缺片", result.Evidence, System.StringComparison.Ordinal);
        }

        [Fact]
        public void 七z_片数对得上但字节对不上_不能报缺片()
        {
            var unit = new VolumeGroupUnit(
                "P55-8",
                VolumeNamingFamily.SevenZipNumbered,
                @"C:\in",
                @"C:\in\P55-8.7z.001");

            unit.Pieces = new List<GroupPieceFact>
            {
                Piece(
                    @"C:\in\P55-8.7z.001",
                    size: 18 * 1024 * 1024,
                    number: 1,
                    first: true,
                    declaredCount: 2,
                    declaredBytes: 10_000_000_000),
                Piece(
                    @"C:\in\P55-8.7z.002",
                    size: 3_535_025_138,
                    number: 2,
                    first: false,
                    declaredCount: 0,
                    declaredBytes: 0)
            };

            FamilyResult result = new SevenZipFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Unknown, result.Verdict);
            Assert.DoesNotContain("缺片", result.Evidence, System.StringComparison.Ordinal);
        }

        [Fact]
        public void 组分析候选_扫描根没覆盖种子目录_仍要收同目录内容片()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "af-group-seed-dir-" + System.Guid.NewGuid().ToString("N"));

            string other = Path.Combine(root, "other");
            string seedDir = Path.Combine(root, "rest");
            Directory.CreateDirectory(other);
            Directory.CreateDirectory(seedDir);

            string seed = Path.Combine(seedDir, "9953.part1.rar");
            string part2 = Path.Combine(seedDir, "9953.part2.b2");
            string part3 = Path.Combine(seedDir, "9953.part3.c3");

            try
            {
                File.WriteAllText(seed, "x");
                File.WriteAllText(part2, "y");
                File.WriteAllText(part3, "z");

                List<GroupAnalysis.GroupCandidate> groups = GroupAnalysis.Analyze(
                    new[] { seed },
                    new[] { other });

                GroupAnalysis.GroupCandidate group = Assert.Single(groups);
                Assert.Equal(3, group.Pieces.Count);
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        [Fact]
        public void 组分析_内容卡证明第2卷但基名不同_按缺号补进唯一组()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "af-group-content-fill-" + System.Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            string part1 = Path.Combine(root, "9953.part1.rar");
            string part2 = Path.Combine(root, "other.part2.b2");
            string part3 = Path.Combine(root, "9953.part3.c3");

            try
            {
                File.WriteAllText(part1, "1");
                File.WriteAllText(part2, "2");
                File.WriteAllText(part3, "3");

                var handoffs = new List<VolumeHandoffEntry>
                {
                    Handoff(part1, baseName: "9953", VolumeContentFormat.Rar, number: 1, first: true),
                    Handoff(part2, baseName: "other", VolumeContentFormat.Rar, number: 2, first: false),
                    Handoff(part3, baseName: "9953", VolumeContentFormat.Rar, number: 3, first: false)
                };

                List<GroupAnalysis.GroupCandidate> groups = GroupAnalysis.Analyze(
                    handoffs,
                    new[] { root });

                GroupAnalysis.GroupCandidate group = Assert.Single(
                    groups,
                    candidate => candidate.BaseName == "9953");
                Assert.Equal(3, group.Pieces.Count);
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        private static VolumeHandoffEntry Handoff(
            string path,
            string baseName,
            VolumeContentFormat format,
            int number,
            bool first)
        {
            return new VolumeHandoffEntry
            {
                Path = path,
                BaseName = baseName,
                Format = format,
                Size = 1,
                BodyKind = first ? ArchiveBodyKind.VolumeFirstPiece : ArchiveBodyKind.VolumePiece,
                VolumeNumber = number,
                FirstVolumeFlag = first,
                Membership = VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember,
                ContentFail = VolumeNumberFail.None,
                SourceLayer = Path.GetDirectoryName(path) ?? string.Empty,
                Source = VolumeHandoffSource.ChainProduced
            };
        }

        private static GroupPieceFact Piece(
            string path,
            long size,
            int? number,
            bool first,
            int declaredCount,
            long declaredBytes)
        {
            return new GroupPieceFact(
                path,
                size,
                VolumeContentFormat.SevenZip,
                first ? ArchiveBodyKind.VolumeFirstPiece : ArchiveBodyKind.VolumePiece,
                number,
                first,
                VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember,
                VolumeNumberFail.None,
                declaredBytes,
                declaredCount,
                number,
                @"C:\in",
                null,
                FromHandoff: false);
        }
    }
}

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

        [Fact]
        public void 组分析_同目录两个组都缺同一个号_内容卡不补_防混组()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "af-group-ambiguous-" + System.Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            string a1 = Path.Combine(root, "aaa.part1.rar");
            string a3 = Path.Combine(root, "aaa.part3.c3");
            string b1 = Path.Combine(root, "bbb.part1.rar");
            string b3 = Path.Combine(root, "bbb.part3.c3");
            string other2 = Path.Combine(root, "other.part2.b2");

            try
            {
                File.WriteAllText(a1, "a1");
                File.WriteAllText(a3, "a3");
                File.WriteAllText(b1, "b1");
                File.WriteAllText(b3, "b3");
                File.WriteAllText(other2, "o2");

                var handoffs = new List<VolumeHandoffEntry>
                {
                    Handoff(a1, "aaa", VolumeContentFormat.Rar, 1, true),
                    Handoff(a3, "aaa", VolumeContentFormat.Rar, 3, false),
                    Handoff(b1, "bbb", VolumeContentFormat.Rar, 1, true),
                    Handoff(b3, "bbb", VolumeContentFormat.Rar, 3, false),
                    Handoff(other2, "other", VolumeContentFormat.Rar, 2, false)
                };

                List<GroupAnalysis.GroupCandidate> groups = GroupAnalysis.Analyze(
                    handoffs,
                    new[] { root });

                Assert.Equal(2, groups.Single(group => group.BaseName == "aaa").Pieces.Count);
                Assert.Equal(2, groups.Single(group => group.BaseName == "bbb").Pieces.Count);
                Assert.Single(groups.Single(group => group.BaseName == "other").Pieces);
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
        public void 组分析_七z自述片数与实收不一致_候选层也不能报缺片()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "af-group-7z-evidence-" + System.Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            string first = Path.Combine(root, "P53-8.7z.001");
            string second = Path.Combine(root, "P53-8.7z.002");

            try
            {
                File.WriteAllText(first, "1");
                File.WriteAllText(second, "2");

                var firstHandoff = Handoff(first, "P53-8", VolumeContentFormat.SevenZip, 1, true);
                firstHandoff = new VolumeHandoffEntry
                {
                    Path = firstHandoff.Path,
                    BaseName = firstHandoff.BaseName,
                    Format = firstHandoff.Format,
                    Size = firstHandoff.Size,
                    BodyKind = firstHandoff.BodyKind,
                    VolumeNumber = 1,
                    FirstVolumeFlag = true,
                    Membership = firstHandoff.Membership,
                    ContentFail = firstHandoff.ContentFail,
                    DeclaredVolumeCount = 138,
                    DeclaredTotalBytes = 138L * 1024 * 1024 * 1024,
                    SourceLayer = firstHandoff.SourceLayer,
                    Source = firstHandoff.Source
                };

                List<GroupAnalysis.GroupCandidate> groups = GroupAnalysis.Analyze(
                    new[]
                    {
                        firstHandoff,
                        Handoff(second, "P53-8", VolumeContentFormat.SevenZip, 2, false)
                    },
                    new[] { root });

                GroupAnalysis.GroupCandidate group = Assert.Single(groups);
                Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Unknown, group.Verdict);
                Assert.DoesNotContain("缺", group.Describe(), StringComparison.Ordinal);
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
        public void Zip通用分片_有试开确认顺序_名字不标准也按确认顺序()
        {
            string layer = Path.Combine(
                Path.GetTempPath(),
                "af-zip-confirmed-" + System.Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(layer);

            string first = Path.Combine(layer, "a.bin");
            string middle1 = Path.Combine(layer, "b.bin");
            string middle2 = Path.Combine(layer, "c.bin");
            string end = Path.Combine(layer, "d.bin");

            try
            {
                File.WriteAllText(first, "1");
                File.WriteAllText(middle1, "2");
                File.WriteAllText(middle2, "3");
                File.WriteAllText(end, "4");

                var unit = new VolumeGroupUnit(
                    "组",
                    VolumeNamingFamily.ZipNumbered,
                    layer,
                    first)
                {
                    Pieces = new List<GroupPieceFact>
                    {
                        ZipPiece(first, first: true, end: false),
                        ZipPiece(middle1, first: false, end: false),
                        ZipPiece(middle2, first: false, end: false),
                        ZipPiece(end, first: false, end: true)
                    },
                    ConfirmedOrder = new[] { first, middle1, middle2, end }
                };

                FamilyResult result = new ZipNumberedFamilyAlgorithm().Evaluate(unit);

                Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Complete, result.Verdict);
                Assert.Equal(
                    new[] { "组.zip.001", "组.zip.002", "组.zip.003", "组.zip.004" },
                    result.NamePlan!.Items.Select(item => item.TargetName).ToArray());
            }
            finally
            {
                try
                {
                    Directory.Delete(layer, recursive: true);
                }
                catch
                {
                }
            }
        }

        [Fact]
        public void 跨盘Zip_有试开确认顺序_两个无名中间片也按确认顺序()
        {
            string layer = Path.Combine(
                Path.GetTempPath(),
                "af-spanned-confirmed-" + System.Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(layer);

            string first = Path.Combine(layer, "a.bin");
            string middle1 = Path.Combine(layer, "b.bin");
            string middle2 = Path.Combine(layer, "c.bin");
            string end = Path.Combine(layer, "组.zip");

            try
            {
                File.WriteAllText(first, "1");
                File.WriteAllText(middle1, "2");
                File.WriteAllText(middle2, "3");
                File.WriteAllText(end, "4");

                var unit = new VolumeGroupUnit(
                    "组",
                    VolumeNamingFamily.ZipSpanned,
                    layer,
                    end)
                {
                    Pieces = new List<GroupPieceFact>
                    {
                        ZipSpannedPiece(first, first: true, end: false, total: 4),
                        ZipSpannedPiece(middle1, first: false, end: false, total: 4),
                        ZipSpannedPiece(middle2, first: false, end: false, total: 4),
                        ZipSpannedPiece(end, first: false, end: true, total: 4)
                    },
                    ConfirmedOrder = new[] { first, middle1, middle2, end }
                };

                FamilyResult result = new ZipSpannedFamilyAlgorithm().Evaluate(unit);

                Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Complete, result.Verdict);
                Assert.Equal(
                    new[] { "组.z01", "组.z02", "组.z03", "组.zip" },
                    result.NamePlan!.Items.Select(item => item.TargetName).ToArray());
            }
            finally
            {
                try
                {
                    Directory.Delete(layer, recursive: true);
                }
                catch
                {
                }
            }
        }

        private static GroupPieceFact ZipSpannedPiece(
            string path,
            bool first,
            bool end,
            int total)
        {
            return new GroupPieceFact(
                path,
                1,
                VolumeContentFormat.Zip,
                ArchiveBodyKind.VolumePiece,
                end ? total : null,
                first,
                VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember,
                VolumeNumberFail.None,
                0,
                end ? total : 0,
                end ? total : null,
                Path.GetDirectoryName(path) ?? string.Empty,
                null,
                FromHandoff: false,
                HasZipEndRecord: end,
                ZipEndRecordMeansSplit: end,
                HasZipLocalHeader: first);
        }

        private static GroupPieceFact ZipPiece(string path, bool first, bool end)
        {
            return new GroupPieceFact(
                path,
                1,
                VolumeContentFormat.Zip,
                ArchiveBodyKind.VolumePiece,
                null,
                first,
                VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember,
                VolumeNumberFail.None,
                0,
                0,
                null,
                Path.GetDirectoryName(path) ?? string.Empty,
                null,
                FromHandoff: false,
                HasZipEndRecord: end,
                ZipEndRecordMeansSplit: end,
                HasZipLocalHeader: first);
        }

        [Fact]
        public void 七z_有试开确认顺序_名字不标准也按确认顺序()
        {
            string layer = Path.Combine(
                Path.GetTempPath(),
                "af-7z-confirmed-" + System.Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(layer);

            string[] paths =
            {
                Path.Combine(layer, "a.bin"),
                Path.Combine(layer, "b.bin"),
                Path.Combine(layer, "c.bin"),
                Path.Combine(layer, "d.bin")
            };

            try
            {
                foreach (string path in paths)
                {
                    File.WriteAllText(path, "x");
                }

                var unit = new VolumeGroupUnit(
                    "P53-8",
                    VolumeNamingFamily.SevenZipNumbered,
                    layer,
                    paths[0])
                {
                    Pieces = new List<GroupPieceFact>
                    {
                        Piece(paths[0], size: 100, number: null, first: true, declaredCount: 4, declaredBytes: 400),
                        Piece(paths[1], size: 100, number: null, first: false, declaredCount: 0, declaredBytes: 0),
                        Piece(paths[2], size: 100, number: null, first: false, declaredCount: 0, declaredBytes: 0),
                        Piece(paths[3], size: 100, number: null, first: false, declaredCount: 0, declaredBytes: 0)
                    },
                    ConfirmedOrder = paths
                };

                FamilyResult result = new SevenZipFamilyAlgorithm().Evaluate(unit);

                Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Complete, result.Verdict);
                Assert.Equal(
                    new[] { "P53-8.7z.001", "P53-8.7z.002", "P53-8.7z.003", "P53-8.7z.004" },
                    result.NamePlan!.Items.Select(item => item.TargetName).ToArray());
            }
            finally
            {
                try
                {
                    Directory.Delete(layer, recursive: true);
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

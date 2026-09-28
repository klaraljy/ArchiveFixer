using System;
using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Detection;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 分卷组识别的纯逻辑测试。
    ///
    /// 全部用 <see cref="VolumeCandidate"/> 假路径构造，**不碰真实文件系统** —— 识别只按命名，
    /// 一旦测试真的去建目录建文件，就跑偏成集成测试了（而且慢、还依赖盘符）。
    /// </summary>
    public class VolumeGroupDetectorTests
    {
        private static List<VolumeCandidate> Candidates(params string[] fileNames)
        {
            var list = new List<VolumeCandidate>();

            foreach (string fileName in fileNames)
            {
                list.Add(new VolumeCandidate { Path = @"C:\t\" + fileName, Size = 1024 });
            }

            return list;
        }

        [Theory]
        [InlineData("volume.7z.001", 1)]
        [InlineData("volume.7z.012", 12)]
        [InlineData("archive.001", 1)]
        [InlineData("archive.003", 3)]
        [InlineData("x.zip", 1)]
        [InlineData("x.z01", 2)]
        [InlineData("x.z02", 3)]
        [InlineData("x.rar", 1)]
        [InlineData("x.r00", 2)]
        [InlineData("x.r01", 3)]
        [InlineData("x.part1.rar", 1)]
        [InlineData("x.part02.rar", 2)]
        [InlineData("x.part1", 1)]
        [InlineData("movie.part3.rar", 3)]
        [InlineData("x.001.rar", 1)]
        [InlineData("VOLUME.7Z.001", 1)]
        public void TryGetVolumeIndex_SupportedForms(string fileName, int expected)
        {
            Assert.Equal(expected, VolumeGroupDetector.TryGetVolumeIndex(fileName));
        }

        // 本体（x.zip / x.rar）单看名字就是本族的第 1 卷，所以这里返回 1。
        // "算不算分卷组"是 Group() 的事：没有同族的 .z01 / .r00 就不成组（见 Group_PlainArchives_AreNotVolumeGroups）。
        [Theory]
        [InlineData("movie.zip")]
        [InlineData("movie.rar")]
        [InlineData("x.part.rar")]
        public void TryGetVolumeIndex_ArchiveBody_IsFirstVolume(string fileName)
        {
            Assert.Equal(1, VolumeGroupDetector.TryGetVolumeIndex(fileName));
        }

        [Theory]
        // ⚠ 这里原来有 "volume.7z.001.txt"，2026-09-28 被用户推翻：
        //   「x.001.txt 依旧不算（点在段外）。这个难道你就弄不了了吗」→ 现在它**算**这一组的第 1 卷，
        //   用例见 VolumeJunkTailTests.卷号后面粘着垃圾_仍然算分卷。
        [InlineData("readme.txt")]
        [InlineData("archive")]
        [InlineData(".zip")]
        [InlineData("x.z")]
        [InlineData("x.r1")]
        [InlineData("x.01")]
        [InlineData("x.000")]
        [InlineData("x.z00")]
        [InlineData("")]
        [InlineData("   ")]
        public void TryGetVolumeIndex_NonVolume_ReturnsNull(string fileName)
        {
            Assert.Null(VolumeGroupDetector.TryGetVolumeIndex(fileName));
        }

        [Theory]
        [InlineData("volume.7z.003", "volume.7z.001")]
        [InlineData("archive.002", "archive.001")]
        [InlineData("x.z02", "x.zip")]
        [InlineData("x.r01", "x.rar")]
        [InlineData("movie.part3.rar", "movie.part1.rar")]
        [InlineData("x.part02.rar", "x.part01.rar")]
        [InlineData("x.part1.rar", "x.part1.rar")]
        [InlineData("x.002.rar", "x.001.rar")]
        public void TryGetFirstVolumeName_DerivesFirstVolume(string fileName, string expected)
        {
            Assert.Equal(expected, VolumeGroupDetector.TryGetFirstVolumeName(fileName));
        }

        [Theory]
        [InlineData("readme.txt")]
        // "volume.7z.001.txt" 原来在这儿，2026-09-28 改成算分卷（用户指示，见上一条注释）
        public void TryGetFirstVolumeName_NonVolume_ReturnsNull(string fileName)
        {
            Assert.Null(VolumeGroupDetector.TryGetFirstVolumeName(fileName));
        }

        [Fact]
        public void TryGetVolumeIndex_FullPath_IsAccepted()
        {
            Assert.Equal(2, VolumeGroupDetector.TryGetVolumeIndex(@"C:\t\volume.7z.002"));
        }

        // 只返回文件名，不返回路径：调用方自己拼目录（本模块不碰文件系统）。
        [Fact]
        public void TryGetFirstVolumeName_ReturnsNameOnly()
        {
            Assert.Equal("volume.7z.001", VolumeGroupDetector.TryGetFirstVolumeName(@"C:\t\volume.7z.002"));
        }

        [Theory]
        [InlineData("volume.7z.001", "volume.7z.002", true)]
        [InlineData("volume.7z.001", "VOLUME.7Z.003", true)]
        [InlineData("a.7z.001", "b.7z.001", false)]
        [InlineData("x.zip", "x.z01", true)]
        [InlineData("x.rar", "x.r00", true)]
        [InlineData("x.part1.rar", "x.part2.rar", true)]
        [InlineData("x.rar", "x.zip", false)]
        [InlineData("x.rar", "x.part1.rar", false)]
        // ⚠ 这一条 2026-09-28 由 false 改成 true：`…001.txt` 现在算同一组的第 1 卷（用户指示）
        [InlineData("volume.7z.001", "volume.7z.001.txt", true)]
        [InlineData("archive.001", "archive.part1.rar", false)]
        public void BelongsToSameGroup_ComparesFamilyAndBaseName(string fileNameA, string fileNameB, bool expected)
        {
            Assert.Equal(expected, VolumeGroupDetector.BelongsToSameGroup(fileNameA, fileNameB));
        }

        [Fact]
        public void Group_NumericVolumes_OneGroupSortedByIndex()
        {
            // 故意乱序喂进去：输出必须按卷序排好，解压要按顺序取卷。
            var groups = VolumeGroupDetector.Group(Candidates("volume.7z.003", "volume.7z.001", "volume.7z.002"));

            var group = Assert.Single(groups);

            Assert.Equal(@"C:\t", group.DirectoryPath);
            Assert.Equal("volume.7z", group.BaseName);
            Assert.Equal(3, group.KnownVolumeCount);
            Assert.Equal(0, group.ExpectedVolumeCount);
            Assert.True(group.IsComplete);
            Assert.Empty(group.MissingVolumeNames);
            Assert.Equal(@"C:\t\volume.7z.001", group.FirstVolumePath);
            Assert.Equal(
                new[] { @"C:\t\volume.7z.001", @"C:\t\volume.7z.002", @"C:\t\volume.7z.003" },
                group.Volumes.Select(v => v.Path).ToArray());
        }

        [Fact]
        public void Group_NumericVolumesWithoutInnerExtension_UsesFileStemAsBaseName()
        {
            var group = Assert.Single(VolumeGroupDetector.Group(Candidates("archive.001", "archive.002")));

            Assert.Equal("archive", group.BaseName);
            Assert.Equal(2, group.KnownVolumeCount);
            Assert.True(group.IsComplete);
        }

        [Fact]
        public void Group_NumericVolumesWithGap_ListsMissingVolumeAndStaysIncomplete()
        {
            var group = Assert.Single(VolumeGroupDetector.Group(Candidates("volume.7z.001", "volume.7z.003")));

            Assert.False(group.IsComplete);
            Assert.Equal(2, group.KnownVolumeCount);
            Assert.Equal(new[] { "volume.7z.002" }, group.MissingVolumeNames.ToArray());
            Assert.Contains("volume.7z.002", group.Note);
            Assert.Equal(@"C:\t\volume.7z.001", group.FirstVolumePath);
        }

        [Fact]
        public void Group_FirstVolumeMissing_ListsItAndKeepsSmallestFoundAsStart()
        {
            var group = Assert.Single(VolumeGroupDetector.Group(Candidates("volume.7z.002", "volume.7z.003")));

            Assert.False(group.IsComplete);
            Assert.Equal(new[] { "volume.7z.001" }, group.MissingVolumeNames.ToArray());
            Assert.Equal(@"C:\t\volume.7z.002", group.FirstVolumePath);
        }

        // 只有 1 卷时不能说"完整"，也不能说"缺卷"：总数根本无从得知，只能提示可能不完整。
        [Fact]
        public void Group_SingleVolume_IsCompleteButWarns()
        {
            var group = Assert.Single(VolumeGroupDetector.Group(Candidates("volume.7z.001")));

            Assert.True(group.IsComplete);
            Assert.Equal(1, group.KnownVolumeCount);
            Assert.Empty(group.MissingVolumeNames);
            Assert.Contains("只找到 1 卷，可能不完整", group.Note);
        }

        [Fact]
        public void Group_DifferentBaseNames_AreNotMerged()
        {
            var groups = VolumeGroupDetector.Group(Candidates("a.7z.001", "a.7z.002", "b.7z.001"));

            Assert.Equal(2, groups.Count);

            var a = groups.Single(g => g.BaseName == "a.7z");
            Assert.Equal(2, a.KnownVolumeCount);
            Assert.True(a.IsComplete);

            // b 只有一卷：不能因为"目录里还有 a 的分卷"就把别人的卷算进来。
            var b = groups.Single(g => g.BaseName == "b.7z");
            Assert.Equal(1, b.KnownVolumeCount);
            Assert.Equal(@"C:\t\b.7z.001", b.FirstVolumePath);
        }

        [Fact]
        public void Group_DifferentDirectories_AreNotMerged()
        {
            var files = new List<VolumeCandidate>
            {
                new VolumeCandidate { Path = @"C:\t\volume.7z.001" },
                new VolumeCandidate { Path = @"C:\t\sub\volume.7z.002" }
            };

            var groups = VolumeGroupDetector.Group(files);

            Assert.Equal(2, groups.Count);
            Assert.Equal(@"C:\t", groups[0].DirectoryPath);
            Assert.Equal(@"C:\t\sub", groups[1].DirectoryPath);
            Assert.All(groups, g => Assert.Equal(1, g.KnownVolumeCount));
        }

        [Fact]
        public void Group_TrailingTextAfterVolumeMark_IsAVolume()
        {
            /*
             * ⚠ 这条用例的**口径在 2026-09-28 被用户推翻过**，别照着老名字改回去：
             * 老口径是"卷标记必须是最后一段"，于是 `volume.7z.003.txt` 不算分卷、被当成独立压缩包
             * （一键处理接着就会把 `.003` 吃掉）。用户原话：「x.001.txt 依旧不算（点在段外）。
             * 这个难道你就弄不了了吗」。现在的判据：卷标记后面**只要不是已知压缩后缀**，就还算这一组的卷。
             */
            var group = Assert.Single(VolumeGroupDetector.Group(
                Candidates("volume.7z.001", "volume.7z.002", "volume.7z.003.txt")));

            Assert.Equal(3, group.KnownVolumeCount);
            Assert.True(group.IsComplete);
            Assert.Contains(group.Volumes, v => v.Path.EndsWith("volume.7z.003.txt", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Group_TrailingArchiveExtension_IsStillNotAVolume()
        {
            // 卷标记后面挂着**已知压缩后缀**的不算这一组（`x.7z.001.zip` 更像"一个真 zip 被改了名"）
            var group = Assert.Single(VolumeGroupDetector.Group(
                Candidates("volume.7z.001", "volume.7z.002")));

            Assert.Equal(2, group.KnownVolumeCount);
            Assert.DoesNotContain(group.Volumes, v => v.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Group_MixedCaseSameDirectory_AreOneGroup()
        {
            var group = Assert.Single(VolumeGroupDetector.Group(Candidates("VOLUME.7Z.001", "volume.7z.002")));

            Assert.Equal(2, group.KnownVolumeCount);
            Assert.True(group.IsComplete);

            // 基名保留首次出现时的写法，只有比较时忽略大小写 —— 显示给用户的名字不该被悄悄改写。
            Assert.Equal("VOLUME.7Z", group.BaseName);
            Assert.Equal(@"C:\t\VOLUME.7Z.001", group.FirstVolumePath);
        }

        [Fact]
        public void Group_MixedCaseDirectory_AreOneGroup()
        {
            var files = new List<VolumeCandidate>
            {
                new VolumeCandidate { Path = @"C:\T\volume.7z.001" },
                new VolumeCandidate { Path = @"c:\t\volume.7z.002" }
            };

            var group = Assert.Single(VolumeGroupDetector.Group(files));

            Assert.Equal(2, group.KnownVolumeCount);
            Assert.True(group.IsComplete);
        }

        [Fact]
        public void Group_ZipSpannedWithBody_BodyIsFirstVolume()
        {
            var group = Assert.Single(VolumeGroupDetector.Group(Candidates("x.z01", "x.zip", "x.z02")));

            Assert.Equal("x", group.BaseName);
            Assert.Equal(3, group.KnownVolumeCount);
            Assert.True(group.IsComplete);
            Assert.Empty(group.MissingVolumeNames);
            Assert.Equal(@"C:\t\x.zip", group.FirstVolumePath);
            Assert.Equal(
                new[] { @"C:\t\x.zip", @"C:\t\x.z01", @"C:\t\x.z02" },
                group.Volumes.Select(v => v.Path).ToArray());
        }

        [Fact]
        public void Group_ZipSpannedWithoutBody_StartsAtZ01AndReportsMissingBody()
        {
            var group = Assert.Single(VolumeGroupDetector.Group(Candidates("x.z01", "x.z02")));

            Assert.False(group.IsComplete);
            Assert.Equal(2, group.KnownVolumeCount);
            Assert.Equal(new[] { "x.zip" }, group.MissingVolumeNames.ToArray());
            Assert.Equal(@"C:\t\x.z01", group.FirstVolumePath);
            Assert.Contains("x.zip", group.Note);
        }

        [Fact]
        public void Group_RarOldWithBody_BodyIsFirstVolume()
        {
            var group = Assert.Single(VolumeGroupDetector.Group(Candidates("x.rar", "x.r00", "x.r01")));

            Assert.Equal("x", group.BaseName);
            Assert.Equal(3, group.KnownVolumeCount);
            Assert.True(group.IsComplete);
            Assert.Equal(@"C:\t\x.rar", group.FirstVolumePath);
            Assert.Equal(
                new[] { @"C:\t\x.rar", @"C:\t\x.r00", @"C:\t\x.r01" },
                group.Volumes.Select(v => v.Path).ToArray());
        }

        [Fact]
        public void Group_RarOldWithoutBody_StartsAtR00AndReportsMissingBody()
        {
            var group = Assert.Single(VolumeGroupDetector.Group(Candidates("x.r00", "x.r01")));

            Assert.False(group.IsComplete);
            Assert.Equal(new[] { "x.rar" }, group.MissingVolumeNames.ToArray());
            Assert.Equal(@"C:\t\x.r00", group.FirstVolumePath);
        }

        [Fact]
        public void Group_RarPartNumbered_TotalStaysUnknown()
        {
            var group = Assert.Single(VolumeGroupDetector.Group(
                Candidates("movie.part3.rar", "movie.part1.rar", "movie.part2.rar")));

            Assert.Equal("movie", group.BaseName);
            Assert.Equal(3, group.KnownVolumeCount);
            Assert.True(group.IsComplete);

            // partN 的名字里没有"共几卷"，所以期望卷数只能是 0 —— 不许拿最大卷号冒充总数。
            Assert.Equal(0, group.ExpectedVolumeCount);
            Assert.Empty(group.MissingVolumeNames);
            Assert.Equal(@"C:\t\movie.part1.rar", group.FirstVolumePath);
        }

        [Fact]
        public void Group_RarPartNumberedWithGap_KeepsDigitWidth()
        {
            var group = Assert.Single(VolumeGroupDetector.Group(
                Candidates("movie.part01.rar", "movie.part03.rar")));

            Assert.False(group.IsComplete);
            Assert.Equal(new[] { "movie.part02.rar" }, group.MissingVolumeNames.ToArray());
            Assert.Contains("movie.part02.rar", group.Note);
        }

        [Fact]
        public void Group_PartNumberedWithoutOuterExtension_FormsGroup()
        {
            var group = Assert.Single(VolumeGroupDetector.Group(Candidates("data.part1", "data.part2")));

            Assert.Equal("data", group.BaseName);
            Assert.Equal(2, group.KnownVolumeCount);
            Assert.True(group.IsComplete);
            Assert.Equal(@"C:\t\data.part1", group.FirstVolumePath);
        }

        // part0 不是"第 0 卷"，它连分卷标记都不算（只会被当成一个名字里带 part 的普通 rar）。
        // 认了它，缺卷检测就会开始报不存在的名字。
        [Fact]
        public void Group_Part0_DoesNotJoinTheVolumeGroup()
        {
            var group = Assert.Single(VolumeGroupDetector.Group(
                Candidates("x.part0.rar", "x.part1.rar", "x.part2.rar")));

            Assert.Equal("x", group.BaseName);
            Assert.Equal(2, group.KnownVolumeCount);
            Assert.True(group.IsComplete);
            Assert.Equal(@"C:\t\x.part1.rar", group.FirstVolumePath);
        }

        [Fact]
        public void Group_PlainArchives_AreNotVolumeGroups()
        {
            // 普通压缩包必须原样走"单文件识别"，不能被分卷逻辑吞掉。
            var groups = VolumeGroupDetector.Group(Candidates("movie.zip", "movie.rar", "other.7z", "readme.txt"));

            Assert.Empty(groups);
        }

        [Fact]
        public void Group_OldAndNewRarNaming_ProducesTwoGroupsWithUniqueKeys()
        {
            var groups = VolumeGroupDetector.Group(
                Candidates("x.rar", "x.r00", "x.part1.rar", "x.part2.rar"));

            Assert.Equal(2, groups.Count);
            Assert.All(groups, g => Assert.Equal("x", g.BaseName));
            Assert.Equal(2, groups.Select(g => g.GroupKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void Group_GroupKey_IsDirectoryAndBaseName()
        {
            var group = Assert.Single(VolumeGroupDetector.Group(Candidates("volume.7z.001", "volume.7z.002")));

            Assert.Equal(@"C:\t|volume.7z", group.GroupKey);
        }

        [Fact]
        public void Group_RelativeNames_HaveNoDirectoryInKey()
        {
            var files = new List<VolumeCandidate>
            {
                new VolumeCandidate { Path = "volume.7z.001" },
                new VolumeCandidate { Path = "volume.7z.002" }
            };

            var group = Assert.Single(VolumeGroupDetector.Group(files));

            Assert.Equal(string.Empty, group.DirectoryPath);
            Assert.Equal("volume.7z", group.GroupKey);
        }

        [Fact]
        public void Group_DuplicateCandidates_AreCountedOnce()
        {
            var files = new List<VolumeCandidate>
            {
                new VolumeCandidate { Path = @"C:\t\volume.7z.001" },
                new VolumeCandidate { Path = @"C:\T\VOLUME.7Z.001" },
                new VolumeCandidate { Path = @"C:\t\volume.7z.002" }
            };

            var group = Assert.Single(VolumeGroupDetector.Group(files));

            Assert.Equal(2, group.KnownVolumeCount);
            Assert.Equal(@"C:\t\volume.7z.001", group.FirstVolumePath);
        }

        [Fact]
        public void Group_KeepsCandidateSizeInVolumeOrder()
        {
            var files = new List<VolumeCandidate>
            {
                new VolumeCandidate { Path = @"C:\t\volume.7z.002", Size = 2000 },
                new VolumeCandidate { Path = @"C:\t\volume.7z.001", Size = 1000 }
            };

            var group = Assert.Single(VolumeGroupDetector.Group(files));

            Assert.Equal(new long[] { 1000, 2000 }, group.Volumes.Select(v => v.Size).ToArray());
        }

        [Fact]
        public void Group_EmptyOrNullInput_ReturnsEmpty()
        {
            Assert.Empty(VolumeGroupDetector.Group(Array.Empty<VolumeCandidate>()));
            Assert.Empty(VolumeGroupDetector.Group(null!));
        }
    }
}

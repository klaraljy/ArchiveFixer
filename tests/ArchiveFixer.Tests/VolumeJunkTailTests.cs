using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 分卷名后面**粘着垃圾**这一档（2026-09-28 真机事故，用户当场报的）。
    ///
    /// 现场：百度网盘把每个分卷名缀上「删除」，得到
    /// <c>giu910.7z.001删除</c> / <c>giu910.7z.002删除</c> / <c>giu910.7z.003删除</c>。
    /// 老判据要求"分卷标记必须是最后一段"，于是：
    ///   ① 三卷不算一组 —— 变成三个各自独立的任务（用户原话："选择了这个文件夹这两个文件也就一起被勾选了"）；
    ///   ② 更有害的是「修正后缀」把它们改成 <c>amb909.7z</c> / <c>amb909(1).7z</c> ——
    ///      卷号被吃掉，分卷链当场断掉（介质没坏，名字再也对不上）。
    ///
    /// 这组用例钉住三件事：**认得出**（是分卷）、**剥得对**（基名不含卷号）、**仍不成组**的那几种不许误伤。
    /// </summary>
    public class VolumeJunkTailTests
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
        [InlineData("giu910.7z.001删除", 1)]
        [InlineData("giu910.7z.002删除", 2)]
        [InlineData("giu910.7z.003删除", 3)]
        [InlineData("amb909.7z.001删除", 1)]
        [InlineData("x.7z.001(1)", 1)]
        [InlineData("x.part1副本", 1)]
        [InlineData("x.part02删除", 2)]
        [InlineData("x.z01删除", 2)]
        [InlineData("x.r00删除", 2)]
        [InlineData("VOLUME.7Z.001删除", 1)]
        public void 卷号后面粘着垃圾_仍然算分卷(string fileName, int expectedIndex)
        {
            Assert.Equal(expectedIndex, VolumeGroupDetector.TryGetVolumeIndex(fileName));
            Assert.True(FileNameHelper.IsVolumePartFileName(fileName));
        }

        [Theory]
        // 老口径不许回退：另起一段的后缀（点分隔）依旧不算卷号 —— 那是"后缀被改坏"，不是分卷
        [InlineData("volume.7z.001.txt")]
        [InlineData("volume.7z.002.jpg")]
        [InlineData("x.001.bak")]
        // 纯数字尾巴更像另一套位宽，不许乱猜
        [InlineData("volume.7z.0012")]
        [InlineData("movie.mp4")]
        public void 不是分卷的照旧不算分卷(string fileName)
        {
            Assert.Null(VolumeGroupDetector.TryGetVolumeIndex(fileName));
            Assert.False(FileNameHelper.IsVolumePartFileName(fileName));
        }

        [Fact]
        public void 普通rar本体_照旧按老口径算第一巻()
        {
            // movie.rar 单看名字是 rar 本族的第 1 卷（老口径，见 VolumeGroupDetectorTests），
            // 但它**不是**"分卷的一部分"（没有 .r00 就不成组）—— 别再被这次改动带偏。
            Assert.Equal(1, VolumeGroupDetector.TryGetVolumeIndex("movie.rar"));
            Assert.False(FileNameHelper.IsVolumePartFileName("movie.rar"));
        }

        [Fact]
        public void 卷号后面粘着垃圾_基名要剥干净()
        {
            // ⚠ 这一条是事故的**要害**：老代码把 "001删除" 当成"普通后缀"剥，
            //    算出来的基名是 giu910.7z —— 拿它去改名就会把 .001 吃掉。
            Assert.Equal("giu910", FileNameHelper.GetArchiveBaseName(@"H:\BaiduNetdiskDownload\giu910\giu910.7z.001删除"));
            Assert.Equal("giu910", FileNameHelper.GetArchiveBaseName(@"H:\BaiduNetdiskDownload\giu910\giu910.7z.003删除"));
            Assert.Equal("giu910", FileNameHelper.GetArchiveBaseName(@"H:\BaiduNetdiskDownload\giu910\giu910.7z.001"));
            Assert.Equal("x", FileNameHelper.GetArchiveBaseName(@"C:\t\x.part1删除"));
        }

        [Fact]
        public void 名字被网盘缀了垃圾的三卷_要归成一组一个任务()
        {
            var groups = VolumeGroupDetector.Group(Candidates(
                "giu910.7z.003删除",
                "giu910.7z.001删除",
                "giu910.7z.002删除"));

            var group = Assert.Single(groups);

            Assert.Equal("giu910.7z", group.BaseName);
            Assert.Equal(3, group.KnownVolumeCount);
            Assert.True(group.IsComplete);
            Assert.Empty(group.MissingVolumeNames);

            // 启动解压要从第 1 卷起；而且**带垃圾的那一卷**就是它，不许另算一个名字
            Assert.Equal(@"C:\t\giu910.7z.001删除", group.FirstVolumePath);
            Assert.Equal(
                new[] { @"C:\t\giu910.7z.001删除", @"C:\t\giu910.7z.002删除", @"C:\t\giu910.7z.003删除" },
                group.Volumes.Select(v => v.Path).ToArray());
        }

        [Fact]
        public void 名字被网盘缀了垃圾_缺卷提示也要带上垃圾尾巴()
        {
            // 只有 001 与 003：少的那个应该报成"带同样尾巴的 002 删除"，
            // 否则用户按提示去找 giu910.7z.002 是找不到的。
            var groups = VolumeGroupDetector.Group(Candidates("giu910.7z.001删除", "giu910.7z.003删除"));

            var group = Assert.Single(groups);

            Assert.False(group.IsComplete);
            Assert.Equal(new[] { "giu910.7z.002删除" }, group.MissingVolumeNames.ToArray());
        }

        [Fact]
        public void 已被改坏的现场_凭magic也能认出第一卷一整组()
        {
            // amb909 现场：改名之后两个文件叫 amb909.7z 与 amb909(1).7z。
            // 纯命名看不出是一组（那是"缺 001"的另一档），这里只钉住老口径别被这次改动带偏：
            // 两个平铺的 .7z 名字**不许**被凑成一组。
            var groups = VolumeGroupDetector.Group(Candidates("amb909.7z", "amb909(1).7z"));

            Assert.Empty(groups);
        }
    }
}

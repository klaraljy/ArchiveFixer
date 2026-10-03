using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
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
        // 夹在中间的垃圾（骨架规则）：z0删除3 → z03（z01 是第 2 卷，所以 z03 是第 4 卷）
        [InlineData("x.z0删除3", 4)]
        [InlineData("x.0删0除1", 1)]
        [InlineData("x.part删01", 1)]
        // 垃圾缀在卷号**前面**（社区真实案例 `xxx.7z.删除001`）
        [InlineData("x.7z.删除001", 1)]
        [InlineData("x.7z.删除002", 2)]
        // 卷标记后面还挂着点段（用户 2026-09-28："x.001.txt 难道你就弄不了了吗"）
        // ⚠ 边界（全量回归逼出来的）：只有"卷标记**紧跟压缩后缀**"才算跨段伪装 ——
        //    不加这条，`rar-android-722.132.apk` 会被剥成 `rar-android-722`（落点目录跟着变）。
        [InlineData("volume.7z.001.txt", 1)]
        [InlineData("volume.7z.002.jpg", 2)]
        [InlineData("x.rar.001.bak", 1)]
        [InlineData("x.7z.002.txt", 2)]
        public void 卷号后面粘着垃圾_仍然算分卷(string fileName, int expectedIndex)
        {
            Assert.Equal(expectedIndex, VolumeGroupDetector.TryGetVolumeIndex(fileName));
            Assert.True(FileNameHelper.IsVolumePartFileName(fileName));
        }

        [Theory]
        // 纯数字尾巴更像另一套位宽，不许乱猜
        [InlineData("volume.7z.0012")]
        // 卷标记后面挂着**已知压缩后缀**的不算这一组（`x.7z.001.zip` 更像"一个真 zip 被改了名"）
        [InlineData("volume.7z.001.zip")]
        // 与分卷无关的普通文件
        [InlineData("movie.mp4")]
        [InlineData("readme.txt")]
        public void 不是分卷的照旧不算分卷(string fileName)
        {
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
        public void 名字被伪装过的组_尺寸不规律就不认()
        {
            // 三层方案的第 2 层：名字宽松了，就必须靠"尺寸规律"兜底（除末卷外全相等）。
            // 这里三卷大小各不相同 —— 宁可不认，也不许凑成一组去改人家的名字。
            var irregular = new List<VolumeCandidate>
            {
                new() { Path = @"C:\t\x.7z.001删除", Size = 1000 },
                new() { Path = @"C:\t\x.7z.002删除", Size = 700 },
                new() { Path = @"C:\t\x.7z.003删除", Size = 300 }
            };

            Assert.Empty(VolumeGroupDetector.Group(irregular));

            // 规律的（整卷 1000 + 尾卷 300）→ 认
            var regular = new List<VolumeCandidate>
            {
                new() { Path = @"C:\t\x.7z.001删除", Size = 1000 },
                new() { Path = @"C:\t\x.7z.002删除", Size = 1000 },
                new() { Path = @"C:\t\x.7z.003删除", Size = 300 }
            };

            var group = Assert.Single(VolumeGroupDetector.Group(regular));
            Assert.Equal(3, group.KnownVolumeCount);
        }

        [Fact]
        public void 名字标准的组_不需要尺寸规律也认()
        {
            // 老口径不许被这次改动连累：标准名（.001/.002）照旧只看名字，尺寸随便。
            var candidates = new List<VolumeCandidate>
            {
                new() { Path = @"C:\t\x.7z.001", Size = 1000 },
                new() { Path = @"C:\t\x.7z.002", Size = 700 }
            };

            Assert.Single(VolumeGroupDetector.Group(candidates));
        }

        [Fact]
        public void 卷标记后面挂点段_整组也能改回标准名()
        {
            string dir = Path.Combine(Path.GetTempPath(), "af-voljunk-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                foreach (string name in new[] { "x.7z.001.txt", "x.7z.002.txt" })
                {
                    File.WriteAllBytes(Path.Combine(dir, name), new byte[16]);
                }

                string first = Path.Combine(dir, "x.7z.001.txt");
                VolumeNameRepairPlan plan = VolumeNameRepair.Plan(first, VolumeNameRepair.EnumerateFileNamesInDirectory(first));

                Assert.True(plan.CanRepair);
                Assert.Equal(2, plan.Items.Count);
                Assert.Equal(new[] { "x.7z.001", "x.7z.002" }, plan.Items.Select(i => i.SuggestedFileName).ToArray());

                Assert.True(VolumeNameRepair.TryApply(plan).Success);
                Assert.Contains("x.7z.001", Directory.GetFiles(dir).Select(Path.GetFileName).ToArray());
                Assert.Contains("x.7z.002", Directory.GetFiles(dir).Select(Path.GetFileName).ToArray());
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // 清理失败不影响判据
                }
            }
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

    /// <summary>
    /// <c>partN</c> 族的**尾巴上粘垃圾**这一档（2026-10-03 真机：他把一个 rar 里的 4 卷传给我，
    /// 名字是 <c>X.part1.rar删除</c> … <c>.part4.rar删除</c>，网盘给每卷缀了「删除」）。
    ///
    /// <para>老写法在**三处**各写了一遍、都硬编码 <c>尾巴 == "rar"</c>：
    /// <c>VolumeGroupDetector.Analyze</c>（卷序 / 归组）、<c>FileNameHelper.StripVolumeMarkers</c>
    /// （包基名 ⇒ 同组判定与落点）、<c>FileNameHelper.IsVolumePartFileName</c>（拦住会破坏分卷链的改名）。
    /// 尾巴一粘垃圾三处同时失效 ⇒ 4 卷被判成"四个基名互不相同的第 1 卷本体"：列表四行、
    /// 递归四个分支（同一组解 4 遍）、引擎那边拼不起整组。</para>
    ///
    /// <para>现在三处 + 改名目标 + 递归折叠都转调同一个出口
    /// <c>ExtensionHelper.TrySplitPartNumberedVolume</c>。这组用例把五个观察点一次钉住，
    /// 并附三条"不许剥坏"的对照（⛔ 那几条是既有红线，别被这次放宽连累）。</para>
    /// </summary>
    public class PartNumberedVolumeJunkTailTests
    {
        private const string Base = @"C:\t\X";

        private static List<VolumeCandidate> FourVolumes()
        {
            return new List<VolumeCandidate>
            {
                new() { Path = Base + ".part1.rar删除", Size = 1000 },
                new() { Path = Base + ".part2.rar删除", Size = 1000 },
                new() { Path = Base + ".part3.rar删除", Size = 1000 },
                new() { Path = Base + ".part4.rar删除", Size = 400 }
            };
        }

        [Fact]
        public void rar尾巴粘垃圾_四卷仍然是一组_基名去掉partN与rar()
        {
            VolumeGroup group = Assert.Single(VolumeGroupDetector.Group(FourVolumes()));

            Assert.Equal("X", group.BaseName);
            Assert.Equal(4, group.KnownVolumeCount);
            Assert.Empty(group.MissingVolumeNames);
            Assert.Equal(Base + ".part1.rar删除", group.FirstVolumePath);
        }

        [Fact]
        public void rar尾巴粘垃圾_卷序与首卷名都对着盘上的真名()
        {
            Assert.Equal(1, VolumeGroupDetector.TryGetVolumeIndex(Base + ".part1.rar删除"));
            Assert.Equal(2, VolumeGroupDetector.TryGetVolumeIndex(Base + ".part2.rar删除"));
            Assert.Equal(4, VolumeGroupDetector.TryGetVolumeIndex(Base + ".part4.rar删除"));

            // 补缺失卷名要拼回**磁盘上的真名**（尾巴连垃圾一起带着），否则报出来的名字盘上不存在。
            // ⚠ 它返回的是**文件名**（内部会剥掉目录）。
            Assert.Equal(
                "X.part1.rar删除",
                VolumeGroupDetector.TryGetFirstVolumeName(Base + ".part2.rar删除"));
        }

        [Fact]
        public void rar尾巴粘垃圾_包基名一组一个_而且算得上分卷名()
        {
            foreach (string name in new[]
                     {
                         "X.part1.rar删除", "X.part2.rar删除",
                         "X.part3.rar删除", "X.part4.rar删除"
                     })
            {
                Assert.Equal("X", FileNameHelper.StripVolumeMarkers(name));
                Assert.Equal("X", FileNameHelper.GetArchiveBaseName(name));
                Assert.True(FileNameHelper.IsVolumePartFileName(name), name);
            }
        }

        [Fact]
        public void rar尾巴粘垃圾_改名目标必须连rar一起保留()
        {
            string dir = Path.Combine(Path.GetTempPath(), "af-partjunk-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                foreach (VolumeCandidate candidate in FourVolumes())
                {
                    File.WriteAllBytes(Path.Combine(dir, Path.GetFileName(candidate.Path)), new byte[16]);
                }

                string first = Path.Combine(dir, Path.GetFileName(Base + ".part1.rar删除"));
                VolumeNameRepairPlan plan = VolumeNameRepair.Plan(first, VolumeNameRepair.EnumerateFileNamesInDirectory(first));

                Assert.True(plan.CanRepair);
                Assert.Equal(4, plan.Items.Count);

                // ⛔ 目标名**不许**是 `…part1`（把族后缀吃掉 = 改完引擎更打不开，比不改更糟）。
                Assert.Equal(
                    new[]
                    {
                        "X.part1.rar", "X.part2.rar",
                        "X.part3.rar", "X.part4.rar"
                    },
                    plan.Items.Select(i => i.SuggestedFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // 清理失败不影响判据
                }
            }
        }

        [Fact]
        public void 标记里夹垃圾也认_AAA夹具同形()
        {
            Assert.Equal(1, VolumeGroupDetector.TryGetVolumeIndex("111.parts1.racr"));
            Assert.Equal(2, VolumeGroupDetector.TryGetVolumeIndex("111.part删2.ra除r"));
            Assert.Equal("111", FileNameHelper.StripVolumeMarkers("111.parts1.rar"));
            Assert.Equal("111", FileNameHelper.StripVolumeMarkers("111.parts1.racr"));
        }

        [Fact]
        public void 递归层里一组卷只算一个内层归档()
        {
            var found = new List<string>
            {
                Base + ".part1.rar删除",
                Base + ".part2.rar删除",
                Base + ".part3.rar删除",
                Base + ".part4.rar删除"
            };

            List<string> collapsed = RecursiveExtractor.CollapseSameGroupVolumes(found);

            Assert.Equal(new[] { Base + ".part1.rar删除" }, collapsed.ToArray());
        }

        [Fact]
        public void 折叠不许连累别的归档_不同组的卷与普通包一个都不动()
        {
            var mixed = new List<string>
            {
                @"C:\t\a.part1.rar",
                @"C:\t\a.part2.rar",
                @"C:\t\b.7z",
                @"C:\t\c.7z"
            };

            List<string> collapsed = RecursiveExtractor.CollapseSameGroupVolumes(mixed);

            Assert.Equal(3, collapsed.Count);
            Assert.Contains(@"C:\t\a.part1.rar", collapsed);
            Assert.DoesNotContain(@"C:\t\a.part2.rar", collapsed);
            Assert.Contains(@"C:\t\b.7z", collapsed);
            Assert.Contains(@"C:\t\c.7z", collapsed);
        }

        // ── 三条"不许剥坏"的对照（既有红线，别被这次放宽连累）──

        [Fact]
        public void 对照_普通文件名不许被剥()
        {
            // 2026-09-28 全量回归逮到的真回归：`.apk` 里那串数字不是卷号。
            Assert.Equal("rar-android-722.132.apk", FileNameHelper.StripVolumeMarkers("rar-android-722.132.apk"));

            // 本体后缀被塞垃圾（zip 族）走的是另一条路，这里不许被当成 partN。
            Assert.Equal("222.zi删除p", FileNameHelper.StripVolumeMarkers("222.zi删除p"));
        }

        [Fact]
        public void 对照_数字族与标准partN照旧()
        {
            Assert.Equal("giu910.7z", FileNameHelper.StripVolumeMarkers("giu910.7z.001删除"));
            Assert.Equal("giu910", FileNameHelper.GetArchiveBaseName("giu910.7z.002删除"));
            Assert.Equal("X", FileNameHelper.StripVolumeMarkers("X.part2.rar"));
            Assert.Equal(2, VolumeGroupDetector.TryGetVolumeIndex("X.part2.rar"));
        }
    }
}

using System.Collections.Generic;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **基名只有两份**（用户 2026-10-23 拍板）：第一大步（单一压缩包）与第二大步（分卷）。
    ///
    /// <para><b>规则（逐字口径）</b>：取文件名里**从左数第一个 <c>.</c>**，删掉这个点及后面全部内容，
    /// 剩下的就是基名；没有 <c>.</c> ⇒ 整个文件名就是基名。</para>
    ///
    /// <para>⚠ 本文件替换了旧的《阶段 A 唯一出口 5 档对照表》—— 那份表钉的是"每档各一套剥法"
    /// （剥卷标记档 / 包名档 / 锚点档 / 伪装卷档 / 归组键档），用户点名的"14 处"之一就是它；
    /// 6 档体系已整体删除，这里钉**新口径**：</para>
    /// <list type="number">
    /// <item><description>用户点名的 8 个名字逐字对上；</description></item>
    /// <item><description>两步的方法对同一串名字给出同一个值（文本规则相同，前提不同）；</description></item>
    /// <item><description>落点那一处按"有没有整组基名"选步；</description></item>
    /// <item><description>判不出（空 / 以点开头）⇒ 空串，绝不硬剪。</description></item>
    /// </list>
    /// </summary>
    public class ArchiveBaseNameTests
    {
        /// <summary>用户点名的 8 个 + 几格旧现场（脏族后缀 / 多点 / 单段）。</summary>
        public static IEnumerable<object[]> Cases() => new[]
        {
            new object[] { "111.ra删除r", "111" },
            new object[] { "222-c.7AAAAAz", "222-c" },
            new object[] { "333---v.zi中文中文中文p", "333---v" },
            new object[] { "111.7z.001", "111" },
            new object[] { "222.zi删除p.002", "222" },
            new object[] { "333.part3.rar", "333" },
            new object[] { "444.AAAAA.7删除z", "444" },
            new object[] { "555", "555" },
            new object[] { "444.p1art2.part2.rar", "444" },
            new object[] { "movie.2024", "movie" },
            new object[] { "222.rar.jpg", "222" },
            new object[] { "222.zscip", "222" },
            new object[] { "222.z01", "222" },
            new object[] { "x.z01", "x" },
            new object[] { "x.z0删除3", "x" },
            new object[] { "VOLUME.7Z.001删除", "VOLUME" },
            new object[] { "amb909.7sz.00c1", "amb909" },
            new object[] { "rar-android-722.132.apk", "rar-android-722" },
            new object[] { @"C:\t\111.ra删除r", "111" }
        };

        [Theory]
        [MemberData(nameof(Cases))]
        public void 两步_同一条规则_第一个点之前(string name, string expected)
        {
            Assert.Equal(expected, FileNameHelper.FirstStepBaseName(name));
            Assert.Equal(expected, FileNameHelper.SecondStepBaseName(name));

            // 两个旧名字现在只是"第一大步那份"的别名（⛔ 不许再长出第二套剥法）。
            Assert.Equal(expected, FileNameHelper.GetArchiveBaseName(name));
            Assert.Equal(expected, FileNameHelper.GetSafeArchiveBaseName(name));
        }

        [Theory]
        [InlineData("", "")]
        [InlineData(".zip", "")]
        [InlineData(".hidden.rar", "")]
        [InlineData("   ", "")]
        public void 判不出_一律空串_绝不硬剪(string name, string expected)
        {
            Assert.Equal(expected, FileNameHelper.FirstStepBaseName(name));
            Assert.Equal(expected, FileNameHelper.SecondStepBaseName(name));
        }

        [Fact]
        public void 落点_单一包走第一大步_分卷组走第二大步()
        {
            // 没有整组基名 ⇒ 第一大步（单一压缩包）：movie.2024 → movie
            Assert.Equal("movie", OutputPlacement.ResolveArchiveBaseName(@"C:\111\movie.2024"));
            Assert.Equal("111", OutputPlacement.ResolveArchiveBaseName(@"C:\111\111.ra删除r"));

            // 给了整组基名 ⇒ 第二大步（分卷）：222.7z.001 → 222
            Assert.Equal("222", OutputPlacement.ResolveArchiveBaseName(@"C:\111\000.7z.001", "222.7z.001"));
        }

        [Theory]
        [InlineData("111.7z.001", "111")]
        [InlineData("222.part1.rar", "222")]
        [InlineData("333.z01", "333")]
        [InlineData("444.ra删除r", "444")]
        public void 第二大步_归组键与包基名同一个值(string name, string expected)
        {
            // 组键（归组）与包基名（落点）必须逐字相等 —— 否则同一组每一卷各建一个目录。
            Assert.Equal(expected, FileNameHelper.SecondStepBaseName(name));
            Assert.Equal(expected, OutputPlacement.ResolveArchiveBaseName(name, name));
        }
    }
}

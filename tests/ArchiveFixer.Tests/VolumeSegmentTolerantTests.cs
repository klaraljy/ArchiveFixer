using ArchiveFixer.Helpers;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「容错卷标记」这一档（用户 2026-09-28 第三次真机：`amb909.7sz.00c1` / `amb909.7删z.00除2`）。
    ///
    /// <para>他这次把干扰字符**塞进了卷号内部**、而且是**字母**（`001`→`00c1`），老的两档
    /// （整段是标记 / 前缀标记+尾巴 / 剔非字母数字）都认不出来 —— 结果一卷被当成"后缀写错"、
    /// 只好把卷号丢掉改成 `amb909.7sz.7z`，另一卷修成 `amb909.7删z.002`，两个基名不同 → 散成两组。</para>
    ///
    /// <para>这一组只钉**判据本身**：允许删最多 2 个"非纯数字"的多余字符后剩下合法卷标记，且**候选唯一**。
    /// ⛔ 它不负责"敢不敢用"—— 真要用它去改名字，调用方还得拿到"兄弟卷佐证 + 尺寸规律"。</para>
    /// </summary>
    public class VolumeSegmentTolerantTests
    {
        [Theory]
        [InlineData("00c1", "001")]
        [InlineData("7删z", null)]          // 段本身不是卷标记（垃圾在**后缀段**里，由基名那边处理）
        [InlineData("00除2", "002")]
        [InlineData("0删0除1", "001")]      // 非字母数字的，老骨架规则已经认得
        [InlineData("001", "001")]
        [InlineData("z01", "z01")]
        [InlineData("part1", "part1")]
        [InlineData("00c01", "001")]        // 删掉 `0c` 两个字符 → 001（删掉的不是纯数字，允许）
        [InlineData("0012", null)]          // 删掉的是纯数字 → 不猜
        [InlineData("0c0d1", "001")]        // 删掉两个字母 → 001
        public void 容错判据_删掉少量非数字干扰后是合法卷标记才算(string segment, string? expected)
        {
            bool ok = ExtensionHelper.TrySplitVolumeSegmentTolerant(segment, out string mark, out _);

            if (expected == null)
            {
                Assert.False(ok, $"「{segment}」不该被认成卷标记（宁可不认，也不乱猜）");
                return;
            }

            Assert.True(ok, $"「{segment}」应该被认出来");
            Assert.Equal(expected, mark);
        }

        [Fact]
        public void 容错判据_认不出来时不许瞎猜()
        {
            Assert.False(ExtensionHelper.TrySplitVolumeSegmentTolerant("00c1x9", out _, out _));
            Assert.False(ExtensionHelper.TrySplitVolumeSegmentTolerant("apk", out _, out _));
            Assert.False(ExtensionHelper.TrySplitVolumeSegmentTolerant(string.Empty, out _, out _));
        }

        [Fact]
        public void 夹在号码中间的垃圾_不许当成尾巴往外传()
        {
            // 用户 2026-09-28 真机：`amb909.7删z.00除2` 补出来的缺卷名是 `amb909.7z.00100除2`
            // —— 垃圾夹在号码中间，却被拼到了"卷号后面"，成了磁盘上不存在的名字。
            Assert.True(ExtensionHelper.TrySplitVolumeSegmentLoose("00除2", out string mark, out string junk));
            Assert.Equal("002", mark);
            Assert.Equal(string.Empty, junk);

            // 真·后缀垃圾照旧带尾巴（`giu910.7z.002删除` 才是用户能在磁盘上找到的那个名字）
            Assert.True(ExtensionHelper.TrySplitVolumeSegmentLoose("002删除", out string mark2, out string junk2));
            Assert.Equal("002", mark2);
            Assert.Equal("删除", junk2);
        }
    }
}

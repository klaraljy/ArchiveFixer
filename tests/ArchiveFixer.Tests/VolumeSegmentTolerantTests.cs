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

        /// <summary>
        /// **第五档（2026-10-12）：数字型"拉长卷号"** —— `0000000001` → `001`。
        ///
        /// <para>现场（大测试困难批，样本 `22222222` 逐字）：造包细则的"重复字符"那一法把 `.001`
        /// 写成 `.0000000001`；套娃包解开之后吐出这一片，而前四档一条都命不中 ⇒ 它在链里被当成
        /// "普通内层包"去开 ⇒ 打不开 ⇒ 逐字 `已完成 1 层，停在第 2 层，原因：压缩包损坏` ⇒
        /// 那一单失败、产物被清掉 ⇒ 整组永远缺 `.001` ⇒ ①「内容物解出来」红。</para>
        ///
        /// <para>判据：整段都是数字、长度 &gt; 3 时，把"同一字符连写 ≥3 次"的片段各压成 1 个字符，
        /// 压完必须正好是 3 位数字的合法卷标记。</para>
        /// </summary>
        [Theory]
        [InlineData("0000000001", "001")]
        [InlineData("00000000012", "012")]
        [InlineData("0022222222222", "002")]
        public void 数字型拉长卷号_压掉连写三以上的重复字符后是合法标记才算(string segment, string expected)
        {
            Assert.True(
                ExtensionHelper.TrySplitVolumeSegmentTolerant(segment, out string mark, out _),
                $"「{segment}」应该被认出来（同一条口径：⛔ 不设个数上限）");

            Assert.Equal(expected, mark);
        }

        /// <summary>
        /// 第五档的边界：没有"≥3 次连写"的全数字段照旧不认（老口径一个字不改，`0012` 就是既有的那条反例）；
        /// `1000000` 压掉 6 个 0 之后是 `100`（合法三位标记）⇒ 认。
        /// </summary>
        [Theory]
        [InlineData("0012", null)]
        [InlineData("11111111111", null)]
        [InlineData("1000000", "100")]
        public void 第五档的边界_压完不成三位合法标记就不认(string segment, string? expected)
        {
            bool ok = ExtensionHelper.TrySplitVolumeSegmentTolerant(segment, out string mark, out _);

            if (expected == null)
            {
                Assert.False(ok, $"「{segment}」不该被认成卷标记");
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

        /// <summary>
        /// **第四档（2026-10-12）：任意长度的"垃圾前缀"**（用户口径逐字：「这种情况就要去改代码了，
        /// 范围太小了，以后凡是限制两到三个的都会出现问题，赶紧修改」）。
        ///
        /// <para>现场（大测试困难批，样本 `22222222`）：顶层那一片叫 `资源222-L2.zip.--p002` ——
        /// 卷标记 `.002` 前面被塞了 **3 个**符号（`--p`）。老的三档（删 ≤2 个字符 / 首尾对齐的骨架）
        /// **一条都命不中** ⇒ 那一片判不出是分卷 ⇒ 被当成"双面文件"从偏移 162 抠出来 ⇒ 整组凑不齐 ⇒ ①红。</para>
        ///
        /// <para>⛔ 判据是**结构**（标记贴段末 + 垃圾里至少一个非字母数字 + 垃圾里没有 ≥3 个连续 ASCII 字母），
        /// 不是"最多几个字符"—— 所以以后塞 5 个、8 个符号照样认得出。</para>
        /// </summary>
        [Theory]
        [InlineData("--p002", "002")]        // 真机现场：3 个符号 + 卷号
        [InlineData("@@@003", "003")]        // 5 个字符（老尺子上限 2 ⇒ 认不出）
        [InlineData("#%$@!&*003", "003")]    // 8 个符号（老尺子上限 2 ⇒ 认不出）
        [InlineData("中文002", "002")]        // 中文垃圾前缀
        [InlineData("#%z01", "z01")]         // 字母前缀优先（z 族）
        [InlineData("【删除】r00", "r00")]    // r 族
        public void 任意长度的垃圾前缀_照样认得出卷标记(string segment, string expected)
        {
            Assert.True(
                ExtensionHelper.TrySplitVolumeSegmentTolerant(segment, out string mark, out _),
                $"「{segment}」应该被认出来（结构判据，⛔ 不设个数上限）");

            Assert.Equal(expected, mark);
        }

        /// <summary>
        /// 反向：**别把真名字吃进来**（第四档的结构防线）。
        ///
        /// <list type="bullet">
        /// <item><description>`photo-123` / `backup-002`：垃圾里出现 ≥3 个连续 ASCII 字母 ⇒ 那是一个词，不是塞进去的垃圾；</description></item>
        /// <item><description>`--p0012`：段末连续数字 4 位 ⇒ 判不出是 `001` 还是 `012` ⇒ 宁可不认；</description></item>
        /// <item><description>`--p1`：卷号只有 1 位 ⇒ 不是卷标记（老口径）。</description></item>
        /// </list>
        /// </summary>
        [Theory]
        [InlineData("photo-123")]
        [InlineData("backup-002")]
        [InlineData("--p0012")]
        [InlineData("--p1")]
        public void 第四档不许把真名字或判不出的形状当成卷标记(string segment)
        {
            Assert.False(
                ExtensionHelper.TrySplitVolumeSegmentTolerant(segment, out _, out _),
                $"「{segment}」不该被认成卷标记（宁可不认，也不乱猜）");
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

using System;
using System.IO;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「其余物里的分卷是半套就什么都不做」那道防数据丢失闸门（<see cref="RestVolumeCompletenessGate"/>）
    /// 靠的基名 —— 用户 2026-10-23 拍板：**基名只有两份方法**（第一大步 / 第二大步），
    /// 规则都是"第一个点之前"，因此**归档基名与包基名不再是两个值**。
    ///
    /// <para>现场（2026-09-30 真机 <c>一只顶美合集</c> / 真案 ④）：其余物里是**本体**那一份、
    /// 成品目录里是**同组的片** —— 判据只问"两边基名相不相等"。相等 ⇒ 认得出这是同一组被拆在两边
    /// ⇒ **整份处理其余物一个字节都不做**；不等 ⇒ 判不出 ⇒ 放行 ⇒ 那一组会被整份删掉。</para>
    ///
    /// <para>⚠ 旧口径下 <c>222.zscip</c> 的两个量不相等（包基名只剥已知归档后缀），那一格曾用来论证
    /// "两个量不许合并"；用户已拍板合并成一条规则，这里钉的是**新口径下闸门照样拦得住**。</para>
    /// </summary>
    public class RestVolumeGateBaseNameTests : IDisposable
    {
        private readonly string _root;

        public RestVolumeGateBaseNameTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerRestGateBase", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        /// <summary>
        /// 真案 ④ 的那一对（本体 <c>222.zscip</c> + 同组的一片 <c>222.z删除01</c>）：
        /// 两处基名同解（都是 <c>222</c>），闸门**拦下**。
        /// </summary>
        [Fact]
        public void 真案四那一对_两处基名同解_闸门拦下()
        {
            // ① 两处取值必须逐字相等（同一条规则、同一份实现）。
            Assert.Equal("222", FileNameHelper.GetArchiveBaseName("222.zscip"));
            Assert.Equal("222", FileNameHelper.GetArchiveBaseName("222.z删除01"));
            Assert.Equal("222", OutputPlacement.ResolveArchiveBaseName("222.zscip"));
            Assert.Equal("222", OutputPlacement.ResolveArchiveBaseName("222.z删除01"));

            // ② 布置：其余物里是本体那一份，成品目录里躺着同组的一片。
            string artifactRoot = Path.Combine(_root, "AAA");
            string rest = Path.Combine(artifactRoot, ProcessArtifactLayout.ArtifactDirectoryName);
            Directory.CreateDirectory(rest);

            File.WriteAllBytes(Path.Combine(rest, "222.zscip"), new byte[64]);
            File.WriteAllBytes(Path.Combine(artifactRoot, "222.z删除01"), new byte[64]);

            // ③ 闸门结论：**拦下**（两边基名都是 `222` ⇒ 认得出是同一组被拆在两边）。
            string? blocker = RestVolumeCompletenessGate.DescribeBlocker(rest);

            Assert.NotNull(blocker);
            Assert.Contains("222.zscip", blocker!, StringComparison.Ordinal);
            Assert.Contains("222.z删除01", blocker!, StringComparison.Ordinal);
        }

        /// <summary>
        /// 两处取值不许再分叉（旧口径那一格是"包基名 ≠ 归档基名"）—— 现在同一条规则、同一份实现。
        /// </summary>
        [Theory]
        [InlineData("222.zscip")]
        [InlineData("222.z删除01")]
        [InlineData("444.p1art2.part2.rar")]
        [InlineData("movie.2024")]
        public void 归档基名与包基名同解(string name)
        {
            Assert.Equal(
                FileNameHelper.GetArchiveBaseName(name),
                OutputPlacement.ResolveArchiveBaseName(name));
        }
    }
}

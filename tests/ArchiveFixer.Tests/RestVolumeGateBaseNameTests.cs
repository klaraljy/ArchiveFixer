using System;
using System.IO;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「两个都叫**包基名**的量」在 <see cref="RestVolumeCompletenessGate"/>（"其余物里的分卷是半套就
    /// 什么都不做"那道防数据丢失闸门）上的差别 —— 用户 2026-10-04 问"`222.zscip` 那一格是什么情况"，
    /// 这里把它**量出来**并钉住。
    ///
    /// <para>两个量（都由**唯一基名出口**算，见 <c>ArchiveBaseNameTests</c>）：
    /// <list type="bullet">
    /// <item><description><b>归档基名</b> <see cref="FileNameHelper.GetArchiveBaseName"/>（本闸门用的那个，
    /// 也是 <c>EngineRouter</c> / <c>RecursiveExtractor</c> 当匹配键用的那个）：剥标记 + 天真剥一层后缀
    /// ⇒ <c>222.zscip</c> = <c>222</c>、<c>222.z删除01</c> = <c>222</c> ⇒ **两者相等**。</description></item>
    /// <item><description><b>包基名</b> <c>OutputPlacement.ResolveArchiveBaseName</c>（落点用）：
    /// 只剥**已知归档后缀** ⇒ <c>222.zscip</c> = <c>222.zscip</c>（认不出 <c>zscip</c> 是后缀）、
    /// <c>222.z删除01</c> = <c>222</c> ⇒ **两者不等**。</description></item>
    /// </list></para>
    ///
    /// <para>现场（2026-09-30 真机 <c>一只顶美合集</c> / 真案 ④）：其余物里是**本体**那一份、
    /// 成品目录里是**同组的片** —— 判据只问"两边基名相不相等"。相等 ⇒ 认得出这是同一组被拆在两边
    /// ⇒ **整份处理其余物一个字节都不做**；不等 ⇒ 判不出 ⇒ 放行 ⇒ 那一组会被整份删掉。</para>
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
        /// 两个量的取值、以及闸门**按当前取值**给出的结论。
        /// </summary>
        [Fact]
        public void 真案四那一对_两个包基名的取值与闸门结论()
        {
            // ① 两个量各自的取值（差异就在"认不认得出 zscip/z删除01 里藏着后缀与卷标记"）。
            Assert.Equal("222", FileNameHelper.GetArchiveBaseName("222.zscip"));
            Assert.Equal("222", FileNameHelper.GetArchiveBaseName("222.z删除01"));
            Assert.Equal("222.zscip", OutputPlacement.ResolveArchiveBaseName("222.zscip"));
            Assert.Equal("222", OutputPlacement.ResolveArchiveBaseName("222.z删除01"));

            // ② 布置：其余物里是本体那一份，成品目录里躺着同组的一片。
            string artifactRoot = Path.Combine(_root, "AAA");
            string rest = Path.Combine(artifactRoot, ProcessArtifactLayout.ArtifactDirectoryName);
            Directory.CreateDirectory(rest);

            File.WriteAllBytes(Path.Combine(rest, "222.zscip"), new byte[64]);
            File.WriteAllBytes(Path.Combine(artifactRoot, "222.z删除01"), new byte[64]);

            // ③ 闸门结论：**拦下**（归档基名两边都是 `222` ⇒ 认得出是同一组被拆在两边）。
            string? blocker = RestVolumeCompletenessGate.DescribeBlocker(rest);

            Assert.NotNull(blocker);
            Assert.Contains("222.zscip", blocker!, StringComparison.Ordinal);
            Assert.Contains("222.z删除01", blocker!, StringComparison.Ordinal);
        }

        /// <summary>
        /// **对照**：把闸门换成"包基名"那一档的取值会发生什么 —— 这里不去改产品代码，
        /// 而是用**同一个判据**在两套取值下各算一遍（取值的来源只有一个出口，见 <c>ArchiveBaseNameTests</c>）。
        ///
        /// <para>结论：包基名那一档 <c>222.zscip</c> ≠ <c>222</c> ⇒ 两边基名**不相等** ⇒
        /// 闸门**放行**（"外面没有同组的片"）⇒ 其余物会被整份处理。
        /// ⛔ 这一格就是"两个量不许合并成一个值"的理由。</para>
        /// </summary>
        [Fact]
        public void 对照_换成包基名那一档取值_同一对名字就不再相等()
        {
            Assert.NotEqual(
                OutputPlacement.ResolveArchiveBaseName("222.zscip"),
                OutputPlacement.ResolveArchiveBaseName("222.z删除01"));

            // 闸门那一处问的就是"两边基名相不相等"（`TryGetArchivePieceBaseName` 的两个消费点同解）。
            // 相等 ⇒ 拦下；不等 ⇒ 没有可返回的拦下理由（返回 null = 放行）。
            Assert.Equal(
                FileNameHelper.GetArchiveBaseName("222.zscip"),
                FileNameHelper.GetArchiveBaseName("222.z删除01"));
        }
    }
}

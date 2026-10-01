using ArchiveFixer.Extraction;
using System;
using System.IO;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 缺口 ⑨ 的守门用例：**没人配过工作区根时，生产默认不回落到系统临时目录（C 盘）**。
    ///
    /// <para>真机背景（用户 2026-09-24 第 23 条）：「刚刚我就发现你会讲解压失败的残留放在安装包的位置，
    /// 这次是小的 5G 左右，那要是 40G 的东西……」—— 递归工作区动辄几百 MB，
    /// 落到系统盘既占空间又拖慢整机。所以不变量 12 的字面口径是"拿不到目标目录就**报错指路**，
    /// ⛔ 绝不回落程序目录 / C 盘 / 源卷根 / <c>%TEMP%</c>"。</para>
    ///
    /// <para>而 <c>RecursiveExtractor</c> 原来**默认**就回落 <c>%TEMP%</c>（= C 盘），
    /// 等于默认违反那条红线：谁新写一条"没经过批首就解压"的路，它就会悄悄写到系统盘上。
    /// 现在回落成了一位**显式开关**（默认关）—— 这三条钉的就是它。</para>
    ///
    /// <para>串行执行：它会读/改 <see cref="RecursiveExtractor.AllowSystemTempWorkspaceFallback"/>
    /// 这个进程级静态。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class WorkspaceTempFallbackGateTests
    {
        /// <summary>
        /// **产品默认必须是关着的**（⛔ 不许改回 true）：改回 true 就等于"默认违反不变量 12"，
        /// 这条会当场变红。
        /// </summary>
        [Fact]
        public void 产品默认_不许回落到系统临时目录()
        {
            Assert.False(
                TestAssemblyInitialize.ProductDefault,
                "RecursiveExtractor.AllowSystemTempWorkspaceFallback 的产品默认值必须是 false —— "
                + "默认 true 等于默认把递归工作区写到 %TEMP%（C 盘），违反不变量 12。");
        }

        /// <summary>
        /// 关着（= 生产默认）时：拿不到根就**什么都不做** —— 抛异常并**指路**，
        /// ⛔ 不是悄悄返回一个 <c>%TEMP%</c> 下的路径。
        ///
        /// <para>⚠ "一个目录都不建"这一条由代码结构保证：抛异常那一步排在
        /// <c>WorkspaceTree.EnsureHiddenDirectory</c> **之前**（本用例只钉"抛不抛、说什么"；
        /// 对照见下一条 —— 开关打开时那一支确实会去建目录，所以顺序错了这里就会露馅）。</para>
        /// </summary>
        [Fact]
        public void 关着时_拿不到根就报错指路_绝不回落()
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => RecursiveExtractor.ResolveWorkspaceRootDirectory(null, allowSystemTempFallback: false));

            Assert.Contains("不变量 12", error.Message, StringComparison.Ordinal);
            Assert.Contains("%TEMP%", error.Message, StringComparison.Ordinal);

            // 空/空白同档（旧代码用 IsNullOrWhiteSpace 判的，别在这里退化成只判 null）。
            Assert.Throws<InvalidOperationException>(
                () => RecursiveExtractor.ResolveWorkspaceRootDirectory("   ", allowSystemTempFallback: false));
        }

        /// <summary>
        /// **对照**：显式打开时才回落，而且确实落在系统临时目录下 ——
        /// 正是这一条让"关着"有了意义（不打开就永远不会走到这里）。
        /// </summary>
        [Fact]
        public void 显式打开时_才回落到系统临时目录()
        {
            string root = RecursiveExtractor.ResolveWorkspaceRootDirectory(null, allowSystemTempFallback: true);

            Assert.Equal(
                Path.Combine(Path.GetTempPath(), "ArchiveFixer", "recursive"),
                root);
        }

        /// <summary>
        /// 配了根就按配的走（两条支路互不影响）：<c>&lt;根&gt;\recursive</c>，与开关取值无关。
        /// </summary>
        [Fact]
        public void 配了根就按配的走_与开关无关()
        {
            string configured = Path.Combine(Path.GetTempPath(), "ArchiveFixerAaa", Guid.NewGuid().ToString("N"));

            string withFallbackOff = RecursiveExtractor.ResolveWorkspaceRootDirectory(configured, allowSystemTempFallback: false);
            string withFallbackOn = RecursiveExtractor.ResolveWorkspaceRootDirectory(configured, allowSystemTempFallback: true);

            Assert.Equal(Path.Combine(configured, "recursive"), withFallbackOff);
            Assert.Equal(withFallbackOff, withFallbackOn);

            try
            {
                Directory.Delete(configured, recursive: true);
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }
    }
}

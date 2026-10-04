using System;
using System.IO;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// <b>②页「嵌套与覆盖」那一堆解释文字收进折叠区</b>（用户 2026-10-04 当天第三次改口径）。
    ///
    /// <para>用户原话：「这个嵌套与覆盖你的文字这么多，你就不会放到说明里面或者是在旁边弄一个小弹窗
    /// 点击一下就会出来详细解释」⇒ 他当场选「就地折叠」（①页「手动操作」已经是这个手法）。</para>
    ///
    /// <para>这里钉四件事：① 递归三档那三段说明在**一个默认收起的折叠区**里（页面上只占一行标题）；
    /// ② 覆盖策略 / 中间层 / 分卷拼装那三段长说明也一样收在折叠区里；③ 控件旁边留的是**短句**
    /// （长的那份不再摊在页面上、也不再整段挂在 ToolTip 上）；④ ⛔ **一个字都没删**：
    /// 三档说明与那三段长说明的正文仍逐字在 <see cref="StatusText"/> 里，折叠区引用的就是它们。</para>
    ///
    /// <para><b>红检</b>：把三档说明从 <c>Expander</c> 里挪回页面（或给折叠区加
    /// <c>IsExpanded="True"</c>）⇒ <see cref="三档说明收在默认收起的折叠区里_页面上只剩一行标题"/> 当场红。</para>
    /// </summary>
    public class NestedGroupCollapseTests
    {
        private const string ExtractionTabXaml = @"src\ArchiveFixer\Views\Tabs\ExtractionTab.xaml";

        [Fact]
        public void 三档说明收在默认收起的折叠区里_页面上只剩一行标题()
        {
            string xaml = ReadRepositoryFile(ExtractionTabXaml);

            // ① 第一段折叠区：标题就是那句「详细说明：这三档各自怎么走」。
            int expanderStart = xaml.IndexOf(
                "Header=\"{x:Static models:StatusText.RecursionModeHintLabel}\"",
                StringComparison.Ordinal);

            Assert.True(expanderStart > 0, "三档说明那个折叠区不见了（标题应当引用 StatusText.RecursionModeHintLabel）");

            int expanderEnd = xaml.IndexOf("</Expander>", expanderStart, StringComparison.Ordinal);
            Assert.True(expanderEnd > expanderStart, "折叠区没有闭合");

            string inside = xaml.Substring(expanderStart, expanderEnd - expanderStart);
            string before = xaml.Substring(0, expanderStart);

            // ② 三段说明**都在折叠区里面**（不是摊在页面上）。
            foreach (string constant in new[]
                     {
                         "StatusText.RecursionModeAllBranchesHint",
                         "StatusText.RecursionModeSingleChainHint",
                         "StatusText.RecursionModeSingleLayerHint"
                     })
            {
                Assert.Contains(constant, inside, StringComparison.Ordinal);
                Assert.DoesNotContain(constant, before, StringComparison.Ordinal);
            }

            // ③ 默认收起：本页不许有"一打开就展开"的折叠区（用户点名嫌的就是摊开的文字）。
            Assert.DoesNotContain("IsExpanded", xaml, StringComparison.Ordinal);
        }

        [Fact]
        public void 其余长说明也收在折叠区里_控件旁边只留短句()
        {
            string xaml = ReadRepositoryFile(ExtractionTabXaml);

            // ① 第二段折叠区（覆盖策略 / 中间层 / 分卷拼装）在，而且三段都在它里面。
            int expanderStart = xaml.IndexOf(
                "Header=\"{x:Static models:StatusText.NestedExtrasDetailHeader}\"",
                StringComparison.Ordinal);

            Assert.True(expanderStart > 0, "「嵌套与覆盖」最下面那个折叠区不见了");

            int expanderEnd = xaml.IndexOf("</Expander>", expanderStart, StringComparison.Ordinal);
            string inside = xaml.Substring(expanderStart, expanderEnd - expanderStart);

            foreach (string constant in new[]
                     {
                         "StatusText.NestedOverwriteDetail",
                         "StatusText.NestedMiddleLayerDetail",
                         "StatusText.NestedVolumeAssemblyDetail"
                     })
            {
                Assert.Contains(constant, inside, StringComparison.Ordinal);
            }

            // ② 控件旁边留的是短句（两个勾选框的 ToolTip 都改成短的那份）。
            Assert.Contains("StatusText.NestedMiddleLayerShortHint", xaml, StringComparison.Ordinal);
            Assert.Contains("StatusText.NestedVolumeAssemblyShortHint", xaml, StringComparison.Ordinal);

            // ③ 长的那两份**不再整段挂在 ToolTip 上**、覆盖策略那段也不再摊在页面上
            //    （判据 = 旧的字面开头一个都不许留在 XAML 里）。
            Assert.DoesNotContain("默认不勾 = 忠实档", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("有些包的形状是：一个文件", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("RAR 包默认走 UnRAR，而它没有", xaml, StringComparison.Ordinal);
        }

        [Fact]
        public void 收起来的那些说明_正文一个字都没丢_而且都还写着关键事实()
        {
            // ⛔ 折叠只改"默认看不看得见"，不改内容：每条长说明的关键事实逐条钉住。
            Assert.Contains("什么时候会停", StatusText.RecursionModeAllBranchesHint, StringComparison.Ordinal);
            Assert.Contains("代价", StatusText.RecursionModeAllBranchesHint, StringComparison.Ordinal);
            Assert.Contains("代价", StatusText.RecursionModeSingleChainHint, StringComparison.Ordinal);
            Assert.Contains("代价", StatusText.RecursionModeSingleLayerHint, StringComparison.Ordinal);

            // 覆盖策略：必须留着那条真会咬人的事实（UnRAR 没有"自动重命名已存在文件"）。
            Assert.Contains("跳过已存在文件", StatusText.NestedOverwriteDetail, StringComparison.Ordinal);

            // 中间层：两种档位的目录形状都要在（用户就是照这个例子理解的）。
            Assert.Contains(@"111\222\333\444\555\666\内容物", StatusText.NestedMiddleLayerDetail, StringComparison.Ordinal);
            Assert.Contains(@"111\222\666\内容物", StatusText.NestedMiddleLayerDetail, StringComparison.Ordinal);

            // 分卷拼装：代价必须写清（只接名字 / 不复制字节 / 不改用户文件）。
            Assert.Contains("硬链接", StatusText.NestedVolumeAssemblyDetail, StringComparison.Ordinal);
            Assert.Contains("不改你的文件", StatusText.NestedVolumeAssemblyDetail, StringComparison.Ordinal);

            // 面向用户的字符串里不许写 Markdown（强调一律用「」）。
            foreach (string text in new[]
                     {
                         StatusText.RecursionModeHintLabel,
                         StatusText.NestedExtrasDetailHeader,
                         StatusText.NestedOverwriteDetail,
                         StatusText.NestedMiddleLayerDetail,
                         StatusText.NestedVolumeAssemblyDetail,
                         StatusText.NestedMiddleLayerShortHint,
                         StatusText.NestedVolumeAssemblyShortHint
                     })
            {
                Assert.DoesNotContain("**", text, StringComparison.Ordinal);
            }
        }

        /// <summary>从仓库根读一个文本文件（测试的工作目录是 bin\…，往上找到仓库那一层为止）。</summary>
        private static string ReadRepositoryFile(string relativePath)
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, relativePath);

                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException($"仓库里找不到这个文件：{relativePath}");
        }
    }
}

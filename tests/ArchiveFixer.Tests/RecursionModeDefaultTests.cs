using System;
using System.IO;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// <b>用例①（用户 2026-10-04）</b>：**默认档 = 展开所有分支**。
    ///
    /// <para>用户原话：「那你为什么会将这个单链解压设为默认……现在将默认设为全文件解压，要不然出现了上面的情况
    /// 你就没有了，并且在选择旁边详细说明情况」。</para>
    ///
    /// <para>这里钉三件事：① 出厂默认值（新装 / 无设置时就是它）；② ②页那个下拉框的**初值**来自设置
    /// （XAML 里不许另写死一个默认项）；③ 三档的「详细说明」真的挂在②页上（文案唯一来源 = <see cref="StatusText"/>）。</para>
    ///
    /// <para><b>红检</b>：把 <see cref="AppSettings.RecursionMode"/> 的默认值改回 <c>"SingleLayer"</c>
    /// ⇒ 本用例当场红（`Assert.Equal() Failure: Expected "AllBranches" / Actual "SingleLayer"`）。</para>
    /// </summary>
    public class RecursionModeDefaultTests
    {
        /// <summary>②页「嵌套压缩包」那一栏所在的文件（初值与三档说明都在这里渲染）。</summary>
        private const string ExtractionTabXaml = @"src\ArchiveFixer\Views\Tabs\ExtractionTab.xaml";

        [Fact]
        public void 默认档_出厂就是展开所有分支_空值也归一到它()
        {
            // ① 出厂默认（`CreateDefault` 与属性初值两处都要对；设置文件不存在时走的就是 CreateDefault）。
            Assert.Equal("AllBranches", new AppSettings().RecursionMode);
            Assert.Equal("AllBranches", AppSettings.CreateDefault().RecursionMode);

            // ② 归一化：空 / 只有空格（老配置缺字段、手改坏）一律回到出厂默认那一档。
            var blank = new AppSettings { RecursionMode = "   " };
            blank.Normalize();

            Assert.Equal("AllBranches", blank.RecursionMode);

            // ③ ⛔ 用户自己存过的档一个都不许被迁移（他机器上选的是哪一档就还是哪一档）。
            var chosen = new AppSettings { RecursionMode = "SingleChain" };
            chosen.Normalize();

            Assert.Equal("SingleChain", chosen.RecursionMode);
        }

        [Fact]
        public void 二页初值来自设置_而且三档旁边真的写了详细说明()
        {
            string xaml = ReadRepositoryFile(ExtractionTabXaml);

            // ① 那个下拉框绑的就是设置本身（初值 = 设置里的值，XAML 里不许另写死一个默认项）。
            Assert.Contains(
                "SelectedValue=\"{Binding SettingsEditor.Settings.RecursionMode, Mode=TwoWay}\"",
                xaml,
                StringComparison.Ordinal);

            Assert.DoesNotContain("SelectedIndex=", xaml, StringComparison.Ordinal);

            // ② 三档一个都不少（出厂默认那一档要在选项文字里说清自己是默认）。
            Assert.Contains("Tag=\"SingleLayer\"", xaml, StringComparison.Ordinal);
            Assert.Contains("Tag=\"SingleChain\"", xaml, StringComparison.Ordinal);
            Assert.Contains("Tag=\"AllBranches\"", xaml, StringComparison.Ordinal);
            Assert.Contains("出厂默认", xaml, StringComparison.Ordinal);

            // ③ 「在选择旁边详细说明情况」：三档的说明都真的渲染出来（文案出自 StatusText，⛔ 不手写中文）。
            //    ⚠ 2026-10-04 当天用户又提「这个嵌套与覆盖你的文字这么多」⇒ 这三段现在装在一个
            //    **默认收起**的折叠区里（挂在②页上这件事没变；"默认收起 / 页面上只剩一行标题"由
            //    `NestedGroupCollapseTests` 钉住）。
            Assert.Contains("StatusText.RecursionModeHintLabel", xaml, StringComparison.Ordinal);
            Assert.Contains("StatusText.RecursionModeAllBranchesHint", xaml, StringComparison.Ordinal);
            Assert.Contains("StatusText.RecursionModeSingleChainHint", xaml, StringComparison.Ordinal);
            Assert.Contains("StatusText.RecursionModeSingleLayerHint", xaml, StringComparison.Ordinal);

            /*
             * ④ 每一条说明都要说清三件事（它怎么走 / 什么时候会停 / 空间与时间代价）——
             * 这是用户点名的那句"详细说明情况"的可判定形式，⛔ 不是"写了就算"。
             */
            foreach (string hint in new[]
                     {
                         StatusText.RecursionModeAllBranchesHint,
                         StatusText.RecursionModeSingleChainHint,
                         StatusText.RecursionModeSingleLayerHint
                     })
            {
                Assert.Contains("停", hint, StringComparison.Ordinal);
                Assert.Contains("代价", hint, StringComparison.Ordinal);

                // 面向用户的字符串里不许写 Markdown（强调一律用「」）。
                Assert.DoesNotContain("**", hint, StringComparison.Ordinal);
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

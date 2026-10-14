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

        /// <summary>
        /// ② 页**不再有「嵌套压缩包」三档那一栏**（用户 2026-10-14 最终设计：
        /// 「嵌套与覆盖：三档功能全部删除，只默认解开所有分支」）。
        ///
        /// <para>⚠ 这一条取代 2026-10-04 的「初值来自设置 + 三档旁边写了详细说明」：那一栏连同它的
        /// 说明折叠区**整格删掉**（功能没删 —— 档位固定成出厂默认那一档，见上面那条默认值用例）。
        /// ⛔ 反向断言：以后谁再把三档挂回②页，这里立刻红。</para>
        /// </summary>
        [Fact]
        public void 二页不再有三档那一栏_档位固定成出厂默认()
        {
            string xaml = ReadRepositoryFile(ExtractionTabXaml);

            // ① 那一栏的下拉框与三档选项一个字都不许留。
            Assert.DoesNotContain("SettingsEditor.Settings.RecursionMode", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("Tag=\"SingleLayer\"", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("Tag=\"SingleChain\"", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("Tag=\"AllBranches\"", xaml, StringComparison.Ordinal);

            // ② 它的说明折叠区也一起没了（三段说明的正文仍在 StatusText 里，给"说明"页与日志用）。
            Assert.DoesNotContain("StatusText.RecursionModeHintLabel", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("StatusText.RecursionModeAllBranchesHint", xaml, StringComparison.Ordinal);

            // ③ 最大嵌套层数那一格照旧留在②页（设计：「最大嵌套层数留着」）。
            Assert.Contains("SettingsEditor.Settings.MaxRecursionDepth", xaml, StringComparison.Ordinal);
        }

        /// <summary>
        /// 三档的说明**正文一个字没丢**（只是不再挂在②页上）：它们仍是「说明」页 / 日志要用的文案，
        /// 所以 2026-10-04 那条「每条都说清 怎么走 / 什么时候会停 / 空间与时间代价」的判据照旧成立。
        /// </summary>
        [Fact]
        public void 三档的说明正文一个字没丢_仍然说清怎么走与代价()
        {
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

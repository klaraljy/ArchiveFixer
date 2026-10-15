using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 功能框最下面的「展示框」（用户 2026-10-14 最终设计）。
    ///
    /// <para>用户原话：「所有说明性文字，详细的说明文字全部写入选项卡说明里面和在每一个功能框最下面
    /// 有一个展示框，点击才能详细展示，平时的功能介绍只能简洁的文字介绍」。</para>
    ///
    /// <para>这里把它变成**可判定的四件事**：</para>
    /// <list type="number">
    /// <item><description>展示框**默认收起**（不许 <c>IsExpanded</c>）、只吃 <c>Header</c> / <c>Detail</c>
    /// 两个入参、自己一句中文都不写；</description></item>
    /// <item><description>页面上**不许再摊开长说明**：<c>HintTextStyle</c> 的 TextBlock 只要 Text 字面量超过
    /// <see cref="LongHintThreshold"/> 字，就必须已经搬进展示框；
    /// ⚠ 两条**明确排除**：① 常驻警告横幅（<c>BannerTextStyle</c>）—— 它是"选中就一直放在那"的红字，
    /// 按用户 2026-09-25 第 32 条**必须常驻**；② 运行时拼出来的文字（<c>&lt;Run&gt;</c> / 绑定）——
    /// 它随机器变，不属于"说明性文字"；</description></item>
    /// <item><description>每个展示框的 Header / Detail 都来自 <see cref="StatusText"/>，而且那两个常量
    /// **真的存在**（反射逐个确认，⛔ 不是"写了就信"）；</description></item>
    /// <item><description>搬走的正文**一个字没丢**（常量里逐字含原来那几句关键事实）。</description></item>
    /// </list>
    /// </summary>
    public class BoxDetailTests
    {
        /// <summary>超过这么多字的说明就算"长说明"，必须收进展示框（用户：「平时只能简洁的文字介绍」）。</summary>
        private const int LongHintThreshold = 45;

        private static IEnumerable<string> TabFiles()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, "src", "ArchiveFixer", "Views", "Tabs");

                if (Directory.Exists(candidate))
                {
                    return Directory.GetFiles(candidate, "*.xaml").OrderBy(path => path, StringComparer.Ordinal);
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("往上找不到 src\\ArchiveFixer\\Views\\Tabs");
        }

        [Fact]
        public void 展示框自己默认收起_只有两个入参_不写中文()
        {
            string xaml = ReadRepositoryFile(Path.Combine("src", "ArchiveFixer", "Views", "Controls", "BoxDetail.xaml"));

            // ① 默认收起：连"展开"这个属性都不许出现（用户嫌的就是摊开的文字）。
            //    ⚠ 只看**代码**：注释里正解释着"不许写 IsExpanded"，那是说明不是属性。
            string code = StripXmlComments(xaml);

            Assert.DoesNotContain("IsExpanded", code, StringComparison.Ordinal);

            // ② 只有两个入参（Header / Detail），而且都从控件自己的依赖属性取。
            Assert.Contains("{Binding Header, RelativeSource={RelativeSource AncestorType=UserControl}}", xaml, StringComparison.Ordinal);
            Assert.Contains("{Binding Detail, RelativeSource={RelativeSource AncestorType=UserControl}}", xaml, StringComparison.Ordinal);

            // ③ 控件自己不写一句中文（文案唯一出处 = StatusText）。
            Assert.False(
                Regex.IsMatch(code, @"[\u4e00-\u9fff]"),
                "BoxDetail.xaml 里出现了中文字面量 —— 文案必须全部来自 StatusText");
        }

        [Fact]
        public void 页面上不许再摊开长说明()
        {
            var offenders = new List<string>();

            foreach (string file in TabFiles())
            {
                XDocument document = XDocument.Load(file);

                foreach (XElement block in document.Descendants().Where(e => e.Name.LocalName == "TextBlock"))
                {
                    string style = (string?)block.Attribute("Style") ?? string.Empty;
                    string text = (string?)block.Attribute("Text") ?? string.Empty;

                    // 只查"说明性文字"那一档：BannerTextStyle 是常驻警告横幅，按用户口径必须常驻。
                    if (!style.Contains("HintTextStyle", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // 绑定 / 动态拼出来的不算（它随机器变）。
                    if (text.StartsWith("{", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (text.Length > LongHintThreshold)
                    {
                        offenders.Add($"{Path.GetFileName(file)}：{text.Length} 字 —— {text.Substring(0, Math.Min(40, text.Length))}…");
                    }
                }
            }

            Assert.True(
                offenders.Count == 0,
                $"这些长说明还摊在页面上（应当搬进控件 BoxDetail，只留标题一行）：{Environment.NewLine}"
                + string.Join(Environment.NewLine, offenders));
        }

        [Fact]
        public void 每个展示框的文案都来自StatusText_常量真的在()
        {
            var boxes = 0;

            foreach (string file in TabFiles())
            {
                string xaml = File.ReadAllText(file);

                /*
                 * ⚠ 按"元素"匹配而不是按"两行固定写法"匹配：属性顺序 / 换行怎么排都算数。
                 * `[^>]*` 里天然含换行（否定字符类不排除 \n），所以整段属性跨行也能吃下。
                 */
                foreach (Match element in Regex.Matches(xaml, "<controls:BoxDetail\\b[^>]*>"))
                {
                    boxes++;

                    string block = element.Value;
                    Match header = Regex.Match(block, "Header=\"\\{x:Static models:StatusText\\.(?<n>\\w+)\\}\"");
                    Match detail = Regex.Match(block, "Detail=\"\\{x:Static models:StatusText\\.(?<n>\\w+)\\}\"");

                    Assert.True(
                        header.Success && detail.Success,
                        $"{Path.GetFileName(file)} 里有一个展示框的 Header / Detail 不是 StatusText 常量：{block}");

                    foreach (string name in new[] { header.Groups["n"].Value, detail.Groups["n"].Value })
                    {
                        FieldInfo? field = typeof(StatusText).GetField(name, BindingFlags.Public | BindingFlags.Static);

                        Assert.True(field != null, $"{Path.GetFileName(file)} 引用了 StatusText.{name}，但那个常量不存在");

                        string value = (string?)field!.GetRawConstantValue() ?? string.Empty;

                        Assert.False(string.IsNullOrWhiteSpace(value), $"StatusText.{name} 是空的");
                    }
                }
            }

            /*
             * ⚠ 门槛 = 7 而不是 9：做完这一刀时全库共 7 处（②③④⑤⑥ 各页都有），
             * 设置页原本那两处（启动时行为 / 扫描与识别）**随它们所在的组一起被删掉了**
             * （2026-10-14 最终设计：那两组移出界面、值钉成系统默认）⇒ 门槛跟着落到 7。
             * ⛔ 这里卡的是"这一刀的东西还在不在"，不是"必须有多少个"。
             */
            Assert.True(boxes >= 7, $"全库只找到 {boxes} 个展示框 —— 这一刀应当把各处长说明都收进去了");
        }

        [Fact]
        public void 搬走的正文一个字没丢()
        {
            // ⛔ 搬进展示框只改"平时看不看得见"，一个字都不许改 —— 逐条钉住原来那几句关键事实。
            Assert.Contains("三条同时成立才动源包", StatusText.BoxDetailSourceHandlingDetail, StringComparison.Ordinal);
            Assert.Contains("失败 / 部分完成 / 取消时源包一律原地不动", StatusText.BoxDetailSourceHandlingDetail, StringComparison.Ordinal);

            Assert.Contains("不是「跳过确认」", StatusText.BoxDetailManualCleanupDetail, StringComparison.Ordinal);
            Assert.Contains("绝不会因此降级成彻底删除", StatusText.BoxDetailManualCleanupDetail, StringComparison.Ordinal);

            Assert.Contains("512 MiB 余量", StatusText.BoxDetailSpaceGateDetail, StringComparison.Ordinal);
            Assert.Contains("取不到目标盘可用空间时「不拦」", StatusText.BoxDetailSpaceGateDetail, StringComparison.Ordinal);

            // 打包那四步一步都不许少（用户就是照这个理解打包在干什么的）。
            foreach (string step in new[] { "1）切分卷", "2）分卷就地落盘", "3）做外层容器", "4）校验 + 收尾" })
            {
                Assert.Contains(step, StatusText.BoxDetailPackingStepsDetail, StringComparison.Ordinal);
            }

            Assert.Contains("默认彻底删除", StatusText.BoxDetailPackingStepsDetail, StringComparison.Ordinal);
            Assert.Contains("单个包要用别的密码", StatusText.BoxDetailPerTaskPasswordDetail, StringComparison.Ordinal);
            Assert.Contains("这四条只「拒绝解压」", StatusText.BoxDetailBudgetDetail, StringComparison.Ordinal);

            // 面向用户的字符串里不许写 Markdown（强调一律用「」）。
            foreach (string text in new[]
                     {
                         StatusText.BoxDetailSourceHandlingHeader, StatusText.BoxDetailSourceHandlingDetail,
                         StatusText.BoxDetailManualCleanupHeader, StatusText.BoxDetailManualCleanupDetail,
                         StatusText.BoxDetailSpaceGateHeader, StatusText.BoxDetailSpaceGateDetail,
                         StatusText.BoxDetailPackingConfirmHeader, StatusText.BoxDetailPackingConfirmDetail,
                         StatusText.BoxDetailPackingStepsHeader, StatusText.BoxDetailPackingStepsDetail,
                         StatusText.BoxDetailPerTaskPasswordHeader, StatusText.BoxDetailPerTaskPasswordDetail,
                         StatusText.BoxDetailStartupBehaviorHeader, StatusText.BoxDetailStartupBehaviorDetail,
                         StatusText.BoxDetailScanAndIdentifyHeader, StatusText.BoxDetailScanAndIdentifyDetail,
                         StatusText.BoxDetailBudgetHeader, StatusText.BoxDetailBudgetDetail
                     })
            {
                Assert.DoesNotContain("**", text, StringComparison.Ordinal);
            }
        }

        private static string StripXmlComments(string xaml) =>
            Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

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

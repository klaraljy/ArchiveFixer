using System;
using System.IO;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// <b>③页「提醒」那一组：删掉停用的那格 + 剩下那格写清"打勾是开还是关"</b>
    /// （用户 2026-10-04 真机原话）。
    ///
    /// <para>用户原话：「你这个提醒里面第一个是什么意思，我现在打勾，是开还是关，我自己都看不懂，
    /// 要删除的是第二个你已经停止显示的，就是底层自动会做的」。</para>
    ///
    /// <para>这里钉四件事：① 那格"（已停用）"的开关**从界面上整格消失**（只有代码注释里还能提到它）；
    /// ② 剩下那一格的标签与说明**第一句就说清勾选方向**（勾上 = 开启提醒 / 取消 = 不提醒）——
    /// 复选框既能表示"开启某功能"也能表示"以后不再提醒"，只写一个名词短语用户猜不出来；
    /// ③ 说明里还要点明**提醒出现在哪**（用户 2026-10-05 真机追问：「那弹窗提示呢，还是你只有在
    /// 日志里面输出」—— 他勾上以后只看到日志，读成"这个勾没用"；事实是能弹框的只有"手动只解压 + 这一批
    /// 真有东西要提醒"这一档，一键处理的提醒并进那唯一的确认框，勾过「以后不再询问」就只写日志）；
    /// ④ ⛔ 底层那件事与它的**日志一个字都没动**（用户：「提示还是要有的」，而且要保留「无用物」这个说法）。</para>
    ///
    /// <para><b>红检</b>：把 <c>SettingsRemindBeforeExtractLabel</c> 改回
    /// 「解压前提醒可能的无用物与没有可用密码的包」（不带"（勾上 = 开启提醒）"）⇒
    /// <see cref="剩下那格_标签与说明第一句就写清勾选方向"/> 当场红；
    /// 把 <c>SettingsRemindBeforeExtractHint</c> 里"提醒出现在哪"那一段删掉 ⇒ 同一条用例红在 ⑥；
    /// 把那一格"（已停用）"的开关放回③页 ⇒
    /// <see cref="停用那格已经从界面上整格删掉_底层那件事与日志照旧"/> 当场红。</para>
    /// </summary>
    public class ReminderGroupTests
    {
        private const string CleanupTabXaml = @"src\ArchiveFixer\Views\Tabs\CleanupTab.xaml";

        [Fact]
        public void 停用那格已经从界面上整格删掉_底层那件事与日志照旧()
        {
            string xaml = StripXmlComments(ReadRepositoryFile(CleanupTabXaml));

            // ① 界面上再没有任何控件绑那一格（注释里提到设置项名字不算入口，所以先剥注释）。
            Assert.DoesNotContain("RemindJunkAfterImport", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("SettingsRemindJunkAfterImport", xaml, StringComparison.Ordinal);

            // ② ⛔ 设置属性本身留着（不动设置序列化，旧键安静忽略）。
            Assert.NotNull(typeof(AppSettings).GetProperty("RemindJunkAfterImport"));

            // ③ ⛔ 底层那件事照旧：两句日志仍在，而且**「无用物」这个说法一个字没改**
            //    （用户 2026-10-04：「不是让你换掉无用物……提示还是要有的」）。
            Assert.Contains("无用物", StatusText.ImportJunkRemovedLogFormat, StringComparison.Ordinal);
            Assert.Contains("无用物", StatusText.OneClickJunkRemovedLogFormat, StringComparison.Ordinal);
            Assert.Contains("移出", StatusText.ImportJunkRemovedLogFormat, StringComparison.Ordinal);
            Assert.Contains("源文件一个字节都没动", StatusText.ImportJunkRemovedLogFormat, StringComparison.Ordinal);
        }

        [Fact]
        public void 剩下那格_标签与说明第一句就写清勾选方向()
        {
            string label = StatusText.SettingsRemindBeforeExtractLabel;
            string hint = StatusText.SettingsRemindBeforeExtractHint;

            // ① 标签里明写"勾上 = 开启提醒"（用户点名读不懂的就是这一格）。
            Assert.Contains("勾上", label, StringComparison.Ordinal);
            Assert.Contains("开启提醒", label, StringComparison.Ordinal);

            // ② 说明第一句就把方向说死：勾上做什么、取消做什么 —— 两个方向都要有。
            Assert.Contains("勾上", hint, StringComparison.Ordinal);
            Assert.Contains("取消", hint, StringComparison.Ordinal);

            int firstSentenceEnd = hint.IndexOf('。');
            Assert.True(firstSentenceEnd > 0, "说明里得有个句号");

            string firstSentence = hint.Substring(0, firstSentenceEnd);

            Assert.Contains("勾上", firstSentence, StringComparison.Ordinal);
            Assert.Contains("取消", firstSentence, StringComparison.Ordinal);

            // ③ ⛔ 别再写成"开：…… 关：……"那种（用户就是被这个读糊涂的：界面上的勾与这两个字对不上）。
            //    ⚠ 判据是"**作为标签用的**开：/关："—— `开关：` 里那个「关：」不算（写这条时先踩过一次：
            //    朴素的 DoesNotContain("关：") 被"这个勾就是开关："里的三个字误伤，假红）。
            Assert.DoesNotContain("开：", hint, StringComparison.Ordinal);
            Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex("(?<!开)关："), hint);

            // ④ 该说清的照旧说清：提醒的两类内容、以及"不影响解压"。
            Assert.Contains("可用密码都没有", hint, StringComparison.Ordinal);
            Assert.Contains("不影响解压", hint, StringComparison.Ordinal);

            // ⑤ 面向用户的字符串里不许写 Markdown（强调一律用「」）。
            Assert.DoesNotContain("**", label, StringComparison.Ordinal);
            Assert.DoesNotContain("**", hint, StringComparison.Ordinal);

            // ⑥ 2026-10-05（用户拍板 A：只改说明、行为一个字不动）：说明里必须点明"提醒出现在哪"
            //    —— 他勾上以后只看到日志，于是追问「那弹窗提示呢，还是你只有在日志里面输出」。
            //    这一条与实现里的三条分支逐条对应（⛔ 改行为时先回来改说明）：
            //    ① 手动「只解压」⇒ 弹一个提醒框；② 一键处理 ⇒ 并进那唯一的确认框；
            //    ③ 勾过「以后不再询问」（落盘记住）⇒ 一个框都不弹，只在日志里。
            Assert.Contains("手动「只解压」", hint, StringComparison.Ordinal);
            Assert.Contains("一键处理", hint, StringComparison.Ordinal);
            Assert.Contains("以后不再询问", hint, StringComparison.Ordinal);
            Assert.Contains("只在日志里", hint, StringComparison.Ordinal);
        }

        /// <summary>去掉 XAML 里 <c>&lt;!-- … --&gt;</c> 那一段（注释不渲染，判"界面上还有没有它"要比剥完注释的内容）。</summary>
        private static string StripXmlComments(string xaml) =>
            System.Text.RegularExpressions.Regex.Replace(
                xaml,
                "<!--.*?-->",
                string.Empty,
                System.Text.RegularExpressions.RegexOptions.Singleline);

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

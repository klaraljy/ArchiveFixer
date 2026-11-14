using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// <b>「内容物保留关键词」那一栏搬家 + 改形态</b>（用户 2026-10-04 当天第二次改口径）。
    ///
    /// <para>用户原话：「你这个内容物保留关键词的输入框这么弄的这么大，你应该弄的像密码一样，
    /// 而且是放在解压方式里面」。</para>
    ///
    /// <para>这里钉四件事：① 形态 = 一条条加（单行输入 + 「添加」+ 每条能「移除」），空白输入不许点「添加」；
    /// ② 加减两步都当场归一化（Trim / 去重 / 大小写不敏感），并且**界面跟着刷新**（列表 + 那句摘要）；
    /// ③ 界面落点**只有②解压方式页一处**（⑥设置里那一栏整块搬走，⛔ 两处入口 = 两个控件改同一个值）；
    /// ④ 真值仍是设置里那一个字符串数组 —— 栏目搬了家，判据与落盘的键一个字符都没变。</para>
    ///
    /// <para><b>红检</b>：把⑥设置里那一栏再放回去（或把②页那一段删掉）⇒
    /// <see cref="界面落点_只在二页那一处_六设置里已经搬空"/> 当场红。</para>
    /// </summary>
    public class ContentKeepKeywordEditorTests
    {
        private const string ExtractionTabXaml = @"src\ArchiveFixer\Views\Tabs\ExtractionTab.xaml";
        private const string SettingsTabXaml = @"src\ArchiveFixer\Views\Tabs\SettingsTab.xaml";

        // ================================================================ ① 形态：一条条加

        [Fact]
        public void 加一个词_进设置也进列表_输入框清空()
        {
            SettingsViewModel editor = NewEditor(out AppSettings settings);

            editor.NewContentKeepKeyword = " 小明 ";
            Assert.True(editor.CanAddContentKeepKeyword, "输入框里有内容 ⇒ 「添加」可点");

            editor.AddContentKeepKeywordCommand.Execute(null);

            // ① 真值进了设置（落盘的键没变）——顺带证明 Trim 生效。
            Assert.Equal(new[] { "小明" }, settings.ContentKeepKeywords);

            // ② 界面跟着刷新（§9.5：值对而界面不刷新 = 用户读成"没生效"）。
            Assert.Equal(new[] { "小明" }, editor.ContentKeepKeywordItems.ToArray());
            Assert.Contains("1", editor.ContentKeepKeywordsSummary, StringComparison.Ordinal);

            // ③ 输入框清空，好接着加下一个；空白输入 ⇒ 按钮自己禁用。
            Assert.Equal(string.Empty, editor.NewContentKeepKeyword);
            Assert.False(editor.CanAddContentKeepKeyword);
        }

        [Fact]
        public void 空白输入直接执行添加_列表一个字节都不变()
        {
            SettingsViewModel editor = NewEditor(out AppSettings settings);

            editor.NewContentKeepKeyword = "   ";
            editor.AddContentKeepKeywordCommand.Execute(null);

            Assert.Empty(settings.ContentKeepKeywords!);
            Assert.Empty(editor.ContentKeepKeywordItems);
            Assert.Equal(StatusText.ContentKeepKeywordsEmptySummary, editor.ContentKeepKeywordsSummary);
        }

        [Fact]
        public void 同一个词加两次_只有一条_且大小写不敏感()
        {
            SettingsViewModel editor = NewEditor(out AppSettings settings);

            foreach (string typed in new[] { "小明", " 小明 ", "小明" })
            {
                editor.NewContentKeepKeyword = typed;
                editor.AddContentKeepKeywordCommand.Execute(null);
            }

            editor.NewContentKeepKeyword = "ABC";
            editor.AddContentKeepKeywordCommand.Execute(null);
            editor.NewContentKeepKeyword = "abc";
            editor.AddContentKeepKeywordCommand.Execute(null);

            Assert.Equal(new[] { "小明", "ABC" }, settings.ContentKeepKeywords);
            Assert.Equal(new[] { "小明", "ABC" }, editor.ContentKeepKeywordItems.ToArray());
        }

        [Fact]
        public void 移除只去掉那一个_顺序照旧()
        {
            SettingsViewModel editor = NewEditor(out AppSettings settings);

            foreach (string word in new[] { "小明", "小红", "小刚" })
            {
                editor.NewContentKeepKeyword = word;
                editor.AddContentKeepKeywordCommand.Execute(null);
            }

            editor.RemoveContentKeepKeywordCommand.Execute("小红");

            Assert.Equal(new[] { "小明", "小刚" }, settings.ContentKeepKeywords);
            Assert.Equal(new[] { "小明", "小刚" }, editor.ContentKeepKeywordItems.ToArray());

            // 大小写不敏感地移除最后一条 ⇒ 空列表回到"这个功能不生效"那句。
            editor.RemoveContentKeepKeywordCommand.Execute("小刚");
            editor.RemoveContentKeepKeywordCommand.Execute("小明");

            Assert.Empty(settings.ContentKeepKeywords!);
            Assert.Empty(editor.ContentKeepKeywordItems);
            Assert.Equal(StatusText.ContentKeepKeywordsEmptySummary, editor.ContentKeepKeywordsSummary);
        }

        [Fact]
        public void 恢复默认会把列表一起清空()
        {
            SettingsViewModel editor = NewEditor(out AppSettings settings);

            editor.NewContentKeepKeyword = "小明";
            editor.AddContentKeepKeywordCommand.Execute(null);
            Assert.NotEmpty(editor.ContentKeepKeywordItems);

            editor.ResetDefaultCommand.Execute(null);

            Assert.Empty(editor.ContentKeepKeywordItems);
            Assert.Equal(StatusText.ContentKeepKeywordsEmptySummary, editor.ContentKeepKeywordsSummary);

            // 「恢复默认」换的是**另一份**设置对象（CreateDefault），所以看的是编辑器现在挂的那一份
            // —— 原来那份对象按设计原地不动（别把这条读成"没清掉"）。
            Assert.NotSame(settings, editor.Settings);
            Assert.Empty(editor.Settings.ContentKeepKeywords!);
        }

        // ================================================================ ② 判据照旧接在后面（搬家不改判据）

        [Fact]
        public void 加进去的词_判据当场就认()
        {
            SettingsViewModel editor = NewEditor(out AppSettings settings);

            editor.NewContentKeepKeyword = "小明";
            editor.AddContentKeepKeywordCommand.Execute(null);

            ContentKeepRules rules = ContentKeepRules.FromSettings(settings);

            Assert.True(rules.ShouldKeep(@"D:\某处\内容物\小明.zip"));
            Assert.True(rules.ShouldKeep(@"D:\某处\内容物\小明和小红.7z"));
            Assert.False(rules.ShouldKeep(@"D:\某处\内容物\小红.zip"));

            // 移除之后判据立刻不认了（面板与判据同一份真值，没有第二份缓存）。
            editor.RemoveContentKeepKeywordCommand.Execute("小明");

            Assert.False(ContentKeepRules.FromSettings(settings).ShouldKeep(@"D:\某处\内容物\小明.zip"));
        }

        // ================================================================ ③ 界面落点：只有②页一处

        [Fact]
        public void 界面落点_只在二页那一处_六设置里已经搬空()
        {
            string extractionTab = ReadRepositoryFile(ExtractionTabXaml);
            string settingsTab = ReadRepositoryFile(SettingsTabXaml);

            // ① ②页：单行输入 + 「添加」+ 列表 + 每条「移除」，全都在。
            Assert.Contains("SettingsEditor.NewContentKeepKeyword", extractionTab, StringComparison.Ordinal);
            Assert.Contains("SettingsEditor.AddContentKeepKeywordCommand", extractionTab, StringComparison.Ordinal);
            Assert.Contains("SettingsEditor.RemoveContentKeepKeywordCommand", extractionTab, StringComparison.Ordinal);
            Assert.Contains("SettingsEditor.ContentKeepKeywordItems", extractionTab, StringComparison.Ordinal);
            Assert.Contains("SettingsEditor.ContentKeepKeywordsSummary", extractionTab, StringComparison.Ordinal);
            Assert.Contains("SettingsEditor.CanAddContentKeepKeyword", extractionTab, StringComparison.Ordinal);

            // ② 回车也能加（手不离键盘）——不是只有点按钮那一条路。
            Assert.Contains("Key=\"Enter\"", extractionTab, StringComparison.Ordinal);

            // ③ 那句"怎么用"的说明也搬过来了（唯一来源 = StatusText，⛔ 不在 XAML 里手写一份）。
            Assert.Contains("StatusText.ContentKeepKeywordsIntro", extractionTab, StringComparison.Ordinal);
            Assert.Contains("StatusText.ContentKeepKeywordsRulesHint", extractionTab, StringComparison.Ordinal);

            // ④ ⛔ ⑥设置里那一栏整块搬空：这一页**没有任何控件**再绑它
            //    （两处入口 = 两个控件改同一个值，用户会看到"改了没反应"）。
            //    判据 = 把 XML 注释整段去掉之后，这一页连"ContentKeepKeyword"这个词都不该出现 ——
            //    ⛔ 别只比 "SettingsEditor.ContentKeepKeyword"：那串比不出 NewContentKeepKeyword /
            //    AddContentKeepKeywordCommand 这些名字（写这条时就踩过一次，红检反而没红）。
            Assert.DoesNotContain("ContentKeepKeyword", StripXmlComments(settingsTab), StringComparison.Ordinal);

            // ⑤ 那个又高又大的多行框不许回来（用户点名嫌的就是它）。
            Assert.DoesNotContain("ContentKeepKeywordsText", extractionTab, StringComparison.Ordinal);
            Assert.DoesNotContain("ContentKeepKeywordsText", settingsTab, StringComparison.Ordinal);
        }

        // ================================================================ 夹具

        /// <summary>
        /// 一个挂在**同一份**设置上的编辑器（②③④⑥四页共用的就是这一个类；
        /// 生产里主视图模型也是 <c>AttachSharedSettings</c> 共享同一份，不用构造函数那个克隆件 ——
        /// 不然断言看的是一份、界面上动的是另一份）。
        /// </summary>
        private static SettingsViewModel NewEditor(out AppSettings settings)
        {
            settings = AppSettings.CreateDefault();

            var editor = new SettingsViewModel(settings, new SettingsService());
            editor.AttachSharedSettings(settings);

            return editor;
        }

        /// <summary>
        /// 去掉 XAML 里 <c>&lt;!-- … --&gt;</c> 那一段（注释是给人看的、用户界面上不渲染，
        /// 所以"这一页还有没有那个控件"要比的是**去掉注释之后**剩下的东西）。
        /// </summary>
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

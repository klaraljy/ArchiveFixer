using System;
using System.IO;
using System.Linq;
using ArchiveFixer.Models;
using ArchiveFixer.Views;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 说明页的结构守门：三种处理方式都要有完整例子，9 个页签都要出现，
    /// 诊断包入口和常见词条不能被下一次改版悄悄丢掉。
    /// </summary>
    public sealed class HelpContentStructureTests
    {
        [Fact]
        public void 三种处理方式各有完整图文例子()
        {
            Assert.Equal(3, HelpContent.ModeExamples.Count);

            Assert.Contains(HelpContent.ModeExamples, example => example.Title.Contains("普通", StringComparison.Ordinal));
            Assert.Contains(HelpContent.ModeExamples, example => example.Title.Contains("特定", StringComparison.Ordinal));
            Assert.Contains(HelpContent.ModeExamples, example => example.Title.Contains("空间不足", StringComparison.Ordinal));

            foreach (HelpModeExample example in HelpContent.ModeExamples)
            {
                Assert.True(example.Steps.Count >= 6, example.Title + " 至少要有 6 步");
                Assert.False(string.IsNullOrWhiteSpace(example.OutputTree), example.Title + " 要有落点/结果图");
                Assert.False(string.IsNullOrWhiteSpace(example.Boundary), example.Title + " 要有边界说明");
                Assert.False(string.IsNullOrWhiteSpace(example.Summary), example.Title + " 要有一句话总结");
                Assert.NotEmpty(example.Notes);
            }
        }

        [Fact]
        public void 功能地图覆盖九个页签()
        {
            Assert.Equal(9, HelpContent.FeatureMap.Count);
            Assert.Equal(9, HelpContent.TabGuides.Count);

            string[] tabs =
            {
                "① 任务", "② 解压方式", "③ 清理方式", "④ 密码", "⑤ 打包",
                "⑥ 提醒", "⑦ 引擎", "⑧ 设置", "⑨ 说明"
            };

            foreach (string tab in tabs)
            {
                Assert.Contains(HelpContent.FeatureMap, item => item.Term == tab);
                Assert.Contains(HelpContent.TabGuides, item => item.Term == tab + "页");
            }
        }

        [Fact]
        public void 诊断包与关键术语必须可查()
        {
            Assert.Contains(
                HelpContent.DiagnosticsSteps,
                step => step.Text.Contains("导出诊断包", StringComparison.Ordinal));
            Assert.Contains(HelpContent.Faq, item => item.Term.Contains("诊断包", StringComparison.Ordinal));
            Assert.Contains(HelpContent.Glossary, item => item.Term == "特定解压");
            Assert.Contains(HelpContent.Glossary, item => item.Term == "空间不足");
            Assert.Contains(HelpContent.Glossary, item => item.Term == "第一大步");
            Assert.Contains(HelpContent.Glossary, item => item.Term == "第二大步");
        }

        [Fact]
        public void 说明正文不再出现已下线的旧档位说法()
        {
            string all = string.Join(
                "\n",
                HelpContent.QuickStartSteps.Select(step => step.Title + step.Text + step.Path)
                    .Concat(HelpContent.ModeExamples.SelectMany(example => example.Steps)
                        .Select(step => step.Title + step.Text + step.Path))
                    .Concat(HelpContent.FeatureMap.Select(item => item.Term + item.Explanation))
                    .Concat(HelpContent.TabGuides.Select(item => item.Term + item.Explanation))
                    .Concat(HelpContent.Scenarios.Select(item => item.Term + item.Explanation))
                    .Concat(HelpContent.Glossary.Select(item => item.Term + item.Explanation))
                    .Concat(HelpContent.Faq.Select(item => item.Term + item.Explanation)));

            Assert.DoesNotContain("单链自动展开", all, StringComparison.Ordinal);
            Assert.DoesNotContain("展开所有分支", all, StringComparison.Ordinal);
            Assert.DoesNotContain("测试完整性", all, StringComparison.Ordinal);
            Assert.DoesNotContain("自动打开目录", all, StringComparison.Ordinal);
            Assert.DoesNotContain("⑥ 设置页", all, StringComparison.Ordinal);
            Assert.DoesNotContain("不删原包", all, StringComparison.Ordinal);
        }

        [Fact]
        public void 确认框和设置提示不再引用旧档位()
        {
            Assert.DoesNotContain("单链自动展开", StatusText.OneClickConfirmNestedCaveat, StringComparison.Ordinal);
            Assert.DoesNotContain("展开所有分支", StatusText.OneClickConfirmNestedCaveat, StringComparison.Ordinal);
            Assert.Contains("层数", StatusText.OneClickConfirmNestedCaveat, StringComparison.Ordinal);
            Assert.Contains("⑧设置", StatusText.SecurityCapHint, StringComparison.Ordinal);
            Assert.Contains("③「清理方式」", StatusText.WorkspaceKeptOnFailureFormat, StringComparison.Ordinal);
        }

        [Fact]
        public void 复制全部说明包含三种例子和诊断包()
        {
            string text = HelpWindow.BuildPlainText();

            Assert.Contains("普通档位", text, StringComparison.Ordinal);
            Assert.Contains("特定解压", text, StringComparison.Ordinal);
            Assert.Contains("空间不足", text, StringComparison.Ordinal);
            Assert.Contains("导出诊断包", text, StringComparison.Ordinal);
            Assert.Contains("术语表", text, StringComparison.Ordinal);
        }

        [Fact]
        public void 说明面板真的渲染新章节()
        {
            string xaml = File.ReadAllText(Path.Combine(
                RepositoryRoot(),
                "src", "ArchiveFixer", "Views", "HelpBodyPanel.xaml"));

            Assert.Contains("HelpContent.QuickStartSteps", xaml, StringComparison.Ordinal);
            Assert.Contains("HelpContent.ModeExamples", xaml, StringComparison.Ordinal);
            Assert.Contains("HelpContent.FeatureMap", xaml, StringComparison.Ordinal);
            Assert.Contains("HelpContent.Glossary", xaml, StringComparison.Ordinal);
            Assert.Contains("HelpContent.DiagnosticsSteps", xaml, StringComparison.Ordinal);
            Assert.Contains("HelpContent.Faq", xaml, StringComparison.Ordinal);
        }

        private static string RepositoryRoot()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return AppContext.BaseDirectory;
        }
    }
}

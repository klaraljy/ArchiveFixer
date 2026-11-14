using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;

namespace ArchiveFixer.Views
{
    /// <summary>
    /// 「说明」窗（用户 2026-09-27 要求：选项卡那一排最右端一颗「说明」，
    /// 里面有快速开始、三种完整例子、功能地图、专有名词和排查 FAQ）。
    ///
    /// <para>文字全部来自 <see cref="HelpContent"/>（单一出处）；这个窗口自己不写任何一句中文，
    /// 也不读任何外部文件 —— 帮助菜单里的「使用说明」依赖 `docs\使用说明.md`（分发包里可能没有），
    /// 这一份必须永远能打开。</para>
    /// </summary>
    public partial class HelpWindow : Window
    {
        public HelpWindow()
        {
            InitializeComponent();

            // 模态子窗：不许最小化（A2），但保留拖边框改大小。
            WindowMinimizePolicy.Apply(this);
        }

        /// <summary>把整份说明拼成一段纯文本 —— 「复制全部说明」用。</summary>
        internal static string BuildPlainText()
        {
            var builder = new StringBuilder();

            builder.AppendLine(HelpContent.Tagline);
            builder.AppendLine();

            AppendSection(builder, HelpContent.QuickStartHeading);
            AppendSteps(builder, HelpContent.QuickStartSteps);

            AppendSection(builder, HelpContent.ModeExamplesHeading);
            builder.AppendLine(HelpContent.ModeExamplesIntro);
            builder.AppendLine();

            foreach (HelpModeExample example in HelpContent.ModeExamples)
            {
                builder.AppendLine("【" + example.Title + "】" + example.Badge);
                builder.AppendLine(example.Summary);
                builder.AppendLine();
                AppendSteps(builder, example.Steps);

                if (!string.IsNullOrWhiteSpace(example.OutputTree))
                {
                    builder.AppendLine("落点/结果：");
                    builder.AppendLine(example.OutputTree);
                    builder.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(example.Boundary))
                {
                    builder.AppendLine("边界：" + example.Boundary);
                    builder.AppendLine();
                }

                foreach (string note in example.Notes)
                {
                    builder.AppendLine("· " + note);
                }

                builder.AppendLine();
            }

            AppendSection(builder, HelpContent.FeatureMapHeading);
            AppendTerms(builder, HelpContent.FeatureMap);

            AppendSection(builder, HelpContent.TabGuidesHeading);
            AppendTerms(builder, HelpContent.TabGuides);

            AppendSection(builder, HelpContent.ScenariosHeading);
            AppendTerms(builder, HelpContent.Scenarios);

            AppendSection(builder, HelpContent.GlossaryHeading);
            AppendTerms(builder, HelpContent.Glossary);

            AppendSection(builder, HelpContent.DiagnosticsHeading);
            AppendSteps(builder, HelpContent.DiagnosticsSteps);
            builder.AppendLine(HelpContent.DiagnosticsPrivacy);
            builder.AppendLine();

            AppendSection(builder, HelpContent.LimitsHeading);
            AppendTerms(builder, HelpContent.Limits);

            AppendSection(builder, HelpContent.FaqHeading);
            AppendTerms(builder, HelpContent.Faq);

            AppendSection(builder, HelpContent.VersionHeading);
            builder.AppendLine(HelpContent.VersionText);

            return builder.ToString().TrimEnd() + Environment.NewLine;
        }

        private static void AppendSection(StringBuilder builder, string title)
        {
            builder.AppendLine("———— " + title + " ————");
            builder.AppendLine();
        }

        private static void AppendTerms(StringBuilder builder, IReadOnlyList<HelpTerm> terms)
        {
            foreach (HelpTerm term in terms)
            {
                builder.AppendLine(term.Term + "：" + term.Explanation);
                builder.AppendLine();
            }
        }

        private static void AppendSteps(StringBuilder builder, IReadOnlyList<HelpExampleStep> steps)
        {
            foreach (HelpExampleStep step in steps)
            {
                builder.AppendLine(step.Number + ". " + step.Title + "：" + step.Text);

                if (!string.IsNullOrWhiteSpace(step.Path))
                {
                    builder.AppendLine("   " + step.Path);
                }

                builder.AppendLine();
            }
        }

        private void CopyAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(BuildPlainText());
            }
            catch
            {
                // 剪贴板被别的进程占用是常事：复制失败不该让窗口崩掉，也不值得弹错误框。
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}

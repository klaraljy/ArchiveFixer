using System;
using System.Text;
using System.Windows;
using ArchiveFixer.Models;

namespace ArchiveFixer.Views
{
    /// <summary>
    /// 「说明」窗（用户 2026-09-27 要求：选项卡那一排、⑥设置右边一颗「说明」，
    /// 里面有工具介绍 + 专有名词解释）。
    ///
    /// <para>文字全部来自 <see cref="HelpContent"/>（单一出处）；这个窗口自己不写任何一句中文，
    /// 也不读任何外部文件 —— 帮助菜单里的「使用说明」依赖 `docs\使用说明.md`（分发包里可能没有），
    /// 这一份必须**永远**能打开。</para>
    /// </summary>
    public partial class HelpWindow : Window
    {
        public HelpWindow()
        {
            InitializeComponent();

            // 模态子窗：不许最小化（A2），但保留拖边框改大小。
            WindowMinimizePolicy.Apply(this);
        }

        /// <summary>把整份说明（介绍 + 功能详解 + 术语表）拼成一段纯文本 —— 「复制全部说明」用。</summary>
        internal static string BuildPlainText()
        {
            var builder = new StringBuilder();

            builder.AppendLine(HelpContent.Tagline);
            builder.AppendLine();

            foreach (string paragraph in HelpContent.Introduction)
            {
                builder.AppendLine(paragraph);
                builder.AppendLine();
            }

            builder.AppendLine("———— " + HelpContent.FeaturesHeading + " ————");
            builder.AppendLine();

            foreach (HelpTerm feature in HelpContent.Features)
            {
                builder.AppendLine(feature.Term + "：" + feature.Explanation);
                builder.AppendLine();
            }

            builder.AppendLine("———— " + HelpContent.GlossaryHeading + " ————");
            builder.AppendLine();

            foreach (HelpTerm term in HelpContent.Glossary)
            {
                builder.AppendLine(term.Term + "：" + term.Explanation);
                builder.AppendLine();
            }

            return builder.ToString().TrimEnd() + Environment.NewLine;
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

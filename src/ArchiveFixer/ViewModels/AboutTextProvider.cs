using ArchiveFixer.Models;
using System.Collections.Generic;
using System.Text;

namespace ArchiveFixer.ViewModels
{
    /// <summary>「关于」文本的唯一格式出口；界面只绑定。</summary>
    internal static class AboutTextProvider
    {
        internal static string Build(IReadOnlyList<EngineOptionItem> engines)
        {
            var builder = new StringBuilder();

            string version = typeof(AboutTextProvider).Assembly.GetName().Version?.ToString() ?? "（版本读不到）";

            builder.AppendLine("ArchiveFixer " + version);
            builder.AppendLine();
            builder.AppendLine("遇到问题请反馈给作者：发邮件 " + StatusText.FeedbackEmail);
            builder.AppendLine("最新版本下载：" + StatusText.ReleaseDownloadUrl);
            builder.AppendLine();

            if (engines.Count == 0)
            {
                builder.AppendLine("（没有检测到任何引擎）");
            }
            else
            {
                foreach (EngineOptionItem engine in engines)
                {
                    builder.AppendLine(engine.HeaderText);
                }
            }

            builder.AppendLine();
            builder.Append(StatusText.AboutLicenseText);

            return builder.ToString();
        }
    }
}

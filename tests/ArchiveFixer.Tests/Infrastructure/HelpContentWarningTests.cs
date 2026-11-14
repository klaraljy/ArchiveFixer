using System;
using System.IO;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class HelpContentWarningTests
    {
        [Fact]
        public void 说明页深色定位语下面有红色递归解压风险提示()
        {
            string warning = HelpContent.RecursionRiskWarning;

            Assert.Contains("递归对所有能够解压的压缩包全部解压", warning, StringComparison.Ordinal);
            Assert.Contains("内容物保留关键词", warning, StringComparison.Ordinal);
            Assert.Contains("正常的.exe和.apk文件我们是不会碰的", warning, StringComparison.Ordinal);

            string xaml = File.ReadAllText(Path.Combine(
                RepositoryRoot(),
                "src", "ArchiveFixer", "Views", "HelpBodyPanel.xaml"));

            Assert.Contains("{x:Static models:HelpContent.RecursionRiskWarning}", xaml, StringComparison.Ordinal);
            Assert.Contains("Foreground=\"{StaticResource DangerTextBrush}\"", xaml, StringComparison.Ordinal);
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

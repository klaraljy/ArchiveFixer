using System;
using System.IO;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 2026-11-04 界面精简：手工清理入口从 ③ 清理页移到「手动操作」窗口。
    /// 这里只钉“入口在哪”，命令本身的删除语义由 CleanupSelectionScopeTests 继续钉。
    /// </summary>
    public sealed class ManualCleanupEntryTests
    {
        [Fact]
        public void 清理页不再有手工清理命令_手动窗口三个入口都在()
        {
            string cleanup = ReadRepositoryFile(Path.Combine("src", "ArchiveFixer", "Views", "Tabs", "CleanupTab.xaml"));
            string manual = ReadRepositoryFile(Path.Combine("src", "ArchiveFixer", "Views", "ManualOperationWindow.xaml"));

            foreach (string command in new[]
                     {
                         "CleanProcessArtifactsCommand",
                         "CleanAllProcessArtifactsCommand",
                         "CleanEmptyFoldersCommand"
                     })
            {
                Assert.DoesNotContain(command, cleanup, StringComparison.Ordinal);
                Assert.Contains(command, manual, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("Header=\"手工清理", cleanup, StringComparison.Ordinal);
            Assert.Contains("删除过程物", manual, StringComparison.Ordinal);
            Assert.Contains("本目录全部过程物", manual, StringComparison.Ordinal);
            Assert.Contains("清理空文件夹", manual, StringComparison.Ordinal);
        }

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

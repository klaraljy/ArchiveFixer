using ArchiveFixer.Models;
using ArchiveFixer.Views;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class TaskDetailSanitizationTests
    {
        [Fact]
        public void 任务详情的复制文本不含明文密码()
        {
            var task = new ArchiveTask(@"C:\samples\sample.7z")
            {
                ErrorMessage = "ERROR: -pSuperSecret cannot open archive"
            };

            string detail = TaskDetailWindow.BuildDetailText(task);

            Assert.DoesNotContain("SuperSecret", detail, System.StringComparison.Ordinal);
            Assert.Contains("错误信息：", detail, System.StringComparison.Ordinal);
        }
    }
}

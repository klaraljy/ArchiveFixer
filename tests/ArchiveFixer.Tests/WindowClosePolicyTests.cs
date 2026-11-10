using ArchiveFixer.Views;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class WindowClosePolicyTests
    {
        [Fact]
        public void 任务列表空点X直接退出_列表非空点X收托盘()
        {
            Assert.False(WindowClosePolicy.ShouldHideToTray(taskCount: 0));
            Assert.True(WindowClosePolicy.ShouldHideToTray(taskCount: 1));
            Assert.True(WindowClosePolicy.ShouldHideToTray(taskCount: 17));
        }

        [Fact]
        public void 生产宿主窗口关闭后必须结束进程_不依赖MainWindow属性()
        {
            Assert.True(WindowClosePolicy.ShouldShutdownAfterClose(
                allowClose: true,
                isProductionApplication: true));

            // 测试探针 / 非生产宿主（普通 Application）不能把 testhost 关掉。
            Assert.False(WindowClosePolicy.ShouldShutdownAfterClose(
                allowClose: true,
                isProductionApplication: false));

            Assert.False(WindowClosePolicy.ShouldShutdownAfterClose(
                allowClose: false,
                isProductionApplication: true));
        }
    }
}

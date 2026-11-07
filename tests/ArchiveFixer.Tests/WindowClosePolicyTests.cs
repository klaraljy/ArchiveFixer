using ArchiveFixer.Views;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class WindowClosePolicyTests
    {
        [Fact]
        public void 空闲点X直接退出_有任务点X收托盘()
        {
            Assert.False(WindowClosePolicy.ShouldHideToTray(isBusy: false));
            Assert.True(WindowClosePolicy.ShouldHideToTray(isBusy: true));
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

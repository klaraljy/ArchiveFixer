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
    }
}

using System;
using System.Threading;
using ArchiveFixer.Experimental.AutoConcurrency;
using Xunit;

namespace ArchiveFixer.Tests
{
    [Trait("Category", "ExperimentalAutoConcurrency")]
    /// <summary>
    /// 系统压力探针的最小事实：Windows API 能返回合法 CPU/内存值，
    /// 不是永远 0；真正为 0 时必须是“本轮确实空闲”，不能当作默认失败。
    /// </summary>
    public sealed class SystemResourceProbeTests
    {
        [Fact]
        public void 两次采样都返回合法CPU与可用内存()
        {
            ConcurrencyResourceSnapshot first = SystemResourceProbe.Snapshot();
            Thread.Sleep(250);
            ConcurrencyResourceSnapshot second = SystemResourceProbe.Snapshot();

            Assert.InRange(first.CpuBusyPercent, 0, 100);
            Assert.InRange(second.CpuBusyPercent, 0, 100);
            Assert.True(first.AvailablePhysicalBytes >= 0);
            Assert.True(second.AvailablePhysicalBytes > 0, "可用物理内存采样不应为 0");
            Assert.True(second.AvailablePhysicalBytes <= first.AvailablePhysicalBytes * 2 + 64L * 1024 * 1024);
        }
    }
}

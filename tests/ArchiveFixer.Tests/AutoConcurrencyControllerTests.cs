using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 自适应并发控制器（用户 2026-11-01：并发数不再给用户选，由程序按设备与吞吐自己判断）。
    /// 纯逻辑：假时钟/假样本，不读盘。
    /// </summary>
    public sealed class AutoConcurrencyControllerTests
    {
        private const long EightGb = 8L * 1024 * 1024 * 1024;
        private const long SixteenGb = 16L * 1024 * 1024 * 1024;

        [Fact]
        public void SSD八核_初始4_硬顶6()
        {
            var controller = new AutoConcurrencyController(
                StorageDeviceKind.Ssd,
                sameVolume: false,
                logicalCores: 8,
                totalMemoryBytes: SixteenGb);

            Assert.Equal(4, controller.InitialLimit);
            Assert.Equal(6, controller.HardLimit);
        }

        [Fact]
        public void HDD_初始2_硬顶3()
        {
            var controller = new AutoConcurrencyController(
                StorageDeviceKind.Hdd,
                sameVolume: true,
                logicalCores: 16,
                totalMemoryBytes: SixteenGb);

            Assert.Equal(2, controller.InitialLimit);
            Assert.Equal(3, controller.HardLimit);
        }

        [Fact]
        public void 未知介质_按HDD保守()
        {
            var controller = new AutoConcurrencyController(
                StorageDeviceKind.Unknown,
                sameVolume: false,
                logicalCores: 32,
                totalMemoryBytes: SixteenGb);

            Assert.Equal(2, controller.InitialLimit);
            Assert.Equal(2, controller.HardLimit);
        }

        [Fact]
        public void 内存不足8G_SSD初值降一档()
        {
            var controller = new AutoConcurrencyController(
                StorageDeviceKind.Ssd,
                sameVolume: false,
                logicalCores: 16,
                totalMemoryBytes: EightGb - 1);

            Assert.Equal(3, controller.InitialLimit);
        }

        [Fact]
        public void 吞吐持续上升_加到硬顶()
        {
            var controller = new AutoConcurrencyController(
                StorageDeviceKind.Hdd,
                sameVolume: false,
                logicalCores: 8,
                totalMemoryBytes: SixteenGb);

            double throughput = 100;

            for (int i = 0; i < 12; i++)
            {
                controller.Observe(throughput, activeTasks: 2, uiStalls: 0, diskSpaceLow: false);
                throughput *= 1.10;
            }

            Assert.Equal(3, controller.CurrentLimit);
        }

        [Fact]
        public void 加档后吞吐没有提升_回退并停在拐点()
        {
            var controller = new AutoConcurrencyController(
                StorageDeviceKind.Hdd,
                sameVolume: false,
                logicalCores: 8,
                totalMemoryBytes: SixteenGb);

            controller.Observe(100, 2, 0, false);
            controller.Observe(120, 2, 0, false); // 加档
            Assert.Equal(3, controller.CurrentLimit);

            controller.Observe(120, 3, 0, false); // 冷却窗口
            int after = controller.Observe(120, 3, 0, false); // 无提升 ⇒ 回退

            Assert.Equal(2, after);

            // 停在拐点：后续吞吐再涨也不越过 2（有效硬顶已被压下）
            for (int i = 0; i < 5; i++)
            {
                controller.Observe(1000, 3, 0, false);
            }

            Assert.Equal(2, controller.CurrentLimit);
        }

        [Fact]
        public void UI线程有停顿_立即降一档()
        {
            var controller = new AutoConcurrencyController(
                StorageDeviceKind.Ssd,
                sameVolume: false,
                logicalCores: 12,
                totalMemoryBytes: SixteenGb);

            controller.Observe(200, 4, 0, false);
            controller.Observe(200, 4, 0, false);

            int after = controller.Observe(200, 4, uiStalls: 1, diskSpaceLow: false);

            Assert.True(after < 4);
        }

        [Fact]
        public void 空间逼近保留区_降到1()
        {
            var controller = new AutoConcurrencyController(
                StorageDeviceKind.Ssd,
                sameVolume: false,
                logicalCores: 12,
                totalMemoryBytes: SixteenGb);

            int after = controller.Observe(500, 4, 0, diskSpaceLow: true);

            Assert.Equal(1, after);
        }

        [Fact]
        public void 连续两个窗口无吞吐_降一档()
        {
            var controller = new AutoConcurrencyController(
                StorageDeviceKind.Ssd,
                sameVolume: false,
                logicalCores: 12,
                totalMemoryBytes: SixteenGb);

            controller.Observe(200, 4, 0, false);
            controller.Observe(0, 4, 0, false);
            int after = controller.Observe(0, 4, 0, false);

            Assert.True(after < 4);
        }
    }
}

using System;
using System.IO;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 延迟梯度控制器（Envoy/Netflix 同族）：设备只定起点，方向由归一化延迟决定。
    /// </summary>
    public sealed class AdaptiveConcurrencyControllerTests : IDisposable
    {
        private readonly string _root;

        public AdaptiveConcurrencyControllerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerAdaptiveConcurrency", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        private static ConcurrencyWindowSample Sample(
            double latency,
            double throughput = 100,
            int active = 4,
            int stalls = 0,
            bool spaceLow = false,
            int failures = 0) =>
            new(latency, throughput, active, stalls, spaceLow, failures);

        [Fact]
        public void 延迟没有恶化_按梯度逐步加档()
        {
            var controller = new AdaptiveConcurrencyController(initialLimit: 4);

            controller.Observe(Sample(1.0));
            int after = controller.Observe(Sample(1.0));

            Assert.True(after > 4, $"延迟平稳时应加档，实际 {after}");
            Assert.True(controller.CurrentLimit <= controller.MaxLimit);
        }

        [Fact]
        public void 延迟相对基线明显变差_降档()
        {
            var controller = new AdaptiveConcurrencyController(initialLimit: 6);

            controller.Observe(Sample(1.0));      // 基线
            int after = controller.Observe(Sample(3.0)); // 3 倍基线

            Assert.True(after < 6, $"延迟恶化时应降档，实际 {after}");
        }

        [Fact]
        public void UI线程停顿_立即降一档()
        {
            var controller = new AdaptiveConcurrencyController(initialLimit: 4);
            controller.Observe(Sample(1.0));

            int before = controller.CurrentLimit;
            int after = controller.Observe(Sample(1.0, stalls: 1));

            Assert.True(after < before);
        }

        [Fact]
        public void 空间见底_直接降到1()
        {
            var controller = new AdaptiveConcurrencyController(initialLimit: 6);

            int after = controller.Observe(Sample(1.0, spaceLow: true));

            Assert.Equal(1, after);
        }

        [Fact]
        public void 有失败_保持当前值不冒进()
        {
            var controller = new AdaptiveConcurrencyController(initialLimit: 4);
            controller.Observe(Sample(1.0));

            int after = controller.Observe(Sample(1.0, failures: 1));

            Assert.Equal(4, after);
        }

        [Fact]
        public void 总吞吐下降_拒绝加档()
        {
            var controller = new AdaptiveConcurrencyController(initialLimit: 4);

            controller.Observe(Sample(1.0, throughput: 100));
            int after = controller.Observe(Sample(1.0, throughput: 50));

            Assert.Equal(4, after);
        }

        [Fact]
        public void 每批调整次数有上限_防抖动()
        {
            var controller = new AdaptiveConcurrencyController(initialLimit: 1);

            for (int i = 0; i < 40; i++)
            {
                controller.Observe(Sample(1.0));
            }

            Assert.True(controller.Changes <= 8, $"调整次数应受限，实际 {controller.Changes}");
            Assert.True(controller.CurrentLimit <= controller.MaxLimit);
        }

        [Fact]
        public void 基线重测_压到1并清基线()
        {
            var controller = new AdaptiveConcurrencyController(initialLimit: 4);
            controller.Observe(Sample(1.0));

            controller.BeginBaselineRecalibration();

            Assert.Equal(1, controller.CurrentLimit);
            Assert.Equal(0, controller.MinLatencySecondsPerGb);
        }

        [Fact]
        public void Profile按卷与设备隔离_同名不同卷不串()
        {
            var profile = new ConcurrencyProfile
            {
                LastGoodLimit = 3,
                MinLatencySecondsPerGb = 1.25,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            ConcurrencyProfileStore.Save(_root, @"C:\", StorageDeviceKind.Ssd, profile);
            ConcurrencyProfileStore.Save(_root, @"D:\", StorageDeviceKind.Ssd, profile);

            string a = ConcurrencyProfileStore.BuildKey(@"C:\", StorageDeviceKind.Ssd);
            string b = ConcurrencyProfileStore.BuildKey(@"D:\", StorageDeviceKind.Ssd);
            string c = ConcurrencyProfileStore.BuildKey(@"C:\", StorageDeviceKind.Hdd);

            Assert.NotEqual(a, b);
            Assert.NotEqual(a, c);
            Assert.NotNull(ConcurrencyProfileStore.Load(_root, @"C:\", StorageDeviceKind.Ssd));
        }

        [Fact]
        public void Profile损坏_返回空而不是抛异常()
        {
            string path = ConcurrencyProfileStore.PathFor(_root, @"C:\", StorageDeviceKind.Ssd);
            File.WriteAllText(path, "{not-json");

            Assert.Null(ConcurrencyProfileStore.Load(_root, @"C:\", StorageDeviceKind.Ssd));
        }
    }
}

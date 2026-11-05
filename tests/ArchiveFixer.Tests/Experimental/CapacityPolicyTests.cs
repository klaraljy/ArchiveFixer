using ArchiveFixer.Experimental.AutoConcurrency;
using Xunit;

namespace ArchiveFixer.Tests
{
    [Trait("Category", "ExperimentalAutoConcurrency")]
    public sealed class CapacityPolicyTests
    {
        [Fact]
        public void 查询容量不会自己升档_压力只降档()
        {
            var policy = new CapacityPolicy();
            policy.Reset(initial: 3, maximum: 4);

            Assert.Equal(3, policy.Evaluate(new CapacitySample(10, 8L * 1024 * 1024 * 1024, false)));
            Assert.Equal(2, policy.Evaluate(new CapacitySample(95, 8L * 1024 * 1024 * 1024, false)));
            Assert.Equal(2, policy.Evaluate(new CapacitySample(10, 8L * 1024 * 1024 * 1024, false)));
            Assert.Equal(2, policy.Evaluate(new CapacitySample(10, 8L * 1024 * 1024 * 1024, false)));
        }

        [Fact]
        public void 探测窗口内吞吐不足会回退_而不是一路升满()
        {
            var policy = new CapacityPolicy();
            policy.Reset(initial: 3, maximum: 4);

            policy.ObserveCompletion(100, 10, new CapacitySample(10, 8L * 1024 * 1024 * 1024, false));
            Assert.Equal(4, policy.Current);

            policy.ObserveCompletion(101, 10, new CapacitySample(10, 8L * 1024 * 1024 * 1024, false));
            Assert.Equal(3, policy.Current);
            Assert.Contains("回退", policy.LastReason);
        }

        [Fact]
        public void 探测窗口内吞吐提升且延迟不恶化才保留()
        {
            var policy = new CapacityPolicy();
            policy.Reset(initial: 3, maximum: 4);

            policy.ObserveCompletion(100, 10, new CapacitySample(10, 8L * 1024 * 1024 * 1024, false));
            policy.ObserveCompletion(110, 10.5, new CapacitySample(10, 8L * 1024 * 1024 * 1024, false));

            Assert.Equal(4, policy.Current);
            Assert.Contains("探测成功", policy.LastReason);
        }

        [Fact]
        public void 磁盘读写停顿会降档()
        {
            var policy = new CapacityPolicy();
            policy.Reset(initial: 3, maximum: 4);

            policy.ObserveCompletion(100, 10, new CapacitySample(10, 8L * 1024 * 1024 * 1024, false));
            Assert.Equal(4, policy.Current);

            Assert.Equal(3, policy.Evaluate(
                new CapacitySample(10, 8L * 1024 * 1024 * 1024, false, DiskStalled: true)));
        }

        [Fact]
        public void 已到上限后保持_不无限增长()
        {
            var policy = new CapacityPolicy();
            policy.Reset(initial: 4, maximum: 4);

            for (int i = 0; i < 10; i++)
            {
                policy.ObserveCompletion(100, 10, new CapacitySample(10, 8L * 1024 * 1024 * 1024, false));
                Assert.Equal(4, policy.Current);
            }
        }
    }
}

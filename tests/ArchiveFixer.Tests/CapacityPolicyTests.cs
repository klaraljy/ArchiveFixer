using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class CapacityPolicyTests
    {
        [Fact]
        public void 压力降档_冷却后才升档_不来回抖()
        {
            var policy = new CapacityPolicy();
            policy.Reset(initial: 3, maximum: 4);
            Assert.Equal(4, policy.Evaluate(new CapacitySample(10, 8L * 1024 * 1024 * 1024, false)));
            Assert.Equal(3, policy.Evaluate(new CapacitySample(95, 8L * 1024 * 1024 * 1024, false)));
            Assert.Equal(3, policy.Evaluate(new CapacitySample(10, 8L * 1024 * 1024 * 1024, false)));
            Assert.Equal(3, policy.Evaluate(new CapacitySample(10, 8L * 1024 * 1024 * 1024, false)));
            Assert.Equal(4, policy.Evaluate(new CapacitySample(10, 8L * 1024 * 1024 * 1024, false)));
        }

        [Fact]
        public void 已到上限后保持_不无限增长()
        {
            var policy = new CapacityPolicy();
            policy.Reset(initial: 4, maximum: 4);

            for (int i = 0; i < 10; i++)
            {
                Assert.Equal(4, policy.Evaluate(new CapacitySample(10, 8L * 1024 * 1024 * 1024, false)));
            }
        }
    }
}

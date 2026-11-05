using System.Collections.Generic;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class DevicePoolSchedulerTests
    {
        [Fact]
        public void 两台设备各自占槽_不互相拖累()
        {
            var pools = new DevicePoolScheduler<string>();
            pools.Reset(
                new[]
                {
                    ("h1", @"H:\"),
                    ("h2", @"H:\"),
                    ("s1", @"C:\"),
                    ("s2", @"C:\")
                },
                initialPerDevice: 1,
                maximumPerDevice: 2,
                estimate: _ => 1);

            Assert.True(pools.TryTakeNext(1, out string? first, out string? firstRoot));
            Assert.True(pools.TryTakeNext(1, out string? second, out string? secondRoot));
            Assert.NotEqual(firstRoot, secondRoot);
            Assert.Contains(first, new[] { "h1", "s1" });
            Assert.Contains(second, new[] { "h1", "s1" });

            Assert.True(pools.MarkCompleted(first!));
            Assert.True(pools.TryTakeNext(1, out string? third, out string? thirdRoot));
            Assert.Equal(firstRoot, thirdRoot);
            Assert.DoesNotContain(third, new[] { first, second });
        }

        [Fact]
        public void 每个设备有自己的容量策略()
        {
            var pools = new DevicePoolScheduler<string>();
            pools.Reset(
                new[] { ("h1", @"H:\"), ("s1", @"C:\") },
                initialPerDevice: 1,
                maximumPerDevice: 4,
                estimate: _ => 1);

            Assert.NotNull(pools.CapacityFor(@"H:\"));
            Assert.NotNull(pools.CapacityFor(@"C:\"));
            Assert.NotSame(pools.CapacityFor(@"H:\"), pools.CapacityFor(@"C:\"));
        }
    }
}

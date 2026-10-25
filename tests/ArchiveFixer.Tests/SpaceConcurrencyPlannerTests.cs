using System;
using System.Collections.Generic;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class SpaceConcurrencyPlannerTests
    {
        private const long Gb = 1024L * 1024 * 1024;

        [Fact]
        public void 九G面对十G只能并发一个()
        {
            int count = SpaceConcurrencyPlanner.CountLaunchable(
                availableBytes: 10 * Gb,
                reservedBytes: 0,
                pendingSizes: new long[] { 9 * Gb, 5 * Gb, 3 * Gb, 1 * Gb },
                maxParallel: 4);

            Assert.Equal(1, count);
        }

        [Fact]
        public void 大包跑完后五三一可以并发三个()
        {
            int count = SpaceConcurrencyPlanner.CountLaunchable(
                availableBytes: 10 * Gb,
                reservedBytes: 0,
                pendingSizes: new long[] { 5 * Gb, 3 * Gb, 1 * Gb },
                maxParallel: 4);

            Assert.Equal(3, count);
        }

        [Fact]
        public void 正在跑的预留量要扣掉()
        {
            int count = SpaceConcurrencyPlanner.CountLaunchable(
                availableBytes: 10 * Gb,
                reservedBytes: 5 * Gb,
                pendingSizes: new long[] { 3 * Gb, 1 * Gb },
                maxParallel: 4);

            Assert.Equal(2, count);
        }

        [Fact]
        public void 不能超过用户设置的最大并发()
        {
            int count = SpaceConcurrencyPlanner.CountLaunchable(
                availableBytes: 100 * Gb,
                reservedBytes: 0,
                pendingSizes: new long[] { 1 * Gb, 1 * Gb, 1 * Gb, 1 * Gb, 1 * Gb },
                maxParallel: 2);

            Assert.Equal(2, count);
        }
    }
}

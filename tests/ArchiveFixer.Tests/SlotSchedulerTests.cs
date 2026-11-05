using System;
using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class SlotSchedulerTests
    {
        [Fact]
        public void 按预计耗时降序_最小负载槽位_完成一个补一个()
        {
            var scheduler = new SlotScheduler<string>();
            scheduler.Reset(new[] { "1", "10", "5a", "5b", "4", "3" }, 3, Estimate);
            var starts = scheduler.StartAvailable();

            Assert.Equal(3, starts.Count);
            Assert.Equal("10", starts[0].Unit);
            Assert.Equal("5a", starts[1].Unit);
            Assert.Equal("5b", starts[2].Unit);

            Assert.True(scheduler.MarkCompleted("5a"));
            var refill = scheduler.StartAvailable();

            Assert.Single(refill);
            Assert.Equal("4", refill[0].Unit);
        }

        [Fact]
        public void 容量下降只停止新启动_不杀在跑任务()
        {
            var scheduler = new SlotScheduler<string>();
            scheduler.Reset(new[] { "a", "b", "c", "d" }, 3, Estimate);
            scheduler.StartAvailable();
            Assert.Equal(3, scheduler.ActiveCount);

            scheduler.SetCapacity(1);
            Assert.True(scheduler.MarkCompleted("a"));
            Assert.Equal(2, scheduler.ActiveCount);

            Assert.Empty(scheduler.StartAvailable());
            Assert.Equal("d", scheduler.PendingUnits.Single());
        }

        [Fact]
        public void 容量恢复后才继续补位()
        {
            var scheduler = new SlotScheduler<string>();
            scheduler.Reset(new[] { "a", "b", "c", "d" }, 2, Estimate);
            scheduler.StartAvailable();
            scheduler.SetCapacity(1);

            Assert.True(scheduler.MarkCompleted("a"));
            Assert.Empty(scheduler.StartAvailable());
            Assert.True(scheduler.MarkCompleted("b"));

            scheduler.SetCapacity(2);
            var starts = scheduler.StartAvailable();

            Assert.Equal(2, starts.Count);
            Assert.Equal("c", starts[0].Unit);
        }

        private static double Estimate(string unit)
        {
            if (unit.StartsWith("5", StringComparison.Ordinal)) return 5;
            return double.Parse(unit, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

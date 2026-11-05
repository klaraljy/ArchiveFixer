// STATUS: 未完成 / 暂停（2026-11-05）。自动并发实验代码，不接入产品路径。
using ArchiveFixer.Extraction;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Experimental.AutoConcurrency
{
    /// <summary>
    /// 普通/特定共用的唯一调度入口：只负责取任务、占槽、完成、补位、
    /// 读容量和容量变化；不包含任何空间门、落点或源包/unit 收尾。
    /// </summary>
    public sealed class SlotSchedulerRunner<T> where T : class
    {
        private readonly DevicePoolScheduler<T> _pools = new();

        public int ActiveCount => _pools.ActiveCount;
        public int PendingCount => _pools.PendingCount;
        public bool HasWork => _pools.HasWork;
        public IReadOnlyList<string> Roots => _pools.Roots;
        public IReadOnlyList<T> PendingUnits => _pools.PendingUnits;
        public IReadOnlyList<T> ActiveUnits => _pools.ActiveUnits;

        public void Reset(
            IEnumerable<(T Unit, string Root)> units,
            Func<string, int> initialPerDevice,
            Func<string, int> maximumPerDevice,
            Func<T, double> estimate)
        {
            _pools.Reset(units, initialPerDevice, maximumPerDevice, estimate);
        }

        public bool TryTakeNext(out T? unit, out string? root)
        {
            return _pools.TryTakeNext(null, out unit, out root);
        }

        public bool Complete(T unit)
        {
            return _pools.MarkCompleted(unit);
        }

        public void ObserveCompletion(
            T unit,
            long sourceBytes,
            double latencySecondsPerGb,
            CapacitySample sample)
        {
            _pools.ObserveCompletion(unit, sourceBytes, latencySecondsPerGb, sample);
        }

        public void UpdatePendingEstimate(T unit, double seconds)
        {
            _pools.UpdatePendingEstimate(unit, seconds);
        }

        public IReadOnlyList<DeviceCapacityChange> DrainCapacityChanges()
        {
            return _pools.DrainCapacityChanges();
        }
    }
}

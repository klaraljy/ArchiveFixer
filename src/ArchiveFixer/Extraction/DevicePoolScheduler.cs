using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Extraction
{
    public readonly record struct DeviceCapacityChange(
        string Root,
        int Previous,
        int Current,
        string Reason,
        double AggregateMegabytesPerSecond,
        double AverageLatencySecondsPerGb);

    /// <summary>
    /// 每设备/卷一个 SlotScheduler + CapacityPolicy。设备池只管调度，
    /// 不碰普通空间门、特定解压 unit、落点或源包处理。
    /// </summary>
    public sealed class DevicePoolScheduler<T> where T : class
    {
        private sealed class DevicePool
        {
            public DevicePool(string root, int initial, int maximum)
            {
                Root = root;
                Capacity = new CapacityPolicy();
                Capacity.Reset(initial, maximum);
                Slots = new SlotScheduler<T>();
                LastCapacity = Capacity.Current;
                LastReason = Capacity.LastReason;
            }

            public string Root { get; }
            public CapacityPolicy Capacity { get; }
            public SlotScheduler<T> Slots { get; }
            public int LastCapacity { get; set; }
            public string LastReason { get; set; } = string.Empty;
            public DateTime WindowStartedUtc { get; set; } = DateTime.UtcNow;
            public long WindowBytes { get; set; }
            public int WindowTasks { get; set; }
            public double WindowLatency { get; set; }
        }

        private readonly Dictionary<string, DevicePool> _pools = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<T, string> _unitRoot = new(ReferenceEqualityComparer.Instance);
        private readonly List<string> _order = new();
        private readonly List<DeviceCapacityChange> _changes = new();

        public IReadOnlyList<string> Roots => _order;
        public int ActiveCount => _pools.Values.Sum(pool => pool.Slots.ActiveCount);
        public int PendingCount => _pools.Values.Sum(pool => pool.Slots.PendingCount);
        public bool HasWork => ActiveCount > 0 || PendingCount > 0;

        public IReadOnlyList<T> PendingUnits =>
            _pools.Values.SelectMany(pool => pool.Slots.PendingUnits).ToList();

        public IReadOnlyList<T> ActiveUnits =>
            _pools.Values.SelectMany(pool => pool.Slots.ActiveUnits).ToList();

        public void Reset(
            IEnumerable<(T Unit, string Root)> units,
            int initialPerDevice,
            int maximumPerDevice,
            Func<T, double> estimate)
        {
            Reset(units, _ => initialPerDevice, _ => maximumPerDevice, estimate);
        }

        public void Reset(
            IEnumerable<(T Unit, string Root)> units,
            Func<string, int> initialPerDevice,
            Func<string, int> maximumPerDevice,
            Func<T, double> estimate)
        {
            _pools.Clear();
            _unitRoot.Clear();
            _order.Clear();

            foreach (IGrouping<string, T> group in (units ?? Enumerable.Empty<(T Unit, string Root)>())
                         .Where(item => item.Unit is not null)
                         .GroupBy(item => NormalizeRoot(item.Root), item => item.Unit))
            {
                if (_pools.ContainsKey(group.Key))
                {
                    continue;
                }

                var pool = new DevicePool(
                    group.Key,
                    initialPerDevice(group.Key),
                    maximumPerDevice(group.Key));
                pool.Slots.Reset(group, pool.Capacity.Current, estimate);
                _pools[group.Key] = pool;
                _order.Add(group.Key);
                foreach (T unit in group)
                {
                    _unitRoot[unit] = group.Key;
                }
            }
        }

        public bool TryTakeNext(int? ignoredGlobalCapacity, out T? unit, out string? root)
        {
            unit = null;
            root = null;

            if (ActiveCount >= 8)
            {
                return false;
            }

            foreach (string key in _order)
            {
                DevicePool pool = _pools[key];
                pool.Slots.SetCapacity(pool.Capacity.Current);

                if (pool.Slots.TryTakeNext(out unit))
                {
                    root = pool.Root;
                    return true;
                }
            }

            return false;
        }

        /// <summary>完成任务时累计该池聚合窗口；窗口够长才推进一次容量探测。</summary>
        public void ObserveCompletion(
            T unit,
            long sourceBytes,
            double latencySecondsPerGb,
            CapacitySample sample)
        {
            if (unit is null || !_unitRoot.TryGetValue(unit, out string? root))
            {
                return;
            }

            if (!_pools.TryGetValue(root, out DevicePool? pool))
            {
                return;
            }

            pool.WindowBytes += Math.Max(0, sourceBytes);
            pool.WindowTasks++;
            pool.WindowLatency += Math.Max(0, latencySecondsPerGb);

            double elapsed = (DateTime.UtcNow - pool.WindowStartedUtc).TotalSeconds;
            if (elapsed < 3 && pool.WindowTasks < 2)
            {
                return;
            }

            double aggregateMbps = elapsed > 0
                ? pool.WindowBytes / (1024d * 1024d) / elapsed
                : 0;
            double averageLatency = pool.WindowTasks > 0
                ? pool.WindowLatency / pool.WindowTasks
                : 0;

            pool.Capacity.ObserveCompletion(aggregateMbps, averageLatency, sample);
            if (pool.Capacity.Current != pool.LastCapacity
                || !string.Equals(pool.LastReason, pool.Capacity.LastReason, StringComparison.Ordinal))
            {
                _changes.Add(new DeviceCapacityChange(
                    pool.Root,
                    pool.LastCapacity,
                    pool.Capacity.Current,
                    pool.Capacity.LastReason,
                    aggregateMbps,
                    averageLatency));
                pool.LastCapacity = pool.Capacity.Current;
                pool.LastReason = pool.Capacity.LastReason;
            }

            pool.Slots.SetCapacity(pool.Capacity.Current);
            pool.WindowStartedUtc = DateTime.UtcNow;
            pool.WindowBytes = 0;
            pool.WindowTasks = 0;
            pool.WindowLatency = 0;
        }

        public bool MarkCompleted(T unit)
        {
            if (unit is null || !_unitRoot.TryGetValue(unit, out string? root))
            {
                return false;
            }

            return _pools.TryGetValue(root, out DevicePool? pool)
                   && pool.Slots.MarkCompleted(unit);
        }

        public CapacityPolicy? CapacityFor(string root)
        {
            return _pools.TryGetValue(NormalizeRoot(root), out DevicePool? pool)
                ? pool.Capacity
                : null;
        }

        public IReadOnlyList<DeviceCapacityChange> DrainCapacityChanges()
        {
            if (_changes.Count == 0)
            {
                return Array.Empty<DeviceCapacityChange>();
            }

            var copy = _changes.ToList();
            _changes.Clear();
            return copy;
        }

        public void SetCapacity(string root, int capacity)
        {
            if (_pools.TryGetValue(NormalizeRoot(root), out DevicePool? pool))
            {
                pool.Slots.SetCapacity(capacity);
            }
        }

        public void UpdatePendingEstimate(T unit, double seconds)
        {
            if (unit is not null
                && _unitRoot.TryGetValue(unit, out string? root)
                && _pools.TryGetValue(root, out DevicePool? pool))
            {
                pool.Slots.UpdatePendingEstimate(unit, seconds);
            }
        }

        private static string NormalizeRoot(string? root)
        {
            return string.IsNullOrWhiteSpace(root) ? "unknown" : root;
        }
    }
}

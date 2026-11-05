using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Extraction
{
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
            }

            public string Root { get; }
            public CapacityPolicy Capacity { get; }
            public SlotScheduler<T> Slots { get; }
        }

        private readonly Dictionary<string, DevicePool> _pools = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<T, string> _unitRoot = new(ReferenceEqualityComparer.Instance);
        private readonly List<string> _order = new();

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

                var pool = new DevicePool(group.Key, initialPerDevice, maximumPerDevice);
                pool.Slots.Reset(group, initialPerDevice, estimate);
                _pools[group.Key] = pool;
                _order.Add(group.Key);
                foreach (T unit in group)
                {
                    _unitRoot[unit] = group.Key;
                }
            }
        }

        public bool TryTakeNext(int? capacityPerDevice, out T? unit, out string? root)
        {
            unit = null;
            root = null;

            foreach (string key in _order)
            {
                DevicePool pool = _pools[key];
                if (capacityPerDevice.HasValue)
                {
                    pool.Slots.SetCapacity(capacityPerDevice.Value);
                }

                if (pool.Slots.TryTakeNext(out unit))
                {
                    root = pool.Root;
                    return true;
                }
            }

            return false;
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

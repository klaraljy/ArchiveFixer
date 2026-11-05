using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 独立的 Pm||Cmax 槽位调度器：
    /// - pending 按预计耗时从大到小；
    /// - 每个新任务放到当前累计预计负载最小的空闲槽位；
    /// - 完成一个，补一个；
    /// - 容量下降只停止新启动，不杀在跑任务。
    ///
    /// 它不知道普通挡位/特定解压/UI，单位构造和实际启动由调用方负责。
    /// </summary>
    public sealed class SlotScheduler<T> where T : class
    {
        public readonly record struct Start(T Unit, int SlotIndex);

        private sealed class PendingItem
        {
            public PendingItem(T unit, double seconds)
            {
                Unit = unit;
                Seconds = seconds;
            }

            public T Unit { get; }
            public double Seconds { get; set; }
        }

        private sealed class SlotState
        {
            public T? Active { get; set; }
            public double PredictedLoadSeconds { get; set; }
        }

        private readonly List<PendingItem> _pending = new();
        private readonly List<SlotState> _slots = new();
        private readonly Dictionary<T, int> _active = new(ReferenceEqualityComparer.Instance);
        private Func<T, double> _estimate = _ => 1;

        public int Capacity { get; private set; } = 1;
        public int ActiveCount => _active.Count;
        public int PendingCount => _pending.Count;
        public bool HasWork => _pending.Count > 0 || _active.Count > 0;

        public IReadOnlyList<T> PendingUnits =>
            _pending.Select(item => item.Unit).ToList();

        public IReadOnlyList<T> ActiveUnits =>
            _active.Keys.ToList();

        public void Reset(IEnumerable<T> units, int capacity, Func<T, double> estimate)
        {
            _pending.Clear();
            _slots.Clear();
            _active.Clear();
            _estimate = estimate ?? (_ => 1);
            Capacity = Math.Max(1, capacity);

            foreach (T unit in units ?? Enumerable.Empty<T>())
            {
                if (unit is null)
                {
                    continue;
                }

                _pending.Add(new PendingItem(unit, Estimate(unit)));
            }

            SortPending();
            EnsureSlots(Capacity);
        }

        public void SetCapacity(int capacity)
        {
            Capacity = Math.Max(1, capacity);
            EnsureSlots(Capacity);
        }

        public IReadOnlyList<Start> StartAvailable(int? capacity = null)
        {
            if (capacity.HasValue)
            {
                SetCapacity(capacity.Value);
            }

            var starts = new List<Start>();
            while (TryTakeNext(out T? unit))
            {
                starts.Add(new Start(unit!, _active[unit!]));
            }

            return starts;
        }

        /// <summary>尝试占用一个槽位并取出下一个任务；没有空槽或没有 pending 时返回 false。</summary>
        public bool TryTakeNext(out T? unit)
        {
            unit = null;
            if (_pending.Count == 0 || _active.Count >= Capacity)
            {
                return false;
            }

            int slotIndex = FindLeastLoadedFreeSlot();
            if (slotIndex < 0)
            {
                return false;
            }

            PendingItem next = _pending[0];
            _pending.RemoveAt(0);

            SlotState slot = _slots[slotIndex];
            slot.Active = next.Unit;
            slot.PredictedLoadSeconds += Math.Max(1, next.Seconds);
            _active[next.Unit] = slotIndex;
            unit = next.Unit;
            return true;
        }

        public bool MarkCompleted(T unit)
        {
            if (unit is null || !_active.Remove(unit, out int slotIndex))
            {
                return false;
            }

            SlotState slot = _slots[slotIndex];
            slot.Active = null;
            // 这个槽位空了；历史预测不再累计，后续任务从零开始算这个槽位。
            slot.PredictedLoadSeconds = 0;
            return true;
        }

        public void UpdatePendingEstimate(T unit, double seconds)
        {
            PendingItem? item = _pending.FirstOrDefault(candidate => ReferenceEquals(candidate.Unit, unit));
            if (item is null)
            {
                return;
            }

            item.Seconds = Math.Max(1, seconds);
            SortPending();
        }

        public double EstimatedLoadSeconds()
        {
            return _slots.Sum(slot => slot.PredictedLoadSeconds);
        }

        /// <summary>
        /// 只做启动顺序模拟：把任务按预计耗时降序，依次放到最早可用的槽位。
        /// 普通挡位用它把 plan 顺序换成槽位启动顺序；实际运行仍由调用方启动/收尾。
        /// </summary>
        public static IReadOnlyList<T> SimulateStartOrder(
            IEnumerable<T> units,
            int capacity,
            Func<T, double> estimate)
        {
            var pending = (units ?? Enumerable.Empty<T>())
                .Where(unit => unit is not null)
                .Select(unit => new PendingItem(unit, Math.Max(1, estimate(unit))))
                .OrderByDescending(item => item.Seconds)
                .ToList();
            int slots = Math.Max(1, Math.Min(capacity, Math.Max(1, pending.Count)));
            double[] available = new double[slots];
            var ordered = new List<T>(pending.Count);

            while (pending.Count > 0)
            {
                int slot = 0;
                for (int i = 1; i < slots; i++)
                {
                    if (available[i] < available[slot])
                    {
                        slot = i;
                    }
                }

                PendingItem next = pending[0];
                pending.RemoveAt(0);
                ordered.Add(next.Unit);
                available[slot] += Math.Max(1, next.Seconds);
            }

            return ordered;
        }

        private int FindLeastLoadedFreeSlot()
        {
            int best = -1;
            double bestLoad = double.MaxValue;

            for (int i = 0; i < Capacity && i < _slots.Count; i++)
            {
                SlotState slot = _slots[i];
                if (slot.Active is not null)
                {
                    continue;
                }

                if (slot.PredictedLoadSeconds < bestLoad)
                {
                    bestLoad = slot.PredictedLoadSeconds;
                    best = i;
                }
            }

            return best;
        }

        private void EnsureSlots(int capacity)
        {
            while (_slots.Count < capacity)
            {
                _slots.Add(new SlotState());
            }
        }

        private void SortPending()
        {
            _pending.Sort((left, right) => right.Seconds.CompareTo(left.Seconds));
        }

        private double Estimate(T unit)
        {
            try
            {
                double seconds = _estimate(unit);
                return double.IsFinite(seconds) && seconds > 0 ? seconds : 1;
            }
            catch
            {
                return 1;
            }
        }
    }
}

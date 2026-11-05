// STATUS: 未完成 / 暂停（2026-11-05）。自动并发实验代码，不接入产品路径。
using System;

namespace ArchiveFixer.Experimental.AutoConcurrency
{
    public readonly record struct CapacitySample(
        double CpuBusyPercent,
        long AvailablePhysicalBytes,
        bool UiStalled,
        bool DiskStalled = false);

    /// <summary>
    /// 并发容量策略：压力只降档；加档必须经过一次可比较的探测窗口。
    /// Evaluate 只读压力，不再每次调用就升档；ObserveCompletion 才推进状态机。
    /// </summary>
    public sealed class CapacityPolicy
    {
        private enum Phase
        {
            Baseline,
            Probe,
            Cooldown
        }

        private const double CpuCriticalPercent = 90;
        private const long MemoryCriticalBytes = 1024L * 1024 * 1024;
        private const int CooldownSamples = 2;
        private const double RequiredThroughputGain = 0.05;
        private const double AllowedLatencyWorsening = 0.20;

        private Phase _phase;
        private int _cooldown;
        private int _probeFrom;
        private double _baselineThroughput;
        private double _baselineLatency;

        public int Current { get; private set; } = 1;
        public int Maximum { get; private set; } = 1;
        public string LastReason { get; private set; } = "尚未采样";

        public void Reset(int initial, int maximum)
        {
            Maximum = Math.Max(1, maximum);
            Current = Math.Clamp(initial, 1, Maximum);
            _phase = Phase.Baseline;
            _cooldown = 0;
            _probeFrom = Current;
            _baselineThroughput = 0;
            _baselineLatency = 0;
            LastReason = "初始容量";
        }

        /// <summary>只处理即时压力；不升档。返回当前容量，调用方不得据此频繁加槽。</summary>
        public int Evaluate(CapacitySample sample)
        {
            if (PressureReason(sample) is string reason)
            {
                _cooldown = CooldownSamples;
                _phase = Phase.Cooldown;
                if (Current > 1)
                {
                    Current--;
                    LastReason = reason;
                }
                else
                {
                    LastReason = "压力已到最低容量";
                }
            }

            return Current;
        }

        /// <summary>
        /// 一个任务完成后调用：基线 → 探针（+1）→ 比较吞吐/延迟 → 保留或回退。
        /// 这里的吞吐使用调用方给的相对值即可，重点看同一窗口前后的比例。
        /// </summary>
        public void ObserveCompletion(
            double throughput,
            double latency,
            CapacitySample sample)
        {
            if (PressureReason(sample) is string pressure)
            {
                _cooldown = CooldownSamples;
                _phase = Phase.Cooldown;
                if (Current > 1)
                {
                    Current--;
                }

                LastReason = pressure;
                return;
            }

            if (!double.IsFinite(throughput) || throughput <= 0)
            {
                return;
            }

            switch (_phase)
            {
                case Phase.Cooldown:
                    if (_cooldown > 0)
                    {
                        _cooldown--;
                        LastReason = "冷却中";
                        return;
                    }

                    _phase = Phase.Baseline;
                    LastReason = "冷却结束";
                    return;

                case Phase.Baseline:
                    if (Current >= Maximum)
                    {
                        LastReason = "已达到设备上限";
                        return;
                    }

                    _probeFrom = Current;
                    _baselineThroughput = throughput;
                    _baselineLatency = latency > 0 ? latency : 0;
                    Current++;
                    _phase = Phase.Probe;
                    LastReason = "开始探测加档";
                    return;

                case Phase.Probe:
                    bool throughputGain = _baselineThroughput <= 0
                                          || throughput >= _baselineThroughput * (1 + RequiredThroughputGain);
                    bool latencyOk = _baselineLatency <= 0
                                     || latency <= 0
                                     || latency <= _baselineLatency * (1 + AllowedLatencyWorsening);

                    if (throughputGain && latencyOk)
                    {
                        _phase = Phase.Baseline;
                        LastReason = $"探测成功，保留 {Current} 个";
                    }
                    else
                    {
                        Current = _probeFrom;
                        _phase = Phase.Cooldown;
                        _cooldown = CooldownSamples;
                        LastReason = throughputGain
                            ? "探测回退：延迟恶化"
                            : "探测回退：吞吐未提升";
                    }

                    return;
            }
        }

        private static string? PressureReason(CapacitySample sample)
        {
            if (sample.CpuBusyPercent >= CpuCriticalPercent)
            {
                return "系统 CPU 过高";
            }

            if (sample.AvailablePhysicalBytes > 0
                && sample.AvailablePhysicalBytes < MemoryCriticalBytes)
            {
                return "可用内存过低";
            }

            if (sample.DiskStalled)
            {
                return "磁盘读写停顿";
            }

            return sample.UiStalled ? "UI 停顿" : null;
        }
    }
}

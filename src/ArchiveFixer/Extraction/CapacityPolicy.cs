using System;

namespace ArchiveFixer.Extraction
{
    public readonly record struct CapacitySample(
        double CpuBusyPercent,
        long AvailablePhysicalBytes,
        bool UiStalled);

    /// <summary>
    /// 并发容量策略：静态上限 + 压力降档 + 冷却升档。
    /// 只决定“还能启动几个”，不杀正在跑的任务；调用方按 Current 补槽。
    /// </summary>
    public sealed class CapacityPolicy
    {
        private const double CpuCriticalPercent = 90;
        private const long MemoryCriticalBytes = 1024L * 1024 * 1024;
        private const int CooldownSamples = 2;

        private int _cooldown;

        public int Current { get; private set; } = 1;
        public int Maximum { get; private set; } = 1;
        public string LastReason { get; private set; } = "尚未采样";

        public void Reset(int initial, int maximum)
        {
            Maximum = Math.Max(1, maximum);
            Current = Math.Clamp(initial, 1, Maximum);
            _cooldown = 0;
            LastReason = "初始容量";
        }

        public int Evaluate(CapacitySample sample)
        {
            bool cpuCritical = sample.CpuBusyPercent >= CpuCriticalPercent;
            bool memoryCritical = sample.AvailablePhysicalBytes > 0
                                  && sample.AvailablePhysicalBytes < MemoryCriticalBytes;
            bool uiStalled = sample.UiStalled;

            if (cpuCritical || memoryCritical || uiStalled)
            {
                _cooldown = CooldownSamples;
                if (Current > 1)
                {
                    Current--;
                    LastReason = cpuCritical
                        ? "系统 CPU 过高"
                        : memoryCritical ? "可用内存过低" : "UI 停顿";
                }
                else
                {
                    LastReason = "压力已到最低容量";
                }

                return Current;
            }

            if (_cooldown > 0)
            {
                _cooldown--;
                LastReason = "冷却中";
                return Current;
            }

            if (Current < Maximum)
            {
                Current++;
                LastReason = "压力稳定，增加容量";
            }
            else
            {
                LastReason = "已达到设备上限";
            }

            return Current;
        }
    }
}
